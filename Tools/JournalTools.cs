using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using StewardMcp.Data;
using StewardMcp.Formation;

namespace StewardMcp.Tools;

[McpServerToolType]
public class JournalTools
{
    private readonly StewardDb _db;
    private readonly VectorStore _vectors;
    private readonly ReflectionPipeline _reflections;
    private readonly ILogger<JournalTools> _logger;

    public JournalTools(StewardDb db, VectorStore vectors, ReflectionPipeline reflections, ILogger<JournalTools> logger)
    {
        _db = db;
        _vectors = vectors;
        _reflections = reflections;
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

        // After assistant messages, check if reflections should trigger
        if (role == "assistant")
            reflectionStatus = await TriggerReflectionsAsync(threadId);

        var result = new { ok = true, journalId, reflectionStatus };
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

        var reflectionStatus = await TriggerReflectionsAsync(threadId);

        var result = new { ok = true, userJournalId = userId, assistantJournalId = assistantId, reflectionStatus };
        return JsonSerializer.Serialize(result);
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

    private async Task<string> TriggerReflectionsAsync(string threadId)
    {
        var prefetched = await _reflections.MaybeTriggerReflectionsAsync(threadId);
        if (prefetched == null)
            return "skipped";

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

        return "triggered";
    }
}
