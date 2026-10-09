using BybitDownloader.Core.Api;
using BybitDownloader.Core.Resilience;
using BybitDownloader.Download;
using BybitDownloader.Storage;

namespace BybitDownloader.Gui.Services;

/// <summary>
/// GUI-facing operations over <see cref="DownloadJobRunner"/>. Kept as an interface so the view models
/// can be exercised with fakes (no network, no disk).
/// </summary>
public interface IDownloadService
{
    /// <summary>Resolves symbols and builds the plan without touching the network for candles or the disk.</summary>
    Task<DownloadJobPlan> PlanAsync(DownloadJob job, CancellationToken ct);

    /// <summary>Runs the job end to end, opening the Parquet store and SQLite manifest under the given paths.</summary>
    Task<DownloadJobResult> RunAsync(DownloadJob job, string outputRoot, string databasePath,
        IProgress<DownloadProgress>? progress, CancellationToken ct);

    /// <summary>Re-reads every Parquet file under the output root and checks integrity + completeness (offline).</summary>
    Task<ValidationSummary> ValidateAsync(string outputRoot, string databasePath, CancellationToken ct);
}

/// <summary>Owns the single long-lived HttpClient and the shared limiter/retry policy.</summary>
public sealed class DownloadService : IDownloadService, IDisposable
{
    private readonly HttpClient _http;
    private readonly IBybitMarketData _market;

    public DownloadService() : this(null) { }

    public DownloadService(IBybitMarketData? market)
    {
        if (market is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(BybitApiClient.DefaultBaseUrl) };
            _market = new BybitApiClient(
                _http, new AdaptiveRateLimiter(new RateLimiterOptions()), new RetryPolicy(new RetryOptions()));
        }
        else
        {
            _http = new HttpClient();
            _market = market;
        }
    }

    public Task<DownloadJobPlan> PlanAsync(DownloadJob job, CancellationToken ct) =>
        DownloadJobRunner.ForPlanning(_market).PlanAsync(job, ct);

    public async Task<DownloadJobResult> RunAsync(DownloadJob job, string outputRoot, string databasePath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var manifest = new SqliteChunkManifest(databasePath);
        var store = new ParquetCandleStore(outputRoot);
        var runner = new DownloadJobRunner(_market, store, manifest);
        return await runner.RunAsync(job, progress, ct).ConfigureAwait(false);
    }

    public async Task<ValidationSummary> ValidateAsync(string outputRoot, string databasePath, CancellationToken ct)
    {
        var store = new ParquetCandleStore(outputRoot);
        using var manifest = File.Exists(databasePath) ? new SqliteChunkManifest(databasePath) : null;
        var validator = new CandleFileValidator(outputRoot, store, manifest);
        return await validator.ValidateAllAsync(now: DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
