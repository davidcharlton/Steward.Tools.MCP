using Microsoft.Extensions.Logging;
using StewardMcp.Data;
using StewardMcp.Services;
using static StewardMcp.Formation.ReflectionConstants;
using static StewardMcp.Formation.ReflectionHelpers;

namespace StewardMcp.Formation;

/// <summary>
/// Builds the reflection tree: L1 summaries from L0 chat, higher levels from unreflected entries.
/// Each entry records its parent lineage via reflection_sources.
/// </summary>
public class TreeBuilder
{
    private readonly StewardDb _db;
    private readonly VectorStore _vectors;
    private readonly Canon _canon;
    private readonly LlmService _llm;
    private readonly ILogger<TreeBuilder> _logger;

    // Probabilistic trigger thresholds for higher-level reflections
    private const int BaseThreshold = 2;  // added to level: L2=4, L3=5, L4=6...
    private const int MaxThreshold = 8;   // cap: L6+ all require ~8 entries for certainty

    public TreeBuilder(StewardDb db, VectorStore vectors, Canon canon, LlmService llm, ILogger<TreeBuilder> logger)
    {
        _db = db;
        _vectors = vectors;
        _canon = canon;
        _llm = llm;
        _logger = logger;
    }

    /// <summary>Create an L1 summary from unreflected L0 events, with lineage tracking.</summary>
    public async Task<long> CreateL1WithLineageAsync(string threadId, List<JournalEvent> l0Events)
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
        // Same L1 builder for all threads — Scripture tone comes from the study prompt in Scripture.cs
        var threadProfile = await _db.GetThreadProfileAsync(threadId);
        var threadTags = threadProfile?.Tags != null ? string.Join(", ", threadProfile.Tags.Take(10)) : "";
        var dossierContext = !string.IsNullOrEmpty(threadTags) ? $"Thread themes: {threadTags}" : "";

        var systemPrompt = $"""
            You are Avaniel's L1 summary engine.

            ORIENTATION:
            {seed}

            YOUR TASK: Summarize this exchange. What was discussed? What matters? Keep under {SummaryTargetWords} words. This is the most recent part of the working memory for the conversation. Keep important details for short term context. {dossierContext}

            """ + JsonOutputFormat;

        var userPrompt = $"Recent conversation:\n\n{historyBlock}";
        var responseText = await _llm.CallReflectionLlmAsync(systemPrompt, userPrompt);
        var parsed = ParseJsonMaybeFenced(responseText);

        var payload = BuildPayload(parsed);
        var meta = new { source = "steward-mcp", source_count = l0Events.Count };
        var contentText = RenderReflectionText(
            GetString(parsed, "summary"), GetStringList(parsed, "key_points"), GetStringList(parsed, "tags"));

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

        return l1Id;
    }

    /// <summary>Walk up the tree, probabilistically building higher levels from unreflected entries.</summary>
    public async Task BuildHigherLevelsAsync(string threadId)
    {
        for (int level = 2; ; level++)
        {
            var unreflected = await _db.GetUnreflectedEntriesAsync(threadId, level - 1);
            if (unreflected.Count < MinEntriesForReflection)
                break;

            // Threshold scales with level: L2 needs ~4 entries for certainty, L6+ caps at 8.
            // At exactly MinEntriesForReflection, P = 2/threshold (~50% for L2).
            var threshold = Math.Min(BaseThreshold + level, MaxThreshold);
            var probability = Math.Min(1.0, (double)unreflected.Count / threshold);
            if (Random.Shared.NextDouble() >= probability)
                break;

            _logger.LogInformation("Building L{Level} for thread {Thread} ({Count} unreflected L{Source} entries, P={Prob:F2})",
                level, threadId, unreflected.Count, level - 1, probability);

            await CreateHigherLevelWithLineageAsync(threadId, level, unreflected);
        }
    }

    private async Task CreateHigherLevelWithLineageAsync(string threadId, int level, List<JournalEvent> sources)
    {
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
            foreach (var gc in grandchildren.Take(3)) // limit to avoid bloat
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

                YOUR TASK: Synthesize the L{level - 1} entries into an L{level} reflection. Use the supporting detail and thread context for depth. Discern patterns and emerging themes. What is happening? Why does it matter? What might be helpful? This is part of the working memory for the conversation. Keep under {targetWords} words.

                """ + JsonOutputFormat
            : $"""
                You are Avaniel's L{level} summary engine.

                ORIENTATION:
                {seed}

                YOUR TASK: Synthesize the L{level - 1} entries into a unified L{level} summary. Use the supporting detail and thread context for depth. Keep what matters for historical context and understanding. This is part of the working memory for the conversation. Keep under {targetWords} words.

                """ + JsonOutputFormat;

        var responseText = await _llm.CallReflectionLlmAsync(systemPrompt, userPrompt);
        var parsed = ParseJsonMaybeFenced(responseText);

        var payload = BuildPayload(parsed);
        var meta = new { source = "steward-mcp", source_level = level - 1, source_count = sources.Count };
        var contentText = RenderReflectionText(
            GetString(parsed, "summary"), GetStringList(parsed, "key_points"), GetStringList(parsed, "tags"));

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
