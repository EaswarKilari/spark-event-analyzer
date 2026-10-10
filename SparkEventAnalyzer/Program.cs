using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using SparkEventAnalyzer.Services;

var builder = WebApplication.CreateBuilder(args);

// One shared Azure credential for Foundry, Azure OpenAI, and the Spark History Server.
// Empty AzureIdentity:ManagedIdentityClientId => DefaultAzureCredential (system-assigned
// Managed Identity in Azure, az login locally). Set it to a user-assigned MI's client id
// to authenticate as that identity — no code change required.
builder.Services.Configure<AzureIdentityOptions>(
    builder.Configuration.GetSection(AzureIdentityOptions.SectionName));
builder.Services.AddSingleton<TokenCredential>(sp =>
{
    var clientId = sp.GetRequiredService<IOptions<AzureIdentityOptions>>().Value.ManagedIdentityClientId;
    return string.IsNullOrWhiteSpace(clientId)
        ? new DefaultAzureCredential()
        : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });
});

// Options bound from the "SparkHistory" config section (see appsettings.json).
builder.Services.Configure<SparkHistoryOptions>(
    builder.Configuration.GetSection(SparkHistoryOptions.SectionName));

// Request-scoped "which cluster/workspace" target — set from each request, read by the client.
builder.Services.AddScoped<ISparkTargetAccessor, SparkTargetAccessor>();

// HDInsight cluster credentials, resolved from Key Vault (a JSON map of clusterFqdn -> "user:pass")
// with a static appsettings fallback. Singleton so the fetched map is cached across requests.
builder.Services.AddSingleton<HdInsightCredentialProvider>();

// Parses the raw Spark event log ("/logs" zip) into the run's jobs/stages/executors.
builder.Services.AddSingleton<SparkEventLogSummarizer>();

// Typed HttpClient for the Spark History Server REST client.
builder.Services.AddHttpClient<ISparkHistoryClient, SparkHistoryClient>();
builder.Services.AddScoped<SparkRunSummarizer>();

// Component 2: the Spark analyst agent. The runtime is a swappable adapter behind
// IAnalystAgent, selected by Agent:Provider (AzureOpenAI for dev/demo, Foundry for prod).
builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<AzureOpenAIOptions>(
    builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<FoundryOptions>(
    builder.Configuration.GetSection(FoundryOptions.SectionName));

// Code-search tool: hybrid vector + semantic queries over the CodeIndexer-built Azure AI Search
// index, so the agent can ground answers in the indexed source. Singleton so the Search +
// embedding clients are reused; the tool degrades gracefully when the section is unconfigured.
builder.Services.Configure<CodeSearchOptions>(
    builder.Configuration.GetSection(CodeSearchOptions.SectionName));
builder.Services.AddSingleton<CodeSearchClient>();

var agentProvider = builder.Configuration
    .GetSection(AgentOptions.SectionName)
    .GetValue<AgentProvider>(nameof(AgentOptions.Provider));

switch (agentProvider)
{
    case AgentProvider.PromptAgent:
        builder.Services.AddScoped<IAnalystAgent, PromptAgentAnalystAgent>();
        break;
    case AgentProvider.Foundry:
        builder.Services.AddScoped<IAnalystAgent, FoundryAnalystAgent>();
        break;
    default:
        builder.Services.AddScoped<IAnalystAgent, AzureOpenAIAnalystAgent>();
        break;
}

// Remote Azure DevOps MCP server (delegated, per-user device-code sign-in). Optional:
// controlled by Mcp:AzureDevOps:Enabled. Singleton so the token cache + MCP connection
// are shared across requests.
builder.Services.Configure<AzureDevOpsMcpOptions>(
    builder.Configuration.GetSection(AzureDevOpsMcpOptions.SectionName));
builder.Services.AddSingleton<AzureDevOpsMcpClient>();

var app = builder.Build();

// Serve the minimal browser UI from wwwroot (index.html at "/").
app.UseDefaultFiles();
app.UseStaticFiles();

// Health check (Component 1 "done when").
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "SparkEventAnalyzer" }));

// Azure DevOps MCP: start a one-time device-code sign-in for the current user. Returns the
// verification URL + code to complete in a browser; the token is then cached in-process and
// the agent can call ADO tools. No managed identity, PAT, or app registration required.
app.MapPost("/api/ado/login", async (AzureDevOpsMcpClient ado, CancellationToken ct) =>
{
    if (!ado.Enabled)
        return Results.Problem(title: "Azure DevOps MCP disabled", detail: "Set Mcp:AzureDevOps:Enabled=true.", statusCode: 503);
    var info = await ado.StartSignInAsync(ct);
    return Results.Ok(info);
});

// Azure DevOps MCP: current sign-in status.
app.MapGet("/api/ado/status", async (AzureDevOpsMcpClient ado, CancellationToken ct) =>
    Results.Ok(new
    {
        enabled = ado.Enabled,
        authenticated = await ado.IsAuthenticatedAsync(ct),
        account = await ado.GetAccountAsync()
    }));

// Azure DevOps MCP: list the tools currently exposed to the agent (empty until signed in).
app.MapGet("/api/ado/tools", async (AzureDevOpsMcpClient ado, CancellationToken ct) =>
{
    var tools = await ado.GetToolsAsync(ct);
    return Results.Ok(tools.Select(t => t.Name).OrderBy(n => n));
});

// Azure DevOps MCP: browser sign-in config. The frontend reads this to decide whether to run
// the MSAL.js popup (AuthMode=Browser) and with which SPA client id / scope / authority.
app.MapGet("/api/ado/config", (IOptions<AzureDevOpsMcpOptions> opts) =>
{
    var o = opts.Value;
    return Results.Ok(new
    {
        enabled = o.IsConfigured,
        authMode = o.AuthMode.ToString(),
        spaClientId = o.SpaClientId,
        tenantId = o.TenantId,
        scope = o.Scope,
        url = o.Url
    });
});

// Azure DevOps MCP: accept a delegated token acquired in the browser (MSAL.js) so the agent
// can call ADO tools on the user's behalf. Used by AuthMode=Browser (headless-safe).
app.MapPost("/api/ado/token", (AdoTokenRequest req, AzureDevOpsMcpClient ado) =>
{
    if (string.IsNullOrWhiteSpace(req.AccessToken))
        return Results.BadRequest(new { error = "accessToken is required" });
    ado.SetExternalToken(req.AccessToken, req.ExpiresOn, req.Account);
    return Results.Ok(new { accepted = true });
});

// Component 3: fetch a run's Spark data via the History Server REST API and
// return a compact summary (seed of preprocess_events).
app.MapGet("/api/spark/{appId}/summary", async (
    string appId,
    string? attemptId,
    string? platform,
    string? cluster,
    string? workspace,
    string? sparkPool,
    string? livyId,
    ISparkTargetAccessor targetAccessor,
    SparkRunSummarizer summarizer,
    CancellationToken ct) =>
{
    SetTarget(targetAccessor, platform, cluster, workspace, sparkPool, livyId);
    try
    {
        var summary = await summarizer.SummarizeAsync(appId, attemptId, ct);
        return Results.Ok(summary);
    }
    catch (SparkHistoryException ex)
    {
        return Results.Problem(title: "Spark History request failed", detail: ex.Message, statusCode: 502);
    }
});

// Raw pass-throughs (useful for debugging the REST integration).
app.MapGet("/api/spark/applications", async (
    string? platform, string? cluster, string? workspace, string? sparkPool, string? livyId,
    ISparkTargetAccessor targetAccessor, ISparkHistoryClient client, CancellationToken ct) =>
{
    SetTarget(targetAccessor, platform, cluster, workspace, sparkPool, livyId);
    return Results.Ok(await client.ListApplicationsAsync(ct));
});

app.MapGet("/api/spark/{appId}/jobs", async (
    string appId, string? attemptId,
    string? platform, string? cluster, string? workspace, string? sparkPool, string? livyId,
    ISparkTargetAccessor targetAccessor, ISparkHistoryClient client, CancellationToken ct) =>
{
    SetTarget(targetAccessor, platform, cluster, workspace, sparkPool, livyId);
    return Results.Ok(await client.GetJobsAsync(appId, attemptId, ct));
});

app.MapGet("/api/spark/{appId}/stages", async (
    string appId, string? attemptId,
    string? platform, string? cluster, string? workspace, string? sparkPool, string? livyId,
    ISparkTargetAccessor targetAccessor, ISparkHistoryClient client, CancellationToken ct) =>
{
    SetTarget(targetAccessor, platform, cluster, workspace, sparkPool, livyId);
    return Results.Ok(await client.GetStagesAsync(appId, attemptId, ct));
});

app.MapGet("/api/spark/{appId}/executors", async (
    string appId, string? attemptId,
    string? platform, string? cluster, string? workspace, string? sparkPool, string? livyId,
    ISparkTargetAccessor targetAccessor, ISparkHistoryClient client, CancellationToken ct) =>
{
    SetTarget(targetAccessor, platform, cluster, workspace, sparkPool, livyId);
    return Results.Ok(await client.GetExecutorsAsync(appId, attemptId, ct));
});

// Code search: query the CodeIndexer-built Azure AI Search index directly (bypasses the agent).
// Handy smoke test for the search_code tool's retrieval path.
app.MapGet("/api/search-code", async (
    string q,
    int? k,
    string? language,
    CodeSearchClient codeSearch,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "q is required" });

    if (!codeSearch.IsConfigured)
        return Results.Problem(
            title: "CodeSearch not configured",
            detail: "Set CodeSearch:SearchEndpoint and CodeSearch:EmbeddingEndpoint (and a key or Managed Identity RBAC).",
            statusCode: 503);

    var hits = await codeSearch.SearchAsync(q, k is > 0 ? k.Value : codeSearch.DefaultK, language, ct);
    return Results.Ok(hits.Select(h => new
    {
        h.FilePath, h.Symbol, h.Language, h.StartLine, h.EndLine,
        score = Math.Round(h.Score, 3),
    }));
});

// Component 2: agent analysis. The LLM calls the get_spark_run_summary tool,
// then explains failures / perf issues and likely causes.
app.MapPost("/api/analyze", async (
    AnalyzeRequest req,
    ISparkTargetAccessor targetAccessor,
    IAnalystAgent agent,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AppId))
        return Results.BadRequest(new { error = "appId is required" });

    if (!agent.IsConfigured)
        return Results.Problem(
            title: "Agent provider not configured",
            detail: "Configure the selected Agent:Provider. For AzureOpenAI set AzureOpenAI:Endpoint + Deployment (+ optional ApiKey); for Foundry set Foundry:ProjectEndpoint + Deployment.",
            statusCode: 503);

    SetTarget(targetAccessor, req.Platform, req.Cluster, req.Workspace, req.SparkPool, req.LivyId);

    try
    {
        var analysis = await agent.AnalyzeAsync(req.AppId, req.AttemptId, req.Question, ct);
        return Results.Ok(new { appId = req.AppId, attemptId = req.AttemptId, analysis });
    }
    catch (SparkHistoryException ex)
    {
        return Results.Problem(title: "Spark History request failed", detail: ex.Message, statusCode: 502);
    }
});

// Component 3: plain LLM chat. No Spark data, no tools, no target — general conversation.
// The client posts the full turn history each call (the API is stateless).
app.MapPost("/api/chat", async (
    ChatRequest req,
    IAnalystAgent agent,
    CancellationToken ct) =>
{
    if (req.Messages is null || req.Messages.Count == 0)
        return Results.BadRequest(new { error = "messages is required" });

    if (!agent.IsConfigured)
        return Results.Problem(
            title: "Agent provider not configured",
            detail: "Configure the selected Agent:Provider. For AzureOpenAI set AzureOpenAI:Endpoint + Deployment (+ optional ApiKey); for Foundry set Foundry:ProjectEndpoint + Deployment.",
            statusCode: 503);

    var reply = await agent.ChatAsync(req.Messages, ct);
    return Results.Ok(new { reply });
});

// Code analysis: run the LLM agent grounded in the code index (via the search_code tool) and
// return a written analysis with cited files — not raw hits. No Spark target required.
app.MapPost("/api/analyze-code", async (
    CodeAnalyzeRequest req,
    IAnalystAgent agent,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });

    if (!agent.IsConfigured)
        return Results.Problem(
            title: "Agent provider not configured",
            detail: "Configure the selected Agent:Provider. For AzureOpenAI set AzureOpenAI:Endpoint + Deployment (+ optional ApiKey); for Foundry set Foundry:ProjectEndpoint + Deployment.",
            statusCode: 503);

    var analysis = await agent.ChatAsync(new List<ChatTurn> { new("user", req.Query) }, ct);
    return Results.Ok(new { analysis });
});

app.Run();

// Sets the request-scoped Spark target from request/query values. Platform is inferred from the
// identifiers when not given explicitly (cluster => HDInsight, workspace => Synapse). When nothing
// is supplied the client falls back to SparkHistory:DefaultPlatform.
static void SetTarget(
    ISparkTargetAccessor accessor,
    string? platform, string? cluster, string? workspace, string? sparkPool, string? livyId)
{
    SparkPlatform? p = platform?.Trim().ToLowerInvariant() switch
    {
        "hdinsight" or "hdi" => SparkPlatform.HDInsight,
        "synapse" => SparkPlatform.Synapse,
        "generic" or "local" => SparkPlatform.Generic,
        _ => null,
    };

    p ??= !string.IsNullOrWhiteSpace(cluster) ? SparkPlatform.HDInsight
        : !string.IsNullOrWhiteSpace(workspace) ? SparkPlatform.Synapse
        : (SparkPlatform?)null;

    if (p is null) return; // leave null => client uses the configured DefaultPlatform

    accessor.Current = new SparkTarget(p.Value, cluster, workspace, sparkPool, livyId);
}

internal sealed record AnalyzeRequest(
    string AppId,
    string? AttemptId,
    string? Question,
    string? Platform = null,
    string? Cluster = null,
    string? Workspace = null,
    string? SparkPool = null,
    string? LivyId = null);

/// <summary>Browser-supplied (MSAL.js) delegated ADO MCP token posted to /api/ado/token.</summary>
internal sealed record AdoTokenRequest(string AccessToken, DateTimeOffset? ExpiresOn, string? Account);

/// <summary>Plain-chat request: the full conversation so far (oldest first), posted to /api/chat.</summary>
internal sealed record ChatRequest(List<ChatTurn> Messages);

/// <summary>Code-analysis request: a natural-language question answered with the search_code tool, posted to /api/analyze-code.</summary>
internal sealed record CodeAnalyzeRequest(string Query);
