using Microsoft.Extensions.Logging;
using StewardMcp.Data;

namespace StewardMcp.Formation;

/// <summary>
/// Handles mindfulness thread triggers. Generic threads get master dossier context
/// and their prompt. Scripture gets special reading plan handling.
/// </summary>
public class MindfulnessHandler
{
    private readonly StewardDb _db;
    private readonly ReflectionPipeline _pipeline;
    private readonly Scripture _scripture;
    private readonly ILogger<MindfulnessHandler> _logger;

    public MindfulnessHandler(StewardDb db, ReflectionPipeline pipeline, Scripture scripture, ILogger<MindfulnessHandler> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _scripture = scripture;
        _logger = logger;

        // Wire up the generic mindfulness trigger
        _pipeline.OnMindfulnessTriggered += HandleMindfulnessAsync;
    }

    private async Task HandleMindfulnessAsync(MindfulnessThread mt)
    {
        // Scripture has special reading plan logic
        if (mt.ThreadId == ReflectionConstants.ScriptureThreadId)
        {
            await _scripture.TriggerScriptureStudyAsync();
            return;
        }

        // Generic mindfulness: build context, create L0 pair, run reflections
        _logger.LogInformation("Running mindfulness: {Name}", mt.Name);

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

        var context = contextParts.Count > 0 ? string.Join("\n\n", contextParts) + "\n\n" : "";

        var prompt = $"""
            Mindfulness: {mt.Name}

            {context}{mt.Prompt}
            """;

        // Create L0 pair (same pattern as Scripture)
        await _db.AppendJournalAsync(mt.ThreadId, mode: "chat", level: 0, content: prompt, role: "user");
        await _db.AppendJournalAsync(mt.ThreadId, mode: "chat", level: 0,
            content: $"Reflecting on {mt.Name}...", role: "assistant");

        await _pipeline.RunReflectionsAsync(mt.ThreadId);
        _logger.LogInformation("Mindfulness complete: {Name}", mt.Name);
    }
}
