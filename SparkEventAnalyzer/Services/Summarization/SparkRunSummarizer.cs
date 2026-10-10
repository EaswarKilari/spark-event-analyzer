using Microsoft.Extensions.Options;
using SparkEventAnalyzer.Models;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Builds a compact <see cref="SparkRunSummary"/> from Spark run data. For HDInsight it prefers the
/// raw event log (the History Server "/logs" zip) as the primary source and derives findings from
/// it, falling back to the jobs/stages/executors REST endpoints when the event log is unavailable.
/// It extracts only the signals that matter (failures, hot stages, skew, spill, GC) so the result
/// is small enough to hand to an LLM.
/// </summary>
public sealed class SparkRunSummarizer
{
    private readonly ISparkHistoryClient _client;
    private readonly ISparkTargetAccessor _target;
    private readonly SparkHistoryOptions _options;
    private readonly SparkEventLogSummarizer _eventLog;
    private readonly ILogger<SparkRunSummarizer> _logger;

    // Heuristic thresholds (tunable).
    private const double GcRatioThreshold = 0.10;          // >10% time in GC is suspicious
    private const long SpillThresholdBytes = 1L * 1024 * 1024; // >1 MB spilled flags a stage
    private const int TopStages = 5;

    public SparkRunSummarizer(
        ISparkHistoryClient client,
        ISparkTargetAccessor target,
        IOptions<SparkHistoryOptions> options,
        SparkEventLogSummarizer eventLog,
        ILogger<SparkRunSummarizer> logger)
    {
        _client = client;
        _target = target;
        _options = options.Value;
        _eventLog = eventLog;
        _logger = logger;
    }

    public async Task<SparkRunSummary> SummarizeAsync(string appId, string? attemptId = null, CancellationToken ct = default)
    {
        var platform = _target.Current?.Platform ?? _options.DefaultPlatform;

        // Primary path for HDInsight: derive everything from the full event log.
        if (platform == SparkPlatform.HDInsight && _options.HDInsight.UseEventLog)
        {
            try
            {
                var text = await _client.DownloadEventLogTextAsync(appId, attemptId, ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var parsed = _eventLog.Parse(appId, attemptId, text!);
                    _logger.LogInformation(
                        "Built Spark run summary from event log for {AppId}: {Jobs} job(s), {Stages} stage(s), {Executors} executor(s).",
                        appId, parsed.Jobs.Count, parsed.Stages.Count, parsed.Executors.Count);
                    return Build(parsed.App, parsed.Jobs, parsed.Stages, parsed.Executors, appId, attemptId);
                }

                _logger.LogWarning("Event log empty for {AppId}; falling back to REST endpoints.", appId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event-log summarization failed for {AppId}; falling back to REST endpoints.", appId);
            }
        }

        // Fallback path: jobs/stages/executors REST endpoints (also the path for Synapse/Generic).
        var app = await _client.GetApplicationAsync(appId, ct);
        var jobs = await _client.GetJobsAsync(appId, attemptId, ct);
        var stages = await _client.GetStagesAsync(appId, attemptId, ct);
        var executors = await _client.GetExecutorsAsync(appId, attemptId, ct);
        return Build(app, jobs, stages, executors, appId, attemptId);
    }

    /// <summary>
    /// Reduces raw run data (from either the event log or the REST endpoints) to the compact
    /// <see cref="SparkRunSummary"/> using the shared failure/perf heuristics.
    /// </summary>
    internal static SparkRunSummary Build(
        SparkApplication? app,
        IReadOnlyList<SparkJob> jobs,
        IReadOnlyList<SparkStage> stages,
        IReadOnlyList<SparkExecutor> executors,
        string appId,
        string? attemptId)
    {
        var attempt = app?.Attempts.FirstOrDefault(a =>
            attemptId is null || string.Equals(a.AttemptId, attemptId, StringComparison.OrdinalIgnoreCase));

        var summary = new SparkRunSummary
        {
            ApplicationId = appId,
            ApplicationName = app?.Name ?? string.Empty,
            AttemptId = attempt?.AttemptId ?? attemptId,
            SparkUser = attempt?.SparkUser,
            Completed = attempt?.Completed ?? false,
            TotalJobs = jobs.Count,
            FailedJobs = jobs.Count(j => j.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)),
            TotalStages = stages.Count,
            FailedStages = stages.Count(s => s.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)),
            TotalFailedTasks = stages.Sum(s => s.NumFailedTasks),
        };

        summary.FailedStageDetails = stages
            .Where(s => s.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) || s.NumFailedTasks > 0)
            .OrderByDescending(s => s.NumFailedTasks)
            .Select(ToFinding)
            .ToList();

        summary.HotStages = stages
            .OrderByDescending(s => s.ExecutorRunTime)
            .Take(TopStages)
            .Select(ToFinding)
            .ToList();

        summary.SpillSuspects = stages
            .Where(s => s.MemoryBytesSpilled > SpillThresholdBytes || s.DiskBytesSpilled > SpillThresholdBytes)
            .OrderByDescending(s => s.MemoryBytesSpilled + s.DiskBytesSpilled)
            .Select(ToFinding)
            .ToList();

        // Skew heuristic: a stage whose executor run time is large relative to its
        // task count (few tasks dominating wall time) OR heavy shuffle read.
        summary.SkewSuspects = stages
            .Where(s => s.NumTasks > 0)
            .Where(s => s.ShuffleReadBytes > 0 &&
                        s.ExecutorRunTime / Math.Max(1, s.NumTasks) > 60_000) // >60s avg/task
            .OrderByDescending(s => s.ShuffleReadBytes)
            .Take(TopStages)
            .Select(ToFinding)
            .ToList();

        summary.ExecutorIssues = executors
            .Select(e =>
            {
                var ratio = e.TotalDuration > 0 ? (double)e.TotalGCTime / e.TotalDuration : 0d;
                return new ExecutorFinding
                {
                    Id = e.Id,
                    IsActive = e.IsActive,
                    FailedTasks = e.FailedTasks,
                    TotalGCTimeMs = e.TotalGCTime,
                    TotalDurationMs = e.TotalDuration,
                    GcRatio = Math.Round(ratio, 3),
                };
            })
            .Where(e => e.FailedTasks > 0 || e.GcRatio > GcRatioThreshold || !e.IsActive)
            .OrderByDescending(e => e.GcRatio)
            .ToList();

        BuildSignals(summary);
        return summary;
    }

    private static StageFinding ToFinding(SparkStage s) => new()
    {
        StageId = s.StageId,
        AttemptId = s.AttemptId,
        Name = s.Name,
        Status = s.Status,
        NumTasks = s.NumTasks,
        NumFailedTasks = s.NumFailedTasks,
        ExecutorRunTimeMs = s.ExecutorRunTime,
        ShuffleReadBytes = s.ShuffleReadBytes,
        ShuffleWriteBytes = s.ShuffleWriteBytes,
        MemoryBytesSpilled = s.MemoryBytesSpilled,
        DiskBytesSpilled = s.DiskBytesSpilled,
        FailureReason = string.IsNullOrWhiteSpace(s.FailureReason) ? s.Details : s.FailureReason,
    };

    private static void BuildSignals(SparkRunSummary s)
    {
        if (s.FailedJobs > 0)
            s.Signals.Add($"{s.FailedJobs} job(s) FAILED.");
        if (s.FailedStages > 0)
            s.Signals.Add($"{s.FailedStages} stage(s) FAILED, {s.TotalFailedTasks} failed task(s) total.");
        if (s.SpillSuspects.Count > 0)
            s.Signals.Add($"{s.SpillSuspects.Count} stage(s) spilled to memory/disk — possible memory pressure.");
        if (s.SkewSuspects.Count > 0)
            s.Signals.Add($"{s.SkewSuspects.Count} stage(s) show data-skew characteristics (high avg task time + shuffle).");
        var gc = s.ExecutorIssues.Count(e => e.GcRatio > GcRatioThreshold);
        if (gc > 0)
            s.Signals.Add($"{gc} executor(s) spent >10% time in GC — possible memory pressure.");
        var lost = s.ExecutorIssues.Count(e => !e.IsActive);
        if (lost > 0)
            s.Signals.Add($"{lost} executor(s) no longer active — possible executor loss/OOM.");
        if (s.Signals.Count == 0)
            s.Signals.Add("No obvious failure/perf signals detected from summary-level metrics.");
    }
}
