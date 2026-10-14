using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
using BybitDownloader.Core.Telemetry;
using BybitDownloader.Download;
using BybitDownloader.Gui.Services;
using BybitDownloader.Gui.ViewModels;
using BybitDownloader.Storage;

namespace BybitDownloader.Gui.Tests;

public class MainWindowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static MainWindowViewModel Vm(IDownloadService? service = null) =>
        new(service ?? new FakeDownloadService(), post: a => a(), clock: () => Now);

    [Fact]
    public void Defaults_DescribeAScreenMonthlyBothJob()
    {
        var vm = Vm();
        var job = vm.BuildJob();

        Assert.True(job.ScreenUniverse);
        Assert.Equal([MarketCategory.Linear, MarketCategory.Inverse], job.Categories);
        Assert.Single(job.Ranges);
        Assert.Equal(PeriodSelector.Month(2024, 6), job.Ranges[0]);
        Assert.Equal(8, job.Orchestrator.MaxParallelWorkers);
        Assert.Equal(ScreeningPresets.Balanced.Name, job.Screening.Name);
        Assert.Equal(Now, job.Now);
        Assert.False(job.Screening.IncludeTradFi);
    }

    [Fact]
    public void TradFi_ExcludedByDefault_AndCanBeEnabled()
    {
        var vm = Vm();

        var job = vm.BuildJob();
        Assert.False(job.Screening.IncludeTradFi);

        vm.IncludeTradFi = true;
        Assert.True(vm.BuildJob().Screening.IncludeTradFi);
    }

    [Fact]
    public void YearMode_ProducesOneMergedYearRange()
    {
        var vm = Vm();
        vm.PeriodMode = "Year";
        vm.Year = "2023";

        var ranges = vm.BuildRanges(Now);

        Assert.Single(ranges);
        Assert.Equal(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), ranges[0].Start);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), ranges[0].End);
    }

    [Fact]
    public void RangeMode_UsesInclusiveDates()
    {
        var vm = Vm();
        vm.PeriodMode = "Range";
        vm.FromDate = "2024-01-10";
        vm.ToDate = "2024-01-12";

        var ranges = vm.BuildRanges(Now);

        Assert.Single(ranges);
        Assert.Equal(new DateTimeOffset(2024, 1, 10, 0, 0, 0, TimeSpan.Zero), ranges[0].Start);
        Assert.Equal(new DateTimeOffset(2024, 1, 13, 0, 0, 0, TimeSpan.Zero), ranges[0].End);
    }

    [Fact]
    public void AllMode_StartsAtBybitEpoch()
    {
        var vm = Vm();
        vm.PeriodMode = "All";

        var range = Assert.Single(vm.BuildRanges(Now));

        Assert.Equal(PeriodSelector.BybitEpoch, range.Start);
        Assert.Equal(Now, range.End);
    }

    [Fact]
    public void ExplicitSymbols_AreParsedAndDeduplicated()
    {
        var vm = Vm();
        vm.SymbolMode = "Specific symbols";
        vm.SymbolsText = "BTCUSDT, ETHUSDT, BTCUSDT\nSOLUSDT";

        var job = vm.BuildJob();

        Assert.False(job.ScreenUniverse);
        Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT"], job.Symbols);
    }

    [Fact]
    public void InverseOnly_WhenOnlyInverseChecked()
    {
        var vm = Vm();
        vm.IsLinear = false;
        Assert.Equal([MarketCategory.Inverse], vm.BuildJob().Categories);
    }

    [Fact]
    public void AllMarketTypes_AreOrderedSpotLinearInverse()
    {
        var vm = Vm();
        vm.IsSpot = true;
        Assert.Equal([MarketCategory.Spot, MarketCategory.Linear, MarketCategory.Inverse], vm.BuildJob().Categories);
    }

    [Fact]
    public void ExplicitMode_WithoutSymbols_Throws()
    {
        var vm = Vm();
        vm.SymbolMode = "Specific symbols";
        vm.SymbolsText = "   ";

        Assert.Throws<InvalidOperationException>(() => vm.BuildJob());
    }

    [Fact]
    public void InvalidMonth_Throws()
    {
        var vm = Vm();
        vm.Month = "2024-13";
        Assert.Throws<InvalidOperationException>(() => vm.BuildJob());
    }

    [Fact]
    public void InvalidWorkers_Throws()
    {
        var vm = Vm();
        vm.WorkersText = "0";
        Assert.Throws<InvalidOperationException>(() => vm.BuildJob());
    }

    [Fact]
    public void RangeMode_ToBeforeFrom_Throws()
    {
        var vm = Vm();
        vm.PeriodMode = "Range";
        vm.FromDate = "2024-02-01";
        vm.ToDate = "2024-01-01";
        Assert.Throws<InvalidOperationException>(() => vm.BuildJob());
    }

    [Fact]
    public async Task PreviewAsync_SetsSummaryFromPlan()
    {
        var fake = new FakeDownloadService();
        var vm = Vm(fake);

        await vm.PreviewAsync();

        Assert.Equal(1, fake.PlanCalls);
        Assert.Equal(0, fake.RunCalls);
        Assert.Contains("Symbols:", vm.PlanSummary);
        Assert.StartsWith("Plan ready", vm.Status);
    }

    [Fact]
    public async Task PreviewAsync_InvalidInput_SetsErrorStatus()
    {
        var vm = Vm();
        vm.Month = "not-a-month";

        await vm.PreviewAsync();

        Assert.StartsWith("Error:", vm.Status);
    }

    [Fact]
    public async Task StartAsync_StreamsRowsAndSetsSummary()
    {
        var fake = new FakeDownloadService();
        fake.RunOverride = (job, progress, _) =>
        {
            progress!.Report(new DownloadProgress(1, 2, 1440, FakeDownloadService.Outcome("a", ChunkStatus.Done)));
            progress.Report(new DownloadProgress(2, 2, 2880,
                FakeDownloadService.Outcome("b", ChunkStatus.Done, skipped: true)));
            return Task.FromResult(new DownloadJobResult(
                FakeDownloadService.BuildPlan(job), new DownloadSummary(2, 1, 1, 0, 2880, TimeSpan.FromSeconds(3))));
        };
        var vm = Vm(fake);

        await vm.StartAsync();

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("skipped", vm.Rows[0].Status);
        Assert.Equal(100, vm.ProgressValue);
        Assert.StartsWith("Done: 1 ok, 1 skipped", vm.Status);
        Assert.Equal(fake.LastOutputRoot, vm.OutputRoot);
        Assert.Equal(vm.DatabasePath, fake.LastDatabasePath);
    }

    [Fact]
    public async Task StartAsync_ReportsFailures()
    {
        var fake = new FakeDownloadService();
        fake.RunOverride = (job, progress, _) =>
        {
            progress!.Report(new DownloadProgress(1, 1, 0,
                FakeDownloadService.Outcome("a", ChunkStatus.Failed, error: "boom")));
            return Task.FromResult(new DownloadJobResult(
                FakeDownloadService.BuildPlan(job), new DownloadSummary(1, 0, 0, 1, 0, TimeSpan.Zero)));
        };
        var vm = Vm(fake);

        await vm.StartAsync();

        Assert.Equal("failed", vm.Rows[0].Status);
        Assert.Contains("1 failed", vm.Status);
    }

    [Fact]
    public async Task StartAsync_TogglesIsRunning()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeDownloadService();
        fake.RunOverride = async (job, _, _) =>
        {
            await gate.Task;
            return new DownloadJobResult(FakeDownloadService.BuildPlan(job),
                new DownloadSummary(0, 0, 0, 0, 0, TimeSpan.Zero));
        };
        var vm = Vm(fake);

        var run = vm.StartAsync();
        Assert.True(vm.IsRunning);
        Assert.False(vm.CanEdit);
        Assert.False(vm.StartCommand.CanExecute(null));

        gate.SetResult();
        await run;

        Assert.False(vm.IsRunning);
        Assert.True(vm.CanEdit);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_StopsTheRun()
    {
        var fake = new FakeDownloadService();
        fake.RunOverride = async (job, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new DownloadJobResult(FakeDownloadService.BuildPlan(job),
                new DownloadSummary(0, 0, 0, 0, 0, TimeSpan.Zero));
        };
        var vm = Vm(fake);

        var run = vm.StartAsync();
        Assert.True(vm.IsRunning);
        vm.Cancel();
        await run;

        Assert.Equal("Cancelled.", vm.Status);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void ApplyProgress_ComputesPercentAndMapsStatus()
    {
        var vm = Vm();

        vm.ApplyProgress(new DownloadProgress(1, 4, 1440, FakeDownloadService.Outcome("x", ChunkStatus.Done)));

        Assert.Equal(25, vm.ProgressValue);
        Assert.Equal("done", vm.Rows[0].Status);
        Assert.Equal("1,440", vm.Rows[0].RowsText);
        Assert.Contains("1,440 candles", vm.Status);
    }

    private static FileValidationReport Row(string cat, string sym, int y, int m,
        long rows, long expected, double coverage, FileValidationStatus status) =>
        new($"{cat}/{sym}/{y:D4}-{m:D2}", cat, sym, y, m, rows, expected, coverage, 0, 0,
            0, 0, 0, 0, 0, 0, 0, true, status, []);

    [Fact]
    public void DatabasePath_FollowsOutputRoot()
    {
        var vm = Vm();
        vm.OutputRoot = "D:/hist";
        Assert.Equal(Path.Combine("D:/hist", "manifest.db"), vm.DatabasePath);
    }

    [Fact]
    public async Task ValidateAsync_PopulatesReportRows()
    {
        var fake = new FakeDownloadService
        {
            ValidateResult = new ValidationSummary(
                2, 1, 1, 0, 500, 600, 100,
                [
                    Row("linear", "BTCUSDT", 2024, 1, 100, 100, 1.0, FileValidationStatus.Ok),
                    Row("linear", "ETHUSDT", 2024, 1, 400, 500, 0.8, FileValidationStatus.Warnings)
                ],
                [], [])
        };
        var vm = Vm(fake);
        vm.OutputRoot = "D:/hist";

        await vm.ValidateAsync();

        Assert.Equal(1, fake.ValidateCalls);
        Assert.Equal("D:/hist", fake.LastValidateRoot);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("warn", vm.Rows[0].Status);
        Assert.Equal("ok", vm.Rows[1].Status);
        Assert.Contains("1 ok, 1 warnings, 0 errors", vm.Status);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void Wizard_StartsOnExchangeStep()
    {
        var vm = Vm();
        Assert.True(vm.IsExchangeStep);
        Assert.False(vm.CanGoBack);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public void Next_AdvancesThroughSetupSteps()
    {
        var vm = Vm();
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsMarketStep);
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsPeriodStep);
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsOptionsStep);
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsSummaryStep);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public void Next_MarketStep_WithoutCategory_StaysAndReportsError()
    {
        var vm = Vm();
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsMarketStep);

        vm.IsLinear = false;
        vm.IsInverse = false;
        vm.NextCommand.Execute(null);

        Assert.True(vm.IsMarketStep);
        Assert.StartsWith("Error:", vm.Status);
    }

    [Fact]
    public void Back_ReturnsToPreviousStep()
    {
        var vm = Vm();
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsPeriodStep);
        vm.BackCommand.Execute(null);
        Assert.True(vm.IsMarketStep);
    }

    [Fact]
    public void SelectPeriodCommand_SetsPeriodMode()
    {
        var vm = Vm();
        vm.SelectPeriodCommand.Execute("Year");
        Assert.True(vm.IsYearPeriod);
        Assert.False(vm.IsMonthPeriod);
    }

    [Fact]
    public void SelectExchangeCommand_SetsExchangeMode()
    {
        var vm = Vm();
        vm.SelectExchangeCommand.Execute("binance");
        Assert.Equal("binance", vm.ExchangeMode);
        Assert.Equal("Binance", vm.SelectedExchangeName);

        vm.SelectExchangeCommand.Execute("not-an-exchange");
        Assert.Equal("binance", vm.ExchangeMode);
    }

    [Fact]
    public void WindowBackground_IsBlackUntilAnExchangeIsChosen_ThenUsesItsPalette()
    {
        var vm = Vm();
        Assert.Equal("#000000", vm.WindowBackgroundColor);

        // The window darkens to 20% of the brand accent.
        vm.SelectExchangeCommand.Execute("kraken");
        Assert.Equal("#160A31", vm.WindowBackgroundColor);

        vm.SelectExchangeCommand.Execute("bybit");
        Assert.Equal("#312100", vm.WindowBackgroundColor);

        // Re-picking the current exchange still re-applies its tint, and unknown slugs are ignored.
        vm.SelectExchangeCommand.Execute("bybit");
        Assert.Equal("#312100", vm.WindowBackgroundColor);

        vm.SelectExchangeCommand.Execute("nope");
        Assert.Equal("#312100", vm.WindowBackgroundColor);
    }

    [Fact]
    public void PrimaryCommand_AdvancesUntilReview_ThenStartsTheDownload()
    {
        var vm = Vm();

        // Steps 0..3: the footer primary button just advances and stays enabled.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(vm.PrimaryCommand.CanExecute(null));
            vm.PrimaryCommand.Execute(null);
        }

        // Review step: the button reads "Start download" and must be enabled, not greyed out.
        Assert.True(vm.IsSummaryStep);
        Assert.Equal("Start download", vm.NextLabel);
        Assert.True(vm.PrimaryCommand.CanExecute(null));

        vm.PrimaryCommand.Execute(null);
        Assert.True(vm.IsMonitorStep);
    }

    [Fact]
    public void StepText_MatchesTheWizardCopy()
    {
        var vm = Vm();
        Assert.Equal("Exchange", vm.StepTitle);
        Assert.Equal("Choose the exchange for this run.", vm.StepSubtitle);
        Assert.True(vm.ShowNext);
        Assert.False(vm.ShowBack);
        Assert.False(vm.ShowMonitorActions);
        Assert.Equal("Next", vm.NextLabel);

        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsSummaryStep);
        Assert.Equal("Review & start", vm.StepTitle);
        Assert.Equal("Start download", vm.NextLabel);
        Assert.True(vm.ShowBack);
    }

    [Fact]
    public void Next_WithInvalidInput_ReportsError_AndClearsItOnTheNextStep()
    {
        var vm = Vm();
        vm.NextCommand.Execute(null);   // -> Markets
        vm.IsSpot = vm.IsLinear = vm.IsInverse = false;

        vm.NextCommand.Execute(null);
        Assert.Contains("market type", vm.ErrorMessage);
        Assert.True(vm.IsMarketStep);

        vm.IsLinear = true;
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsPeriodStep);
        Assert.Equal("", vm.ErrorMessage);
    }

    [Fact]
    public void SummaryText_DescribesSelections()
    {
        var vm = Vm();
        vm.ExchangeMode = "binance";
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsMarketStep);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsSummaryStep);

        Assert.Contains("Binance", vm.SummaryText);
        Assert.Contains("linear, inverse", vm.SummaryText);
    }

    [Fact]
    public async Task Browse_UsesFolderPicker()
    {
        var vm = new MainWindowViewModel(new FakeDownloadService(), post: a => a(), clock: () => Now,
            folderPicker: () => Task.FromResult<string?>("D:/picked"));
        await ((AsyncRelayCommand)vm.BrowseCommand).ExecuteAsync();

        Assert.Equal("D:/picked", vm.OutputRoot);
    }

    [Fact]
    public void ApplyTelemetry_TracksRowsAndRetries()
    {
        var vm = Vm();

        vm.ApplyTelemetry(new TelemetryEvent(TelemetryKind.ChunkStarted, TelemetryLevel.Info, "start",
            ChunkId: "bybit/linear/BTCUSDT/202406", Symbol: "BTCUSDT", Category: MarketCategory.Linear));
        Assert.Equal("BTCUSDT", vm.CurrentSymbol);
        Assert.Equal("running", vm.Rows[0].Status);
        Assert.Equal("BTCUSDT", vm.Rows[0].Symbol);

        vm.ApplyTelemetry(new TelemetryEvent(TelemetryKind.Retry, TelemetryLevel.Warning, "retry",
            ChunkId: "bybit/linear/BTCUSDT/202406", Attempt: 2));
        Assert.Equal(1, vm.RetryCount);
        Assert.Equal(1, vm.Rows[0].Retries);

        vm.ApplyTelemetry(new TelemetryEvent(TelemetryKind.ChunkCompleted, TelemetryLevel.Info, "done",
            ChunkId: "bybit/linear/BTCUSDT/202406", Rows: 43200));
        Assert.Equal("done", vm.Rows[0].Status);
        Assert.Equal("43,200", vm.Rows[0].RowsText);
        Assert.NotEmpty(vm.Logs);
    }

    [Fact]
    public async Task StartAsync_NavigatesToMonitor()
    {
        var vm = Vm();
        await vm.StartAsync();
        Assert.True(vm.IsMonitorStep);
    }
}
