using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Validation;

namespace BybitDownloader.Core.Screening;

/// <summary>A point-in-time view of the exchange used for screening.</summary>
public sealed record UniverseSnapshot(
    IReadOnlyList<Instrument> Instruments,
    IReadOnlyDictionary<string, Ticker> Tickers);

/// <summary>A symbol that passed screening, carrying everything the planner needs.</summary>
public sealed record SelectedSymbol(
    MarketCategory Category, string Symbol, long? LaunchTimeMs, TickerMetrics Metrics);

public sealed record ActivityScreenResult(
    MarketCategory Category, string Symbol, bool IsEligible, IReadOnlyList<string> Reasons, ActivityMetrics Metrics);

public sealed record UniverseScreenResult(
    IReadOnlyList<SelectedSymbol> Selected,
    IReadOnlyList<ScreeningResult> InstrumentResults,
    IReadOnlyList<ActivityScreenResult> ActivityResults,
    IReadOnlyList<string> Errors);

public sealed record UniverseScreenOptions
{
    public ScreeningSettings Settings { get; init; } = ScreeningPresets.Balanced;
    public IReadOnlyList<MarketCategory> Categories { get; init; } = [MarketCategory.Linear, MarketCategory.Inverse];

    /// <summary>Half-open sample window for the optional candle-based activity stage. Required when Settings.Activity is set.</summary>
    public UtcRange? ActivitySample { get; init; }
}

/// <summary>
/// Turns live exchange data into a screening result. Stage 1 filters the instrument+ticker snapshot;
/// stage 2 (optional) measures recent candle activity for the survivors. Output feeds <c>DownloadPlanner.PlanUniverse</c>.
/// </summary>
public sealed class UniverseScreener
{
    private readonly IBybitMarketData _market;
    private readonly LiquidityFilterService _filter;

    public UniverseScreener(IBybitMarketData market, LiquidityFilterService? filter = null)
    {
        _market = market;
        _filter = filter ?? new LiquidityFilterService();
    }

    public async Task<UniverseSnapshot> FetchAsync(IEnumerable<MarketCategory> categories, CancellationToken ct)
    {
        var instruments = new List<Instrument>();
        var tickers = new Dictionary<string, Ticker>(StringComparer.Ordinal);
        foreach (var category in categories.Distinct())
        {
            instruments.AddRange(await _market.GetInstrumentsAsync(category, ct).ConfigureAwait(false));
            foreach (var t in await _market.GetTickersAsync(category, ct).ConfigureAwait(false))
                tickers[t.Symbol] = t;
        }
        return new UniverseSnapshot(instruments, tickers);
    }

    public IReadOnlyList<ScreeningResult> ScreenInstruments(UniverseSnapshot snapshot, ScreeningSettings settings) =>
        _filter.EvaluateAll(snapshot.Instruments, snapshot.Tickers, settings);

    public async Task<UniverseScreenResult> ScreenAsync(UniverseScreenOptions options, CancellationToken ct)
    {
        var snapshot = await FetchAsync(options.Categories, ct).ConfigureAwait(false);
        return await ScreenAsync(snapshot, options, ct).ConfigureAwait(false);
    }

    /// <summary>Screens a snapshot the caller already fetched (avoids a second network round-trip).</summary>
    public async Task<UniverseScreenResult> ScreenAsync(UniverseSnapshot snapshot, UniverseScreenOptions options, CancellationToken ct)
    {
        var settings = options.Settings;
        if (settings.Activity is not null && options.ActivitySample is null)
            throw new ArgumentException("Settings.Activity is set but no ActivitySample window was provided.", nameof(options));

        var evaluations = _filter.EvaluateAll(snapshot.Instruments, snapshot.Tickers, settings);

        var candidates = new List<SelectedSymbol>();
        for (var i = 0; i < evaluations.Count; i++)
        {
            if (!evaluations[i].IsEligible) continue;
            var inst = snapshot.Instruments[i];
            candidates.Add(new SelectedSymbol(inst.Category, inst.Symbol, inst.LaunchTimeMs, evaluations[i].Metrics));
        }

        var errors = new List<string>();
        var activityResults = new List<ActivityScreenResult>();
        var selected = candidates;

        if (settings.Activity is { } thresholds && options.ActivitySample is { } sample)
        {
            var survivors = new List<SelectedSymbol>();
            foreach (var candidate in candidates)
            {
                try
                {
                    var activity = await MeasureActivityAsync(candidate, sample, thresholds.AtrPeriod, ct).ConfigureAwait(false);
                    var reasons = _filter.EvaluateActivity(activity, thresholds);
                    var ok = reasons.Count == 0;
                    activityResults.Add(new ActivityScreenResult(candidate.Category, candidate.Symbol, ok, reasons, activity));
                    if (ok) survivors.Add(candidate);
                }
                catch (ApiException ex)
                {
                    errors.Add($"{candidate.Symbol}: {ex.Message}");
                }
            }
            selected = survivors;
        }

        return new UniverseScreenResult(selected, evaluations, activityResults, errors);
    }

    public async Task<ActivityMetrics> MeasureActivityAsync(
        SelectedSymbol symbol, UtcRange sample, int atrPeriod, CancellationToken ct)
    {
        var candles = new List<Candle>();
        foreach (var window in DownloadPlanner.Windows(sample.StartMs, sample.EndMs))
        {
            var raw = await _market.GetKlinesAsync(
                symbol.Category, symbol.Symbol, window.StartMs, window.EndMsInclusive, ct).ConfigureAwait(false);
            candles.AddRange(raw);
        }

        var validated = CandleValidator.Validate(candles, sample.StartMs, sample.EndMs);
        var expected = validated.ExpectedCandles > int.MaxValue ? int.MaxValue : (int)validated.ExpectedCandles;
        return ActivityAnalyzer.Compute(validated.Candles, expected, atrPeriod);
    }
}
