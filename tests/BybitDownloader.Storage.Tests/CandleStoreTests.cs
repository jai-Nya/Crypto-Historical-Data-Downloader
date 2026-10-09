using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;

namespace BybitDownloader.Storage.Tests;

public class CandleStoreTests : IDisposable
{
    private readonly string _dir;

    public CandleStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bybit-storage-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static Candle C(long ts, decimal o = 100, decimal h = 101, decimal l = 99, decimal c = 100,
        decimal v = 10, decimal t = 1000) => new(ts, o, h, l, c, v, t);

    [Fact]
    public async Task Round_trips_candles_exactly()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        var candles = new[]
        {
            C(1704067200000, 42324.8m, 42349.9m, 42300.2m, 42346.8m, 187.632m, 7943155.034m),
            C(1704067260000, 0.0000012345m, 2m, 0.1m, 123456789012.123456789m, 4m, 5m),
        };

        await store.WriteAsync(path, candles);
        var back = await store.ReadAsync(path);

        Assert.Equal(candles.Length, back.Count);
        Assert.Equal(candles, back);
    }

    [Fact]
    public void Path_is_symbol_month()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Inverse, "BTCUSD", 2024, 3);
        Assert.Equal(Path.Combine("inverse", "BTCUSD", "2024-03.parquet"), path[(store.RootDirectory.Length + 1)..]);
    }

    [Fact]
    public void Path_from_chunk_matches_explicit()
    {
        var store = new ParquetCandleStore(_dir);
        var chunk = new DownloadChunk(MarketCategory.Linear, "ETHUSDT", 2023, 12, 1701388800000, 1704067200000);
        Assert.Equal(store.GetPath(MarketCategory.Linear, "ETHUSDT", 2023, 12), store.GetPath(chunk));
    }

    [Fact]
    public async Task Write_replaces_existing_and_leaves_no_temp_file()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);

        await store.WriteAsync(path, new[] { C(1704067200000) });
        await store.WriteAsync(path, new[] { C(1704067200000), C(1704067260000) });

        var back = await store.ReadAsync(path);
        Assert.Equal(2, back.Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".*.tmp-*"));
    }

    [Fact]
    public async Task Write_rejects_empty_set()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(path, Array.Empty<Candle>()));
    }

    [Fact]
    public void Exists_reflects_file_presence()
    {
        var store = new ParquetCandleStore(_dir);
        var path = store.GetPath(MarketCategory.Linear, "BTCUSDT", 2024, 1);
        Assert.False(store.Exists(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "placeholder");
        Assert.True(store.Exists(path));
    }
}
