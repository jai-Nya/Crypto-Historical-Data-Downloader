using System.Globalization;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Screening;

public sealed record ActivityThresholds(
    decimal? MinNonZeroRangeFraction,
    decimal? MinNonZeroCloseChangeFraction,
    decimal? MinMedianRangePct,
    decimal? MinAtrPct,
    decimal? MinCoverage,
    int AtrPeriod = 14);

public sealed record ScreeningSettings
{
    public string Name { get; init; } = "Balanced";
    public decimal? MinTurnover24h { get; init; } = 2_000_000m;
    public decimal? MinOpenInterestValue { get; init; } = 500_000m;
    public decimal? MinPriceRangePct { get; init; } = 1.0m;
    public decimal? MaxSpreadPct { get; init; } = null;
    public bool RequireTrading { get; init; } = true;
    public bool RequirePerpetual { get; init; } = true;
    /// <summary>Second-stage (candle-based) thresholds. Null = stage disabled.</summary>
    public ActivityThresholds? Activity { get; init; } = null;
}

public static class ScreeningPresets
{
    // Heuristic starting points, NOT universal market-quality thresholds.
    public static ScreeningSettings Broad { get; } = new()
    {
        Name = "Broad", MinTurnover24h = 500_000m, MinOpenInterestValue = 100_000m, MinPriceRangePct = 0.5m
    };

    public static ScreeningSettings Balanced { get; } = new();

    public static ScreeningSettings Strict { get; } = new()
    {
        Name = "Strict", MinTurnover24h = 10_000_000m, MinOpenInterestValue = 3_000_000m,
        MinPriceRangePct = 2.0m, MaxSpreadPct = 0.05m,
        Activity = new ActivityThresholds(0.80m, 0.60m, 0.03m, 0.05m, 0.95m, 14)
    };
}

/// <summary>
/// Currency handling:
///  Linear  : turnover24h and openInterestValue are in the settle/quote coin (e.g. USDT). Used as-is.
///  Inverse : contracts are USD-denominated, so volume24h (contracts) ~ USD turnover and openInterest (contracts)
///            ~ USD open interest. Flagged Approximate. ASSUMPTION TO VERIFY against the tickers docs.
/// </summary>
public sealed record TickerMetrics(
    decimal? TurnoverQuote, decimal? OpenInterestValueQuote, decimal? PriceRangePct, decimal? SpreadPct,
    string Currency, bool Approximate);

public static class TickerMetricsCalculator
{
    public static TickerMetrics Compute(Instrument inst, Ticker? t)
    {
        var inverse = inst.Category == MarketCategory.Inverse;
        var currency = inverse ? "USD" : (string.IsNullOrEmpty(inst.SettleCoin) ? inst.QuoteCoin : inst.SettleCoin);
        if (t is null) return new TickerMetrics(null, null, null, null, currency, inverse);

        decimal? range = null;
        if (Pos(t.LastPrice) && Pos(t.HighPrice24h) && Pos(t.LowPrice24h) && t.HighPrice24h >= t.LowPrice24h)
            range = (t.HighPrice24h!.Value - t.LowPrice24h!.Value) / t.LastPrice!.Value * 100m;

        decimal? spread = null;
        if (Pos(t.Bid1Price) && Pos(t.Ask1Price) && t.Ask1Price >= t.Bid1Price)
        {
            var mid = (t.Ask1Price!.Value + t.Bid1Price!.Value) / 2m;
            spread = (t.Ask1Price.Value - t.Bid1Price.Value) / mid * 100m;
        }

        var turnover = inverse ? t.Volume24h : t.Turnover24h;
        var oi = inverse ? t.OpenInterest : t.OpenInterestValue;
        return new TickerMetrics(NonNeg(turnover), NonNeg(oi), range, spread, currency, inverse);
    }

    private static bool Pos(decimal? x) => x is > 0m;
    private static decimal? NonNeg(decimal? x) => x is >= 0m ? x : null;
}

public sealed record ScreeningResult(string Symbol, bool IsEligible, IReadOnlyList<string> Reasons, TickerMetrics Metrics);

public sealed class LiquidityFilterService
{
    public ScreeningResult Evaluate(Instrument inst, Ticker? ticker, ScreeningSettings s)
    {
        var reasons = new List<string>();
        var m = TickerMetricsCalculator.Compute(inst, ticker);

        if (s.RequirePerpetual && !inst.IsPerpetual) reasons.Add($"Not a perpetual contract ({inst.ContractType})");
        if (s.RequireTrading && !inst.IsTrading) reasons.Add($"Status is '{inst.Status}', not Trading");

        var tickerFilterOn = s.MinTurnover24h is not null || s.MinOpenInterestValue is not null
                             || s.MinPriceRangePct is not null || s.MaxSpreadPct is not null;
        if (ticker is null && tickerFilterOn)
        {
            reasons.Add("No ticker data available");
        }
        else
        {
            Min(reasons, "24h turnover", m.TurnoverQuote, s.MinTurnover24h, m.Currency);
            Min(reasons, "Open-interest value", m.OpenInterestValueQuote, s.MinOpenInterestValue, m.Currency);
            Min(reasons, "24h price range %", m.PriceRangePct, s.MinPriceRangePct, "%");
            if (s.MaxSpreadPct is { } max)
            {
                if (m.SpreadPct is null) reasons.Add("Bid-ask spread unavailable (filter enabled)");
                else if (m.SpreadPct > max) reasons.Add($"Spread {F(m.SpreadPct.Value)}% > maximum {F(max)}%");
            }
        }
        return new ScreeningResult(inst.Symbol, reasons.Count == 0, reasons, m);
    }

    public IReadOnlyList<ScreeningResult> EvaluateAll(
        IEnumerable<Instrument> instruments, IReadOnlyDictionary<string, Ticker> tickers, ScreeningSettings s) =>
        instruments.Select(i => Evaluate(i, tickers.GetValueOrDefault(i.Symbol), s)).ToList();

    public IReadOnlyList<string> EvaluateActivity(ActivityMetrics m, ActivityThresholds th)
    {
        var r = new List<string>();
        Min(r, "Non-zero high-low candles", m.NonZeroRangeFraction, th.MinNonZeroRangeFraction, "fraction");
        Min(r, "Non-zero close-change candles", m.NonZeroCloseChangeFraction, th.MinNonZeroCloseChangeFraction, "fraction");
        Min(r, "Median 1m range %", m.MedianRangePct, th.MinMedianRangePct, "%");
        Min(r, "ATR %", m.AtrPct, th.MinAtrPct, "%");
        Min(r, "Candle coverage", m.Coverage, th.MinCoverage, "fraction");
        return r;
    }

    private static void Min(List<string> reasons, string name, decimal? value, decimal? min, string unit)
    {
        if (min is not { } threshold) return; // filter disabled
        if (value is null) { reasons.Add($"{name} unavailable (filter enabled)"); return; }
        if (value < threshold) reasons.Add($"{name} {F(value.Value)} {unit} < minimum {F(threshold)}");
    }

    private static string F(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

public sealed record ActivityMetrics(
    int Candles, decimal NonZeroRangeFraction, decimal NonZeroCloseChangeFraction,
    decimal MedianRangePct, decimal AtrPct, decimal Coverage, int MissingIntervals);

public static class ActivityAnalyzer
{
    /// <summary>Input must be chronologically sorted and de-duplicated (see CandleValidator).</summary>
    public static ActivityMetrics Compute(IReadOnlyList<Candle> c, int expectedCandles, int atrPeriod)
    {
        var n = c.Count;
        if (n == 0) return new ActivityMetrics(0, 0, 0, 0, 0, 0, Math.Max(0, expectedCandles));

        var nonZeroRange = c.Count(x => x.High > x.Low);
        var nonZeroChange = 0;
        var trs = new List<decimal>(Math.Max(0, n - 1));
        for (var i = 1; i < n; i++)
        {
            var pc = c[i - 1].Close;
            if (c[i].Close != pc) nonZeroChange++;
            trs.Add(Math.Max(c[i].High - c[i].Low, Math.Max(Math.Abs(c[i].High - pc), Math.Abs(c[i].Low - pc))));
        }

        var ranges = c.Where(x => x.Close > 0).Select(x => (x.High - x.Low) / x.Close * 100m).OrderBy(v => v).ToList();
        decimal median = 0;
        if (ranges.Count > 0)
        {
            var mid = ranges.Count / 2;
            median = ranges.Count % 2 == 1 ? ranges[mid] : (ranges[mid - 1] + ranges[mid]) / 2m;
        }

        var take = Math.Min(Math.Max(1, atrPeriod), trs.Count);
        var lastClose = c[n - 1].Close;
        var atrPct = take == 0 || lastClose <= 0 ? 0m : trs.Skip(trs.Count - take).Average() / lastClose * 100m;

        var coverage = expectedCandles <= 0 ? 1m : Math.Min(1m, (decimal)n / expectedCandles);
        return new ActivityMetrics(n,
            (decimal)nonZeroRange / n,
            (decimal)nonZeroChange / Math.Max(1, n - 1),
            median, atrPct, coverage, Math.Max(0, expectedCandles - n));
    }
}
