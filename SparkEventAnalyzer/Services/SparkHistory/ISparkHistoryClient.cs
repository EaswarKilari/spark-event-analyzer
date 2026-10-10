using SparkEventAnalyzer.Models;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Reads Spark run data from a Spark History Server REST API (v1).
/// Works with HDInsight (cluster gateway) and Synapse (workspace) endpoints,
/// as well as a local Spark History Server for development.
/// </summary>
public interface ISparkHistoryClient
{
    Task<IReadOnlyList<SparkApplication>> ListApplicationsAsync(CancellationToken ct = default);

    Task<SparkApplication?> GetApplicationAsync(string appId, CancellationToken ct = default);

    Task<IReadOnlyList<SparkJob>> GetJobsAsync(string appId, string? attemptId = null, CancellationToken ct = default);

    Task<IReadOnlyList<SparkStage>> GetStagesAsync(string appId, string? attemptId = null, CancellationToken ct = default);

    Task<IReadOnlyList<SparkExecutor>> GetExecutorsAsync(string appId, string? attemptId = null, CancellationToken ct = default);

    /// <summary>
    /// Downloads the raw Spark event log (the History Server "/logs" zip), unzips it, and returns
    /// the largest event-log entry as text (one JSON event per line). Returns null when the log is
    /// unavailable/empty. Forces HTTP/1.1 (the HDInsight gateway hangs on HTTP/2 for this endpoint).
    /// </summary>
    Task<string?> DownloadEventLogTextAsync(string appId, string? attemptId = null, CancellationToken ct = default);
}
