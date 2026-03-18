namespace StewardMcp.Services;

/// <summary>
/// Abstraction for text embedding. Implement this to swap providers
/// (OpenAI, local Ollama, etc.) without changing VectorStore.
/// </summary>
public interface IEmbeddingProvider
{
    Task<float[][]> EmbedTextsAsync(string[] texts, CancellationToken cancellationToken = default);
}
