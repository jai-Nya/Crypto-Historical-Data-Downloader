using BybitDownloader.Core.Api;
using BybitDownloader.Core.Resilience;

namespace BybitDownloader.Core.Tests;

public class RetryPolicyTests
{
    private static (RetryPolicy, List<TimeSpan>) Make(int attempts = 4, double baseSec = 1, double maxSec = 8, double totalSec = 60)
    {
        var delays = new List<TimeSpan>();
        var p = new RetryPolicy(
            new RetryOptions { MaxAttempts = attempts, BaseDelay = TimeSpan.FromSeconds(baseSec),
                               MaxDelay = TimeSpan.FromSeconds(maxSec), MaxTotalDelay = TimeSpan.FromSeconds(totalSec) },
            (ts, ct) => { ct.ThrowIfCancellationRequested(); delays.Add(ts); return Task.CompletedTask; },
            () => 1.0); // jitter=1 => delay equals the exponential ceiling
        return (p, delays);
    }

    [Fact]
    public async Task Succeeds_after_transient_failures()
    {
        var (p, delays) = Make();
        var calls = 0;
        var r = await p.ExecuteAsync(_ => ++calls < 3 ? throw new ApiException("t", true) : Task.FromResult(42), CancellationToken.None);
        Assert.Equal(42, r);
        Assert.Equal(3, calls);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Backoff_is_exponential_then_exhausts()
    {
        var (p, delays) = Make(attempts: 4);
        var ex = await Assert.ThrowsAsync<RetryExhaustedException>(
            () => p.ExecuteAsync<int>(_ => throw new ApiException("t", true), CancellationToken.None));
        Assert.Equal(4, ex.Attempts);
        Assert.Equal(new[] { 1.0, 2.0, 4.0 }, delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task Delay_is_capped_by_MaxDelay()
    {
        var (p, delays) = Make(attempts: 6, maxSec: 3);
        await Assert.ThrowsAsync<RetryExhaustedException>(
            () => p.ExecuteAsync<int>(_ => throw new ApiException("t", true), CancellationToken.None));
        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task MaxTotalDelay_stops_retrying_early()
    {
        var (p, delays) = Make(attempts: 10, totalSec: 2.5);
        var ex = await Assert.ThrowsAsync<RetryExhaustedException>(
            () => p.ExecuteAsync<int>(_ => throw new ApiException("t", true), CancellationToken.None));
        Assert.Equal(2, ex.Attempts);              // 1s ok, next 2s would reach 3s > 2.5s
        Assert.Single(delays);
    }

    [Fact]
    public async Task Permanent_errors_are_not_retried_and_propagate_unchanged()
    {
        var (p, delays) = Make();
        var calls = 0;
        await Assert.ThrowsAsync<BybitApiException>(() => p.ExecuteAsync<int>(_ =>
        {
            calls++;
            throw new BybitApiException(10001, "params error");
        }, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Server_RetryAfter_is_honoured_and_capped()
    {
        var (p, delays) = Make(attempts: 2);
        await Assert.ThrowsAsync<RetryExhaustedException>(
            () => p.ExecuteAsync<int>(_ => throw new ApiException("t", true, TimeSpan.FromSeconds(5)), CancellationToken.None));
        Assert.Equal(5, delays[0].TotalSeconds);

        var (p2, delays2) = Make(attempts: 2, maxSec: 8);
        await Assert.ThrowsAsync<RetryExhaustedException>(
            () => p2.ExecuteAsync<int>(_ => throw new ApiException("t", true, TimeSpan.FromSeconds(30)), CancellationToken.None));
        Assert.Equal(8, delays2[0].TotalSeconds);
    }

    [Fact]
    public async Task Network_errors_are_transient()
    {
        var (p, _) = Make();
        var calls = 0;
        var r = await p.ExecuteAsync(_ => ++calls == 1 ? throw new HttpRequestException("dns") : Task.FromResult(1), CancellationToken.None);
        Assert.Equal(1, r);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unexpected_exceptions_are_not_retried()
    {
        var (p, _) = Make();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.ExecuteAsync<int>(_ => { calls++; throw new InvalidOperationException(); }, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Pre_cancelled_token_never_calls_operation()
    {
        var (p, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.ExecuteAsync(_ => { calls++; return Task.FromResult(1); }, cts.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cancellation_during_backoff_propagates_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var p = new RetryPolicy(new RetryOptions(), (ts, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, () => 1.0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => p.ExecuteAsync<int>(_ => throw new ApiException("t", true), cts.Token));
    }

    [Fact]
    public async Task Timeout_style_cancellation_without_caller_cancel_is_retried()
    {
        var (p, _) = Make();
        var calls = 0;
        var r = await p.ExecuteAsync(_ => ++calls == 1 ? throw new TaskCanceledException("timeout") : Task.FromResult(7), CancellationToken.None);
        Assert.Equal(7, r);
    }
}

public class RateLimiterTests
{
    private static AdaptiveRateLimiter Make(VirtualClock clock, RateLimiterOptions o) => new(o, clock.Read, clock.DelayAsync);

    [Fact]
    public async Task Spaces_requests_by_interval()
    {
        var clock = new VirtualClock();
        var start = clock.Now;
        var lim = Make(clock, new RateLimiterOptions { InitialRatePerSecond = 4, MaxRatePerSecond = 4 });
        for (var i = 0; i < 3; i++) await lim.AcquireAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Now - start); // 0 + 250 + 250
    }

    [Fact]
    public async Task Concurrent_callers_share_one_schedule()
    {
        // Recording delay that does NOT advance time: reveals the slots reserved by concurrent callers.
        var waits = new List<TimeSpan>();
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lim = new AdaptiveRateLimiter(new RateLimiterOptions { InitialRatePerSecond = 4, MaxRatePerSecond = 4 },
            () => now, (ts, _) => { lock (waits) waits.Add(ts); return Task.CompletedTask; });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => lim.AcquireAsync(CancellationToken.None))));

        var sorted = waits.OrderBy(w => w).Select(w => w.TotalMilliseconds).ToArray();
        Assert.Equal(7, sorted.Length); // first caller has zero wait
        Assert.Equal(new double[] { 250, 500, 750, 1000, 1250, 1500, 1750 }, sorted);
        Assert.Equal(8, lim.Stats.TotalRequests);
    }

    [Fact]
    public async Task Rate_limit_signal_halves_rate_and_blocks()
    {
        var clock = new VirtualClock();
        var start = clock.Now;
        var lim = Make(clock, new RateLimiterOptions { InitialRatePerSecond = 8, MaxRatePerSecond = 8 });
        lim.ReportRateLimited(TimeSpan.FromSeconds(3));
        Assert.Equal(4, lim.Stats.CurrentRatePerSecond);
        Assert.Equal(1, lim.Stats.RateLimitEvents);
        await lim.AcquireAsync(CancellationToken.None);
        Assert.True(clock.Now - start >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Rate_never_drops_below_minimum()
    {
        var clock = new VirtualClock();
        var lim = Make(clock, new RateLimiterOptions { InitialRatePerSecond = 1, MinRatePerSecond = 0.5, MaxRatePerSecond = 2 });
        for (var i = 0; i < 10; i++) lim.ReportRateLimited(null);
        Assert.Equal(0.5, lim.Stats.CurrentRatePerSecond);
    }

    [Fact]
    public void Rate_recovers_cautiously_after_sustained_success_and_respects_max()
    {
        var clock = new VirtualClock();
        var lim = Make(clock, new RateLimiterOptions { InitialRatePerSecond = 2, MaxRatePerSecond = 3, IncreaseStep = 1, SuccessesBeforeIncrease = 3 });
        for (var i = 0; i < 2; i++) lim.ReportSuccess();
        Assert.Equal(2, lim.Stats.CurrentRatePerSecond);
        lim.ReportSuccess();
        Assert.Equal(3, lim.Stats.CurrentRatePerSecond);
        for (var i = 0; i < 6; i++) lim.ReportSuccess();
        Assert.Equal(3, lim.Stats.CurrentRatePerSecond);
    }

    [Fact]
    public void A_rate_limit_resets_the_success_streak()
    {
        var clock = new VirtualClock();
        var lim = Make(clock, new RateLimiterOptions { InitialRatePerSecond = 4, MaxRatePerSecond = 8, SuccessesBeforeIncrease = 3 });
        lim.ReportSuccess(); lim.ReportSuccess();
        lim.ReportRateLimited(TimeSpan.Zero);
        lim.ReportSuccess();
        Assert.Equal(2, lim.Stats.CurrentRatePerSecond); // 4*0.5, no increase yet
    }

    [Fact]
    public async Task Exhausted_header_capacity_blocks_until_reset()
    {
        var clock = new VirtualClock();
        var start = clock.Now;
        var lim = Make(clock, new RateLimiterOptions { InitialRatePerSecond = 8, MaxRatePerSecond = 8 });
        lim.ReportHeaders(100, 0, start + TimeSpan.FromSeconds(2));
        await lim.AcquireAsync(CancellationToken.None);
        Assert.True(clock.Now - start >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Low_header_capacity_slows_down_gently()
    {
        var clock = new VirtualClock();
        var lim = Make(clock, new RateLimiterOptions { InitialRatePerSecond = 10, MaxRatePerSecond = 10 });
        lim.ReportHeaders(100, 10, null);   // 10% left <= 20%
        Assert.Equal(8, lim.Stats.CurrentRatePerSecond, 6);
        lim.ReportHeaders(100, 90, null);   // healthy: unchanged
        Assert.Equal(8, lim.Stats.CurrentRatePerSecond, 6);
    }

    [Fact]
    public async Task Cancellation_is_honoured_while_waiting()
    {
        var lim = new AdaptiveRateLimiter(new RateLimiterOptions { InitialRatePerSecond = 1, MaxRatePerSecond = 1 });
        await lim.AcquireAsync(CancellationToken.None);          // consumes the free slot
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lim.AcquireAsync(cts.Token)); // would wait ~1s
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new AdaptiveRateLimiter(new RateLimiterOptions { MinRatePerSecond = 0 }));
        Assert.Throws<ArgumentException>(() => new AdaptiveRateLimiter(new RateLimiterOptions { InitialRatePerSecond = 100 }));
        Assert.Throws<ArgumentException>(() => new AdaptiveRateLimiter(new RateLimiterOptions { BackoffFactor = 1.5 }));
    }
}
