using BybitDownloader.Core.Models;
using BybitDownloader.Core.Screening;
using static BybitDownloader.Core.Tests.Fx;

namespace BybitDownloader.Core.Tests;

public class ScreeningTests
{
    private readonly LiquidityFilterService _svc = new();

    private static Ticker T(string sym = "BTCUSDT", decimal? last = 101, decimal? high = 102, decimal? low = 100,
        decimal? turnover = 5_000_000, decimal? oiv = 1_000_000, decimal? bid = null, decimal? ask = null,
        decimal? vol = null, decimal? oi = null) =>
        new(sym, last, high, low, turnover, vol, oi, oiv, bid, ask);

    [Fact]
    public void Healthy_linear_perp_passes_balanced()
    {
        var r = _svc.Evaluate(Inst(), T(), ScreeningPresets.Balanced);
        Assert.True(r.IsEligible);
        Assert.Empty(r.Reasons);
        Assert.Equal("USDT", r.Metrics.Currency);
    }

    [Fact]
    public void Low_turnover_fails_with_explanatory_reason()
    {
        var r = _svc.Evaluate(Inst(), T(turnover: 1_000_000), ScreeningPresets.Balanced);
        Assert.False(r.IsEligible);
        Assert.Contains(r.Reasons, x => x.Contains("turnover") && x.Contains("2000000"));
    }

    [Fact]
    public void Missing_metric_never_silently_passes_an_enabled_filter()
    {
        var r = _svc.Evaluate(Inst(), T(oiv: null), ScreeningPresets.Balanced);
        Assert.False(r.IsEligible);
        Assert.Contains(r.Reasons, x => x.Contains("Open-interest") && x.Contains("unavailable"));
    }

    [Fact]
    public void Disabled_filter_ignores_missing_metric()
    {
        var s = ScreeningPresets.Balanced with { MinOpenInterestValue = null };
        Assert.True(_svc.Evaluate(Inst(), T(oiv: null), s).IsEligible);
    }

    [Fact]
    public void Missing_ticker_fails_when_any_ticker_filter_enabled()
    {
        Assert.False(_svc.Evaluate(Inst(), null, ScreeningPresets.Balanced).IsEligible);
        var off = ScreeningPresets.Balanced with { MinTurnover24h = null, MinOpenInterestValue = null, MinPriceRangePct = null };
        Assert.True(_svc.Evaluate(Inst(), null, off).IsEligible);
    }

    [Fact]
    public void Expiring_futures_and_non_trading_are_excluded()
    {
        Assert.False(_svc.Evaluate(Inst("BTC-27DEC24", "LinearFutures"), T(), ScreeningPresets.Balanced).IsEligible);
        var pre = _svc.Evaluate(Inst("NEWUSDT", status: "PreLaunch"), T(), ScreeningPresets.Balanced);
        Assert.False(pre.IsEligible);
        Assert.Contains(pre.Reasons, x => x.Contains("PreLaunch"));
    }

    [Fact]
    public void Price_range_uses_last_price_denominator()
    {
        var m = TickerMetricsCalculator.Compute(Inst(), T(last: 100, high: 103, low: 99));
        Assert.Equal(4m, m.PriceRangePct);
    }

    [Fact]
    public void Zero_or_inverted_prices_yield_null_metrics()
    {
        Assert.Null(TickerMetricsCalculator.Compute(Inst(), T(last: 0)).PriceRangePct);
        Assert.Null(TickerMetricsCalculator.Compute(Inst(), T(high: 99, low: 100)).PriceRangePct);
        Assert.Null(TickerMetricsCalculator.Compute(Inst(), T(bid: 0, ask: 1)).SpreadPct);
        Assert.Null(TickerMetricsCalculator.Compute(Inst(), T(bid: 101, ask: 100)).SpreadPct); // crossed book
    }

    [Fact]
    public void Spread_is_relative_to_mid_price()
    {
        var m = TickerMetricsCalculator.Compute(Inst(), T(bid: 99.9m, ask: 100.1m));
        Assert.True(Math.Abs(m.SpreadPct!.Value - 0.2m) < 0.0001m);
    }

    [Fact]
    public void Spread_filter_when_enabled_requires_data_and_threshold()
    {
        var s = ScreeningPresets.Balanced with { MaxSpreadPct = 0.05m };
        Assert.False(_svc.Evaluate(Inst(), T(), s).IsEligible);                          // no bid/ask
        Assert.False(_svc.Evaluate(Inst(), T(bid: 99.9m, ask: 100.1m), s).IsEligible);   // 0.2% > 0.05%
        Assert.True(_svc.Evaluate(Inst(), T(bid: 100m, ask: 100.01m), s).IsEligible);
    }

    [Fact]
    public void Inverse_contracts_use_usd_denominated_fields_and_are_flagged_approximate()
    {
        var inv = Inst("BTCUSD", "InversePerpetual", cat: MarketCategory.Inverse);
        var m = TickerMetricsCalculator.Compute(inv, T("BTCUSD", turnover: 40m, vol: 3_000_000m, oi: 1_000_000m, oiv: 99m));
        Assert.Equal(3_000_000m, m.TurnoverQuote);
        Assert.Equal(1_000_000m, m.OpenInterestValueQuote);
        Assert.Equal("USD", m.Currency);
        Assert.True(m.Approximate);
    }

    [Fact]
    public void Presets_are_ordered_by_strictness()
    {
        Assert.True(ScreeningPresets.Broad.MinTurnover24h < ScreeningPresets.Balanced.MinTurnover24h);
        Assert.True(ScreeningPresets.Balanced.MinTurnover24h < ScreeningPresets.Strict.MinTurnover24h);
        Assert.Equal(2_000_000m, ScreeningPresets.Balanced.MinTurnover24h);
        Assert.Equal(500_000m, ScreeningPresets.Balanced.MinOpenInterestValue);
        Assert.Equal(1.0m, ScreeningPresets.Balanced.MinPriceRangePct);
        Assert.Null(ScreeningPresets.Balanced.MaxSpreadPct);
        Assert.NotNull(ScreeningPresets.Strict.Activity);
    }

    [Fact]
    public void EvaluateAll_matches_tickers_by_symbol()
    {
        var instruments = new[] { Inst("AAAUSDT"), Inst("BBBUSDT") };
        var tickers = new Dictionary<string, Ticker> { ["AAAUSDT"] = T("AAAUSDT") };
        var res = _svc.EvaluateAll(instruments, tickers, ScreeningPresets.Balanced);
        Assert.True(res[0].IsEligible);
        Assert.False(res[1].IsEligible);
    }
}

public class ActivityTests
{
    private const long M = 60_000;
    private readonly LiquidityFilterService _svc = new();

    [Fact]
    public void Flat_market_has_zero_activity()
    {
        var c = Enumerable.Range(0, 20).Select(i => C(B + i * M, 100, 100, 100, 100)).ToList();
        var m = ActivityAnalyzer.Compute(c, 20, 14);
        Assert.Equal(0m, m.NonZeroRangeFraction);
        Assert.Equal(0m, m.NonZeroCloseChangeFraction);
        Assert.Equal(0m, m.MedianRangePct);
        Assert.Equal(0m, m.AtrPct);
        Assert.Equal(1m, m.Coverage);
    }

    [Fact]
    public void Constant_range_gives_expected_median_and_atr_percent()
    {
        var c = Enumerable.Range(0, 20).Select(i => C(B + i * M, 100, 101, 99, 100)).ToList(); // range 2, close 100
        var m = ActivityAnalyzer.Compute(c, 20, 14);
        Assert.Equal(1m, m.NonZeroRangeFraction);
        Assert.Equal(0m, m.NonZeroCloseChangeFraction); // closes never change
        Assert.Equal(2m, m.MedianRangePct);
        Assert.Equal(2m, m.AtrPct);
    }

    [Fact]
    public void Alternating_closes_count_as_changes()
    {
        var c = Enumerable.Range(0, 11).Select(i => C(B + i * M, 100, 102, 98, i % 2 == 0 ? 100 : 101)).ToList();
        var m = ActivityAnalyzer.Compute(c, 11, 5);
        Assert.Equal(1m, m.NonZeroCloseChangeFraction);
    }

    [Fact]
    public void Coverage_and_missing_intervals_reflect_expected_count()
    {
        var c = Enumerable.Range(0, 8).Select(i => C(B + i * M)).ToList();
        var m = ActivityAnalyzer.Compute(c, 10, 14);
        Assert.Equal(0.8m, m.Coverage);
        Assert.Equal(2, m.MissingIntervals);
    }

    [Fact]
    public void Empty_sample_is_safe()
    {
        var m = ActivityAnalyzer.Compute([], 100, 14);
        Assert.Equal(0, m.Candles);
        Assert.Equal(0m, m.Coverage);
    }

    [Fact]
    public void Activity_thresholds_produce_reasons_and_skip_disabled_ones()
    {
        var flat = Enumerable.Range(0, 20).Select(i => C(B + i * M, 100, 100, 100, 100)).ToList();
        var m = ActivityAnalyzer.Compute(flat, 20, 14);
        var reasons = _svc.EvaluateActivity(m, new ActivityThresholds(0.5m, null, 0.01m, null, null));
        Assert.Equal(2, reasons.Count);
        Assert.Empty(_svc.EvaluateActivity(m, new ActivityThresholds(null, null, null, null, null)));
    }
}
