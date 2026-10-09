using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;

namespace BybitDownloader.Download.Tests;

public class DownloadJobRunnerTests
{
    private const long M = 60_000;
    private const long B = 1_700_000_040_000;
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static Instrument Inst(string symbol, MarketCategory cat = MarketCategory.Linear, long? launch = null,
        string type = "LinearPerpetual", string status = "Trading") =>
        new(symbol, cat, type, status, "BTC", cat == MarketCategory.Linear ? "USDT" : "USD",
            cat == MarketCategory.Linear ? "USDT" : "BTC", launch, 0.1m);

    private static Ticker Tick(string sym, decimal turnover = 5_000_000m, decimal oiv = 1_000_000m) =>
        new(sym, 101m, 102m, 100m, turnover, null, null, oiv, null, null);

    private static UtcRange FiveMinutes() => UtcRange.Create(
        DateTimeOffset.FromUnixTimeMilliseconds(B), DateTimeOffset.FromUnixTimeMilliseconds(B + 5 * M));

    private static DownloadJob Job(IReadOnlyList<UtcRange> ranges, bool screen, IReadOnlyList<string>? symbols = null) => new()
    {
        Categories = [MarketCategory.Linear],
        Ranges = ranges,
        ScreenUniverse = screen,
        Symbols = symbols ?? [],
        Now = Now,
    };

    [Fact]
    public async Task Explicit_symbols_resolve_launch_and_download()
    {
        var market = new FakeMarketData();
        market.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT", launch: 1_585_526_400_000)];
        var runner = new DownloadJobRunner(market, new FakeCandleStore(), new FakeChunkManifest());

        var result = await runner.RunAsync(Job([FiveMinutes()], screen: false, ["AAAUSDT"]));

        Assert.Empty(result.UnresolvedSymbols);
        var symbol = Assert.Single(result.Symbols);
        Assert.Equal("AAAUSDT", symbol.Symbol);
        Assert.Equal(1_585_526_400_000, symbol.LaunchTimeMs);
        Assert.Equal(1, result.Summary.Succeeded);
        Assert.Equal(5L, result.Summary.TotalCandles);
    }

    [Fact]
    public async Task Unknown_symbols_are_reported_not_thrown()
    {
        var market = new FakeMarketData();
        market.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT")];
        var runner = new DownloadJobRunner(market, new FakeCandleStore(), new FakeChunkManifest());

        var plan = await runner.PlanAsync(Job([FiveMinutes()], screen: false, ["NOPEUSDT"]));

        Assert.Empty(plan.Symbols);
        Assert.Equal("NOPEUSDT", Assert.Single(plan.UnresolvedSymbols));
        Assert.Empty(plan.Plan.Chunks);
    }

    [Fact]
    public async Task Screening_selects_eligible_symbols_without_downloading()
    {
        var market = new FakeMarketData();
        market.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT"), Inst("DUSTUSDT")];
        market.Tickers[MarketCategory.Linear] = [Tick("AAAUSDT"), Tick("DUSTUSDT", turnover: 10_000m)];
        var runner = new DownloadJobRunner(market, new FakeCandleStore(), new FakeChunkManifest());

        var plan = await runner.PlanAsync(Job([FiveMinutes()], screen: true));

        Assert.Equal("AAAUSDT", Assert.Single(plan.Symbols).Symbol);
        Assert.Equal(0, market.KlinesCalls);
        Assert.Single(plan.Plan.Chunks);
    }

    [Fact]
    public async Task Empty_ranges_are_rejected()
    {
        var runner = new DownloadJobRunner(new FakeMarketData(), new FakeCandleStore(), new FakeChunkManifest());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.PlanAsync(Job([], screen: false, ["AAAUSDT"])));
    }

    [Fact]
    public async Task No_selection_is_rejected()
    {
        var runner = new DownloadJobRunner(new FakeMarketData(), new FakeCandleStore(), new FakeChunkManifest());
        await Assert.ThrowsAsync<ArgumentException>(() => runner.PlanAsync(Job([FiveMinutes()], screen: false)));
    }

    [Fact]
    public async Task Planning_only_runner_plans_but_refuses_to_download()
    {
        var market = new FakeMarketData();
        market.Instruments[MarketCategory.Linear] = [Inst("AAAUSDT")];
        var runner = DownloadJobRunner.ForPlanning(market);

        var plan = await runner.PlanAsync(Job([FiveMinutes()], screen: false, ["AAAUSDT"]));
        Assert.Single(plan.Plan.Chunks);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Job([FiveMinutes()], screen: false, ["AAAUSDT"])));
    }
}
