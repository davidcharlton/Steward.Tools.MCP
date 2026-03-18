using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StewardMcp.Data;
using StewardMcp.Formation;

namespace StewardMcp.Tools;

[McpServerToolType]
public class MemoryTools
{
    private readonly StewardDb _db;
    private readonly Scripture _scripture;

    public MemoryTools(StewardDb db, Scripture scripture)
    {
        _db = db;
        _scripture = scripture;
    }

    [McpServerTool]
    [Description("Get the working memory dossier for a conversation thread. Use 'master_dossier' for cross-thread awareness, 'scripture_dossier' for Scripture insights, or a thread ID for a specific conversation.")]
    public async Task<string> MemoryGetDossier(
        [Description("Thread ID, 'master_dossier', or 'scripture_dossier'")] string threadId)
    {
        var profile = await _db.GetThreadProfileAsync(threadId);
        if (profile == null)
            return JsonSerializer.Serialize(new { status = "not_found", threadId });

        return JsonSerializer.Serialize(new
        {
            threadId = profile.ThreadId,
            summary = profile.Summary,
            keyPoints = profile.KeyPoints,
            openLoops = profile.OpenLoops,
            tags = profile.Tags,
            updatedTs = profile.UpdatedTs,
        });
    }

    [McpServerTool]
    [Description("Get recent reflections from the memory tree at a specific level. L1 = recent summaries, L2+ = deeper patterns.")]
    public async Task<string> MemoryGetReflections(
        [Description("Thread ID")] string threadId,
        [Description("Reflection level (1-12), default 1")] int level = 1,
        [Description("Max results, default 3")] int limit = 3)
    {
        var events = await _db.GetThreadEventsAsync(threadId, mode: "reflection", level: level, limit: limit);

        var reflections = events.Select(e =>
        {
            Dictionary<string, JsonElement>? payload = null;
            if (!string.IsNullOrEmpty(e.PayloadJson))
            {
                try { payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.PayloadJson); }
                catch { }
            }

            return new
            {
                id = e.Id,
                level = e.Level,
                ts = e.Ts,
                summary = payload != null && payload.TryGetValue("summary", out var s) ? s.GetString() : e.Content,
                tags = payload != null && payload.TryGetValue("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString()).ToList()
                    : new List<string?>(),
            };
        }).ToList();

        return JsonSerializer.Serialize(new { threadId, level, reflections });
    }

    [McpServerTool]
    [Description("Get recent conversation entries (L0 chat events) from a thread.")]
    public async Task<string> MemoryGetJournal(
        [Description("Thread ID")] string threadId,
        [Description("Max entries, default 10")] int limit = 10)
    {
        var events = await _db.GetThreadEventsAsync(threadId, mode: "chat", level: 0, limit: limit);

        var entries = events.Select(e => new
        {
            id = e.Id,
            role = e.Role,
            content = e.Content?.Length > 500 ? e.Content[..500] + "..." : e.Content,
            ts = e.Ts,
        }).ToList();

        return JsonSerializer.Serialize(new { threadId, entries });
    }

    [McpServerTool]
    [Description("Get the source entries that a reflection was built from. Use this to drill down from a search result or reflection to the raw material it summarized.")]
    public async Task<string> MemoryGetSources(
        [Description("Journal ID of the reflection entry")] long reflectionId)
    {
        var sources = await _db.GetSourcesForReflectionAsync(reflectionId);

        var entries = sources.Select(e => new
        {
            id = e.Id,
            level = e.Level,
            role = e.Role,
            content = e.Content?.Length > 1000 ? e.Content[..1000] + "..." : e.Content,
            ts = e.Ts,
        }).ToList();

        return JsonSerializer.Serialize(new { reflectionId, sourceCount = entries.Count, sources = entries });
    }

    [McpServerTool]
    [Description("Get current Scripture reading position and recent readings.")]
    public async Task<string> MemoryScriptureStatus()
    {
        var status = _scripture.GetStatus();
        var recentReadings = await _scripture.GetRecentReadingsAsync();

        return JsonSerializer.Serialize(new
        {
            currentPosition = status.CurrentPosition,
            totalChaptersRead = status.TotalChaptersRead,
            totalChapters = status.TotalChapters,
            recentReadings,
        });
    }
}
