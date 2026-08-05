using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StewardMcp.Data;
using StewardMcp.Formation;
using StewardMcp.Services;

namespace StewardMcp.Tools;

[McpServerToolType]
public class MemoryTools
{
    private readonly StewardDb _db;
    private readonly Scripture _scripture;
    private readonly DossierBuilder _dossiers;

    public MemoryTools(UserSteward user)
    {
        _db = user.Db;
        _scripture = user.Scripture;
        _dossiers = user.Dossiers;
    }

    [McpServerTool]
    [Description("List all threads in the steward's memory with activity stats. Returns thread_id, first/last write timestamps, L0 message count, L1 reflection count, and a short dossier summary if available — ordered most-recent-activity first. Use this before importing history (via checkpoint_summary) to see what buckets exist and where your data should land.")]
    public async Task<string> MemoryListThreads()
    {
        var threads = await _db.ListThreadsAsync();
        return JsonSerializer.Serialize(new
        {
            count = threads.Count,
            threads = threads.Select(t => new
            {
                threadId = t.ThreadId,
                firstTs = t.FirstTs,
                lastTs = t.LastTs,
                l0Count = t.L0Count,
                l1Count = t.L1Count,
                dossierSummary = t.DossierSummary != null && t.DossierSummary.Length > 200
                    ? t.DossierSummary[..200] + "..."
                    : t.DossierSummary,
            }).ToList(),
        });
    }

    [McpServerTool]
    [Description("Resolve a canonical thread_id from a host-context string. Call this once at session open with whatever stable identifier your host provides — Claude Code passes a git repo root path, ChatGPT could pass a project name, email could pass a subject-line hash. The Steward slugifies the final path segment (lowercase, '.' → '-') and returns the thread_id to use for all subsequent tool calls. Pass empty or omit for 'general'.")]
    public string ResolveThread(
        [Description("Stable host-context identifier. For Claude Code, the git repo root path (e.g., 'C:\\\\code\\\\steward'). For other hosts, any stable string. Empty string or omitted returns 'general'.")] string? context = null)
    {
        if (string.IsNullOrWhiteSpace(context))
            return JsonSerializer.Serialize(new { threadId = "general", source = "no_input" });

        var normalized = context.Replace('\\', '/').TrimEnd('/');
        var lastSlash = normalized.LastIndexOf('/');
        var basename = lastSlash >= 0 ? normalized.Substring(lastSlash + 1) : normalized;

        // Drive letters ("C:") or empty basename → workspace root, use general
        if (string.IsNullOrWhiteSpace(basename) || (basename.Length == 2 && basename[1] == ':'))
            return JsonSerializer.Serialize(new { threadId = "general", source = "root_path", input = context });

        var threadId = basename.ToLowerInvariant().Replace('.', '-');
        return JsonSerializer.Serialize(new { threadId, source = "basename_slug", input = context });
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
    [Description("Get assembled context for a conversation. Use depth='light' for dossiers only (fast), or depth='full' for dossiers plus the reflection tree (replaces a chat log). Full context is ordered most-recent-first and truncated to fit.")]
    public async Task<string> GetContext(
        [Description("Thread ID")] string threadId,
        [Description("Context depth: 'light' for dossiers only, 'full' for dossiers + reflection tree")] string depth = "light",
        [Description("Include raw L0 chat entries in full context (default false)")] bool includeL0 = false)
    {
        if (depth == "full")
        {
            var context = await _dossiers.BuildFullContextAsync(threadId, includeL0: includeL0);
            return JsonSerializer.Serialize(new { threadId, depth, context });
        }
        else
        {
            var context = await _dossiers.BuildContextSystemPromptAsync(threadId);
            return JsonSerializer.Serialize(new { threadId, depth = "light", context });
        }
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
