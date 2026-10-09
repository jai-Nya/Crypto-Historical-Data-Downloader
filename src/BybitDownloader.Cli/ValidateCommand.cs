using BybitDownloader.Storage;

namespace BybitDownloader.Cli;

/// <summary>Offline validation front end. Scans the output root, re-reads each Parquet file and reports.</summary>
public static class ValidateCommand
{
    public static async Task<int> RunAsync(
        ValidateOptions options, DateTimeOffset now, TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            var store = new ParquetCandleStore(options.OutputRoot);
            using var manifest = File.Exists(options.DatabasePath) ? new SqliteChunkManifest(options.DatabasePath) : null;

            output.WriteLine($"Validating {Path.GetFullPath(options.OutputRoot)}" +
                             (manifest is null ? "  (no manifest found)" : $"  (manifest: {manifest.DatabasePath})"));

            var validator = new CandleFileValidator(options.OutputRoot, store, manifest);
            var summary = await validator.ValidateAllAsync(
                new FileValidationOptions { Category = options.Category, Symbol = options.Symbol }, now, ct)
                .ConfigureAwait(false);

            foreach (var f in summary.Files.OrderBy(f => f.Status).ThenBy(f => f.Label, StringComparer.Ordinal))
            {
                var tag = f.Status switch
                {
                    FileValidationStatus.Ok => "ok   ",
                    FileValidationStatus.Warnings => "warn ",
                    _ => "ERROR"
                };
                var detail = f.Issues.Count == 0 ? "" : "  " + string.Join("; ", f.Issues);
                output.WriteLine(
                    $"[{tag}] {f.Label}  rows={f.RowCount:N0}/{f.ExpectedCandles:N0} cov={f.Coverage:0.000}{detail}");
            }

            if (summary.FileCount == 0) output.WriteLine("No Parquet files found.");

            foreach (var missing in summary.MissingFiles)
                error.WriteLine($"missing file (manifest says Done): {missing}");
            foreach (var failed in summary.FailedChunks)
                error.WriteLine($"failed chunk in manifest: {failed}");

            output.WriteLine(
                $"Summary: {summary.FileCount} file(s) - {summary.OkCount} ok, {summary.WarningCount} warnings, " +
                $"{summary.ErrorCount} errors; {summary.TotalRows:N0}/{summary.TotalExpected:N0} rows; " +
                $"{summary.TotalMissingMinutes:N0} missing minutes.");
            if (summary.MissingFiles.Count > 0)
                error.WriteLine($"Missing files: {summary.MissingFiles.Count}");

            return summary.HasProblems ? 2 : 0;
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
}
