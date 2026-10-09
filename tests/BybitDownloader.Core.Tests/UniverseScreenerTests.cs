using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
using static BybitDownloader.Core.Tests.Fx;

namespace BybitDownloader.Core.Tests;

public class UniverseScreenerTests
{
    private const long M = 60_000;

    private static readonly DateTimeOffset FromB = DateTimeOffset.FromUnixTimeMilliseconds(B);

    private static Ticker Tick(string sym, decimal? last = 101, decimal? high = 102, decimal? low = 100,
        decimal? turnover = 5_000_000, decimal? oiv = 1_000_000, decimal? vol = null) =>
        new(sym, last, high, low, turnover, vol, null, oiv, null, null);

    private static IReadOnlyList<Candle> Active(int count) =>
        Enumerable.Range(0, count).Select(i => C(B + i * M, 100, 101, 99, 100)).ToList();

    private static IReadOnlyList<Candle> Flat(int count) =>
        Enumerable.Range(0, count).Select(i => C(B + i * M, 100, 100, 100, 100)).ToList();

    private sealed class FakeMarket : IBybitMarketData
    {
        public readonly Dictionary<MarketCategory, List<Instrument>> Instruments = new();
        public readonly Dictionary<MarketCategory, List<Ticker>> Tickers = new();
        public Func<MarketCategory, string, long, long, IReadOnlyList<Candle>> Klines = (_, _, _, _) => [];
        public readonly List<(MarketCategory Category, string Symbol, long StartMs, long EndMs)> KlineCalls = new();

        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>(Instruments.GetValueOrDefault(category) ?? []);

        public Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ticker>>(Tickers.GetValueOrDefault(category) ?? []);

        public Task<IReadOnlyList<Candle>> GetKlinesAsync(
            MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct)
        {
            KlineCalls.Add((category, symbol, startMs, endMsInclusive));
            return Task.FromResult(Klines(category, symbol, startMs, endMsInclusive));
        }
    }

    [Fact]
    public async Task Fetch_merges_both_categories_and_indexes_tickers_by_symbol()
    {
        var m = new FakeMarket();
        m.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT")];
        m.Tickers[MarketCategory.Linear] = [Tick("AAAUSDT")];
        m.Instruments[MarketCategory.Inverse] = [Inst("BTCUSD", "InversePerpetual", cat: MarketCategory.Inverse)];
        m.Tickers[MarketCategory.Inverse] = [Tick("BTCUSD", vol: 3_000_000)];

        var snap = await new UniverseScreener(m).FetchAsync(
            [MarketCategory.Linear, MarketCategory.Inverse], CancellationToken.None);

        Assert.Equal(2, snap.Instruments.Count);
        Assert.True(snap.Tickers.ContainsKey("AAAUSDT"));
        Assert.True(snap.Tickers.ContainsKey("BTCUSD"));
    }

    [Fact]
    public async Task Screen_returns_only_eligible_symbols_with_planner_inputs()
    {
        var m = new FakeMarket();
        m.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT"), Inst("DUSTUSDT")];
        m.Tickers[MarketCategory.Linear] = [Tick("AAAUSDT"), Tick("DUSTUSDT", turnover: 10_000)];

        var res = await new UniverseScreener(m).ScreenAsync(
            new UniverseScreenOptions { Categories = [MarketCategory.Linear] }, CancellationToken.None);

        var only = Assert.Single(res.Selected);
        Assert.Equal("AAAUSDT", only.Symbol);
        Assert.Equal(MarketCategory.Linear, only.Category);
        Assert.Equal(1_585_526_400_000, only.LaunchTimeMs);
        Assert.Equal("USDT", only.Metrics.Currency);
        Assert.Equal(2, res.InstrumentResults.Count);
    }

    [Fact]
    public async Task Activity_stage_requires_an_explicit_sample_window()
    {
        var m = new FakeMarket();
        m.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT")];
        m.Tickers[MarketCategory.Linear] = [Tick("AAAUSDT")];

        var opts = new UniverseScreenOptions
        {
            Categories = [MarketCategory.Linear],
            Settings = ScreeningPresets.Balanced with { Activity = new ActivityThresholds(0.5m, null, 0.01m, null, null) },
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new UniverseScreener(m).ScreenAsync(opts, CancellationToken.None));
    }

    [Fact]
    public async Task Activity_stage_drops_inactive_symbols_and_records_reasons()
    {
        var m = new FakeMarket();
        m.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT"), Inst("FLATUSDT")];
        m.Tickers[MarketCategory.Linear] = [Tick("AAAUSDT"), Tick("FLATUSDT")];
        m.Klines = (_, symbol, _, _) => symbol == "AAAUSDT" ? Active(20) : Flat(20);

        var sample = UtcRange.Create(FromB, FromB.AddMinutes(20));
        var res = await new UniverseScreener(m).ScreenAsync(new UniverseScreenOptions
        {
            Categories = [MarketCategory.Linear],
            Settings = ScreeningPresets.Balanced with { Activity = new ActivityThresholds(0.5m, null, 0.01m, null, null) },
            ActivitySample = sample,
        }, CancellationToken.None);

        var only = Assert.Single(res.Selected);
        Assert.Equal("AAAUSDT", only.Symbol);
        Assert.Equal(2, res.ActivityResults.Count);
        var flat = res.ActivityResults.Single(r => r.Symbol == "FLATUSDT");
        Assert.False(flat.IsEligible);
        Assert.Contains(flat.Reasons, x => x.Contains("Non-zero high-low"));
        Assert.Empty(res.Errors);
    }

    [Fact]
    public async Task Activity_sample_larger_than_one_request_is_windowed()
    {
        var m = new FakeMarket();
        m.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT")];
        m.Tickers[MarketCategory.Linear] = [Tick("AAAUSDT")];
        m.Klines = (_, _, _, _) => Active(2500);

        var sample = UtcRange.Create(FromB, FromB.AddMinutes(2500));
        await new UniverseScreener(m).ScreenAsync(new UniverseScreenOptions
        {
            Categories = [MarketCategory.Linear],
            Settings = ScreeningPresets.Balanced with { Activity = new ActivityThresholds(0.5m, null, 0.01m, null, null) },
            ActivitySample = sample,
        }, CancellationToken.None);

        Assert.Equal(3, m.KlineCalls.Count);
        Assert.All(m.KlineCalls, c => Assert.True((c.EndMs - c.StartMs) / M + 1 <= 1000));
    }

    [Fact]
    public async Task Activity_api_failures_are_reported_and_exclude_the_symbol()
    {
        var m = new FakeMarket();
        m.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT")];
        m.Tickers[MarketCategory.Linear] = [Tick("AAAUSDT")];
        m.Klines = (_, _, _, _) => throw new ApiException("boom", true);

        var sample = UtcRange.Create(FromB, FromB.AddMinutes(20));
        var res = await new UniverseScreener(m).ScreenAsync(new UniverseScreenOptions
        {
            Categories = [MarketCategory.Linear],
            Settings = ScreeningPresets.Balanced with { Activity = new ActivityThresholds(0.5m, null, 0.01m, null, null) },
            ActivitySample = sample,
        }, CancellationToken.None);

        Assert.Empty(res.Selected);
        var error = Assert.Single(res.Errors);
        Assert.Contains("AAAUSDT", error);
        Assert.Contains("boom", error);
    }
}
