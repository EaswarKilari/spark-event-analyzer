using System.Text.Json.Serialization;

namespace CodeIndexer;

/// <summary>Strongly-typed configuration bound from appsettings.json + environment variables.</summary>
public sealed class IndexerConfig
{
    public SearchConfig Search { get; set; } = new();
    public EmbeddingsConfig Embeddings { get; set; } = new();
    public ChunkingConfig Chunking { get; set; } = new();

    /// <summary>
    /// Search admin key. Never stored in appsettings — read from env var CODEINDEXER_SEARCH_KEY.
    /// If empty, the tool falls back to DefaultAzureCredential (requires Search RBAC roles).
    /// </summary>
    public string? SearchAdminKey { get; set; }
}

public sealed class SearchConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string IndexName { get; set; } = "code-index";
}

public sealed class EmbeddingsConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string Deployment { get; set; } = "text-embedding-3-large";
    public int Dimensions { get; set; } = 1536;
}

public sealed class ChunkingConfig
{
    public int MaxTokens { get; set; } = 1200;
    public int OverlapTokens { get; set; } = 150;
    public long MaxFileBytes { get; set; } = 1_048_576;
}

/// <summary>One chunk of a source file, plus the metadata that becomes a Search document.</summary>
public sealed class CodeChunk
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string Language { get; init; }
    public required string Symbol { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }
    public int TokenCount { get; init; }
    public float[]? Vector { get; set; }
}

/// <summary>Run metrics written to report.json and printed at the end of an index run.</summary>
public sealed class RunReport
{
    public string Repo { get; set; } = "my-repo";
    public string EmbeddingModel { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public double DurationSeconds { get; set; }

    public int FilesScanned { get; set; }
    public int FilesIndexed { get; set; }
    public int FilesSkipped { get; set; }
    public Dictionary<string, int> SkipReasons { get; set; } = new();
    public Dictionary<string, LanguageStat> ByLanguage { get; set; } = new();

    public int TotalChunks { get; set; }
    public long TotalInputTokens { get; set; }
    public int MaxChunkTokens { get; set; }
    public int EmbeddingBatches { get; set; }
    public int Retries429 { get; set; }

    public long EstimatedIndexSizeMB { get; set; }
    public double EstimatedCostUsd { get; set; }

    public int DocumentsUploaded { get; set; }
    public int DocumentsFailed { get; set; }

    public double AvgChunkTokens => TotalChunks == 0 ? 0 : Math.Round((double)TotalInputTokens / TotalChunks, 1);
    public double TokensPerSecond => DurationSeconds <= 0 ? 0 : Math.Round(TotalInputTokens / DurationSeconds, 0);
}

public sealed class LanguageStat
{
    public int Files { get; set; }
    public int Chunks { get; set; }
    public long Tokens { get; set; }
}

/// <summary>Incremental-index manifest: file path -> content hash + chunk count.</summary>
public sealed class Manifest
{
    public Dictionary<string, ManifestEntry> Files { get; set; } = new();
}

public sealed class ManifestEntry
{
    public required string Sha256 { get; set; }
    public int Chunks { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RunReport))]
[JsonSerializable(typeof(Manifest))]
internal partial class JsonContext : JsonSerializerContext;
