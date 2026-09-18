using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZLinq;

namespace Manimal.MotionMatching
{
    /// <summary>
    /// Bounded, single-writer raid telemetry recorder. Record and Event must be called on
    /// the main thread with immutable, detached DTOs containing no Unity objects or user
    /// profile data. Serialization, checkpoint writes and ZIP creation run on a worker.
    /// </summary>
    internal sealed class RaidReportRecorder
    {
        private const string FilePrefix = "report-";
        private const int SchemaVersion = 1;
        private static readonly object RegistryLock = new object();
        private static readonly HashSet<string> CleanedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ActiveReportPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _directory;
        private readonly string _reportStem;
        private readonly string _checkpointPath;
        private readonly string _archivePath;
        private readonly RaidReportRecorderOptions _options;
        private readonly JToken _metadata;
        private readonly Dictionary<int, BotBuffer> _bots = new Dictionary<int, BotBuffer>();
        private readonly List<ReplayWindow> _windows = new List<ReplayWindow>();
        private readonly List<EventRecord> _events = new List<EventRecord>();
        private readonly Dictionary<TriggerKey, float> _lastTriggerTimes = new Dictionary<TriggerKey, float>();
        private readonly Dictionary<string, long> _aggregates = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly object _finishLock = new object();

        private Task _checkpointTask;
        private Task<string> _finishTask;
        private bool _finished;
        private int _checkpointRunning;
        private float _nextCheckpointTime;
        private long _nextFrameId;
        private long _nextEventIndex;
        private int _retainedFrameCount;
        private int _peakRetainedFrameCount;
        private float _firstTime = float.NaN;
        private float _latestTime = float.NaN;
        private float _lastRoutineTriggerTime = float.NaN;

        private long _recordCalls;
        private long _retainedFrameRecords;
        private long _ringPrunedFrames;
        private long _perBotLimitDrops;
        private long _globalFrameLimitDrops;
        private long _invalidFrameDrops;
        private long _botBufferEvictions;
        private long _clockRegressions;
        private long _eventCalls;
        private long _eventDrops;
        private long _anomalyTriggers;
        private long _routineTriggers;
        private long _suppressedCooldown;
        private long _suppressedWindowLimit;
        private long _suppressedRoutineLimit;
        private long _serializationFailures;
        private long _framesDroppedAtSerialization;
        private long _windowsDroppedAtSerialization;
        private long _eventsDroppedAtSerialization;
        private long _checkpointCount;
        private long _checkpointSkipped;
        private long _checkpointFrameDrops;
        private long _checkpointFailures;
        private long _ringMemoryPrunedFrames;
        private long _windowsEvictedForMemory;
        private long _windowsReplacedByNewer;
        private long _aggregateOverflow;

        internal RaidReportRecorder(string directory, object metadata)
            : this(directory, metadata, null)
        {
        }

        internal RaidReportRecorder(string directory, object metadata, RaidReportRecorderOptions options)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("A report directory is required.", nameof(directory));

            _directory = directory;
            _options = (options ?? new RaidReportRecorderOptions()).CloneAndClamp();
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            long suffix = Interlocked.Increment(ref RaidReportRecorderSequence.Value);
            _reportStem = FilePrefix + stamp + "-" + suffix.ToString("D3", CultureInfo.InvariantCulture);
            _checkpointPath = Path.Combine(_directory, _reportStem + ".partial.jsonl");
            _archivePath = Path.Combine(_directory, _reportStem + ".zip");
            _metadata = CaptureSafeMetadata(metadata);
            _nextCheckpointTime = _options.CheckpointIntervalSeconds;

            Directory.CreateDirectory(_directory);
            string fullDirectory = Path.GetFullPath(_directory);
            lock (RegistryLock)
            {
                if (CleanedDirectories.Add(fullDirectory))
                    CleanupAbandonedTemporaryFiles(fullDirectory);
                ActiveReportPaths.Add(Path.GetFullPath(_checkpointPath));
                ActiveReportPaths.Add(Path.GetFullPath(_archivePath));
            }
            ApplyRetention(_directory, _options.RetainReports, _options.RetainedBytes, null);
        }

        internal string CheckpointPath => _checkpointPath;

        internal void Record(int anonymousBotId, float time, string stage, object snapshot, string trigger = null)
        {
            if (_finished)
                return;

            _recordCalls++;
            if (!IsFinite(time) || snapshot == null)
            {
                _invalidFrameDrops++;
                return;
            }

            if (float.IsNaN(_firstTime))
                _firstTime = time;
            if (float.IsNaN(_latestTime) || time > _latestTime)
                _latestTime = time;

            bool routine = IsRoutineTrigger(trigger);
            if (!string.IsNullOrWhiteSpace(trigger))
            {
                if (routine)
                    _routineTriggers++;
                else
                    _anomalyTriggers++;
            }

            BotBuffer buffer = GetOrCreateBot(anonymousBotId, time);
            if (buffer == null)
            {
                _globalFrameLimitDrops++;
                return;
            }

            if (!float.IsNaN(buffer.LastRecordTime) && time < buffer.LastRecordTime - 0.001f)
            {
                _clockRegressions++;
                ClearRing(buffer);
            }
            buffer.LastRecordTime = time;
            buffer.LastTouchedTime = time;

            PruneRing(buffer, time - _options.HistorySeconds);
            while (buffer.Frames.Count >= _options.MaxFramesPerBot)
            {
                RemoveRingFrameAt(buffer, 0);
                _perBotLimitDrops++;
            }

            for (int windowIndex = 0; windowIndex < _windows.Count; windowIndex++)
            {
                ReplayWindow existing = _windows[windowIndex];
                if (existing.BotId == anonymousBotId && time > existing.EndTime)
                    existing.Complete = true;
            }

            FrameRecord frame = new FrameRecord
            {
                Id = ++_nextFrameId,
                BotId = anonymousBotId,
                Time = time,
                Stage = LimitText(stage, 80),
                Snapshot = snapshot,
                Trigger = string.IsNullOrWhiteSpace(trigger) ? null : LimitText(trigger, 120)
            };

            if (!MakeRoomForFrame(routine, !string.IsNullOrWhiteSpace(trigger)))
            {
                _globalFrameLimitDrops++;
                // Trigger calls are still counted even when the hard frame-object bound wins.
                AdmitTriggerIfPossible(anonymousBotId, time, trigger, routine);
                MaybeScheduleCheckpoint(time);
                return;
            }

            AddFrameReference(frame);
            buffer.Frames.Add(frame);
            _retainedFrameRecords++;
            if (_retainedFrameCount > _peakRetainedFrameCount)
                _peakRetainedFrameCount = _retainedFrameCount;

            for (int windowIndex = 0; windowIndex < _windows.Count; windowIndex++)
            {
                ReplayWindow existing = _windows[windowIndex];
                if (frame.BotId == existing.BotId && time >= existing.TriggerTime && time <= existing.EndTime)
                    AddToWindow(existing, frame);
                else if (frame.BotId == existing.BotId && time > existing.EndTime)
                    existing.Complete = true;
            }

            if (!string.IsNullOrWhiteSpace(trigger))
                AdmitTriggerIfPossible(anonymousBotId, time, trigger, routine);

            MaybeScheduleCheckpoint(time);
        }

        internal void Event(object row)
        {
            if (_finished)
                return;
            _eventCalls++;
            if (row == null || _events.Count >= _options.MaxEventRows)
            {
                _eventDrops++;
                return;
            }

            _events.Add(new EventRecord { Index = ++_nextEventIndex, Row = row });
        }

        internal void Count(string key, long amount = 1)
        {
            if (_finished || string.IsNullOrWhiteSpace(key) || amount <= 0)
                return;

            string safeKey = LimitText(key, 96);
            long current;
            if (!_aggregates.TryGetValue(safeKey, out current) && _aggregates.Count >= 128)
            {
                _aggregateOverflow += amount;
                return;
            }
            _aggregates[safeKey] = current + amount;
        }

        internal Task<string> Finish(string reason)
        {
            lock (_finishLock)
            {
                if (_finishTask != null)
                    return _finishTask;

                _finished = true;
                string safeReason = LimitText(reason, 160);
                RecorderSnapshot snapshot = CaptureSnapshot(safeReason);
                Task checkpoint = _checkpointTask;
                _finishTask = Task.Run(async () =>
                {
                    try
                    {
                        if (checkpoint != null)
                        {
                            try { await checkpoint.ConfigureAwait(false); }
                            catch { /* The last atomically replaced checkpoint remains recoverable. */ }
                        }
                        return WriteArchive(snapshot);
                    }
                    finally
                    {
                        lock (RegistryLock)
                        {
                            ActiveReportPaths.Remove(Path.GetFullPath(_checkpointPath));
                            ActiveReportPaths.Remove(Path.GetFullPath(_archivePath));
                        }
                    }
                });
                return _finishTask;
            }
        }

        private BotBuffer GetOrCreateBot(int botId, float time)
        {
            BotBuffer existing;
            if (_bots.TryGetValue(botId, out existing))
                return existing;

            if (_bots.Count >= _options.MaxTrackedBots)
            {
                BotBuffer oldest = null;
                foreach (KeyValuePair<int, BotBuffer> pair in _bots)
                {
                    if (oldest == null || pair.Value.LastTouchedTime < oldest.LastTouchedTime)
                        oldest = pair.Value;
                }

                if (oldest == null)
                    return null;
                _bots.Remove(oldest.BotId);
                for (int i = 0; i < oldest.Frames.Count; i++)
                    RemoveFrameReference(oldest.Frames[i]);
                _botBufferEvictions++;
            }

            BotBuffer created = new BotBuffer { BotId = botId, LastTouchedTime = time, LastRecordTime = float.NaN };
            _bots.Add(botId, created);
            return created;
        }

        private void PruneRing(BotBuffer buffer, float oldestAllowedTime)
        {
            int removeCount = 0;
            while (removeCount < buffer.Frames.Count && buffer.Frames[removeCount].Time < oldestAllowedTime)
                removeCount++;
            for (int i = 0; i < removeCount; i++)
                RemoveRingFrameAt(buffer, 0);
            _ringPrunedFrames += removeCount;
        }

        private void ClearRing(BotBuffer buffer)
        {
            int count = buffer.Frames.Count;
            for (int i = count - 1; i >= 0; i--)
                RemoveRingFrameAt(buffer, i);
            _ringPrunedFrames += count;
        }

        private void RemoveRingFrameAt(BotBuffer buffer, int index)
        {
            FrameRecord frame = buffer.Frames[index];
            buffer.Frames.RemoveAt(index);
            RemoveFrameReference(frame);
        }

        private bool MakeRoomForFrame(bool routine, bool trigger)
        {
            if (_retainedFrameCount < _options.MaxRetainedFrames)
                return true;

            // Finished windows are least costly to discard, and baselines are discarded first.
            while (_retainedFrameCount >= _options.MaxRetainedFrames)
            {
                int candidate = FindOldestWindow(true, true);
                if (candidate < 0)
                    candidate = FindOldestWindow(true, false);
                if (candidate < 0 && trigger)
                    candidate = FindOldestWindow(false, true);
                if (candidate < 0 && trigger && !routine)
                    candidate = FindOldestWindow(false, false);
                if (candidate < 0)
                {
                    if (EvictOldestUnwindowedRingFrame())
                        continue;
                    return false;
                }
                RemoveWindowAt(candidate, true);
            }
            return true;
        }

        private int FindOldestWindow(bool completedOnly, bool routineOnly)
        {
            int found = -1;
            float oldestTime = float.PositiveInfinity;
            for (int i = 0; i < _windows.Count; i++)
            {
                ReplayWindow window = _windows[i];
                if ((completedOnly && !window.Complete) || (routineOnly && !window.Routine))
                    continue;
                if (window.TriggerTime < oldestTime)
                {
                    oldestTime = window.TriggerTime;
                    found = i;
                }
            }
            return found;
        }

        private int FindOldestWindowOfClass(bool completedOnly, bool routine)
        {
            int found = -1;
            float oldestTime = float.PositiveInfinity;
            for (int i = 0; i < _windows.Count; i++)
            {
                ReplayWindow window = _windows[i];
                if ((completedOnly && !window.Complete) || window.Routine != routine)
                    continue;
                if (window.TriggerTime < oldestTime)
                {
                    oldestTime = window.TriggerTime;
                    found = i;
                }
            }
            return found;
        }

        private void RemoveWindowAt(int index, bool forMemory)
        {
            ReplayWindow window = _windows[index];
            _windows.RemoveAt(index);
            for (int i = 0; i < window.Frames.Count; i++)
                RemoveFrameReference(window.Frames[i]);
            if (forMemory)
                _windowsEvictedForMemory++;
        }

        private bool EvictOldestUnwindowedRingFrame()
        {
            BotBuffer selectedBuffer = null;
            int selectedIndex = -1;
            float oldestTime = float.PositiveInfinity;
            foreach (KeyValuePair<int, BotBuffer> pair in _bots)
            {
                List<FrameRecord> frames = pair.Value.Frames;
                for (int i = 0; i < frames.Count; i++)
                {
                    FrameRecord candidate = frames[i];
                    if (candidate.References == 1 && candidate.Time < oldestTime)
                    {
                        oldestTime = candidate.Time;
                        selectedBuffer = pair.Value;
                        selectedIndex = i;
                    }
                }
            }
            if (selectedBuffer == null)
                return false;
            RemoveRingFrameAt(selectedBuffer, selectedIndex);
            _ringMemoryPrunedFrames++;
            return true;
        }

        private void AddFrameReference(FrameRecord frame)
        {
            if (frame.References++ == 0)
                _retainedFrameCount++;
        }

        private void RemoveFrameReference(FrameRecord frame)
        {
            if (frame.References <= 0)
                return;
            frame.References--;
            if (frame.References == 0)
                _retainedFrameCount--;
        }

        private static void AddToWindow(ReplayWindow window, FrameRecord frame)
        {
            window.Frames.Add(frame);
            frame.References++;
        }

        private void AdmitTriggerIfPossible(int botId, float time, string trigger, bool routine)
        {
            string safeTrigger = LimitText(trigger, 120);
            TriggerKey key = new TriggerKey(botId, safeTrigger);
            float lastTime;
            float cooldown = routine ? _options.RoutineTriggerCooldownSeconds : _options.TriggerCooldownSeconds;
            if (routine && !float.IsNaN(_lastRoutineTriggerTime) && time - _lastRoutineTriggerTime < cooldown)
            {
                _suppressedCooldown++;
                return;
            }
            if (!routine && _lastTriggerTimes.TryGetValue(key, out lastTime) && time - lastTime < cooldown)
            {
                _suppressedCooldown++;
                return;
            }
            if (routine)
                _lastRoutineTriggerTime = time;
            else
            {
                _lastTriggerTimes[key] = time;
                if (_lastTriggerTimes.Count > 128)
                    RemoveOldestTriggerKey();
            }

            int routineCount = 0;
            int anomalyCount = 0;
            for (int i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].Routine) routineCount++;
                else anomalyCount++;
            }

            bool atClassLimit = routine ? routineCount >= _options.MaxRoutineWindows : anomalyCount >= _options.MaxAnomalyWindows;
            if (atClassLimit)
            {
                int replace = FindOldestWindowOfClass(true, routine);
                if (replace >= 0)
                {
                    RemoveWindowAt(replace, false);
                    _windowsReplacedByNewer++;
                }
                else
                {
                    if (routine)
                        _suppressedRoutineLimit++;
                    else
                        _suppressedWindowLimit++;
                    return;
                }
            }
            if (_windows.Count >= _options.MaxWindows)
            {
                _suppressedWindowLimit++;
                return;
            }

            ReplayWindow window = new ReplayWindow
            {
                Id = ++_nextWindowId,
                BotId = botId,
                Trigger = safeTrigger,
                TriggerTime = time,
                StartTime = time - _options.HistorySeconds,
                EndTime = time + _options.TailSeconds,
                Routine = routine
            };

            BotBuffer targetBuffer;
            if (_bots.TryGetValue(botId, out targetBuffer))
            {
                List<FrameRecord> frames = targetBuffer.Frames;
                for (int i = 0; i < frames.Count; i++)
                {
                    FrameRecord frame = frames[i];
                    if (frame.Time >= window.StartTime && frame.Time <= time)
                        AddToWindow(window, frame);
                }
            }

            if (IsFinite(_latestTime) && _latestTime > window.EndTime)
                window.Complete = true;
            _windows.Add(window);
        }

        private long _nextWindowId;

        private void RemoveOldestTriggerKey()
        {
            bool found = false;
            TriggerKey oldestKey = default(TriggerKey);
            float oldestTime = float.PositiveInfinity;
            foreach (KeyValuePair<TriggerKey, float> pair in _lastTriggerTimes)
            {
                if (pair.Value < oldestTime)
                {
                    found = true;
                    oldestTime = pair.Value;
                    oldestKey = pair.Key;
                }
            }
            if (found)
                _lastTriggerTimes.Remove(oldestKey);
        }

        private void MaybeScheduleCheckpoint(float time)
        {
            if (time < _nextCheckpointTime)
                return;
            while (_nextCheckpointTime <= time)
                _nextCheckpointTime += _options.CheckpointIntervalSeconds;

            if (Interlocked.CompareExchange(ref _checkpointRunning, 1, 0) != 0)
            {
                _checkpointSkipped++;
                return;
            }

            RecorderSnapshot snapshot;
            try
            {
                snapshot = CaptureSnapshot("in-progress");
            }
            catch
            {
                Interlocked.Exchange(ref _checkpointRunning, 0);
                _checkpointFailures++;
                return;
            }

            _checkpointTask = Task.Run(() =>
            {
                try
                {
                    WriteCheckpoint(snapshot);
                    _checkpointCount++;
                }
                catch
                {
                    _checkpointFailures++;
                }
                finally
                {
                    Interlocked.Exchange(ref _checkpointRunning, 0);
                }
            });
        }

        private RecorderSnapshot CaptureSnapshot(string reason)
        {
            HashSet<FrameRecord> unique = new HashSet<FrameRecord>();
            foreach (KeyValuePair<int, BotBuffer> pair in _bots)
            {
                List<FrameRecord> frames = pair.Value.Frames;
                for (int i = 0; i < frames.Count; i++)
                    unique.Add(frames[i]);
            }
            for (int i = 0; i < _windows.Count; i++)
            {
                List<FrameRecord> frames = _windows[i].Frames;
                for (int j = 0; j < frames.Count; j++)
                    unique.Add(frames[j]);
            }

            List<FrameRecord> sorted = new List<FrameRecord>(unique);
            sorted.Sort((a, b) => a.Id.CompareTo(b.Id));

            List<WindowSnapshot> windows = new List<WindowSnapshot>(_windows.Count);
            for (int i = 0; i < _windows.Count; i++)
            {
                ReplayWindow source = _windows[i];
                long[] frameIds = new long[source.Frames.Count];
                for (int j = 0; j < source.Frames.Count; j++)
                    frameIds[j] = source.Frames[j].Id;
                windows.Add(new WindowSnapshot
                {
                    Id = source.Id,
                    BotId = source.BotId,
                    Trigger = source.Trigger,
                    TriggerTime = source.TriggerTime,
                    StartTime = source.StartTime,
                    EndTime = source.EndTime,
                    Routine = source.Routine,
                    Complete = source.Complete || (!float.IsNaN(_latestTime) && _latestTime >= source.EndTime),
                    FrameIds = frameIds
                });
            }

            EventRecord[] events = new EventRecord[_events.Count];
            for (int i = 0; i < _events.Count; i++)
                events[i] = _events[i];

            return new RecorderSnapshot
            {
                Metadata = _metadata,
                Reason = reason,
                Frames = sorted.ToArray(),
                Windows = windows.ToArray(),
                Events = events,
                Counts = CaptureCounts()
            };
        }

        private Dictionary<string, object> CaptureCounts()
        {
            Dictionary<string, object> counts = new Dictionary<string, object>
            {
                ["recordCalls"] = _recordCalls,
                ["retainedFrameRecords"] = _retainedFrameRecords,
                ["currentRetainedFrameObjects"] = _retainedFrameCount,
                ["peakRetainedFrameObjects"] = _peakRetainedFrameCount,
                ["ringPrunedFrames"] = _ringPrunedFrames,
                ["ringMemoryPrunedFrames"] = _ringMemoryPrunedFrames,
                ["perBotFrameLimitDrops"] = _perBotLimitDrops,
                ["globalFrameLimitDrops"] = _globalFrameLimitDrops,
                ["invalidFrameDrops"] = _invalidFrameDrops,
                ["botBufferEvictions"] = _botBufferEvictions,
                ["clockRegressions"] = _clockRegressions,
                ["eventCalls"] = _eventCalls,
                ["eventBufferDrops"] = _eventDrops,
                ["anomalyTriggers"] = _anomalyTriggers,
                ["routineTriggers"] = _routineTriggers,
                ["suppressedByCooldown"] = _suppressedCooldown,
                ["suppressedAnomalyWindows"] = _suppressedWindowLimit,
                ["suppressedRoutineWindows"] = _suppressedRoutineLimit,
                ["windowsEvictedForMemory"] = _windowsEvictedForMemory,
                ["windowsReplacedByNewer"] = _windowsReplacedByNewer,
                ["lateTriggersDropped"] = _suppressedCooldown + _suppressedWindowLimit + _suppressedRoutineLimit,
                ["aggregateCounterOverflow"] = _aggregateOverflow,
                ["serializationFailures"] = _serializationFailures,
                ["framesDroppedAtSerialization"] = _framesDroppedAtSerialization,
                ["windowsDroppedAtSerialization"] = _windowsDroppedAtSerialization,
                ["eventsDroppedAtSerialization"] = _eventsDroppedAtSerialization,
                ["checkpointCount"] = _checkpointCount,
                ["checkpointsSkippedWhileBusy"] = _checkpointSkipped,
                ["checkpointFrameDrops"] = _checkpointFrameDrops,
                ["checkpointFailures"] = _checkpointFailures,
                ["trackedBots"] = _bots.Count,
                ["retainedWindows"] = _windows.Count,
                ["retainedEvents"] = _events.Count,
                ["actualDurationSeconds"] = DurationSeconds(),
                ["latestTimeSeconds"] = float.IsNaN(_latestTime) ? 0f : _latestTime
            };
            counts["eventTotals"] = new Dictionary<string, long>(_aggregates, StringComparer.OrdinalIgnoreCase);
            return counts;
        }

        private Dictionary<string, object> CaptureLimits()
        {
            return new Dictionary<string, object>
            {
                ["historySeconds"] = _options.HistorySeconds,
                ["tailSeconds"] = _options.TailSeconds,
                ["triggerCooldownSeconds"] = _options.TriggerCooldownSeconds,
                ["routineTriggerCooldownSeconds"] = _options.RoutineTriggerCooldownSeconds,
                ["maxTrackedBots"] = _options.MaxTrackedBots,
                ["maxFramesPerBot"] = _options.MaxFramesPerBot,
                ["maxRetainedFrameObjects"] = _options.MaxRetainedFrames,
                ["maxAnomalyWindows"] = _options.MaxAnomalyWindows,
                ["maxRoutineWindows"] = _options.MaxRoutineWindows,
                ["maxWindows"] = _options.MaxWindows,
                ["maxEventRows"] = _options.MaxEventRows,
                ["maxReportBytes"] = _options.MaxArchiveBytes,
                ["checkpointIntervalSeconds"] = _options.CheckpointIntervalSeconds,
                ["checkpointMaxBytes"] = _options.MaxCheckpointBytes,
                ["maxSerializedRowBytes"] = _options.MaxSerializedRowBytes
            };
        }

        private float DurationSeconds()
        {
            if (float.IsNaN(_firstTime) || float.IsNaN(_latestTime))
                return 0f;
            return Math.Max(0f, _latestTime - _firstTime);
        }

        private void WriteCheckpoint(RecorderSnapshot snapshot)
        {
            string temporaryPath = _checkpointPath + ".tmp";
            long written = 0;
            long droppedFrames = 0;
            long droppedEvents = 0;
            long droppedWindows = 0;
            long budget = _options.MaxCheckpointBytes;
            try
            {
                using (FileStream file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false)))
                {
                    Dictionary<string, object> header = new Dictionary<string, object>
                    {
                        ["kind"] = "checkpoint",
                        ["schemaVersion"] = SchemaVersion,
                        ["savedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        ["metadata"] = snapshot.Metadata,
                        ["reason"] = snapshot.Reason,
                        ["counts"] = snapshot.Counts,
                        ["limits"] = CaptureLimits()
                    };
                    if (!WriteCheckpointLine(writer, header, ref written, budget))
                        throw new InvalidDataException("The checkpoint header exceeded its size limit.");

                    JsonSerializer serializer = CreateSerializer();
                    for (int i = 0; i < snapshot.Frames.Length; i++)
                    {
                        FrameRecord frame = snapshot.Frames[i];
                        Dictionary<string, object> row = FrameRow(frame, "frame");
                        if (!WriteCheckpointLine(writer, row, ref written, budget, serializer))
                            droppedFrames++;
                    }
                    for (int i = 0; i < snapshot.Windows.Length; i++)
                    {
                        if (!WriteCheckpointLine(writer, WindowRow(snapshot.Windows[i], "window"), ref written, budget, serializer))
                            droppedWindows++;
                    }
                    for (int i = 0; i < snapshot.Events.Length; i++)
                    {
                        Dictionary<string, object> row = new Dictionary<string, object>
                        {
                            ["kind"] = "event",
                            ["eventIndex"] = snapshot.Events[i].Index,
                            ["row"] = snapshot.Events[i].Row
                        };
                        if (!WriteCheckpointLine(writer, row, ref written, budget, serializer))
                            droppedEvents++;
                    }
                    Dictionary<string, object> footer = new Dictionary<string, object>
                    {
                        ["kind"] = "checkpointSummary",
                        ["bytesWritten"] = written,
                        ["framesOmitted"] = droppedFrames,
                        ["windowsOmitted"] = droppedWindows,
                        ["eventsOmitted"] = droppedEvents
                    };
                    WriteCheckpointLine(writer, footer, ref written, budget, serializer);
                    writer.Flush();
                }
                ReplaceFileAtomically(temporaryPath, _checkpointPath);
                _checkpointFrameDrops += droppedFrames;
            }
            catch
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                throw;
            }
        }

        private bool WriteCheckpointLine(StreamWriter writer, object value, ref long written, long budget, JsonSerializer serializer = null)
        {
            byte[] bytes;
            try
            {
                bytes = SerializeBounded(value, _options.MaxSerializedRowBytes, serializer);
            }
            catch
            {
                return false;
            }

            long lineBytes = bytes.Length + 1L;
            if (written + lineBytes > budget)
                return false;
            writer.BaseStream.Write(bytes, 0, bytes.Length);
            writer.BaseStream.WriteByte((byte)'\n');
            written += lineBytes;
            return true;
        }

        private string WriteArchive(RecorderSnapshot snapshot)
        {
            string finalPath = _archivePath;
            string temporaryPath = finalPath + ".partial";
            long payloadWritten = 0;
            long payloadBudget = Math.Max(1024L, (long)(_options.MaxArchiveBytes * 0.80));
            long frameBudget = Math.Max(512L, (long)(payloadBudget * 0.70));
            long windowBudget = Math.Max(256L, (long)(payloadBudget * 0.10));
            long eventBudget = Math.Max(256L, payloadBudget - frameBudget - windowBudget);
            long frameBytes = 0;
            long windowBytes = 0;
            long eventBytes = 0;
            HashSet<long> includedFrames = new HashSet<long>();
            int serializedWindows = 0;

            try
            {
                using (FileStream file = new FileStream(temporaryPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    JsonSerializer serializer = CreateSerializer();
                    ZipArchiveEntry frameEntry = archive.CreateEntry("frames.jsonl", CompressionLevel.Optimal);
                    using (Stream entry = frameEntry.Open())
                    {
                        for (int i = 0; i < snapshot.Frames.Length; i++)
                        {
                            FrameRecord frame = snapshot.Frames[i];
                            byte[] line;
                            try
                            {
                                line = SerializeBounded(FrameRow(frame, null), _options.MaxSerializedRowBytes, serializer);
                            }
                            catch
                            {
                                _serializationFailures++;
                                _framesDroppedAtSerialization++;
                                continue;
                            }

                            if (frameBytes + line.Length + 1 > frameBudget)
                            {
                                _framesDroppedAtSerialization++;
                                continue;
                            }
                            WriteLine(entry, line);
                            long lineBytes = line.Length + 1L;
                            frameBytes += lineBytes;
                            payloadWritten += lineBytes;
                            includedFrames.Add(frame.Id);
                        }
                    }

                    ZipArchiveEntry windowEntry = archive.CreateEntry("windows.jsonl", CompressionLevel.Optimal);
                    using (Stream entry = windowEntry.Open())
                    {
                        for (int i = 0; i < snapshot.Windows.Length; i++)
                        {
                            WindowSnapshot source = snapshot.Windows[i];
                            long[] available = source.FrameIds.AsValueEnumerable()
                                .Where(frameId => includedFrames.Contains(frameId))
                                .ToArray<long>();
                            Dictionary<string, object> row = WindowRow(source, null);
                            row["frameIds"] = available;
                            byte[] line;
                            try
                            {
                                line = SerializeBounded(row, _options.MaxSerializedRowBytes, serializer);
                            }
                            catch
                            {
                                _serializationFailures++;
                                _windowsDroppedAtSerialization++;
                                continue;
                            }
                            if (windowBytes + line.Length + 1 > windowBudget || payloadWritten + line.Length + 1 > payloadBudget)
                            {
                                _windowsDroppedAtSerialization++;
                                continue;
                            }
                            WriteLine(entry, line);
                            long lineBytes = line.Length + 1L;
                            windowBytes += lineBytes;
                            payloadWritten += lineBytes;
                            serializedWindows++;
                        }
                    }

                    ZipArchiveEntry eventEntry = archive.CreateEntry("events.jsonl", CompressionLevel.Optimal);
                    using (Stream entry = eventEntry.Open())
                    {
                        for (int i = 0; i < snapshot.Events.Length; i++)
                        {
                            Dictionary<string, object> row = new Dictionary<string, object>
                            {
                                ["eventIndex"] = snapshot.Events[i].Index,
                                ["row"] = snapshot.Events[i].Row
                            };
                            byte[] line;
                            try
                            {
                                line = SerializeBounded(row, _options.MaxSerializedRowBytes, serializer);
                            }
                            catch
                            {
                                _serializationFailures++;
                                _eventsDroppedAtSerialization++;
                                continue;
                            }
                            if (eventBytes + line.Length + 1 > eventBudget || payloadWritten + line.Length + 1 > payloadBudget)
                            {
                                _eventsDroppedAtSerialization++;
                                continue;
                            }
                            WriteLine(entry, line);
                            long lineBytes = line.Length + 1L;
                            eventBytes += lineBytes;
                            payloadWritten += lineBytes;
                        }
                    }

                    Dictionary<string, object> counts = new Dictionary<string, object>(snapshot.Counts);
                    counts["serializationFailures"] = _serializationFailures;
                    counts["framesDroppedAtSerialization"] = _framesDroppedAtSerialization;
                    counts["windowsDroppedAtSerialization"] = _windowsDroppedAtSerialization;
                    counts["eventsDroppedAtSerialization"] = _eventsDroppedAtSerialization;
                    counts["checkpointCount"] = _checkpointCount;
                    counts["checkpointFrameDrops"] = _checkpointFrameDrops;
                    counts["checkpointFailures"] = _checkpointFailures;
                    Dictionary<string, object> summary = new Dictionary<string, object>
                    {
                        ["schemaVersion"] = SchemaVersion,
                        ["reason"] = snapshot.Reason,
                        ["metadata"] = snapshot.Metadata,
                        ["counts"] = counts,
                        ["limits"] = CaptureLimits(),
                        ["windowsSerialized"] = serializedWindows,
                        ["uniqueFramesSerialized"] = includedFrames.Count,
                        ["payloadBytesUncompressed"] = payloadWritten,
                        ["recoveryCheckpointAvailableAtStart"] = File.Exists(_checkpointPath)
                    };
                    byte[] summaryBytes;
                    try
                    {
                        summaryBytes = SerializeBounded(summary, Math.Min(_options.MaxSerializedRowBytes, 2 * 1024 * 1024), serializer);
                    }
                    catch
                    {
                        summaryBytes = Encoding.UTF8.GetBytes("{}" );
                        _serializationFailures++;
                    }
                    if (payloadWritten + summaryBytes.Length + 1 <= _options.MaxArchiveBytes)
                    {
                        ZipArchiveEntry summaryEntry = archive.CreateEntry("summary.json", CompressionLevel.Optimal);
                        using (Stream entry = summaryEntry.Open())
                        {
                            entry.Write(summaryBytes, 0, summaryBytes.Length);
                            entry.WriteByte((byte)'\n');
                        }
                    }
                }

                FileInfo info = new FileInfo(temporaryPath);
                if (info.Length > _options.MaxArchiveBytes)
                    throw new IOException("The report exceeded its archive size cap.");
                File.Move(temporaryPath, finalPath);
                try { if (File.Exists(_checkpointPath)) File.Delete(_checkpointPath); } catch { }
                try { ApplyRetention(_directory, _options.RetainReports, _options.RetainedBytes, finalPath); } catch { }
                return finalPath;
            }
            catch (Exception)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                // Keep the last valid `.partial.jsonl` checkpoint for manual recovery.
                throw new IOException("Could not write the raid report. The last valid recovery checkpoint remains available when one was saved.");
            }
        }

        private static int CountSerializedWindows(RecorderSnapshot snapshot, HashSet<long> includedFrames)
        {
            int count = 0;
            for (int i = 0; i < snapshot.Windows.Length; i++)
            {
                WindowSnapshot window = snapshot.Windows[i];
                for (int j = 0; j < window.FrameIds.Length; j++)
                {
                    if (includedFrames.Contains(window.FrameIds[j]))
                    {
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        private static void WriteLine(Stream stream, byte[] line)
        {
            stream.Write(line, 0, line.Length);
            stream.WriteByte((byte)'\n');
        }

        private static Dictionary<string, object> FrameRow(FrameRecord frame, string kind)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["frameId"] = frame.Id,
                ["botId"] = frame.BotId,
                ["time"] = frame.Time,
                ["stage"] = frame.Stage,
                ["snapshot"] = frame.Snapshot
            };
            if (frame.Trigger != null)
                row["trigger"] = frame.Trigger;
            if (kind != null)
                row["kind"] = kind;
            return row;
        }

        private static Dictionary<string, object> WindowRow(WindowSnapshot window, string kind)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["windowId"] = window.Id,
                ["botId"] = window.BotId,
                ["trigger"] = window.Trigger,
                ["triggerTime"] = window.TriggerTime,
                ["startTime"] = window.StartTime,
                ["endTime"] = window.EndTime,
                ["routine"] = window.Routine,
                ["complete"] = window.Complete,
                ["frameIds"] = window.FrameIds
            };
            if (kind != null)
                row["kind"] = kind;
            return row;
        }

        private static JsonSerializer CreateSerializer()
        {
            return JsonSerializer.Create(new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                MaxDepth = 40,
                Culture = CultureInfo.InvariantCulture
            });
        }

        private static byte[] SerializeBounded(object value, int maxBytes, JsonSerializer serializer = null)
        {
            using (BoundedMemoryStream memory = new BoundedMemoryStream(maxBytes))
            using (StreamWriter text = new StreamWriter(memory, new UTF8Encoding(false), 1024, true))
            using (JsonTextWriter json = new JsonTextWriter(text))
            {
                json.Formatting = Formatting.None;
                (serializer ?? CreateSerializer()).Serialize(json, value);
                json.Flush();
                text.Flush();
                return memory.ToArray();
            }
        }

        private static JToken CaptureSafeMetadata(object metadata)
        {
            if (metadata == null)
                return new JObject();
            try
            {
                JToken token = JToken.FromObject(metadata, CreateSerializer());
                SanitizeMetadata(token);
                return token;
            }
            catch
            {
                return new JObject();
            }
        }

        private static void SanitizeMetadata(JToken token)
        {
            JObject obj = token as JObject;
            if (obj != null)
            {
                List<JProperty> properties = obj.Properties().AsValueEnumerable().ToList();
                for (int i = 0; i < properties.Count; i++)
                {
                    JProperty property = properties[i];
                    if (IsPrivateOrLocalKey(property.Name))
                        property.Remove();
                    else
                        SanitizeMetadata(property.Value);
                }
                return;
            }

            JArray array = token as JArray;
            if (array != null)
            {
                for (int i = 0; i < array.Count; i++)
                    SanitizeMetadata(array[i]);
                return;
            }

            JValue value = token as JValue;
            if (value != null && value.Type == JTokenType.String)
            {
                string text = value.Value<string>();
                if (LooksLikeAbsolutePath(text))
                    value.Value = "[redacted-local-path]";
            }
        }

        private static bool IsPrivateOrLocalKey(string key)
        {
            string normalized = key.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            return normalized.Contains("nickname") || normalized.Contains("username") ||
                normalized.Contains("profileid") || normalized.Contains("accountid") ||
                normalized == "aid" || normalized.Contains("playerid") ||
                normalized.Contains("memberid") || normalized.Contains("filepath") ||
                normalized.Contains("fullpath") || normalized.Contains("directory") ||
                normalized.Contains("folderpath") || normalized.Contains("localpath") ||
                normalized == "path" || normalized == "configpath" || normalized == "logpath";
        }

        private static bool LooksLikeAbsolutePath(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (text.StartsWith("\\\\", StringComparison.Ordinal) || text.StartsWith("/", StringComparison.Ordinal))
                return true;
            return text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' &&
                (text[2] == '\\' || text[2] == '/');
        }

        private static void ReplaceFileAtomically(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
                File.Replace(temporaryPath, destinationPath, null);
            else
                File.Move(temporaryPath, destinationPath);
        }

        private static void ApplyRetention(string directory, int maxReports, long maxBytes, string keepPath)
        {
            if (!Directory.Exists(directory))
                return;

            List<FileInfo> reports = Directory.GetFiles(directory, FilePrefix + "*", SearchOption.TopDirectoryOnly)
                .AsValueEnumerable()
                .Where(file =>
                {
                    string name = Path.GetFileName(file);
                    bool managedReport = name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) &&
                        (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".partial.jsonl", StringComparison.OrdinalIgnoreCase));
                    bool isKeep = keepPath != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase);
                    return managedReport && (isKeep || !IsActiveReportPath(file));
                })
                .Select(file => new FileInfo(file))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
            long totalBytes = 0;
            for (int i = 0; i < reports.Count; i++)
                totalBytes += reports[i].Length;

            int remaining = reports.Count;
            for (int i = 0; i < reports.Count && (remaining > maxReports || totalBytes > maxBytes); i++)
            {
                FileInfo candidate = reports[i];
                if (keepPath != null && string.Equals(candidate.FullName, keepPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    long length = candidate.Length;
                    candidate.Delete();
                    totalBytes -= length;
                    remaining--;
                }
                catch { }
            }
        }

        private static void CleanupAbandonedTemporaryFiles(string directory)
        {
            Directory.GetFiles(directory, FilePrefix + "*", SearchOption.TopDirectoryOnly)
                .AsValueEnumerable()
                .Where(file =>
                {
                    string name = Path.GetFileName(file);
                    return name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) &&
                        (name.EndsWith(".zip.partial", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".partial.jsonl.tmp", StringComparison.OrdinalIgnoreCase));
                })
                .ToList()
                .ForEach(file =>
                {
                    try { File.Delete(file); } catch { }
                });
        }

        private static bool IsActiveReportPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            lock (RegistryLock)
                return ActiveReportPaths.Contains(fullPath);
        }

        private static bool IsRoutineTrigger(string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
                return false;
            return trigger.StartsWith("routine:", StringComparison.OrdinalIgnoreCase) ||
                trigger.StartsWith("baseline:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string LimitText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            string clean = value.Replace('\r', ' ').Replace('\n', ' ');
            return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength);
        }

        private sealed class BotBuffer
        {
            internal int BotId;
            internal float LastTouchedTime;
            internal float LastRecordTime;
            internal readonly List<FrameRecord> Frames = new List<FrameRecord>();
        }

        private sealed class FrameRecord
        {
            internal long Id;
            internal int BotId;
            internal float Time;
            internal string Stage;
            internal object Snapshot;
            internal string Trigger;
            internal int References;
        }

        private sealed class ReplayWindow
        {
            internal long Id;
            internal int BotId;
            internal string Trigger;
            internal float TriggerTime;
            internal float StartTime;
            internal float EndTime;
            internal bool Routine;
            internal bool Complete;
            internal readonly List<FrameRecord> Frames = new List<FrameRecord>();
        }

        private sealed class EventRecord
        {
            internal long Index;
            internal object Row;
        }

        private sealed class WindowSnapshot
        {
            internal long Id;
            internal int BotId;
            internal string Trigger;
            internal float TriggerTime;
            internal float StartTime;
            internal float EndTime;
            internal bool Routine;
            internal bool Complete;
            internal long[] FrameIds;
        }

        private sealed class RecorderSnapshot
        {
            internal JToken Metadata;
            internal string Reason;
            internal FrameRecord[] Frames;
            internal WindowSnapshot[] Windows;
            internal EventRecord[] Events;
            internal Dictionary<string, object> Counts;
        }

        private struct TriggerKey : IEquatable<TriggerKey>
        {
            private readonly int _botId;
            private readonly string _trigger;

            internal TriggerKey(int botId, string trigger)
            {
                _botId = botId;
                _trigger = trigger ?? string.Empty;
            }

            public bool Equals(TriggerKey other)
            {
                return _botId == other._botId && string.Equals(_trigger, other._trigger, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object obj)
            {
                return obj is TriggerKey && Equals((TriggerKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked { return (_botId * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(_trigger); }
            }
        }

        private sealed class BoundedMemoryStream : MemoryStream
        {
            private readonly int _maximum;

            internal BoundedMemoryStream(int maximum)
            {
                _maximum = Math.Max(128, maximum);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                EnsureCapacityFor(count);
                base.Write(buffer, offset, count);
            }

            public override void WriteByte(byte value)
            {
                EnsureCapacityFor(1);
                base.WriteByte(value);
            }

            private void EnsureCapacityFor(int count)
            {
                if (Length + count > _maximum)
                    throw new InvalidDataException("A report row exceeded its size limit.");
            }
        }

        private static class RaidReportRecorderSequence
        {
            internal static long Value;
        }
    }

    internal sealed class RaidReportRecorderOptions
    {
        internal float HistorySeconds = 2f;
        internal float TailSeconds = 3f;
        internal float TriggerCooldownSeconds = 10f;
        internal float RoutineTriggerCooldownSeconds = 60f;
        internal float CheckpointIntervalSeconds = 10f;
        internal int MaxTrackedBots = 8;
        internal int MaxFramesPerBot = 960;
        internal int MaxRetainedFrames = 12000;
        internal int MaxWindows = 24;
        internal int MaxAnomalyWindows = 18;
        internal int MaxRoutineWindows = 6;
        internal int MaxEventRows = 5000;
        internal int MaxSerializedRowBytes = 1024 * 1024;
        internal long MaxArchiveBytes = 64L * 1024L * 1024L;
        internal long MaxCheckpointBytes = 64L * 1024L * 1024L;
        internal int RetainReports = 10;
        internal long RetainedBytes = 250L * 1024L * 1024L;

        internal RaidReportRecorderOptions CloneAndClamp()
        {
            return new RaidReportRecorderOptions
            {
                HistorySeconds = Clamp(HistorySeconds, 0.1f, 10f, 2f),
                TailSeconds = Clamp(TailSeconds, 0.1f, 20f, 3f),
                TriggerCooldownSeconds = Clamp(TriggerCooldownSeconds, 0f, 60f, 10f),
                RoutineTriggerCooldownSeconds = Clamp(RoutineTriggerCooldownSeconds, 0f, 600f, 60f),
                CheckpointIntervalSeconds = Clamp(CheckpointIntervalSeconds, 1f, 120f, 10f),
                MaxTrackedBots = Clamp(MaxTrackedBots, 1, 32, 8),
                MaxFramesPerBot = Clamp(MaxFramesPerBot, 1, 4000, 960),
                MaxRetainedFrames = Clamp(MaxRetainedFrames, 1, 50000, 12000),
                MaxWindows = Clamp(MaxWindows, 1, 64, 24),
                MaxAnomalyWindows = Clamp(MaxAnomalyWindows, 1, 64, 18),
                MaxRoutineWindows = Clamp(MaxRoutineWindows, 1, 64, 6),
                MaxEventRows = Clamp(MaxEventRows, 0, 50000, 5000),
                MaxSerializedRowBytes = Clamp(MaxSerializedRowBytes, 128, 8 * 1024 * 1024, 1024 * 1024),
                MaxArchiveBytes = Clamp(MaxArchiveBytes, 1024L, 512L * 1024L * 1024L, 64L * 1024L * 1024L),
                MaxCheckpointBytes = Clamp(MaxCheckpointBytes, 1024L, 512L * 1024L * 1024L, 64L * 1024L * 1024L),
                RetainReports = Clamp(RetainReports, 1, 100, 10),
                RetainedBytes = Clamp(RetainedBytes, 1024L, 2L * 1024L * 1024L * 1024L, 250L * 1024L * 1024L)
            };
        }

        private static int Clamp(int value, int min, int max, int fallback)
        {
            return value < min || value > max ? fallback : value;
        }

        private static long Clamp(long value, long min, long max, long fallback)
        {
            return value < min || value > max ? fallback : value;
        }

        private static float Clamp(float value, float min, float max, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max ? fallback : value;
        }
    }
}
