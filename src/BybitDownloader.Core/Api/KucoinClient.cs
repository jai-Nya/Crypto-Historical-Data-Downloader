using System.Globalization;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>
/// KuCoin public REST client (spot https://api.kucoin.com, futures https://api-futures.kucoin.com).
/// LIMITATION: the futures 1-minute kline endpoint only serves data from 2025-01-01 onwards; windows before that
/// return nothing. Rate limits are signalled by HTTP 429 with body code 429000 (handled via the parser).
/// </summary>
public sealed class KucoinClient : ExchangeHttpClient
{
    public const string SpotBaseUrl = "https://api.kucoin.com";
    public const string FuturesBaseUrl = "https://api-futures.kucoin.com";
    public const int MaxChartPages = 3000;

    public KucoinClient(HttpClient http, ExchangeProfile? profile = null)
        : base(http, profile ?? ExchangeProfiles.Get(Exchange.KuCoin))
    {
    }

    public override Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct) =>
        category == MarketCategory.Spot
            ? SendAsync(category, $"{SpotBaseUrl}/api/v1/symbols", KucoinParser.ParseSpotInstruments, ct)
            : SendAsync(category, $"{FuturesBaseUrl}/api/v1/contracts/active", KucoinParser.ParseFuturesInstruments, ct);

    public override Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct) =>
        category == MarketCategory.Spot
            ? SendAsync(category, $"{SpotBaseUrl}/api/v1/market/allTickers", KucoinParser.ParseSpotTickers, ct)
            : SendAsync(category, $"{FuturesBaseUrl}/api/v1/allTickers", KucoinParser.ParseFuturesTickers, ct);

    public override async Task<IReadOnlyList<Candle>> GetKlinesAsync(
        MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct)
    {
        if (category == MarketCategory.Spot)
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{SpotBaseUrl}/api/v1/market/candles?symbol={Uri.EscapeDataString(symbol)}&type=1min" +
                $"&startAt={startMs / 1000L}&endAt={endMsInclusive / 1000L}");
            return await SendAsync(category, url, KucoinParser.ParseSpotKlines, ct).ConfigureAwait(false);
        }

        // Futures klines are oldest-first and the endpoint caps each response at 200 candles regardless of the
        // requested `to`, so walk forward page-by-page until the window end is covered.
        var all = new List<Candle>();
        var from = startMs;
        for (var page = 0; page < MaxChartPages; page++)
        {
            var url2 = string.Create(CultureInfo.InvariantCulture,
                $"{FuturesBaseUrl}/api/v1/kline/query?symbol={Uri.EscapeDataString(symbol)}&granularity=1" +
                $"&from={from}&to={endMsInclusive}");
            var candles = await SendAsync(category, url2, KucoinParser.ParseFuturesKlines, ct).ConfigureAwait(false);
            all.AddRange(candles);

            if (candles.Count == 0) break;          // window start is before the exchange's data availability
            var newest = candles[^1].TimestampMs;
            if (newest >= endMsInclusive) break;    // window fully covered
            var next = newest + 60_000;
            if (next <= from) break;                // safety: no forward progress
            from = next;
        }

        return all.OrderBy(c => c.TimestampMs)
                  .DistinctBy(c => c.TimestampMs)
                  .ToList();
    }
}