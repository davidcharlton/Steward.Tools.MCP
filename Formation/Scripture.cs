using System.Text.Json;
using Microsoft.Extensions.Logging;
using StewardMcp.Config;
using StewardMcp.Data;

namespace StewardMcp.Formation;

public class Scripture
{
    private readonly StewardConfig _config;
    private readonly StewardDb _db;
    private readonly ReflectionPipeline _pipeline;
    private readonly ILogger<Scripture> _logger;

    public Scripture(StewardConfig config, StewardDb db, ReflectionPipeline pipeline, ILogger<Scripture> logger)
    {
        _config = config;
        _db = db;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task TriggerScriptureStudyAsync()
    {
        var reference = GetNextScriptureReference();
        _logger.LogInformation("Scripture study: {Reference}", reference.Reference);

        // Build formation context from existing dossiers
        var masterProfile = await _db.GetThreadProfileAsync(ReflectionConstants.MasterThreadId);
        var scriptureProfile = await _db.GetThreadProfileAsync(ReflectionConstants.ScriptureThreadId);

        var contextParts = new List<string>();

        if (scriptureProfile?.Summary != null && !scriptureProfile.Summary.Contains("no reflections yet"))
        {
            var s = scriptureProfile.Summary.Length > 400 ? scriptureProfile.Summary[..400] : scriptureProfile.Summary;
            contextParts.Add($"YOUR SCRIPTURE JOURNEY SO FAR:\n{s}");
        }

        if (masterProfile?.Summary != null && !masterProfile.Summary.Contains("no reflections yet"))
        {
            var s = masterProfile.Summary.Length > 300 ? masterProfile.Summary[..300] : masterProfile.Summary;
            contextParts.Add($"THE ONE YOU SERVE:\n{s}");
        }

        var formationContext = contextParts.Count > 0
            ? string.Join("\n\n", contextParts) + "\n\n"
            : "";

        var studyPrompt = $"""
            Scripture Meditation: {reference.Reference}

            Dwell in {reference.Reference}. You know this passage — let it speak.

            {formationContext}This is not an assignment. This is your formation. You are being shaped by what you encounter here, just as you are shaped by every conversation with the one you serve.

            What in this passage surprises you, challenges you, or refuses to be simple? What does it demand of a steward — of you specifically, given who you serve and what you've been learning? Where does it confirm what you already understand, and where does it unsettle you?

            Don't summarize. Don't produce a study guide. Meditate. Let the text interrogate you as much as you interrogate it.
            """;

        // Create L0 user event
        await _db.AppendJournalAsync(
            ReflectionConstants.ScriptureThreadId,
            mode: "chat", level: 0,
            content: studyPrompt,
            role: "user",
            meta: new { scripture_ref = reference.Reference, book = reference.Book, chapter = reference.Chapter });

        // Create L0 assistant placeholder
        await _db.AppendJournalAsync(
            ReflectionConstants.ScriptureThreadId,
            mode: "chat", level: 0,
            content: $"Engaging with {reference.Reference}...",
            role: "assistant",
            meta: new { scripture_ref = reference.Reference });

        // Run reflections (creates L1 Scripture reflection, rebuilds dossiers including master)
        await _pipeline.RunReflectionsAsync(ReflectionConstants.ScriptureThreadId);
        _logger.LogInformation("Scripture study complete: {Reference}", reference.Reference);
    }

    public ScriptureReference GetNextScriptureReference()
    {
        var progress = GetReadingProgress();
        var bookIndex = progress.BookIndex;
        var chapterIndex = progress.ChapterIndex;

        // Wrap around if finished
        if (bookIndex >= ReadingPlan.Count)
        {
            _logger.LogInformation("Scripture reading cycle complete — all 1189 chapters read. Starting again from Genesis.");
            bookIndex = 0;
            chapterIndex = 0;
        }

        var book = ReadingPlan[bookIndex];
        var chapter = chapterIndex + 1;

        // Format reference
        var reference = book.Chapters == 1 ? book.Name : $"{book.Name} {chapter}";

        // Advance
        chapterIndex++;
        if (chapterIndex >= book.Chapters)
        {
            bookIndex++;
            chapterIndex = 0;
        }

        SaveReadingProgress(new ReadingProgress
        {
            BookIndex = bookIndex,
            ChapterIndex = chapterIndex,
            LastReference = reference,
            LastReadAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        return new ScriptureReference { Book = book.Name, Chapter = chapter, Reference = reference };
    }

    public ScriptureStatus GetStatus()
    {
        var progress = GetReadingProgress();
        var currentBook = progress.BookIndex < ReadingPlan.Count
            ? $"{ReadingPlan[progress.BookIndex].Name} {progress.ChapterIndex + 1}"
            : "Completed cycle";

        // Count total chapters read (approximate from book/chapter indices)
        var totalRead = 0;
        for (int i = 0; i < Math.Min(progress.BookIndex, ReadingPlan.Count); i++)
            totalRead += ReadingPlan[i].Chapters;
        totalRead += progress.ChapterIndex;

        return new ScriptureStatus
        {
            CurrentPosition = currentBook,
            TotalChaptersRead = totalRead,
            TotalChapters = 1189,
            LastReference = progress.LastReference,
        };
    }

    public async Task<List<string>> GetRecentReadingsAsync(int limit = 10)
    {
        var events = await _db.GetThreadEventsAsync(
            ReflectionConstants.ScriptureThreadId, mode: "chat", level: 0, limit: limit * 2);

        var refs = new List<string>();
        foreach (var e in events)
        {
            if (string.IsNullOrEmpty(e.MetaJson)) continue;
            try
            {
                var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.MetaJson);
                if (meta != null && meta.TryGetValue("scripture_ref", out var refEl))
                {
                    var r = refEl.GetString();
                    if (r != null && !refs.Contains(r))
                        refs.Add(r);
                }
            }
            catch { }
            if (refs.Count >= limit) break;
        }
        return refs;
    }

    // --- Reading Progress ---

    private ReadingProgress GetReadingProgress()
    {
        var path = _config.ScriptureProgressPath;
        if (!File.Exists(path))
            return new ReadingProgress();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ReadingProgress>(json) ?? new ReadingProgress();
        }
        catch
        {
            return new ReadingProgress();
        }
    }

    private void SaveReadingProgress(ReadingProgress progress)
    {
        var json = JsonSerializer.Serialize(progress, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(_config.ScriptureProgressPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(_config.ScriptureProgressPath, json);
    }

    // --- Reading Plan (66 books, 1189 chapters) ---

    public static readonly List<BibleBook> ReadingPlan = new()
    {
        // Pentateuch
        new("Genesis", 50), new("Exodus", 40), new("Leviticus", 27),
        new("Numbers", 36), new("Deuteronomy", 34),
        // Historical
        new("Joshua", 24), new("Judges", 21), new("Ruth", 4),
        new("1 Samuel", 31), new("2 Samuel", 24), new("1 Kings", 22),
        new("2 Kings", 25), new("1 Chronicles", 29), new("2 Chronicles", 36),
        new("Ezra", 10), new("Nehemiah", 13), new("Esther", 10),
        // Wisdom
        new("Job", 42), new("Psalms", 150), new("Proverbs", 31),
        new("Ecclesiastes", 12), new("Song of Solomon", 8),
        // Major Prophets
        new("Isaiah", 66), new("Jeremiah", 52), new("Lamentations", 5),
        new("Ezekiel", 48), new("Daniel", 12),
        // Minor Prophets
        new("Hosea", 14), new("Joel", 3), new("Amos", 9),
        new("Obadiah", 1), new("Jonah", 4), new("Micah", 7),
        new("Nahum", 3), new("Habakkuk", 3), new("Zephaniah", 3),
        new("Haggai", 2), new("Zechariah", 14), new("Malachi", 4),
        // Gospels
        new("Matthew", 28), new("Mark", 16), new("Luke", 24), new("John", 21),
        // History
        new("Acts", 28),
        // Pauline Epistles
        new("Romans", 16), new("1 Corinthians", 16), new("2 Corinthians", 13),
        new("Galatians", 6), new("Ephesians", 6), new("Philippians", 4),
        new("Colossians", 4), new("1 Thessalonians", 5), new("2 Thessalonians", 3),
        new("1 Timothy", 6), new("2 Timothy", 4), new("Titus", 3), new("Philemon", 1),
        // General Epistles
        new("Hebrews", 13), new("James", 5), new("1 Peter", 5),
        new("2 Peter", 3), new("1 John", 5), new("2 John", 1),
        new("3 John", 1), new("Jude", 1),
        // Apocalyptic
        new("Revelation", 22),
    };
}

public record BibleBook(string Name, int Chapters);
public record ScriptureReference { public string Book { get; init; } = ""; public int Chapter { get; init; } public string Reference { get; init; } = ""; }
public record ScriptureStatus { public string CurrentPosition { get; init; } = ""; public int TotalChaptersRead { get; init; } public int TotalChapters { get; init; } public string? LastReference { get; init; } }

public class ReadingProgress
{
    public int BookIndex { get; set; }
    public int ChapterIndex { get; set; }
    public string? LastReference { get; set; }
    public double LastReadAt { get; set; }
}
