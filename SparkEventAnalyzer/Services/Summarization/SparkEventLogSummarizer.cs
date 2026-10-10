using System.Text.Json;
using SparkEventAnalyzer.Models;

namespace SparkEventAnalyzer.Services;

/// <summary>The pieces of a Spark run reconstructed from a raw event log.</summary>
public sealed record ParsedSparkRun(
    SparkApplication App,
    List<SparkJob> Jobs,
    List<SparkStage> Stages,
    List<SparkExecutor> Executors);

/// <summary>
/// Parses a raw Spark event log (one JSON event per line, as produced by the History Server
/// "/logs" endpoint) into the same <see cref="SparkApplication"/>/<see cref="SparkJob"/>/
/// <see cref="SparkStage"/>/<see cref="SparkExecutor"/> shapes the REST endpoints return. This lets
/// <see cref="SparkRunSummarizer"/> derive its findings from the full event log with the exact same
/// heuristics it uses for the REST path.
///
/// Only the events needed for failure/perf signals are consumed; everything else is ignored, so a
/// large log still reduces to a small summary.
/// </summary>
public sealed class SparkEventLogSummarizer
{
    public ParsedSparkRun Parse(string appId, string? attemptId, string eventLogText)
    {
        var appName = string.Empty;
        string? sparkUser = null;
        DateTimeOffset? startTime = null;
        DateTimeOffset? endTime = null;
        var completed = false;

        var jobs = new Dictionary<int, SparkJob>();
        var stages = new Dictionary<(int Id, int Attempt), StageAgg>();
        var executors = new Dictionary<string, ExecAgg>();

        using var reader = new StringReader(eventLogText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // tolerate a truncated final line

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("Event", out var evtEl))
                    continue;

                switch (evtEl.GetString())
                {
                    case "SparkListenerApplicationStart":
                        appName = GetString(root, "App Name") ?? appName;
                        sparkUser = GetString(root, "User") ?? sparkUser;
                        startTime = GetEpoch(root, "Timestamp") ?? startTime;
                        break;

                    case "SparkListenerApplicationEnd":
                        completed = true;
                        endTime = GetEpoch(root, "Timestamp") ?? endTime;
                        break;

                    case "SparkListenerJobStart":
                        if (GetInt(root, "Job ID") is { } startId)
                            jobs[startId] = new SparkJob { JobId = startId, Status = "RUNNING" };
                        break;

                    case "SparkListenerJobEnd":
                        if (GetInt(root, "Job ID") is { } endId)
                        {
                            var job = jobs.TryGetValue(endId, out var existing)
                                ? existing
                                : new SparkJob { JobId = endId };
                            job.Status = JobResultStatus(root);
                            jobs[endId] = job;
                        }
                        break;

                    case "SparkListenerStageCompleted":
                        HandleStageCompleted(root, stages);
                        break;

                    case "SparkListenerTaskEnd":
                        HandleTaskEnd(root, stages, executors);
                        break;

                    case "SparkListenerExecutorAdded":
                        if (GetString(root, "Executor ID") is { } addedId)
                            GetExec(executors, addedId).Active = true;
                        break;

                    case "SparkListenerExecutorRemoved":
                        if (GetString(root, "Executor ID") is { } removedId)
                        {
                            var ex = GetExec(executors, removedId);
                            ex.Active = false;
                            ex.RemovedReason = GetString(root, "Removed Reason");
                        }
                        break;
                }
            }
        }

        var app = new SparkApplication
        {
            Id = appId,
            Name = appName,
            Attempts =
            {
                new SparkApplicationAttempt
                {
                    AttemptId = attemptId,
                    SparkUser = sparkUser,
                    Completed = completed,
                    StartTime = startTime,
                    EndTime = endTime,
                },
            },
        };

        var stageList = stages.Values.Select(s => s.ToStage()).ToList();
        var execList = executors.Values.Select(e => e.ToExecutor()).ToList();

        return new ParsedSparkRun(app, jobs.Values.ToList(), stageList, execList);
    }

    private static void HandleStageCompleted(JsonElement root, Dictionary<(int, int), StageAgg> stages)
    {
        if (!root.TryGetProperty("Stage Info", out var info) || info.ValueKind != JsonValueKind.Object)
            return;

        var stageId = GetInt(info, "Stage ID") ?? -1;
        var attempt = GetInt(info, "Stage Attempt ID") ?? 0;
        if (stageId < 0)
            return;

        var agg = GetStage(stages, stageId, attempt);
        agg.Name = GetString(info, "Stage Name") ?? agg.Name;
        agg.NumTasksReported = GetInt(info, "Number of Tasks") ?? agg.NumTasksReported;

        var failure = GetString(info, "Failure Reason");
        if (!string.IsNullOrWhiteSpace(failure))
            agg.FailureReason = failure;
    }

    private static void HandleTaskEnd(
        JsonElement root,
        Dictionary<(int, int), StageAgg> stages,
        Dictionary<string, ExecAgg> executors)
    {
        var stageId = GetInt(root, "Stage ID") ?? -1;
        var attempt = GetInt(root, "Stage Attempt ID") ?? 0;
        if (stageId < 0)
            return;

        var agg = GetStage(stages, stageId, attempt);
        agg.TaskCount++;

        var failed = false;
        if (root.TryGetProperty("Task End Reason", out var reason) &&
            reason.ValueKind == JsonValueKind.Object)
        {
            var reasonText = GetString(reason, "Reason");
            failed = !string.Equals(reasonText, "Success", StringComparison.OrdinalIgnoreCase);
        }
        if (failed)
            agg.FailedTasks++;

        string? executorId = null;
        if (root.TryGetProperty("Task Info", out var taskInfo) && taskInfo.ValueKind == JsonValueKind.Object)
            executorId = GetString(taskInfo, "Executor ID");

        if (!root.TryGetProperty("Task Metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
        {
            if (executorId is not null)
            {
                var e = GetExec(executors, executorId);
                if (failed) e.FailedTasks++;
            }
            return;
        }

        var runTime = GetLong(metrics, "Executor Run Time") ?? 0;
        var gcTime = GetLong(metrics, "JVM GC Time") ?? 0;
        agg.ExecutorRunTime += runTime;
        agg.MemoryBytesSpilled += GetLong(metrics, "Memory Bytes Spilled") ?? 0;
        agg.DiskBytesSpilled += GetLong(metrics, "Disk Bytes Spilled") ?? 0;

        if (metrics.TryGetProperty("Shuffle Read Metrics", out var read) && read.ValueKind == JsonValueKind.Object)
            agg.ShuffleReadBytes += (GetLong(read, "Remote Bytes Read") ?? 0) + (GetLong(read, "Local Bytes Read") ?? 0);

        if (metrics.TryGetProperty("Shuffle Write Metrics", out var write) && write.ValueKind == JsonValueKind.Object)
            agg.ShuffleWriteBytes += GetLong(write, "Shuffle Bytes Written") ?? 0;

        if (executorId is not null)
        {
            var e = GetExec(executors, executorId);
            e.TotalDuration += runTime;
            e.TotalGCTime += gcTime;
            if (failed) e.FailedTasks++;
        }
    }

    private static string JobResultStatus(JsonElement root)
    {
        if (root.TryGetProperty("Job Result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            var text = GetString(result, "Result");
            if (string.Equals(text, "JobSucceeded", StringComparison.OrdinalIgnoreCase))
                return "SUCCEEDED";
            if (!string.IsNullOrWhiteSpace(text))
                return "FAILED";
        }
        return "UNKNOWN";
    }

    private static StageAgg GetStage(Dictionary<(int, int), StageAgg> stages, int id, int attempt)
    {
        var key = (id, attempt);
        if (!stages.TryGetValue(key, out var agg))
        {
            agg = new StageAgg { StageId = id, AttemptId = attempt };
            stages[key] = agg;
        }
        return agg;
    }

    private static ExecAgg GetExec(Dictionary<string, ExecAgg> executors, string id)
    {
        if (!executors.TryGetValue(id, out var agg))
        {
            agg = new ExecAgg { Id = id, Active = true };
            executors[id] = agg;
        }
        return agg;
    }

    // --- JSON helpers (Spark event logs use spaced PascalCase field names) ---

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static DateTimeOffset? GetEpoch(JsonElement el, string name)
        => GetLong(el, name) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private sealed class StageAgg
    {
        public int StageId;
        public int AttemptId;
        public string Name = string.Empty;
        public int NumTasksReported;
        public int TaskCount;
        public int FailedTasks;
        public long ExecutorRunTime;
        public long ShuffleReadBytes;
        public long ShuffleWriteBytes;
        public long MemoryBytesSpilled;
        public long DiskBytesSpilled;
        public string? FailureReason;

        public SparkStage ToStage() => new()
        {
            StageId = StageId,
            AttemptId = AttemptId,
            Name = Name,
            Status = string.IsNullOrWhiteSpace(FailureReason) ? "COMPLETE" : "FAILED",
            NumTasks = NumTasksReported > 0 ? NumTasksReported : TaskCount,
            NumFailedTasks = FailedTasks,
            NumCompleteTasks = Math.Max(0, TaskCount - FailedTasks),
            ExecutorRunTime = ExecutorRunTime,
            ShuffleReadBytes = ShuffleReadBytes,
            ShuffleWriteBytes = ShuffleWriteBytes,
            MemoryBytesSpilled = MemoryBytesSpilled,
            DiskBytesSpilled = DiskBytesSpilled,
            FailureReason = FailureReason,
        };
    }

    private sealed class ExecAgg
    {
        public string Id = string.Empty;
        public bool Active;
        public int FailedTasks;
        public long TotalDuration;
        public long TotalGCTime;
        public string? RemovedReason;

        public SparkExecutor ToExecutor() => new()
        {
            Id = Id,
            IsActive = Active,
            FailedTasks = FailedTasks,
            TotalDuration = TotalDuration,
            TotalGCTime = TotalGCTime,
        };
    }
}
