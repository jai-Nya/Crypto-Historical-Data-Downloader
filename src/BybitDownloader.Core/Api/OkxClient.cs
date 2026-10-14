using System.Globalization;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>
/// OKX public REST client (https://www.okx.com). Uses the general-purpose candles endpoints (history-candles for
/// older windows, candles for windows within the last 24h) because they return up to 300 rows per call.
/// OKX reports most errors with HTTP 200 and a body code, which the parser turns into transient/rate-limit errors.
/// </summary>
public sealed class OkxClient : ExchangeHttpClient
{
    public const string DefaultBaseUrl = "https://www.okx.com";

    public OkxClient(HttpClient http, ExchangeProfile? profile = null)
        : base(http, profile ?? ExchangeProfiles.Get(Exchange.Okx))
    {
    }

    public override Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(MarketCategory category, CancellationToken ct)
    {
        var instType = category == MarketCategory.Spot ? "SPOT" : "SWAP";
        var url = $"{DefaultBaseUrl}/api/v5/public/instruments?instType={instType}";
        return SendAsync(category, url, json => OkxParser.ParseInstruments(json, category), ct);
    }

    public override Task<IReadOnlyList<Ticker>> GetTickersAsync(MarketCategory category, CancellationToken ct)
    {
        var instType = category == MarketCategory.Spot ? "SPOT" : "SWAP";
        var url = $"{DefaultBaseUrl}/api/v5/market/tickers?instType={instType}";
        return SendAsync(category, url, json => OkxParser.ParseTickers(json, category), ct);
    }

    public override Task<IReadOnlyList<Candle>> GetKlinesAsync(
        MarketCategory category, string symbol, long startMs, long endMsInclusive, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var recent = startMs >= now - 24 * 60 * 60_000;
        var candles = Math.Max(1, (endMsInclusive - startMs) / 60_000 + 1);
        var limit = Math.Min(candles, Profile.MaxCandlesPerRequest(category));

        // OKX pagination is EXCLUSIVE: before=start-1 returns rows newer than it, after=end+1 returns rows older
        // than it, so together they select [start, end]. Newest rows come first.
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{(recent ? $"{DefaultBaseUrl}/api/v5/market/candles" : $"{DefaultBaseUrl}/api/v5/market/history-candles")}?" +
            $"instId={Uri.EscapeDataString(symbol)}&bar=1m&before={startMs - 1}&after={endMsInclusive + 1}&limit={limit}");
        return SendAsync(category, url, OkxParser.ParseKlines, ct);
    }
}