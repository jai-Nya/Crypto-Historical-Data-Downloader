using System.Globalization;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Resilience;

namespace BybitDownloader.Core.Api;

/// <summary>
/// Bybit public REST client. EVERY request goes through the shared limiter and retry policy (one per category).
/// The HttpClient is injected (single long-lived instance owned by the host).
/// </summary>
public sealed class BybitApiClient : ExchangeHttpClient
{
    public const string DefaultBaseUrl = "https://api.bybit.com";
    private const int MaxInstrumentPages = 50;

    public BybitApiClient(HttpClient http, AdaptiveRateLimiter limiter, RetryPolicy retry)
        : base(http, ExchangeProfiles.Get(Exchange.Bybit), (_, _) => limiter, (_, _) => retry)
    {
    }

    public BybitApiClient(HttpClient http, ExchangeProfile? profile = null)
        : base(http, profile ?? ExchangeProfiles.Get(Exchange.Bybit))
    {
    }

    public override async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct)
    {
        var all = new List<Instrument>();
        var seenCursors = new HashSet<string>();
        string? cursor = null;

        for (var page = 0; page < MaxInstrumentPages; page++)
        {
            var url = $"/v5/market/instruments-info?category={category.ToApiString()}&limit=1000"
                      + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var result = await SendAsync(category, url, json => BybitParser.ParseInstruments(json, category), ct).ConfigureAwait(false);
            all.AddRange(result.Items);

            if (string.IsNullOrEmpty(result.NextCursor)) return all;
            if (!seenCursors.Add(result.NextCursor))
                throw new ApiException("Instrument pagination cursor repeated; aborting", false);
            cursor = result.NextCursor;
        }
        throw new ApiException($"Instrument pagination exceeded {MaxInstrumentPages} pages", false);
    }

    public override Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct) =>
        SendAsync(category, $"/v5/market/tickers?category={category.ToApiString()}", BybitParser.ParseTickers, ct);

    public override Task<IReadOnlyList<Candle>> GetKlinesAsync(
        MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"/v5/market/kline?category={category.ToApiString()}&symbol={Uri.EscapeDataString(symbol)}&interval=1" +
            $"&start={startMs}&end={endMsInclusive}&limit={Profile.MaxCandlesPerRequest(category)}");
        return SendAsync(category, url, BybitParser.ParseKlines, ct);
    }

    protected override bool IsAccessDeniedStatus(int status) => status == 403;

    protected override TimeSpan AccessDeniedBackoff(MarketCategory category)
    {
        // Bybit uses 403 for an IP-level limit breach (temporary ban). Back off hard.
        _ = category;
        return TimeSpan.FromMinutes(10);
    }

    protected override void OnResponseHeaders(MarketCategory category, HttpResponseMessage r)
    {
        _ = category;
        var limit = HeaderInt(r, "X-Bapi-Limit");
        var remaining = HeaderInt(r, "X-Bapi-Limit-Status");
        DateTimeOffset? reset = null;
        if (r.Headers.TryGetValues("X-Bapi-Limit-Reset-Timestamp", out var v)
            && long.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            reset = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        LimiterFor(category).ReportHeaders(limit, remaining, reset);
    }
}