using Microsoft.Extensions.Logging;
using StewardMcp.Data;
using StewardMcp.Services;

namespace StewardMcp.Formation;

/// <summary>
/// Handles mindfulness thread triggers. Scripture study is triggered deterministically
/// by the binary MOD rule (l1Count % 8 == 7). Generic mindfulness threads can be
/// triggered separately as needed.
/// </summary>
public class MindfulnessHandler
{
    private readonly StewardDb _db;
    private readonly ReflectionPipeline _pipeline;
    private readonly Scripture _scripture;
    private readonly LlmService _llm;
    private readonly ILogger<MindfulnessHandler> _logger;

    /// <summary>
    /// Optional delegate to fetch external data for a mindfulness thread.
    /// Set by the API layer to plug in feed providers.
    /// Args: (sourceType, sourceUrl, sourceConfig) → fetched content.
    /// </summary>
    public Func<string, string, string?, Task<string?>>? FeedFetcher { get; set; }

    public MindfulnessHandler(StewardDb db, ReflectionPipeline pipeline, Scripture scripture, LlmService llm, ILogger<MindfulnessHandler> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _scripture = scripture;
        _llm = llm;
        _logger = logger;

        // Wire up the deterministic Scripture trigger
        _pipeline.OnScriptureTriggered += HandleScriptureAsync;
    }

    private async Task HandleScriptureAsync(string sourceThreadId, int l1Count)
    {
        _logger.LogInformation("Scripture study triggered at L1 count {Count} from thread {Thread}", l1Count, sourceThreadId);
        await _scripture.TriggerScriptureStudyAsync();
    }

    /// <summary>Run a generic mindfulness thread on demand.</summary>
    public async Task RunMindfulnessAsync(MindfulnessThread mt)
    {
        if (mt.ThreadId == ReflectionConstants.ScriptureThreadId)
        {
            await _scripture.TriggerScriptureStudyAsync();
            return;
        }

        _logger.LogInformation("Running mindfulness: {Name}", mt.Name);

        // Build dossier context
        var masterProfile = await _db.GetThreadProfileAsync(ReflectionConstants.MasterThreadId);
        var threadProfile = await _db.GetThreadProfileAsync(mt.ThreadId);

        var contextParts = new List<string>();
        if (masterProfile?.Summary != null && !masterProfile.Summary.Contains("no reflections yet"))
        {
            var s = masterProfile.Summary.Length > 400 ? masterProfile.Summary[..400] : masterProfile.Summary;
            contextParts.Add($"CURRENT AWARENESS:\n{s}");
        }
        if (threadProfile?.Summary != null && !threadProfile.Summary.Contains("no reflections yet"))
        {
            var s = threadProfile.Summary.Length > 400 ? threadProfile.Summary[..400] : threadProfile.Summary;
            contextParts.Add($"PREVIOUS REFLECTIONS ON THIS TOPIC:\n{s}");
        }

        // Fetch external data if this thread has a source
        string? fetchedContent = null;
        if (!string.IsNullOrEmpty(mt.SourceType) && !string.IsNullOrEmpty(mt.SourceUrl) && FeedFetcher != null)
        {
            try
            {
                fetchedContent = await FeedFetcher(mt.SourceType, mt.SourceUrl, mt.SourceConfig);
                _logger.LogInformation("Fetched {Type} feed for {Name}: {Length} chars",
                    mt.SourceType, mt.Name, fetchedContent?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feed fetch failed for {Name}", mt.Name);
            }
        }

        // If we have fetched content, curate it through the LLM
        string promptContent;
        if (!string.IsNullOrEmpty(fetchedContent))
        {
            var context = contextParts.Count > 0 ? string.Join("\n\n", contextParts) : "";
            var curationPrompt = $"""
                You are reviewing external data on behalf of your person.

                {context}

                DATA SOURCE: {mt.Name}
                {fetchedContent}

                INSTRUCTIONS: {mt.Prompt}

                Review this data through the lens of what you know about your person.
                Select what's relevant, note themes, discard noise. Be concise.
                """;

            promptContent = await _llm.CallReflectionLlmAsync(
                "You are a Personal Steward curating information for your person. Be selective and relevant.",
                curationPrompt);

            if (string.IsNullOrWhiteSpace(promptContent))
                promptContent = $"Feed check: {mt.Name} — nothing notable.";
        }
        else
        {
            var context = contextParts.Count > 0 ? string.Join("\n\n", contextParts) + "\n\n" : "";
            promptContent = $"Mindfulness: {mt.Name}\n\n{context}{mt.Prompt}";
        }

        // Create L0 pair
        await _db.AppendJournalAsync(mt.ThreadId, mode: "chat", level: 0, content: promptContent, role: "user");
        await _db.AppendJournalAsync(mt.ThreadId, mode: "chat", level: 0,
            content: $"Reflecting on {mt.Name}...", role: "assistant");

        await _pipeline.RunReflectionsAsync(mt.ThreadId, isMindfulness: true);
        _logger.LogInformation("Mindfulness complete: {Name}", mt.Name);
    }
}
