using System.ClientModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace CodeIndexer;

internal static class Program
{
    private const double PricePer1MTokens = 0.13; // text-embedding-3-large

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var config = LoadConfig();
        var credential = new DefaultAzureCredential();
        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "init" => await InitAsync(config, credential),
                "index" => await IndexAsync(config, credential, args),
                "search" => await SearchAsync(config, credential, args),
                _ => Fail($"Unknown command '{command}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> InitAsync(IndexerConfig config, DefaultAzureCredential credential)
    {
        Console.WriteLine($"Creating/updating index '{config.Search.IndexName}' on {config.Search.Endpoint} ...");
        var store = new SearchStore(config, credential);
        await store.CreateOrUpdateIndexAsync(CancellationToken.None);
        Console.WriteLine("Index ready (HNSW + scalar quantization + semantic ranker).");
        return 0;
    }

    private static async Task<int> IndexAsync(IndexerConfig config, DefaultAzureCredential credential, string[] args)
    {
        var root = GetOption(args, "--root") ?? @"c:\your-repo";
        var pathsCsv = GetOption(args, "--paths");
        var subPaths = pathsCsv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var maxFiles = int.TryParse(GetOption(args, "--max"), out var mf) ? mf : int.MaxValue;
        var batchSize = int.TryParse(GetOption(args, "--batch"), out var bs) ? bs : 32;
        var dump = HasFlag(args, "--dump");
        var incremental = HasFlag(args, "--incremental");

        var outDir = Path.Combine(AppContext.BaseDirectory, ".codeindex");
        Directory.CreateDirectory(outDir);
        var manifest = incremental ? LoadManifest(outDir) : new Manifest();

        var tokens = new TokenCounter();
        var chunker = new Chunker(tokens, config.Chunking);
        var embedder = CreateEmbedder(config, credential);
        var store = new SearchStore(config, credential);

        var report = new RunReport
        {
            EmbeddingModel = config.Embeddings.Deployment,
            Dimensions = config.Embeddings.Dimensions,
        };
        var sw = Stopwatch.StartNew();

        // 1) Enumerate + chunk.
        Console.WriteLine($"Scanning {root}{(subPaths is null ? "" : " [" + string.Join(", ", subPaths) + "]")} ...");
        var allChunks = new List<CodeChunk>();
        var files = FileWalker.Enumerate(root, subPaths, config.Chunking.MaxFileBytes,
            reason => { report.FilesSkipped++; report.SkipReasons[reason] = report.SkipReasons.GetValueOrDefault(reason) + 1; });

        foreach (var file in files)
        {
            if (report.FilesIndexed >= maxFiles) break;
            report.FilesScanned++;

            string text;
            try { text = File.ReadAllText(file); }
            catch { report.FilesSkipped++; report.SkipReasons["ioerror"] = report.SkipReasons.GetValueOrDefault("ioerror") + 1; continue; }

            var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var sha = Sha256(text);

            if (incremental && manifest.Files.TryGetValue(relPath, out var prev) && prev.Sha256 == sha)
                continue; // unchanged; keep existing chunks

            var language = LanguageMap.FromPath(file);
            var fileChunks = chunker.Chunk(relPath, language, text).ToList();
            if (fileChunks.Count == 0) continue;

            report.FilesIndexed++;
            var lang = report.ByLanguage.TryGetValue(language, out var ls) ? ls : (report.ByLanguage[language] = new LanguageStat());
            lang.Files++;
            lang.Chunks += fileChunks.Count;
            lang.Tokens += fileChunks.Sum(c => (long)c.TokenCount);

            manifest.Files[relPath] = new ManifestEntry { Sha256 = sha, Chunks = fileChunks.Count };
            allChunks.AddRange(fileChunks);
        }

        report.TotalChunks = allChunks.Count;
        report.TotalInputTokens = allChunks.Sum(c => (long)c.TokenCount);
        report.MaxChunkTokens = allChunks.Count == 0 ? 0 : allChunks.Max(c => c.TokenCount);
        Console.WriteLine($"Chunks: {report.TotalChunks:N0} from {report.FilesIndexed:N0} files ({report.TotalInputTokens:N0} tokens).");

        if (allChunks.Count == 0)
        {
            Console.WriteLine("Nothing to index.");
            return 0;
        }

        // 2+3) Embed and upload in bounded-parallel batches. Streaming: documents appear in the
        // index progressively (not only at the end), and a mid-run failure keeps prior work.
        var parallelism = int.TryParse(GetOption(args, "--parallel"), out var pp) ? pp : 6;
        Console.WriteLine($"Embedding with {config.Embeddings.Deployment} @ {config.Embeddings.Dimensions} dims + uploading (parallel={parallelism}) ...");

        var batches = new List<List<CodeChunk>>();
        for (var i = 0; i < allChunks.Count; i += batchSize)
            batches.Add(allChunks.Skip(i).Take(batchSize).ToList());

        int processed = 0, uploaded = 0, failedDocs = 0, batchesDone = 0;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = CancellationToken.None };

        await Parallel.ForEachAsync(batches, parallelOptions, async (batch, token) =>
        {
            var vectors = await embedder.EmbedBatchAsync(batch.Select(c => c.Content).ToList(), token);
            for (var j = 0; j < batch.Count; j++) batch[j].Vector = vectors[j];

            var (o, f) = await store.UploadAsync(batch, batch.Count, token);
            Interlocked.Add(ref uploaded, o);
            Interlocked.Add(ref failedDocs, f);
            Interlocked.Increment(ref batchesDone);
            var done = Interlocked.Add(ref processed, batch.Count);
            Console.Write($"\r  embedded+uploaded {done:N0}/{allChunks.Count:N0}  (batch {batchesDone}/{batches.Count})   ");
        });
        Console.WriteLine();

        report.EmbeddingBatches = batchesDone;
        report.Retries429 = embedder.Retries;
        report.DocumentsUploaded = uploaded;
        report.DocumentsFailed = failedDocs;

        // 4) Optional local snapshot.
        if (dump)
        {
            var dumpPath = Path.Combine(outDir, "chunks.jsonl");
            await using var w = new StreamWriter(dumpPath, append: false);
            foreach (var c in allChunks)
            {
                var line = JsonSerializer.Serialize(new
                {
                    c.Id, c.FilePath, c.Language, c.Symbol, c.StartLine, c.EndLine, c.ChunkIndex, c.Content, c.Vector,
                });
                await w.WriteLineAsync(line);
            }
            Console.WriteLine($"Wrote snapshot: {dumpPath}");
        }

        // 5) Metrics + manifest.
        sw.Stop();
        report.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        report.EstimatedCostUsd = Math.Round(report.TotalInputTokens / 1_000_000.0 * PricePer1MTokens, 4);
        report.EstimatedIndexSizeMB = (long)Math.Round(report.TotalChunks * (double)config.Embeddings.Dimensions * 4 / 1_048_576);

        SaveManifest(outDir, manifest);
        var reportPath = Path.Combine(outDir, "report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonContext.Default.RunReport));

        PrintReport(report, reportPath);
        return failedDocs == 0 ? 0 : 2;
    }

    private static async Task<int> SearchAsync(IndexerConfig config, DefaultAzureCredential credential, string[] args)
    {
        var query = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrWhiteSpace(query)) return Fail("Provide a query: search \"how does scheduling work\" [--k 8]");
        var k = int.TryParse(GetOption(args, "--k"), out var kk) ? kk : 8;

        var embedder = CreateEmbedder(config, credential);
        var store = new SearchStore(config, credential);

        var qv = (await embedder.EmbedBatchAsync(new[] { query }, CancellationToken.None))[0];
        var hits = await store.SearchAsync(query, qv, k, CancellationToken.None);

        Console.WriteLine($"\nTop {hits.Count} results for: {query}\n");
        foreach (var h in hits)
        {
            Console.WriteLine($"  [{h.Score:F3}] {h.FilePath}#L{h.StartLine}-L{h.EndLine}  ({h.Symbol}, {h.Language})");
            var preview = h.Content.Split('\n').Skip(1).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            if (preview.Length > 120) preview = preview[..120] + "…";
            Console.WriteLine($"        {preview}");
        }
        return 0;
    }

    // ---- helpers ----

    private static EmbeddingGenerator CreateEmbedder(IndexerConfig cfg, DefaultAzureCredential credential)
    {
        // Prefer an AOAI API key (env CODEINDEXER_AOAI_KEY) for long batch runs — avoids az-CLI
        // token timeouts. Falls back to DefaultAzureCredential when the key is not set.
        var aoaiKey = Environment.GetEnvironmentVariable("CODEINDEXER_AOAI_KEY");
        var aoai = !string.IsNullOrWhiteSpace(aoaiKey)
            ? new AzureOpenAIClient(new Uri(cfg.Embeddings.Endpoint), new ApiKeyCredential(aoaiKey))
            : new AzureOpenAIClient(new Uri(cfg.Embeddings.Endpoint), credential);
        return new EmbeddingGenerator(aoai, cfg.Embeddings.Deployment, cfg.Embeddings.Dimensions);
    }

    private static IndexerConfig LoadConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables("CODEINDEXER_")
            .Build();

        var cfg = new IndexerConfig();
        configuration.Bind(cfg);
        // Secret comes only from env (never appsettings): CODEINDEXER_SEARCH_KEY.
        cfg.SearchAdminKey = Environment.GetEnvironmentVariable("CODEINDEXER_SEARCH_KEY");
        return cfg;
    }

    private static string? GetOption(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static string Sha256(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static Manifest LoadManifest(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path)) return new Manifest();
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), JsonContext.Default.Manifest) ?? new Manifest(); }
        catch { return new Manifest(); }
    }

    private static void SaveManifest(string dir, Manifest manifest)
        => File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonContext.Default.Manifest));

    private static void PrintReport(RunReport r, string path)
    {
        Console.WriteLine();
        Console.WriteLine("================ Index Run Report ================");
        Console.WriteLine($"  Files scanned/indexed/skipped : {r.FilesScanned:N0} / {r.FilesIndexed:N0} / {r.FilesSkipped:N0}");
        Console.WriteLine($"  Chunks                        : {r.TotalChunks:N0}  (avg {r.AvgChunkTokens} tok, max {r.MaxChunkTokens})");
        Console.WriteLine($"  Input tokens                  : {r.TotalInputTokens:N0}");
        Console.WriteLine($"  Embedding model / dimensions  : {r.EmbeddingModel} / {r.Dimensions}");
        Console.WriteLine($"  Embedding batches / 429 retries: {r.EmbeddingBatches:N0} / {r.Retries429}");
        Console.WriteLine($"  Uploaded / failed             : {r.DocumentsUploaded:N0} / {r.DocumentsFailed:N0}");
        Console.WriteLine($"  Est. index size (raw vectors) : ~{r.EstimatedIndexSizeMB:N0} MB");
        Console.WriteLine($"  Est. embedding cost           : ${r.EstimatedCostUsd}");
        Console.WriteLine($"  Duration                      : {r.DurationSeconds}s  ({r.TokensPerSecond:N0} tok/s)");
        if (r.ByLanguage.Count > 0)
        {
            Console.WriteLine("  By language:");
            foreach (var (lang, s) in r.ByLanguage.OrderByDescending(kv => kv.Value.Chunks))
                Console.WriteLine($"      {lang,-12} files={s.Files,-6} chunks={s.Chunks,-7} tokens={s.Tokens:N0}");
        }
        Console.WriteLine($"  Report: {path}");
        Console.WriteLine("==================================================");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        CodeIndexer — embed a code repo into Azure AI Search.

        Commands:
          init                                  Create/update the Search index.
          index [--root <path>] [--paths a,b]   Chunk + embed + upload.
                [--max N] [--batch 32]
                [--dump] [--incremental]
          search "<query>" [--k 8]              Hybrid + semantic search.

        Auth:
          Embeddings use DefaultAzureCredential (az login).
          Search uses env CODEINDEXER_SEARCH_KEY if set, else DefaultAzureCredential.
        """);
    }
}
