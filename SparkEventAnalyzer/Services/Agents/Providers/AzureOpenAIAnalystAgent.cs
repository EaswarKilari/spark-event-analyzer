using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// The "Spark failure analyst" agent, built on the Microsoft Agent Framework over an
/// Azure OpenAI chat client. It calls the <see cref="SparkTools"/> tool to fetch a run
/// summary, then explains failures / performance issues and likely causes.
///
/// This is the self-hosted adapter (dev / CI / demo). The Foundry Agent Service adapter
/// (<see cref="FoundryAnalystAgent"/>) is selected in production via <c>Agent:Provider</c>.
/// </summary>
public sealed class AzureOpenAIAnalystAgent : IAnalystAgent
{
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

    private readonly AzureOpenAIOptions _options;
    private readonly SparkRunSummarizer _summarizer;
    private readonly CodeSearchClient _codeSearch;
    private readonly TokenCredential _credential;
    private readonly ILogger<AzureOpenAIAnalystAgent> _logger;

    public AzureOpenAIAnalystAgent(
        IOptions<AzureOpenAIOptions> options,
        SparkRunSummarizer summarizer,
        CodeSearchClient codeSearch,
        TokenCredential credential,
        ILogger<AzureOpenAIAnalystAgent> logger)
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
                "AzureOpenAI is not configured. Set AzureOpenAI:Endpoint and AzureOpenAI:Deployment (and optionally ApiKey).");
        }

        var agent = BuildAgent();

        var userMessage = string.IsNullOrWhiteSpace(question)
            ? $"Analyze Spark application '{appId}'{(string.IsNullOrWhiteSpace(attemptId) ? "" : $" attempt '{attemptId}'")}."
            : $"Spark application '{appId}'{(string.IsNullOrWhiteSpace(attemptId) ? "" : $" attempt '{attemptId}'")}. {question}";

        _logger.LogInformation("Running Spark analyst agent for {AppId}", appId);
        var response = await agent.RunAsync(userMessage, cancellationToken: ct);
        return response.Text;
    }

    public async Task<string> ChatAsync(IReadOnlyList<ChatTurn> history, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "AzureOpenAI is not configured. Set AzureOpenAI:Endpoint and AzureOpenAI:Deployment (and optionally ApiKey).");
        }

        var endpoint = new Uri(_options.Endpoint);
        AzureOpenAIClient client = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new AzureOpenAIClient(endpoint, _credential)
            : new AzureOpenAIClient(endpoint, new AzureKeyCredential(_options.ApiKey));

        IChatClient chatClient = client.GetChatClient(_options.Deployment).AsIChatClient();
        var chatOptions = new ChatOptions { Instructions = ChatInstructions };
        if (_codeSearch.IsConfigured)
        {
            var codeSearchTools = new CodeSearchTools(_codeSearch);
            chatOptions.Tools = new List<AITool> { AIFunctionFactory.Create(codeSearchTools.SearchCodeAsync) };
        }
        var agentOptions = new ChatClientAgentOptions
        {
            Name = "GeneralChat",
            ChatOptions = chatOptions
        };
        var agent = new ChatClientAgent(chatClient, agentOptions);

        _logger.LogInformation("Running Azure OpenAI general chat ({Turns} turns)", history.Count);
        var response = await agent.RunAsync(history.ToChatMessages(), cancellationToken: ct);
        return response.Text;
    }

    private AIAgent BuildAgent()
    {
        var endpoint = new Uri(_options.Endpoint);
        AzureOpenAIClient client = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new AzureOpenAIClient(endpoint, _credential)                         // Managed Identity / az login
            : new AzureOpenAIClient(endpoint, new AzureKeyCredential(_options.ApiKey));

        // Expose the Spark summary fetch as an agent tool.
        var sparkTools = new SparkTools(_summarizer);
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(sparkTools.GetSparkRunSummaryAsync)
        };

        // Expose code search too, when the index is configured.
        if (_codeSearch.IsConfigured)
        {
            var codeSearchTools = new CodeSearchTools(_codeSearch);
            tools.Add(AIFunctionFactory.Create(codeSearchTools.SearchCodeAsync));
        }

        IChatClient chatClient = client.GetChatClient(_options.Deployment).AsIChatClient();

        var agentOptions = new ChatClientAgentOptions
        {
            Name = "SparkFailureAnalyst",
            ChatOptions = new ChatOptions
            {
                Instructions = SystemInstructions,
                Tools = tools
            }
        };

        return new ChatClientAgent(chatClient, agentOptions);
    }
}
