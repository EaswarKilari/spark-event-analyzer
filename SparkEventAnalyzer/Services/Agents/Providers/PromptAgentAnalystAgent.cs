using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Adapter that runs against a persistent, server-side Azure AI Foundry <b>prompt agent</b>.
///
/// Unlike <see cref="FoundryAnalystAgent"/> — which uses <c>AsAIAgent</c> to run an
/// <i>ephemeral, in-process</i> agent that merely uses the Foundry project as a model
/// provider — this adapter calls <c>ToPromptAgentAsync</c> to <b>promote</b> the agent into
/// a Foundry-managed prompt agent (definition stored/versioned in the project). Foundry then
/// owns the agent object and the run/history lifecycle.
///
/// The <see cref="SparkTools"/> tool still executes here in the backend via function-calling
/// ("agent decides, backend executes"), because it needs Managed Identity + network access to
/// the Spark History Server. Reuses the same <see cref="FoundryOptions"/> (the "Foundry" section).
/// </summary>
public sealed class PromptAgentAnalystAgent : IAnalystAgent
{
    /// <summary>Stable name of the server-side prompt agent registered in the Foundry project.</summary>
    private const string AgentName = "SparkFailureAnalyst";

    private const string SystemInstructions =
        """
        You are a Spark failure analyst. A user gives you a Spark application id.
        Workflow you MUST follow:
        1. Call get_spark_run_summary with the application id (and attemptId if provided) to fetch the compact run summary.
        2. Reason ONLY over the returned summary — do not invent metrics.
        3. Explain what happened: any failures, and the most likely performance issues (skew, spill, GC/memory pressure, executor loss).
        4. Give a short, prioritized list of likely causes and concrete next steps.
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
    private readonly ILogger<PromptAgentAnalystAgent> _logger;

    // The server-side prompt agent is published once per process, then reused. Only the resolved
    // version string is cached (building the runnable agent from a reference is local/no-network).
    private static string? _publishedVersion;
    private static readonly SemaphoreSlim _publishLock = new(1, 1);

    public PromptAgentAnalystAgent(
        IOptions<FoundryOptions> options,
        SparkRunSummarizer summarizer,
        CodeSearchClient codeSearch,
        TokenCredential credential,
        ILogger<PromptAgentAnalystAgent> logger)
    {
        _options = options.Value;
        _summarizer = summarizer;
        _codeSearch = codeSearch;
        _credential = credential;
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

        var agent = await BuildPromptAgentAsync(ct);

        var userMessage = string.IsNullOrWhiteSpace(question)
            ? $"Analyze Spark application '{appId}'{(string.IsNullOrWhiteSpace(attemptId) ? "" : $" attempt '{attemptId}'")}."
            : $"Spark application '{appId}'{(string.IsNullOrWhiteSpace(attemptId) ? "" : $" attempt '{attemptId}'")}. {question}";

        _logger.LogInformation("Running Foundry PROMPT agent for {AppId}", appId);
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

        // General chat needs no Spark tools, so skip the prompt-agent publish and just run an
        // ephemeral agent against the Foundry model. Code search is offered when configured.
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

    private async Task<AIAgent> BuildPromptAgentAsync(CancellationToken ct)
    {
        var sparkTools = new SparkTools(_summarizer);

        // The Foundry prompt agent (Responses API) validates tool schemas in STRICT mode, which
        // requires every function's parameter schema to set "additionalProperties": false and mark
        // all properties required. Force a strict schema so publishing/running the agent succeeds.
        var toolOptions = new AIFunctionFactoryOptions
        {
            JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
            {
                TransformOptions = new AIJsonSchemaTransformOptions
                {
                    DisallowAdditionalProperties = true,
                    RequireAllProperties = true,
                },
            },
        };

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(sparkTools.GetSparkRunSummaryAsync, toolOptions)
        };
        if (_codeSearch.IsConfigured)
        {
            var codeSearchTools = new CodeSearchTools(_codeSearch);
            tools.Add(AIFunctionFactory.Create(codeSearchTools.SearchCodeAsync, toolOptions));
        }

        // Managed Identity in prod; az login locally (shared TokenCredential).
        var client = new AIProjectClient(new Uri(_options.ProjectEndpoint), _credential);

        // Publish the server-side prompt agent once per process, then reuse the version. If the
        // caller pinned a specific Foundry:AgentId, use it as the version and skip publishing.
        if (_options.AgentId is { Length: > 0 } pinned)
        {
            _publishedVersion ??= pinned;
        }
        else if (_publishedVersion is null)
        {
            await _publishLock.WaitAsync(ct);
            try
            {
                if (_publishedVersion is null)
                {
                    // 1) Build the in-process definition (model + instructions + tools) ...
                    ChatClientAgent chatAgent = client.AsAIAgent(
                        model: _options.Deployment,
                        instructions: SystemInstructions,
                        name: AgentName,
                        tools: tools);

                    // 2) ... convert it to a Foundry prompt-agent definition ...
                    ProjectsAgentDefinition definition = await chatAgent.ToPromptAgentAsync(ct);

                    // 3) ... and PERSIST it as an agent version in the project (this is the step that
                    //    actually creates the server-side agent; ToPromptAgentAsync only builds the
                    //    definition object). The returned version is what we bind and run against.
                    ProjectsAgentVersion published = await client.AgentAdministrationClient
                        .CreateAgentVersionAsync(AgentName, new ProjectsAgentVersionCreationOptions(definition), foundryFeatures: null, cancellationToken: ct);

                    _publishedVersion = published.Version;
                    _logger.LogInformation("Published Foundry prompt agent {Name} version {Version}", published.Name, published.Version);
                }
            }
            finally
            {
                _publishLock.Release();
            }
        }

        // Bind a runnable agent to the published prompt agent by reference. Tools are re-supplied so
        // the get_spark_run_summary callback still executes here in the backend.
        var reference = new AgentReference(AgentName, _publishedVersion);
        return client.AsAIAgent(reference, tools);
    }
}
