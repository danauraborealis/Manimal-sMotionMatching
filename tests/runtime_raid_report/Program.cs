using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using Manimal.MotionMatching;

internal static class Program
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "raid-report-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WindowFramesStayWithTheirBotAndFinishIsIdempotent(Path.Combine(root, "isolation"));
            CapsRetainAggregatesAndReplaceOldWindows(Path.Combine(root, "caps"));
            GlobalFrameObjectCapIsHard(Path.Combine(root, "global-cap"));
            ArchiveBudgetPreservesEventsAndSummary(Path.Combine(root, "bytes"));
            CheckpointIsValidJsonLinesAndSurvivesFinalizationFailure(Path.Combine(root, "recovery"));
            RetentionTouchesOnlyRecentReportFiles(Path.Combine(root, "retention"));
            Console.WriteLine("runtime_raid_report: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void WindowFramesStayWithTheirBotAndFinishIsIdempotent(string directory)
    {
        RaidReportRecorder recorder = new RaidReportRecorder(directory, new Dictionary<string, object>
        {
            ["gameVersion"] = "test-game",
            ["map"] = "Interchange",
            ["profileId"] = "must-not-ship",
            ["pluginPath"] = "C:\\Users\\tester\\private.dll",
            ["nested"] = new Dictionary<string, object> { ["username"] = "private-name", ["setting"] = 4 }
        });

        for (int sample = 0; sample <= 62; sample++)
        {
            float time = sample * 0.1f;
            string firstTrigger = sample == 30 ? "foot_snap" : null;
            string secondTrigger = sample == 31 ? "knee_clearance" : null;
            recorder.Record(101, time, "after-placement", new Dictionary<string, object>
            {
                ["root"] = new[] { time, 0f, 0f },
                ["stageValue"] = "first"
            }, firstTrigger);
            recorder.Record(202, time, "after-placement", new Dictionary<string, object>
            {
                ["root"] = new[] { -time, 0f, 0f },
                ["stageValue"] = "second"
            }, secondTrigger);
        }

        recorder.Count("hit.request", 100);
        recorder.Count("hit.played", 17);
        recorder.Event(new Dictionary<string, object> { ["kind"] = "sample-event", ["count"] = 3 });

        var firstFinish = recorder.Finish("raid-ended");
        var secondFinish = recorder.Finish("ignored-second-reason");
        Check(object.ReferenceEquals(firstFinish, secondFinish), "Finish must return the same task when called more than once.");
        string archivePath = firstFinish.GetAwaiter().GetResult();
        Check(File.Exists(archivePath), "Finish did not create the report ZIP.");

        using (ZipArchive archive = ZipFile.OpenRead(archivePath))
        {
            JsonDocument summary = ReadJson(archive, "summary.json");
            JsonElement metadata = summary.RootElement.GetProperty("metadata");
            string metadataJson = metadata.GetRawText();
            Check(!metadataJson.Contains("must-not-ship", StringComparison.Ordinal), "Metadata retained a profile identifier.");
            Check(!metadataJson.Contains("private-name", StringComparison.Ordinal), "Metadata retained a username.");
            Check(!metadataJson.Contains("C:\\\\Users", StringComparison.Ordinal), "Metadata retained a local path.");
            Check(metadata.GetProperty("map").GetString() == "Interchange", "Safe metadata was removed.");
            Check(summary.RootElement.GetProperty("reason").GetString() == "raid-ended", "Finish reason was not recorded.");
            Check(Math.Abs(summary.RootElement.GetProperty("counts").GetProperty("actualDurationSeconds").GetSingle() - 6.2f) < 0.01f,
                "Actual capture duration was incorrect.");
            Check(summary.RootElement.GetProperty("counts").GetProperty("eventTotals").GetProperty("hit.request").GetInt64() == 100,
                "Aggregated events were not preserved.");

            Dictionary<long, (int botId, float time)> frames = ReadFrames(archive);
            List<JsonElement> windows = ReadRows(archive, "windows.jsonl");
            Check(windows.Count == 2, "Expected one anomaly window per triggering bot.");
            int seenFirst = 0;
            int seenSecond = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                JsonElement window = windows[i];
                int botId = window.GetProperty("botId").GetInt32();
                string trigger = window.GetProperty("trigger").GetString();
                float triggerTime = window.GetProperty("triggerTime").GetSingle();
                Check(window.GetProperty("complete").GetBoolean(), "Expected completed replay window.");
                float minimum = float.PositiveInfinity;
                float maximum = float.NegativeInfinity;
                JsonElement frameIds = window.GetProperty("frameIds");
                for (int j = 0; j < frameIds.GetArrayLength(); j++)
                {
                    long frameId = frameIds[j].GetInt64();
                    Check(frames.TryGetValue(frameId, out var frame), "Replay window references a missing frame.");
                    Check(frame.botId == botId, "Replay window mixed frames from different bots.");
                    minimum = Math.Min(minimum, frame.time);
                    maximum = Math.Max(maximum, frame.time);
                }
                Check(minimum <= triggerTime - 1.9f, "Replay did not preserve the two-second pre-trigger ring.");
                Check(maximum >= triggerTime + 2.8f, "Replay did not preserve the three-second post-trigger tail.");
                if (botId == 101 && trigger == "foot_snap") seenFirst++;
                if (botId == 202 && trigger == "knee_clearance") seenSecond++;
            }
            Check(seenFirst == 1 && seenSecond == 1, "Replay windows were not isolated by bot and trigger.");
            Check(ReadRows(archive, "events.jsonl").Count == 1, "Detailed event rows were not written.");
        }
    }

    private static void CapsRetainAggregatesAndReplaceOldWindows(string directory)
    {
        RaidReportRecorderOptions options = new RaidReportRecorderOptions
        {
            MaxTrackedBots = 1,
            MaxFramesPerBot = 3,
            MaxRetainedFrames = 64,
            MaxWindows = 2,
            MaxAnomalyWindows = 1,
            MaxRoutineWindows = 1,
            MaxEventRows = 2,
            TriggerCooldownSeconds = 0,
            RoutineTriggerCooldownSeconds = 0,
            CheckpointIntervalSeconds = 120,
            MaxArchiveBytes = 1024 * 1024,
            MaxCheckpointBytes = 1024 * 1024
        };
        RaidReportRecorder recorder = new RaidReportRecorder(directory, new { gameVersion = "test" }, options);
        recorder.Count("hit.played", 250);
        for (int i = 0; i < 9; i++)
        {
            float time = i * 0.2f;
            recorder.Record(10, time, "motion", new { x = i }, i == 0 ? "foot_snap" : null);
        }
        recorder.Record(10, 4f, "motion", new { x = 20 }, "foot_snap");
        recorder.Record(10, 4.1f, "motion", new { x = 21 }, "routine:walk");
        recorder.Record(10, 7.2f, "motion", new { x = 22 });
        recorder.Record(10, 7.3f, "motion", new { x = 23 }, "knee_clip");
        recorder.Record(10, 7.4f, "motion", new { x = 24 }, "routine:walk");
        recorder.Event(new { kind = "one" });
        recorder.Event(new { kind = "two" });
        recorder.Event(new { kind = "three" });
        recorder.Record(20, 7.5f, "motion", new { x = 25 });
        for (int i = 1; i <= 30; i++)
            recorder.Record(20, 7.5f + i * 0.2f, "motion", new { x = 25 + i });

        string path = recorder.Finish("cap-test").GetAwaiter().GetResult();
        using (ZipArchive archive = ZipFile.OpenRead(path))
        {
            JsonElement counts = ReadJson(archive, "summary.json").RootElement.GetProperty("counts");
            Check(counts.GetProperty("perBotFrameLimitDrops").GetInt64() > 0, "Per-bot frame cap did not drop old ring records.");
            Check(counts.GetProperty("botBufferEvictions").GetInt64() == 1, "Tracked bot turnover did not evict the least-recent bot ring.");
            Check(counts.GetProperty("windowsReplacedByNewer").GetInt64() >= 2, "Late replay windows did not replace completed windows of the same class.");
            Check(counts.GetProperty("eventBufferDrops").GetInt64() == 1, "Detailed event cap did not report a dropped event.");
            Check(counts.GetProperty("eventTotals").GetProperty("hit.played").GetInt64() == 250,
                "Aggregated event totals should outlive the detailed row cap.");
            Check(counts.GetProperty("peakRetainedFrameObjects").GetInt32() <= 64, "Global retained frame-object cap was exceeded.");
            Check(ReadRows(archive, "windows.jsonl").Count <= 2, "Window count exceeded its hard cap.");
        }
    }

    private static void GlobalFrameObjectCapIsHard(string directory)
    {
        RaidReportRecorderOptions options = new RaidReportRecorderOptions
        {
            MaxFramesPerBot = 100,
            MaxRetainedFrames = 4,
            MaxAnomalyWindows = 2,
            MaxRoutineWindows = 1,
            MaxWindows = 3,
            TriggerCooldownSeconds = 0,
            CheckpointIntervalSeconds = 120
        };
        RaidReportRecorder recorder = new RaidReportRecorder(directory, new { version = "test" }, options);
        recorder.Record(1, 0f, "pose", new { x = 0 }, "active-window");
        for (int i = 1; i <= 12; i++)
            recorder.Record(1, i * 0.1f, "pose", new { x = i });

        string path = recorder.Finish("global-cap-test").GetAwaiter().GetResult();
        using (ZipArchive archive = ZipFile.OpenRead(path))
        {
            JsonElement counts = ReadJson(archive, "summary.json").RootElement.GetProperty("counts");
            Check(counts.GetProperty("peakRetainedFrameObjects").GetInt32() <= 4, "The global frame-object cap was exceeded.");
            Check(counts.GetProperty("globalFrameLimitDrops").GetInt64() > 0, "The saturated active window did not report dropped frames.");
        }
    }

    private static void ArchiveBudgetPreservesEventsAndSummary(string directory)
    {
        RaidReportRecorderOptions options = new RaidReportRecorderOptions
        {
            MaxTrackedBots = 1,
            MaxFramesPerBot = 100,
            MaxRetainedFrames = 100,
            MaxWindows = 2,
            MaxAnomalyWindows = 2,
            MaxRoutineWindows = 1,
            MaxEventRows = 100,
            TriggerCooldownSeconds = 0,
            MaxArchiveBytes = 256 * 1024,
            MaxCheckpointBytes = 256 * 1024,
            MaxSerializedRowBytes = 40 * 1024,
            CheckpointIntervalSeconds = 120
        };
        RaidReportRecorder recorder = new RaidReportRecorder(directory, new { version = "test" }, options);
        for (int i = 0; i < 12; i++)
            recorder.Record(7, i * 0.1f, "full-pose", new { payload = new string('x', 30000) }, i == 0 ? "foot_snap" : null);
        for (int i = 0; i < 20; i++)
            recorder.Event(new { kind = "reaction-event", ordinal = i, detail = new string('e', 600) });
        recorder.Count("reaction.played", 20);

        string path = recorder.Finish("byte-budget-test").GetAwaiter().GetResult();
        Check(new FileInfo(path).Length <= options.MaxArchiveBytes, "ZIP exceeded the configured byte cap.");
        using (ZipArchive archive = ZipFile.OpenRead(path))
        {
            JsonElement counts = ReadJson(archive, "summary.json").RootElement.GetProperty("counts");
            Check(counts.GetProperty("framesDroppedAtSerialization").GetInt64() > 0, "Frame payload did not respect its byte reservation.");
            Check(ReadRows(archive, "events.jsonl").Count > 0, "Frame payload crowded out all detailed event rows.");
            Check(counts.GetProperty("eventTotals").GetProperty("reaction.played").GetInt64() == 20,
                "Small aggregates were lost under the byte cap.");
            Check(ReadJson(archive, "summary.json").RootElement.GetProperty("schemaVersion").GetInt32() == 1,
                "The report summary was omitted under the byte cap.");
        }
    }

    private static void CheckpointIsValidJsonLinesAndSurvivesFinalizationFailure(string directory)
    {
        RaidReportRecorderOptions options = new RaidReportRecorderOptions
        {
            CheckpointIntervalSeconds = 1,
            MaxArchiveBytes = 1024 * 1024,
            MaxCheckpointBytes = 1024 * 1024
        };
        RaidReportRecorder recorder = new RaidReportRecorder(directory, new { gameVersion = "test" }, options);
        recorder.Record(5, 0f, "before", new { x = 0 });
        recorder.Record(5, 1f, "after", new { x = 1 });
        WaitForFile(recorder.CheckpointPath);

        string[] checkpointLines = File.ReadAllLines(recorder.CheckpointPath, Encoding.UTF8);
        Check(checkpointLines.Length >= 4, "Checkpoint should contain multiple recovery rows.");
        string lastKind = null;
        for (int i = 0; i < checkpointLines.Length; i++)
        {
            using (JsonDocument row = JsonDocument.Parse(checkpointLines[i]))
                lastKind = row.RootElement.GetProperty("kind").GetString();
        }
        Check(lastKind == "checkpointSummary", "Checkpoint rows were not complete JSONL objects.");

        string stem = Path.GetFileName(recorder.CheckpointPath).Replace(".partial.jsonl", "", StringComparison.Ordinal);
        Directory.CreateDirectory(Path.Combine(directory, stem + ".zip"));
        bool failed = false;
        try
        {
            recorder.Finish("forced-finalization-failure").GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            failed = true;
        }
        Check(failed, "Finalization failure was not reported to the caller.");
        Check(File.Exists(recorder.CheckpointPath), "The valid recovery checkpoint was deleted after ZIP failure.");
    }

    private static void RetentionTouchesOnlyRecentReportFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        DateTime baseTime = DateTime.UtcNow.AddDays(-2);
        for (int i = 0; i < 11; i++)
        {
            string report = Path.Combine(directory, "report-old-" + i.ToString("D2") + ".zip");
            File.WriteAllText(report, "test");
            File.SetLastWriteTimeUtc(report, baseTime.AddMinutes(i));
        }
        string unrelated = Path.Combine(directory, "player-notes.zip");
        File.WriteAllText(unrelated, "keep");
        string abandonedZip = Path.Combine(directory, "report-crashed.zip.partial");
        string abandonedCheckpoint = Path.Combine(directory, "report-crashed.partial.jsonl.tmp");
        File.WriteAllText(abandonedZip, "partial");
        File.WriteAllText(abandonedCheckpoint, "partial");

        RaidReportRecorder recorder = new RaidReportRecorder(directory, new { version = "test" });
        Check(!File.Exists(abandonedZip) && !File.Exists(abandonedCheckpoint), "Startup cleanup did not remove abandoned report temporaries.");
        string output = recorder.Finish("retention-test").GetAwaiter().GetResult();
        Check(File.Exists(output), "Retention setup report was not created.");
        int count = Directory.GetFiles(directory, "report-*.zip", SearchOption.TopDirectoryOnly).Length;
        Check(count <= 10, "Retention did not enforce the report-count limit.");
        Check(File.Exists(unrelated), "Retention modified a non-report file.");
    }

    private static Dictionary<long, (int botId, float time)> ReadFrames(ZipArchive archive)
    {
        Dictionary<long, (int botId, float time)> frames = new Dictionary<long, (int botId, float time)>();
        List<JsonElement> rows = ReadRows(archive, "frames.jsonl");
        for (int i = 0; i < rows.Count; i++)
        {
            JsonElement row = rows[i];
            long id = row.GetProperty("frameId").GetInt64();
            frames.Add(id, (row.GetProperty("botId").GetInt32(), row.GetProperty("time").GetSingle()));
        }
        return frames;
    }

    private static List<JsonElement> ReadRows(ZipArchive archive, string entryName)
    {
        List<JsonElement> rows = new List<JsonElement>();
        ZipArchiveEntry entry = archive.GetEntry(entryName);
        Check(entry != null, "Report is missing " + entryName + ".");
        using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                    continue;
                using (JsonDocument document = JsonDocument.Parse(line))
                    rows.Add(document.RootElement.Clone());
            }
        }
        return rows;
    }

    private static JsonDocument ReadJson(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName);
        Check(entry != null, "Report is missing " + entryName + ".");
        using (Stream stream = entry.Open())
            return JsonDocument.Parse(stream);
    }

    private static void WaitForFile(string path)
    {
        for (int i = 0; i < 300; i++)
        {
            if (File.Exists(path))
                return;
            Thread.Sleep(10);
        }
        throw new InvalidOperationException("Checkpoint was not written in time.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
