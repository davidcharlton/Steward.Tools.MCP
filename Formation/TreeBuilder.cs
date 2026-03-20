using Microsoft.Extensions.Logging;
using StewardMcp.Data;
using StewardMcp.Services;
using static StewardMcp.Formation.ReflectionConstants;
using static StewardMcp.Formation.ReflectionHelpers;

namespace StewardMcp.Formation;

/// <summary>
/// Builds the reflection tree: L1 summaries from L0 chat, higher levels via binary MOD cascade.
/// Each entry records its parent lineage via reflection_sources.
/// Odd levels = summaries, even levels = reflections.
/// </summary>
public class TreeBuilder
{
    private readonly StewardDb _db;
    private readonly VectorStore _vectors;
    private readonly Canon _canon;
    private readonly LlmService _llm;
    private readonly ILogger<TreeBuilder> _logger;

    public TreeBuilder(StewardDb db, VectorStore vectors, Canon canon, LlmService llm, ILogger<TreeBuilder> logger)
    {
        _db = db;
        _vectors = vectors;
        _canon = canon;
        _llm = llm;
        _logger = logger;
    }

    /// <summary>Create an L1 summary from unreflected L0 events, with lineage tracking.</summary>
    /// <returns>Tuple of (L1 journal ID, L1 count for this thread), or null if the LLM failed.</returns>
    public async Task<(long L1Id, int L1Count)?> CreateL1WithLineageAsync(string threadId, List<JournalEvent> l0Events)
    {
        var history = new List<string>();
        foreach (var e in l0Events)
        {
            var label = (e.Role?.ToUpper()) switch
            {
                "USER" => "[USER]",
                "ASSISTANT" => "[ASSISTANT]",
                _ => $"[{e.Role?.ToUpper() ?? "UNKNOWN"}]",
            };
            var content = e.Content?.Length > 2000 ? e.Content[..2000] + "..." : e.Content;
            history.Add($"{label}: {content}");
        }
        var historyBlock = string.Join("\n\n", history);

        var seed = _canon.GetSeedContext();
        var threadProfile = await _db.GetThreadProfileAsync(threadId);
        var threadTags = threadProfile?.Tags != null ? string.Join(", ", threadProfile.Tags.Take(10)) : "";
        var dossierContext = !string.IsNullOrEmpty(threadTags) ? $"Thread themes: {threadTags}" : "";

        var systemPrompt = $"""
            You are Avaniel's L1 summary engine.

            ORIENTATION:
            {seed}

            YOUR TASK: Summarize this exchange. Capture the substantive content — what was asked, what was learned, what decisions were made. When the user expresses preferences, intent, or gives direction, highlight those. Include key details from the assistant's response where they contain information worth recalling. Ignore assistant pleasantries and acknowledgments. Keep under {SummaryTargetWords} words. {dossierContext}

            """ + JsonOutputFormat;

        var userPrompt = $"Recent conversation:\n\n{historyBlock}";
        var responseText = await _llm.CallReflectionLlmAsync(systemPrompt, userPrompt);

        // If LLM failed (rate limited, down, etc.), don't create the L1.
        // L0s stay unreflected and will be picked up on the next successful cycle.
        if (string.IsNullOrWhiteSpace(responseText))
        {
            _logger.LogWarning("LLM returned empty for L1 summary on thread {Thread} — L0s remain unreflected", threadId);
            return null;
        }

        var parsed = ParseJsonMaybeFenced(responseText);
        var summary = GetString(parsed, "summary");
        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger.LogWarning("LLM returned unparseable response for L1 on thread {Thread} — L0s remain unreflected", threadId);
            return null;
        }

        var payload = BuildPayload(parsed);
        var l1Count = await _db.GetL1CountForThreadAsync(threadId) + 1; // +1 for the one we're about to insert
        var meta = new { source = "steward-mcp", source_count = l0Events.Count, l1_seq = l1Count };
        var contentText = RenderReflectionText(
            summary, GetStringList(parsed, "key_points"), GetStringList(parsed, "tags"));

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var l1Id = await _db.AppendJournalAsync(
            threadId, mode: "reflection", level: 1,
            content: contentText, payload: payload, meta: meta);

        await _db.InsertReflectionSourcesAsync(l1Id, l0Events.Select(e => e.Id).ToList());

        // Fire-and-forget: embedding is for future search, doesn't block the reflection pipeline
        _ = Task.Run(async () =>
        {
            try { await _vectors.UpsertJournalEmbeddingAsync(l1Id, threadId, 1, "reflection", now, contentText); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to embed L1 #{Id}", l1Id); }
        });

        // Re-read actual count after insert (in case of concurrent writes)
        var actualCount = await _db.GetL1CountForThreadAsync(threadId);
        return (l1Id, actualCount);
    }

    /// <summary>
    /// Build higher levels of the reflection tree using the binary MOD rule.
    /// L(n+1) fires when l1Count % 2^n == 0. Break on first level that doesn't fire.
    /// </summary>
    public async Task BuildTreeAfterL1Async(string threadId, int l1Count)
    {
        for (int level = 2; level <= MaxReflectionLevel; level++)
        {
            int interval = 1 << (level - 1); // 2^(level-1): L2=2, L3=4, L4=8, ...
            if (l1Count % interval != 0)
                break;

            _logger.LogInformation("Building L{Level} for thread {Thread} (L1 count={Count}, interval={Interval})",
                level, threadId, l1Count, interval);

            await CreateHigherLevelFromLatest2Async(threadId, level);
        }
    }

    private async Task CreateHigherLevelFromLatest2Async(string threadId, int level)
    {
        // Get exactly the 2 most recent entries from the level below
        var sources = await _db.GetLatestReflectionsAsync(threadId, level - 1, limit: 2);
        if (sources.Count < 2)
        {
            _logger.LogWarning("Not enough L{Level} entries for L{Target} in thread {Thread} (found {Count})",
                level - 1, level, threadId, sources.Count);
            return;
        }

        // Build context: L[N-1] entries (direct sources) + their L[N-2] sources (grandchildren)
        var childBlocks = new List<string>();
        var grandchildBlocks = new List<string>();

        foreach (var child in sources)
        {
            var payloadDict = ParsePayload(child.PayloadJson);
            var summary = GetString(payloadDict, "summary");
            if (summary.Length > 500) summary = summary[..500] + "...";
            var keyPoints = GetStringList(payloadDict, "key_points");
            var block = $"[L{level - 1} #{child.Id}]\nSummary: {summary}";
            if (keyPoints.Count > 0) block += "\nKey points: " + string.Join("; ", keyPoints.Take(5));
            childBlocks.Add(block);

            // Follow lineage one level deeper for context
            var grandchildren = await _db.GetSourcesForReflectionAsync(child.Id);
            foreach (var gc in grandchildren.Take(3))
            {
                var gcPayload = ParsePayload(gc.PayloadJson);
                var gcSummary = GetString(gcPayload, "summary");
                if (string.IsNullOrEmpty(gcSummary)) gcSummary = gc.Content ?? "";
                if (gcSummary.Length > 300) gcSummary = gcSummary[..300] + "...";
                grandchildBlocks.Add($"[L{gc.Level} #{gc.Id}]: {gcSummary}");
            }
        }

        var contextParts = new List<string>();
        contextParts.Add($"ENTRIES TO SYNTHESIZE (L{level - 1}):\n" + string.Join("\n\n", childBlocks));
        if (grandchildBlocks.Count > 0)
            contextParts.Add($"SUPPORTING DETAIL (L{level - 2}):\n" + string.Join("\n", grandchildBlocks));

        // Thread dossier for broader context
        var profile = await _db.GetThreadProfileAsync(threadId);
        if (profile?.Summary != null)
        {
            var s = profile.Summary.Length > 300 ? profile.Summary[..300] : profile.Summary;
            contextParts.Add($"THREAD CONTEXT:\n{s}");
        }

        var userPrompt = string.Join("\n\n", contextParts);

        var seed = _canon.GetSeedContext();
        var isReflection = level % 2 == 0;
        var targetWords = isReflection ? ReflectionTargetWords : SummaryTargetWords;

        var systemPrompt = isReflection
            ? $"""
                You are Avaniel's L{level} reflection engine.

                ORIENTATION:
                {seed}

                YOUR TASK: Synthesize the L{level - 1} entries into an L{level} reflection. Use the supporting detail and thread context for depth. Focus on what the user expressed — their intent, decisions, and concerns. Note substantive information from the assistant where it adds value. Discern patterns and emerging themes. What is happening? Why does it matter? What might be helpful? Keep under {targetWords} words.

                """ + JsonOutputFormat
            : $"""
                You are Avaniel's L{level} summary engine.

                ORIENTATION:
                {seed}

                YOUR TASK: Synthesize the L{level - 1} entries into a unified L{level} summary. Focus on what the user discussed, decided, and cares about. Include substantive content from the assistant where it contains information worth preserving. Keep what matters for historical context and understanding. Keep under {targetWords} words.

                """ + JsonOutputFormat;

        var responseText = await _llm.CallReflectionLlmAsync(systemPrompt, userPrompt);

        // If LLM failed, skip this level. Sources stay unconsumed and will be picked up next cycle.
        if (string.IsNullOrWhiteSpace(responseText))
        {
            _logger.LogWarning("LLM returned empty for L{Level} on thread {Thread} — skipping", level, threadId);
            return;
        }

        var parsed = ParseJsonMaybeFenced(responseText);
        var parsedSummary = GetString(parsed, "summary");
        if (string.IsNullOrWhiteSpace(parsedSummary))
        {
            _logger.LogWarning("LLM returned unparseable response for L{Level} on thread {Thread} — skipping", level, threadId);
            return;
        }

        var payload = BuildPayload(parsed);
        var meta = new { source = "steward-mcp", source_level = level - 1, source_count = sources.Count };
        var contentText = RenderReflectionText(
            parsedSummary, GetStringList(parsed, "key_points"), GetStringList(parsed, "tags"));

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var entryId = await _db.AppendJournalAsync(
            threadId, mode: "reflection", level: level,
            content: contentText, payload: payload, meta: meta);

        await _db.InsertReflectionSourcesAsync(entryId, sources.Select(e => e.Id).ToList());

        _ = Task.Run(async () =>
        {
            try { await _vectors.UpsertJournalEmbeddingAsync(entryId, threadId, level, "reflection", now, contentText); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to embed L{Level} #{Id}", level, entryId); }
        });
    }
}
