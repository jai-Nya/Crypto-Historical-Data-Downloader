using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
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
        Assert.Equal(4, job.Orchestrator.MaxParallelWorkers);
        Assert.Equal(ScreeningPresets.Balanced.Name, job.Screening.Name);
        Assert.Equal(Now, job.Now);
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
        vm.SymbolMode = "Explicit symbols";
        vm.SymbolsText = "BTCUSDT, ETHUSDT, BTCUSDT\nSOLUSDT";

        var job = vm.BuildJob();

        Assert.False(job.ScreenUniverse);
        Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT"], job.Symbols);
    }

    [Fact]
    public void CategoryMode_Inverse_SelectsInverseOnly()
    {
        var vm = Vm();
        vm.CategoryMode = "inverse";
        Assert.Equal([MarketCategory.Inverse], vm.BuildJob().Categories);
    }

    [Fact]
    public void ExplicitMode_WithoutSymbols_Throws()
    {
        var vm = Vm();
        vm.SymbolMode = "Explicit symbols";
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

    [Fact]
    public void DatabasePath_FollowsOutputRoot()
    {
        var vm = Vm();
        vm.OutputRoot = "D:/hist";
        Assert.Equal(Path.Combine("D:/hist", "manifest.db"), vm.DatabasePath);
    }

    private static FileValidationReport Row(string cat, string sym, int y, int m,
        long rows, long expected, double coverage, FileValidationStatus status) =>
        new($"{cat}/{sym}/{y:D4}-{m:D2}", cat, sym, y, m, rows, expected, coverage, 0, 0,
            0, 0, 0, 0, 0, 0, 0, true, status, []);

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
}
