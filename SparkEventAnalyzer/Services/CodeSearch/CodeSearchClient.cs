using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;

namespace SparkEventAnalyzer.Services;

/// <summary>
/// Queries the CodeIndexer-built Azure AI Search index using the same hybrid (keyword + vector)
/// query with semantic reranking as the indexer's own <c>search</c> command. Because the index has
/// no integrated vectorizer, the query text is embedded here with Azure OpenAI and passed as a
/// vector query alongside the keyword text.
/// </summary>
public sealed class CodeSearchClient
{
    // Must match the names the CodeIndexer used when it created the index.
    private const string SemanticConfig = "semantic";
    private const string VectorField = "contentVector";

    private static readonly string[] SelectFields =
        { "filePath", "symbol", "language", "startLine", "endLine", "content" };

    private readonly CodeSearchOptions _options;
    private readonly ILogger<CodeSearchClient> _logger;
    private readonly SearchClient? _searchClient;
    private readonly EmbeddingClient? _embeddingClient;

    public CodeSearchClient(IOptions<CodeSearchOptions> options, TokenCredential credential, ILogger<CodeSearchClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!_options.IsConfigured)
            return;

        // Search: key (config or env) if present, else Managed Identity / az login.
        var searchKey = FirstNonEmpty(_options.SearchKey, Environment.GetEnvironmentVariable("CODEINDEXER_SEARCH_KEY"));
        var searchEndpoint = new Uri(_options.SearchEndpoint);
        _searchClient = string.IsNullOrWhiteSpace(searchKey)
            ? new SearchClient(searchEndpoint, _options.IndexName, credential)
            : new SearchClient(searchEndpoint, _options.IndexName, new AzureKeyCredential(searchKey));

        // Embeddings: same key/credential pattern.
        var aoaiKey = FirstNonEmpty(_options.EmbeddingKey, Environment.GetEnvironmentVariable("CODEINDEXER_AOAI_KEY"));
        var aoaiEndpoint = new Uri(_options.EmbeddingEndpoint);
        AzureOpenAIClient aoai = string.IsNullOrWhiteSpace(aoaiKey)
            ? new AzureOpenAIClient(aoaiEndpoint, credential)
            : new AzureOpenAIClient(aoaiEndpoint, new AzureKeyCredential(aoaiKey));
        _embeddingClient = aoai.GetEmbeddingClient(_options.EmbeddingDeployment);
    }

    public bool IsConfigured => _options.IsConfigured;

    public int DefaultK => _options.DefaultK;

    /// <summary>Hybrid (keyword + vector) query with semantic reranking, optionally filtered by language.</summary>
    public async Task<IReadOnlyList<CodeSearchHit>> SearchAsync(string query, int k, string? language, CancellationToken ct)
    {
        if (_searchClient is null || _embeddingClient is null)
        {
            throw new InvalidOperationException(
                "CodeSearch is not configured. Set CodeSearch:SearchEndpoint and CodeSearch:EmbeddingEndpoint.");
        }

        // Embed the query text (the index has no integrated vectorizer).
        var embedOptions = new EmbeddingGenerationOptions { Dimensions = _options.Dimensions };
        OpenAIEmbeddingCollection embeddings = await _embeddingClient.GenerateEmbeddingsAsync(new[] { query }, embedOptions, ct);
        float[] queryVector = embeddings[0].ToFloats().ToArray();

        var options = new SearchOptions
        {
            Size = k,
            QueryType = SearchQueryType.Semantic,
            VectorSearch = new VectorSearchOptions
            {
                Queries = { new VectorizedQuery(queryVector) { KNearestNeighborsCount = k, Fields = { VectorField } } },
            },
            SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = SemanticConfig },
        };
        if (!string.IsNullOrWhiteSpace(language))
            options.Filter = $"language eq '{language.Replace("'", "''")}'";
        foreach (var field in SelectFields)
            options.Select.Add(field);

        var response = await _searchClient.SearchAsync<SearchDocument>(query, options, ct);

        var hits = new List<CodeSearchHit>();
        await foreach (var r in response.Value.GetResultsAsync())
        {
            var d = r.Document;
            hits.Add(new CodeSearchHit(
                (string)d["filePath"],
                (string)d["symbol"],
                (string)d["language"],
                Convert.ToInt32(d["startLine"]),
                Convert.ToInt32(d["endLine"]),
                r.SemanticSearch?.RerankerScore ?? r.Score ?? 0,
                (string)d["content"]));
        }

        _logger.LogInformation("Code search '{Query}' returned {Count} hits", query, hits.Count);
        return hits;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>One code-search result: a source chunk plus its location and relevance score.</summary>
public sealed record CodeSearchHit(
    string FilePath, string Symbol, string Language, int StartLine, int EndLine, double Score, string Content);
