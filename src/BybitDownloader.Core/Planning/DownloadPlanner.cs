using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Planning;

/// <summary>Half-open UTC interval [Start, End).</summary>
public readonly record struct UtcRange(DateTimeOffset Start, DateTimeOffset End)
{
    public long StartMs => Start.ToUnixTimeMilliseconds();
    public long EndMs => End.ToUnixTimeMilliseconds();

    public static UtcRange Create(DateTimeOffset start, DateTimeOffset end)
    {
        start = start.ToUniversalTime();
        end = end.ToUniversalTime();
        if (end <= start) throw new ArgumentException("End must be after Start (end-exclusive).");
        return new UtcRange(start, end);
    }

    public static UtcRange FromMs(long startMs, long endMs) =>
        Create(DateTimeOffset.FromUnixTimeMilliseconds(startMs), DateTimeOffset.FromUnixTimeMilliseconds(endMs));

    public override string ToString() => $"[{Start:yyyy-MM-dd'T'HH:mm:ss'Z'}, {End:yyyy-MM-dd'T'HH:mm:ss'Z'})";
}

/// <summary>Turns GUI selections into UTC half-open ranges. Dates are ALWAYS interpreted as UTC dates.</summary>
public static class PeriodSelector
{
    public static readonly DateTimeOffset BybitEpoch = new(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static UtcRange Month(int year, int month)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        if (year is < 1970 or > 9998) throw new ArgumentOutOfRangeException(nameof(year));
        var s = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        return new UtcRange(s, s.AddMonths(1));
    }

    public static IReadOnlyList<UtcRange> Months(IEnumerable<(int Year, int Month)> months) =>
        Merge(months.Distinct().Select(m => Month(m.Year, m.Month)));

    public static IReadOnlyList<UtcRange> Year(int year) =>
        Months(Enumerable.Range(1, 12).Select(m => (year, m)));

    public static IReadOnlyList<UtcRange> Years(int fromYear, int toYearInclusive)
    {
        if (toYearInclusive < fromYear) throw new ArgumentException("toYear < fromYear");
        return Months(Enumerable.Range(fromYear, toYearInclusive - fromYear + 1)
            .SelectMany(y => Enumerable.Range(1, 12).Select(m => (y, m))));
    }

    /// <summary>Both dates inclusive, interpreted as UTC calendar dates.</summary>
    public static IReadOnlyList<UtcRange> DateRange(DateOnly start, DateOnly endInclusive)
    {
        if (endInclusive < start) throw new ArgumentException("End date before start date");
        var s = new DateTimeOffset(start.Year, start.Month, start.Day, 0, 0, 0, TimeSpan.Zero);
        var e = new DateTimeOffset(endInclusive.Year, endInclusive.Month, endInclusive.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
        return [UtcRange.Create(s, e)];
    }

    /// <summary>Explicit instants; any offset is converted to UTC (never reinterpreted).</summary>
    public static IReadOnlyList<UtcRange> Custom(DateTimeOffset start, DateTimeOffset endExclusive) =>
        [UtcRange.Create(start, endExclusive)];

    /// <summary>Everything from the Bybit epoch to now; the planner clamps to each symbol's launch time.</summary>
    public static IReadOnlyList<UtcRange> AllHistory(DateTimeOffset now) => [UtcRange.Create(BybitEpoch, now)];

    public static IReadOnlyList<UtcRange> Merge(IEnumerable<UtcRange> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var result = new List<UtcRange>();
        foreach (var r in sorted)
        {
            if (result.Count > 0 && r.Start <= result[^1].End)
            {
                var last = result[^1];
                if (r.End > last.End) result[^1] = new UtcRange(last.Start, r.End);
            }
            else result.Add(r);
        }
        return result;
    }
}

/// <summary>A month-partition slice of one symbol's request. End is exclusive. Id is deterministic and stable.</summary>
public sealed record DownloadChunk(MarketCategory Category, string Symbol, int Year, int Month, long StartMs, long EndMs)
{
    // Deliberately keyed by month only (not the timestamps): a still-forming month is re-planned with a later
    // end on every run, and an id that embedded the end would create a new manifest row each time instead of
    // updating the one for that month.
    public string Id => $"{Category.ToApiString()}/{Symbol}/{Year:D4}{Month:D2}";
    public long CandleCount => (EndMs - StartMs) / DownloadPlanner.MinuteMs;
}

/// <summary>One API request. Both bounds inclusive (matches Bybit kline start/end).</summary>
public readonly record struct RequestWindow(long StartMs, long EndMsInclusive)
{
    public long MaxCandles => (EndMsInclusive - StartMs) / DownloadPlanner.MinuteMs + 1;
}

public sealed record PlanResult(IReadOnlyList<DownloadChunk> Chunks, IReadOnlyList<UtcRange> UnavailableBeforeLaunch);

public sealed record Workload(int Chunks, long Candles, long Requests)
{
    public bool IsLarge => Requests > 10_000;
}

public static class DownloadPlanner
{
    public const long MinuteMs = 60_000;
    public const int MaxCandlesPerRequest = 1000;

    public static PlanResult Plan(
        MarketCategory category, string symbol, long? launchTimeMs,
        IEnumerable<UtcRange> ranges, DateTimeOffset now, bool includeFormingCandle = false)
    {
        var latestEnd = FloorMinute(now.ToUnixTimeMilliseconds()) + (includeFormingCandle ? MinuteMs : 0);
        long? launch = launchTimeMs is { } l ? FloorMinute(l) : null;

        var chunks = new List<DownloadChunk>();
        var unavailable = new List<UtcRange>();

        foreach (var r in PeriodSelector.Merge(ranges))
        {
            var s = CeilMinute(r.StartMs);
            var e = FloorMinute(r.EndMs);
            if (e <= s) continue;

            if (launch is { } lm && lm > s)
            {
                unavailable.Add(UtcRange.FromMs(s, Math.Min(lm, e)));
                s = lm;
            }
            e = Math.Min(e, latestEnd);
            if (s >= e) continue;

            var cur = DateTimeOffset.FromUnixTimeMilliseconds(s);
            var monthStart = new DateTimeOffset(cur.Year, cur.Month, 1, 0, 0, 0, TimeSpan.Zero);
            while (monthStart.ToUnixTimeMilliseconds() < e)
            {
                var next = monthStart.AddMonths(1);
                var cs = Math.Max(s, monthStart.ToUnixTimeMilliseconds());
                var ce = Math.Min(e, next.ToUnixTimeMilliseconds());
                if (cs < ce) chunks.Add(new DownloadChunk(category, symbol, monthStart.Year, monthStart.Month, cs, ce));
                monthStart = next;
            }
        }
        return new PlanResult(chunks, unavailable);
    }

    /// <summary>Contiguous, non-overlapping request windows of at most 1000 candles covering the chunk exactly.</summary>
    public static IEnumerable<RequestWindow> Windows(DownloadChunk chunk) => Windows(chunk.StartMs, chunk.EndMs);

    /// <summary>Same as <see cref="Windows(DownloadChunk)"/> but for a half-open [start, endExclusive) range.</summary>
    public static IEnumerable<RequestWindow> Windows(long startMs, long endMsExclusive)
    {
        var s = startMs;
        while (s < endMsExclusive)
        {
            var e = Math.Min(s + (MaxCandlesPerRequest - 1) * MinuteMs, endMsExclusive - MinuteMs);
            yield return new RequestWindow(s, e);
            s = e + MinuteMs;
        }
    }

    /// <summary>Plans every selected symbol into one combined result. Duplicate (category, symbol) pairs are ignored.</summary>
    public static PlanResult PlanUniverse(
        IEnumerable<(MarketCategory Category, string Symbol, long? LaunchTimeMs)> symbols,
        IEnumerable<UtcRange> ranges, DateTimeOffset now, bool includeFormingCandle = false)
    {
        var rangeList = ranges as IReadOnlyList<UtcRange> ?? ranges.ToList();
        var chunks = new List<DownloadChunk>();
        var unavailable = new List<UtcRange>();
        var seen = new HashSet<(MarketCategory, string)>();
        foreach (var (category, symbol, launch) in symbols)
        {
            if (!seen.Add((category, symbol))) continue;
            var plan = Plan(category, symbol, launch, rangeList, now, includeFormingCandle);
            chunks.AddRange(plan.Chunks);
            unavailable.AddRange(plan.UnavailableBeforeLaunch);
        }
        return new PlanResult(chunks, unavailable);
    }

    public static Workload Estimate(IEnumerable<DownloadChunk> chunks)
    {
        int n = 0; long candles = 0, requests = 0;
        foreach (var c in chunks)
        {
            n++;
            candles += c.CandleCount;
            requests += (c.CandleCount + MaxCandlesPerRequest - 1) / MaxCandlesPerRequest;
        }
        return new Workload(n, candles, requests);
    }

    public static long FloorMinute(long ms) => ms - (((ms % MinuteMs) + MinuteMs) % MinuteMs);
    public static long CeilMinute(long ms) => FloorMinute(ms + MinuteMs - 1);
}
