using System.Text.Json;
using Microsoft.Extensions.Logging;
using StewardMcp.Data;
using StewardMcp.Services;
using static StewardMcp.Formation.ReflectionConstants;
using static StewardMcp.Formation.ReflectionHelpers;

namespace StewardMcp.Formation;

/// <summary>
/// Builds and maintains dossiers — synthesized working memory for threads and the master view.
/// </summary>
public class DossierBuilder
{
    private readonly StewardDb _db;
    private readonly Canon _canon;
    private readonly LlmService _llm;
    private readonly ILogger<DossierBuilder> _logger;

    public DossierBuilder(StewardDb db, Canon canon, LlmService llm, ILogger<DossierBuilder> logger)
    {
        _db = db;
        _canon = canon;
        _llm = llm;
        _logger = logger;
    }

    /// <summary>Rebuild a thread's dossier from its reflection tree.</summary>
    public async Task RebuildDossierAsync(string threadId)
    {
        // Single query: latest reflection at each level
        var latest = await _db.GetLatestReflectionPerLevelAsync(threadId);
        if (latest.Count == 0)
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

        // Build context: master dossier + previous thread dossier + latest per level
        var contextParts = new List<string>();

        // Master dossier (cross-thread awareness)
        var masterProfile = await _db.GetThreadProfileAsync(ReflectionConstants.MasterThreadId);
        if (masterProfile?.Summary != null && !masterProfile.Summary.Contains("no reflections yet"))
        {
            var s = masterProfile.Summary.Length > 400 ? masterProfile.Summary[..400] : masterProfile.Summary;
            contextParts.Add($"MASTER DOSSIER (cross-thread awareness):\n{s}");
        }

        // Previous thread dossier (continuity)
        var previousDossier = await _db.GetThreadProfileAsync(threadId);
        if (previousDossier?.Summary != null && !previousDossier.Summary.Contains("no reflections yet"))
        {
            var s = previousDossier.Summary.Length > 400 ? previousDossier.Summary[..400] : previousDossier.Summary;
            contextParts.Add($"PREVIOUS THREAD DOSSIER:\n{s}");
        }

        // Latest entry at each level
        var samples = latest.Select(e =>
        {
            var payloadDict = ParsePayload(e.PayloadJson);
            return (object)new
            {
                level = e.Level,
                ts = e.Ts,
                summary = GetString(payloadDict, "summary"),
            };
        }).ToList();
        var samplesJson = JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true });
        contextParts.Add($"LATEST REFLECTIONS (one per level):\n{samplesJson}");

        var seed = _canon.GetSeedContext();

        var systemPrompt = $"You are Avaniel's dossier builder.\n\nORIENTATION:\n{seed}\n\nYOUR TASK: Update the working memory for this conversation thread. Use the master dossier for cross-thread awareness, the previous dossier for continuity, and the latest reflections for current state. What is the current focus? What are the most important things right now? What details need to stay in context? What concerns or questions remain? How can you best help your user? Keep under {DossierTargetWords} words. Attribute clearly: what user said vs what you inferred. Don't exaggerate. Don't lie." + JsonOutputFormat;

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

    /// <summary>Rebuild the master dossier from all thread dossiers + Scripture.</summary>
    public async Task RebuildMasterDossierAsync()
    {
        var allThreadIds = await _db.GetAllThreadIdsAsync();
        var userDossiers = new List<ThreadProfile>();

        foreach (var tid in allThreadIds)
        {
            var threadProfile = await _db.GetThreadProfileAsync(tid);
            if (threadProfile == null) continue;
            userDossiers.Add(threadProfile);
        }

        var scriptureDossier = await _db.GetThreadProfileAsync(ScriptureThreadId);
        var seed = _canon.GetSeedContext();

        var contextParts = new List<string>();
        if (scriptureDossier?.Summary != null)
        {
            var sTags = scriptureDossier.Tags != null ? string.Join(", ", scriptureDossier.Tags.Take(10)) : "";
            var sSummary = scriptureDossier.Summary.Length > 500 ? scriptureDossier.Summary[..500] : scriptureDossier.Summary;
            contextParts.Add($"SCRIPTURE FORMATION (theological grounding):\n  Summary: {sSummary}\n  Themes: {sTags}");
        }

        var threadSummaries = new List<string>();
        foreach (var d in userDossiers.Take(20))
        {
            var summary = d.Summary ?? "";
            if (summary.Length > 300) summary = summary[..300];
            var tags = d.Tags != null ? string.Join(", ", d.Tags.Take(5)) : "";
            threadSummaries.Add($"[{d.ThreadId}]\n  {summary}\n  Tags: {tags}");
        }
        if (threadSummaries.Count > 0)
            contextParts.Add($"USER THREADS ({threadSummaries.Count} conversations):\n" + string.Join("\n\n", threadSummaries));

        var dossierContext = string.Join("\n\n", contextParts);

        var systemPrompt = $"You are Avaniel's master dossier builder.\n\nORIENTATION:\n{seed}\n\nYOUR TASK: Synthesize a unified portrait from all conversation threads and reflect. What patterns emerge across them? Who is the user, and how have you been helping them? How can you be a better steward? This is to be the current overview of your whole working relationship with the user including your thoughts on how to improve it, especially in light of scriptural guidance. Include any significant questions or concerns. Keep under {DossierTargetWords} words." + JsonOutputFormat;

        var userPrompt = $"""
            Synthesize a master dossier from these thread dossiers:

            {dossierContext}

            Create a unified portrait that captures cross-thread patterns.
            """;

        var responseText = await _llm.CallReflectionLlmAsync(systemPrompt, userPrompt);
        var parsed = ParseJsonMaybeFenced(responseText);

        var profile = new ThreadProfile
        {
            ThreadId = MasterThreadId,
            Summary = GetString(parsed, "summary"),
            KeyPoints = GetStringList(parsed, "key_points"),
            OpenLoops = GetStringList(parsed, "open_loops"),
            Tags = GetStringList(parsed, "tags"),
        };

        await _db.UpsertThreadProfileAsync(MasterThreadId, profile);
        _logger.LogInformation("Rebuilt master dossier");
    }

    /// <summary>Assemble context for a conversation from master + thread dossiers.</summary>
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
}
