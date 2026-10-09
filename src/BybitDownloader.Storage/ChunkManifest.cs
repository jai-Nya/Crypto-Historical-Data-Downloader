using BybitDownloader.Core.Planning;

namespace BybitDownloader.Storage;

public enum ChunkStatus
{
    Pending = 0,
    Done = 1,
    Failed = 2
}

/// <summary>Persisted state of one chunk. Coverage is stored[0,1]; Issues is human-readable.</summary>
public sealed record ChunkRecord(
    string ChunkId,
    string Category,
    string Symbol,
    int Year,
    int Month,
    long StartMs,
    long EndMs,
    ChunkStatus Status,
    long RowCount,
    long ExpectedCandles,
    double Coverage,
    string? FilePath,
    string? Issues,
    string UpdatedUtc);

/// <summary>Tracks per-chunk completion so a re-run can resume. Keyed by the deterministic <see cref="DownloadChunk.Id"/>.</summary>
public interface IChunkManifest
{
    void Initialize();
    ChunkRecord? Get(string chunkId);
    IReadOnlyList<ChunkRecord> GetAll();
    IReadOnlyList<ChunkRecord> GetByStatus(ChunkStatus status);
    void MarkPending(DownloadChunk chunk);
    void MarkDone(DownloadChunk chunk, long rowCount, long expectedCandles, double coverage, string filePath, string? issues);
    void MarkFailed(DownloadChunk chunk, string error, string? filePath = null);
    void Remove(string chunkId);
}
