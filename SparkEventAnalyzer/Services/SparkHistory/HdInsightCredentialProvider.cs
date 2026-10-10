using System.Text.Json;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Options;

namespace SparkEventAnalyzer.Services;

/// <summary>Resolved HDInsight cluster-gateway Basic-auth credentials.</summary>
public sealed record HdInsightCredentials(string Username, string Password);

/// <summary>
/// Resolves HDInsight cluster-gateway credentials for a given cluster FQDN.
///
/// Primary source is a Key Vault secret whose value is a JSON map of
/// <c>{ "&lt;clusterFqdn&gt;": "username:password" }</c>. The parsed map is cached for
/// <see cref="HdInsightKeyVaultOptions.CacheMinutes"/> minutes. When Key Vault is not configured
/// (or has no entry for the requested cluster), the static
/// <see cref="HdInsightHistoryOptions.Username"/>/<see cref="HdInsightHistoryOptions.Password"/>
/// are used as a fallback.
///
/// The managed identity running this service needs "Key Vault Secrets User" (get) on the vault.
/// </summary>
public sealed class HdInsightCredentialProvider
{
    private readonly SparkHistoryOptions _options;
    private readonly TokenCredential _credential;
    private readonly ILogger<HdInsightCredentialProvider> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, HdInsightCredentials>? _cache;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    public HdInsightCredentialProvider(
        IOptions<SparkHistoryOptions> options,
        TokenCredential credential,
        ILogger<HdInsightCredentialProvider> logger)
    {
        _options = options.Value;
        _credential = credential;
        _logger = logger;
    }

    /// <summary>
    /// Returns credentials for <paramref name="clusterFqdn"/>, or the static fallback, or null if
    /// no credentials are configured at all.
    /// </summary>
    public async Task<HdInsightCredentials?> GetAsync(string? clusterFqdn, CancellationToken ct = default)
    {
        var kv = _options.HDInsight.KeyVault;
        if (!string.IsNullOrWhiteSpace(kv.VaultUri) && !string.IsNullOrWhiteSpace(kv.SecretName))
        {
            var map = await GetMapAsync(kv, ct);
            if (map is { Count: > 0 } && !string.IsNullOrWhiteSpace(clusterFqdn))
            {
                if (map.TryGetValue(clusterFqdn!.TrimEnd('.'), out var creds))
                    return creds;

                _logger.LogWarning(
                    "Key Vault credentials map has no entry for cluster '{Cluster}'; using static fallback if configured.",
                    clusterFqdn);
            }
        }

        var hdi = _options.HDInsight;
        return string.IsNullOrEmpty(hdi.Username)
            ? null
            : new HdInsightCredentials(hdi.Username!, hdi.Password ?? string.Empty);
    }

    private async Task<Dictionary<string, HdInsightCredentials>?> GetMapAsync(
        HdInsightKeyVaultOptions kv, CancellationToken ct)
    {
        if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cache;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiry)
                return _cache;

            var client = new SecretClient(new Uri(kv.VaultUri!), _credential);
            var secret = await client.GetSecretAsync(kv.SecretName, cancellationToken: ct);
            var map = Parse(secret.Value.Value);

            _cache = map;
            _cacheExpiry = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, kv.CacheMinutes));
            _logger.LogInformation("Loaded {Count} HDInsight cluster credential(s) from Key Vault.", map.Count);
            return map;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to read HDInsight credentials from Key Vault {Vault} (secret '{Secret}').",
                kv.VaultUri, kv.SecretName);
            return _cache; // may be null/stale; caller falls back to static config
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Dictionary<string, HdInsightCredentials> Parse(string json)
    {
        var map = new Dictionary<string, HdInsightCredentials>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var raw = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // Split on the FIRST ':' only — passwords may contain ':'.
            var idx = raw!.IndexOf(':');
            var user = idx >= 0 ? raw[..idx] : raw;
            var pass = idx >= 0 ? raw[(idx + 1)..] : string.Empty;
            map[prop.Name.TrimEnd('.')] = new HdInsightCredentials(user, pass);
        }

        return map;
    }
}
