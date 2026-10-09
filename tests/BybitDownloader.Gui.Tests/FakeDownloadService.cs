using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
using BybitDownloader.Download;
using BybitDownloader.Gui.Services;
using BybitDownloader.Storage;

namespace BybitDownloader.Gui.Tests;

/// <summary>In-memory <see cref="IDownloadService"/> so view models can be driven without network or disk.</summary>
internal sealed class FakeDownloadService : IDownloadService
{
    public DownloadJob? LastJob { get; private set; }
    public string? LastOutputRoot { get; private set; }
    public string? LastDatabasePath { get; private set; }
    public int PlanCalls { get; private set; }
    public int RunCalls { get; private set; }
    public int ValidateCalls { get; private set; }
    public string? LastValidateRoot { get; private set; }

    public Func<DownloadJob, DownloadJobPlan>? PlanOverride { get; set; }
    public Func<DownloadJob, IProgress<DownloadProgress>?, CancellationToken, Task<DownloadJobResult>>? RunOverride { get; set; }
    public ValidationSummary? ValidateResult { get; set; }

    public Task<DownloadJobPlan> PlanAsync(DownloadJob job, CancellationToken ct)
    {
        PlanCalls++;
        LastJob = job;
        return Task.FromResult(PlanOverride?.Invoke(job) ?? BuildPlan(job));
    }

    public Task<DownloadJobResult> RunAsync(DownloadJob job, string outputRoot, string databasePath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        RunCalls++;
        LastJob = job;
        LastOutputRoot = outputRoot;
        LastDatabasePath = databasePath;
        if (RunOverride is not null) return RunOverride(job, progress, ct);
        return Task.FromResult(new DownloadJobResult(
            BuildPlan(job), new DownloadSummary(0, 0, 0, 0, 0, TimeSpan.Zero)));
    }

    public Task<ValidationSummary> ValidateAsync(string outputRoot, string databasePath, CancellationToken ct)
    {
        ValidateCalls++;
        LastValidateRoot = outputRoot;
        return Task.FromResult(ValidateResult
            ?? new ValidationSummary(0, 0, 0, 0, 0, 0, 0, [], [], []));
    }

    public static DownloadJobPlan BuildPlan(DownloadJob job)
    {
        var symbols = job.ScreenUniverse
            ? new[] { new SelectedSymbol(MarketCategory.Linear, "BTCUSDT", null, Metrics) }
            : job.Symbols.Select(s => new SelectedSymbol(MarketCategory.Linear, s, null, Metrics)).ToArray();

        var plan = DownloadPlanner.PlanUniverse(
            symbols.Select(s => (s.Category, s.Symbol, s.LaunchTimeMs)),
            job.Ranges, job.Now ?? DateTimeOffset.UnixEpoch, job.IncludeFormingCandle);

        return new DownloadJobPlan(symbols, [], plan);
    }

    public static readonly TickerMetrics Metrics = new(null, null, null, null, "USDT", false);

    public static ChunkOutcome Outcome(string id, ChunkStatus status, bool skipped = false, long rows = 1440, string? error = null) =>
        new(id, status, skipped, rows, 1.0, error);
}
