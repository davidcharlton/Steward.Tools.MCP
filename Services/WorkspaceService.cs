using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using StewardMcp.Config;
using StewardMcp.Data;

namespace StewardMcp.Services;

public class WorkspaceService
{
    private readonly StewardConfig _config;
    private readonly VectorStore _vectorStore;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(StewardConfig config, VectorStore vectorStore, ILogger<WorkspaceService> logger)
    {
        _config = config;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    private string ResolvePath(string relativePath)
    {
        var root = Path.GetFullPath(_config.WorkspaceDir);
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes workspace: {relativePath}");
        return resolved;
    }

    public string Tree(string path = "", int maxDepth = 3, bool includeHidden = false)
    {
        var root = ResolvePath(path);
        if (!Directory.Exists(root)) return $"Directory not found: {path}";

        var sb = new StringBuilder();
        BuildTree(sb, root, "", maxDepth, 0, includeHidden);
        return sb.ToString();
    }

    private void BuildTree(StringBuilder sb, string dir, string prefix, int maxDepth, int depth, bool includeHidden)
    {
        if (depth >= maxDepth) return;

        var entries = new List<string>();
        try
        {
            entries.AddRange(Directory.GetDirectories(dir));
            entries.AddRange(Directory.GetFiles(dir));
        }
        catch { return; }

        entries.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!includeHidden && name.StartsWith('.')) continue;

            var isDir = Directory.Exists(entry);
            sb.AppendLine($"{prefix}{(isDir ? name + "/" : name)}");
            if (isDir) BuildTree(sb, entry, prefix + "  ", maxDepth, depth + 1, includeHidden);
        }
    }

    public List<string> List(string path = "", bool recursive = false, bool includeHidden = false, int maxItems = 2000)
    {
        var root = ResolvePath(path);
        if (!Directory.Exists(root)) return [$"Directory not found: {path}"];

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var rootFull = Path.GetFullPath(_config.WorkspaceDir);

        return Directory.EnumerateFileSystemEntries(root, "*", option)
            .Where(e => includeHidden || !Path.GetFileName(e).StartsWith('.'))
            .Take(maxItems)
            .Select(e =>
            {
                var rel = Path.GetRelativePath(rootFull, e);
                return Directory.Exists(e) ? rel + "/" : rel;
            })
            .ToList();
    }

    public (string content, string sha256) ReadLines(string path, int startLine = 1, int maxLines = 200, bool includeLineNumbers = true)
    {
        var resolved = ResolvePath(path);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"File not found: {path}");

        var allLines = File.ReadAllLines(resolved);
        var sha256 = ComputeSha256(File.ReadAllText(resolved));
        var selected = allLines.Skip(startLine - 1).Take(maxLines);

        var sb = new StringBuilder();
        int lineNum = startLine;
        foreach (var line in selected)
        {
            sb.AppendLine(includeLineNumbers ? $"{lineNum,5}: {line}" : line);
            lineNum++;
        }

        return (sb.ToString(), sha256);
    }

    public async Task<string> WriteAsync(string path, string content, bool overwrite = false, bool makeDirs = true)
    {
        var resolved = ResolvePath(path);
        if (File.Exists(resolved) && !overwrite)
            return $"File already exists: {path}. Set overwrite=true to replace.";

        if (makeDirs)
        {
            var dir = Path.GetDirectoryName(resolved);
            if (dir != null) Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(resolved, content);

        // Auto-vectorize
        _ = Task.Run(async () =>
        {
            try { await _vectorStore.UpsertFileEmbeddingAsync(path, content, fileHash: ComputeSha256(content)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to vectorize {Path}", path); }
        });

        return $"Written: {path} ({content.Length} chars)";
    }

    public async Task<string> PatchAsync(string path, int startLine, int endLine, string replacement, string? expectedSha256 = null)
    {
        var resolved = ResolvePath(path);
        if (!File.Exists(resolved))
            return $"File not found: {path}";

        var fullText = await File.ReadAllTextAsync(resolved);
        if (expectedSha256 != null)
        {
            var actual = ComputeSha256(fullText);
            if (actual != expectedSha256)
                return $"SHA256 mismatch — file was modified. Expected: {expectedSha256}, Actual: {actual}";
        }

        var lines = fullText.Split('\n').ToList();
        var start = Math.Max(0, startLine - 1);
        var end = Math.Min(lines.Count, endLine);
        var replacementLines = replacement.Split('\n');

        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, replacementLines);

        var newContent = string.Join('\n', lines);
        await File.WriteAllTextAsync(resolved, newContent);

        // Auto-vectorize
        _ = Task.Run(async () =>
        {
            try { await _vectorStore.UpsertFileEmbeddingAsync(path, newContent, fileHash: ComputeSha256(newContent)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to vectorize {Path}", path); }
        });

        return $"Patched: {path} (lines {startLine}-{endLine} replaced)";
    }

    public List<SearchMatch> Search(string query, string path = "", int maxResults = 20)
    {
        var root = ResolvePath(path);
        if (!Directory.Exists(root)) return [];

        var rootFull = Path.GetFullPath(_config.WorkspaceDir);
        var results = new List<SearchMatch>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).StartsWith('.')) continue;
            if (results.Count >= maxResults) break;

            try
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new SearchMatch
                        {
                            Path = Path.GetRelativePath(rootFull, file),
                            Line = i + 1,
                            Content = lines[i].Trim(),
                        });
                        if (results.Count >= maxResults) break;
                    }
                }
            }
            catch { /* skip binary/unreadable files */ }
        }

        return results;
    }

    public async Task<string> DeleteAsync(string path)
    {
        var resolved = ResolvePath(path);
        if (!File.Exists(resolved))
            return $"File not found: {path}";
        if (Directory.Exists(resolved))
            return "Cannot delete directories";

        File.Delete(resolved);

        // Remove from vector store
        _ = Task.Run(async () =>
        {
            try { await _vectorStore.DeleteFileEmbeddingAsync(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove embedding for {Path}", path); }
        });

        return $"Deleted: {path}";
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}

public class SearchMatch
{
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string Content { get; set; } = "";
}
