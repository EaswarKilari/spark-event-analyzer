namespace SparkEventAnalyzer.Services;

/// <summary>
/// Configuration for the code-search tool (the "CodeSearch" section). Points the agent at the
/// Azure AI Search index built by the sibling CodeIndexer tool (hybrid vector + semantic search
/// over the indexed source), plus the Azure OpenAI embedding deployment used to embed the query
/// text (the index has no integrated vectorizer, so the query must be embedded here).
///
/// Auth: if a key is supplied (config or the CODEINDEXER_* env vars) it is used; otherwise the
/// shared <see cref="Azure.Core.TokenCredential"/> (Managed Identity / az login) is used. For
/// Managed Identity, the app identity needs "Search Index Data Reader" on the Search service and
/// "Cognitive Services OpenAI User" on the AI resource.
/// </summary>
public sealed class CodeSearchOptions
{
    public const string SectionName = "CodeSearch";

    /// <summary>Azure AI Search endpoint, e.g. https://&lt;svc&gt;.search.windows.net</summary>
    public string SearchEndpoint { get; set; } = string.Empty;

    /// <summary>Index name produced by CodeIndexer.</summary>
    public string IndexName { get; set; } = "code-index";

    /// <summary>Azure OpenAI endpoint hosting the embedding deployment.</summary>
    public string EmbeddingEndpoint { get; set; } = string.Empty;

    /// <summary>Embedding deployment name — must match how the index was built.</summary>
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-large";

    /// <summary>Embedding dimensions — must match the index's vector field.</summary>
    public int Dimensions { get; set; } = 1536;

    /// <summary>Default number of results when the caller does not specify one.</summary>
    public int DefaultK { get; set; } = 8;

    /// <summary>Optional Search query/admin key. Prefer the CODEINDEXER_SEARCH_KEY env var.</summary>
    public string? SearchKey { get; set; }

    /// <summary>Optional Azure OpenAI key. Prefer the CODEINDEXER_AOAI_KEY env var.</summary>
    public string? EmbeddingKey { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SearchEndpoint) && !string.IsNullOrWhiteSpace(EmbeddingEndpoint);
}
