﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;
using log4net;

namespace Marius.Mister
{
    public class MisterConnectionMaintenanceService<TValue, TKeyAtom, TValueAtom, TFaster>
        where TKeyAtom : new()
        where TValueAtom : new()
        where TFaster : IFasterKV<TKeyAtom, TValueAtom, byte[], TValue, object>
    {
        private struct MisterMaintenanceItem
        {
            public AutoResetEvent ResetEvent;
            public TaskCompletionSource<MisterVoid> TaskCompletionSource;

            public MisterMaintenanceItem(AutoResetEvent resetEvent)
            {
                ResetEvent = resetEvent;
                TaskCompletionSource = null;
            }

            public MisterMaintenanceItem(TaskCompletionSource<MisterVoid> taskCompletionSource)
            {
                TaskCompletionSource = taskCompletionSource;
                ResetEvent = null;
            }
        }

        private static readonly ILog Log = LogManager.GetLogger("MisterConnectionCheckpointService");

        private readonly DirectoryInfo _directory;
        private readonly FileInfo _checkpointTokenFile;
        private readonly FileInfo _checkpointTokenBackupFile;
        private readonly int _maintenanceIntervalMilliseconds;
        private readonly string _name;

        private readonly Guid[] _takenCheckpoints;

        private int _takenCount;
        private int _checkpointVersion;
        private int _currentCheckpointVersion;

        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly ConcurrentQueue<MisterMaintenanceItem> _maintenanceQueue;
        private Thread _maintenanceThread;
        private int _isRunning;

        protected TFaster _faster;

        public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

        public MisterConnectionMaintenanceService(DirectoryInfo directory, int maintenanceIntervalMilliseconds, int checkpointCleanCount, string name)
        {
            _directory = directory;
            _checkpointTokenFile = new FileInfo(Path.Combine(directory.FullName, "checkpoint_token.txt"));
            _checkpointTokenBackupFile = new FileInfo(Path.Combine(directory.FullName, "checkpoint_token_backup.txt"));
            _maintenanceIntervalMilliseconds = maintenanceIntervalMilliseconds;
            _name = name;

            _takenCheckpoints = new Guid[checkpointCleanCount];

            _cancellationTokenSource = new CancellationTokenSource();
            _maintenanceQueue = new ConcurrentQueue<MisterMaintenanceItem>();
        }

        public void Start()
        {
            _maintenanceThread = new Thread(MaintenanceLoop) { IsBackground = true, Name = $"{_name ?? "Mister"} checkpoint thread" };
            _maintenanceThread.Start();
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();

            lock (_maintenanceQueue)
                Monitor.PulseAll(_maintenanceQueue);

            _maintenanceThread.Join();
        }

        public void Close()
        {
            PerformCheckpoint();

            _cancellationTokenSource.Dispose();
        }

        public void Recover(Func<TFaster> create)
        {
            CleanCheckpoints();

            if (!TryRecover(create, _checkpointTokenFile))
            {
                if (!TryRecover(create, _checkpointTokenBackupFile))
                    _faster = create();
            }
        }

        public void IncrementVersion()
        {
            Interlocked.Increment(ref _checkpointVersion);
        }

        public void Checkpoint()
        {
            using (var handle = new AutoResetEvent(false))
            {
                _maintenanceQueue.Enqueue(new MisterMaintenanceItem(handle));

                lock (_maintenanceQueue)
                    Monitor.Pulse(_maintenanceQueue);

                handle.WaitOne();
            }
        }

        public Task CheckpointAsync()
        {
            var tsc = new TaskCompletionSource<MisterVoid>(TaskCreationOptions.RunContinuationsAsynchronously);
            _maintenanceQueue.Enqueue(new MisterMaintenanceItem(tsc));

            lock (_maintenanceQueue)
                Monitor.Pulse(_maintenanceQueue);

            return tsc.Task;
        }

        protected virtual void Maintain()
        {
            PerformCheckpoint();
        }

        protected void PerformCheckpoint()
        {
            var newCheckpoint = Volatile.Read(ref _checkpointVersion);
            if (newCheckpoint != _currentCheckpointVersion)
            {
                _currentCheckpointVersion = newCheckpoint;

                _faster.StartSession();
                _faster.TakeFullCheckpoint(out var token);
                _faster.CompleteCheckpoint(true);
                _faster.StopSession();

                _takenCheckpoints[_takenCount++] = token;
                if (_takenCount >= _takenCheckpoints.Length)
                {
                    for (var i = 0; i < _takenCheckpoints.Length - 2; i++)
                    {
                        try
                        {
                            var info = new DirectoryInfo(Path.Combine(_directory.FullName, "cpr-checkpoints", _takenCheckpoints[i].ToString()));
                            if (info.Exists)
                                info.Delete(true);
                        }
                        catch { }

                        try
                        {
                            var info = new DirectoryInfo(Path.Combine(_directory.FullName, "index-checkpoints", _takenCheckpoints[i].ToString()));
                            if (info.Exists)
                                info.Delete(true);
                        }
                        catch { }
                    }

                    _takenCheckpoints[0] = _takenCheckpoints[_takenCheckpoints.Length - 2];
                    _takenCheckpoints[1] = _takenCheckpoints[_takenCheckpoints.Length - 1];
                    _takenCount = 2;
                }

                try
                {
                    if (!_checkpointTokenFile.Exists)
                    {
                        using (var writer = _checkpointTokenFile.CreateText())
                            writer.WriteLine(token.ToString());

                        _checkpointTokenFile.Refresh();
                    }
                    else
                    {
                        var temp = new FileInfo(Path.Combine(_directory.FullName, $"checkpoint_{token}.txt"));
                        using (var writer = temp.CreateText())
                            writer.WriteLine(token.ToString());

                        File.Replace(temp.FullName, _checkpointTokenFile.FullName, _checkpointTokenBackupFile.FullName, true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            }
        }

        private bool TryRecover(Func<TFaster> create, FileInfo checkpointTokenFile)
        {
            if (checkpointTokenFile.Exists)
            {
                try
                {
                    var checkpointToken = default(Guid?);
                    using (var reader = checkpointTokenFile.OpenText())
                    {
                        var line = reader.ReadLine();
                        if (Guid.TryParse(line, out var result))
                            checkpointToken = result;
                    }

                    if (checkpointToken != null)
                    {
                        _faster = create();
                        _faster.Recover(checkpointToken.Value);
                    }
                    else
                    {
                        checkpointTokenFile.Delete();
                    }

                    return checkpointToken != null;
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            }

            return false;
        }

        private void CleanCheckpoints()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_checkpointTokenFile.Exists)
            {
                using (var reader = _checkpointTokenFile.OpenText())
                {
                    var line = reader.ReadLine();
                    if (Guid.TryParse(line, out var result))
                        set.Add(result.ToString());
                }
            }

            if (_checkpointTokenBackupFile.Exists)
            {
                using (var reader = _checkpointTokenBackupFile.OpenText())
                {
                    var line = reader.ReadLine();
                    if (Guid.TryParse(line, out var result))
                        set.Add(result.ToString());
                }
            }

            try
            {
                var info = new DirectoryInfo(Path.Combine(_directory.FullName, "cpr-checkpoints"));
                foreach (var item in info.EnumerateDirectories())
                {
                    if (set.Contains(item.Name))
                        continue;

                    try
                    {
                        item.Delete(true);
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                var info = new DirectoryInfo(Path.Combine(_directory.FullName, "index-checkpoints"));
                foreach (var item in info.EnumerateDirectories())
                {
                    if (set.Contains(item.Name))
                        continue;

                    try
                    {
                        item.Delete(true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void MaintenanceLoop()
        {
            try
            {
                Volatile.Write(ref _isRunning, 1);

                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    var checkpointItem = default(MisterMaintenanceItem);
                    lock (_maintenanceQueue)
                    {
                        if (_maintenanceQueue.IsEmpty && !_cancellationTokenSource.IsCancellationRequested)
                            Monitor.Wait(_maintenanceQueue, _maintenanceIntervalMilliseconds);
                    }

                    _maintenanceQueue.TryDequeue(out checkpointItem);

                    Maintain();

                    if (checkpointItem.ResetEvent != null)
                        checkpointItem.ResetEvent.Set();

                    if (checkpointItem.TaskCompletionSource != null)
                        checkpointItem.TaskCompletionSource.SetResult(MisterVoid.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            finally
            {
                Volatile.Write(ref _isRunning, 0);
            }
        }
    }
}