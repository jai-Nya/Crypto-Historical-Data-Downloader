namespace BybitDownloader.Cli;

public sealed record ValidateOptions(string OutputRoot, string DatabasePath, string? Symbol, string? Category);

public sealed record ValidateParseResult(ValidateOptions? Options, string? Error, bool ShowHelp)
{
    public bool IsOk => Options is not null;
    public static ValidateParseResult Ok(ValidateOptions o) => new(o, null, false);
    public static ValidateParseResult Fail(string e) => new(null, e, false);
    public static ValidateParseResult Help() => new(null, null, true);
}

/// <summary>Parses the <c>validate</c> subcommand (args[0] is expected to be "validate").</summary>
public static class ValidateParser
{
    public const string Usage = """
        bybit-dl validate - re-read downloaded Parquet files and verify integrity + completeness

        Usage:
          bybit-dl validate [options]

        Options:
          --out DIR          Output root to scan (default: data)
          --db PATH          Manifest SQLite path (default: <out>/manifest.db)
          --symbol SYM       Restrict to one symbol
          --category NAME    linear | inverse (default: all)
          -h, --help         Show this help

        For each {category}/{symbol}/{yyyy-MM}.parquet it checks readability, 1-minute alignment, ordering,
        duplicate and OHLCV sanity, gaps, row count vs expected, and cross-checks the manifest (status,
        row counts, and files recorded as Done but missing). Nothing is modified.
        Exit codes: 0 clean, 2 problems found, 1 configuration error.
        """;

    public static ValidateParseResult Parse(IReadOnlyList<string> args)
    {
        var outputRoot = "data";
        string? dbPath = null, symbol = null, category = null;

        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    return ValidateParseResult.Help();
                case "--out":
                    if (!Next(args, ref i, out var o, out var e1)) return ValidateParseResult.Fail(e1);
                    outputRoot = o;
                    break;
                case "--db":
                    if (!Next(args, ref i, out var d, out var e2)) return ValidateParseResult.Fail(e2);
                    dbPath = d;
                    break;
                case "--symbol":
                    if (!Next(args, ref i, out var s, out var e3)) return ValidateParseResult.Fail(e3);
                    symbol = s;
                    break;
                case "--category":
                    if (!Next(args, ref i, out var c, out var e4)) return ValidateParseResult.Fail(e4);
                    if (c is not ("linear" or "inverse"))
                        return ValidateParseResult.Fail($"Invalid --category value '{c}' (expected linear|inverse).");
                    category = c;
                    break;
                default:
                    return ValidateParseResult.Fail($"Unknown argument '{args[i]}'.");
            }
        }

        var root = string.IsNullOrWhiteSpace(outputRoot) ? "data" : outputRoot;
        var db = string.IsNullOrWhiteSpace(dbPath) ? Path.Combine(root, "manifest.db") : dbPath;
        return ValidateParseResult.Ok(new ValidateOptions(root, db, symbol, category));
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
}
