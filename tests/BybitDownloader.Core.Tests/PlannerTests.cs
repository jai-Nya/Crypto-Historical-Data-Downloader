using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;

namespace BybitDownloader.Core.Tests;

public class PlannerTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static PlanResult Plan(IEnumerable<UtcRange> r, long? launch = null, DateTimeOffset? now = null, bool forming = false) =>
        DownloadPlanner.Plan(MarketCategory.Linear, "BTCUSDT", launch, r, now ?? Now, forming);

    [Fact]
    public void Full_year_selection_is_one_contiguous_half_open_range()
    {
        var r = Assert.Single(PeriodSelector.Year(2024));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), r.Start);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), r.End);
    }

    [Fact]
    public void Year_plan_has_twelve_month_chunks()
    {
        var p = Plan(PeriodSelector.Year(2024));
        Assert.Equal(12, p.Chunks.Count);
        Assert.Equal(Enumerable.Range(1, 12), p.Chunks.Select(c => c.Month));
        // chunks tile the year with no gaps/overlaps
        for (var i = 1; i < p.Chunks.Count; i++) Assert.Equal(p.Chunks[i - 1].EndMs, p.Chunks[i].StartMs);
    }

    [Theory]
    [InlineData(2024, 2, 29)]  // leap
    [InlineData(2023, 2, 28)]
    [InlineData(2100, 2, 28)]  // century non-leap
    [InlineData(2024, 3, 31)]  // UTC has no DST: March is exactly 31*24h
    [InlineData(2024, 4, 30)]
    public void Month_candle_counts_follow_calendar(int y, int m, int days)
    {
        var p = Plan([PeriodSelector.Month(y, m)], now: new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(days * 1440L, Assert.Single(p.Chunks).CandleCount);
    }

    [Fact]
    public void Range_spanning_month_boundary_splits_into_two_chunks()
    {
        var r = PeriodSelector.Custom(new DateTimeOffset(2024, 1, 31, 23, 0, 0, TimeSpan.Zero),
                                      new DateTimeOffset(2024, 2, 1, 1, 0, 0, TimeSpan.Zero));
        var p = Plan(r);
        Assert.Equal(2, p.Chunks.Count);
        Assert.All(p.Chunks, c => Assert.Equal(60, c.CandleCount));
        Assert.NotEqual(p.Chunks[0].Id, p.Chunks[1].Id);
    }

    [Fact]
    public void Local_offsets_are_converted_not_reinterpreted()
    {
        var r = PeriodSelector.Custom(new DateTimeOffset(2024, 3, 10, 0, 0, 0, TimeSpan.FromHours(-5)),
                                      new DateTimeOffset(2024, 3, 11, 0, 0, 0, TimeSpan.FromHours(-5)));
        Assert.Equal(new DateTimeOffset(2024, 3, 10, 5, 0, 0, TimeSpan.Zero), r[0].Start);
        Assert.Equal(TimeSpan.Zero, r[0].Start.Offset);
    }

    [Fact]
    public void DateRange_end_is_inclusive_date_but_exclusive_instant()
    {
        var r = PeriodSelector.DateRange(new DateOnly(2024, 2, 28), new DateOnly(2024, 2, 29))[0];
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), r.End);
    }

    [Fact]
    public void Overlapping_or_adjacent_months_merge()
    {
        var m = PeriodSelector.Months([(2024, 1), (2024, 2), (2024, 2), (2024, 5)]);
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void Launch_time_clamps_start_and_reports_unavailable_range()
    {
        var launch = new DateTimeOffset(2024, 2, 15, 10, 30, 30, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var p = Plan([PeriodSelector.Month(2024, 2)], launch);
        var chunk = Assert.Single(p.Chunks);
        Assert.Equal(new DateTimeOffset(2024, 2, 15, 10, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), chunk.StartMs);
        var gap = Assert.Single(p.UnavailableBeforeLaunch);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero), gap.Start);
    }

    [Fact]
    public void Range_entirely_before_launch_yields_no_chunks()
    {
        var launch = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var p = Plan([PeriodSelector.Month(2024, 2)], launch);
        Assert.Empty(p.Chunks);
        Assert.Single(p.UnavailableBeforeLaunch);
    }

    [Fact]
    public void Forming_candle_is_excluded_by_default_and_includable()
    {
        var now = new DateTimeOffset(2024, 2, 10, 12, 34, 56, TimeSpan.Zero);
        var floor = new DateTimeOffset(2024, 2, 10, 12, 34, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(floor, Assert.Single(Plan([PeriodSelector.Month(2024, 2)], now: now).Chunks).EndMs);
        Assert.Equal(floor + 60_000, Assert.Single(Plan([PeriodSelector.Month(2024, 2)], now: now, forming: true).Chunks).EndMs);
    }

    [Fact]
    public void Future_range_yields_nothing()
    {
        Assert.Empty(Plan([PeriodSelector.Month(2030, 1)]).Chunks);
    }

    [Fact]
    public void Windows_tile_chunk_exactly_with_max_1000_candles()
    {
        var chunk = new DownloadChunk(MarketCategory.Linear, "X", 2024, 1, 0, 2500 * 60_000L);
        var w = DownloadPlanner.Windows(chunk).ToList();
        Assert.Equal(new long[] { 1000L, 1000L, 500L }, w.Select(x => x.MaxCandles));
        Assert.Equal(0, w[0].StartMs);
        for (var i = 1; i < w.Count; i++) Assert.Equal(w[i - 1].EndMsInclusive + 60_000, w[i].StartMs);
        Assert.Equal(chunk.EndMs - 60_000, w[^1].EndMsInclusive);
    }

    [Fact]
    public void Single_candle_chunk_has_one_window()
    {
        var chunk = new DownloadChunk(MarketCategory.Linear, "X", 2024, 1, 60_000, 120_000);
        var w = Assert.Single(DownloadPlanner.Windows(chunk));
        Assert.Equal(1, w.MaxCandles);
    }

    [Fact]
    public void Windows_range_overload_matches_chunk_overload()
    {
        var chunk = new DownloadChunk(MarketCategory.Linear, "X", 2024, 1, 0, 2500 * 60_000L);
        var fromRange = DownloadPlanner.Windows(0, 2500 * 60_000L).ToList();
        var fromChunk = DownloadPlanner.Windows(chunk).ToList();
        Assert.Equal(fromChunk, fromRange);
    }

    [Fact]
    public void PlanUniverse_plans_every_symbol_and_dedupes_duplicates()
    {
        var symbols = new[]
        {
            (MarketCategory.Linear, "AAAUSDT", (long?)null),
            (MarketCategory.Linear, "BBBUSDT", (long?)null),
            (MarketCategory.Linear, "AAAUSDT", (long?)null),
            (MarketCategory.Inverse, "BTCUSD", (long?)null),
        };
        var p = DownloadPlanner.PlanUniverse(symbols, [PeriodSelector.Month(2024, 1)], Now);
        Assert.Equal(3, p.Chunks.Count);
        Assert.Equal(3, p.Chunks.Select(c => $"{c.Category}/{c.Symbol}").Distinct().Count());
    }

    [Fact]
    public void PlanUniverse_clamps_per_symbol_launch_times()
    {
        var launch = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var symbols = new[]
        {
            (MarketCategory.Linear, "OLDUSDT", (long?)null),
            (MarketCategory.Linear, "NEWUSDT", (long?)launch),
        };
        var p = DownloadPlanner.PlanUniverse(symbols, [PeriodSelector.Month(2024, 1)], Now);
        Assert.Equal(2, p.Chunks.Count);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            p.Chunks.Single(c => c.Symbol == "OLDUSDT").StartMs);
        Assert.Equal(launch, p.Chunks.Single(c => c.Symbol == "NEWUSDT").StartMs);
        Assert.Single(p.UnavailableBeforeLaunch);
    }

    [Fact]
    public void Workload_estimate_counts_requests()
    {
        var p = Plan([PeriodSelector.Month(2024, 2)], now: new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var w = DownloadPlanner.Estimate(p.Chunks);
        Assert.Equal(41760, w.Candles);
        Assert.Equal(42, w.Requests); // ceil(41760/1000)
        Assert.False(w.IsLarge);
    }

    [Fact]
    public void Invalid_inputs_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PeriodSelector.Month(2024, 13));
        Assert.Throws<ArgumentException>(() => PeriodSelector.DateRange(new DateOnly(2024, 2, 2), new DateOnly(2024, 2, 1)));
    }
}
