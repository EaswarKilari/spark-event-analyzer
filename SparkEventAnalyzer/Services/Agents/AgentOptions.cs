namespace SparkEventAnalyzer.Services;

/// <summary>
/// Selects which agent runtime (adapter) to use.
/// - AzureOpenAI: in-process agent loop over a bare Azure OpenAI resource (dev / CI / demo).
/// - Foundry: in-process agent (AsAIAgent) pointed at a Foundry project — Foundry as model provider.
/// - PromptAgent: promotes the agent to a persistent, server-side Foundry **prompt agent**
///   (ToPromptAgentAsync) — Foundry as a managed, versioned agent runtime.
/// In all cases the SparkTools tool executes in the backend (function-calling).
/// </summary>
public enum AgentProvider
{
    AzureOpenAI,
    Foundry,
    PromptAgent
}

/// <summary>
/// Top-level agent configuration (the "Agent" section).
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public AgentProvider Provider { get; set; } = AgentProvider.AzureOpenAI;
}
