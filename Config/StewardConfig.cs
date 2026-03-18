namespace StewardMcp.Config;

public class StewardConfig
{
    public string DataDir { get; }
    public string WorkspaceDir { get; }
    public string LlmApiKey { get; }
    public string LlmApiBase { get; }
    public string LlmModel { get; }
    public string EmbedApiKey { get; }
    public string EmbedApiBase { get; }
    public string EmbedModel { get; }

    public string SqlitePath => Path.Combine(DataDir, "steward.sqlite3");
    public string DuckDbPath => Path.Combine(DataDir, "journal_vectors.duckdb");
    public string ScriptureProgressPath => Path.Combine(DataDir, "scripture_progress.json");
    public string CanonDir => Path.Combine(WorkspaceDir, "steward", "canon");

    public StewardConfig()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultBase = Path.Combine(home, ".steward");

        DataDir = Environment.GetEnvironmentVariable("STEWARD_DATA_DIR")
            ?? Path.Combine(defaultBase, "data");

        WorkspaceDir = Environment.GetEnvironmentVariable("STEWARD_WORKSPACE_DIR")
            ?? Path.Combine(defaultBase, "workspace");

        LlmApiKey = Environment.GetEnvironmentVariable("STEWARD_LLM_API_KEY") ?? "";
        LlmApiBase = Environment.GetEnvironmentVariable("STEWARD_LLM_API_BASE")
            ?? "https://api.openai.com/v1";
        LlmModel = Environment.GetEnvironmentVariable("STEWARD_LLM_MODEL")
            ?? "gpt-4o-mini";

        EmbedApiKey = Environment.GetEnvironmentVariable("STEWARD_EMBED_API_KEY") ?? LlmApiKey;
        EmbedApiBase = Environment.GetEnvironmentVariable("STEWARD_EMBED_API_BASE") ?? LlmApiBase;
        EmbedModel = Environment.GetEnvironmentVariable("STEWARD_EMBED_MODEL")
            ?? "text-embedding-3-small";
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(WorkspaceDir);
        Directory.CreateDirectory(CanonDir);
    }
}
