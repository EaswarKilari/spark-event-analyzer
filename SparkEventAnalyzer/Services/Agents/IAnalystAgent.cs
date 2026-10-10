using Microsoft.Extensions.AI;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// The port for the Spark failure analyst agent. The runtime (self-hosted Azure
/// OpenAI vs. Foundry Agent Service) is a swappable adapter behind this interface,
/// selected by <see cref="AgentOptions.Provider"/>. Endpoints and tools do not
/// change when the adapter changes.
/// </summary>
public interface IAnalystAgent
{
    /// <summary>True when the selected provider is configured and usable.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Analyze a Spark run: the agent fetches the run summary (via its tool), then
    /// explains failures / performance issues and likely causes.
    /// </summary>
    Task<string> AnalyzeAsync(string appId, string? attemptId, string? question, CancellationToken ct = default);

    /// <summary>
    /// Plain conversational chat with the model. No Spark data, no tools — general-purpose
    /// assistant. <paramref name="history"/> is the full turn-by-turn conversation so far
    /// (oldest first), ending with the latest user message.
    /// </summary>
    Task<string> ChatAsync(IReadOnlyList<ChatTurn> history, CancellationToken ct = default);
}

/// <summary>One conversational turn. <paramref name="Role"/> is "user" or "assistant".</summary>
public sealed record ChatTurn(string Role, string Content);

internal static class ChatTurnExtensions
{
    /// <summary>Maps UI turns to Agent Framework chat messages (anything not "assistant" is a user turn).</summary>
    public static List<ChatMessage> ToChatMessages(this IReadOnlyList<ChatTurn> history) =>
        history.Select(t => new ChatMessage(
            string.Equals(t.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User,
            t.Content ?? string.Empty)).ToList();
}
