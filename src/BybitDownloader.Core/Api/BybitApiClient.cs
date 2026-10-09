using System.Globalization;
using System.Net;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Resilience;

namespace BybitDownloader.Core.Api;

public interface IBybitMarketData
{
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct);
    Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct);
    /// <summary>1-minute klines for [startMs, endMsInclusive]. Returned in API order (newest first); caller sorts.</summary>
    Task<IReadOnlyList<Candle>> GetKlinesAsync(MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct);
}

/// <summary>
/// Public REST client. EVERY request goes through the shared limiter and retry policy.
/// The HttpClient is injected (single long-lived instance owned by the host).
/// </summary>
public sealed class BybitApiClient : IBybitMarketData
{
    public const string DefaultBaseUrl = "https://api.bybit.com";
    private const int MaxInstrumentPages = 50;

    private readonly HttpClient _http;
    private readonly AdaptiveRateLimiter _limiter;
    private readonly RetryPolicy _retry;

    public BybitApiClient(HttpClient http, AdaptiveRateLimiter limiter, RetryPolicy retry)
    {
        _http = http;
        _limiter = limiter;
        _retry = retry;
        _http.BaseAddress ??= new Uri(DefaultBaseUrl);
    }

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct)
    {
        var all = new List<Instrument>();
        var seenCursors = new HashSet<string>();
        string? cursor = null;

        for (var page = 0; page < MaxInstrumentPages; page++)
        {
            var url = $"/v5/market/instruments-info?category={category.ToApiString()}&limit=1000"
                      + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var result = await SendAsync(url, json => BybitParser.ParseInstruments(json, category), ct).ConfigureAwait(false);
            all.AddRange(result.Items);

            if (string.IsNullOrEmpty(result.NextCursor)) return all;
            if (!seenCursors.Add(result.NextCursor))
                throw new ApiException("Instrument pagination cursor repeated; aborting", false);
            cursor = result.NextCursor;
        }
        throw new ApiException($"Instrument pagination exceeded {MaxInstrumentPages} pages", false);
    }

    public Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct) =>
        SendAsync($"/v5/market/tickers?category={category.ToApiString()}", BybitParser.ParseTickers, ct);

    public Task<IReadOnlyList<Candle>> GetKlinesAsync(
        MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"/v5/market/kline?category={category.ToApiString()}&symbol={Uri.EscapeDataString(symbol)}&interval=1&start={startMs}&end={endMsInclusive}&limit=1000");
        return SendAsync(url, BybitParser.ParseKlines, ct);
    }

    private Task<T> SendAsync<T>(string relativeUrl, Func<string, T> parse, CancellationToken ct) =>
        _retry.ExecuteAsync(async t =>
        {
            await _limiter.AcquireAsync(t).ConfigureAwait(false);
            using var response = await _http.GetAsync(relativeUrl, t).ConfigureAwait(false);
            ReportHeaders(response);

            var status = (int)response.StatusCode;
            if (status == 429)
            {
                var ra = ReadRetryAfter(response) ?? TimeSpan.FromSeconds(5);
                _limiter.ReportRateLimited(ra);
                throw new ApiException("HTTP 429 rate limited", true, ra);
            }
            if (status == 403)
            {
                // Bybit uses 403 for an IP-level limit breach (temporary ban). Back off hard.
                var ra = TimeSpan.FromMinutes(10);
                _limiter.ReportRateLimited(ra);
                throw new ApiException("HTTP 403 (IP rate limit / access denied)", true, ra);
            }
            if (status >= 500) throw new ApiException($"HTTP {status}", true);
            if (!response.IsSuccessStatusCode) throw new ApiException($"HTTP {status}", false);

            var body = await response.Content.ReadAsStringAsync(t).ConfigureAwait(false);
            T parsed;
            try
            {
                parsed = parse(body);
            }
            catch (BybitApiException ex) when (ex.IsRateLimit)
            {
                _limiter.ReportRateLimited(ex.RetryAfter);
                throw;
            }
            _limiter.ReportSuccess();
            return parsed;
        }, ct);

    private void ReportHeaders(HttpResponseMessage r)
    {
        var limit = HeaderInt(r, "X-Bapi-Limit");
        var remaining = HeaderInt(r, "X-Bapi-Limit-Status");
        DateTimeOffset? reset = null;
        if (r.Headers.TryGetValues("X-Bapi-Limit-Reset-Timestamp", out var v)
            && long.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            reset = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        _limiter.ReportHeaders(limit, remaining, reset);
    }

    private static int? HeaderInt(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v)
        && int.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage r)
    {
        var ra = r.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d) return d;
        if (ra.Date is { } dt) { var diff = dt - DateTimeOffset.UtcNow; return diff > TimeSpan.Zero ? diff : TimeSpan.Zero; }
        return null;
    }
}
