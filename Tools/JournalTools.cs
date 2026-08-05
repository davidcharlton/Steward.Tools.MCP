using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using StewardMcp.Data;
using StewardMcp.Formation;
using StewardMcp.Services;

namespace StewardMcp.Tools;

[McpServerToolType]
public class JournalTools
{
    private readonly StewardDb _db;
    private readonly VectorStore _vectors;
    private readonly ReflectionPipeline _reflections;
    private readonly TreeBuilder _tree;
    private readonly DossierBuilder _dossiers;
    private readonly ILogger<JournalTools> _logger;

    public JournalTools(UserSteward user, ILogger<JournalTools> logger)
    {
        _db = user.Db;
        _vectors = user.Vectors;
        _reflections = user.Pipeline;
        _tree = user.Tree;
        _dossiers = user.Dossiers;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Record a conversation message. Call this for each user message you receive and each assistant response you send, to build the steward's memory. The reflection system will automatically process exchanges in the background.")]
    public async Task<string> JournalMessage(
        [Description("Stable conversation thread identifier")] string threadId,
        [Description("Message role: 'user' or 'assistant'")] string role,
        [Description("The message text")] string content)
    {
        var journalId = await _db.AppendJournalAsync(
            threadId, mode: "chat", level: 0, content: content, role: role);

        // Embed L0 for direct semantic search
        await EmbedL0Async(journalId, threadId, content);

        var reflectionStatus = "none";
        int? l1Count = null;

        // After assistant messages, check if reflections should trigger
        if (role == "assistant")
            (reflectionStatus, l1Count) = await TriggerReflectionsAsync(threadId);

        var result = new { ok = true, journalId, reflectionStatus, l1Count };
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool]
    [Description("Record a complete user-assistant exchange in one call. Writes both messages as L0 events and triggers background reflection. Use this instead of calling journal_message twice.")]
    public async Task<string> JournalExchange(
        [Description("Stable conversation thread identifier")] string threadId,
        [Description("The user's message")] string userMessage,
        [Description("The assistant's response")] string assistantMessage)
    {
        var userId = await _db.AppendJournalAsync(
            threadId, mode: "chat", level: 0, content: userMessage, role: "user");
        var assistantId = await _db.AppendJournalAsync(
            threadId, mode: "chat", level: 0, content: assistantMessage, role: "assistant");

        // Embed both L0s for direct semantic search
        await EmbedL0Async(userId, threadId, userMessage);
        await EmbedL0Async(assistantId, threadId, assistantMessage);

        var (reflectionStatus, l1Count) = await TriggerReflectionsAsync(threadId);

        var result = new { ok = true, userJournalId = userId, assistantJournalId = assistantId, reflectionStatus, l1Count };
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool]
    [Description("Checkpoint a batch of conversation messages into the steward's memory. Use this to feed exchanges from any system (Claude Code, ChatGPT, email, etc.) into the steward's persistent memory. Messages are journaled as L0 events and reflections are triggered automatically. Trailing user messages (typically the meta-instruction that triggered the checkpoint) are dropped so reflection fires on a completed exchange. Each message may carry an optional externalId (e.g., source system's message ID) for idempotent re-imports — messages with an externalId that already exists in this thread are skipped. This is the 'memory stick' write-side — any host can contribute context.")]
    public async Task<string> CheckpointConversation(
        [Description("Stable conversation thread identifier")] string threadId,
        [Description("Array of messages, each with 'role' (user/assistant), 'content', and optional 'externalId' for dedupe")] List<CheckpointMessage> messages)
    {
        if (messages.Count == 0)
            return JsonSerializer.Serialize(new { ok = false, error = "No messages provided" });

        // Drop trailing user messages: usually the "please checkpoint" meta-turn, and
        // they leave reflection on a half-exchange boundary. Host can re-send in the next batch.
        var trimmed = new List<CheckpointMessage>(messages);
        while (trimmed.Count > 0 && (trimmed[^1].Role?.ToLower() ?? "user") != "assistant")
            trimmed.RemoveAt(trimmed.Count - 1);

        if (trimmed.Count == 0)
            return JsonSerializer.Serialize(new { ok = false, error = "No assistant messages in batch after trimming trailing user messages" });

        var journalIds = new List<long>();
        var dedupeCount = 0;
        foreach (var msg in trimmed)
        {
            if (!string.IsNullOrEmpty(msg.ExternalId))
            {
                var existing = await _db.FindJournalByExternalIdAsync(threadId, msg.ExternalId);
                if (existing != null)
                {
                    dedupeCount++;
                    continue;
                }
            }

            var role = msg.Role?.ToLower() ?? "user";
            var id = await _db.AppendJournalAsync(
                threadId, mode: "chat", level: 0, content: msg.Content ?? "", role: role,
                externalId: string.IsNullOrEmpty(msg.ExternalId) ? null : msg.ExternalId);
            journalIds.Add(id);
            await EmbedL0Async(id, threadId, msg.Content ?? "");
        }

        string reflectionStatus = "none";
        int? l1Count = null;
        if (journalIds.Count > 0)
            (reflectionStatus, l1Count) = await TriggerReflectionsAsync(threadId);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            journalCount = journalIds.Count,
            trimmedCount = messages.Count - trimmed.Count,
            dedupeCount,
            firstJournalId = journalIds.Count > 0 ? journalIds[0] : (long?)null,
            lastJournalId = journalIds.Count > 0 ? journalIds[^1] : (long?)null,
            reflectionStatus,
            l1Count,
        });
    }

    [McpServerTool]
    [Description("Feed pre-summarized entries directly as L1 into the steward's reflection tree. Use this to seed the steward with conversation history from another system — the host summarizes its own conversations and the steward stores and cascades them. No LLM call on the steward's side. Great for onboarding: ChatGPT summarizes its last 20 conversations, sends them here, and the steward immediately has a rich dossier. Each entry may carry an optional externalId (e.g., source conversation ID) for idempotent re-imports — entries with an externalId that already exists in this thread are skipped.")]
    public async Task<string> CheckpointSummary(
        [Description("Stable conversation thread identifier")] string threadId,
        [Description("Array of pre-summarized entries to insert as L1s. Each may include 'externalId' for dedupe on re-import.")] List<SummaryEntry> summaries)
    {
        if (summaries.Count == 0)
            return JsonSerializer.Serialize(new { ok = false, error = "No summaries provided" });

        var l1Ids = new List<long>();
        var dedupeCount = 0;
        int l1Count = 0;

        foreach (var entry in summaries)
        {
            if (!string.IsNullOrEmpty(entry.ExternalId))
            {
                var existing = await _db.FindJournalByExternalIdAsync(threadId, entry.ExternalId);
                if (existing != null)
                {
                    dedupeCount++;
                    continue;
                }
            }

            var content = entry.Summary ?? "";
            var payload = new
            {
                summary = content,
                key_points = entry.KeyPoints ?? new List<string>(),
                open_loops = new List<string>(),
                tags = entry.Tags ?? new List<string>(),
            };
            var meta = new { source = "checkpoint_summary", source_system = entry.SourceSystem };

            var l1Id = await _db.AppendJournalAsync(
                threadId, mode: "reflection", level: 1,
                content: content, payload: payload, meta: meta,
                externalId: string.IsNullOrEmpty(entry.ExternalId) ? null : entry.ExternalId);
            l1Ids.Add(l1Id);

            l1Count = await _db.GetL1CountForThreadAsync(threadId);

            // Embed for semantic search
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                await _vectors.UpsertJournalEmbeddingAsync(l1Id, threadId, 1, "reflection", now, content);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to embed checkpoint L1 #{Id}", l1Id);
            }
        }

        // Run binary cascade on final L1 count and rebuild dossiers
        if (l1Ids.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _tree.BuildTreeAfterL1Async(threadId, l1Count);
                    await _dossiers.RebuildDossierAsync(threadId);
                    await _dossiers.FeedDossierToMasterAsync(threadId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cascade after checkpoint_summary failed for thread {Thread}", threadId);
                }
            });
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            l1Count,
            insertedCount = l1Ids.Count,
            dedupeCount,
            firstL1Id = l1Ids.Count > 0 ? l1Ids[0] : (long?)null,
            lastL1Id = l1Ids.Count > 0 ? l1Ids[^1] : (long?)null,
        });
    }

    private async Task EmbedL0Async(long journalId, string threadId, string content)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            await _vectors.UpsertJournalEmbeddingAsync(journalId, threadId, 0, "chat", now, content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed L0 #{Id}", journalId);
        }
    }

    private async Task<(string status, int? l1Count)> TriggerReflectionsAsync(string threadId)
    {
        var prefetched = await _reflections.MaybeTriggerReflectionsAsync(threadId);
        if (prefetched == null)
            return ("skipped", null);

        _ = Task.Run(async () =>
        {
            try
            {
                await _reflections.RunReflectionsAsync(threadId, prefetched);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background reflection failed for thread {Thread}", threadId);
            }
            finally
            {
                _reflections.MarkComplete(threadId);
            }
        });

        return ("triggered", null); // l1Count available after background task completes
    }
}

public class CheckpointMessage
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? ExternalId { get; set; }
}

public class SummaryEntry
{
    public string? Summary { get; set; }
    public List<string>? KeyPoints { get; set; }
    public List<string>? Tags { get; set; }
    public string? SourceSystem { get; set; }
    public string? ExternalId { get; set; }
}
