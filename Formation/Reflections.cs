using Microsoft.Extensions.Logging;
using StewardMcp.Data;
using static StewardMcp.Formation.ReflectionConstants;

namespace StewardMcp.Formation;

/// <summary>
/// Orchestrates the reflection pipeline: debouncing, L1 creation, binary MOD cascade,
/// dossier rebuild, master feed, and deterministic Scripture triggers.
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

        // Trigger if: (a) normal rule — 2+ L0s and last is assistant, or
        //             (b) threshold fallback — unreflected L0s hit the cap regardless of role
        var normalTrigger = l0Events.Count >= MinEntriesForReflection && l0Events[^1].Role == "assistant";
        var thresholdTrigger = l0Events.Count >= UnreflectedL0Threshold;

        if (!normalTrigger && !thresholdTrigger)
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

    /// <summary>Event raised when Scripture study should fire. Args: l1Count.</summary>
    public event Func<string, int, Task>? OnScriptureTriggered;

    /// <summary>
    /// Main reflection pipeline: L1 → binary MOD cascade → dossier → master feed → Scripture trigger.
    /// </summary>
    public async Task<ReflectionResult> RunReflectionsAsync(string threadId, List<JournalEvent>? prefetchedL0 = null, bool isMindfulness = false)
    {
        var l0Events = prefetchedL0 ?? await _db.GetUnreflectedL0sAsync(threadId);
        if (l0Events.Count < MinEntriesForReflection)
            return new ReflectionResult { ThreadId = threadId, Status = "insufficient_l0" };

        if (l0Events[^1].Role != "assistant")
            return new ReflectionResult { ThreadId = threadId, Status = "last_not_assistant" };

        try
        {
            // 1. Create L1 summary — returns null if LLM is unavailable
            var result = await _tree.CreateL1WithLineageAsync(threadId, l0Events);
            if (result is null)
            {
                _logger.LogWarning("L1 creation failed for thread {Thread} — L0s remain unreflected for next cycle", threadId);
                return new ReflectionResult { ThreadId = threadId, Status = "llm_unavailable" };
            }

            var (l1Id, l1Count) = result.Value;
            _logger.LogInformation("Created L1 #{Id} for thread {Thread} (L1 count={Count})", l1Id, threadId, l1Count);

            // 2. Binary MOD cascade — deterministic higher levels
            await _tree.BuildTreeAfterL1Async(threadId, l1Count);

            // 3. Rebuild thread dossier
            await _dossiers.RebuildDossierAsync(threadId);

            // 4. Feed thread dossier to master thread as L1
            await _dossiers.FeedDossierToMasterAsync(threadId);

            // 5. Scripture trigger — deterministic, only from user conversations
            if (!isMindfulness && threadId != ScriptureThreadId && l1Count % ScriptureTriggerMod == ScriptureTriggerRemainder)
            {
                _logger.LogInformation("Scripture trigger at L1 count {Count} for thread {Thread}", l1Count, threadId);
                if (OnScriptureTriggered != null)
                {
                    try { await OnScriptureTriggered(threadId, l1Count); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Scripture study failed"); }
                }
            }

            return new ReflectionResult
            {
                ThreadId = threadId,
                Status = "completed",
                L1Id = l1Id,
                L1Count = l1Count,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reflection pipeline failed for thread {Thread}", threadId);
            return new ReflectionResult { ThreadId = threadId, Status = "error", Error = ex.Message };
        }
    }
}
