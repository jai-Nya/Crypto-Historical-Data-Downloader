using System.Globalization;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;

namespace BybitDownloader.Cli;

public sealed record CliOptions(
    IReadOnlyList<MarketCategory> Categories,
    bool Screen,
    IReadOnlyList<string> Symbols,
    ScreeningSettings Screening,
    UtcRange? ActivitySample,
    IReadOnlyList<UtcRange> Ranges,
    string OutputRoot,
    string DatabasePath,
    int Workers,
    bool IncludeFormingCandle,
    bool PlanOnly);

public sealed record CliParseResult(CliOptions? Options, string? Error, bool ShowHelp)
{
    public bool IsOk => Options is not null;
    public static CliParseResult Ok(CliOptions o) => new(o, null, false);
    public static CliParseResult Fail(string e) => new(null, e, false);
    public static CliParseResult Help() => new(null, null, true);
}

/// <summary>Hand-rolled parser (no CLI framework dependency). Pure: takes the clock and returns a result.</summary>
public static class CliParser
{
    public const string Usage = """
        bybit-dl - Bybit perpetual-futures historical 1-minute candle downloader

        Usage:
          bybit-dl <period> (--symbol SYM... | --screen [--preset NAME]) [options]
          bybit-dl validate [--out DIR] [--db PATH] [--symbol SYM] [--category NAME]

        Period (at least one; multiple are merged):
          --year YYYY            A whole year (repeatable)
          --month YYYY-MM        A calendar month (repeatable)
          --years YYYY-YYYY      An inclusive year range
          --from YYYY-MM-DD      Start date (inclusive); requires --to
          --to   YYYY-MM-DD      End date (inclusive); requires --from
          --all                  From the Bybit epoch to now

        Symbols:
          --symbol SYM           Explicit symbol (repeatable)
          --screen               Screen the live universe instead of naming symbols
          --preset NAME          broad | balanced | strict (implies --screen; default balanced)

        Options:
          --category NAME        linear | inverse | both (default: both when screening, else linear)
          --out DIR              Output root for Parquet files (default: data)
          --db PATH              Manifest SQLite path (default: <out>/manifest.db)
          --workers N            Parallel chunk workers (default: 4)
          --include-forming      Include the current, still-forming candle
          --activity-minutes N   Candle-activity sample window; requires a preset with activity (strict)
          --plan-only            Screen/plan and print the workload without downloading
          -h, --help             Show this help

        Commands:
          validate               Re-read downloaded Parquet files and verify integrity + completeness
                                 (see `bybit-dl validate --help`)

        Exit codes: 0 success, 1 usage/config error, 2 some chunks failed, 130 cancelled.
        """;

    public static CliParseResult Parse(IReadOnlyList<string> args, DateTimeOffset now)
    {
        var symbols = new List<string>();
        var years = new List<int>();
        var months = new List<(int Year, int Month)>();
        string? yearsRange = null;
        DateOnly? from = null, to = null;
        var all = false;
        var screen = false;
        var planOnly = false;
        var includeForming = false;
        string? categoryArg = null;
        var presetName = "balanced";
        var outputRoot = "data";
        string? dbPath = null;
        var workers = 4;
        int? activityMinutes = null;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    return CliParseResult.Help();
                case "--screen":
                    screen = true;
                    break;
                case "--plan-only":
                    planOnly = true;
                    break;
                case "--include-forming":
                    includeForming = true;
                    break;
                case "--all":
                    all = true;
                    break;
                case "--symbol":
                    if (!Next(args, ref i, out var sym, out var e1)) return CliParseResult.Fail(e1);
                    symbols.Add(sym);
                    break;
                case "--category":
                    if (!Next(args, ref i, out var cat, out var e2)) return CliParseResult.Fail(e2);
                    categoryArg = cat;
                    break;
                case "--preset":
                    if (!Next(args, ref i, out var pv, out var e3)) return CliParseResult.Fail(e3);
                    presetName = pv;
                    screen = true;
                    break;
                case "--out":
                    if (!Next(args, ref i, out var outv, out var e4)) return CliParseResult.Fail(e4);
                    outputRoot = outv;
                    break;
                case "--db":
                    if (!Next(args, ref i, out var dbv, out var e5)) return CliParseResult.Fail(e5);
                    dbPath = dbv;
                    break;
                case "--workers":
                    if (!Next(args, ref i, out var wv, out var e6)) return CliParseResult.Fail(e6);
                    if (!int.TryParse(wv, NumberStyles.Integer, CultureInfo.InvariantCulture, out workers) || workers < 1)
                        return CliParseResult.Fail($"Invalid --workers value '{wv}' (expected a positive integer).");
                    break;
                case "--year":
                    if (!Next(args, ref i, out var yv, out var e7)) return CliParseResult.Fail(e7);
                    if (!int.TryParse(yv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) || y is < 1970 or > 9998)
                        return CliParseResult.Fail($"Invalid --year value '{yv}'.");
                    years.Add(y);
                    break;
                case "--month":
                    if (!Next(args, ref i, out var mv, out var e8)) return CliParseResult.Fail(e8);
                    if (!TryMonth(mv, out var mm)) return CliParseResult.Fail($"Invalid --month value '{mv}' (expected YYYY-MM).");
                    months.Add(mm);
                    break;
                case "--years":
                    if (!Next(args, ref i, out var ys, out var e9)) return CliParseResult.Fail(e9);
                    yearsRange = ys;
                    break;
                case "--from":
                    if (!Next(args, ref i, out var fv, out var e10)) return CliParseResult.Fail(e10);
                    if (!DateOnly.TryParseExact(fv, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fd))
                        return CliParseResult.Fail($"Invalid --from date '{fv}' (expected YYYY-MM-DD).");
                    from = fd;
                    break;
                case "--to":
                    if (!Next(args, ref i, out var tv, out var e11)) return CliParseResult.Fail(e11);
                    if (!DateOnly.TryParseExact(tv, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var td))
                        return CliParseResult.Fail($"Invalid --to date '{tv}' (expected YYYY-MM-DD).");
                    to = td;
                    break;
                case "--activity-minutes":
                    if (!Next(args, ref i, out var av, out var e12)) return CliParseResult.Fail(e12);
                    if (!int.TryParse(av, NumberStyles.Integer, CultureInfo.InvariantCulture, out var am) || am < 1)
                        return CliParseResult.Fail($"Invalid --activity-minutes value '{av}' (expected a positive integer).");
                    activityMinutes = am;
                    break;
                default:
                    return CliParseResult.Fail($"Unknown argument '{a}'.");
            }
        }

        if (screen && symbols.Count > 0)
            return CliParseResult.Fail("--screen cannot be combined with --symbol.");

        var screening = presetName switch
        {
            "broad" => ScreeningPresets.Broad,
            "balanced" => ScreeningPresets.Balanced,
            "strict" => ScreeningPresets.Strict,
            _ => null
        };
        if (screening is null)
            return CliParseResult.Fail($"Unknown --preset '{presetName}' (expected broad|balanced|strict).");

        if (!screen && symbols.Count == 0)
            return CliParseResult.Fail("No symbols selected. Use --symbol SYM (repeatable) or --screen.");

        UtcRange? activitySample = null;
        if (activityMinutes is { } mins)
        {
            if (!screen) return CliParseResult.Fail("--activity-minutes requires --screen.");
            if (screening.Activity is null)
                return CliParseResult.Fail("The chosen preset has no candle-activity thresholds; use --preset strict.");
            activitySample = UtcRange.Create(now.AddMinutes(-mins), now);
        }

        var categories = ParseCategories(categoryArg, screen, out var catError);
        if (catError is not null) return CliParseResult.Fail(catError);

        var ranges = new List<UtcRange>();
        foreach (var y in years) ranges.AddRange(PeriodSelector.Year(y));
        if (months.Count > 0) ranges.AddRange(PeriodSelector.Months(months));
        if (yearsRange is not null)
        {
            if (!TryYearsRange(yearsRange, out var a0, out var a1))
                return CliParseResult.Fail($"Invalid --years value '{yearsRange}' (expected YYYY-YYYY).");
            ranges.AddRange(PeriodSelector.Years(a0, a1));
        }
        if (from is { } fs && to is { } ts)
        {
            if (ts < fs) return CliParseResult.Fail("--to must not be before --from.");
            ranges.AddRange(PeriodSelector.DateRange(fs, ts));
        }
        else if (from is not null || to is not null)
        {
            return CliParseResult.Fail("--from and --to must be provided together.");
        }
        if (all) ranges.AddRange(PeriodSelector.AllHistory(now));

        var merged = PeriodSelector.Merge(ranges);
        if (merged.Count == 0)
            return CliParseResult.Fail("No period specified. Use --year, --month, --years, --from/--to, or --all.");

        var root = string.IsNullOrWhiteSpace(outputRoot) ? "data" : outputRoot;
        var database = string.IsNullOrWhiteSpace(dbPath) ? Path.Combine(root, "manifest.db") : dbPath;

        return CliParseResult.Ok(new CliOptions(
            categories, screen, symbols, screening, activitySample, merged, root, database, workers, includeForming, planOnly));
    }

    private static bool Next(IReadOnlyList<string> args, ref int i, out string value, out string error)
    {
        if (i + 1 >= args.Count)
        {
            value = "";
            error = $"Missing value for '{args[i]}'.";
            return false;
        }
        value = args[++i];
        error = "";
        return true;
    }

    private static IReadOnlyList<MarketCategory> ParseCategories(string? arg, bool screen, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(arg))
            return screen ? [MarketCategory.Linear, MarketCategory.Inverse] : [MarketCategory.Linear];
        switch (arg.ToLowerInvariant())
        {
            case "linear": return [MarketCategory.Linear];
            case "inverse": return [MarketCategory.Inverse];
            case "both": return [MarketCategory.Linear, MarketCategory.Inverse];
            default:
                error = $"Invalid --category value '{arg}' (expected linear|inverse|both).";
                return [];
        }
    }

    private static bool TryMonth(string value, out (int Year, int Month) month)
    {
        month = default;
        var parts = value.Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || m is < 1 or > 12 || y is < 1970 or > 9998)
            return false;
        month = (y, m);
        return true;
    }

    private static bool TryYearsRange(string value, out int fromYear, out int toYearInclusive)
    {
        fromYear = toYearInclusive = 0;
        var parts = value.Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out fromYear)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out toYearInclusive))
            return false;
        return toYearInclusive >= fromYear && fromYear >= 1970;
    }
}
