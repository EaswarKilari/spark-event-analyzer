using System.ComponentModel;
using System.Text.Json;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Exposes the CodeIndexer Azure AI Search index to the agent as a callable tool so the LLM can
/// ground its answers in the actual indexed source. The LLM decides when to call it; execution
/// happens here in the backend (the same "agent decides, backend executes" pattern as
/// <see cref="SparkTools"/>).
///
/// The [Description] attributes are read by the Agent Framework's AIFunctionFactory when it builds
/// the tool schema.
/// </summary>
public sealed class CodeSearchTools
{
    private const int MaxSnippetChars = 1200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly CodeSearchClient _client;

    public CodeSearchTools(CodeSearchClient client) => _client = client;

    [Description("Searches the source code (hybrid vector + semantic search over the indexed repository) and returns the most relevant code chunks with file path, symbol, and line range. Use this to ground answers in the actual codebase — e.g. to locate the code that owns a Spark job, a config value, or a failure path.")]
    public async Task<string> SearchCodeAsync(
        [Description("Natural-language or keyword query describing the code you want to find.")] string query,
        [Description("Maximum number of results to return. Pass 0 to use the default.")] int maxResults,
        [Description("Optional language filter, e.g. \"csharp\", \"scala\", \"powershell\". Pass an empty string \"\" for all languages.")] string language,
        CancellationToken ct = default)
    {
        // Hosted prompt-agent Responses API validates function schemas strictly, so all parameters
        // are required + non-nullable. Treat 0 as "default k" and an empty language as "no filter".
        var k = maxResults > 0 ? maxResults : _client.DefaultK;
        var lang = string.IsNullOrWhiteSpace(language) ? null : language;

        var hits = await _client.SearchAsync(query, k, lang, ct);

        var projected = hits.Select(h => new
        {
            file = h.FilePath,
            symbol = h.Symbol,
            language = h.Language,
            startLine = h.StartLine,
            endLine = h.EndLine,
            score = Math.Round(h.Score, 3),
            snippet = h.Content.Length > MaxSnippetChars ? h.Content[..MaxSnippetChars] : h.Content,
        });

        return JsonSerializer.Serialize(projected, JsonOptions);
    }
}
