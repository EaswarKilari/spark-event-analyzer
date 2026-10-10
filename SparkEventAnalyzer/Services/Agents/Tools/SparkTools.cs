using System.ComponentModel;
using System.Text.Json;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Spark History data exposed to the agent as callable tools. The LLM decides
/// when to invoke these; execution happens here in the backend (the same
/// "agent decides, backend executes" pattern used by Foundry Agent Service).
///
/// The [Description] attributes are read by the Agent Framework's
/// AIFunctionFactory when it builds the tool schema.
/// </summary>
public sealed class SparkTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly SparkRunSummarizer _summarizer;

    public SparkTools(SparkRunSummarizer summarizer) => _summarizer = summarizer;

    [Description("Fetches a compact summary of a Spark application run (failures, hot stages, skew, spill, executor GC/loss) from the Spark History Server. Call this first, using the application id, before analyzing.")]
    public async Task<string> GetSparkRunSummaryAsync(
        [Description("The Spark application id, e.g. application_1700000000000_0001")] string applicationId,
        [Description("Attempt id for multi-attempt applications. Pass an empty string \"\" if there is no specific attempt.")] string attemptId,
        CancellationToken ct = default)
    {
        // Hosted prompt-agent Responses API validates function schemas strictly, so both parameters
        // are required + non-nullable. Treat an empty attempt id as "no attempt".
        var effectiveAttempt = string.IsNullOrWhiteSpace(attemptId) ? null : attemptId;
        var summary = await _summarizer.SummarizeAsync(applicationId, effectiveAttempt, ct);
        return JsonSerializer.Serialize(summary, JsonOptions);
    }
}
