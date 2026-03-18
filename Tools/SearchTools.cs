using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StewardMcp.Data;

namespace StewardMcp.Tools;

[McpServerToolType]
public class SearchTools
{
    private readonly VectorStore _vectorStore;

    public SearchTools(VectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    [McpServerTool]
    [Description("Search memory (journals and reflections) and workspace files by semantic similarity. Returns the most relevant matches.")]
    public async Task<string> MemorySearch(
        [Description("Search text — describe what you're looking for")] string query,
        [Description("Scope: 'journals', 'files', or 'all' (default 'all')")] string scope = "all",
        [Description("Limit to a specific thread ID (optional)")] string? threadId = null,
        [Description("Max results (default 5)")] int limit = 5)
    {
        var results = new List<SearchResult>();

        if (scope is "journals" or "all")
        {
            var journalResults = await _vectorStore.QueryJournalsAsync(query, threadId, limit);
            results.AddRange(journalResults);
        }

        if (scope is "files" or "all")
        {
            var fileResults = await _vectorStore.QueryFilesAsync(query, limit);
            results.AddRange(fileResults);
        }

        // Sort by score descending, take top N
        var sorted = results.OrderByDescending(r => r.Score).Take(limit).Select(r => new
        {
            id = r.Id,
            source = r.Source,
            threadId = r.ThreadId,
            content = r.Content.Length > 500 ? r.Content[..500] + "..." : r.Content,
            score = Math.Round(r.Score, 3),
            category = r.Category,
        }).ToList();

        return JsonSerializer.Serialize(new { query, resultCount = sorted.Count, results = sorted });
    }
}
