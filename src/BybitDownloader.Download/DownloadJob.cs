using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;

namespace BybitDownloader.Download;

/// <summary>
/// A complete, declarative download request: which symbols (explicit or screened), which period, and where to store.
/// Network access, storage and the manifest are supplied to <see cref="DownloadJobRunner"/>.
/// </summary>
public sealed record DownloadJob
{
    public required IReadOnlyList<MarketCategory> Categories { get; init; }

    /// <summary>When true the universe is screened; otherwise <see cref="Symbols"/> are resolved by name.</summary>
    public bool ScreenUniverse { get; init; }

    public IReadOnlyList<string> Symbols { get; init; } = [];

    public ScreeningSettings Screening { get; init; } = ScreeningPresets.Balanced;

    /// <summary>Half-open window for the optional candle-activity stage (required when Screening.Activity is set).</summary>
    public UtcRange? ActivitySample { get; init; }

    public required IReadOnlyList<UtcRange> Ranges { get; init; }

    public bool IncludeFormingCandle { get; init; }

    public OrchestratorOptions Orchestrator { get; init; } = new();

    /// <summary>Planning "now". Null = <see cref="DateTimeOffset.UtcNow"/> at run time.</summary>
    public DateTimeOffset? Now { get; init; }
}

public sealed record DownloadJobPlan(
    IReadOnlyList<SelectedSymbol> Symbols,
    IReadOnlyList<string> UnresolvedSymbols,
    PlanResult Plan);

public sealed record DownloadJobResult(DownloadJobPlan Plan, DownloadSummary Summary)
{
    public IReadOnlyList<SelectedSymbol> Symbols => Plan.Symbols;
    public IReadOnlyList<string> UnresolvedSymbols => Plan.UnresolvedSymbols;
}
