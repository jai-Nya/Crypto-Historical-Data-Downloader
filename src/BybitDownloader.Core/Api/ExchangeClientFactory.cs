using BybitDownloader.Core.Models;
using BybitDownloader.Core.Telemetry;

namespace BybitDownloader.Core.Api;

/// <summary>Builds the right exchange adapter for an <see cref="Exchange"/>. Each client owns per-category
/// rate limiters and retry policies built from its <see cref="ExchangeProfiles"/> entry.</summary>
public static class ExchangeClientFactory
{
    public static IExchangeMarketData Create(Exchange exchange, HttpClient http, ITelemetry? telemetry = null)
    {
        IExchangeMarketData client = exchange switch
        {
            Exchange.Bybit => new BybitApiClient(http),
            Exchange.Binance => new BinanceClient(http),
            Exchange.Okx => new OkxClient(http),
            Exchange.Kraken => new KrakenClient(http),
            Exchange.KuCoin => new KucoinClient(http),
            _ => throw new ArgumentOutOfRangeException(nameof(exchange))
        };
        if (client is ExchangeHttpClient httpClient) httpClient.Telemetry = telemetry;
        return client;
    }
}