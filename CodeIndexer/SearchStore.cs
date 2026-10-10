using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;

namespace CodeIndexer;

/// <summary>
/// Owns the Azure AI Search index: create/update its schema (HNSW vector + scalar quantization +
/// semantic ranker), upload chunk documents, and run hybrid + semantic queries.
/// </summary>
public sealed class SearchStore
{
    private const string VectorProfile = "hnsw-scalar";
    private const string VectorAlgorithm = "hnsw";
    private const string VectorCompression = "scalar";
    private const string SemanticConfig = "semantic";
    private const string VectorField = "contentVector";

    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly string _indexName;
    private readonly int _dimensions;

    public SearchStore(IndexerConfig cfg, TokenCredential credential)
    {
        _indexName = cfg.Search.IndexName;
        _dimensions = cfg.Embeddings.Dimensions;
        var endpoint = new Uri(cfg.Search.Endpoint);

        if (!string.IsNullOrWhiteSpace(cfg.SearchAdminKey))
        {
            var key = new AzureKeyCredential(cfg.SearchAdminKey);
            _indexClient = new SearchIndexClient(endpoint, key);
            _searchClient = new SearchClient(endpoint, _indexName, key);
        }
        else
        {
            _indexClient = new SearchIndexClient(endpoint, credential);
            _searchClient = new SearchClient(endpoint, _indexName, credential);
        }
    }

    public async Task CreateOrUpdateIndexAsync(CancellationToken ct)
    {
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SearchableField("content"),
            new SearchField(VectorField, SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = _dimensions,
                VectorSearchProfileName = VectorProfile,
            },
            new SearchableField("filePath") { IsFilterable = true, IsSortable = true },
            new SimpleField("repo", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("language", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SearchableField("symbol") { IsFilterable = true },
            new SimpleField("startLine", SearchFieldDataType.Int32) { IsFilterable = true },
            new SimpleField("endLine", SearchFieldDataType.Int32) { IsFilterable = true },
            new SimpleField("chunkIndex", SearchFieldDataType.Int32) { IsFilterable = true },
            new SimpleField("commit", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("lastIndexed", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
        };

        var index = new SearchIndex(_indexName)
        {
            Fields = fields,
            VectorSearch = new VectorSearch
            {
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(VectorAlgorithm)
                    {
                        Parameters = new HnswParameters
                        {
                            M = 4,
                            EfConstruction = 400,
                            EfSearch = 500,
                            Metric = VectorSearchAlgorithmMetric.Cosine,
                        },
                    },
                },
                Compressions = { new ScalarQuantizationCompression(VectorCompression) },
                Profiles =
                {
                    new VectorSearchProfile(VectorProfile, VectorAlgorithm) { CompressionName = VectorCompression },
                },
            },
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(SemanticConfig, new SemanticPrioritizedFields
                    {
                        TitleField = new SemanticField("symbol"),
                        ContentFields = { new SemanticField("content") },
                        KeywordsFields = { new SemanticField("filePath") },
                    }),
                },
            },
        };

        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
    }

    /// <summary>Uploads chunks (with vectors) in batches. Returns (succeeded, failed).</summary>
    public async Task<(int ok, int failed)> UploadAsync(IReadOnlyList<CodeChunk> chunks, int batchSize, CancellationToken ct)
    {
        int ok = 0, failed = 0;
        for (var i = 0; i < chunks.Count; i += batchSize)
        {
            var slice = chunks.Skip(i).Take(batchSize)
                .Select(c => new SearchDocument
                {
                    ["id"] = c.Id,
                    ["content"] = c.Content,
                    [VectorField] = c.Vector,
                    ["filePath"] = c.FilePath,
                    ["repo"] = "my-repo",
                    ["language"] = c.Language,
                    ["symbol"] = c.Symbol,
                    ["startLine"] = c.StartLine,
                    ["endLine"] = c.EndLine,
                    ["chunkIndex"] = c.ChunkIndex,
                    ["lastIndexed"] = DateTimeOffset.UtcNow,
                })
                .ToList();

            var batch = IndexDocumentsBatch.MergeOrUpload(slice);
            var result = await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);
            ok += result.Value.Results.Count(r => r.Succeeded);
            failed += result.Value.Results.Count(r => !r.Succeeded);
        }
        return (ok, failed);
    }

    /// <summary>Hybrid (keyword + vector) query with semantic reranking.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, float[] queryVector, int k, CancellationToken ct)
    {
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
        options.Select.Add("filePath");
        options.Select.Add("symbol");
        options.Select.Add("language");
        options.Select.Add("startLine");
        options.Select.Add("endLine");
        options.Select.Add("content");

        var response = await _searchClient.SearchAsync<SearchDocument>(query, options, ct);

        var hits = new List<SearchHit>();
        await foreach (var r in response.Value.GetResultsAsync())
        {
            var d = r.Document;
            hits.Add(new SearchHit(
                (string)d["filePath"],
                (string)d["symbol"],
                (string)d["language"],
                Convert.ToInt32(d["startLine"]),
                Convert.ToInt32(d["endLine"]),
                r.SemanticSearch?.RerankerScore ?? r.Score ?? 0,
                (string)d["content"]));
        }
        return hits;
    }
}

public sealed record SearchHit(string FilePath, string Symbol, string Language, int StartLine, int EndLine, double Score, string Content);
