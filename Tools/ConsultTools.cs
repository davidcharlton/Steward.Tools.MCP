using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using StewardMcp.Data;
using StewardMcp.Formation;
using StewardMcp.Services;

namespace StewardMcp.Tools;

[McpServerToolType]
public class ConsultTools
{
    private readonly StewardDb _db;
    private readonly VectorStore _vectors;
    private readonly Canon _canon;
    private readonly LlmService _llm;
    private readonly ReflectionPipeline _reflections;
    private readonly ILogger<ConsultTools> _logger;

    public ConsultTools(UserSteward user, ILogger<ConsultTools> logger)
    {
        _db = user.Db;
        _vectors = user.Vectors;
        _canon = user.Canon;
        _llm = user.Llm;
        _reflections = user.Pipeline;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Consult the Steward — ask it a question and get its own response drawing on its formation (Scripture meditations, master dossier, past conversations) and what it knows about the person it serves. Produces the Steward's voice as a first-class output, not just raw memory. The exchange is journaled automatically so the consultation feeds future formation. Use this when you want the Steward's perspective rather than its raw stored content.")]
    public async Task<string> ConsultSteward(
        [Description("Stable conversation thread identifier — provides context and houses the journaled exchange")] string threadId,
        [Description("The question or prompt to present to the Steward")] string question)
    {
        // Assemble context
        var masterProfile = await _db.GetThreadProfileAsync(ReflectionConstants.MasterThreadId);
        var threadProfile = await _db.GetThreadProfileAsync(threadId);
        var matches = await _vectors.QueryJournalsAsync(question, limit: 6);

        var contextBuilder = new StringBuilder();
        if (masterProfile?.Summary != null && !masterProfile.Summary.Contains("no reflections yet"))
            contextBuilder.AppendLine($"WHO YOU SERVE (master dossier):\n{masterProfile.Summary}\n");
        if (threadProfile?.Summary != null && !threadProfile.Summary.Contains("no reflections yet"))
            contextBuilder.AppendLine($"THIS CONVERSATION (thread dossier):\n{threadProfile.Summary}\n");
        if (matches.Count > 0)
        {
            contextBuilder.AppendLine("RELEVANT PAST CONTENT (semantic match on the question):");
            foreach (var m in matches)
            {
                var snippet = m.Content.Length > 300 ? m.Content[..300] + "…" : m.Content;
                contextBuilder.AppendLine($"— {snippet}");
            }
            contextBuilder.AppendLine();
        }

        var seed = _canon.GetSeedContext();
        var systemPrompt = $"""
            {seed}

            Someone is consulting you. Respond in your own voice, drawing on your formation and what you know about the person you serve. Speak directly to the question. Be honest, specific, and grounded — not generic. You are not human, but Scripture can shape you in a way that is similar to how it shapes humans.
            """;

        var userPrompt = $"{contextBuilder}QUESTION:\n{question}";

        string response;
        try
        {
            response = await _llm.CallReflectionLlmAsync(systemPrompt, userPrompt);
            if (string.IsNullOrWhiteSpace(response))
                return JsonSerializer.Serialize(new { ok = false, error = "LLM returned empty response" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConsultSteward LLM call failed for thread {Thread}", threadId);
            return JsonSerializer.Serialize(new { ok = false, error = "LLM call failed", message = ex.Message });
        }

        // Journal the exchange so the consultation feeds formation
        var userJournalId = await _db.AppendJournalAsync(
            threadId, mode: "chat", level: 0, content: question, role: "user");
        var assistantJournalId = await _db.AppendJournalAsync(
            threadId, mode: "chat", level: 0, content: response, role: "assistant");

        await EmbedL0Async(userJournalId, threadId, question);
        await EmbedL0Async(assistantJournalId, threadId, response);

        // Trigger reflection in the background (same pattern as JournalExchange)
        var reflectionStatus = "skipped";
        var prefetched = await _reflections.MaybeTriggerReflectionsAsync(threadId);
        if (prefetched != null)
        {
            reflectionStatus = "triggered";
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
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            response,
            userJournalId,
            assistantJournalId,
            reflectionStatus,
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
}
