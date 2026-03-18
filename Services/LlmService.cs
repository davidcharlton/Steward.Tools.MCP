using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StewardMcp.Config;

namespace StewardMcp.Services;

public class LlmService : IEmbeddingProvider
{
    private readonly StewardConfig _config;
    private readonly ILogger<LlmService> _logger;
    private readonly HttpClient _llmClient;
    private readonly HttpClient _embedClient;

    private const int MaxRetries = 2;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    public LlmService(StewardConfig config, ILogger<LlmService> logger)
    {
        _config = config;
        _logger = logger;

        _llmClient = CreateClient(config.LlmApiBase, config.LlmApiKey);
        _embedClient = CreateClient(config.EmbedApiBase, config.EmbedApiKey);
    }

    private static HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(90),
        };
        if (!string.IsNullOrEmpty(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    public async Task<string> CallReflectionLlmAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.4,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = _config.LlmModel,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature,
            max_tokens = 1500,
        };

        var json = JsonSerializer.Serialize(payload);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                _logger.LogDebug("Calling reflection LLM ({Model}), attempt {Attempt}...", _config.LlmModel, attempt + 1);
                var response = await _llmClient.PostAsync("chat/completions", content, cancellationToken);

                if (IsTransient(response.StatusCode) && attempt < MaxRetries)
                {
                    _logger.LogWarning("LLM returned {Status}, retrying in {Delay}s...",
                        (int)response.StatusCode, RetryDelays[attempt].TotalSeconds);
                    await Task.Delay(RetryDelays[attempt], cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson);
                var text = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
                _logger.LogDebug("Reflection LLM returned {Length} chars", text.Length);
                return text;
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransientException(ex))
            {
                _logger.LogWarning(ex, "LLM call failed (attempt {Attempt}), retrying in {Delay}s...",
                    attempt + 1, RetryDelays[attempt].TotalSeconds);
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
        }

        // All retries exhausted — return empty rather than crashing background tasks
        _logger.LogError("LLM call failed after {MaxRetries} retries, returning empty response", MaxRetries + 1);
        return "";
    }

    public async Task<float[][]> EmbedTextsAsync(string[] texts, CancellationToken cancellationToken = default)
    {
        if (texts.Length == 0) return [];

        var payload = new
        {
            model = _config.EmbedModel,
            input = texts,
        };

        var json = JsonSerializer.Serialize(payload);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                _logger.LogDebug("Embedding {Count} text(s) ({Model}), attempt {Attempt}...",
                    texts.Length, _config.EmbedModel, attempt + 1);
                var response = await _embedClient.PostAsync("embeddings", content, cancellationToken);

                if (IsTransient(response.StatusCode) && attempt < MaxRetries)
                {
                    _logger.LogWarning("Embed API returned {Status}, retrying in {Delay}s...",
                        (int)response.StatusCode, RetryDelays[attempt].TotalSeconds);
                    await Task.Delay(RetryDelays[attempt], cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<EmbeddingResponse>(responseJson);

                return result?.Data?
                    .OrderBy(d => d.Index)
                    .Select(d => d.Embedding ?? [])
                    .ToArray() ?? [];
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransientException(ex))
            {
                _logger.LogWarning(ex, "Embed call failed (attempt {Attempt}), retrying in {Delay}s...",
                    attempt + 1, RetryDelays[attempt].TotalSeconds);
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
        }

        _logger.LogError("Embed call failed after {MaxRetries} retries, returning empty", MaxRetries + 1);
        return [];
    }

    private static bool IsTransient(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout
            or System.Net.HttpStatusCode.InternalServerError;

    private static bool IsTransientException(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;

    // Response models
    private class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }
    }

    private class Choice
    {
        [JsonPropertyName("message")]
        public MessageContent? Message { get; set; }
    }

    private class MessageContent
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private class EmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingData>? Data { get; set; }
    }

    private class EmbeddingData
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
