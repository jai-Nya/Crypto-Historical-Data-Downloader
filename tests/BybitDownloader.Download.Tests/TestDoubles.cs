using System.Collections.Concurrent;
using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Storage;

namespace BybitDownloader.Download.Tests;

/// <summary>Scripted market data. Returns candles (newest-first by default) for the requested inclusive window.</summary>
public sealed class FakeMarketData : IBybitMarketData
{
    private readonly Func<MarketCategory, string, long, long, IReadOnlyList<Candle>> _handler;
    private int _klinesCalls;

    public FakeMarketData(Func<MarketCategory, string, long, long, IReadOnlyList<Candle>>? handler = null)
        => _handler = handler ?? DefaultHandler;

    public int KlinesCalls => Volatile.Read(ref _klinesCalls);

    public readonly Dictionary<MarketCategory, List<Instrument>> Instruments = new();
    public readonly Dictionary<MarketCategory, List<Ticker>> Tickers = new();

    private readonly List<(long Start, long End)> _windows = new();
    public IReadOnlyList<(long Start, long End)> Windows { get { lock (_windows) return _windows.ToList(); } }

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Instrument>>(Instruments.GetValueOrDefault(category) ?? []);

    public Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Ticker>>(Tickers.GetValueOrDefault(category) ?? []);

    public Task<IReadOnlyList<Candle>> GetKlinesAsync(MarketCategory category, string symbol, long startMs,
        long endMsInclusive, CancellationToken ct)
    {
        Interlocked.Increment(ref _klinesCalls);
        lock (_windows) _windows.Add((startMs, endMsInclusive));
        return Task.FromResult(_handler(category, symbol, startMs, endMsInclusive));
    }

    public static IReadOnlyList<Candle> MakeWindow(long startMs, long endMsInclusive, bool descending = true)
    {
        var list = new List<Candle>();
        for (var ts = startMs; ts <= endMsInclusive; ts += 60_000)
            list.Add(new Candle(ts, 100m, 101m, 99m, 100m, 10m, 1000m));
        if (descending) list.Reverse();
        return list;
    }

    private static IReadOnlyList<Candle> DefaultHandler(MarketCategory c, string s, long a, long b) => MakeWindow(a, b);
}

/// <summary>In-memory stand-in for the Parquet store. Concurrent: the orchestrator writes from parallel workers.</summary>
public sealed class FakeCandleStore : ICandleStore
{
    public ConcurrentDictionary<string, List<Candle>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string GetPath(MarketCategory category, string symbol, int year, int month) =>
        $"{category.ToApiString()}/{symbol}/{year:D4}-{month:D2}.parquet";

    public string GetPath(DownloadChunk chunk) => GetPath(chunk.Category, chunk.Symbol, chunk.Year, chunk.Month);

    public bool Exists(string path) => Files.ContainsKey(path);

    public Task WriteAsync(string path, IReadOnlyList<Candle> candles, CancellationToken ct = default)
    {
        Files[path] = candles.ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Candle>> ReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Candle>>(Files[path]);
}

/// <summary>Invokes inline (unlike <see cref="Progress{T}"/>, which posts asynchronously). Deterministic for tests.</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public SyncProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}

/// <summary>In-memory manifest. Concurrent: the orchestrator records outcomes from parallel workers.</summary>
public sealed class FakeChunkManifest : IChunkManifest
{
    public ConcurrentDictionary<string, ChunkRecord> Records { get; } = new(StringComparer.OrdinalIgnoreCase);
    private int _initializeCount;
    public int InitializeCount => Volatile.Read(ref _initializeCount);

    public void Initialize() => Interlocked.Increment(ref _initializeCount);

    public ChunkRecord? Get(string chunkId) => Records.TryGetValue(chunkId, out var r) ? r : null;

    public IReadOnlyList<ChunkRecord> GetAll() => Records.Values.ToList();

    public IReadOnlyList<ChunkRecord> GetByStatus(ChunkStatus status) =>
        Records.Values.Where(r => r.Status == status).ToList();

    public void MarkPending(DownloadChunk chunk) => Set(chunk, ChunkStatus.Pending, 0, chunk.CandleCount, 0, null, null);

    public void MarkDone(DownloadChunk chunk, long rowCount, long expectedCandles, double coverage, string filePath, string? issues) =>
        Set(chunk, ChunkStatus.Done, rowCount, expectedCandles, coverage, filePath, issues);

    public void MarkFailed(DownloadChunk chunk, string error, string? filePath = null) =>
        Set(chunk, ChunkStatus.Failed, 0, chunk.CandleCount, 0, filePath, error);

    public void Remove(string chunkId) => Records.TryRemove(chunkId, out _);

    public void Seed(ChunkRecord record) => Records[record.ChunkId] = record;

    private void Set(DownloadChunk chunk, ChunkStatus status, long rowCount, long expectedCandles,
        double coverage, string? filePath, string? issues) =>
        Records[chunk.Id] = new ChunkRecord(chunk.Id, chunk.Category.ToApiString(), chunk.Symbol, chunk.Year, chunk.Month,
            chunk.StartMs, chunk.EndMs, status, rowCount, expectedCandles, coverage, filePath, issues,
            DateTimeOffset.UtcNow.ToString("O"));
}
