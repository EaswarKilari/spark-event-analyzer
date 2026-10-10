namespace SparkEventAnalyzer.Services;

/// <summary>
/// Configuration for the Spark History Server REST client. The client resolves a base URL and
/// authentication per <see cref="SparkPlatform"/> using the per-request <see cref="SparkTarget"/>,
/// then appends the standard Spark History "/api/v1/..." paths.
///
/// Endpoint shapes are templated so they can be adjusted via config without a code change.
/// </summary>
public sealed class SparkHistoryOptions
{
    public const string SectionName = "SparkHistory";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Platform used when a request does not specify one (e.g. local dev).</summary>
    public SparkPlatform DefaultPlatform { get; set; } = SparkPlatform.Generic;

    public HdInsightHistoryOptions HDInsight { get; set; } = new();

    public SynapseHistoryOptions Synapse { get; set; } = new();

    public GenericHistoryOptions Generic { get; set; } = new();
}

/// <summary>HDInsight gateway (Basic auth with cluster credentials).</summary>
public sealed class HdInsightHistoryOptions
{
    /// <summary>
    /// Base URL template. Placeholder: {cluster} = the cluster gateway FQDN, e.g.
    /// "my-cluster.azurehdinsight.net". The client appends
    /// "/api/v1/applications/{appId}/{attempt}/...".
    /// </summary>
    public string BaseUrlTemplate { get; set; } = "https://{cluster}/sparkhistory";

    /// <summary>
    /// Cluster gateway user (Basic auth). Used as a fallback when Key Vault is not configured or
    /// does not contain an entry for the requested cluster. Prefer <see cref="KeyVault"/>.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Cluster gateway password (Basic auth). Fallback for <see cref="KeyVault"/>. Provide via a
    /// SparkHistory__HDInsight__Password secret if used.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Preferred credential source: a Key Vault secret holding a JSON map of cluster gateway FQDN
    /// to "username:password" (e.g. { "my-cluster.azurehdinsight.net": "ambariuser:pass" }).
    /// </summary>
    public HdInsightKeyVaultOptions KeyVault { get; set; } = new();

    /// <summary>
    /// When true, the summarizer fetches the raw Spark event log (the History Server "/logs" zip)
    /// as the primary data source and derives the run summary from it, falling back to the
    /// jobs/stages/executors REST endpoints if the event log is unavailable.
    /// </summary>
    public bool UseEventLog { get; set; } = true;

    /// <summary>
    /// Safety cap on how much of the (uncompressed) event log to read into memory. Large apps can
    /// produce very big logs; reading stops at the last complete line before this many characters.
    /// </summary>
    public long EventLogMaxBytes { get; set; } = 26_214_400; // 25 MB
}

/// <summary>Key Vault location of the HDInsight cluster credentials map.</summary>
public sealed class HdInsightKeyVaultOptions
{
    /// <summary>Key Vault URI, e.g. "https://myvault.vault.azure.net/". Empty = disabled.</summary>
    public string? VaultUri { get; set; }

    /// <summary>
    /// Name of the secret whose value is a JSON object mapping cluster gateway FQDN to
    /// "username:password".
    /// </summary>
    public string? SecretName { get; set; }

    /// <summary>How long to cache the fetched credentials map before re-reading from Key Vault.</summary>
    public int CacheMinutes { get; set; } = 10;
}

/// <summary>Azure Synapse Spark history (Entra/AAD auth).</summary>
public sealed class SynapseHistoryOptions
{
    /// <summary>
    /// Base URL template. Placeholders: {workspace}, {sparkPool}, {livyId}. The client appends
    /// "/api/v1/applications/{appId}/{attempt}/...".
    ///
    /// NOTE: the exact Synapse Spark-history REST base differs from the ms.web.azuresynapse.net
    /// *UI* URL. This default is a best-effort starting point — adjust it in config to match the
    /// REST endpoint that returns JSON for your workspace.
    /// </summary>
    public string BaseUrlTemplate { get; set; } =
        "https://{workspace}.dev.azuresynapse.net/sparkhistory/{sparkPool}/livyid/{livyId}";

    /// <summary>Entra scope for the bearer token used against Synapse.</summary>
    public string Scope { get; set; } = "https://dev.azuresynapse.net/.default";
}

/// <summary>A directly-reachable Spark History Server (local dev or a plain URL).</summary>
public sealed class GenericHistoryOptions
{
    /// <summary>Full base URL. The client appends "/api/v1/...".</summary>
    public string BaseUrl { get; set; } = "http://localhost:18080";

    /// <summary>Optional Entra scope for a bearer token. Empty = no auth.</summary>
    public string? AuthScope { get; set; }
}
