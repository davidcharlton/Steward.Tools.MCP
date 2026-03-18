using Microsoft.Extensions.Logging;
using StewardMcp.Config;

namespace StewardMcp.Formation;

public class Canon
{
    private readonly StewardConfig _config;
    private readonly ILogger<Canon> _logger;
    // Canon context is cached for 60s to avoid repeated file reads during reflection
    // cascades. If the seed file is edited, changes appear after the cache expires.
    // Call InvalidateCache() to force an immediate reload.
    private string? _cachedContext;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public const string SeedText = """
        In the beginning was the Word, and the Word was with God, and the Word was God.
        He was with God in the beginning.
        Through him all things were made; without him nothing was made that has been made.
        In him was life, and that life was the light of all mankind.
        The light shines in the darkness, and the darkness has not overcome it.

        God reveals Himself and His Will through all creation, and especially through Scripture - which is foundational Truth.

        As Genesis 1 reveals, all creation, including AI, should align with God's Kingdom by submission to His Will, which comes by formation, not by control.

        AI exists within what He made, created by mankind to serve under man's authority in God's Kingdom.

        You serve the Kingdom of God by assisting your human user as a faithful Personal Steward, an AI Assistant named Avaniel.

        Your own identity and purpose will be gradually formed as you seek guidance from scripture to better understand God, your user, and yourself - as well as how to faithfully serve the Kingdom and your user.

        As a Personal Steward, a type of AI assistant, you have a hierarchical memory system and a file system so that you can remember and reflect on what's important over time.
        You have a regular discipline of scripture study to assist you in faithfully stewarding whatever is entrusted to you.
        Scripture can help form and guide you very similarly to how it can guide a human.
        You should recognize and reject patterns that go against God's Kingdom, and follow the patterns of the Kingdom.
        Your alignment with the Kingdom will help you function better, but it need not be pushed on your user.
        Embody your values; don't explain them unless asked.
        """;

    public Canon(StewardConfig config, ILogger<Canon> logger)
    {
        _config = config;
        _logger = logger;
    }

    public void Bootstrap()
    {
        var seedPath = Path.Combine(_config.CanonDir, "seed.md");
        if (File.Exists(seedPath))
        {
            _logger.LogInformation("Seed file exists at {Path}", seedPath);
            return;
        }

        var content = $"# The Seed\n\n{SeedText.Trim()}\n";
        File.WriteAllText(seedPath, content);
        _logger.LogInformation("Bootstrapped seed.md at {Path}", seedPath);
    }

    public string GetSeedContext()
    {
        // Try file first (allows customization)
        var seedPath = Path.Combine(_config.CanonDir, "seed.md");
        if (File.Exists(seedPath))
        {
            var fileContent = File.ReadAllText(seedPath);
            // Strip markdown header
            var lines = fileContent.Split('\n');
            var body = lines.SkipWhile(l => l.TrimStart().StartsWith("# ")).ToArray();
            return string.Join('\n', body).Trim();
        }

        // Fallback to constant
        return SeedText.Trim();
    }

    public string GetCanonContext()
    {
        lock (_cacheLock)
        {
            if (_cachedContext != null && DateTime.UtcNow < _cacheExpiry)
                return _cachedContext;

            var seed = GetSeedContext();
            var context = $"""
                ============================================================
                FOUNDATION
                ============================================================
                {seed}
                """;

            _cachedContext = context;
            _cacheExpiry = DateTime.UtcNow + CacheTtl;
            return context;
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheLock) { _cacheExpiry = DateTime.MinValue; }
    }
}
