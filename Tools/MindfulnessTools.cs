using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StewardMcp.Data;

namespace StewardMcp.Tools;

[McpServerToolType]
public class MindfulnessTools
{
    private readonly StewardDb _db;

    public MindfulnessTools(StewardDb db)
    {
        _db = db;
    }

    [McpServerTool]
    [Description("List all mindfulness threads — background topics the steward reflects on regularly.")]
    public async Task<string> MindfulnessListThreads()
    {
        var threads = await _db.GetEnabledMindfulnessThreadsAsync();
        return JsonSerializer.Serialize(new
        {
            count = threads.Count,
            threads = threads.Select(t => new
            {
                threadId = t.ThreadId,
                name = t.Name,
                probability = t.Probability,
                prompt = t.Prompt.Length > 200 ? t.Prompt[..200] + "..." : t.Prompt,
            }).ToList(),
        });
    }

    [McpServerTool]
    [Description("Create or update a mindfulness thread — a background topic the steward will regularly reflect on. Examples: pattern recognition, user wellness, documentation freshness, project status.")]
    public async Task<string> MindfulnessUpsertThread(
        [Description("Thread ID (e.g., 'mindful_patterns', 'mindful_wellness')")] string threadId,
        [Description("Short name for this mindfulness focus")] string name,
        [Description("The prompt/instructions for what to reflect on")] string prompt,
        [Description("Probability of triggering per reflection cycle (0.0-1.0, default 0.10)")] double probability = 0.10,
        [Description("Whether this thread is active")] bool enabled = true)
    {
        await _db.UpsertMindfulnessThreadAsync(new MindfulnessThread
        {
            ThreadId = threadId,
            Name = name,
            Prompt = prompt,
            Probability = probability,
            Enabled = enabled,
        });

        return JsonSerializer.Serialize(new { ok = true, threadId, name, probability, enabled });
    }
}
