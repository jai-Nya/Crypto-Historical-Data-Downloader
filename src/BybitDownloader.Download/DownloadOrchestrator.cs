using System.Diagnostics;
using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Telemetry;
using BybitDownloader.Core.Validation;
using BybitDownloader.Storage;

namespace BybitDownloader.Download;

public sealed record OrchestratorOptions
{
    /// <summary>Chunks processed concurrently. They share the injected client, hence one limiter/retry policy.</summary>
    public int MaxParallelWorkers { get; init; } = 8;

    /// <summary>Request windows fetched concurrently within a single chunk (lets one chunk use the full rate).</summary>
    public int MaxWindowConcurrency { get; init; } = 4;

    /// <summary>When true, a chunk already marked Done (with its file present) is not re-requested.</summary>
    public bool SkipCompleted { get; init; } = true;

    /// <summary>When true, the first chunk failure cancels the remaining work.</summary>
    public bool FailFast { get; init; } = false;
}

public sealed record ChunkOutcome(string ChunkId, ChunkStatus Result, bool Skipped, long Rows, double Coverage, string? Error);

public sealed record DownloadProgress(int Completed, int Total, long CandlesDownloaded, ChunkOutcome Outcome);

public sealed record DownloadSummary(int Total, int Succeeded, int Skipped, int Failed, long TotalCandles, TimeSpan Elapsed)
{
    public bool AnyFailed => Failed > 0;
}

/// <summary>
/// Drives a <see cref="PlanResult"/> to completion: skips finished chunks, fans the rest across N workers
/// (each chunk's request windows are fetched concurrently), merges + validates the candles, writes Parquet atomically,
/// then records the outcome in the manifest so the run is resumable.
/// </summary>
public sealed class DownloadOrchestrator
{
    private readonly IExchangeMarketData _market;
    private readonly ICandleStore _store;
    private readonly IChunkManifest _manifest;
    private readonly OrchestratorOptions _options;
    private readonly ITelemetry? _telemetry;

    public DownloadOrchestrator(IExchangeMarketData market, ICandleStore store, IChunkManifest manifest,
        OrchestratorOptions? options = null, ITelemetry? telemetry = null)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _options = options ?? new OrchestratorOptions();
        _telemetry = telemetry;
        if (_options.MaxParallelWorkers < 1)
            throw new ArgumentException("MaxParallelWorkers must be >= 1.", nameof(options));
        if (_options.MaxWindowConcurrency < 1)
            throw new ArgumentException("MaxWindowConcurrency must be >= 1.", nameof(options));
    }

    public async Task<DownloadSummary> RunAsync(PlanResult plan, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _manifest.Initialize();

        var chunks = plan.Chunks;
        EnsureDistinctTargets(chunks);

        var total = chunks.Count;
        var tally = new Tally();
        var sw = Stopwatch.StartNew();
        _telemetry?.Report(new TelemetryEvent(TelemetryKind.Log, TelemetryLevel.Info,
            $"Downloading {total} chunk(s) across {_options.MaxParallelWorkers} worker(s)."));

        if (total > 0)
        {
            using var failFastCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxParallelWorkers,
                CancellationToken = failFastCts.Token
            };

            try
            {
                await Parallel.ForEachAsync(chunks, parallelOptions, async (chunk, token) =>
                {
                    ChunkOutcome outcome;
                    try
                    {
                        outcome = await ProcessChunkAsync(chunk, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var path = _store.GetPath(chunk);
                        _manifest.MarkFailed(chunk, ex.Message, _store.Exists(path) ? path : null);
                        outcome = new ChunkOutcome(chunk.Id, ChunkStatus.Failed, false, 0, 0, ex.Message);
                        if (_options.FailFast) failFastCts.Cancel();
                    }

                    ReportCompleted(outcome);
                    var completed = tally.Add(outcome);
                    progress?.Report(new DownloadProgress(completed, total, tally.Candles, outcome));
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // FailFast cancelled the loop; partial results are already recorded.
            }
        }

        sw.Stop();
        return new DownloadSummary(total, tally.Succeeded, tally.Skipped, tally.Failed, tally.Candles, sw.Elapsed);
    }

    private async Task<ChunkOutcome> ProcessChunkAsync(DownloadChunk chunk, CancellationToken ct)
    {
        var path = _store.GetPath(chunk);

        // Skip only when the recorded run covered this exact window (or a wider one). A still-forming month is
        // re-planned with a later end each run, so its record's end is older than the chunk's -> re-download.
        if (_options.SkipCompleted
            && _manifest.Get(chunk.Id) is { Status: ChunkStatus.Done } record
            && _store.Exists(path)
            && record.StartMs <= chunk.StartMs
            && record.EndMs >= chunk.EndMs)
            return new ChunkOutcome(chunk.Id, ChunkStatus.Done, true, record.RowCount, record.Coverage, null);

        _manifest.MarkPending(chunk);

        _telemetry?.Report(new TelemetryEvent(TelemetryKind.ChunkStarted, TelemetryLevel.Info,
            $"Starting {chunk.Symbol} {chunk.Year:D4}-{chunk.Month:D2}.",
            ChunkId: chunk.Id, Symbol: chunk.Symbol, Category: chunk.Category));
        using var scope = DownloadScope.Begin(chunk.Id, chunk.Symbol, chunk.Category);

        var windows = DownloadPlanner.Windows(chunk).ToList();
        var batches = new IReadOnlyList<Candle>[windows.Count];
        var windowOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.MaxWindowConcurrency,
            CancellationToken = ct
        };
        await Parallel.ForEachAsync(Enumerable.Range(0, windows.Count), windowOptions, async (i, token) =>
        {
            var window = windows[i];
            batches[i] = await _market.GetKlinesAsync(
                chunk.Category, chunk.Symbol, window.StartMs, window.EndMsInclusive, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var raw = new List<Candle>();
        foreach (var batch in batches) raw.AddRange(batch);

        var validation = CandleValidator.Validate(raw, chunk.StartMs, chunk.EndMs);
        if (validation.Candles.Count == 0)
        {
            const string message = "No valid candles returned for chunk.";
            _manifest.MarkFailed(chunk, message, path);
            return new ChunkOutcome(chunk.Id, ChunkStatus.Failed, false, 0, 0, message);
        }

        await _store.WriteAsync(path, validation.Candles, ct).ConfigureAwait(false);
        var issues = validation.Issues.Count == 0 ? null : string.Join("; ", validation.Issues);
        _manifest.MarkDone(chunk, validation.Candles.Count, validation.ExpectedCandles, validation.Coverage, path, issues);
        return new ChunkOutcome(chunk.Id, ChunkStatus.Done, false, validation.Candles.Count, validation.Coverage, null);
    }

    private void ReportCompleted(ChunkOutcome outcome)
    {
        if (_telemetry is null) return;

        var failed = outcome.Result != ChunkStatus.Done;
        var level = failed ? TelemetryLevel.Error : TelemetryLevel.Info;
        var message = failed
            ? $"Failed {ChunkIdTail(outcome.ChunkId)}: {outcome.Error}"
            : outcome.Skipped
                ? $"Skipped {ChunkIdTail(outcome.ChunkId)} (already complete)."
                : $"Completed {ChunkIdTail(outcome.ChunkId)} ({outcome.Rows} rows, {outcome.Coverage:P0}).";

        _telemetry.Report(new TelemetryEvent(TelemetryKind.ChunkCompleted, level, message,
            ChunkId: outcome.ChunkId, Rows: outcome.Rows, Skipped: outcome.Skipped, Failed: failed));
    }

    private static string ChunkIdTail(string chunkId)
    {
        // Chunk id is "{exchange}/{category}/{symbol}/{yyyyMM}"; surface "symbol yyyyMM" for readability.
        var parts = chunkId.Split('/');
        return parts.Length >= 2 ? $"{parts[^2]} {parts[^1]}" : chunkId;
    }

    private void EnsureDistinctTargets(IReadOnlyList<DownloadChunk> chunks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            if (!seen.Add(_store.GetPath(chunk)))
                throw new InvalidOperationException(
                    $"Plan maps more than one chunk to the same file ({chunk.Symbol} {chunk.Year:D4}-{chunk.Month:D2}). " +
                    "Merge overlapping ranges before downloading.");
        }
    }

    private sealed class Tally
    {
        private int _completed;
        private int _succeeded;
        private int _skipped;
        private int _failed;
        private long _candles;

        public int Succeeded => Volatile.Read(ref _succeeded);
        public int Skipped => Volatile.Read(ref _skipped);
        public int Failed => Volatile.Read(ref _failed);
        public long Candles => Volatile.Read(ref _candles);

        public int Add(ChunkOutcome outcome)
        {
            Interlocked.Add(ref _candles, outcome.Rows);
            switch (outcome.Result)
            {
                case ChunkStatus.Done when outcome.Skipped: Interlocked.Increment(ref _skipped); break;
                case ChunkStatus.Done: Interlocked.Increment(ref _succeeded); break;
                default: Interlocked.Increment(ref _failed); break;
            }
            return Interlocked.Increment(ref _completed);
        }
    }
}
