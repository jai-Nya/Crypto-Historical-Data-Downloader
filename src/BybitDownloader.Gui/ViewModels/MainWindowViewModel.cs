using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
using BybitDownloader.Core.Telemetry;
using BybitDownloader.Download;
using BybitDownloader.Gui.Services;
using BybitDownloader.Storage;

namespace BybitDownloader.Gui.ViewModels;

/// <summary>
/// Main-window view model driving a step wizard: Exchange -> Markets &amp; Symbols -> Period -> Options ->
/// Summary -> Monitor. Pure BCL: all UI-thread marshalling goes through the injected <c>post</c> delegate
/// (the Avalonia dispatcher in production, a synchronous no-op in tests).
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    public const int ExchangeStep = 0;
    public const int MarketStep = 1;
    public const int PeriodStep = 2;
    public const int OptionsStep = 3;
    public const int SummaryStep = 4;
    public const int MonitorStep = 5;

    private const int MaxLogLines = 500;

    private readonly IDownloadService _service;
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<Task<string?>>? _folderPicker;

    private CancellationTokenSource? _cts;

    private int _step = ExchangeStep;
    private string _windowBackgroundColor = "#000000";
    private string _outputRoot = "data";
    private string _periodMode = "Month";
    private string _year;
    private string _month;
    private string _fromDate;
    private string _toDate;
    private string _exchangeMode = "bybit";
    private string _symbolMode = "All symbols (screen universe)";
    private string _preset = "balanced";
    private string _symbolsText = "";
    private string _workersText = "8";
    private bool _includeForming;
    private bool _includeTradFi;
    private bool _isSpot;
    private bool _isLinear = true;
    private bool _isInverse = true;
    private bool _isRunning;
    private string _status = "Idle.";
    private string _errorMessage = "";
    private string _planSummary = "";
    private string _summaryText = "";
    private string _currentSymbol = "";
    private int _completedChunks;
    private int _totalChunks;
    private int _retryCount;
    private double _progressValue;

    private readonly Dictionary<string, ChunkProgressRow> _rowsByChunk = new(StringComparer.Ordinal);

    public MainWindowViewModel(IDownloadService service, Action<Action>? post = null,
        Func<DateTimeOffset>? clock = null, Func<Task<string?>>? folderPicker = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _post = post ?? (action => action());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _folderPicker = folderPicker;

        var now = _clock();
        _year = now.Year.ToString(CultureInfo.InvariantCulture);
        _month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        _fromDate = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _toDate = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        NextCommand = new RelayCommand(Next, () => CanGoNext);
        PrimaryCommand = new AsyncRelayCommand(PrimaryAsync, () => Step == SummaryStep ? !IsRunning : CanGoNext);
        BackCommand = new RelayCommand(Back, () => CanGoBack);
        RestartCommand = new RelayCommand(Restart, () => CanRestart);
        SelectExchangeCommand = new RelayCommand<string>(SelectExchange, _ => !IsRunning);
        SelectPeriodCommand = new RelayCommand<string>(SelectPeriod, _ => !IsRunning);
        BrowseCommand = new AsyncRelayCommand(BrowseAsync, () => CanEdit && _folderPicker is not null);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !IsRunning);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
    }

    public IReadOnlyList<string> PeriodModes { get; } = ["Year", "Month", "Range", "All"];
    public IReadOnlyList<string> ExchangeModes { get; } = ["bybit", "binance", "okx", "kraken", "kucoin"];
    public IReadOnlyList<ExchangeOption> Exchanges { get; } =
    [
        new("bybit", "Bybit"),
        new("binance", "Binance"),
        new("okx", "OKX"),
        new("kraken", "Kraken"),
        new("kucoin", "KuCoin")
    ];
    public IReadOnlyList<string> SymbolModes { get; } = ["All symbols (screen universe)", "Specific symbols"];
    public IReadOnlyList<string> Presets { get; } = ["broad", "balanced", "strict"];
    public IReadOnlyList<string> YearOptions { get; } = BuildYearOptions(DateTimeOffset.UtcNow.Year);

    /// <summary>Brand accents used by the exchange tiles and by the window tint.</summary>
    private static readonly Dictionary<string, string> ExchangeAccents = new(StringComparer.Ordinal)
    {
        ["bybit"] = "#F7A600",
        ["binance"] = "#F0B90B",
        ["okx"] = "#BDBDBD",
        ["kraken"] = "#7132F5",
        ["kucoin"] = "#23AF91"
    };

    public ICommand NextCommand { get; }

    /// <summary>
    /// The footer's primary button: it advances the wizard and, on the review step, starts the download
    /// (matching the prototype's single Next/Start download button).
    /// </summary>
    public ICommand PrimaryCommand { get; }

    public ICommand BackCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand SelectExchangeCommand { get; }
    public ICommand SelectPeriodCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand CancelCommand { get; }

    public ObservableCollection<ChunkProgressRow> Rows { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    public string OutputRoot
    {
        get => _outputRoot;
        set
        {
            if (SetProperty(ref _outputRoot, value)) OnPropertyChanged(nameof(DatabasePath));
        }
    }

    public string PeriodMode
    {
        get => _periodMode;
        set
        {
            if (SetProperty(ref _periodMode, value))
            {
                OnPropertyChanged(nameof(IsYearPeriod));
                OnPropertyChanged(nameof(IsMonthPeriod));
                OnPropertyChanged(nameof(IsRangePeriod));
                OnPropertyChanged(nameof(IsAllPeriod));
                OnPropertyChanged(nameof(IsYearOrMonth));
            }
        }
    }

    public string Year
    {
        get => _year;
        set
        {
            if (SetProperty(ref _year, value)) OnPropertyChanged(nameof(MonthOptions));
        }
    }

    public string Month { get => _month; set => SetProperty(ref _month, value); }
    public string FromDate { get => _fromDate; set => SetProperty(ref _fromDate, value); }
    public string ToDate { get => _toDate; set => SetProperty(ref _toDate, value); }
    public string ExchangeMode
    {
        get => _exchangeMode;
        set
        {
            if (!SetProperty(ref _exchangeMode, value)) return;
            OnPropertyChanged(nameof(SelectedExchangeName));
            ApplyExchangeTint(value);
        }
    }

    public string SelectedExchangeName =>
        Exchanges.FirstOrDefault(e => e.Slug == ExchangeMode)?.Name ?? ExchangeMode;

    /// <summary>
    /// Full-window background colour: plain black until an exchange is chosen, then a dark tint of that
    /// exchange's brand accent (see <see cref="ExchangeAccents"/>).
    /// </summary>
    public string WindowBackgroundColor
    {
        get => _windowBackgroundColor;
        private set => SetProperty(ref _windowBackgroundColor, value);
    }

    public string SymbolMode
    {
        get => _symbolMode;
        set
        {
            if (SetProperty(ref _symbolMode, value))
            {
                OnPropertyChanged(nameof(IsScreenMode));
                OnPropertyChanged(nameof(IsExplicitMode));
            }
        }
    }

    public string Preset { get => _preset; set => SetProperty(ref _preset, value); }
    public string SymbolsText { get => _symbolsText; set => SetProperty(ref _symbolsText, value); }
    public string WorkersText { get => _workersText; set => SetProperty(ref _workersText, value); }
    public bool IncludeForming { get => _includeForming; set => SetProperty(ref _includeForming, value); }
    public bool IncludeTradFi { get => _includeTradFi; set => SetProperty(ref _includeTradFi, value); }

    public bool IsSpot { get => _isSpot; set => SetProperty(ref _isSpot, value); }
    public bool IsLinear { get => _isLinear; set => SetProperty(ref _isLinear, value); }
    public bool IsInverse { get => _isInverse; set => SetProperty(ref _isInverse, value); }

    public bool IsScreenMode
    {
        get => SymbolMode == SymbolModes[0];
        set
        {
            if (value) SymbolMode = SymbolModes[0];
        }
    }

    public bool IsExplicitMode
    {
        get => SymbolMode != SymbolModes[0];
        set
        {
            if (value) SymbolMode = SymbolModes[1];
        }
    }

    public bool IsYearPeriod => PeriodMode == "Year";
    public bool IsMonthPeriod => PeriodMode == "Month";
    public bool IsRangePeriod => PeriodMode == "Range";
    public bool IsAllPeriod => PeriodMode == "All";
    public bool IsYearOrMonth => IsYearPeriod || IsMonthPeriod;

    public IReadOnlyList<string> MonthOptions
    {
        get
        {
            var year = int.TryParse(Year.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                ? y
                : _clock().Year;
            return Enumerable.Range(1, 12)
                .Select(m => $"{year:D4}-{m:D2}")
                .ToList();
        }
    }

    public string DatabasePath => Path.Combine(OutputRoot, "manifest.db");

    public int Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            OnPropertyChanged(nameof(IsExchangeStep));
            OnPropertyChanged(nameof(IsMarketStep));
            OnPropertyChanged(nameof(IsPeriodStep));
            OnPropertyChanged(nameof(IsOptionsStep));
            OnPropertyChanged(nameof(IsSummaryStep));
            OnPropertyChanged(nameof(IsMonitorStep));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanRestart));
            OnPropertyChanged(nameof(ShowBack));
            OnPropertyChanged(nameof(ShowNext));
            OnPropertyChanged(nameof(ShowMonitorActions));
            OnPropertyChanged(nameof(NextLabel));
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(StepSubtitle));
            ErrorMessage = "";
            ((RelayCommand)NextCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)PrimaryCommand).RaiseCanExecuteChanged();
            ((RelayCommand)BackCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RestartCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)StartCommand).RaiseCanExecuteChanged();
        }
    }

    public bool IsExchangeStep => Step == ExchangeStep;
    public bool IsMarketStep => Step == MarketStep;
    public bool IsPeriodStep => Step == PeriodStep;
    public bool IsOptionsStep => Step == OptionsStep;
    public bool IsSummaryStep => Step == SummaryStep;
    public bool IsMonitorStep => Step == MonitorStep;

    public string StepTitle => Step switch
    {
        ExchangeStep => "Exchange",
        MarketStep => "Markets & symbols",
        PeriodStep => "Date range",
        OptionsStep => "Output & options",
        SummaryStep => "Review & start",
        MonitorStep => "Monitor",
        _ => ""
    };

    public string StepSubtitle => Step switch
    {
        ExchangeStep => "Choose the exchange for this run.",
        MarketStep => "Pick market types and which symbols to download.",
        PeriodStep => "Everything is interpreted as UTC.",
        OptionsStep => "Where files go and how many workers to use.",
        SummaryStep => "Check your selections before starting.",
        MonitorStep => "Download in progress.",
        _ => ""
    };

    public bool CanGoBack => Step > ExchangeStep && !IsRunning;
    public bool CanGoNext => Step < SummaryStep && !IsRunning;
    public bool CanRestart => !IsRunning && Step != ExchangeStep;

    /// <summary>Footer layout: Back on the middle steps, Next until the monitor, Cancel/New run on the monitor.</summary>
    public bool ShowBack => Step > ExchangeStep && Step < MonitorStep;
    public bool ShowNext => Step < MonitorStep;
    public bool ShowMonitorActions => Step == MonitorStep;
    public string NextLabel => Step == SummaryStep ? "Start download" : "Next";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanRestart));
            ((RelayCommand)NextCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)PrimaryCommand).RaiseCanExecuteChanged();
            ((RelayCommand)BackCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RestartCommand).RaiseCanExecuteChanged();
            ((RelayCommand<string>)SelectExchangeCommand).RaiseCanExecuteChanged();
            ((RelayCommand<string>)SelectPeriodCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)BrowseCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)PreviewCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)StartCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)ValidateCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        }
    }

    public bool CanEdit => !IsRunning;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Validation message shown in red under the wizard (cleared on every step change).</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }
    public string PlanSummary { get => _planSummary; private set => SetProperty(ref _planSummary, value); }
    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }

    public string CurrentSymbol { get => _currentSymbol; private set => SetProperty(ref _currentSymbol, value); }
    public int CompletedChunks { get => _completedChunks; private set { if (SetProperty(ref _completedChunks, value)) OnPropertyChanged(nameof(ChunkProgressText)); } }
    public int TotalChunks { get => _totalChunks; private set { if (SetProperty(ref _totalChunks, value)) OnPropertyChanged(nameof(ChunkProgressText)); } }
    public int RetryCount { get => _retryCount; private set { if (SetProperty(ref _retryCount, value)) OnPropertyChanged(nameof(RetryCountText)); } }

    public string ChunkProgressText => $"{CompletedChunks} / {TotalChunks} chunks";
    public string RetryCountText => RetryCount == 1 ? "1 retry" : $"{RetryCount} retries";

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    // ----- wizard navigation -------------------------------------------------

    private void Next()
    {
        if (IsRunning || Step >= SummaryStep) return;
        if (!TryValidateStep(Step, out var error))
        {
            ErrorMessage = error;
            Status = "Error: " + error;
            return;
        }
        ErrorMessage = "";
        Status = "Idle.";
        Step++;
        if (Step == SummaryStep) SummaryText = BuildSummaryText();
    }

    private async Task PrimaryAsync()
    {
        if (IsRunning) return;
        if (Step == SummaryStep)
        {
            await StartAsync().ConfigureAwait(true);
            return;
        }
        Next();
    }

    private void Back()
    {
        if (!CanGoBack) return;
        Step--;
    }

    private void Restart()
    {
        if (!CanRestart) return;
        Step = ExchangeStep;
        Status = "Idle.";
    }

    private void SelectExchange(string? slug)
    {
        if (IsRunning || string.IsNullOrWhiteSpace(slug)) return;
        if (!ExchangeExtensions.TryFromSlug(slug, out _)) return;

        ExchangeMode = slug;
        ApplyExchangeTint(slug);
    }

    /// <summary>Darkens the window to 20% of the exchange accent; black for unknown slugs.</summary>
    private void ApplyExchangeTint(string slug)
    {
        WindowBackgroundColor = ExchangeAccents.TryGetValue(slug, out var accent) ? Tint(accent) : "#000000";
    }

    private static string Tint(string hex)
    {
        static byte Scale(string part) => (byte)(Convert.ToByte(part, 16) * 0.2);
        return $"#{Scale(hex.Substring(1, 2)):X2}{Scale(hex.Substring(3, 2)):X2}{Scale(hex.Substring(5, 2)):X2}";
    }

    private void SelectPeriod(string? mode)
    {
        if (!IsRunning && mode is not null) PeriodMode = mode;
    }

    private async Task BrowseAsync()
    {
        if (_folderPicker is null) return;
        var picked = await _folderPicker().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(picked)) OutputRoot = picked;
    }

    private bool TryValidateStep(int step, out string error)
    {
        error = "";
        try
        {
            switch (step)
            {
                case ExchangeStep:
                    return true;
                case MarketStep:
                    if (Categories().Count == 0)
                    {
                        error = "Select at least one market type (spot, linear, inverse).";
                        return false;
                    }
                    if (!IsScreenMode && ParseSymbols(SymbolsText).Count == 0)
                    {
                        error = "Enter at least one symbol, or switch to screening the universe.";
                        return false;
                    }
                    return true;
                case PeriodStep:
                    BuildRanges(_clock());
                    return true;
                case OptionsStep:
                    if (!int.TryParse(WorkersText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var workers) || workers < 1)
                    {
                        error = $"Workers must be a positive integer (got '{WorkersText}').";
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(OutputRoot))
                    {
                        error = "Output directory is required.";
                        return false;
                    }
                    return true;
                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ----- job construction --------------------------------------------------

    /// <summary>Builds the declarative job from the current inputs. Throws on invalid input.</summary>
    public DownloadJob BuildJob()
    {
        var now = _clock();
        var ranges = BuildRanges(now);
        var categories = Categories();
        if (categories.Count == 0)
            throw new InvalidOperationException("Select at least one market type (spot, linear, inverse).");

        var exchange = ExchangeExtensions.TryFromSlug(ExchangeMode, out var ex) ? ex : Exchange.Bybit;

        var screening = Preset switch
        {
            "broad" => ScreeningPresets.Broad,
            "strict" => ScreeningPresets.Strict,
            _ => ScreeningPresets.Balanced
        } with { IncludeTradFi = IncludeTradFi };

        if (!int.TryParse(WorkersText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var workers) || workers < 1)
            throw new InvalidOperationException($"Workers must be a positive integer (got '{WorkersText}').");

        if (string.IsNullOrWhiteSpace(OutputRoot))
            throw new InvalidOperationException("Output directory is required.");

        var screen = IsScreenMode;
        var symbols = screen ? [] : ParseSymbols(SymbolsText);
        if (!screen && symbols.Count == 0)
            throw new InvalidOperationException("Enter at least one symbol, or switch to screening the universe.");

        return new DownloadJob
        {
            Exchange = exchange,
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

    private IReadOnlyList<MarketCategory> Categories()
    {
        var list = new List<MarketCategory>(3);
        if (IsSpot) list.Add(MarketCategory.Spot);
        if (IsLinear) list.Add(MarketCategory.Linear);
        if (IsInverse) list.Add(MarketCategory.Inverse);
        return list;
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

    public string BuildSummaryText()
    {
        try
        {
            var job = BuildJob();
            var categories = string.Join(", ", job.Categories.Select(c => c.ToApiString()));
            var symbols = job.ScreenUniverse
                ? $"screen universe ({job.Screening.Name} preset{(job.Screening.IncludeTradFi ? ", +TradFi" : "")})"
                : $"{job.Symbols.Count} symbol(s): {string.Join(", ", job.Symbols.Take(8))}{(job.Symbols.Count > 8 ? ", …" : "")}";
            return string.Join("\n",
                $"Exchange:    {job.Exchange.DisplayName()}",
                $"Markets:     {categories}",
                $"Symbols:     {symbols}",
                $"Period:      {DescribePeriod(job.Ranges)}",
                $"Forming:     {(job.IncludeFormingCandle ? "include current forming candle" : "completed candles only")}",
                $"Workers:     {job.Orchestrator.MaxParallelWorkers}",
                $"Output:      {OutputRoot}",
                $"Manifest:    {DatabasePath}");
        }
        catch (Exception ex)
        {
            return "Cannot build job: " + ex.Message;
        }
    }

    private string DescribePeriod(IReadOnlyList<UtcRange> ranges) => PeriodMode switch
    {
        "Year" => $"year {Year}",
        "Month" => $"month {Month}",
        "Range" => $"{FromDate} to {ToDate}",
        "All" => "all available history",
        _ => $"{ranges.Count} range(s)"
    };

    // ----- run / preview / validate -----------------------------------------

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

        ResetMonitor();
        Step = MonitorStep;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Status = "Running...";
        AddLogLine("Info", $"Starting {job.Exchange.DisplayName()} download.");

        try
        {
            var progress = new PostProgress(_post, ApplyProgress);
            var telemetry = new PostTelemetry(_post, ApplyTelemetry);
            var result = await _service.RunAsync(job, OutputRoot, DatabasePath, progress, _cts.Token, telemetry)
                .ConfigureAwait(true);
            ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            AddLogLine("Warning", "Cancelled.");
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            AddLogLine("Error", ex.Message);
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

        ResetMonitor();
        Step = MonitorStep;
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
            AddLogLine("Info", Status);
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

    // ----- telemetry / progress application ---------------------------------

    /// <summary>Applies one downstream progress report (UI thread; marshalled by <see cref="PostProgress"/>).</summary>
    public void ApplyProgress(DownloadProgress progress)
    {
        var outcome = progress.Outcome;
        var status = outcome.Skipped ? "skipped" : outcome.Result == ChunkStatus.Failed ? "failed" : "done";
        UpsertRow(outcome.ChunkId, status, outcome.Rows);

        CompletedChunks = progress.Completed;
        TotalChunks = progress.Total;
        ProgressValue = progress.Total <= 0 ? 0 : Math.Round(progress.Completed * 100.0 / progress.Total, 1);

        var detail = outcome.Error is null ? "" : $" — {outcome.Error}";
        Status = $"{progress.Completed}/{progress.Total} chunks, {progress.CandlesDownloaded:N0} candles{detail}";
    }

    /// <summary>Applies one core telemetry event (UI thread; marshalled by <see cref="PostTelemetry"/>).</summary>
    public void ApplyTelemetry(TelemetryEvent evt)
    {
        switch (evt.Kind)
        {
            case TelemetryKind.ChunkStarted:
                if (evt.Symbol is not null) CurrentSymbol = evt.Symbol;
                if (evt.ChunkId is not null) UpsertRow(evt.ChunkId, "running", 0);
                break;
            case TelemetryKind.ChunkCompleted:
                if (evt.ChunkId is not null)
                    UpsertRow(evt.ChunkId, evt.Failed ? "failed" : evt.Skipped ? "skipped" : "done", evt.Rows);
                break;
            case TelemetryKind.Retry:
                RetryCount++;
                if (evt.ChunkId is not null)
                {
                    var row = UpsertRow(evt.ChunkId, "retrying", 0);
                    row.Retries++;
                }
                break;
        }

        AddLogLine(evt.Level.ToString(), evt.Message);
    }

    private ChunkProgressRow UpsertRow(string chunkId, string status, long rows)
    {
        if (!_rowsByChunk.TryGetValue(chunkId, out var row))
        {
            row = new ChunkProgressRow(chunkId, status, rows);
            _rowsByChunk[chunkId] = row;
            Rows.Insert(0, row);
        }
        else
        {
            row.Status = status;
            if (rows > 0) row.Rows = rows;
        }
        return row;
    }

    private void AddLogLine(string level, string message)
    {
        Logs.Insert(0, $"{_clock():HH:mm:ss} [{level}] {message}");
        while (Logs.Count > MaxLogLines) Logs.RemoveAt(Logs.Count - 1);
    }

    private void ResetMonitor()
    {
        _rowsByChunk.Clear();
        Rows.Clear();
        Logs.Clear();
        PlanSummary = "";
        ProgressValue = 0;
        CurrentSymbol = "";
        CompletedChunks = 0;
        TotalChunks = 0;
        RetryCount = 0;
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
        CurrentSymbol = "";
        AddLogLine("Info", Status);
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

    private static IReadOnlyList<string> BuildYearOptions(int currentYear)
    {
        var years = new List<string>();
        for (var y = currentYear; y >= PeriodSelector.BybitEpoch.Year; y--)
            years.Add(y.ToString(CultureInfo.InvariantCulture));
        return years;
    }

    private sealed class PostProgress(Action<Action> post, Action<DownloadProgress> apply) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => post(() => apply(value));
    }

    private sealed class PostTelemetry(Action<Action> post, Action<TelemetryEvent> apply) : ITelemetry
    {
        public void Report(TelemetryEvent evt) => post(() => apply(evt));
    }
}

/// <summary>One selectable exchange: stable <paramref name="Slug"/> (bound to <see cref="MainWindowViewModel.ExchangeMode"/>) and display <paramref name="Name"/>.</summary>
public sealed record ExchangeOption(string Slug, string Name);
