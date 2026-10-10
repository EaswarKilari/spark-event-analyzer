using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using ModelContextProtocol.Client;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Connects the agent to the remote Azure DevOps MCP server on behalf of the signed-in user.
///
/// Auth: a delegated (device-code) token scoped to <c>https://mcp.dev.azure.com/.default</c>,
/// acquired with a public client id (Azure CLI by default). The user completes a one-time
/// device-code sign-in via <c>POST /api/ado/login</c>; the token is cached in-process and
/// refreshed silently. Each MCP HTTP request carries the current bearer token.
///
/// The exposed MCP tools are <see cref="AIFunction"/>s, so they plug directly into the
/// Agent Framework tool list — the model can call ADO tools (repos, work items, search, …)
/// alongside the local Spark tools.
/// </summary>
public sealed class AzureDevOpsMcpClient : IAsyncDisposable
{
    private readonly AzureDevOpsMcpOptions _options;
    private readonly ILogger<AzureDevOpsMcpClient> _logger;
    private readonly IPublicClientApplication? _pca;
    private readonly string[] _scopes;

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private IMcpClient? _client;
    private IReadOnlyList<AITool> _tools = Array.Empty<AITool>();
    private Task? _loginInProgress;
    private DeviceLoginInfo? _pendingPrompt;

    // Browser-supplied (MSAL.js) delegated token, used when AuthMode=Browser. Takes precedence
    // over any server-side MSAL token so the same MCP plumbing works headless.
    private readonly object _externalLock = new();
    private string? _externalToken;
    private DateTimeOffset _externalExpiry;
    private string? _externalAccount;

    public AzureDevOpsMcpClient(
        IOptions<AzureDevOpsMcpOptions> options,
        ILogger<AzureDevOpsMcpClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _scopes = new[] { _options.Scope };

        if (_options.IsConfigured)
        {
            _pca = PublicClientApplicationBuilder
                .Create(_options.ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, _options.TenantId)
                // Loopback redirect enables interactive system-browser sign-in with a public
                // client id (same mechanism az login uses). Unused by the device-code path.
                .WithRedirectUri("http://localhost")
                .Build();
        }
    }

    public bool Enabled => _options.IsConfigured;

    /// <summary>True when a cached token is available (user has signed in).</summary>
    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
        => await TryGetTokenSilentAsync(ct).ConfigureAwait(false) is not null;

    public async Task<string?> GetAccountAsync()
    {
        lock (_externalLock)
        {
            if (_externalToken is not null && _externalExpiry > DateTimeOffset.UtcNow)
                return _externalAccount;
        }
        if (_pca is null) return null;
        var accounts = await _pca.GetAccountsAsync().ConfigureAwait(false);
        return accounts.FirstOrDefault()?.Username;
    }

    /// <summary>
    /// Accepts a delegated MCP token acquired in the browser (MSAL.js). Stored in-process and
    /// used by every subsequent MCP request until it expires. Used by AuthMode=Browser.
    /// </summary>
    public void SetExternalToken(string accessToken, DateTimeOffset? expiresOn, string? account)
    {
        lock (_externalLock)
        {
            _externalToken = accessToken;
            _externalExpiry = expiresOn ?? DateTimeOffset.UtcNow.AddMinutes(50);
            _externalAccount = account;
        }
        _logger.LogInformation("Accepted browser-supplied ADO MCP token for {Account}.", account ?? "user");
    }

    /// <summary>
    /// Starts (or resumes) a sign-in using the configured <see cref="McpAuthMode"/> and returns
    /// the prompt to show the user. For Interactive a browser window opens; for DeviceCode a
    /// code + verification URL is returned. The token completes in the background; poll status.
    /// </summary>
    public async Task<DeviceLoginInfo> StartSignInAsync(CancellationToken ct = default)
    {
        if (_pca is null)
            return new DeviceLoginInfo("disabled", "Azure DevOps MCP is not configured.", null, null, null, null);

        if (await TryGetTokenSilentAsync(ct).ConfigureAwait(false) is not null)
            return new DeviceLoginInfo("already-signed-in", null, null, null, null, await GetAccountAsync().ConfigureAwait(false));

        return _options.AuthMode switch
        {
            McpAuthMode.Browser => new DeviceLoginInfo(
                "use-browser", "Sign in from the browser (MSAL popup).", null, null, null, null),
            McpAuthMode.Interactive => await StartInteractiveAsync(ct).ConfigureAwait(false),
            _ => await StartDeviceLoginAsync(ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Pops the system browser for interactive Authorization Code + PKCE sign-in. Returns
    /// immediately with an "interactive" prompt; the token completes when the user finishes
    /// in the opened browser window. Not affected by device-code Conditional Access policies.
    /// </summary>
    private async Task<DeviceLoginInfo> StartInteractiveAsync(CancellationToken ct)
    {
        await _loginGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loginInProgress is { IsCompleted: false })
                return _pendingPrompt ?? new DeviceLoginInfo("interactive", "Sign-in already in progress — check the browser window.", null, null, null, null);

            _loginInProgress = Task.Run(async () =>
            {
                try
                {
                    await _pca!.AcquireTokenInteractive(_scopes)
                        .WithUseEmbeddedWebView(false) // launch the default system browser + loopback listener
                        .ExecuteAsync(CancellationToken.None)
                        .ConfigureAwait(false);

                    _logger.LogInformation("Azure DevOps MCP interactive sign-in completed.");
                    _pendingPrompt = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Azure DevOps MCP interactive sign-in failed.");
                    _pendingPrompt = null;
                }
            }, CancellationToken.None);

            var info = new DeviceLoginInfo(
                "interactive", "A browser window has opened — complete sign-in there.", null, null, null, null);
            _pendingPrompt = info;
            return info;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>
    /// Starts (or resumes) a device-code sign-in and returns the prompt to show the user.
    /// Returns as soon as the user_code/verification_uri are available; the token is
    /// completed in the background when the user finishes signing in.
    /// </summary>
    private async Task<DeviceLoginInfo> StartDeviceLoginAsync(CancellationToken ct = default)
    {
        await _loginGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loginInProgress is { IsCompleted: false })
                return _pendingPrompt ?? new DeviceLoginInfo("pending", "Sign-in already in progress.", null, null, null, null);

            var tcs = new TaskCompletionSource<DeviceLoginInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loginInProgress = Task.Run(async () =>
            {
                try
                {
                    await _pca!.AcquireTokenWithDeviceCode(_scopes, dcr =>
                    {
                        var info = new DeviceLoginInfo(
                            "device-code", dcr.Message, dcr.UserCode, dcr.VerificationUrl, dcr.ExpiresOn, null);
                        _pendingPrompt = info;
                        tcs.TrySetResult(info);
                        return Task.CompletedTask;
                    }).ExecuteAsync(CancellationToken.None).ConfigureAwait(false);

                    _logger.LogInformation("Azure DevOps MCP device-code sign-in completed.");
                    _pendingPrompt = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Azure DevOps MCP device-code sign-in failed.");
                    _pendingPrompt = null;
                    tcs.TrySetException(ex);
                }
            }, CancellationToken.None);

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>
    /// Returns the ADO MCP tools as Agent Framework tools. Empty when disabled or the user
    /// has not signed in yet (the agent then runs with only the local Spark tools).
    /// </summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct = default)
    {
        if (!Enabled) return Array.Empty<AITool>();
        if (await TryGetTokenSilentAsync(ct).ConfigureAwait(false) is null)
        {
            _logger.LogInformation("Azure DevOps MCP requested but user is not signed in; skipping ADO tools.");
            return Array.Empty<AITool>();
        }

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return _tools;
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is not null) return;

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_options.Toolsets))
                headers["X-MCP-Toolsets"] = _options.Toolsets!;
            if (_options.ReadOnly)
                headers["X-MCP-Readonly"] = "true";

            var httpClient = new HttpClient(new BearerTokenHandler(GetTokenAsync));

            var transport = new SseClientTransport(
                new SseClientTransportOptions
                {
                    Endpoint = new Uri(_options.Url),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    Name = "azure-devops",
                    AdditionalHeaders = headers,
                },
                httpClient,
                ownsHttpClient: true);

            var client = await McpClientFactory.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);

            _client = client;
            _tools = tools.Cast<AITool>().ToList();
            _logger.LogInformation("Connected to Azure DevOps MCP; {Count} tools available.", _tools.Count);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<string?> TryGetTokenSilentAsync(CancellationToken ct)
    {
        // Browser-supplied (MSAL.js) token takes precedence — used by AuthMode=Browser.
        lock (_externalLock)
        {
            if (_externalToken is not null && _externalExpiry > DateTimeOffset.UtcNow.AddMinutes(1))
                return _externalToken;
        }

        if (_pca is null) return null;
        var accounts = await _pca.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        if (account is null) return null;
        try
        {
            var result = await _pca.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var token = await TryGetTokenSilentAsync(ct).ConfigureAwait(false);
        return token
            ?? throw new InvalidOperationException(
                "Not signed in to Azure DevOps MCP. Call POST /api/ado/login and complete device-code sign-in.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable d)
            await d.DisposeAsync().ConfigureAwait(false);
        _connectGate.Dispose();
        _loginGate.Dispose();
    }

    /// <summary>Injects the current delegated bearer token into every MCP HTTP request.</summary>
    private sealed class BearerTokenHandler : DelegatingHandler
    {
        private readonly Func<CancellationToken, Task<string>> _tokenProvider;

        public BearerTokenHandler(Func<CancellationToken, Task<string>> tokenProvider)
            : base(new HttpClientHandler())
            => _tokenProvider = tokenProvider;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
