using BybitDownloader.Cli;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;

namespace BybitDownloader.Cli.Tests;

public class CliParserTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static CliOptions Ok(params string[] args)
    {
        var r = CliParser.Parse(args, Now);
        Assert.True(r.IsOk, r.Error);
        return r.Options!;
    }

    private static string Err(params string[] args)
    {
        var r = CliParser.Parse(args, Now);
        Assert.False(r.IsOk);
        Assert.False(r.ShowHelp);
        return r.Error!;
    }

    [Fact]
    public void Help_is_recognized()
    {
        Assert.True(CliParser.Parse(["--help"], Now).ShowHelp);
        Assert.True(CliParser.Parse(["-h"], Now).ShowHelp);
    }

    [Fact]
    public void Explicit_symbols_default_to_linear_and_parse_month()
    {
        var o = Ok("--symbol", "BTCUSDT", "--month", "2024-01");

        Assert.False(o.Screen);
        Assert.Equal([MarketCategory.Linear], o.Categories);
        Assert.Equal(["BTCUSDT"], o.Symbols);
        var range = Assert.Single(o.Ranges);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), range.Start);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero), range.End);
        Assert.Equal("data", o.OutputRoot);
        Assert.Equal(Path.Combine("data", "manifest.db"), o.DatabasePath);
        Assert.Equal(4, o.Workers);
        Assert.False(o.PlanOnly);
    }

    [Fact]
    public void Screen_defaults_to_both_categories_and_full_year()
    {
        var o = Ok("--screen", "--year", "2024");

        Assert.True(o.Screen);
        Assert.Equal([MarketCategory.Linear, MarketCategory.Inverse], o.Categories);
        var range = Assert.Single(o.Ranges); // 12 contiguous months merge into one range
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), range.Start);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), range.End);
    }

    [Fact]
    public void Strict_preset_implies_screen_and_builds_activity_window()
    {
        var o = Ok("--preset", "strict", "--year", "2024", "--activity-minutes", "60");

        Assert.True(o.Screen);
        Assert.Equal("Strict", o.Screening.Name);
        Assert.True(o.ActivitySample.HasValue);
        Assert.Equal(UtcRange.Create(Now.AddMinutes(-60), Now), o.ActivitySample.Value);
    }

    [Fact]
    public void Activity_without_thresholds_is_rejected()
    {
        Assert.Contains("activity", Err("--screen", "--year", "2024", "--activity-minutes", "60"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires --screen", Err("--symbol", "X", "--month", "2024-01", "--activity-minutes", "60"));
    }

    [Fact]
    public void Categories_parse_and_merge_months()
    {
        var o = Ok("--symbol", "BTCUSD", "--category", "inverse", "--month", "2024-01", "--month", "2024-02", "--month", "2024-02");

        Assert.Equal([MarketCategory.Inverse], o.Categories);
        var range = Assert.Single(o.Ranges); // contiguous months merge
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), range.Start);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), range.End);
    }

    [Fact]
    public void Years_range_and_all_history()
    {
        var years = Ok("--screen", "--years", "2022-2024");
        var single = Assert.Single(years.Ranges);
        Assert.Equal(new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero), single.Start);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), single.End);

        var all = Ok("--screen", "--all");
        var history = Assert.Single(all.Ranges);
        Assert.Equal(PeriodSelector.BybitEpoch, history.Start);
        Assert.Equal(Now, history.End);
    }

    [Fact]
    public void From_and_to_dates_are_inclusive()
    {
        var o = Ok("--symbol", "BTCUSDT", "--from", "2024-02-01", "--to", "2024-02-29");
        var range = Assert.Single(o.Ranges);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero), range.Start);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), range.End);
    }

    [Theory]
    [InlineData("--month", "2024-01")]
    [InlineData("--symbol", "BTCUSDT")]
    [InlineData("--symbol", "BTCUSDT", "--from", "2024-01-01")]
    [InlineData("--symbol", "BTCUSDT", "--to", "2024-01-01")]
    [InlineData("--symbol", "BTCUSDT", "--from", "2024-02-01", "--to", "2024-01-01")]
    public void Invalid_combinations_are_rejected(params string[] args)
    {
        Assert.NotEmpty(Err(args));
    }

    [Fact]
    public void Screen_and_symbol_cannot_be_combined()
    {
        Assert.Contains("cannot be combined", Err("--screen", "--symbol", "BTCUSDT", "--year", "2024"));
    }

    [Theory]
    [InlineData("--workers", "0")]
    [InlineData("--workers", "abc")]
    [InlineData("--preset", "wrong")]
    [InlineData("--category", "spot")]
    [InlineData("--month", "2024-13")]
    [InlineData("--year", "nope")]
    [InlineData("--frobnicate")]
    [InlineData("--symbol")]
    public void Bad_values_produce_errors(params string[] bad)
    {
        Assert.NotEmpty(Err(bad.Append("--year").Append("2024").ToArray()));
    }

    [Fact]
    public void Out_db_workers_and_flags_are_honoured()
    {
        var o = Ok("--symbol", "BTCUSDT", "--month", "2024-01",
            "--out", "D:/hist", "--db", "D:/hist/m.db", "--workers", "8", "--include-forming", "--plan-only");

        Assert.Equal("D:/hist", o.OutputRoot);
        Assert.Equal("D:/hist/m.db", o.DatabasePath);
        Assert.Equal(8, o.Workers);
        Assert.True(o.IncludeFormingCandle);
        Assert.True(o.PlanOnly);
    }
}
