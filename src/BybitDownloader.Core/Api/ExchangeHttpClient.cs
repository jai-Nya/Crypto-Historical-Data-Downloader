using System.Globalization;
using System.Net;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Resilience;
using BybitDownloader.Core.Telemetry;

namespace BybitDownloader.Core.Api;

/// <summary>
/// The market-data surface every exchange adapter must satisfy. One instance is chosen per run and shared by
/// every request; the <c>GetKlinesAsync</c> window is [startMs, endMsInclusive] and rows come back in API order
/// (callers sort/dedupe).
/// </summary>
public interface IExchangeMarketData
{
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct);
    Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct);
    Task<IReadOnlyList<Candle>> GetKlinesAsync(MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct);
}

/// <summary>
/// Shared REST plumbing for one exchange: a per-category adaptive rate limiter and retry policy, with hooks
/// for each exchange's rate-limit signalling. EVERY request goes through the limiter and retry policy.
/// The HttpClient is injected (single long-lived instance owned by the host).
/// </summary>
public abstract class ExchangeHttpClient : IExchangeMarketData
{
    private readonly HttpClient _http;
    private readonly ExchangeProfile _profile;
    private readonly IReadOnlyDictionary<MarketCategory, AdaptiveRateLimiter> _limiters;
    private readonly IReadOnlyDictionary<MarketCategory, RetryPolicy> _retries;

    protected ExchangeHttpClient(HttpClient http, ExchangeProfile profile,
        Func<ExchangeProfile, MarketCategory, AdaptiveRateLimiter>? limiterFactory = null,
        Func<ExchangeProfile, MarketCategory, RetryPolicy>? retryFactory = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _http.BaseAddress = new Uri(profile.DefaultBaseUrl);

        var limiters = new Dictionary<MarketCategory, AdaptiveRateLimiter>();
        var retries = new Dictionary<MarketCategory, RetryPolicy>();
        foreach (var category in profile.SupportedCategories)
        {
            limiters[category] = limiterFactory is null
                ? new AdaptiveRateLimiter(profile.For(category).RateLimiter)
                : limiterFactory(profile, category);
            retries[category] = retryFactory is null
                ? new RetryPolicy(profile.For(category).Retry)
                : retryFactory(profile, category);
        }
        _limiters = limiters;
        _retries = retries;
    }

    public ExchangeProfile Profile => _profile;
    public AdaptiveRateLimiter LimiterFor(MarketCategory category) => _limiters[category];
    public RetryPolicy RetryFor(MarketCategory category) => _retries[category];

    /// <summary>Optional observer for retries (and other diagnostics). Set by the host after construction.</summary>
    public ITelemetry? Telemetry { get; set; }

    public abstract Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct);
    public abstract Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct);
    public abstract Task<IReadOnlyList<Candle>> GetKlinesAsync(MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct);

    /// <summary>
    /// Runs one HTTP GET under the category's limiter + retry policy. <c>url</c> may be absolute (multi-host
    /// exchanges) or relative to the HttpClient base address.
    /// </summary>
    protected Task<T> SendAsync<T>(MarketCategory category, string url, Func<string, T> parse, CancellationToken ct) =>
        _retries[category].ExecuteAsync(async t =>
        {
            await _limiters[category].AcquireAsync(t).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            OnRequest(request);
            using var response = await _http.SendAsync(request, t).ConfigureAwait(false);
            OnResponseHeaders(category, response);

            var status = (int)response.StatusCode;
            if (IsRateLimitStatus(status))
            {
                var retryAfter = ReadRetryAfter(response) ?? _profile.For(category).RateLimiter.DefaultBlock;
                _limiters[category].ReportRateLimited(retryAfter);
                throw new ApiException($"HTTP {status} rate limited", true, retryAfter);
            }
            if (IsAccessDeniedStatus(status))
            {
                var backoff = AccessDeniedBackoff(category);
                _limiters[category].ReportRateLimited(backoff);
                throw new ApiException($"HTTP {status} (access denied / IP rate limit)", true, backoff);
            }
            if (status >= 500) throw new ApiException($"HTTP {status}", true);
            if (!response.IsSuccessStatusCode) throw new ApiException($"HTTP {status}", false);

            var body = await response.Content.ReadAsStringAsync(t).ConfigureAwait(false);
            T parsed;
            try
            {
                parsed = parse(body);
            }
            catch (ApiException ex) when (ex.IsTransient)
            {
                // A rate-limit signalled inside a response body (OKX "50011", KuCoin 429000, Bybit 10006)
                // must drive the limiter's backoff, not just the retry delay.
                if (ex.IsRateLimit) _limiters[category].ReportRateLimited(ex.RetryAfter);
                throw;
            }
            _limiters[category].ReportSuccess();
            return parsed;
        }, ct, Telemetry is null ? null : OnRetry);

    private void OnRetry(int attempt, Exception error, TimeSpan delay) =>
        Telemetry!.Report(new TelemetryEvent(
            TelemetryKind.Retry,
            TelemetryLevel.Warning,
            $"Retry #{attempt} in {delay.TotalSeconds:0.0}s ({DownloadScope.Category?.ToApiString() ?? "request"}): {error.Message}",
            ChunkId: DownloadScope.ChunkId,
            Symbol: DownloadScope.Symbol,
            Category: DownloadScope.Category,
            Attempt: attempt));

    /// <summary>HTTP statuses that mean "slow down" for this exchange. Default: 429.</summary>
    protected virtual bool IsRateLimitStatus(int status) => status == 429;

    /// <summary>HTTP statuses that mean the IP was blocked. Default: none (403 is treated as permanent).</summary>
    protected virtual bool IsAccessDeniedStatus(int status) => false;

    protected virtual TimeSpan AccessDeniedBackoff(MarketCategory category) => _profile.For(category).RateLimiter.DefaultBlock;

    /// <summary>Hook for setting per-exchange request headers (e.g. Binance requires a User-Agent).</summary>
    protected virtual void OnRequest(HttpRequestMessage request) { }

    /// <summary>Hook for feeding per-exchange rate-limit headers (e.g. Bybit X-Bapi-*) into the limiter.</summary>
    protected virtual void OnResponseHeaders(MarketCategory category, HttpResponseMessage response) { }

    protected static int? HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var v)
        && int.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    protected static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d) return d;
        if (ra.Date is { } dt) { var diff = dt - DateTimeOffset.UtcNow; return diff > TimeSpan.Zero ? diff : TimeSpan.Zero; }
        return null;
    }
}