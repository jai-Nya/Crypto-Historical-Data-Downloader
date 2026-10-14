using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Telemetry;

/// <summary>Severity of a log-style telemetry event.</summary>
public enum TelemetryLevel { Info, Warning, Error }

/// <summary>What a telemetry event describes.</summary>
public enum TelemetryKind
{
    /// <summary>A free-form log line.</summary>
    Log,
    /// <summary>A chunk (symbol-month) began downloading.</summary>
    ChunkStarted,
    /// <summary>A chunk finished (done/skipped/failed).</summary>
    ChunkCompleted,
    /// <summary>A request is being retried.</summary>
    Retry,
}

/// <summary>
/// A single progress/log signal emitted while planning and downloading. Chunk-bound events carry the chunk id,
/// symbol and category so a UI can attribute retries and status to the right row.
/// </summary>
public sealed record TelemetryEvent(
    TelemetryKind Kind,
    TelemetryLevel Level,
    string Message,
    string? ChunkId = null,
    string? Symbol = null,
    MarketCategory? Category = null,
    int Attempt = 0,
    long Rows = 0,
    bool Skipped = false,
    bool Failed = false);

/// <summary>Receives <see cref="TelemetryEvent"/>s. Implementations must be thread-safe (reports come from parallel workers).</summary>
public interface ITelemetry
{
    void Report(TelemetryEvent evt);
}

/// <summary>
/// Ambient "currently downloading chunk" context, flowed across awaits. The HTTP layer reads it so a retry can be
/// attributed to the chunk being fetched without threading the chunk through every call.
/// </summary>
public static class DownloadScope
{
    private sealed record Scope(string ChunkId, string Symbol, MarketCategory Category);

    private static readonly AsyncLocal<Scope?> Ambient = new();

    public static string? ChunkId => Ambient.Value?.ChunkId;
    public static string? Symbol => Ambient.Value?.Symbol;
    public static MarketCategory? Category => Ambient.Value?.Category;

    public static IDisposable Begin(string chunkId, string symbol, MarketCategory category)
    {
        var previous = Ambient.Value;
        Ambient.Value = new Scope(chunkId, symbol, category);
        return new Restore(previous);
    }

    private sealed class Restore(Scope? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
