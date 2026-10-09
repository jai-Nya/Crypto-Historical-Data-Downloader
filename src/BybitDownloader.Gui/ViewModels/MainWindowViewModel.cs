using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
using BybitDownloader.Download;
using BybitDownloader.Gui.Services;
using BybitDownloader.Storage;

namespace BybitDownloader.Gui.ViewModels;

/// <summary>
/// Main-window view model. Pure BCL: all UI-thread marshalling goes through the injected <c>post</c>
/// delegate (the Avalonia dispatcher in production, a synchronous no-op in tests).
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IDownloadService _service;
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _clock;

    private CancellationTokenSource? _cts;

    private string _outputRoot = "data";
    private string _periodMode = "Month";
    private string _year;
    private string _month;
    private string _fromDate;
    private string _toDate;
    private string _categoryMode = "both";
    private string _symbolMode = "Screen universe";
    private string _preset = "balanced";
    private string _symbolsText = "";
    private string _workersText = "4";
    private bool _includeForming;
    private bool _isRunning;
    private string _status = "Idle.";
    private string _planSummary = "";
    private double _progressValue;

    public MainWindowViewModel(IDownloadService service, Action<Action>? post = null, Func<DateTimeOffset>? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _post = post ?? (action => action());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        var now = _clock();
        _year = now.Year.ToString(CultureInfo.InvariantCulture);
        _month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        _fromDate = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _toDate = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !IsRunning);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
    }

    public IReadOnlyList<string> PeriodModes { get; } = ["Year", "Month", "Range", "All"];
    public IReadOnlyList<string> CategoryModes { get; } = ["linear", "inverse", "both"];
    public IReadOnlyList<string> SymbolModes { get; } = ["Screen universe", "Explicit symbols"];
    public IReadOnlyList<string> Presets { get; } = ["broad", "balanced", "strict"];

    public ICommand PreviewCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand CancelCommand { get; }

    public ObservableCollection<ChunkProgressRow> Rows { get; } = [];

    public string OutputRoot { get => _outputRoot; set => SetProperty(ref _outputRoot, value); }
    public string PeriodMode { get => _periodMode; set { if (SetProperty(ref _periodMode, value)) OnPropertyChanged(nameof(PeriodHint)); } }
    public string Year { get => _year; set => SetProperty(ref _year, value); }
    public string Month { get => _month; set => SetProperty(ref _month, value); }
    public string FromDate { get => _fromDate; set => SetProperty(ref _fromDate, value); }
    public string ToDate { get => _toDate; set => SetProperty(ref _toDate, value); }
    public string CategoryMode { get => _categoryMode; set => SetProperty(ref _categoryMode, value); }
    public string SymbolMode { get => _symbolMode; set { if (SetProperty(ref _symbolMode, value)) { OnPropertyChanged(nameof(IsScreenMode)); OnPropertyChanged(nameof(IsExplicitMode)); } } }
    public string Preset { get => _preset; set => SetProperty(ref _preset, value); }
    public string SymbolsText { get => _symbolsText; set => SetProperty(ref _symbolsText, value); }
    public string WorkersText { get => _workersText; set => SetProperty(ref _workersText, value); }
    public bool IncludeForming { get => _includeForming; set => SetProperty(ref _includeForming, value); }

    public bool IsScreenMode => SymbolMode == "Screen universe";
    public bool IsExplicitMode => !IsScreenMode;
    public string DatabasePath => Path.Combine(OutputRoot, "manifest.db");
    public string PeriodHint => PeriodMode switch
    {
        "Year" => "Year (yyyy)",
        "Month" => "Month (yyyy-MM)",
        "Range" => "From / To (yyyy-MM-dd, inclusive)",
        _ => "All history up to now"
    };

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            ((AsyncRelayCommand)PreviewCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)StartCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)ValidateCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        }
    }

    public bool CanEdit => !IsRunning;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string PlanSummary { get => _planSummary; private set => SetProperty(ref _planSummary, value); }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    /// <summary>Builds the declarative job from the current inputs. Throws on invalid input.</summary>
    public DownloadJob BuildJob()
    {
        var now = _clock();
        var ranges = BuildRanges(now);

        var categories = CategoryMode switch
        {
            "linear" => (IReadOnlyList<MarketCategory>)[MarketCategory.Linear],
            "inverse" => [MarketCategory.Inverse],
            _ => [MarketCategory.Linear, MarketCategory.Inverse]
        };

        var screening = Preset switch
        {
            "broad" => ScreeningPresets.Broad,
            "strict" => ScreeningPresets.Strict,
            _ => ScreeningPresets.Balanced
        };

        if (!int.TryParse(WorkersText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var workers) || workers < 1)
            throw new InvalidOperationException($"Workers must be a positive integer (got '{WorkersText}').");

        if (string.IsNullOrWhiteSpace(OutputRoot))
            throw new InvalidOperationException("Output directory is required.");

        var screen = IsScreenMode;
        var symbols = screen ? [] : ParseSymbols(SymbolsText);
        if (!screen && symbols.Count == 0)
            throw new InvalidOperationException("Enter at least one symbol, or switch to screen mode.");

        return new DownloadJob
        {
            Categories = categories,
            ScreenUniverse = screen,
            Symbols = symbols,
            Screening = screening,
            Ranges = ranges,
            IncludeFormingCandle = IncludeForming,
            Orchestrator = new OrchestratorOptions { MaxParallelWorkers = workers },
            Now = now
        };
    }

    /// <summary>Turns the period selection into merged UTC half-open ranges. Throws on invalid input.</summary>
    public IReadOnlyList<UtcRange> BuildRanges(DateTimeOffset now)
    {
        var ranges = new List<UtcRange>();
        switch (PeriodMode)
        {
            case "Year":
                if (!int.TryParse(Year.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) || y is < 1970 or > 9998)
                    throw new InvalidOperationException($"Invalid year '{Year}'.");
                ranges.AddRange(PeriodSelector.Year(y));
                break;
            case "Month":
                if (!TryMonth(Month, out var my, out var mm))
                    throw new InvalidOperationException($"Invalid month '{Month}' (expected yyyy-MM).");
                ranges.Add(PeriodSelector.Month(my, mm));
                break;
            case "Range":
                if (!DateOnly.TryParseExact(FromDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
                    throw new InvalidOperationException($"Invalid 'from' date '{FromDate}' (expected yyyy-MM-dd).");
                if (!DateOnly.TryParseExact(ToDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
                    throw new InvalidOperationException($"Invalid 'to' date '{ToDate}' (expected yyyy-MM-dd).");
                if (to < from)
                    throw new InvalidOperationException("'To' must not be before 'From'.");
                ranges.AddRange(PeriodSelector.DateRange(from, to));
                break;
            case "All":
                ranges.AddRange(PeriodSelector.AllHistory(now));
                break;
            default:
                throw new InvalidOperationException($"Unknown period mode '{PeriodMode}'.");
        }

        var merged = PeriodSelector.Merge(ranges);
        if (merged.Count == 0) throw new InvalidOperationException("No period selected.");
        return merged;
    }

    public async Task PreviewAsync()
    {
        if (IsRunning) return;

        DownloadJob job;
        try
        {
            job = BuildJob();
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            return;
        }

        Status = "Planning...";
        try
        {
            var plan = await _service.PlanAsync(job, CancellationToken.None).ConfigureAwait(true);
            PlanSummary = FormatPlan(plan);
            Status = $"Plan ready: {plan.Symbols.Count} symbols, {plan.Plan.Chunks.Count} chunks." +
                     (plan.UnresolvedSymbols.Count > 0
                         ? $" Unresolved: {string.Join(", ", plan.UnresolvedSymbols)}"
                         : "");
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        DownloadJob job;
        try
        {
            job = BuildJob();
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            return;
        }

        Rows.Clear();
        PlanSummary = "";
        ProgressValue = 0;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Status = "Running...";

        try
        {
            var progress = new PostProgress(_post, ApplyProgress);
            var result = await _service.RunAsync(job, OutputRoot, DatabasePath, progress, _cts.Token)
                .ConfigureAwait(true);
            ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Re-reads every Parquet file under the output folder and reports integrity + completeness.</summary>
    public async Task ValidateAsync()
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(OutputRoot))
        {
            Status = "Error: output directory is required.";
            return;
        }

        Rows.Clear();
        PlanSummary = "";
        ProgressValue = 0;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Status = "Validating...";

        try
        {
            var summary = await _service.ValidateAsync(OutputRoot, DatabasePath, _cts.Token).ConfigureAwait(true);

            foreach (var f in summary.Files
                         .OrderByDescending(f => f.Status)
                         .ThenBy(f => f.Label, StringComparer.Ordinal))
                Rows.Add(new ChunkProgressRow(f.Label, MapStatus(f.Status), f.RowCount));

            var missing = summary.MissingFiles.Count > 0 ? $"; {summary.MissingFiles.Count} missing" : "";
            Status = $"Validated {summary.FileCount} file(s): {summary.OkCount} ok, " +
                     $"{summary.WarningCount} warnings, {summary.ErrorCount} errors{missing}.";
            ProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string MapStatus(FileValidationStatus status) => status switch
    {
        FileValidationStatus.Ok => "ok",
        FileValidationStatus.Warnings => "warn",
        _ => "error"
    };

    /// <summary>Applies one progress report. Must be called on the UI thread (marshalled by <see cref="PostProgress"/>).</summary>
    public void ApplyProgress(DownloadProgress progress)
    {
        var outcome = progress.Outcome;
        var status = outcome.Skipped ? "skipped" : outcome.Result == ChunkStatus.Failed ? "failed" : "done";
        Rows.Insert(0, new ChunkProgressRow(outcome.ChunkId, status, outcome.Rows));
        ProgressValue = progress.Total <= 0 ? 0 : Math.Round(progress.Completed * 100.0 / progress.Total, 1);

        var detail = outcome.Error is null ? "" : $" — {outcome.Error}";
        Status = $"{progress.Completed}/{progress.Total} chunks, {progress.CandlesDownloaded:N0} candles{detail}";
    }

    private void ApplyResult(DownloadJobResult result)
    {
        PlanSummary = FormatPlan(result.Plan);
        var s = result.Summary;
        Status = $"Done: {s.Succeeded} ok, {s.Skipped} skipped, {s.Failed} failed, " +
                 $"{s.TotalCandles:N0} candles in {s.Elapsed.TotalSeconds:0.0}s." +
                 (result.UnresolvedSymbols.Count > 0
                     ? $" Unresolved: {string.Join(", ", result.UnresolvedSymbols)}"
                     : "");
        ProgressValue = 100;
    }

    public static string FormatPlan(DownloadJobPlan plan)
    {
        var workload = DownloadPlanner.Estimate(plan.Plan.Chunks);
        var text = $"Symbols: {plan.Symbols.Count}    Chunks: {workload.Chunks}    " +
                   $"Candles: {workload.Candles:N0}    Requests (est): {workload.Requests:N0}";
        if (plan.Plan.UnavailableBeforeLaunch.Count > 0)
            text += $"\nRanges before launch (skipped): {plan.Plan.UnavailableBeforeLaunch.Count}";
        return text;
    }

    private static IReadOnlyList<string> ParseSymbols(string text) =>
        text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool TryMonth(string value, out int year, out int month)
    {
        year = month = 0;
        var parts = value.Trim().Split('-');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out year)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out month)
               && month is >= 1 and <= 12 && year is >= 1970 and <= 9998;
    }

    private sealed class PostProgress(Action<Action> post, Action<DownloadProgress> apply) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => post(() => apply(value));
    }
}
