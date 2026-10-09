namespace BybitDownloader.Core.Resilience;

public sealed record RateLimiterOptions
{
    // CONSERVATIVE defaults. Verify Bybit's current limits at
    // https://bybit-exchange.github.io/docs/v5/rate-limit before raising MaxRatePerSecond.
    public double InitialRatePerSecond { get; init; } = 5;
    public double MinRatePerSecond { get; init; } = 0.5;
    public double MaxRatePerSecond { get; init; } = 20;
    public double IncreaseStep { get; init; } = 0.5;
    public int SuccessesBeforeIncrease { get; init; } = 50;
    public double BackoffFactor { get; init; } = 0.5;
    public double LowCapacityFraction { get; init; } = 0.2;
    public TimeSpan DefaultBlock { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        if (MinRatePerSecond <= 0) throw new ArgumentException("MinRatePerSecond must be > 0");
        if (InitialRatePerSecond < MinRatePerSecond || InitialRatePerSecond > MaxRatePerSecond)
            throw new ArgumentException("InitialRatePerSecond must lie within [Min, Max]");
        if (BackoffFactor is <= 0 or >= 1) throw new ArgumentException("BackoffFactor must be in (0,1)");
        if (SuccessesBeforeIncrease < 1) throw new ArgumentException("SuccessesBeforeIncrease must be >= 1");
    }
}

public sealed record RateLimiterStats(double CurrentRatePerSecond, long TotalRequests, TimeSpan TotalWait, int RateLimitEvents);

/// <summary>
/// ONE instance is shared by every API call in the app. Spaces requests (slot reservation), cuts the rate
/// multiplicatively on rate-limit signals, and recovers additively after sustained success (AIMD).
/// Also honours a global "blocked until" time set by 429/10006 or exhausted-header signals.
/// </summary>
public sealed class AdaptiveRateLimiter
{
    private const double HeaderPressureFactor = 0.8;

    private readonly RateLimiterOptions _o;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();

    private double _rate;
    private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private int _successStreak;
    private long _totalRequests;
    private TimeSpan _totalWait;
    private int _rateLimitEvents;

    public AdaptiveRateLimiter(RateLimiterOptions options,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        options.Validate();
        _o = options;
        _rate = options.InitialRatePerSecond;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    public RateLimiterStats Stats
    {
        get { lock (_gate) return new RateLimiterStats(_rate, _totalRequests, _totalWait, _rateLimitEvents); }
    }

    public async Task AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TimeSpan wait;
        lock (_gate)
        {
            var now = _now();
            var slot = now > _nextSlot ? now : _nextSlot;
            _nextSlot = slot + TimeSpan.FromSeconds(1.0 / _rate);
            wait = slot - now;
            _totalRequests++;
        }

        while (true)
        {
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, ct).ConfigureAwait(false);
                lock (_gate) _totalWait += wait;
            }
            // A rate-limit signal may have arrived while we were queued.
            lock (_gate) wait = _blockedUntil - _now();
            if (wait <= TimeSpan.Zero) return;
        }
    }

    public void ReportSuccess()
    {
        lock (_gate)
        {
            if (++_successStreak < _o.SuccessesBeforeIncrease) return;
            _successStreak = 0;
            _rate = Math.Min(_o.MaxRatePerSecond, _rate + _o.IncreaseStep);
        }
    }

    public void ReportRateLimited(TimeSpan? retryAfter)
    {
        lock (_gate)
        {
            _rateLimitEvents++;
            _successStreak = 0;
            _rate = Math.Max(_o.MinRatePerSecond, _rate * _o.BackoffFactor);
            BlockUntil(_now() + (retryAfter ?? _o.DefaultBlock));
        }
    }

    /// <summary>Feed Bybit's X-Bapi-Limit / X-Bapi-Limit-Status / X-Bapi-Limit-Reset-Timestamp values.</summary>
    public void ReportHeaders(int? limit, int? remaining, DateTimeOffset? resetAt)
    {
        if (remaining is null) return;
        lock (_gate)
        {
            if (remaining <= 0)
            {
                var until = resetAt is { } r && r > _now() ? r : _now() + TimeSpan.FromSeconds(1);
                BlockUntil(until);
                _successStreak = 0;
            }
            else if (limit is > 0 && (double)remaining.Value / limit.Value <= _o.LowCapacityFraction)
            {
                _rate = Math.Max(_o.MinRatePerSecond, _rate * HeaderPressureFactor);
                _successStreak = 0;
            }
        }
    }

    private void BlockUntil(DateTimeOffset t)
    {
        if (t > _blockedUntil) _blockedUntil = t;
    }
}
