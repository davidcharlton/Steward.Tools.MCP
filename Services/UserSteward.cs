using StewardMcp.Config;
using StewardMcp.Data;
using StewardMcp.Formation;

namespace StewardMcp.Services;

/// <summary>
/// All the engine services tied to a single user's data directory.
/// Held by MCP tool classes; lifetime owned by the host (stdio = process,
/// HTTP = UserStewardFactory + LRU cache). Not IDisposable — DI must not
/// dispose this, because the underlying services live across many requests.
/// </summary>
public class UserSteward
{
    public string UserId { get; init; } = "";
    public StewardConfig Config { get; init; } = null!;
    public StewardDb Db { get; init; } = null!;
    public VectorStore Vectors { get; init; } = null!;
    public ReflectionPipeline Pipeline { get; init; } = null!;
    public TreeBuilder Tree { get; init; } = null!;
    public DossierBuilder Dossiers { get; init; } = null!;
    public Scripture Scripture { get; init; } = null!;
    public Canon Canon { get; init; } = null!;
    public LlmService Llm { get; init; } = null!;
}
