using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using SparkEventAnalyzer.Models;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Spark History Server REST API (v1) client. Fetches applications, jobs, stages and executors.
/// The base URL and authentication are resolved per request from the scoped
/// <see cref="ISparkTargetAccessor"/> (HDInsight gateway = Basic auth, Synapse = Entra bearer,
/// Generic = optional bearer), so a single deployment can serve multiple clusters/workspaces.
/// </summary>
public sealed class SparkHistoryClient : ISparkHistoryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly SparkHistoryOptions _options;
    private readonly TokenCredential _credential;
    private readonly ISparkTargetAccessor _target;
    private readonly HdInsightCredentialProvider _hdiCredentials;
    private readonly ILogger<SparkHistoryClient> _logger;

    public SparkHistoryClient(
        HttpClient http,
        IOptions<SparkHistoryOptions> options,
        TokenCredential credential,
        ISparkTargetAccessor target,
        HdInsightCredentialProvider hdiCredentials,
        ILogger<SparkHistoryClient> logger)
    {
        _http = http;
        _options = options.Value;
        _credential = credential;
        _target = target;
        _hdiCredentials = hdiCredentials;
        _logger = logger;

        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<IReadOnlyList<SparkApplication>> ListApplicationsAsync(CancellationToken ct = default)
        => await GetAsync<List<SparkApplication>>("applications", ct) ?? new List<SparkApplication>();

    public Task<SparkApplication?> GetApplicationAsync(string appId, CancellationToken ct = default)
        => GetAsync<SparkApplication>($"applications/{Uri.EscapeDataString(appId)}", ct);

    public async Task<IReadOnlyList<SparkJob>> GetJobsAsync(string appId, string? attemptId = null, CancellationToken ct = default)
        => await GetAsync<List<SparkJob>>($"{AppPath(appId, attemptId)}/jobs", ct) ?? new List<SparkJob>();

    public async Task<IReadOnlyList<SparkStage>> GetStagesAsync(string appId, string? attemptId = null, CancellationToken ct = default)
        => await GetAsync<List<SparkStage>>($"{AppPath(appId, attemptId)}/stages", ct) ?? new List<SparkStage>();

    public async Task<IReadOnlyList<SparkExecutor>> GetExecutorsAsync(string appId, string? attemptId = null, CancellationToken ct = default)
        => await GetAsync<List<SparkExecutor>>($"{AppPath(appId, attemptId)}/allexecutors", ct) ?? new List<SparkExecutor>();

    public async Task<string?> DownloadEventLogTextAsync(string appId, string? attemptId = null, CancellationToken ct = default)
    {
        var target = _target.Current ?? new SparkTarget(_options.DefaultPlatform);
        // The "/logs" endpoint requires an attempt id; default to "1" when not supplied.
        var attempt = string.IsNullOrWhiteSpace(attemptId) ? "1" : attemptId!;
        var url = BuildUrl(target, $"applications/{Uri.EscapeDataString(appId)}/{Uri.EscapeDataString(attempt)}/logs");

        // Force HTTP/1.1: the HDInsight gateway hangs when streaming this zip over HTTP/2.
        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        await ApplyAuthAsync(request, target, ct);

        _logger.LogInformation("Spark History event-log GET [{Platform}] {Url}", target.Platform, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new SparkHistoryException(
                $"Spark event-log request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for {url}. Body: {Truncate(body, 500)}");
        }

        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var download = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(tempPath))
            {
                await download.CopyToAsync(file, ct);
            }

            return ExtractLargestEntry(tempPath, _options.HDInsight.EventLogMaxBytes);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static string? ExtractLargestEntry(string zipPath, long maxBytes)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries
            .Where(e => e.Length > 0)
            .OrderByDescending(e => e.Length)
            .FirstOrDefault();
        if (entry is null)
            return null;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);

        if (maxBytes <= 0 || entry.Length <= maxBytes)
            return reader.ReadToEnd();

        // Cap reads for very large logs, then trim to the last complete line so JSON parsing is clean.
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while (sb.Length < maxBytes && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
            sb.Append(buffer, 0, read);

        var text = sb.ToString();
        var lastNewline = text.LastIndexOf('\n');
        return lastNewline > 0 ? text[..lastNewline] : text;
    }

    private static string AppPath(string appId, string? attemptId)
    {
        var app = $"applications/{Uri.EscapeDataString(appId)}";
        return string.IsNullOrWhiteSpace(attemptId)
            ? app
            : $"{app}/{Uri.EscapeDataString(attemptId)}";
    }

    private async Task<T?> GetAsync<T>(string relativePath, CancellationToken ct)
    {
        var target = _target.Current ?? new SparkTarget(_options.DefaultPlatform);
        var url = BuildUrl(target, relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        await ApplyAuthAsync(request, target, ct);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _logger.LogInformation("Spark History GET [{Platform}] {Url}", target.Platform, url);
        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new SparkHistoryException(
                $"Spark History request failed ({(int)response.StatusCode} {response.ReasonPhrase}) for {url}. Body: {Truncate(body, 500)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private string BuildUrl(SparkTarget target, string relativePath)
    {
        var baseUrl = ResolveBaseUrl(target).TrimEnd('/');
        return $"{baseUrl}/api/v1/{relativePath}";
    }

    private string ResolveBaseUrl(SparkTarget target) => target.Platform switch
    {
        SparkPlatform.HDInsight => _options.HDInsight.BaseUrlTemplate
            .Replace("{cluster}", target.Cluster ?? string.Empty),

        SparkPlatform.Synapse => _options.Synapse.BaseUrlTemplate
            .Replace("{workspace}", target.Workspace ?? string.Empty)
            .Replace("{sparkPool}", target.SparkPool ?? string.Empty)
            .Replace("{livyId}", target.LivyId ?? string.Empty),

        _ => _options.Generic.BaseUrl,
    };

    private async Task ApplyAuthAsync(HttpRequestMessage request, SparkTarget target, CancellationToken ct)
    {
        switch (target.Platform)
        {
            case SparkPlatform.HDInsight:
                var creds = await _hdiCredentials.GetAsync(target.Cluster, ct);
                if (creds is not null)
                {
                    var basic = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                }
                break;

            case SparkPlatform.Synapse:
                await AttachBearerAsync(request, _options.Synapse.Scope, ct);
                break;

            default:
                await AttachBearerAsync(request, _options.Generic.AuthScope, ct);
                break;
        }
    }

    private async Task AttachBearerAsync(HttpRequestMessage request, string? scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope)) return;
        var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}

public sealed class SparkHistoryException : Exception
{
    public SparkHistoryException(string message) : base(message) { }
}
