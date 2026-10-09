using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using Microsoft.Data.Sqlite;

namespace BybitDownloader.Storage.Tests;

public class ChunkManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ChunkManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bybit-manifest-tests", Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dir, "manifest.db");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static DownloadChunk Chunk(string symbol = "BTCUSDT", int year = 2024, int month = 1,
        long start = 1704067200000, long end = 1706745600000)
        => new(MarketCategory.Linear, symbol, year, month, start, end);

    [Fact]
    public void Initialize_is_idempotent()
    {
        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        m.Initialize();
        Assert.Empty(m.GetAll());
    }

    [Fact]
    public void MarkPending_records_expected_candles()
    {
        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        var chunk = Chunk();

        m.MarkPending(chunk);
        var r = m.Get(chunk.Id);

        Assert.NotNull(r);
        Assert.Equal(ChunkStatus.Pending, r!.Status);
        Assert.Equal(chunk.CandleCount, r.ExpectedCandles);
        Assert.Equal(0L, r.RowCount);
    }

    [Fact]
    public void MarkDone_stores_all_fields()
    {
        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        var chunk = Chunk();

        m.MarkDone(chunk, rowCount: 44_640, expectedCandles: 44_640, coverage: 1.0,
            filePath: @"C:\data\linear\BTCUSDT\2024-01.parquet", issues: "1 missing minute");

        var r = m.Get(chunk.Id)!;
        Assert.Equal(ChunkStatus.Done, r.Status);
        Assert.Equal(44_640L, r.RowCount);
        Assert.Equal(44_640L, r.ExpectedCandles);
        Assert.Equal(1.0, r.Coverage);
        Assert.Equal(@"C:\data\linear\BTCUSDT\2024-01.parquet", r.FilePath);
        Assert.Equal("1 missing minute", r.Issues);
    }

    [Fact]
    public void Status_transitions_pending_failed_done()
    {
        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        var chunk = Chunk();

        m.MarkPending(chunk);
        Assert.Equal(ChunkStatus.Pending, m.Get(chunk.Id)!.Status);

        m.MarkFailed(chunk, "HTTP 503");
        var failed = m.Get(chunk.Id)!;
        Assert.Equal(ChunkStatus.Failed, failed.Status);
        Assert.Equal("HTTP 503", failed.Issues);

        m.MarkDone(chunk, 100, 100, 1.0, "p.parquet", null);
        var done = m.Get(chunk.Id)!;
        Assert.Equal(ChunkStatus.Done, done.Status);
        Assert.Null(done.Issues);
    }

    [Fact]
    public void Persists_across_instances()
    {
        var chunk = Chunk();
        using (var m = new SqliteChunkManifest(_dbPath))
        {
            m.Initialize();
            m.MarkDone(chunk, 100, 100, 1.0, "p.parquet", null);
        }

        using var reopened = new SqliteChunkManifest(_dbPath);
        reopened.Initialize();
        Assert.Equal(ChunkStatus.Done, reopened.Get(chunk.Id)!.Status);
    }

    [Fact]
    public void GetAll_and_GetByStatus_filter_and_order()
    {
        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        var a = Chunk("BTCUSDT", 2024, 1);
        var b = Chunk("BTCUSDT", 2024, 2);
        var c = Chunk("ETHUSDT", 2024, 1);

        m.MarkDone(a, 1, 1, 1.0, "a", null);
        m.MarkFailed(b, "boom");
        m.MarkPending(c);

        Assert.Equal(3, m.GetAll().Count);
        Assert.Single(m.GetByStatus(ChunkStatus.Done));
        Assert.Single(m.GetByStatus(ChunkStatus.Failed));
        Assert.Single(m.GetByStatus(ChunkStatus.Pending));
        Assert.Equal("a", m.GetByStatus(ChunkStatus.Done)[0].FilePath);
    }

    [Fact]
    public void Remove_deletes_record()
    {
        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        var chunk = Chunk();
        m.MarkDone(chunk, 1, 1, 1.0, "a", null);

        m.Remove(chunk.Id);

        Assert.Null(m.Get(chunk.Id));
    }

    private void SeedLegacyRows(params (string Id, long EndMs, int Status, long Rows, string File)[] rows)
    {
        Directory.CreateDirectory(_dir);
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        con.Open();
        using var create = con.CreateCommand();
        create.CommandText = """
            CREATE TABLE chunks (
                chunk_id TEXT PRIMARY KEY, category TEXT NOT NULL, symbol TEXT NOT NULL,
                year INTEGER NOT NULL, month INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
                status INTEGER NOT NULL, row_count INTEGER NOT NULL DEFAULT 0, expected_candles INTEGER NOT NULL DEFAULT 0,
                coverage REAL NOT NULL DEFAULT 0, file_path TEXT NULL, issues TEXT NULL, updated_utc TEXT NOT NULL);
            """;
        create.ExecuteNonQuery();

        foreach (var (id, endMs, status, rows_, file) in rows)
        {
            using var ins = con.CreateCommand();
            ins.CommandText =
                "INSERT INTO chunks VALUES ($id,'linear','BTCUSDT',2026,10,1790812800000,$e,$s,$r,$r,1.0,$f,NULL,'x');";
            ins.Parameters.AddWithValue("$id", id);
            ins.Parameters.AddWithValue("$e", endMs);
            ins.Parameters.AddWithValue("$s", status);
            ins.Parameters.AddWithValue("$r", rows_);
            ins.Parameters.AddWithValue("$f", file);
            ins.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Migrates_legacy_window_ids_to_one_row_per_month()
    {
        SeedLegacyRows(
            ("linear/BTCUSDT/202610/1790812800000-1791534060000", 1791534060000, 1, 12021, "p1"),
            ("linear/BTCUSDT/202610/1790812800000-1791549360000", 1791549360000, 1, 12276, "p2"),
            ("linear/BTCUSDT/202610/1790812800000-1791554880000", 1791554880000, 1, 12368, "p3"));

        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();

        var r = Assert.Single(m.GetAll());
        Assert.Equal("linear/BTCUSDT/202610", r.ChunkId);
        Assert.Equal(1791554880000, r.EndMs);   // widest window kept
        Assert.Equal(12368, r.RowCount);
    }

    [Fact]
    public void Migration_prefers_Done_over_Failed_and_is_idempotent()
    {
        SeedLegacyRows(
            ("linear/BTCUSDT/202610/1790812800000-1791554880000", 1791554880000, 2, 0, "p1"),   // Failed, wider
            ("linear/BTCUSDT/202610/1790812800000-1791534060000", 1791534060000, 1, 12021, "p2")); // Done, narrower

        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        m.Initialize();   // idempotent

        var r = Assert.Single(m.GetAll());
        Assert.Equal(ChunkStatus.Done, r.Status);
        Assert.Equal(1791534060000, r.EndMs);
    }

    [Fact]
    public void Migrated_ids_are_skippable_by_the_planner_key()
    {
        SeedLegacyRows(("linear/BTCUSDT/202610/1790812800000-1791554880000", 1791554880000, 1, 12368, "p1"));

        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();

        var chunk = new DownloadChunk(MarketCategory.Linear, "BTCUSDT", 2026, 10,
            1790812800000, 1791554880000);
        Assert.NotNull(m.Get(chunk.Id));
    }
}
