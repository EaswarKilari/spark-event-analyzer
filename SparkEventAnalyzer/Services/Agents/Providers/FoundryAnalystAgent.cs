using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Production adapter for Azure AI Foundry Agent Service.
///
/// Builds an <see cref="AIAgent"/> against a Foundry project (managed runtime), with the
/// same instructions + <see cref="SparkTools"/> tool as the self-hosted adapter. The tool
/// still executes here in the backend via the Agent Framework's function-calling contract
/// ("agent decides, backend executes"). Endpoints and tools do not change.
/// </summary>
public sealed class FoundryAnalystAgent : IAnalystAgent
{
    private const string SystemInstructions =
        """
        You are a Spark failure analyst. A user gives you a Spark application id.
        Workflow you MUST follow:
        1. Call get_spark_run_summary with the application id (and attemptId if provided) to fetch the compact run summary.
        2. Reason ONLY over the returned summary — do not invent metrics.
        3. Explain what happened: any failures, and the most likely performance issues (skew, spill, GC/memory pressure, executor loss).
        4. Give a short, prioritized list of likely causes and concrete next steps.
        If Azure DevOps tools are available, you MAY use them to add context (e.g. find the owning
        pipeline, recent commits, or related work items) — but only when it helps explain the run.
        You MAY call search_code to find relevant source (the code that owns a Spark job,
        a config value, or a failure path) when it helps explain the run or ground your answer.
        Be concise and specific. If the summary shows no failure/perf signals, say the run looks healthy.
        """;

    private const string ChatInstructions =
        "You are a helpful, friendly AI assistant. Have a natural conversation and answer " +
        "questions clearly and concisely. You can also search the codebase " +
        "with the search_code tool — use it when the user asks about the code, then cite file paths. " +
        "You are general-purpose and not limited to Spark topics.";

    private readonly FoundryOptions _options;
    private readonly SparkRunSummarizer _summarizer;
    private readonly CodeSearchClient _codeSearch;
    private readonly TokenCredential _credential;
    private readonly AzureDevOpsMcpClient _adoMcp;
    private readonly ILogger<FoundryAnalystAgent> _logger;

    public FoundryAnalystAgent(
        IOptions<FoundryOptions> options,
        SparkRunSummarizer summarizer,
        CodeSearchClient codeSearch,
        TokenCredential credential,
        AzureDevOpsMcpClient adoMcp,
        ILogger<FoundryAnalystAgent> logger)
    {
        _options = options.Value;
        _summarizer = summarizer;
        _codeSearch = codeSearch;
        _credential = credential;
        _adoMcp = adoMcp;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<string> AnalyzeAsync(string appId, string? attemptId, string? question, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Foundry is not configured. Set Foundry:ProjectEndpoint and Foundry:Deployment.");
        }

        var mcpTools = await _adoMcp.GetToolsAsync(ct);
        if (mcpTools.Count > 0)
            _logger.LogInformation("Adding {Count} Azure DevOps MCP tools to the agent.", mcpTools.Count);

        var agent = BuildAgent(mcpTools);

        var userMessage = string.IsNullOrWhiteSpace(question)
            ? $"Analyze Spark application '{appId}'{(string.IsNullOrWhiteSpace(attemptId) ? "" : $" attempt '{attemptId}'")}."
            : $"Spark application '{appId}'{(string.IsNullOrWhiteSpace(attemptId) ? "" : $" attempt '{attemptId}'")}. {question}";

        _logger.LogInformation("Running Foundry Spark analyst agent for {AppId}", appId);
        var response = await agent.RunAsync(userMessage, cancellationToken: ct);
        return response.Text;
    }

    public async Task<string> ChatAsync(IReadOnlyList<ChatTurn> history, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Foundry is not configured. Set Foundry:ProjectEndpoint and Foundry:Deployment.");
        }

        // Ephemeral agent — the Foundry project is the model provider. Code search is offered when configured.
        var tools = new List<AITool>();
        if (_codeSearch.IsConfigured)
        {
            var codeSearchTools = new CodeSearchTools(_codeSearch);
            tools.Add(AIFunctionFactory.Create(codeSearchTools.SearchCodeAsync));
        }
        var client = new AIProjectClient(new Uri(_options.ProjectEndpoint), _credential);
        var agent = client.AsAIAgent(
            model: _options.Deployment,
            instructions: ChatInstructions,
            name: "GeneralChat",
            tools: tools);

        _logger.LogInformation("Running Foundry general chat ({Turns} turns)", history.Count);
        var response = await agent.RunAsync(history.ToChatMessages(), cancellationToken: ct);
        return response.Text;
    }

    private AIAgent BuildAgent(IReadOnlyList<AITool> extraTools)
    {
        var sparkTools = new SparkTools(_summarizer);
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(sparkTools.GetSparkRunSummaryAsync)
        };
        if (_codeSearch.IsConfigured)
        {
            var codeSearchTools = new CodeSearchTools(_codeSearch);
            tools.Add(AIFunctionFactory.Create(codeSearchTools.SearchCodeAsync));
        }
        tools.AddRange(extraTools);

        // Managed Identity in prod; az login locally (shared TokenCredential).
        var client = new AIProjectClient(new Uri(_options.ProjectEndpoint), _credential);

        return client.AsAIAgent(
            model: _options.Deployment,
            instructions: SystemInstructions,
            name: "SparkFailureAnalyst",
            tools: tools);
    }
}

/// <summary>Configuration for the Foundry Agent Service adapter (the "Foundry" section).</summary>
public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    /// <summary>Foundry project endpoint, e.g. https://&lt;project&gt;.services.ai.azure.com/api/projects/&lt;name&gt;</summary>
    public string ProjectEndpoint { get; set; } = string.Empty;

    /// <summary>Model deployment name used by the agent, e.g. gpt-4o / gpt-5.</summary>
    public string Deployment { get; set; } = string.Empty;

    /// <summary>Optional id of an already-created persistent agent to reuse.</summary>
    public string? AgentId { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProjectEndpoint) && !string.IsNullOrWhiteSpace(Deployment);
}
