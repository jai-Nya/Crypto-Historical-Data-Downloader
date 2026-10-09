using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
using BybitDownloader.Storage;

namespace BybitDownloader.Download;

/// <summary>
/// Runs a <see cref="DownloadJob"/> end to end: fetch the universe, pick symbols (screened or explicit),
/// plan month chunks, then drive the orchestrator. Shared entry point for the CLI and the future GUI.
/// </summary>
public sealed class DownloadJobRunner
{
    private readonly IBybitMarketData _market;
    private readonly ICandleStore? _store;
    private readonly IChunkManifest? _manifest;
    private readonly UniverseScreener _screener;

    public DownloadJobRunner(IBybitMarketData market, ICandleStore store, IChunkManifest manifest,
        UniverseScreener? screener = null)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _screener = screener ?? new UniverseScreener(_market);
    }

    private DownloadJobRunner(IBybitMarketData market, UniverseScreener? screener)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _screener = screener ?? new UniverseScreener(_market);
    }

    /// <summary>A runner that can resolve symbols and build plans but never downloads (and touches no disk).</summary>
    public static DownloadJobRunner ForPlanning(IBybitMarketData market, UniverseScreener? screener = null) =>
        new(market, screener);

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
            var result = await _screener.ScreenAsync(snapshot, new UniverseScreenOptions
            {
                Categories = job.Categories,
                Settings = job.Screening,
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
            selected.Select(s => (s.Category, s.Symbol, s.LaunchTimeMs)), job.Ranges, now, job.IncludeFormingCandle);

        return new DownloadJobPlan(selected, unresolved, plan);
    }

    public async Task<DownloadJobResult> RunAsync(DownloadJob job, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (_store is null || _manifest is null)
            throw new InvalidOperationException(
                "This runner was created for planning only; construct it with a store and manifest to download.");
        var plan = await PlanAsync(job, ct).ConfigureAwait(false);
        var orchestrator = new DownloadOrchestrator(_market, _store, _manifest, job.Orchestrator);
        var summary = await orchestrator.RunAsync(plan.Plan, progress, ct).ConfigureAwait(false);
        return new DownloadJobResult(plan, summary);
    }

    private static void Validate(DownloadJob job)
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
