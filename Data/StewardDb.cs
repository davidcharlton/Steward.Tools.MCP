using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using StewardMcp.Config;

namespace StewardMcp.Data;

public class StewardDb : IDisposable
{
    private readonly StewardConfig _config;
    private readonly ILogger<StewardDb> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SqliteConnection? _conn;

    public StewardDb(StewardConfig config, ILogger<StewardDb> logger)
    {
        _config = config;
        _logger = logger;
    }

    private SqliteConnection GetConnection()
    {
        if (_conn is not null)
            return _conn;

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _config.SqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _conn = new SqliteConnection(connStr);
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=60000;
            PRAGMA foreign_keys=ON;
            """;
        cmd.ExecuteNonQuery();

        _logger.LogInformation("SQLite connection established with WAL mode at {Path}", _config.SqlitePath);
        return _conn;
    }

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS journal (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts           REAL NOT NULL,
                    thread_id    TEXT NOT NULL,
                    role         TEXT,
                    mode         TEXT NOT NULL,
                    level        INTEGER NOT NULL DEFAULT 0,
                    content      TEXT,
                    payload_json TEXT,
                    meta_json    TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_journal_thread_ts
                ON journal(thread_id, ts);

                CREATE INDEX IF NOT EXISTS idx_journal_mode_level_ts
                ON journal(mode, level, ts);

                CREATE TABLE IF NOT EXISTS thread_profiles (
                    thread_id    TEXT PRIMARY KEY,
                    profile_json TEXT NOT NULL,
                    updated_ts   REAL NOT NULL
                );

                CREATE TABLE IF NOT EXISTS reflection_sources (
                    reflection_id INTEGER NOT NULL,
                    source_id     INTEGER NOT NULL,
                    PRIMARY KEY (reflection_id, source_id)
                );

                CREATE INDEX IF NOT EXISTS idx_reflection_sources_source
                ON reflection_sources(source_id);

                CREATE TABLE IF NOT EXISTS global_state (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS steward_config (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS mindfulness_threads (
                    thread_id     TEXT PRIMARY KEY,
                    name          TEXT NOT NULL,
                    prompt        TEXT NOT NULL,
                    probability   REAL NOT NULL DEFAULT 0.10,
                    enabled       INTEGER NOT NULL DEFAULT 1,
                    source_type   TEXT,
                    source_url    TEXT,
                    source_config TEXT
                );
                """;
            cmd.ExecuteNonQuery();

            // Schema versioning
            using var verCmd = conn.CreateCommand();
            verCmd.CommandText = """
                INSERT OR IGNORE INTO global_state (key, value) VALUES ('schema_version', '3')
                """;
            verCmd.ExecuteNonQuery();

            _logger.LogInformation("Database schema initialized (v3)");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> AppendJournalAsync(
        string threadId,
        string mode,
        int level,
        string content,
        string? role = null,
        object? payload = null,
        object? meta = null)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            cmd.CommandText = """
                INSERT INTO journal (ts, thread_id, role, mode, level, content, payload_json, meta_json)
                VALUES ($ts, $thread_id, $role, $mode, $level, $content, $payload_json, $meta_json)
                """;
            cmd.Parameters.AddWithValue("$ts", now);
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            cmd.Parameters.AddWithValue("$role", role != null ? (object)role : DBNull.Value);
            cmd.Parameters.AddWithValue("$mode", mode);
            cmd.Parameters.AddWithValue("$level", level);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$payload_json", payload != null ? JsonSerializer.Serialize(payload) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$meta_json", meta != null ? JsonSerializer.Serialize(meta) : (object)DBNull.Value);
            cmd.ExecuteNonQuery();

            // Get last inserted ID
            using var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            var id = (long)idCmd.ExecuteScalar()!;
            return id;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<JournalEvent>> GetThreadEventsAsync(
        string threadId,
        string? mode = null,
        int? level = null,
        int limit = 50,
        bool ascending = false)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            var conditions = new List<string> { "thread_id = $thread_id" };
            if (mode != null) conditions.Add("mode = $mode");
            if (level.HasValue) conditions.Add("level = $level");

            var where = string.Join(" AND ", conditions);
            var order = ascending ? "ASC" : "DESC";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, ts, role, mode, level, thread_id, content, payload_json, meta_json
                FROM journal
                WHERE {where}
                ORDER BY ts {order}
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            if (mode != null) cmd.Parameters.AddWithValue("$mode", mode);
            if (level.HasValue) cmd.Parameters.AddWithValue("$level", level.Value);
            cmd.Parameters.AddWithValue("$limit", limit);

            return ReadEvents(cmd);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountEventsAsync(string threadId, string? mode = null, int? level = null)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            var conditions = new List<string> { "thread_id = $thread_id" };
            if (mode != null) conditions.Add("mode = $mode");
            if (level.HasValue) conditions.Add("level = $level");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM journal WHERE {string.Join(" AND ", conditions)}";
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            if (mode != null) cmd.Parameters.AddWithValue("$mode", mode);
            if (level.HasValue) cmd.Parameters.AddWithValue("$level", level.Value);

            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertThreadProfileAsync(string threadId, object profileData)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var json = JsonSerializer.Serialize(profileData);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO thread_profiles (thread_id, profile_json, updated_ts)
                VALUES ($thread_id, $profile_json, $updated_ts)
                ON CONFLICT(thread_id) DO UPDATE SET
                    profile_json = $profile_json,
                    updated_ts = $updated_ts
                """;
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            cmd.Parameters.AddWithValue("$profile_json", json);
            cmd.Parameters.AddWithValue("$updated_ts", now);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ThreadProfile?> GetThreadProfileAsync(string threadId)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT profile_json, updated_ts
                FROM thread_profiles
                WHERE thread_id = $thread_id
                """;
            cmd.Parameters.AddWithValue("$thread_id", threadId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var json = reader.GetString(0);
            var updatedTs = reader.GetDouble(1);
            var profile = JsonSerializer.Deserialize<ThreadProfile>(json) ?? new ThreadProfile();
            profile.ThreadId = threadId;
            profile.UpdatedTs = updatedTs;
            return profile;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<string>> GetAllThreadIdsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT thread_id FROM thread_profiles
                WHERE thread_id != 'master_dossier' AND thread_id != 'scripture_dossier'
                """;

            var results = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(reader.GetString(0));
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task InsertReflectionSourcesAsync(long reflectionId, List<long> sourceIds)
    {
        if (sourceIds.Count == 0) return;
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            foreach (var sourceId in sourceIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO reflection_sources (reflection_id, source_id) VALUES ($rid, $sid)";
                cmd.Parameters.AddWithValue("$rid", reflectionId);
                cmd.Parameters.AddWithValue("$sid", sourceId);
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<JournalEvent>> GetSourcesForReflectionAsync(long reflectionId)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT j.id, j.ts, j.role, j.mode, j.level, j.thread_id, j.content, j.payload_json, j.meta_json
                FROM journal j
                INNER JOIN reflection_sources rs ON rs.source_id = j.id
                WHERE rs.reflection_id = $reflection_id
                ORDER BY j.ts ASC
                """;
            cmd.Parameters.AddWithValue("$reflection_id", reflectionId);

            return ReadEvents(cmd);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<JournalEvent>> GetUnreflectedL0sAsync(string threadId, int limit = 100)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT j.id, j.ts, j.role, j.mode, j.level, j.thread_id, j.content, j.payload_json, j.meta_json
                FROM journal j
                LEFT JOIN reflection_sources rs ON rs.source_id = j.id
                WHERE j.thread_id = $thread_id AND j.mode = 'chat' AND j.level = 0
                  AND rs.reflection_id IS NULL
                ORDER BY j.ts ASC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            cmd.Parameters.AddWithValue("$limit", limit);

            return ReadEvents(cmd);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<JournalEvent>> GetUnreflectedEntriesAsync(string threadId, int level, int limit = 20)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT j.id, j.ts, j.role, j.mode, j.level, j.thread_id, j.content, j.payload_json, j.meta_json
                FROM journal j
                LEFT JOIN reflection_sources rs ON rs.source_id = j.id
                WHERE j.thread_id = $thread_id AND j.mode = 'reflection' AND j.level = $level
                  AND rs.reflection_id IS NULL
                ORDER BY j.ts ASC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            cmd.Parameters.AddWithValue("$level", level);
            cmd.Parameters.AddWithValue("$limit", limit);

            return ReadEvents(cmd);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> IncrementGlobalCounterAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO global_state (key, value) VALUES ($key, '1')
                ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.ExecuteNonQuery();

            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = "SELECT value FROM global_state WHERE key = $key";
            readCmd.Parameters.AddWithValue("$key", key);
            return Convert.ToInt64(readCmd.ExecuteScalar() ?? 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Get the most recent reflection at each level for a thread (for dossier building).</summary>
    public async Task<List<JournalEvent>> GetLatestReflectionPerLevelAsync(string threadId)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, ts, role, mode, level, thread_id, content, payload_json, meta_json
                FROM journal
                WHERE thread_id = $thread_id AND mode = 'reflection'
                  AND id IN (
                    SELECT MAX(id) FROM journal
                    WHERE thread_id = $thread_id AND mode = 'reflection'
                    GROUP BY level
                  )
                ORDER BY level ASC
                """;
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            return ReadEvents(cmd);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Count L1 reflections for a thread (drives the binary MOD cascade).</summary>
    public async Task<int> GetL1CountForThreadAsync(string threadId)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM journal WHERE thread_id = $thread_id AND mode = 'reflection' AND level = 1";
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Get the N most recent reflections at a given level for a thread.</summary>
    public async Task<List<JournalEvent>> GetLatestReflectionsAsync(string threadId, int level, int limit = 2)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, ts, role, mode, level, thread_id, content, payload_json, meta_json
                FROM journal
                WHERE thread_id = $thread_id AND mode = 'reflection' AND level = $level
                ORDER BY ts DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            cmd.Parameters.AddWithValue("$level", level);
            cmd.Parameters.AddWithValue("$limit", limit);

            var results = ReadEvents(cmd);
            results.Reverse(); // Return in chronological order (oldest first)
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Get reflections at a level that have not been consumed as sources by any higher-level reflection.</summary>
    public async Task<List<JournalEvent>> GetUncoveredReflectionsAsync(string threadId, int level, int limit = 10)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT j.id, j.ts, j.role, j.mode, j.level, j.thread_id, j.content, j.payload_json, j.meta_json
                FROM journal j
                LEFT JOIN reflection_sources rs ON rs.source_id = j.id
                WHERE j.thread_id = $thread_id AND j.mode = 'reflection' AND j.level = $level
                  AND rs.reflection_id IS NULL
                ORDER BY j.ts DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            cmd.Parameters.AddWithValue("$level", level);
            cmd.Parameters.AddWithValue("$limit", limit);

            return ReadEvents(cmd);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> GetMaxReflectionLevelAsync(string threadId)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(level) FROM journal WHERE thread_id = $thread_id AND mode = 'reflection'";
            cmd.Parameters.AddWithValue("$thread_id", threadId);
            var result = cmd.ExecuteScalar();
            return result is DBNull || result == null ? 0 : Convert.ToInt32(result);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static List<JournalEvent> ReadEvents(SqliteCommand cmd)
    {
        var results = new List<JournalEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new JournalEvent
            {
                Id = reader.GetInt64(0),
                Ts = reader.GetDouble(1),
                Role = reader.IsDBNull(2) ? null : reader.GetString(2),
                Mode = reader.IsDBNull(3) ? null : reader.GetString(3),
                Level = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                ThreadId = reader.IsDBNull(5) ? null : reader.GetString(5),
                Content = reader.IsDBNull(6) ? null : reader.GetString(6),
                PayloadJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                MetaJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        return results;
    }

    // --- Steward Config (per-user key-value) ---

    public async Task<string?> GetConfigAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM steward_config WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            var result = cmd.ExecuteScalar();
            return result is DBNull || result == null ? null : (string)result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetConfigAsync(string key, string value)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO steward_config (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = $value
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Dictionary<string, string>> GetConfigByPrefixAsync(string prefix)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM steward_config WHERE key LIKE $prefix";
            cmd.Parameters.AddWithValue("$prefix", prefix + "%");
            var results = new Dictionary<string, string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results[reader.GetString(0)] = reader.GetString(1);
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- Mindfulness Threads ---

    public async Task<List<MindfulnessThread>> GetEnabledMindfulnessThreadsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT thread_id, name, prompt, probability, source_type, source_url, source_config FROM mindfulness_threads WHERE enabled = 1";
            var results = new List<MindfulnessThread>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new MindfulnessThread
                {
                    ThreadId = reader.GetString(0),
                    Name = reader.GetString(1),
                    Prompt = reader.GetString(2),
                    Probability = reader.GetDouble(3),
                    SourceType = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SourceUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    SourceConfig = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            }
            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertMindfulnessThreadAsync(MindfulnessThread thread)
    {
        await _lock.WaitAsync();
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO mindfulness_threads (thread_id, name, prompt, probability, enabled, source_type, source_url, source_config)
                VALUES ($thread_id, $name, $prompt, $probability, $enabled, $source_type, $source_url, $source_config)
                ON CONFLICT(thread_id) DO UPDATE SET
                    name = $name, prompt = $prompt, probability = $probability, enabled = $enabled,
                    source_type = $source_type, source_url = $source_url, source_config = $source_config
                """;
            cmd.Parameters.AddWithValue("$thread_id", thread.ThreadId);
            cmd.Parameters.AddWithValue("$name", thread.Name);
            cmd.Parameters.AddWithValue("$prompt", thread.Prompt);
            cmd.Parameters.AddWithValue("$probability", thread.Probability);
            cmd.Parameters.AddWithValue("$source_type", thread.SourceType != null ? (object)thread.SourceType : DBNull.Value);
            cmd.Parameters.AddWithValue("$source_url", thread.SourceUrl != null ? (object)thread.SourceUrl : DBNull.Value);
            cmd.Parameters.AddWithValue("$source_config", thread.SourceConfig != null ? (object)thread.SourceConfig : DBNull.Value);
            cmd.Parameters.AddWithValue("$enabled", thread.Enabled ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _lock.Dispose();
    }
}

public class MindfulnessThread
{
    public string ThreadId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public double Probability { get; set; } = 0.10;
    public bool Enabled { get; set; } = true;
    public string? SourceType { get; set; }  // "rss", "json_api", "scrape", etc.
    public string? SourceUrl { get; set; }
    public string? SourceConfig { get; set; } // JSON config for the feed provider
}

public class JournalEvent
{
    public long Id { get; set; }
    public double Ts { get; set; }
    public string? Role { get; set; }
    public string? Mode { get; set; }
    public int Level { get; set; }
    public string? ThreadId { get; set; }
    public string? Content { get; set; }
    public string? PayloadJson { get; set; }
    public string? MetaJson { get; set; }
}

public class ThreadProfile
{
    public string? ThreadId { get; set; }
    public double UpdatedTs { get; set; }
    public string? Summary { get; set; }
    public List<string>? KeyPoints { get; set; }
    public List<string>? OpenLoops { get; set; }
    public List<string>? Tags { get; set; }
}
