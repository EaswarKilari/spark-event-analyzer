using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace CodeIndexer;

/// <summary>Enumerates indexable source files under a root, applying include/exclude rules.</summary>
public static class FileWalker
{
    // Extensions we index (code + docs). Everything else is skipped.
    private static readonly HashSet<string> IncludeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".scala", ".py", ".ts", ".tsx", ".js", ".jsx", ".sql", ".ps1", ".psm1", ".md", ".java", ".go"
    };

    // Directory names that are never indexed (build output, deps, VCS, generated).
    private static readonly HashSet<string> ExcludeDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "objd", "packages", "node_modules", ".git", ".vs", "TestResults",
        "target", "dist", "build", ".idea", "wwwroot"
    };

    // Filename suffixes that indicate generated/minified files.
    private static readonly string[] ExcludeSuffixes =
    {
        ".g.cs", ".designer.cs", ".g.i.cs", ".assemblyinfo.cs", ".min.js", ".min.css", ".d.ts"
    };

    public static IEnumerable<string> Enumerate(string root, IReadOnlyList<string>? subPaths, long maxBytes, Action<string> onSkip)
    {
        var roots = (subPaths is { Count: > 0 })
            ? subPaths.Select(p => Path.GetFullPath(Path.Combine(root, p)))
            : new[] { Path.GetFullPath(root) };

        foreach (var start in roots)
        {
            if (!Directory.Exists(start) && !File.Exists(start))
            {
                onSkip("missing");
                continue;
            }

            foreach (var file in Walk(start))
            {
                var ext = Path.GetExtension(file);
                if (!IncludeExtensions.Contains(ext)) { onSkip("extension"); continue; }

                var name = Path.GetFileName(file);
                if (ExcludeSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    onSkip("generated");
                    continue;
                }

                long size;
                try { size = new FileInfo(file).Length; }
                catch { onSkip("ioerror"); continue; }

                if (size == 0) { onSkip("empty"); continue; }
                if (size > maxBytes) { onSkip("tooLarge"); continue; }

                yield return file;
            }
        }
    }

    private static IEnumerable<string> Walk(string start)
    {
        if (File.Exists(start))
        {
            yield return start;
            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { continue; }

            foreach (var sub in subDirs)
            {
                if (!ExcludeDirs.Contains(Path.GetFileName(sub)))
                    stack.Push(sub);
            }

            foreach (var f in files)
                yield return f;
        }
    }
}

/// <summary>Maps file extensions to a short language id.</summary>
public static class LanguageMap
{
    public static string FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".scala" => "scala",
        ".py" => "python",
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript",
        ".java" => "java",
        ".go" => "go",
        ".sql" => "sql",
        ".ps1" or ".psm1" => "powershell",
        ".md" => "markdown",
        _ => "text",
    };
}

/// <summary>Accurate cl100k_base token counting (offline via the Data.Cl100kBase package).</summary>
public sealed class TokenCounter
{
    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text);
}

/// <summary>
/// Splits a source file into token-bounded chunks with overlap, carrying best-effort symbol
/// context. Each chunk's content is prefixed with a small header so the embedding has locality.
/// </summary>
public sealed class Chunker
{
    private readonly TokenCounter _tokens;
    private readonly int _maxTokens;
    private readonly int _overlapTokens;

    public Chunker(TokenCounter tokens, ChunkingConfig cfg)
    {
        _tokens = tokens;
        _maxTokens = cfg.MaxTokens;
        _overlapTokens = cfg.OverlapTokens;
    }

    private static readonly Dictionary<string, Regex[]> DeclRegexes = new()
    {
        ["csharp"] = new[]
        {
            new Regex(@"\b(?:class|interface|struct|record|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled),
            new Regex(@"\b(?:public|private|protected|internal|static|async|virtual|override|sealed)[\w\s<>,\[\]\?]*?\s([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled),
        },
        ["scala"] = new[] { new Regex(@"\b(?:class|object|trait|def|val|var)\s+([A-Za-z_]\w*)", RegexOptions.Compiled) },
        ["python"] = new[] { new Regex(@"^\s*(?:def|class)\s+([A-Za-z_]\w*)", RegexOptions.Compiled) },
        ["typescript"] = new[] { new Regex(@"\b(?:class|interface|function|const|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled) },
        ["javascript"] = new[] { new Regex(@"\b(?:class|function|const)\s+([A-Za-z_]\w*)", RegexOptions.Compiled) },
        ["java"] = new[] { new Regex(@"\b(?:class|interface|enum)\s+([A-Za-z_]\w*)", RegexOptions.Compiled) },
        ["go"] = new[] { new Regex(@"\bfunc\s+(?:\([^)]*\)\s*)?([A-Za-z_]\w*)", RegexOptions.Compiled) },
        ["sql"] = new[] { new Regex(@"\b(?:CREATE|ALTER)\s+(?:PROCEDURE|FUNCTION|TABLE|VIEW)\s+([A-Za-z_\[\]\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase) },
        ["powershell"] = new[] { new Regex(@"\bfunction\s+([A-Za-z_][\w-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase) },
    };

    public IEnumerable<CodeChunk> Chunk(string relPath, string language, string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var regexes = DeclRegexes.GetValueOrDefault(language) ?? Array.Empty<Regex>();
        var fileStem = Path.GetFileNameWithoutExtension(relPath);

        var buffer = new List<string>();
        var lineTokens = new List<int>();
        var startLine = 1;
        var currentSymbol = fileStem;
        var symbolAtChunkStart = fileStem;
        var chunkIndex = 0;
        var running = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Track the nearest enclosing declaration as we scan.
            foreach (var rx in regexes)
            {
                var m = rx.Match(line);
                if (m.Success) { currentSymbol = m.Groups[1].Value; break; }
            }

            if (buffer.Count == 0)
                symbolAtChunkStart = currentSymbol;

            var t = _tokens.Count(line) + 1; // +1 approximates the newline
            buffer.Add(line);
            lineTokens.Add(t);
            running += t;

            if (running >= _maxTokens)
            {
                var endLine = startLine + buffer.Count - 1;
                yield return Build(relPath, language, symbolAtChunkStart, startLine, endLine, chunkIndex++, buffer);

                // Carry an overlap tail into the next chunk for context continuity.
                var (keepLines, keepTokens) = TakeOverlapTail(buffer, lineTokens);
                startLine = endLine - keepLines.Count + 1;
                buffer = keepLines;
                lineTokens = keepTokens;
                running = keepTokens.Sum();
            }
        }

        if (buffer.Count > 0 && buffer.Any(l => l.Trim().Length > 0))
        {
            var endLine = startLine + buffer.Count - 1;
            yield return Build(relPath, language, symbolAtChunkStart, startLine, endLine, chunkIndex, buffer);
        }
    }

    private (List<string> lines, List<int> tokens) TakeOverlapTail(List<string> buffer, List<int> lineTokens)
    {
        var keep = new List<string>();
        var keepTok = new List<int>();
        var total = 0;
        for (var i = buffer.Count - 1; i >= 0 && total < _overlapTokens; i--)
        {
            keep.Insert(0, buffer[i]);
            keepTok.Insert(0, lineTokens[i]);
            total += lineTokens[i];
        }
        return (keep, keepTok);
    }

    private CodeChunk Build(string relPath, string language, string symbol, int startLine, int endLine, int chunkIndex, List<string> body)
    {
        var header = $"// file: {relPath} | lang: {language} | symbol: {symbol} | lines: {startLine}-{endLine}";
        var text = header + "\n" + string.Join('\n', body);
        var id = Hash($"{relPath}#{chunkIndex}");
        return new CodeChunk
        {
            Id = id,
            FilePath = relPath,
            Language = language,
            Symbol = symbol,
            StartLine = startLine,
            EndLine = endLine,
            ChunkIndex = chunkIndex,
            Content = text,
            TokenCount = _tokens.Count(text),
        };
    }

    public static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
