using System.Text.Json;
using System.Text.RegularExpressions;

namespace StewardMcp.Formation;

/// <summary>Shared constants and utilities for the reflection system.</summary>
public static class ReflectionConstants
{
    public const string MasterThreadId = "master_dossier";
    public const string ScriptureThreadId = "scripture_dossier";

    public const int SummaryTargetWords = 300;
    public const int ReflectionTargetWords = 400;
    public const int DossierTargetWords = 500;
    public const int MinEntriesForReflection = 2;
    public const int MaxReflectionLevel = 30; // Effectively unbounded — L30 fires at ~500M L1s
    public const int ScriptureTriggerMod = 8;
    public const int ScriptureTriggerRemainder = 7;
    public const int UnreflectedL0Threshold = 10;

    public const string JsonOutputFormat = """

        OUTPUT FORMAT (valid JSON only, no markdown):
        {
          "summary": "Natural prose text",
          "key_points": ["Point 1", "Point 2"],
          "open_loops": ["Unresolved item 1"],
          "tags": ["theme1", "theme2"]
        }
        """;
}

/// <summary>JSON parsing and rendering utilities for reflection payloads.</summary>
public static class ReflectionHelpers
{
    public static Dictionary<string, JsonElement> ParseJsonMaybeFenced(string text)
    {
        var cleaned = text.Trim();
        var match = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Singleline);
        if (match.Success)
            cleaned = match.Groups[1].Value.Trim();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(cleaned)
                ?? new Dictionary<string, JsonElement>();
        }
        catch
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    public static Dictionary<string, JsonElement> ParsePayload(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new(); }
        catch { return new(); }
    }

    public static string GetString(Dictionary<string, JsonElement> dict, string key)
    {
        if (dict.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? "";
        return "";
    }

    public static List<string> GetStringList(Dictionary<string, JsonElement> dict, string key)
    {
        if (!dict.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .ToList();
    }

    public static string RenderReflectionText(string summary, List<string> keyPoints, List<string> tags)
    {
        var parts = new List<string> { summary };
        if (keyPoints.Count > 0) parts.Add("Key points: " + string.Join("; ", keyPoints));
        if (tags.Count > 0) parts.Add("Tags: " + string.Join(", ", tags));
        return string.Join("\n", parts);
    }

    public static object BuildPayload(Dictionary<string, JsonElement> parsed) => new
    {
        summary = GetString(parsed, "summary"),
        key_points = GetStringList(parsed, "key_points"),
        open_loops = GetStringList(parsed, "open_loops"),
        tags = GetStringList(parsed, "tags"),
    };
}

public class ReflectionResult
{
    public string ThreadId { get; set; } = "";
    public string Status { get; set; } = "";
    public long? L1Id { get; set; }
    public int? L1Count { get; set; }
    public string? Error { get; set; }
}
