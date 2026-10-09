using BybitDownloader.Core.Validation;
using static BybitDownloader.Core.Tests.Fx;

namespace BybitDownloader.Core.Tests;

public class ValidatorTests
{
    private const long M = 60_000;

    [Fact]
    public void Sorts_descending_input_ascending()
    {
        var r = CandleValidator.Validate([C(B + 2 * M), C(B + M), C(B)], B, B + 3 * M);
        Assert.Equal(new long[] { B, B + M, B + 2 * M }, r.Candles.Select(c => c.TimestampMs));
        Assert.Equal(ValidationStatus.Valid, r.Status);
        Assert.Equal(1.0, r.Coverage);
    }

    [Fact]
    public void Identical_duplicates_are_removed_silently_valid()
    {
        var r = CandleValidator.Validate([C(B), C(B), C(B + M)], B, B + 2 * M);
        Assert.Equal(2, r.Candles.Count);
        Assert.Equal(1, r.DuplicatesRemoved);
        Assert.Equal(ValidationStatus.Valid, r.Status);
    }

    [Fact]
    public void Conflicting_duplicates_make_result_invalid()
    {
        var r = CandleValidator.Validate([C(B, c: 100), C(B, c: 100.5m)], B, B + M);
        Assert.Equal(1, r.ConflictingDuplicates);
        Assert.Equal(ValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void Detects_gaps_between_observed_candles_without_filling()
    {
        var r = CandleValidator.Validate([C(B), C(B + M), C(B + 5 * M), C(B + 6 * M)], B, B + 7 * M);
        var gap = Assert.Single(r.Gaps);
        Assert.Equal(B + 2 * M, gap.FirstMissingMs);
        Assert.Equal(3, gap.MissingCount);
        Assert.Equal(4, r.Candles.Count); // nothing fabricated
        Assert.Equal(ValidationStatus.ValidWithGaps, r.Status);
        Assert.Equal(3, r.MissingIntervals);
        Assert.Equal(7, r.ExpectedCandles);
    }

    [Fact]
    public void Empty_input_is_valid_with_zero_coverage()
    {
        var r = CandleValidator.Validate([], B, B + 10 * M);
        Assert.Empty(r.Candles);
        Assert.Equal(0.0, r.Coverage);
        Assert.Empty(r.Gaps);
    }

    [Fact]
    public void Out_of_range_rows_are_dropped_and_reported()
    {
        var r = CandleValidator.Validate([C(B - M), C(B), C(B + M)], B, B + M); // end exclusive
        Assert.Single(r.Candles);
        Assert.Equal(2, r.OutOfRange);
    }

    [Fact]
    public void Misaligned_timestamp_is_invalid()
    {
        var r = CandleValidator.Validate([C(B + 1)], B, B + M);
        Assert.Equal(1, r.Misaligned);
        Assert.Equal(ValidationStatus.Invalid, r.Status);
    }

    [Theory]
    [InlineData(100, 99, 98, 100)]   // high < open
    [InlineData(100, 101, 102, 100)] // low > open
    [InlineData(0, 1, 0, 0.5)]       // zero price
    [InlineData(100, 99, 99, 99)]    // high < close/open
    public void Insane_ohlc_rows_are_dropped(decimal o, decimal h, decimal l, decimal c)
    {
        var r = CandleValidator.Validate([C(B, o, h, l, c)], B, B + M);
        Assert.Equal(1, r.InvalidRows);
        Assert.Empty(r.Candles);
        Assert.Equal(ValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void Negative_volume_or_turnover_is_invalid_but_zero_is_fine()
    {
        Assert.False(CandleValidator.IsSane(C(B, v: -1)));
        Assert.False(CandleValidator.IsSane(C(B, t: -1)));
        Assert.True(CandleValidator.IsSane(C(B, v: 0, t: 0)));
    }
}
