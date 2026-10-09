using BybitDownloader.Core.Api;

namespace BybitDownloader.Core.Resilience;

public sealed record RetryOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Upper bound on the SUM of all backoff delays for one operation.</summary>
    public TimeSpan MaxTotalDelay { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (MaxAttempts < 1) throw new ArgumentException("MaxAttempts must be >= 1");
        if (BaseDelay < TimeSpan.Zero || MaxDelay < BaseDelay) throw new ArgumentException("Invalid delay settings");
        if (MaxTotalDelay < TimeSpan.Zero) throw new ArgumentException("MaxTotalDelay must be >= 0");
    }
}

/// <summary>
/// Exponential backoff with "equal jitter" (delay in [d/2, d]). Honors server RetryAfter (capped at MaxDelay).
/// Permanent errors propagate unchanged; transient errors that exhaust the budget throw RetryExhaustedException.
/// </summary>
public sealed class RetryPolicy
{
    private readonly RetryOptions _o;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;

    public RetryPolicy(RetryOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? jitter = null)
    {
        options.Validate();
        _o = options;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        Action<int, Exception, TimeSpan>? onRetry = null)
    {
        var attempt = 0;
        var totalDelay = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex, out var retryAfter))
            {
                if (attempt >= _o.MaxAttempts) throw new RetryExhaustedException(attempt, ex);

                var delay = ComputeDelay(attempt, retryAfter);
                if (totalDelay + delay > _o.MaxTotalDelay) throw new RetryExhaustedException(attempt, ex);

                onRetry?.Invoke(attempt, ex, delay);
                totalDelay += delay;
                await _delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt, TimeSpan? retryAfter)
    {
        var ceilingMs = Math.Min(_o.MaxDelay.TotalMilliseconds,
            _o.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var jittered = ceilingMs / 2 + ceilingMs / 2 * Math.Clamp(_jitter(), 0, 1);
        var ms = jittered;
        if (retryAfter is { } ra) ms = Math.Max(ms, ra.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Min(ms, _o.MaxDelay.TotalMilliseconds));
    }

    private static bool IsTransient(Exception ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        switch (ex)
        {
            case ApiException api:
                retryAfter = api.RetryAfter;
                return api.IsTransient;
            case HttpRequestException:
            case TimeoutException:
            case TaskCanceledException: // HttpClient timeout (caller token NOT cancelled; filtered earlier)
                return true;
            default:
                return false;
        }
    }
}
