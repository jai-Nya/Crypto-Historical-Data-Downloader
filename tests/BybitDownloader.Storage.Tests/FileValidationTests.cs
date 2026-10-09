using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;

namespace BybitDownloader.Storage.Tests;

public class FileValidationTests : IDisposable
{
    private const long MinuteMs = 60_000;
    private const long Jan2024 = 1_704_067_200_000;   // 2024-01-01T00:00:00Z
    private const long Feb2024 = 1_706_745_600_000;   // 2024-02-01T00:00:00Z
    private const long Jan2023 = 1_672_531_200_000;   // 2023-01-01T00:00:00Z

    private readonly string _dir;
    private readonly string _dbPath;

    public FileValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bybit-validation-tests", Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dir, "manifest.db");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static Candle C(long ts, decimal o = 100, decimal h = 101, decimal l = 99, decimal c = 100,
        decimal v = 10, decimal t = 1000) => new(ts, o, h, l, c, v, t);

    private static List<Candle> Contiguous(long startMs, int count) =>
        Enumerable.Range(0, count).Select(i => C(startMs + i * MinuteMs)).ToList();

    private static DownloadChunk Chunk(string symbol, int year, int month, long start, long end) =>
        new(MarketCategory.Linear, symbol, year, month, start, end);

    [Fact]
    public async Task Complete_matching_file_is_Ok()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        await store.WriteAsync(path, Contiguous(Jan2024, 44_640));

        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        m.MarkDone(Chunk("BTCUSDT", 2024, 1, Jan2024, Feb2024), 44_640, 44_640, 1.0, path, null);

        var summary = await new CandleFileValidator(_dir, store, m).ValidateAllAsync();

        var file = Assert.Single(summary.Files);
        Assert.Equal(FileValidationStatus.Ok, file.Status);
        Assert.Equal(44_640, file.RowCount);
        Assert.Equal(1.0, file.Coverage);
        Assert.True(file.ManifestRecorded);
        Assert.Empty(file.Issues);
        Assert.Equal(1, summary.OkCount);
        Assert.Empty(summary.MissingFiles);
        Assert.False(summary.HasProblems);
    }

    [Fact]
    public async Task Gap_lowers_coverage_and_warns()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        var candles = Contiguous(Jan2024, 100).Concat(Contiguous(Jan2024 + 200 * MinuteMs, 100)).ToList();
        await store.WriteAsync(path, candles);

        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        m.MarkDone(Chunk("BTCUSDT", 2024, 1, Jan2024, Feb2024), 200, 300, 200.0 / 300.0, path, null);

        var summary = await new CandleFileValidator(_dir, store, m).ValidateAllAsync();

        var file = Assert.Single(summary.Files);
        Assert.Equal(FileValidationStatus.Warnings, file.Status);
        Assert.Equal(1, file.GapCount);
        Assert.Equal(100, file.MissingMinutes);
        Assert.Contains(file.Issues, i => i.Contains("missing minute"));
    }

    [Fact]
    public async Task Manifest_rowcount_mismatch_warns()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        await store.WriteAsync(path, Contiguous(Jan2024, 1000));

        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        m.MarkDone(Chunk("BTCUSDT", 2024, 1, Jan2024, Feb2024), 999, 44_640, 999.0 / 44_640.0, path, null);

        var file = Assert.Single((await new CandleFileValidator(_dir, store, m).ValidateAllAsync()).Files);

        Assert.Equal(FileValidationStatus.Warnings, file.Status);
        Assert.Contains(file.Issues, i => i.Contains("manifest rows"));
    }

    [Fact]
    public async Task Manifest_failed_but_file_present_is_error()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        await store.WriteAsync(path, Contiguous(Jan2024, 1000));

        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        m.MarkFailed(Chunk("BTCUSDT", 2024, 1, Jan2024, Feb2024), "HTTP 503", path);

        var summary = await new CandleFileValidator(_dir, store, m).ValidateAllAsync();

        Assert.Equal(FileValidationStatus.Errors, Assert.Single(summary.Files).Status);
        Assert.Contains(summary.FailedChunks, c => c.Contains("BTCUSDT"));
    }

    [Fact]
    public async Task Manifest_done_without_file_is_reported_missing()
    {
        using var m = new SqliteChunkManifest(_dbPath);
        m.Initialize();
        m.MarkDone(Chunk("BTCUSDT", 2024, 1, Jan2024, Feb2024), 44_640, 44_640, 1.0, null!, null);

        var summary = await new CandleFileValidator(_dir, manifest: m).ValidateAllAsync();

        Assert.Equal(0, summary.FileCount);
        Assert.Single(summary.MissingFiles);
        Assert.Contains("BTCUSDT", summary.MissingFiles[0]);
        Assert.True(summary.HasProblems);
    }

    [Fact]
    public async Task Unreadable_file_is_error()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "this is not parquet");

        var file = Assert.Single((await new CandleFileValidator(_dir, store).ValidateAllAsync()).Files);

        Assert.Equal(FileValidationStatus.Errors, file.Status);
        Assert.Contains(file.Issues, i => i.StartsWith("unreadable:"));
    }

    [Fact]
    public async Task Unrecorded_file_warns()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2023, 1);
        await store.WriteAsync(path, Contiguous(Jan2023, 44_640));

        var file = Assert.Single((await new CandleFileValidator(_dir, store).ValidateAllAsync()).Files);

        Assert.Equal(FileValidationStatus.Warnings, file.Status);
        Assert.False(file.ManifestRecorded);
        Assert.Equal(44_640, file.ExpectedCandles);
        Assert.Equal(1.0, file.Coverage);
    }

    [Fact]
    public async Task Symbol_filter_selects_one_file()
    {
        var store = new ParquetCandleStore(_dir);
        await store.WriteAsync(store.GetPath(MarketCategory.Linear, "BTCUSDT", 2023, 1), Contiguous(Jan2023, 10));
        await store.WriteAsync(store.GetPath(MarketCategory.Linear, "ETHUSDT", 2023, 1), Contiguous(Jan2023, 10));

        var summary = await new CandleFileValidator(_dir, store)
            .ValidateAllAsync(new FileValidationOptions { Symbol = "ETHUSDT" });

        var file = Assert.Single(summary.Files);
        Assert.Equal("ETHUSDT", file.Symbol);
    }

    [Fact]
    public void Analyzer_flags_insane_and_misaligned_rows()
    {
        var candles = new[]
        {
            C(Jan2024),                              // ok
            C(Jan2024 + 30_000),                     // misaligned
            C(Jan2024 + 2 * MinuteMs, h: 90, l: 99)  // high < low -> insane
        };

        var report = FileValidationAnalyzer.Analyze(
            "p.parquet", "linear", "BTCUSDT", 2024, 1, candles,
            expectedCandles: 44_640, windowStartMs: Jan2024, windowEndMs: Feb2024, record: null);

        Assert.Equal(1, report.Misaligned);
        Assert.Equal(1, report.InsaneRows);
        Assert.Equal(1, report.RowCount);
        Assert.Equal(FileValidationStatus.Errors, report.Status);
    }

    [Fact]
    public void Analyzer_flags_out_of_order_and_out_of_range()
    {
        var candles = new[]
        {
            C(Jan2024 + 2 * MinuteMs),
            C(Jan2024 + MinuteMs),      // out of order
            C(Feb2024)                  // outside [start, end)
        };

        var report = FileValidationAnalyzer.Analyze(
            "p.parquet", "linear", "BTCUSDT", 2024, 1, candles,
            expectedCandles: 1, windowStartMs: Jan2024, windowEndMs: Feb2024, record: null);

        Assert.Equal(1, report.OutOfOrder);
        Assert.Equal(1, report.OutOfRange);
        Assert.Equal(FileValidationStatus.Errors, report.Status);
    }

    [Fact]
    public async Task Multiple_records_for_a_month_prefer_the_widest_done_window()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        await store.WriteAsync(path, Contiguous(Jan2024, 100));

        var narrow = Rec("BTCUSDT", Jan2024, Jan2024 + 50 * MinuteMs, 50, ChunkStatus.Done, "a");
        var wide = Rec("BTCUSDT", Jan2024, Jan2024 + 100 * MinuteMs, 100, ChunkStatus.Done, "b");
        var manifest = new ListManifest(narrow, wide);

        var file = Assert.Single((await new CandleFileValidator(_dir, store, manifest).ValidateAllAsync()).Files);

        Assert.Equal(FileValidationStatus.Ok, file.Status);   // no false out-of-range against the stale narrow row
        Assert.Equal(0, file.OutOfRange);
        Assert.Equal(100, file.RowCount);
    }

    private static ChunkRecord Rec(string symbol, long start, long end, long rows, ChunkStatus status, string id) =>
        new(id, "linear", symbol, 2024, 1, start, end, status, rows, rows, 1.0, "p.parquet", null, "x");

    private sealed class ListManifest : IChunkManifest
    {
        private readonly List<ChunkRecord> _records;
        public ListManifest(params ChunkRecord[] records) => _records = records.ToList();
        public void Initialize() { }
        public ChunkRecord? Get(string chunkId) => _records.FirstOrDefault(r => r.ChunkId == chunkId);
        public IReadOnlyList<ChunkRecord> GetAll() => _records;
        public IReadOnlyList<ChunkRecord> GetByStatus(ChunkStatus status) => _records.Where(r => r.Status == status).ToList();
        public void MarkPending(DownloadChunk chunk) => throw new NotSupportedException();
        public void MarkDone(DownloadChunk chunk, long rowCount, long expectedCandles, double coverage, string filePath, string? issues) => throw new NotSupportedException();
        public void MarkFailed(DownloadChunk chunk, string error, string? filePath = null) => throw new NotSupportedException();
        public void Remove(string chunkId) => throw new NotSupportedException();
    }
}
