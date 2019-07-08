﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace FASTER.core
{

    /// <summary>
    /// 
    /// </summary>
    public unsafe class LightEpoch
    {
        /// <summary>
        /// Default invalid index entry.
        /// </summary>
        private const int kInvalidIndex = 0;

        /// <summary>
        /// Default number of entries in the entries table
        /// </summary>
        public const int kTableSize = 128;

        /// <summary>
        /// Default drainlist size
        /// </summary>
        private const int kDrainListSize = 16;

        /// <summary>
        /// Thread protection status entries.
        /// </summary>
        private Entry[] tableRaw;
        private GCHandle tableHandle;
        private Entry* tableAligned;

        /// <summary>
        /// List of action, epoch pairs containing actions to performed 
        /// when an epoch becomes safe to reclaim.
        /// </summary>
        private int drainCount = 0;
        private readonly EpochActionPair[] drainList = new EpochActionPair[kDrainListSize];

        /// <summary>
        /// Number of entries in the epoch table
        /// </summary>
        private int numEntries;

        /// <summary>
        /// A thread's entry in the epoch table.
        /// </summary>
        private FastThreadLocal<int> threadEntryIndex;

        /// <summary>
        /// Global current epoch value
        /// </summary>
        public int CurrentEpoch;

        /// <summary>
        /// Cached value of latest epoch that is safe to reclaim
        /// </summary>
        public int SafeToReclaimEpoch;

        /// <summary>
        /// Instantiate the epoch table
        /// </summary>
        /// <param name="size"></param>
        public LightEpoch(int size = kTableSize)
        {
            Initialize(size);
        }

        /// <summary>
        /// Initialize the epoch table
        /// </summary>
        /// <param name="size"></param>
        unsafe void Initialize(int size)
        {
            threadEntryIndex = new FastThreadLocal<int>();
            numEntries = size;

            // Over-allocate to do cache-line alignment
            tableRaw = new Entry[size + 2];
            tableHandle = GCHandle.Alloc(tableRaw, GCHandleType.Pinned);
            long p = (long)tableHandle.AddrOfPinnedObject();

            // Force the pointer to align to 64-byte boundaries
            long p2 = (p + (Constants.kCacheLineBytes - 1)) & ~(Constants.kCacheLineBytes - 1);
            tableAligned = (Entry*)p2;

            CurrentEpoch = 1;
            SafeToReclaimEpoch = 0;

            for (int i = 0; i < kDrainListSize; i++)
                drainList[i].epoch = int.MaxValue;
            drainCount = 0;
        }

        /// <summary>
        /// Clean up epoch table
        /// </summary>
        public void Dispose()
        {
            tableHandle.Free();
            tableAligned = null;
            tableRaw = null;

            numEntries = 0;
            CurrentEpoch = 1;
            SafeToReclaimEpoch = 0;

            threadEntryIndex.Dispose();
        }

        /// <summary>
        /// Check whether current thread is protected
        /// </summary>
        /// <returns>Result of the check</returns>
        public bool IsProtected()
        {
            return threadEntryIndex.IsInitializedForThread && kInvalidIndex != threadEntryIndex.Value;
        }

        /// <summary>
        /// Enter the thread into the protected code region
        /// </summary>
        /// <returns>Current epoch</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ProtectAndDrain()
        {
            int entry = threadEntryIndex.Value;

            (*(tableAligned + entry)).localCurrentEpoch = CurrentEpoch;

            if (drainCount > 0)
            {
                Drain((*(tableAligned + entry)).localCurrentEpoch);
            }

            return (*(tableAligned + entry)).localCurrentEpoch;
        }

        /// <summary>
        /// Check and invoke trigger actions that are ready
        /// </summary>
        /// <param name="nextEpoch">Next epoch</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Drain(int nextEpoch)
        {
            ComputeNewSafeToReclaimEpoch(nextEpoch);

            for (int i = 0; i < kDrainListSize; i++)
            {
                var trigger_epoch = drainList[i].epoch;

                if (trigger_epoch <= SafeToReclaimEpoch)
                {
                    if (Interlocked.CompareExchange(ref drainList[i].epoch, int.MaxValue - 1, trigger_epoch) == trigger_epoch)
                    {
                        var trigger_action = drainList[i].action;
                        drainList[i].action = null;
                        drainList[i].epoch = int.MaxValue;
                        trigger_action();
                        if (Interlocked.Decrement(ref drainCount) == 0) break;
                    }
                }
            }
        }

        /// <summary>
        /// Thread acquires its epoch entry
        /// </summary>
        public void Acquire()
        {
            threadEntryIndex.InitializeThread();
            threadEntryIndex.Value = ReserveEntryForThread();
        }


        /// <summary>
        /// Thread releases its epoch entry
        /// </summary>
        public void Release()
        {
            int entry = threadEntryIndex.Value;
            if (kInvalidIndex == entry)
            {
                return;
            }

            threadEntryIndex.Value = kInvalidIndex;
            threadEntryIndex.DisposeThread();
            (*(tableAligned + entry)).localCurrentEpoch = 0;
            (*(tableAligned + entry)).threadId = 0;
        }

        /// <summary>
        /// Increment global current epoch
        /// </summary>
        /// <returns></returns>
        public int BumpCurrentEpoch()
        {
            int nextEpoch = Interlocked.Add(ref CurrentEpoch, 1);

            if (drainCount > 0)
                Drain(nextEpoch);

            return nextEpoch;
        }

        /// <summary>
        /// Increment current epoch and associate trigger action
        /// with the prior epoch
        /// </summary>
        /// <param name="onDrain">Trigger action</param>
        /// <returns></returns>
        public int BumpCurrentEpoch(Action onDrain)
        {
            int PriorEpoch = BumpCurrentEpoch() - 1;

            int i = 0, j = 0;
            while (true)
            {
                if (drainList[i].epoch == int.MaxValue)
                {
                    if (Interlocked.CompareExchange(ref drainList[i].epoch, int.MaxValue - 1, int.MaxValue) == int.MaxValue)
                    {
                        drainList[i].action = onDrain;
                        drainList[i].epoch = PriorEpoch;
                        Interlocked.Increment(ref drainCount);
                        break;
                    }
                }
                else
                {
                    var triggerEpoch = drainList[i].epoch;

                    if (triggerEpoch <= SafeToReclaimEpoch)
                    {
                        if (Interlocked.CompareExchange(ref drainList[i].epoch, int.MaxValue - 1, triggerEpoch) == triggerEpoch)
                        {
                            var triggerAction = drainList[i].action;
                            drainList[i].action = onDrain;
                            drainList[i].epoch = PriorEpoch;
                            triggerAction();
                            break;
                        }
                    }
                }

                if (++i == kDrainListSize)
                {
                    i = 0;
                    if (++j == 500)
                    {
                        j = 0;
                        Debug.WriteLine("Delay finding a free entry in the drain list");
                    }
                }
            }

            ProtectAndDrain();

            return PriorEpoch + 1;
        }

        /// <summary>
        /// Looks at all threads and return the latest safe epoch
        /// </summary>
        /// <param name="currentEpoch">Current epoch</param>
        /// <returns>Safe epoch</returns>
        private int ComputeNewSafeToReclaimEpoch(int currentEpoch)
        {
            int oldestOngoingCall = currentEpoch;

            for (int index = 1; index <= numEntries; ++index)
            {
                int entry_epoch = (*(tableAligned + index)).localCurrentEpoch;
                if (0 != entry_epoch)
                {
                    if (entry_epoch < oldestOngoingCall)
                    {
                        oldestOngoingCall = entry_epoch;
                    }
                }
            }

            // The latest safe epoch is the one just before 
            // the earliest unsafe epoch.
            SafeToReclaimEpoch = oldestOngoingCall - 1;
            return SafeToReclaimEpoch;
        }

        /// <summary>
        /// Reserve entry for thread. This method relies on the fact that no
        /// thread will ever have ID 0.
        /// </summary>
        /// <param name="startIndex">Start index</param>
        /// <param name="threadId">Thread id</param>
        /// <returns>Reserved entry</returns>
        private int ReserveEntry(int startIndex, int threadId)
        {
            int current_iteration = 0;
            for (; ; )
            {
                // Reserve an entry in the table.
                for (int i = 0; i < numEntries; ++i)
                {
                    int index_to_test = 1 + ((startIndex + i) & (numEntries - 1));
                    if (0 == (*(tableAligned + index_to_test)).threadId)
                    {
                        bool success =
                            (0 == Interlocked.CompareExchange(
                            ref (*(tableAligned + index_to_test)).threadId,
                            threadId, 0));

                        if (success)
                        {
                            return (int)index_to_test;
                        }
                    }
                    ++current_iteration;
                }

                if (current_iteration > (numEntries * 3))
                {
                    throw new Exception("Unable to reserve an epoch entry, try increasing the epoch table size (kTableSize)");
                }
            }
        }

        /// <summary>
        /// Allocate a new entry in epoch table. This is called 
        /// once for a thread.
        /// </summary>
        /// <returns>Reserved entry</returns>
        private int ReserveEntryForThread()
        {
            // for portability(run on non-windows platform)
            int threadId = Environment.OSVersion.Platform == PlatformID.Win32NT ? (int)Native32.GetCurrentThreadId() : Thread.CurrentThread.ManagedThreadId;
            int startIndex = Utility.Murmur3(threadId);
            return ReserveEntry(startIndex, threadId);
        }

        /// <summary>
        /// Epoch table entry (cache line size).
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = Constants.kCacheLineBytes)]
        private struct Entry
        {

            /// <summary>
            /// Thread-local value of epoch
            /// </summary>
            [FieldOffset(0)]
            public int localCurrentEpoch;

            /// <summary>
            /// ID of thread associated with this entry.
            /// </summary>
            [FieldOffset(4)]
            public int threadId;

            [FieldOffset(8)]
            public int reentrant;

            [FieldOffset(12)]
            public fixed int markers[13];
        };

        private struct EpochActionPair
        {
            public long epoch;
            public Action action;
        }

        /// <summary>
        /// Mechanism for threads to mark some activity as completed until
        /// some version by this thread, and check if all active threads 
        /// have completed the same activity until that version.
        /// </summary>
        /// <param name="markerIdx">ID of activity</param>
        /// <param name="version">Version</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MarkAndCheckIsComplete(int markerIdx, int version)
        {
            int entry = threadEntryIndex.Value;
            if (kInvalidIndex == entry)
            {
                Debug.WriteLine("New Thread entered during CPR");
                Debug.Assert(false);
            }

            (*(tableAligned + entry)).markers[markerIdx] = version;

            // check if all threads have reported complete
            for (int index = 1; index <= numEntries; ++index)
            {
                int entry_epoch = (*(tableAligned + index)).localCurrentEpoch;
                int fc_version = (*(tableAligned + index)).markers[markerIdx];
                if (0 != entry_epoch)
                {
                    if (fc_version != version)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
