using System.Globalization;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using Microsoft.Data.Sqlite;

namespace BybitDownloader.Storage;

/// <summary>
/// SQLite-backed manifest. A single connection is held open and every operation is serialised, which is
/// sufficient for one desktop process. WAL keeps crash recovery cheap.
/// </summary>
public sealed class SqliteChunkManifest : IChunkManifest, IDisposable
{
    private const string UpsertSql = """
        INSERT INTO chunks
            (chunk_id, category, symbol, year, month, start_ms, end_ms, status,
             row_count, expected_candles, coverage, file_path, issues, updated_utc)
        VALUES
            ($id, $cat, $sym, $y, $m, $s, $e, $st, $rc, $ec, $cov, $fp, $iss, $up)
        ON CONFLICT(chunk_id) DO UPDATE SET
            category = excluded.category,
            symbol = excluded.symbol,
            year = excluded.year,
            month = excluded.month,
            start_ms = excluded.start_ms,
            end_ms = excluded.end_ms,
            status = excluded.status,
            row_count = excluded.row_count,
            expected_candles = excluded.expected_candles,
            coverage = excluded.coverage,
            file_path = excluded.file_path,
            issues = excluded.issues,
            updated_utc = excluded.updated_utc;
        """;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public SqliteChunkManifest(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));

        DatabasePath = Path.GetFullPath(databasePath);
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        _connection.Open();
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS chunks (
                    chunk_id         TEXT    PRIMARY KEY,
                    category         TEXT    NOT NULL,
                    symbol           TEXT    NOT NULL,
                    year             INTEGER NOT NULL,
                    month            INTEGER NOT NULL,
                    start_ms         INTEGER NOT NULL,
                    end_ms           INTEGER NOT NULL,
                    status           INTEGER NOT NULL,
                    row_count        INTEGER NOT NULL DEFAULT 0,
                    expected_candles INTEGER NOT NULL DEFAULT 0,
                    coverage         REAL    NOT NULL DEFAULT 0,
                    file_path        TEXT    NULL,
                    issues           TEXT    NULL,
                    updated_utc      TEXT    NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
            MigrateChunkIds();
        }
    }

    /// <summary>
    /// Collapses legacy rows whose <c>chunk_id</c> embedded the request window (so a still-forming month kept
    /// appending a new row per run) into the stable <c>{category}/{symbol}/{yyyyMM}</c> key, keeping the widest
    /// <c>Done</c> row for each month. Idempotent and a no-op once keys are already stable.
    /// </summary>
    private void MigrateChunkIds()
    {
        using (var check = _connection.CreateCommand())
        {
            check.CommandText =
                "SELECT COUNT(*) FROM chunks WHERE chunk_id <> " +
                "category || '/' || symbol || '/' || printf('%04d', year) || printf('%02d', month);";
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM chunks WHERE rowid NOT IN (
                SELECT rid FROM (
                    SELECT rowid AS rid,
                           ROW_NUMBER() OVER (
                               PARTITION BY category, symbol, year, month
                               ORDER BY (status = 1) DESC, end_ms DESC, rowid DESC) AS rn
                    FROM chunks
                ) WHERE rn = 1
            );
            UPDATE chunks
               SET chunk_id = category || '/' || symbol || '/' || printf('%04d', year) || printf('%02d', month);
            """;
        cmd.ExecuteNonQuery();
    }

    public ChunkRecord? Get(string chunkId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM chunks WHERE chunk_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", chunkId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        }
    }

    public IReadOnlyList<ChunkRecord> GetAll() => Query("SELECT * FROM chunks ORDER BY category, symbol, month, start_ms;", null);

    public IReadOnlyList<ChunkRecord> GetByStatus(ChunkStatus status) =>
        Query("SELECT * FROM chunks WHERE status = $st ORDER BY category, symbol, month, start_ms;",
            cmd => cmd.Parameters.AddWithValue("$st", (int)status));

    public void MarkPending(DownloadChunk chunk) =>
        Upsert(chunk, ChunkStatus.Pending, 0, chunk.CandleCount, 0, null, null);

    public void MarkDone(DownloadChunk chunk, long rowCount, long expectedCandles, double coverage, string filePath, string? issues) =>
        Upsert(chunk, ChunkStatus.Done, rowCount, expectedCandles, coverage, filePath, issues);

    public void MarkFailed(DownloadChunk chunk, string error, string? filePath = null) =>
        Upsert(chunk, ChunkStatus.Failed, 0, chunk.CandleCount, 0, filePath, error);

    public void Remove(string chunkId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM chunks WHERE chunk_id = $id;";
            cmd.Parameters.AddWithValue("$id", chunkId);
            cmd.ExecuteNonQuery();
        }
    }

    private void Upsert(DownloadChunk chunk, ChunkStatus status, long rowCount, long expectedCandles,
        double coverage, string? filePath, string? issues)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = UpsertSql;
            cmd.Parameters.AddWithValue("$id", chunk.Id);
            cmd.Parameters.AddWithValue("$cat", chunk.Category.ToApiString());
            cmd.Parameters.AddWithValue("$sym", chunk.Symbol);
            cmd.Parameters.AddWithValue("$y", chunk.Year);
            cmd.Parameters.AddWithValue("$m", chunk.Month);
            cmd.Parameters.AddWithValue("$s", chunk.StartMs);
            cmd.Parameters.AddWithValue("$e", chunk.EndMs);
            cmd.Parameters.AddWithValue("$st", (int)status);
            cmd.Parameters.AddWithValue("$rc", rowCount);
            cmd.Parameters.AddWithValue("$ec", expectedCandles);
            cmd.Parameters.AddWithValue("$cov", coverage);
            cmd.Parameters.AddWithValue("$fp", (object?)filePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$iss", (object?)issues ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$up", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    private IReadOnlyList<ChunkRecord> Query(string sql, Action<SqliteCommand>? bind)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            using var reader = cmd.ExecuteReader();
            var list = new List<ChunkRecord>();
            while (reader.Read()) list.Add(Map(reader));
            return list;
        }
    }

    private static ChunkRecord Map(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("chunk_id")),
        r.GetString(r.GetOrdinal("category")),
        r.GetString(r.GetOrdinal("symbol")),
        r.GetInt32(r.GetOrdinal("year")),
        r.GetInt32(r.GetOrdinal("month")),
        r.GetInt64(r.GetOrdinal("start_ms")),
        r.GetInt64(r.GetOrdinal("end_ms")),
        (ChunkStatus)r.GetInt32(r.GetOrdinal("status")),
        r.GetInt64(r.GetOrdinal("row_count")),
        r.GetInt64(r.GetOrdinal("expected_candles")),
        r.GetDouble(r.GetOrdinal("coverage")),
        r.IsDBNull(r.GetOrdinal("file_path")) ? null : r.GetString(r.GetOrdinal("file_path")),
        r.IsDBNull(r.GetOrdinal("issues")) ? null : r.GetString(r.GetOrdinal("issues")),
        r.GetString(r.GetOrdinal("updated_utc")));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}
