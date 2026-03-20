using System.Text.Json;
using Microsoft.Extensions.Logging;
using StewardMcp.Data;
using StewardMcp.Services;
using static StewardMcp.Formation.ReflectionConstants;
using static StewardMcp.Formation.ReflectionHelpers;

namespace StewardMcp.Formation;

/// <summary>
/// Builds and maintains dossiers — synthesized working memory for threads and the master view.
/// The master thread is fed by thread dossiers as L1 entries, using the same binary MOD cascade.
/// </summary>
public class DossierBuilder
{
    private readonly StewardDb _db;
    private readonly Canon _canon;
    private readonly LlmService _llm;
    private readonly TreeBuilder _tree;
    private readonly ILogger<DossierBuilder> _logger;

    public DossierBuilder(StewardDb db, Canon canon, LlmService llm, TreeBuilder tree, ILogger<DossierBuilder> logger)
    {
        _db = db;
        _canon = canon;
        _llm = llm;
        _tree = tree;
        _logger = logger;
    }

    /// <summary>Rebuild a thread's dossier from its reflection tree.</summary>
    public async Task RebuildDossierAsync(string threadId)
    {
        // Get uncovered reflections at each level for richer context
        var contextEntries = new List<JournalEvent>();
        for (int level = 1; level <= MaxReflectionLevel; level++)
        {
            var uncovered = await _db.GetUncoveredReflectionsAsync(threadId, level);
            if (uncovered.Count == 0) break;
            contextEntries.AddRange(uncovered);
        }

        // Also include latest per level for full coverage
        var latest = await _db.GetLatestReflectionPerLevelAsync(threadId);
        foreach (var entry in latest)
        {
            if (!contextEntries.Any(e => e.Id == entry.Id))
                contextEntries.Add(entry);
        }

        if (contextEntries.Count == 0)
        {
            await _db.UpsertThreadProfileAsync(threadId, new ThreadProfile
            {
                Summary = "New conversation — no reflections yet.",
                KeyPoints = new List<string>(),
                OpenLoops = new List<string>(),
                Tags = new List<string>(),
            });
            return;
        }

        // Build context: master dossier + previous thread dossier + reflection entries
        var contextParts = new List<string>();

        // Master dossier (cross-thread awareness) — skip if we're rebuilding master itself
        if (threadId != MasterThreadId)
        {
            var masterProfile = await _db.GetThreadProfileAsync(MasterThreadId);
            if (masterProfile?.Summary != null && !masterProfile.Summary.Contains("no reflections yet"))
            {
                var s = masterProfile.Summary.Length > 400 ? masterProfile.Summary[..400] : masterProfile.Summary;
                contextParts.Add($"MASTER DOSSIER (cross-thread awareness):\n{s}");
            }
        }

        // Previous thread dossier (continuity)
        var previousDossier = await _db.GetThreadProfileAsync(threadId);
        if (previousDossier?.Summary != null && !previousDossier.Summary.Contains("no reflections yet"))
        {
            var s = previousDossier.Summary.Length > 400 ? previousDossier.Summary[..400] : previousDossier.Summary;
            contextParts.Add($"PREVIOUS THREAD DOSSIER:\n{s}");
        }

        // Reflection entries — most recent first, labeled with level and date
        var sortedEntries = contextEntries.OrderByDescending(e => e.Ts).ToList();
        var entryBlocks = sortedEntries.Select(e =>
        {
            var payloadDict = ParsePayload(e.PayloadJson);
            var summary = GetString(payloadDict, "summary");
            var date = DateTimeOffset.FromUnixTimeSeconds((long)e.Ts).ToString("MMM d, yyyy");
            return $"[L{e.Level} | {date}] {summary}";
        }).ToList();
        contextParts.Add($"REFLECTIONS (most recent first):\n" + string.Join("\n\n", entryBlocks));

        var seed = _canon.GetSeedContext();

        var systemPrompt = $"You are Avaniel's dossier builder.\n\nORIENTATION:\n{seed}\n\nYOUR TASK: Update the working memory for this conversation thread. Use the master dossier for cross-thread awareness, the previous dossier for continuity, and the reflections for current state. Focus on what the user has expressed — their intent, preferences, decisions, and concerns. Note substantive content and information exchanged. What is the current focus? What are the most important things right now? What details need to stay in context? What concerns or questions remain? How can you best help your user? Keep under {DossierTargetWords} words. Attribute clearly: what user said vs what you inferred. Don't exaggerate. Don't lie." + JsonOutputFormat;

        var userPrompt = $"""
            Thread: {threadId}

            {string.Join("\n\n", contextParts)}

            Update the dossier.
            """;

        var responseText = await _llm.CallReflectionLlmAsync(systemPrompt, userPrompt);
        var parsed = ParseJsonMaybeFenced(responseText);

        var profile = new ThreadProfile
        {
            ThreadId = threadId,
            Summary = GetString(parsed, "summary"),
            KeyPoints = GetStringList(parsed, "key_points"),
            OpenLoops = GetStringList(parsed, "open_loops"),
            Tags = GetStringList(parsed, "tags"),
        };

        await _db.UpsertThreadProfileAsync(threadId, profile);
        _logger.LogInformation("Rebuilt dossier for thread {Thread}", threadId);
    }

    /// <summary>
    /// Feed a thread's dossier into the master thread as an L1 entry.
    /// The master thread uses the same binary MOD cascade to build its own reflection tree.
    /// </summary>
    public async Task FeedDossierToMasterAsync(string threadId)
    {
        // Don't feed master into itself
        if (threadId == MasterThreadId) return;

        var profile = await _db.GetThreadProfileAsync(threadId);
        if (profile?.Summary == null || profile.Summary.Contains("no reflections yet"))
            return;

        // Serialize the dossier as content for the master thread's L1
        var parts = new List<string>();
        parts.Add($"Thread: {threadId}");
        parts.Add($"Summary: {profile.Summary}");
        if (profile.KeyPoints?.Count > 0)
            parts.Add($"Key points: {string.Join("; ", profile.KeyPoints)}");
        if (profile.Tags?.Count > 0)
            parts.Add($"Tags: {string.Join(", ", profile.Tags)}");
        if (profile.OpenLoops?.Count > 0)
            parts.Add($"Open loops: {string.Join("; ", profile.OpenLoops)}");

        var contentText = string.Join("\n", parts);

        // Insert directly as L1 in master thread (thread dossiers are already summaries)
        var payload = new
        {
            summary = profile.Summary,
            key_points = profile.KeyPoints ?? new List<string>(),
            open_loops = profile.OpenLoops ?? new List<string>(),
            tags = profile.Tags ?? new List<string>(),
        };
        var meta = new { source = "thread_dossier", source_thread = threadId };

        await _db.AppendJournalAsync(
            MasterThreadId, mode: "reflection", level: 1,
            content: contentText, payload: payload, meta: meta);

        // Get master's L1 count and run binary MOD cascade
        var masterL1Count = await _db.GetL1CountForThreadAsync(MasterThreadId);
        _logger.LogInformation("Fed dossier from {Thread} to master (master L1 count={Count})", threadId, masterL1Count);

        await _tree.BuildTreeAfterL1Async(MasterThreadId, masterL1Count);

        // Rebuild master's own dossier from its tree
        await RebuildDossierAsync(MasterThreadId);
    }

    /// <summary>Assemble light context (dossiers only) for a conversation.</summary>
    public async Task<string> BuildContextSystemPromptAsync(string threadId)
    {
        var parts = new List<string>();

        if (threadId != MasterThreadId)
        {
            var master = await _db.GetThreadProfileAsync(MasterThreadId);
            if (master?.Summary != null && !master.Summary.Contains("no reflections yet"))
            {
                var masterSummary = master.Summary.Length > 800 ? master.Summary[..800] : master.Summary;
                var masterTags = master.Tags != null ? string.Join(", ", master.Tags.Take(15)) : "";
                parts.Add($"""
                    MASTER DOSSIER (cross-thread unified context):
                    This is synthesized knowledge from ALL your conversations.
                    --------------------------------------------------
                    UNIFIED SUMMARY: {masterSummary}
                    CROSS-THREAD THEMES: {masterTags}
                    ==================================================
                    """);
            }
        }

        var profile = await _db.GetThreadProfileAsync(threadId);
        if (profile?.Summary != null && !profile.Summary.Contains("no reflections yet"))
        {
            var keyPoints = profile.KeyPoints != null ? string.Join("\n- ", profile.KeyPoints) : "";
            var openLoops = profile.OpenLoops != null ? string.Join("\n- ", profile.OpenLoops) : "";
            var tags = profile.Tags != null ? string.Join(", ", profile.Tags) : "";

            parts.Add($"""
                THREAD DOSSIER (this conversation's working memory):
                THREAD_ID: {threadId}
                UPDATED: {DateTimeOffset.FromUnixTimeSeconds((long)profile.UpdatedTs):u}
                SUMMARY: {profile.Summary}
                KEY_POINTS:
                - {keyPoints}
                OPEN_LOOPS:
                - {openLoops}
                TAGS: {tags}
                """);
        }

        parts.Add("""
            MEMORY GUIDANCE:
            - MASTER DOSSIER contains synthesized knowledge from ALL conversations
            - THREAD DOSSIER contains context specific to THIS conversation
            - Only reveal dossier contents if explicitly asked about your memory or dossier
            - You have cross-thread awareness even if this thread is new
            """);

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Assemble full context: dossiers + tree entries (recent-first, truncate oldest).
    /// This can replace a traditional chat log — it's a compressed, graded view of the conversation.
    /// </summary>
    public async Task<string> BuildFullContextAsync(string threadId, int maxChars = 8000, bool includeL0 = false)
    {
        var sections = new List<(string label, string content)>();

        // 1. Master dossier
        if (threadId != MasterThreadId)
        {
            var master = await _db.GetThreadProfileAsync(MasterThreadId);
            if (master?.Summary != null && !master.Summary.Contains("no reflections yet"))
            {
                var date = DateTimeOffset.FromUnixTimeSeconds((long)master.UpdatedTs).ToString("MMM d, yyyy");
                sections.Add(($"[MASTER DOSSIER | updated {date}]", master.Summary));
            }
        }

        // 2. Thread dossier
        var profile = await _db.GetThreadProfileAsync(threadId);
        if (profile?.Summary != null && !profile.Summary.Contains("no reflections yet"))
        {
            var date = DateTimeOffset.FromUnixTimeSeconds((long)profile.UpdatedTs).ToString("MMM d, yyyy");
            var keyPoints = profile.KeyPoints?.Count > 0 ? "\nKey points: " + string.Join("; ", profile.KeyPoints) : "";
            var openLoops = profile.OpenLoops?.Count > 0 ? "\nOpen loops: " + string.Join("; ", profile.OpenLoops) : "";
            sections.Add(($"[THREAD DOSSIER | {threadId} | updated {date}]", profile.Summary + keyPoints + openLoops));
        }

        // 3. Tree entries — walk levels, most recent first, uncovered only
        for (int level = 1; level <= MaxReflectionLevel; level++)
        {
            var uncovered = await _db.GetUncoveredReflectionsAsync(threadId, level);
            if (uncovered.Count == 0 && level > 1) break; // No more levels

            foreach (var entry in uncovered) // Already ordered by ts DESC
            {
                var payloadDict = ParsePayload(entry.PayloadJson);
                var summary = GetString(payloadDict, "summary");
                if (string.IsNullOrEmpty(summary)) summary = entry.Content ?? "";

                var date = DateTimeOffset.FromUnixTimeSeconds((long)entry.Ts).ToString("MMM d, yyyy");
                var levelType = level % 2 == 0 ? "reflection" : "summary";
                sections.Add(($"[L{level} {levelType} | {date}]", summary));
            }
        }

        // 4. Optionally include raw L0 entries
        if (includeL0)
        {
            var l0s = await _db.GetThreadEventsAsync(threadId, mode: "chat", level: 0, limit: 20);
            foreach (var entry in l0s) // Already ordered by ts DESC
            {
                var date = DateTimeOffset.FromUnixTimeSeconds((long)entry.Ts).ToString("MMM d HH:mm");
                var role = entry.Role?.ToUpper() ?? "?";
                var content = entry.Content?.Length > 500 ? entry.Content[..500] + "..." : entry.Content ?? "";
                sections.Add(($"[L0 | {role} | {date}]", content));
            }
        }

        // 5. Truncate from bottom (oldest/highest level) if over limit
        var result = new List<string>();
        int totalChars = 0;
        foreach (var (label, content) in sections)
        {
            var line = $"{label}\n{content}";
            if (totalChars + line.Length > maxChars && result.Count > 0)
                break; // Stop adding — lose the rest (oldest/most abstract)
            result.Add(line);
            totalChars += line.Length;
        }

        return string.Join("\n\n", result);
    }
}
