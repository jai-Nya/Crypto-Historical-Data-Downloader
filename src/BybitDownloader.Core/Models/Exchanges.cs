namespace BybitDownloader.Core.Models;

/// <summary>Supported exchanges. One exchange is chosen per run.</summary>
public enum Exchange { Bybit, Binance, Okx, Kraken, KuCoin }

public static class ExchangeExtensions
{
    /// <summary>Stable, filesystem-safe slug used in storage paths and manifest ids.</summary>
    public static string ToSlug(this Exchange e) => e switch
    {
        Exchange.Bybit => "bybit",
        Exchange.Binance => "binance",
        Exchange.Okx => "okx",
        Exchange.Kraken => "kraken",
        Exchange.KuCoin => "kucoin",
        _ => throw new ArgumentOutOfRangeException(nameof(e))
    };

    public static string DisplayName(this Exchange e) => e switch
    {
        Exchange.Bybit => "Bybit",
        Exchange.Binance => "Binance",
        Exchange.Okx => "OKX",
        Exchange.Kraken => "Kraken",
        Exchange.KuCoin => "KuCoin",
        _ => throw new ArgumentOutOfRangeException(nameof(e))
    };

    public static bool TryParse(string? value, out Exchange exchange)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "bybit": exchange = Exchange.Bybit; return true;
            case "binance": exchange = Exchange.Binance; return true;
            case "okx": exchange = Exchange.Okx; return true;
            case "kraken": exchange = Exchange.Kraken; return true;
            case "kucoin": exchange = Exchange.KuCoin; return true;
            default: exchange = default; return false;
        }
    }

    public static bool TryFromSlug(string? slug, out Exchange exchange) => TryParse(slug, out exchange);
}
