namespace BybitDownloader.Core.Models;

public enum MarketCategory { Linear, Inverse }

public static class MarketCategoryExtensions
{
    public static string ToApiString(this MarketCategory c) => c switch
    {
        MarketCategory.Linear => "linear",
        MarketCategory.Inverse => "inverse",
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    };
}

/// <summary>One 1-minute candle. Timestamp = candle OPEN time, Unix ms UTC. Semantics follow Bybit's kline list.</summary>
public readonly record struct Candle(
    long TimestampMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal Turnover);

public sealed record Instrument(
    string Symbol,
    MarketCategory Category,
    string ContractType,
    string Status,
    string BaseCoin,
    string QuoteCoin,
    string SettleCoin,
    long? LaunchTimeMs,
    decimal? TickSize)
{
    public bool IsPerpetual => ContractType is "LinearPerpetual" or "InversePerpetual";
    public bool IsTrading => Status == "Trading";
}

public sealed record Ticker(
    string Symbol,
    decimal? LastPrice,
    decimal? HighPrice24h,
    decimal? LowPrice24h,
    decimal? Turnover24h,
    decimal? Volume24h,
    decimal? OpenInterest,
    decimal? OpenInterestValue,
    decimal? Bid1Price,
    decimal? Ask1Price);

public sealed record InstrumentsPage(IReadOnlyList<Instrument> Items, string? NextCursor);
