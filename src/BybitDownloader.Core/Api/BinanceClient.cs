using System.Globalization;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>
/// Binance public REST client for Spot (api.binance.com), USDⓈ-M (fapi.binance.com) and COIN-M (dapi.binance.com).
/// Binance enforces a per-minute weight budget, not a raw call rate; the profile's per-category limiter keeps
/// weight consumption safely inside budget (klines weight 5 at limit &lt;=1000 on the futures APIs).
/// </summary>
public sealed class BinanceClient : ExchangeHttpClient
{
    public const string SpotBaseUrl = "https://api.binance.com";
    public const string LinearBaseUrl = "https://fapi.binance.com";
    public const string InverseBaseUrl = "https://dapi.binance.com";

    public BinanceClient(HttpClient http, ExchangeProfile? profile = null)
        : base(http, profile ?? ExchangeProfiles.Get(Exchange.Binance))
    {
    }

    public override Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct) =>
        SendAsync(category, $"{Base(category)}/exchangeInfo", json => BinanceParser.ParseInstruments(json, category), ct);

    public override Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct) =>
        SendAsync(category, $"{Base(category)}/ticker/24hr", BinanceParser.ParseTickers, ct);

    public override Task<IReadOnlyList<Candle>> GetKlinesAsync(
        MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct)
    {
        var limit = Profile.MaxCandlesPerRequest(category);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{Base(category)}/klines?symbol={Uri.EscapeDataString(symbol)}&interval=1m" +
            $"&startTime={startMs}&endTime={endMsInclusive}&limit={limit}");
        return SendAsync(category, url, BinanceParser.ParseKlines, ct);
    }

    protected override bool IsRateLimitStatus(int status) => status is 418 or 429;

    protected override void OnRequest(HttpRequestMessage request)
    {
        // Binance rejects requests without a User-Agent on some endpoints.
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.TryParseAdd("bybit-downloader/0.1");
    }

    private static string Base(MarketCategory category) => category switch
    {
        MarketCategory.Spot => $"{SpotBaseUrl}/api/v3",
        MarketCategory.Linear => $"{LinearBaseUrl}/fapi/v1",
        MarketCategory.Inverse => $"{InverseBaseUrl}/dapi/v1",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}