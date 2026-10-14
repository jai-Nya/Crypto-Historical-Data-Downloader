using System.Globalization;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>
    /// Kraken public REST client (spot REST https://api.kraken.com/0/public, futures https://futures.kraken.com).
    /// LIMITATION: the spot OHLC endpoint returns at most 720 rows from a 'since' pivot and cannot page backward,
    /// so deep spot history cannot be re-fetched through the public API; futures charts paginate forward via
    /// <c>more_candles</c> with a 2000-row cap per page, which this client walks.
    /// </summary>
    public sealed class KrakenClient : ExchangeHttpClient
    {
        public const string SpotBaseUrl = "https://api.kraken.com";
        public const string FuturesBaseUrl = "https://futures.kraken.com";
        private const int MaxChartPages = 3000;

        public KrakenClient(HttpClient http, ExchangeProfile? profile = null)
            : base(http, profile ?? ExchangeProfiles.Get(Exchange.Kraken))
        {
        }

    public override Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct) =>
        category == MarketCategory.Spot
            ? SendAsync(category, $"{SpotBaseUrl}/0/public/AssetPairs", KrakenParser.ParseSpotInstruments, ct)
            : SendAsync(category, $"{FuturesBaseUrl}/derivatives/api/v3/instruments", KrakenParser.ParseFuturesInstruments, ct);

    public override Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct) =>
        category == MarketCategory.Spot
            ? SendAsync(category, $"{SpotBaseUrl}/0/public/Ticker?pair=all", KrakenParser.ParseSpotTickers, ct)
            : SendAsync(category, $"{FuturesBaseUrl}/derivatives/api/v3/tickers", KrakenParser.ParseFuturesTickers, ct);

    public override async Task<IReadOnlyList<Candle>> GetKlinesAsync(
        MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct)
    {
        if (category == MarketCategory.Spot)
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{SpotBaseUrl}/0/public/OHLC?pair={Uri.EscapeDataString(symbol)}&interval=1&since={startMs / 1000L}");
            return await SendAsync(category, url, KrakenParser.ParseSpotKlines, ct).ConfigureAwait(false);
        }

        // Futures charts cap each page at 2000 ascending rows; walk forward while more_candles is set.
        // The endpoint can also drop/throttle rows under concurrent load without an error status, so a page that
        // stops short of the window end is re-fetched before giving up.
        var all = new List<Candle>();
        var from = startMs;
        var emptyPages = 0;
        for (var page = 0; page < MaxChartPages; page++)
        {
            var url2 = string.Create(CultureInfo.InvariantCulture,
                $"{FuturesBaseUrl}/api/charts/v1/trade/{Uri.EscapeDataString(symbol)}/1m?from={from / 1000L}&to={endMsInclusive / 1000L}");
            var (candles, more) = await SendAsync(category, url2, KrakenParser.ParseFuturesKlinesPage, ct).ConfigureAwait(false);
            all.AddRange(candles);

            if (candles.Count == 0)
            {
                if (++emptyPages > 5) break;  // genuinely nothing for this range
                continue;                     // throttled mid-burst: retry the same page
            }
            emptyPages = 0;

            var newest = candles[^1].TimestampMs;
            if (newest >= endMsInclusive) break;   // window fully covered
            if (!more) break;                      // API reports no further data in the window
            var next = newest + 60_000;
            if (next <= from) break;               // safety: no forward progress
            from = next;
        }

        return all.OrderBy(c => c.TimestampMs)
                  .DistinctBy(c => c.TimestampMs)
                  .ToList();
    }
}