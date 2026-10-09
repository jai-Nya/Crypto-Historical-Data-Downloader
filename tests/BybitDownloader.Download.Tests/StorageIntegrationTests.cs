using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Storage;

namespace BybitDownloader.Download.Tests;

public class StorageIntegrationTests : IDisposable
{
    private const long B = 1_700_000_040_000; // minute-aligned

    private readonly string _dir;

    public StorageIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bybit-integration-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Writes_real_parquet_and_second_run_resumes()
    {
        var dbPath = Path.Combine(_dir, "manifest.db");
        var chunk = new DownloadChunk(MarketCategory.Linear, "BTCUSDT", 2024, 1, B, B + 7 * 60_000L);
        var plan = new PlanResult(new[] { chunk }, Array.Empty<UtcRange>());
        var market = new FakeMarketData();
        var store = new ParquetCandleStore(Path.Combine(_dir, "data"));

        using var manifest = new SqliteChunkManifest(dbPath);
        var orch = new DownloadOrchestrator(market, store, manifest);

        var first = await orch.RunAsync(plan);
        Assert.Equal(1, first.Succeeded);

        var path = store.GetPath(chunk);
        Assert.True(File.Exists(path));
        var back = await store.ReadAsync(path);
        Assert.Equal(7, back.Count);
        var callsAfterFirst = market.KlinesCalls;

        var second = await orch.RunAsync(plan);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Succeeded);
        Assert.Equal(callsAfterFirst, market.KlinesCalls);

        manifest.Initialize();
        var record = manifest.Get(chunk.Id)!;
        Assert.Equal(ChunkStatus.Done, record.Status);
        Assert.Equal(7L, record.RowCount);
        Assert.Equal(path, record.FilePath);
    }
}
