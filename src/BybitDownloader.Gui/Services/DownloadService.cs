using System.Net;
using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Telemetry;
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
        IProgress<DownloadProgress>? progress, CancellationToken ct, ITelemetry? telemetry = null);

    /// <summary>Re-reads every Parquet file under the output root and checks integrity + completeness (offline).</summary>
    Task<ValidationSummary> ValidateAsync(string outputRoot, string databasePath, CancellationToken ct);
}

/// <summary>Owns the single long-lived HttpClient; builds the exchange adapter for each run from the job.</summary>
public sealed class DownloadService : IDownloadService, IDisposable
{
    private readonly HttpClient _http;
    private readonly IExchangeMarketData? _injectedMarket;

    public DownloadService() : this(null) { }

    public DownloadService(IExchangeMarketData? market)
    {
        if (market is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
            _injectedMarket = null;
        }
        else
        {
            _http = new HttpClient();
            _injectedMarket = market;
        }
    }

    private IExchangeMarketData MarketFor(DownloadJob job, ITelemetry? telemetry = null) =>
        MarketFor(job.Exchange, telemetry);
    private IExchangeMarketData MarketFor(Exchange exchange, ITelemetry? telemetry = null)
    {
        if (_injectedMarket is not null)
        {
            if (telemetry is not null && _injectedMarket is ExchangeHttpClient injected) injected.Telemetry = telemetry;
            return _injectedMarket;
        }
        return ExchangeClientFactory.Create(exchange, _http, telemetry);
    }

    public Task<DownloadJobPlan> PlanAsync(DownloadJob job, CancellationToken ct) =>
        DownloadJobRunner.ForPlanning(MarketFor(job)).PlanAsync(job, ct);

    public async Task<DownloadJobResult> RunAsync(DownloadJob job, string outputRoot, string databasePath,
        IProgress<DownloadProgress>? progress, CancellationToken ct, ITelemetry? telemetry = null)
    {
        using var manifest = new SqliteChunkManifest(databasePath);
        var store = new ParquetCandleStore(outputRoot);
        var runner = new DownloadJobRunner(MarketFor(job, telemetry), store, manifest);
        return await runner.RunAsync(job, progress, ct, telemetry).ConfigureAwait(false);
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