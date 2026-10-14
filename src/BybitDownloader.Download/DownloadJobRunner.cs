using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
using BybitDownloader.Core.Telemetry;
using BybitDownloader.Storage;

namespace BybitDownloader.Download;

/// <summary>
/// Runs a <see cref="DownloadJob"/> end to end: fetch the universe, pick symbols (screened or explicit),
/// plan month chunks, then drive the orchestrator. Shared entry point for the CLI and the GUI.
/// One exchange governs the whole run (see <see cref="DownloadJob.Exchange"/>).
/// </summary>
public sealed class DownloadJobRunner
{
    private readonly IExchangeMarketData _market;
    private readonly ExchangeProfile _profile;
    private readonly ICandleStore? _store;
    private readonly IChunkManifest? _manifest;
    private readonly UniverseScreener _screener;

    private static ExchangeProfile ResolveProfile(IExchangeMarketData market) =>
        market is ExchangeHttpClient client ? client.Profile : ExchangeProfiles.Get(Exchange.Bybit);

    public DownloadJobRunner(IExchangeMarketData market, ICandleStore store, IChunkManifest manifest,
        UniverseScreener? screener = null, ExchangeProfile? profile = null)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _profile = profile ?? ResolveProfile(market);
        _screener = screener ?? new UniverseScreener(_market, profile: _profile);
    }

    private DownloadJobRunner(IExchangeMarketData market, UniverseScreener? screener, ExchangeProfile? profile)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _profile = profile ?? ResolveProfile(market);
        _screener = screener ?? new UniverseScreener(_market, profile: _profile);
    }

    /// <summary>A runner that can resolve symbols and build plans but never downloads (and touches no disk).</summary>
    public static DownloadJobRunner ForPlanning(IExchangeMarketData market, UniverseScreener? screener = null,
        ExchangeProfile? profile = null) => new(market, screener, profile);

    /// <summary>Resolves symbols and builds the plan without touching the network for candles or the disk.</summary>
    public async Task<DownloadJobPlan> PlanAsync(DownloadJob job, CancellationToken ct = default)
    {
        Validate(job);
        var now = job.Now ?? DateTimeOffset.UtcNow;
        var snapshot = await _screener.FetchAsync(job.Categories, ct).ConfigureAwait(false);

        IReadOnlyList<SelectedSymbol> selected;
        IReadOnlyList<string> unresolved;
        if (job.ScreenUniverse)
        {
            var screening = AdjustForExchange(job.Screening, job.Categories);
            var result = await _screener.ScreenAsync(snapshot, new UniverseScreenOptions
            {
                Categories = job.Categories,
                Settings = screening,
                ActivitySample = job.ActivitySample,
            }, ct).ConfigureAwait(false);
            selected = result.Selected;
            unresolved = [];
        }
        else
        {
            (selected, unresolved) = ResolveExplicit(snapshot, job.Symbols);
        }

        var plan = DownloadPlanner.PlanUniverse(
            selected.Select(s => (s.Category, s.Symbol, s.LaunchTimeMs)), job.Ranges, now, job.IncludeFormingCandle,
            _profile.Exchange);

        return new DownloadJobPlan(selected, unresolved, plan);
    }

    public async Task<DownloadJobResult> RunAsync(DownloadJob job, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default, ITelemetry? telemetry = null)
    {
        if (_store is null || _manifest is null)
            throw new InvalidOperationException(
                "This runner was created for planning only; construct it with a store and manifest to download.");
        var plan = await PlanAsync(job, ct).ConfigureAwait(false);
        telemetry?.Report(new TelemetryEvent(TelemetryKind.Log, TelemetryLevel.Info,
            $"Planned {plan.Plan.Chunks.Count} chunk(s) for {plan.Symbols.Count} symbol(s)."));
        if (plan.UnresolvedSymbols.Count > 0)
            telemetry?.Report(new TelemetryEvent(TelemetryKind.Log, TelemetryLevel.Warning,
                $"Unresolved symbol(s): {string.Join(", ", plan.UnresolvedSymbols)}"));
        var orchestrator = new DownloadOrchestrator(_market, _store, _manifest, job.Orchestrator, telemetry);
        var summary = await orchestrator.RunAsync(plan.Plan, progress, ct).ConfigureAwait(false);
        return new DownloadJobResult(plan, summary);
    }

    /// <summary>
    /// Spot instruments and exchanges whose bulk ticker lacks open interest would otherwise fail the OI filter
    /// across the board, so the OI threshold is dropped when the exchange does not report it for a chosen category.
    /// </summary>
    private ScreeningSettings AdjustForExchange(ScreeningSettings settings, IReadOnlyList<MarketCategory> categories)
    {
        if (settings.MinOpenInterestValue is null) return settings;
        foreach (var category in categories)
        {
            if (!_profile.SupportsOpenInterest(category))
                return settings with { MinOpenInterestValue = null };
        }
        return settings;
    }

    private void Validate(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Categories.Count == 0) throw new ArgumentException("At least one category is required.", nameof(job));
        if (!job.ScreenUniverse && job.Symbols.Count == 0)
            throw new ArgumentException("Provide symbols or enable screening.", nameof(job));
        if (job.Ranges.Count == 0) throw new ArgumentException("At least one period range is required.", nameof(job));
    }

    private static (IReadOnlyList<SelectedSymbol> Resolved, IReadOnlyList<string> Unresolved) ResolveExplicit(
        UniverseSnapshot snapshot, IReadOnlyList<string> symbols)
    {
        var bySymbol = snapshot.Instruments
            .GroupBy(i => i.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var resolved = new List<SelectedSymbol>();
        var unresolved = new List<string>();
        foreach (var symbol in symbols.Distinct(StringComparer.Ordinal))
        {
            if (!bySymbol.TryGetValue(symbol, out var matches))
            {
                unresolved.Add(symbol);
                continue;
            }
            foreach (var inst in matches)
                resolved.Add(new SelectedSymbol(inst.Category, inst.Symbol, inst.LaunchTimeMs,
                    TickerMetricsCalculator.Compute(inst, snapshot.Tickers.GetValueOrDefault(symbol))));
        }
        return (resolved, unresolved);
    }
}