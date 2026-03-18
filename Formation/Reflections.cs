using Microsoft.Extensions.Logging;
using StewardMcp.Data;
using static StewardMcp.Formation.ReflectionConstants;

namespace StewardMcp.Formation;

/// <summary>
/// Orchestrates the reflection pipeline: debouncing, L1 creation, tree building,
/// dossier rebuild, and mindfulness thread triggers.
/// </summary>
public class ReflectionPipeline
{
    private readonly StewardDb _db;
    private readonly TreeBuilder _tree;
    private readonly DossierBuilder _dossiers;
    private readonly ILogger<ReflectionPipeline> _logger;

    // Debounce state
    private readonly Dictionary<string, DateTime> _lastTrigger = new();
    private readonly HashSet<string> _inflight = new();
    private readonly object _triggerLock = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    public ReflectionPipeline(StewardDb db, TreeBuilder tree, DossierBuilder dossiers, ILogger<ReflectionPipeline> logger)
    {
        _db = db;
        _tree = tree;
        _dossiers = dossiers;
        _logger = logger;
    }

    /// <summary>
    /// Check if reflections should run for a thread, with debounce.
    /// Returns prefetched L0 events if ready, null otherwise.
    /// </summary>
    public async Task<List<JournalEvent>?> MaybeTriggerReflectionsAsync(string threadId)
    {
        lock (_triggerLock)
        {
            if (_inflight.Contains(threadId))
                return null;

            if (_lastTrigger.TryGetValue(threadId, out var last) && DateTime.UtcNow - last < Cooldown)
                return null;

            _inflight.Add(threadId);
            _lastTrigger[threadId] = DateTime.UtcNow;
        }

        var l0Events = await _db.GetUnreflectedL0sAsync(threadId);
        if (l0Events.Count < MinEntriesForReflection || l0Events[^1].Role != "assistant")
        {
            lock (_triggerLock) { _inflight.Remove(threadId); }
            return null;
        }

        return l0Events;
    }

    /// <summary>Mark reflection as complete (call from finally block).</summary>
    public void MarkComplete(string threadId)
    {
        lock (_triggerLock) { _inflight.Remove(threadId); }
    }

    /// <summary>
    /// Main reflection pipeline: L1 → higher levels → dossier → mindfulness triggers.
    /// </summary>
    public async Task<ReflectionResult> RunReflectionsAsync(string threadId, List<JournalEvent>? prefetchedL0 = null)
    {
        var l0Events = prefetchedL0 ?? await _db.GetUnreflectedL0sAsync(threadId);
        if (l0Events.Count < MinEntriesForReflection)
            return new ReflectionResult { ThreadId = threadId, Status = "insufficient_l0" };

        if (l0Events[^1].Role != "assistant")
            return new ReflectionResult { ThreadId = threadId, Status = "last_not_assistant" };

        try
        {
            var l1Id = await _tree.CreateL1WithLineageAsync(threadId, l0Events);
            _logger.LogInformation("Created L1 #{Id} for thread {Thread}", l1Id, threadId);

            await _tree.BuildHigherLevelsAsync(threadId);
            await _dossiers.RebuildDossierAsync(threadId);
            await _dossiers.RebuildMasterDossierAsync();

            // Mindfulness triggers: check enabled threads, fire probabilistically
            await TriggerMindfulnessAsync(threadId);

            return new ReflectionResult
            {
                ThreadId = threadId,
                Status = "completed",
                L1Id = l1Id,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reflection pipeline failed for thread {Thread}", threadId);
            return new ReflectionResult { ThreadId = threadId, Status = "error", Error = ex.Message };
        }
    }

    /// <summary>Event raised when a mindfulness thread should fire. Args: thread_id, prompt.</summary>
    public event Func<MindfulnessThread, Task>? OnMindfulnessTriggered;

    private async Task TriggerMindfulnessAsync(string sourceThreadId)
    {
        if (OnMindfulnessTriggered == null) return;

        var threads = await _db.GetEnabledMindfulnessThreadsAsync();
        foreach (var mt in threads)
        {
            // Don't trigger a mindfulness thread from its own reflection cycle
            if (mt.ThreadId == sourceThreadId) continue;

            if (Random.Shared.NextDouble() < mt.Probability)
            {
                _logger.LogInformation("Triggering mindfulness thread '{Name}' ({ThreadId})", mt.Name, mt.ThreadId);
                try { await OnMindfulnessTriggered(mt); }
                catch (Exception ex) { _logger.LogWarning(ex, "Mindfulness thread '{Name}' failed", mt.Name); }
            }
        }
    }
}
