using BybitDownloader.Core.Api;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Resilience;
using BybitDownloader.Download;
using BybitDownloader.Storage;

namespace BybitDownloader.Cli;

/// <summary>Console front end. Kept free of <see cref="Console"/> statics so it can be driven from tests.</summary>
public static class CliApp
{
    public static async Task<int> RunAsync(
        string[] args, DateTimeOffset now, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args.Length > 0 && string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
        {
            var v = ValidateParser.Parse(args);
            if (v.ShowHelp)
            {
                output.WriteLine(ValidateParser.Usage);
                return 0;
            }
            if (!v.IsOk)
            {
                error.WriteLine($"error: {v.Error}");
                output.WriteLine();
                output.WriteLine(ValidateParser.Usage);
                return 1;
            }
            return await ValidateCommand.RunAsync(v.Options!, now, output, error, ct).ConfigureAwait(false);
        }

        var parsed = CliParser.Parse(args, now);
        if (parsed.ShowHelp)
        {
            output.WriteLine(CliParser.Usage);
            return 0;
        }
        if (!parsed.IsOk)
        {
            error.WriteLine($"error: {parsed.Error}");
            output.WriteLine();
            output.WriteLine(CliParser.Usage);
            return 1;
        }

        var o = parsed.Options!;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(BybitApiClient.DefaultBaseUrl) };
            var limiter = new AdaptiveRateLimiter(new RateLimiterOptions());
            var retry = new RetryPolicy(new RetryOptions());
            var client = new BybitApiClient(http, limiter, retry);

            var job = new DownloadJob
            {
                Categories = o.Categories,
                ScreenUniverse = o.Screen,
                Symbols = o.Symbols,
                Screening = o.Screening,
                ActivitySample = o.ActivitySample,
                Ranges = o.Ranges,
                IncludeFormingCandle = o.IncludeFormingCandle,
                Orchestrator = new OrchestratorOptions { MaxParallelWorkers = o.Workers },
                Now = now,
            };

            if (o.PlanOnly)
            {
                var plan = await DownloadJobRunner.ForPlanning(client).PlanAsync(job, ct).ConfigureAwait(false);
                PrintPlan(output, plan);
                if (plan.Symbols.Count == 0)
                    output.WriteLine("No symbols matched; nothing to download.");
                return 0;
            }

            using var manifest = new SqliteChunkManifest(o.DatabasePath);
            var store = new ParquetCandleStore(o.OutputRoot);
            var runner = new DownloadJobRunner(client, store, manifest);

            output.WriteLine($"Output: {Path.GetFullPath(o.OutputRoot)}  Manifest: {o.DatabasePath}");
            var result = await runner.RunAsync(job, new ConsoleProgress(output), ct).ConfigureAwait(false);
            PrintPlan(output, result.Plan);

            var s = result.Summary;
            output.WriteLine(
                $"Done: {s.Succeeded} ok, {s.Skipped} skipped, {s.Failed} failed, " +
                $"{s.TotalCandles:N0} candles in {s.Elapsed.TotalSeconds:0.0}s.");
            if (result.UnresolvedSymbols.Count > 0)
                output.WriteLine($"Unresolved symbols: {string.Join(", ", result.UnresolvedSymbols)}");

            return s.AnyFailed ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            error.WriteLine($"fatal: {ex.Message}");
            return 1;
        }
    }

    private static void PrintPlan(TextWriter output, DownloadJobPlan plan)
    {
        var workload = DownloadPlanner.Estimate(plan.Plan.Chunks);
        output.WriteLine(
            $"Symbols: {plan.Symbols.Count}  Chunks: {workload.Chunks}  " +
            $"Candles: {workload.Candles:N0}  Requests(est): {workload.Requests:N0}");
        if (plan.Plan.UnavailableBeforeLaunch.Count > 0)
            output.WriteLine($"Ranges before launch (skipped): {plan.Plan.UnavailableBeforeLaunch.Count}");
    }
}

internal sealed class ConsoleProgress(TextWriter output) : IProgress<DownloadProgress>
{
    private readonly object _gate = new();

    public void Report(DownloadProgress p)
    {
        var outcome = p.Outcome;
        var tag = outcome.Skipped ? "skip" : outcome.Result == ChunkStatus.Failed ? "FAIL" : "done";
        var suffix = outcome.Error is null ? "" : $"  {outcome.Error}";
        lock (_gate)
            output.WriteLine($"[{p.Completed,3}/{p.Total,-3}] {tag,-4} {outcome.ChunkId}  rows={outcome.Rows}{suffix}");
    }
}
