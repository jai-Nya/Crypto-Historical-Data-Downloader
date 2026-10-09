namespace BybitDownloader.Cli.Tests;

public class ValidateParserTests
{
    private static ValidateParseResult Parse(params string[] args) => ValidateParser.Parse(args);

    [Fact]
    public void Defaults_to_data_root_and_manifest()
    {
        var r = Parse("validate");
        Assert.True(r.IsOk);
        Assert.Equal("data", r.Options!.OutputRoot);
        Assert.Equal(Path.Combine("data", "manifest.db"), r.Options.DatabasePath);
        Assert.Null(r.Options.Symbol);
        Assert.Null(r.Options.Category);
    }

    [Fact]
    public void Parses_all_options()
    {
        var r = Parse("validate", "--out", "D:/hist", "--db", "D:/hist/m.db",
            "--symbol", "BTCUSDT", "--category", "inverse");

        Assert.True(r.IsOk);
        Assert.Equal("D:/hist", r.Options!.OutputRoot);
        Assert.Equal("D:/hist/m.db", r.Options.DatabasePath);
        Assert.Equal("BTCUSDT", r.Options.Symbol);
        Assert.Equal("inverse", r.Options.Category);
    }

    [Fact]
    public void Db_defaults_under_out()
    {
        var r = Parse("validate", "--out", "D:/hist");
        Assert.Equal(Path.Combine("D:/hist", "manifest.db"), r.Options!.DatabasePath);
    }

    [Fact]
    public void Help_is_recognised()
    {
        Assert.True(Parse("validate", "--help").ShowHelp);
        Assert.True(Parse("validate", "-h").ShowHelp);
    }

    [Fact]
    public void Unknown_argument_fails()
    {
        var r = Parse("validate", "--nope");
        Assert.False(r.IsOk);
        Assert.Contains("--nope", r.Error);
    }

    [Fact]
    public void Missing_value_fails()
    {
        var r = Parse("validate", "--out");
        Assert.False(r.IsOk);
        Assert.Contains("--out", r.Error);
    }

    [Fact]
    public void Invalid_category_fails()
    {
        var r = Parse("validate", "--category", "spot");
        Assert.False(r.IsOk);
        Assert.Contains("spot", r.Error);
    }
}
