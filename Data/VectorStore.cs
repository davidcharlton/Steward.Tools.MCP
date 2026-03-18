using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using StewardMcp.Config;
using StewardMcp.Services;

namespace StewardMcp.Data;

public class VectorStore : IDisposable
{
    private readonly StewardConfig _config;
    private readonly IEmbeddingProvider _embedder;
    private readonly ILogger<VectorStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DuckDBConnection? _conn;
    private bool _disabled;

    private const int EmbedDim = 1536;

    /// <summary>True when DuckDB could not be opened (e.g. file locked by another process).</summary>
    public bool IsDisabled => _disabled;

    public VectorStore(StewardConfig config, IEmbeddingProvider embedder, ILogger<VectorStore> logger)
    {
        _config = config;
        _embedder = embedder;
        _logger = logger;
    }

    private DuckDBConnection GetConnection()
    {
        if (_conn is not null)
            return _conn;

        _conn = new DuckDBConnection($"Data Source={_config.DuckDbPath}");
        _conn.Open();
        _logger.LogInformation("DuckDB connection established at {Path}", _config.DuckDbPath);
        return _conn;
    }

    public async Task InitializeAsync(bool retry = false)
    {
        await _lock.WaitAsync();
        try
        {
            if (retry)
            {
                // Dispose stale connection from a previous failed attempt
                _conn?.Dispose();
                _conn = null;
                _disabled = false;
            }

            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS journal_embeddings (
                    journal_id BIGINT PRIMARY KEY,
                    thread_id VARCHAR,
                    level INTEGER,
                    mode VARCHAR,
                    created_at DOUBLE,
                    content VARCHAR,
                    embedding FLOAT[{EmbedDim}]
                );
                CREATE TABLE IF NOT EXISTS file_embeddings (
                    file_path VARCHAR PRIMARY KEY,
                    content VARCHAR,
                    embedding FLOAT[{EmbedDim}],
                    last_modified DOUBLE,
                    file_hash VARCHAR,
                    priority VARCHAR DEFAULT 'normal',
                    category VARCHAR DEFAULT 'general'
                );
                """;
            cmd.ExecuteNonQuery();
            _disabled = false;
            _logger.LogInformation("Vector store schema initialized");
        }
        catch (DuckDBException ex)
        {
            _disabled = true;
            _logger.LogError(ex, "Failed to initialize DuckDB — vector search disabled. " +
                "This usually means another Steward process holds the lock on {Path}", _config.DuckDbPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertJournalEmbeddingAsync(long journalId, string threadId, int level, string mode, double createdAt, string content)
    {
        if (_disabled) return;
        await _lock.WaitAsync();
        try
        {
            var embeddings = await _embedder.EmbedTextsAsync([content]);
            if (embeddings.Length == 0) return;

            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT OR REPLACE INTO journal_embeddings (journal_id, thread_id, level, mode, created_at, content, embedding)
                VALUES ($journal_id, $thread_id, $level, $mode, $created_at, $content, {FormatFloatArray(embeddings[0])})
                """;
            cmd.Parameters.Add(new DuckDBParameter("journal_id", journalId));
            cmd.Parameters.Add(new DuckDBParameter("thread_id", threadId));
            cmd.Parameters.Add(new DuckDBParameter("level", level));
            cmd.Parameters.Add(new DuckDBParameter("mode", mode));
            cmd.Parameters.Add(new DuckDBParameter("created_at", createdAt));
            cmd.Parameters.Add(new DuckDBParameter("content", Truncate(content, 2000)));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed journal {Id}", journalId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertFileEmbeddingAsync(string filePath, string content, string priority = "normal", string category = "general", string? fileHash = null)
    {
        if (_disabled) return;
        await _lock.WaitAsync();
        try
        {
            var truncated = Truncate(content, 8000);
            var embeddings = await _embedder.EmbedTextsAsync([truncated]);
            if (embeddings.Length == 0) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT OR REPLACE INTO file_embeddings (file_path, content, embedding, last_modified, file_hash, priority, category)
                VALUES ($file_path, $content, {FormatFloatArray(embeddings[0])}, $last_modified, $file_hash, $priority, $category)
                """;
            cmd.Parameters.Add(new DuckDBParameter("file_path", filePath));
            cmd.Parameters.Add(new DuckDBParameter("content", truncated));
            cmd.Parameters.Add(new DuckDBParameter("last_modified", now));
            cmd.Parameters.Add(new DuckDBParameter("file_hash", fileHash ?? ""));
            cmd.Parameters.Add(new DuckDBParameter("priority", priority));
            cmd.Parameters.Add(new DuckDBParameter("category", category));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed file {Path}", filePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteFileEmbeddingAsync(string filePath)
    {
        if (_disabled) return;
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM file_embeddings WHERE file_path = $file_path";
            cmd.Parameters.Add(new DuckDBParameter("file_path", filePath));
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<SearchResult>> QueryJournalsAsync(string queryText, string? threadId = null, int limit = 5, float minScore = 0.3f)
    {
        if (_disabled) return [];
        await _lock.WaitAsync();
        try
        {
            var queryVec = await _embedder.EmbedTextsAsync([queryText]);
            if (queryVec.Length == 0) return [];

            var conn = GetConnection();
            using var cmd = conn.CreateCommand();

            var threadFilter = threadId != null ? "AND thread_id = $thread_id" : "";
            cmd.CommandText = $"""
                SELECT journal_id, thread_id, level, content,
                       list_cosine_similarity(embedding, {FormatFloatArray(queryVec[0])}) AS score
                FROM journal_embeddings
                WHERE embedding IS NOT NULL {threadFilter}
                ORDER BY score DESC
                LIMIT $limit
                """;
            cmd.Parameters.Add(new DuckDBParameter("limit", limit));
            if (threadId != null)
                cmd.Parameters.Add(new DuckDBParameter("thread_id", threadId));

            var results = new List<SearchResult>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var score = (float)reader.GetDouble(4);
                if (score < minScore) continue;
                results.Add(new SearchResult
                {
                    Id = reader.GetInt64(0).ToString(),
                    Source = "journal",
                    ThreadId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Content = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Score = score,
                });
            }
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<SearchResult>> QueryFilesAsync(string queryText, int limit = 5, float minScore = 0.3f)
    {
        if (_disabled) return [];
        await _lock.WaitAsync();
        try
        {
            var queryVec = await _embedder.EmbedTextsAsync([queryText]);
            if (queryVec.Length == 0) return [];

            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT file_path, content, category,
                       list_cosine_similarity(embedding, {FormatFloatArray(queryVec[0])}) AS score
                FROM file_embeddings
                WHERE embedding IS NOT NULL
                ORDER BY score DESC
                LIMIT $limit
                """;
            cmd.Parameters.Add(new DuckDBParameter("limit", limit));

            var results = new List<SearchResult>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var score = (float)reader.GetDouble(3);
                if (score < minScore) continue;
                results.Add(new SearchResult
                {
                    Id = reader.GetString(0),
                    Source = "file",
                    Content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Score = score,
                    Category = reader.IsDBNull(2) ? null : reader.GetString(2),
                });
            }
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string FormatFloatArray(float[] arr)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < arr.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(arr[i].ToString("G9", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen];

    public void Dispose()
    {
        _conn?.Dispose();
        _lock.Dispose();
    }
}

public class SearchResult
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string? ThreadId { get; set; }
    public string Content { get; set; } = "";
    public float Score { get; set; }
    public string? Category { get; set; }
}
