namespace SparkEventAnalyzer.Services;

/// <summary>
/// Which managed-Spark platform a run lives on. Determines how the Spark History
/// Server base URL is built and which authentication is used.
/// </summary>
public enum SparkPlatform
{
    /// <summary>A directly-reachable Spark History Server (local dev or a plain URL).</summary>
    Generic,

    /// <summary>HDInsight cluster gateway: https://{cluster}/sparkhistory/... (Basic auth).</summary>
    HDInsight,

    /// <summary>Azure Synapse Spark pool history (Entra/AAD auth).</summary>
    Synapse,
}

/// <summary>
/// The "where" of a Spark run — the platform plus the identifiers needed to build the
/// History Server URL. Supplied per request (the customer gives the app id + cluster for
/// HDInsight, or workspace/pool/livy id for Synapse). The app id and attempt id are passed
/// separately (they come from the model/tool call), so they are not part of the target.
/// </summary>
public sealed record SparkTarget(
    SparkPlatform Platform,
    string? Cluster = null,
    string? Workspace = null,
    string? SparkPool = null,
    string? LivyId = null);

/// <summary>
/// Request-scoped holder for the current <see cref="SparkTarget"/>. The API endpoint sets it
/// from the request; <see cref="SparkHistoryClient"/> reads it to build the URL + pick auth.
/// </summary>
public interface ISparkTargetAccessor
{
    SparkTarget? Current { get; set; }
}

/// <inheritdoc />
public sealed class SparkTargetAccessor : ISparkTargetAccessor
{
    public SparkTarget? Current { get; set; }
}
