using System.Collections.Concurrent;
using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Storage;

namespace BybitDownloader.Download.Tests;

public class OrchestratorTests
{
    private const long B = 1_700_000_040_000; // minute-aligned

    private static DownloadChunk Chunk(int minutes, string symbol = "BTCUSDT", int year = 2024, int month = 1) =>
        new(MarketCategory.Linear, symbol, year, month, B, B + minutes * 60_000L);

    private static PlanResult Plan(params DownloadChunk[] chunks) => new(chunks, Array.Empty<UtcRange>());

    [Fact]
    public async Task Merges_windows_sorts_dedupes_and_persists()
    {
        var chunk = Chunk(minutes: 1001); // -> two request windows
        var market = new FakeMarketData();
        var store = new FakeCandleStore();
        var manifest = new FakeChunkManifest();
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(chunk));

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(1001L, summary.TotalCandles);
        Assert.Equal(2, market.KlinesCalls);

        var saved = store.Files[store.GetPath(chunk)];
        Assert.Equal(1001, saved.Count);
        Assert.Equal(saved.OrderBy(c => c.TimestampMs).Select(c => c.TimestampMs), saved.Select(c => c.TimestampMs));

        var rec = manifest.Get(chunk.Id)!;
        Assert.Equal(ChunkStatus.Done, rec.Status);
        Assert.Equal(1001L, rec.RowCount);
        Assert.Equal(1.0, rec.Coverage, 3);
        Assert.Null(rec.Issues);
    }

    [Fact]
    public async Task Drops_out_of_range_rows_and_duplicates_and_records_issue()
    {
        var chunk = Chunk(minutes: 5);
        var market = new FakeMarketData((c, s, a, b) =>
        {
            var list = FakeMarketData.MakeWindow(a, b, descending: false).ToList();
            list.Add(list[0]);                                  // exact duplicate
            list.Add(new Candle(a - 60_000, 100m, 101m, 99m, 100m, 10m, 1000m)); // out of range
            return list;
        });
        var store = new FakeCandleStore();
        var manifest = new FakeChunkManifest();
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(chunk));

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(5L, summary.TotalCandles);
        var rec = manifest.Get(chunk.Id)!;
        Assert.NotNull(rec.Issues);
        Assert.Contains("duplicate", rec.Issues!);
        Assert.Contains("outside requested range", rec.Issues!);
    }

    [Fact]
    public async Task Skips_chunk_already_done_with_file_present()
    {
        var chunk = Chunk(minutes: 5);
        var market = new FakeMarketData();
        var store = new FakeCandleStore();
        store.Files[store.GetPath(chunk)] = new List<Candle> { new(B, 1m, 1m, 1m, 1m, 1m, 1m) };
        var manifest = new FakeChunkManifest();
        manifest.MarkDone(chunk, 5, 5, 1.0, store.GetPath(chunk), null);
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(chunk));

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(0, market.KlinesCalls);
    }

    [Fact]
    public async Task Rerequests_when_marked_done_but_file_missing()
    {
        var chunk = Chunk(minutes: 5);
        var market = new FakeMarketData();
        var store = new FakeCandleStore(); // no file
        var manifest = new FakeChunkManifest();
        manifest.MarkDone(chunk, 5, 5, 1.0, store.GetPath(chunk), null);
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(chunk));

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(1, market.KlinesCalls);
    }

    [Fact]
    public async Task Resumes_a_grown_still_forming_month()
    {
        var early = Chunk(minutes: 5);
        var later = Chunk(minutes: 8);          // same month, later end (still-forming month)
        Assert.Equal(early.Id, later.Id);       // stable id per month

        var market = new FakeMarketData();
        var store = new FakeCandleStore();
        store.Files[store.GetPath(early)] = new List<Candle> { new(B, 1m, 1m, 1m, 1m, 1m, 1m) };
        var manifest = new FakeChunkManifest();
        manifest.MarkDone(early, 5, 5, 1.0, store.GetPath(early), null);
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(later));

        Assert.Equal(0, summary.Skipped);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, market.KlinesCalls);
        var rec = manifest.Get(later.Id)!;
        Assert.Equal(later.EndMs, rec.EndMs);   // record advanced to the new window
        Assert.Equal(8L, rec.RowCount);
    }

    [Fact]
    public async Task Skips_when_the_recorded_window_covers_the_planned_chunk()
    {
        var planned = Chunk(minutes: 5);
        var recorded = new DownloadChunk(MarketCategory.Linear, "BTCUSDT", planned.Year, planned.Month,
            planned.StartMs, planned.EndMs + 3 * 60_000L);   // recorded a wider window

        var market = new FakeMarketData();
        var store = new FakeCandleStore();
        store.Files[store.GetPath(planned)] = new List<Candle> { new(B, 1m, 1m, 1m, 1m, 1m, 1m) };
        var manifest = new FakeChunkManifest();
        manifest.MarkDone(recorded, 8, 8, 1.0, store.GetPath(planned), null);
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(planned));

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, market.KlinesCalls);
    }

    [Fact]
    public async Task Marks_failed_when_market_throws()
    {
        var chunk = Chunk(minutes: 5);
        var market = new FakeMarketData((c, s, a, b) => throw new ApiException("boom", isTransient: false));
        var store = new FakeCandleStore();
        var manifest = new FakeChunkManifest();
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(chunk));

        Assert.Equal(1, summary.Failed);
        var rec = manifest.Get(chunk.Id)!;
        Assert.Equal(ChunkStatus.Failed, rec.Status);
        Assert.Contains("boom", rec.Issues!);
    }

    [Fact]
    public async Task Marks_failed_when_no_valid_candles_returned()
    {
        var chunk = Chunk(minutes: 5);
        var market = new FakeMarketData((c, s, a, b) => Array.Empty<Candle>());
        var store = new FakeCandleStore();
        var manifest = new FakeChunkManifest();
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(chunk));

        Assert.Equal(1, summary.Failed);
        Assert.Contains("No valid candles", manifest.Get(chunk.Id)!.Issues!);
        Assert.Empty(store.Files);
    }

    [Fact]
    public async Task One_failure_does_not_stop_other_chunks_by_default()
    {
        var bad = Chunk(5, "BADUSDT", 2024, 1);
        var good = Chunk(5, "GOODUSDT", 2024, 1);
        var market = new FakeMarketData((c, s, a, b) =>
            s == "BADUSDT" ? throw new ApiException("boom", false) : FakeMarketData.MakeWindow(a, b));
        var store = new FakeCandleStore();
        var manifest = new FakeChunkManifest();
        var orch = new DownloadOrchestrator(market, store, manifest);

        var summary = await orch.RunAsync(Plan(bad, good));

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Succeeded);
    }

    [Fact]
    public async Task FailFast_cancels_remaining_work_without_throwing()
    {
        var bad1 = Chunk(5, "BAD1USDT", 2024, 1);
        var bad2 = Chunk(5, "BAD2USDT", 2024, 1);
        var market = new FakeMarketData((c, s, a, b) => throw new ApiException("boom", false));
        var store = new FakeCandleStore();
        var manifest = new FakeChunkManifest();
        var orch = new DownloadOrchestrator(market, store, manifest, new OrchestratorOptions
        {
            FailFast = true,
            MaxParallelWorkers = 1
        });

        var summary = await orch.RunAsync(Plan(bad1, bad2));

        Assert.True(summary.Failed >= 1);
        Assert.Equal(0, summary.Succeeded);
    }

    [Fact]
    public async Task Reports_progress_once_per_chunk()
    {
        var chunks = new[] { Chunk(5, "AUSDT"), Chunk(5, "BUSDT"), Chunk(5, "CUSDT") };
        var market = new FakeMarketData();
        var store = new FakeCandleStore();
        var manifest = new FakeChunkManifest();
        var orch = new DownloadOrchestrator(market, store, manifest);

        var reports = new ConcurrentQueue<DownloadProgress>();
        var summary = await orch.RunAsync(Plan(chunks), new SyncProgress<DownloadProgress>(reports.Enqueue));

        Assert.Equal(3, summary.Total);
        Assert.Equal(3, reports.Count);
        Assert.Equal(new[] { 1, 2, 3 }, reports.Select(r => r.Completed).OrderBy(x => x).ToArray());
        Assert.Equal(3, reports.Max(r => r.Total));
        Assert.Equal(15L, summary.TotalCandles);
    }

    [Fact]
    public async Task Rejects_plan_with_duplicate_target_files()
    {
        var a = Chunk(5, "BTCUSDT", 2024, 1);
        var b = Chunk(10, "BTCUSDT", 2024, 1); // same symbol-month file
        var orch = new DownloadOrchestrator(new FakeMarketData(), new FakeCandleStore(), new FakeChunkManifest());

        await Assert.ThrowsAsync<InvalidOperationException>(() => orch.RunAsync(Plan(a, b)));
    }

    [Fact]
    public async Task Empty_plan_is_a_noop()
    {
        var market = new FakeMarketData();
        var orch = new DownloadOrchestrator(market, new FakeCandleStore(), new FakeChunkManifest());

        var summary = await orch.RunAsync(Plan());

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, market.KlinesCalls);
    }

    [Fact]
    public void Requires_at_least_one_worker()
    {
        Assert.Throws<ArgumentException>(() => new DownloadOrchestrator(
            new FakeMarketData(), new FakeCandleStore(), new FakeChunkManifest(),
            new OrchestratorOptions { MaxParallelWorkers = 0 }));
    }
}
