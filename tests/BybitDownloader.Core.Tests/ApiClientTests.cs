using System.Net;
using System.Net.Http.Headers;
using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Resilience;

namespace BybitDownloader.Core.Tests;

public class ApiClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request, Urls.Count));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body) };

    private static (BybitApiClient client, FakeHandler handler, AdaptiveRateLimiter limiter, VirtualClock clock) Build(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond, int maxAttempts = 4)
    {
        var clock = new VirtualClock();
        var limiter = new AdaptiveRateLimiter(
            new RateLimiterOptions { InitialRatePerSecond = 1000, MaxRatePerSecond = 1000, MinRatePerSecond = 1 },
            clock.Read, clock.DelayAsync);
        var retry = new RetryPolicy(new RetryOptions { MaxAttempts = maxAttempts, MaxDelay = TimeSpan.FromSeconds(60) },
            clock.DelayAsync, () => 1.0);
        var handler = new FakeHandler(respond);
        var client = new BybitApiClient(new HttpClient(handler), limiter, retry);
        return (client, handler, limiter, clock);
    }

    private static string InstrumentsPage(string symbol, string? cursor) =>
        $$$"""{"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[{"symbol":"{{{symbol}}}","contractType":"LinearPerpetual","status":"Trading","baseCoin":"A","quoteCoin":"USDT","settleCoin":"USDT","launchTime":"1585526400000","priceFilter":{"tickSize":"0.1"}}],"nextPageCursor":"{{{cursor}}}"},"time":1}""";

    [Fact]
    public async Task Instruments_follow_cursor_until_empty()
    {
        var (client, handler, _, _) = Build((req, n) => n == 1 ? Json(InstrumentsPage("AAAUSDT", "c%3D2")) : Json(InstrumentsPage("BBBUSDT", "")));
        var all = await client.GetInstrumentsAsync(MarketCategory.Linear, CancellationToken.None);
        Assert.Equal(new[] { "AAAUSDT", "BBBUSDT" }, all.Select(i => i.Symbol).ToArray());
        Assert.Equal(2, handler.Urls.Count);
        Assert.DoesNotContain("cursor=", handler.Urls[0]);
        Assert.Contains("cursor=c%253D2", handler.Urls[1]); // cursor is URL-escaped exactly once more
        Assert.Contains("category=linear", handler.Urls[0]);
    }

    [Fact]
    public async Task Repeated_cursor_aborts_instead_of_looping()
    {
        var (client, _, _, _) = Build((_, _) => Json(InstrumentsPage("AAAUSDT", "same")));
        await Assert.ThrowsAsync<ApiException>(() => client.GetInstrumentsAsync(MarketCategory.Linear, CancellationToken.None));
    }

    [Fact]
    public async Task Kline_request_has_expected_query()
    {
        var (client, handler, _, _) = Build((_, _) => Json(Fx.KlineJson(Fx.B + 60_000, Fx.B)));
        var rows = await client.GetKlinesAsync(MarketCategory.Inverse, "BTCUSD", Fx.B, Fx.B + 60_000, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        var url = handler.Urls.Single();
        Assert.Contains("category=inverse", url);
        Assert.Contains("symbol=BTCUSD", url);
        Assert.Contains("interval=1", url);
        Assert.Contains($"start={Fx.B}", url);
        Assert.Contains($"end={Fx.B + 60_000}", url);
        Assert.Contains("limit=1000", url);
    }

    [Fact]
    public async Task Retcode_10006_is_retried_and_lowers_rate()
    {
        var (client, handler, limiter, _) = Build((_, n) => n == 1 ? Json(Fx.Error(10006, "Too many visits!")) : Json(Fx.KlineJson(Fx.B)));
        var rows = await client.GetKlinesAsync(MarketCategory.Linear, "BTCUSDT", Fx.B, Fx.B, CancellationToken.None);
        Assert.Single(rows);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Equal(1, limiter.Stats.RateLimitEvents);
        Assert.Equal(500, limiter.Stats.CurrentRatePerSecond);
    }

    [Fact]
    public async Task Http_429_honours_Retry_After_header()
    {
        var (client, _, limiter, clock) = Build((_, n) =>
        {
            if (n > 1) return Json(Fx.KlineJson(Fx.B));
            var r = Json("", HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        });
        var start = clock.Now;
        await client.GetKlinesAsync(MarketCategory.Linear, "BTCUSDT", Fx.B, Fx.B, CancellationToken.None);
        Assert.True(clock.Now - start >= TimeSpan.FromSeconds(7));
        Assert.Equal(1, limiter.Stats.RateLimitEvents);
    }

    [Fact]
    public async Task Http_5xx_is_retried_then_exhausts()
    {
        var (client, handler, _, _) = Build((_, _) => Json("oops", HttpStatusCode.BadGateway), maxAttempts: 3);
        var ex = await Assert.ThrowsAsync<RetryExhaustedException>(
            () => client.GetTickersAsync(MarketCategory.Linear, CancellationToken.None));
        Assert.Equal(3, ex.Attempts);
        Assert.Equal(3, handler.Urls.Count);
    }

    [Fact]
    public async Task Invalid_parameter_error_is_permanent_single_attempt()
    {
        var (client, handler, _, _) = Build((_, _) => Json(Fx.Error(10001, "params error: symbol invalid")));
        var ex = await Assert.ThrowsAsync<BybitApiException>(
            () => client.GetKlinesAsync(MarketCategory.Linear, "NOPE", Fx.B, Fx.B, CancellationToken.None));
        Assert.Equal(10001, ex.RetCode);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Http_400_is_permanent()
    {
        var (client, handler, _, _) = Build((_, _) => Json("bad", HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<ApiException>(() => client.GetTickersAsync(MarketCategory.Linear, CancellationToken.None));
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Exhausted_rate_limit_headers_are_fed_to_limiter()
    {
        var reset = new DateTimeOffset(2024, 1, 1, 0, 0, 3, TimeSpan.Zero).ToUnixTimeMilliseconds(); // VirtualClock starts 2024-01-01T00:00:00Z
        var (client, _, limiter, clock) = Build((_, _) =>
        {
            var r = Json(Fx.KlineJson(Fx.B));
            r.Headers.Add("X-Bapi-Limit", "100");
            r.Headers.Add("X-Bapi-Limit-Status", "0");
            r.Headers.Add("X-Bapi-Limit-Reset-Timestamp", reset.ToString());
            return r;
        });
        var start = clock.Now;
        await client.GetKlinesAsync(MarketCategory.Linear, "BTCUSDT", Fx.B, Fx.B, CancellationToken.None);
        await limiter.AcquireAsync(CancellationToken.None); // must wait until the advertised reset
        Assert.True(clock.Now - start >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Pre_cancelled_request_makes_no_http_call()
    {
        var (client, handler, _, _) = Build((_, _) => Json(Fx.KlineJson()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetKlinesAsync(MarketCategory.Linear, "BTCUSDT", Fx.B, Fx.B, cts.Token));
        Assert.Empty(handler.Urls);
    }
}
