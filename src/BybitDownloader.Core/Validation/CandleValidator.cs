using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;

namespace BybitDownloader.Core.Validation;

public enum ValidationStatus { Valid, ValidWithGaps, Invalid }

public sealed record Gap(long FirstMissingMs, long MissingCount);

public sealed record ValidationResult(
    IReadOnlyList<Candle> Candles,
    int DuplicatesRemoved,
    int ConflictingDuplicates,
    int OutOfRange,
    int Misaligned,
    int InvalidRows,
    IReadOnlyList<Gap> Gaps,
    long ExpectedCandles,
    IReadOnlyList<string> Issues,
    ValidationStatus Status)
{
    /// <summary>Missing minutes strictly BETWEEN first and last observed candle.</summary>
    public long MissingIntervals => Gaps.Sum(g => g.MissingCount);
    public double Coverage => ExpectedCandles <= 0 ? 1.0 : Math.Min(1.0, (double)Candles.Count / ExpectedCandles);
}

/// <summary>
/// Sorts, de-duplicates and validates raw kline rows. Never fabricates candles. A gap is reported, not "repaired":
/// a missing minute can be a genuine zero-trade minute, so it is not proof of a data error.
/// </summary>
public static class CandleValidator
{
    public static ValidationResult Validate(IEnumerable<Candle> raw, long startMs, long endMsExclusive)
    {
        int outOfRange = 0, misaligned = 0, invalid = 0;
        var accepted = new List<Candle>();

        foreach (var c in raw)
        {
            if (c.TimestampMs < startMs || c.TimestampMs >= endMsExclusive) { outOfRange++; continue; }
            if (c.TimestampMs % DownloadPlanner.MinuteMs != 0) { misaligned++; continue; }
            if (!IsSane(c)) { invalid++; continue; }
            accepted.Add(c);
        }

        // LINQ OrderBy is stable, so "first seen wins" is deterministic for duplicates.
        var sorted = accepted.OrderBy(c => c.TimestampMs).ToList();
        var cleaned = new List<Candle>(sorted.Count);
        int dups = 0, conflicts = 0;
        foreach (var c in sorted)
        {
            if (cleaned.Count > 0 && cleaned[^1].TimestampMs == c.TimestampMs)
            {
                dups++;
                if (cleaned[^1] != c) conflicts++;
                continue;
            }
            cleaned.Add(c);
        }

        var gaps = new List<Gap>();
        for (var i = 1; i < cleaned.Count; i++)
        {
            var diff = cleaned[i].TimestampMs - cleaned[i - 1].TimestampMs;
            if (diff > DownloadPlanner.MinuteMs)
                gaps.Add(new Gap(cleaned[i - 1].TimestampMs + DownloadPlanner.MinuteMs, diff / DownloadPlanner.MinuteMs - 1));
        }

        var issues = new List<string>();
        if (outOfRange > 0) issues.Add($"{outOfRange} row(s) outside requested range were dropped");
        if (misaligned > 0) issues.Add($"{misaligned} row(s) not on a 1-minute boundary were dropped");
        if (invalid > 0) issues.Add($"{invalid} row(s) failed OHLCV sanity checks and were dropped");
        if (dups > 0) issues.Add($"{dups} duplicate timestamp(s) removed");
        if (conflicts > 0) issues.Add($"{conflicts} duplicate(s) had CONFLICTING values (first kept)");
        if (gaps.Count > 0) issues.Add($"{gaps.Sum(g => g.MissingCount)} missing minute(s) in {gaps.Count} gap(s)");

        var status = (invalid > 0 || misaligned > 0 || conflicts > 0) ? ValidationStatus.Invalid
            : gaps.Count > 0 ? ValidationStatus.ValidWithGaps
            : ValidationStatus.Valid;

        var expected = Math.Max(0, (endMsExclusive - startMs) / DownloadPlanner.MinuteMs);
        return new ValidationResult(cleaned, dups, conflicts, outOfRange, misaligned, invalid, gaps, expected, issues, status);
    }

    public static bool IsSane(Candle c) =>
        c.Open > 0 && c.High > 0 && c.Low > 0 && c.Close > 0
        && c.Volume >= 0 && c.Turnover >= 0
        && c.High >= c.Low
        && c.High >= c.Open && c.High >= c.Close
        && c.Low <= c.Open && c.Low <= c.Close;
}
