using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using StewardMcp.Config;
using StewardMcp.Data;

namespace StewardMcp.Services;

/// <summary>
/// Manages SQLite databases in the steward's workspace. The steward creates
/// databases and tables as needed to track structured data for its person.
/// </summary>
public class WorkspaceDbService : IDisposable
{
    private readonly StewardConfig _config;
    private readonly VectorStore _vectorStore;
    private readonly ILogger<WorkspaceDbService> _logger;
    private readonly Dictionary<string, SqliteConnection> _connections = new();
    private readonly object _lock = new();

    public WorkspaceDbService(StewardConfig config, VectorStore vectorStore, ILogger<WorkspaceDbService> logger)
    {
        _config = config;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    private string DbDir => Path.Combine(_config.WorkspaceDir, "db");

    private string ResolveDbPath(string dbName)
    {
        // Sanitize: only allow simple names, no path traversal
        if (string.IsNullOrWhiteSpace(dbName) ||
            dbName.Contains('/') || dbName.Contains('\\') || dbName.Contains(".."))
            throw new InvalidOperationException($"Invalid database name: {dbName}");

        if (!dbName.EndsWith(".db")) dbName += ".db";
        return Path.Combine(DbDir, dbName);
    }

    private SqliteConnection GetConnection(string dbName)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(dbName, out var existing))
                return existing;

            Directory.CreateDirectory(DbDir);
            var path = ResolveDbPath(dbName);
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString();

            var conn = new SqliteConnection(connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();

            _connections[dbName] = conn;
            _logger.LogInformation("Opened workspace database: {DbName}", dbName);
            return conn;
        }
    }

    /// <summary>List available workspace databases.</summary>
    public List<string> ListDatabases()
    {
        if (!Directory.Exists(DbDir)) return [];
        return Directory.GetFiles(DbDir, "*.db")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>List tables in a workspace database.</summary>
    public List<string> ListTables(string dbName)
    {
        var conn = GetConnection(dbName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    /// <summary>
    /// Execute a write operation (CREATE TABLE, INSERT, UPDATE, DELETE).
    /// Returns affected row count. Optionally embeds a description for search.
    /// </summary>
    public async Task<ExecuteResult> ExecuteAsync(string dbName, string sql, string? description = null)
    {
        var conn = GetConnection(dbName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var affected = cmd.ExecuteNonQuery();

        _logger.LogInformation("Workspace DB {Db}: executed SQL, {Affected} rows affected", dbName, affected);

        // Embed for semantic search if description provided
        if (!string.IsNullOrEmpty(description))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var embeddingKey = $"db:{dbName}:{Guid.NewGuid():N}";
                    await _vectorStore.UpsertFileEmbeddingAsync(
                        embeddingKey, description,
                        category: "workspace_db", fileHash: null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to embed workspace DB description");
                }
            });
        }

        return new ExecuteResult { AffectedRows = affected };
    }

    /// <summary>
    /// Execute a query (SELECT). Returns rows as list of dictionaries.
    /// </summary>
    public QueryResult Query(string dbName, string sql, int maxRows = 100)
    {
        var conn = GetConnection(dbName);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read() && rows.Count < maxRows)
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return new QueryResult { Columns = columns, Rows = rows, RowCount = rows.Count };
    }

    /// <summary>
    /// Execute a write and embed the resulting data. Runs the write SQL,
    /// then runs a SELECT to get the affected data and embeds it.
    /// </summary>
    public async Task<ExecuteResult> ExecuteAndEmbedAsync(string dbName, string writeSql, string? selectSql = null)
    {
        var result = await ExecuteAsync(dbName, writeSql);

        if (selectSql != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var queryResult = Query(dbName, selectSql, maxRows: 20);
                    if (queryResult.Rows.Count > 0)
                    {
                        var textContent = RenderRowsForEmbedding(dbName, queryResult);
                        var embeddingKey = $"db:{dbName}:{Guid.NewGuid():N}";
                        await _vectorStore.UpsertFileEmbeddingAsync(
                            embeddingKey, textContent,
                            category: "workspace_db", fileHash: null);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to embed workspace DB rows");
                }
            });
        }

        return result;
    }

    private static string RenderRowsForEmbedding(string dbName, QueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{dbName}]");
        foreach (var row in result.Rows)
        {
            var parts = row.Where(kv => kv.Value != null)
                .Select(kv => $"{kv.Key}: {kv.Value}");
            sb.AppendLine(string.Join(", ", parts));
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var conn in _connections.Values)
                conn.Dispose();
            _connections.Clear();
        }
    }
}

public class ExecuteResult
{
    public int AffectedRows { get; set; }
}

public class QueryResult
{
    public List<string> Columns { get; set; } = [];
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public int RowCount { get; set; }
}
