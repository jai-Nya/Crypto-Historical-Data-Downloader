using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Validation;

namespace BybitDownloader.Storage;

public enum FileValidationStatus
{
    Ok,
    Warnings,
    Errors
}

/// <summary>Result of re-reading one Parquet partition. Coverage is against <see cref="ExpectedCandles"/>.</summary>
public sealed record FileValidationReport(
    string Path,
    string Category,
    string Symbol,
    int Year,
    int Month,
    long RowCount,
    long ExpectedCandles,
    double Coverage,
    long FirstMs,
    long LastMs,
    int Misaligned,
    int OutOfRange,
    int DuplicateTimestamps,
    int OutOfOrder,
    int InsaneRows,
    int GapCount,
    long MissingMinutes,
    bool ManifestRecorded,
    FileValidationStatus Status,
    IReadOnlyList<string> Issues)
{
    public string Label => $"{Category}/{Symbol}/{Year:D4}-{Month:D2}";
}

public sealed record ValidationSummary(
    int FileCount,
    int OkCount,
    int WarningCount,
    int ErrorCount,
    long TotalRows,
    long TotalExpected,
    long TotalMissingMinutes,
    IReadOnlyList<FileValidationReport> Files,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> FailedChunks)
{
    public bool HasProblems => ErrorCount > 0 || MissingFiles.Count > 0;
}

public sealed record FileValidationOptions
{
    /// <summary>Restrict to one category api string ("linear"/"inverse"). Null = all.</summary>
    public string? Category { get; init; }

    /// <summary>Restrict to one symbol. Null = all.</summary>
    public string? Symbol { get; init; }
}

/// <summary>
/// Pure checks over one already-read partition. Structural problems (misaligned/out-of-range/duplicate/out-of-order/
/// insane rows, or a manifest status that is not Done) are errors; low coverage / gaps / manifest mismatches are
/// warnings, because a missing minute can be a genuine zero-trade minute.
/// </summary>
public static class FileValidationAnalyzer
{
    public static FileValidationReport Analyze(
        string path, string category, string symbol, int year, int month,
        IReadOnlyList<Candle> candles, long expectedCandles, long windowStartMs, long windowEndMs,
        ChunkRecord? record)
    {
        int misaligned = 0, outOfRange = 0, duplicates = 0, outOfOrder = 0, insane = 0, gapCount = 0;
        long missing = 0, accepted = 0, firstMs = 0, lastMs = 0, prev = long.MinValue;
        var issues = new List<string>();

        foreach (var c in candles)
        {
            var t = c.TimestampMs;
            if (t % DownloadPlanner.MinuteMs != 0) { misaligned++; continue; }
            if (t < windowStartMs || t >= windowEndMs) { outOfRange++; continue; }
            if (!CandleValidator.IsSane(c)) { insane++; continue; }

            if (accepted > 0)
            {
                if (t == prev) { duplicates++; continue; }
                if (t < prev) { outOfOrder++; continue; }
                var diff = t - prev;
                if (diff > DownloadPlanner.MinuteMs)
                {
                    gapCount++;
                    missing += diff / DownloadPlanner.MinuteMs - 1;
                }
            }

            if (accepted == 0) firstMs = t;
            lastMs = t;
            prev = t;
            accepted++;
        }

        var coverage = expectedCandles <= 0 ? 1.0 : Math.Min(1.0, (double)accepted / expectedCandles);

        if (misaligned > 0) issues.Add($"{misaligned} row(s) not on a 1-minute boundary");
        if (outOfRange > 0) issues.Add($"{outOfRange} row(s) outside the expected window");
        if (insane > 0) issues.Add($"{insane} row(s) failed OHLCV sanity checks");
        if (duplicates > 0) issues.Add($"{duplicates} duplicate timestamp(s)");
        if (outOfOrder > 0) issues.Add($"{outOfOrder} out-of-order timestamp(s)");
        if (gapCount > 0) issues.Add($"{missing} missing minute(s) in {gapCount} gap(s); may be zero-trade minutes");

        var manifestRecorded = record is not null;
        var manifestMismatch = false;
        if (record is { } r)
        {
            if (r.Status != ChunkStatus.Done) issues.Add($"manifest status is {r.Status}, not Done");
            if (r.RowCount != accepted)
            {
                issues.Add($"manifest rows {r.RowCount:N0} != file rows {accepted:N0}");
                manifestMismatch = true;
            }
            else if (Math.Abs(r.Coverage - coverage) > 0.0005)
            {
                issues.Add($"manifest coverage {r.Coverage:0.###} != file {coverage:0.###}");
                manifestMismatch = true;
            }
        }
        else
        {
            issues.Add("no manifest record for this file");
        }

        var structural = misaligned > 0 || outOfRange > 0 || insane > 0 || duplicates > 0 || outOfOrder > 0;
        var manifestError = record is { Status: not ChunkStatus.Done };
        var status = structural || manifestError ? FileValidationStatus.Errors
            : coverage < 1.0 || manifestMismatch || !manifestRecorded ? FileValidationStatus.Warnings
            : FileValidationStatus.Ok;

        return new FileValidationReport(
            path, category, symbol, year, month, accepted, expectedCandles, coverage,
            firstMs, lastMs, misaligned, outOfRange, duplicates, outOfOrder, insane, gapCount, missing,
            manifestRecorded, status, issues);
    }
}

/// <summary>
/// Scans every <c>*.parquet</c> under a root, re-reads each partition and cross-checks it against the manifest.
/// Read-only: never writes, repairs or deletes anything.
/// </summary>
public sealed class CandleFileValidator
{
    private readonly string _root;
    private readonly ICandleStore _store;
    private readonly IChunkManifest? _manifest;

    public CandleFileValidator(string root, ICandleStore? store = null, IChunkManifest? manifest = null)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Root directory is required.", nameof(root));
        _root = Path.GetFullPath(root);
        _store = store ?? new ParquetCandleStore(_root);
        _manifest = manifest;
    }

    public async Task<ValidationSummary> ValidateAllAsync(
        FileValidationOptions? options = null, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        options ??= new FileValidationOptions();
        var at = now ?? DateTimeOffset.UtcNow;
        _manifest?.Initialize();
        var records = BuildRecordIndex();

        var files = new List<FileValidationReport>();
        foreach (var (path, category, symbol, year, month) in Discover(options))
        {
            ct.ThrowIfCancellationRequested();
            records.TryGetValue(Key(category, symbol, year, month), out var record);
            var (startMs, endMs, expected) = ExpectedWindow(year, month, record, at);

            try
            {
                var candles = await _store.ReadAsync(path, ct).ConfigureAwait(false);
                files.Add(FileValidationAnalyzer.Analyze(
                    path, category, symbol, year, month, candles, expected, startMs, endMs, record));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                files.Add(new FileValidationReport(
                    path, category, symbol, year, month, 0, expected, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    record is not null, FileValidationStatus.Errors, [$"unreadable: {ex.Message}"]));
            }
        }

        var missing = new List<string>();
        var failed = new List<string>();
        var missingSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_manifest is not null)
        {
            foreach (var r in _manifest.GetAll())
            {
                if (r.Status == ChunkStatus.Failed)
                {
                    if (failedSeen.Add(r.ChunkId)) failed.Add(r.ChunkId);
                    continue;
                }
                if (r.Status != ChunkStatus.Done) continue;
                var path = r.FilePath ?? _store.GetPath(ParseCategory(r.Category), r.Symbol, r.Year, r.Month);
                if (!File.Exists(path))
                {
                    var label = $"{r.Category}/{r.Symbol}/{r.Year:D4}-{r.Month:D2}";
                    if (missingSeen.Add(label)) missing.Add(label);
                }
            }
        }

        return new ValidationSummary(
            files.Count,
            files.Count(f => f.Status == FileValidationStatus.Ok),
            files.Count(f => f.Status == FileValidationStatus.Warnings),
            files.Count(f => f.Status == FileValidationStatus.Errors),
            files.Sum(f => f.RowCount),
            files.Sum(f => f.ExpectedCandles),
            files.Sum(f => f.MissingMinutes),
            files,
            missing,
            failed);
    }

    public IEnumerable<(string Path, string Category, string Symbol, int Year, int Month)> Discover(
        FileValidationOptions? options = null)
    {
        options ??= new FileValidationOptions();
        if (!Directory.Exists(_root)) yield break;

        foreach (var path in Directory.EnumerateFiles(_root, "*.parquet", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_root, path);
            var segs = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length < 3) continue;

            var category = segs[0];
            var symbol = segs[^2];
            if (!TryParseMonth(Path.GetFileNameWithoutExtension(segs[^1]), out var year, out var month)) continue;
            if (options.Category is { } cf && !string.Equals(cf, category, StringComparison.OrdinalIgnoreCase)) continue;
            if (options.Symbol is { } sf && !string.Equals(sf, symbol, StringComparison.OrdinalIgnoreCase)) continue;

            yield return (Path.GetFullPath(path), category, symbol, year, month);
        }
    }

    private Dictionary<string, ChunkRecord> BuildRecordIndex()
    {
        var map = new Dictionary<string, ChunkRecord>(StringComparer.OrdinalIgnoreCase);
        if (_manifest is null) return map;

        foreach (var r in _manifest.GetAll())
        {
            var key = Key(r.Category, r.Symbol, r.Year, r.Month);
            if (!map.TryGetValue(key, out var existing) || Prefer(r, existing))
                map[key] = r;
        }
        return map;
    }

    /// <summary>
    /// Picks the record that best represents the on-disk file: prefer <see cref="ChunkStatus.Done"/>, then the
    /// widest window. Legacy manifests could hold several rows for one still-forming month.
    /// </summary>
    private static bool Prefer(ChunkRecord candidate, ChunkRecord current)
    {
        if ((candidate.Status == ChunkStatus.Done) != (current.Status == ChunkStatus.Done))
            return candidate.Status == ChunkStatus.Done;
        return candidate.EndMs > current.EndMs;
    }

    private static (long StartMs, long EndMs, long Expected) ExpectedWindow(
        int year, int month, ChunkRecord? record, DateTimeOffset now)
    {
        if (record is not null)
            return (record.StartMs, record.EndMs, Math.Max(0, record.ExpectedCandles));

        var monthStart = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var start = monthStart;
        var end = monthStart.AddMonths(1);
        if (end > now) end = now;
        if (start < PeriodSelector.BybitEpoch) start = PeriodSelector.BybitEpoch;
        var expected = Math.Max(0, (end.ToUnixTimeMilliseconds() - start.ToUnixTimeMilliseconds()) / DownloadPlanner.MinuteMs);
        return (start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(), expected);
    }

    private static string Key(string category, string symbol, int year, int month) =>
        $"{category}|{symbol}|{year:D4}{month:D2}";

    private static MarketCategory ParseCategory(string value) =>
        string.Equals(value, "inverse", StringComparison.OrdinalIgnoreCase)
            ? MarketCategory.Inverse
            : MarketCategory.Linear;

    private static bool TryParseMonth(string name, out int year, out int month)
    {
        year = month = 0;
        var parts = name.Split('-');
        return parts.Length == 2
               && int.TryParse(parts[0], out year)
               && int.TryParse(parts[1], out month)
               && month is >= 1 and <= 12;
    }
}
