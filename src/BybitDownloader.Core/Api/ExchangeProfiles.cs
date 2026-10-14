using BybitDownloader.Core.Models;
using BybitDownloader.Core.Resilience;

namespace BybitDownloader.Core.Api;

/// <summary>
/// Per-category tuning for one exchange. <see cref="MaxCandlesPerRequest"/> sizes the planner's request windows;
/// the limiter/retry options keep traffic inside the exchange's documented public budget with headroom so we
/// never trip a hard ban. <see cref="SupportsOpenInterest"/> is false when the bulk ticker endpoint does not
/// carry open interest (screening then skips the OI filter instead of rejecting everything).
/// </summary>
public sealed record ExchangeCategoryProfile(
    int MaxCandlesPerRequest,
    RateLimiterOptions RateLimiter,
    RetryOptions Retry,
    bool SupportsOpenInterest);

/// <summary>Static description of one exchange: its categories and their tuned request settings.</summary>
public sealed record ExchangeProfile(
    Exchange Exchange,
    string DefaultBaseUrl,
    IReadOnlyDictionary<MarketCategory, ExchangeCategoryProfile> Categories)
{
    public string DisplayName => Exchange.DisplayName();

    public IReadOnlyList<MarketCategory> SupportedCategories => Categories.Keys.OrderBy(c => c).ToList();

    public bool Supports(MarketCategory category) => Categories.ContainsKey(category);

    public ExchangeCategoryProfile For(MarketCategory category) =>
        Categories.TryGetValue(category, out var p)
            ? p
            : throw new NotSupportedException($"{DisplayName} does not support the {category} category.");

    public int MaxCandlesPerRequest(MarketCategory category) => For(category).MaxCandlesPerRequest;

    public bool SupportsOpenInterest(MarketCategory category) => For(category).SupportsOpenInterest;
}

/// <summary>Registry of the five supported exchanges. All settings are pure data (no I/O).</summary>
public static class ExchangeProfiles
{
    // The private profile fields are declared before the map below so that static field initialization order
    // (textual) guarantees Bybit..KuCoin are non-null when Build() runs.

    // Bybit: one IP budget of 600 req / 5s for all public /v5/market/* endpoints (no per-endpoint limit).
    // Keep Max at ~50% (60/s) because exceeding it triggers a hard 403 with a ~10-minute ban.
    private static readonly RateLimiterOptions BybitLimiter = new()
    {
        InitialRatePerSecond = 20, MinRatePerSecond = 0.5, MaxRatePerSecond = 60,
        IncreaseStep = 5, SuccessesBeforeIncrease = 10, DefaultBlock = TimeSpan.FromSeconds(2)
    };
    private static readonly ExchangeProfile BybitProfile = new(
        Exchange.Bybit, "https://api.bybit.com",
        new Dictionary<MarketCategory, ExchangeCategoryProfile>
        {
            [MarketCategory.Spot] = new(1000, BybitLimiter, new RetryOptions(), true),
            [MarketCategory.Linear] = new(1000, BybitLimiter, new RetryOptions(), true),
            [MarketCategory.Inverse] = new(1000, BybitLimiter, new RetryOptions(), true),
        });

    // Binance spot: /api/v3 weight budget is generous (klines weight 2, 6000 weight/min) -> higher rate.
    // Binance USDⓈ-M/COIN-M: 2400 weight/min and klines weight 5 (limit<=1000) -> much lower rate.
    private static readonly RateLimiterOptions BinanceSpotLimiter = new()
    {
        InitialRatePerSecond = 10, MinRatePerSecond = 0.5, MaxRatePerSecond = 30,
        IncreaseStep = 3, SuccessesBeforeIncrease = 10, DefaultBlock = TimeSpan.FromSeconds(5)
    };
    private static readonly RateLimiterOptions BinanceFuturesLimiter = new()
    {
        InitialRatePerSecond = 2, MinRatePerSecond = 0.25, MaxRatePerSecond = 6,
        IncreaseStep = 1, SuccessesBeforeIncrease = 15, DefaultBlock = TimeSpan.FromSeconds(10)
    };
    private static readonly ExchangeProfile BinanceProfile = new(
        Exchange.Binance, "https://api.binance.com",
        new Dictionary<MarketCategory, ExchangeCategoryProfile>
        {
            [MarketCategory.Spot] = new(1000, BinanceSpotLimiter, new RetryOptions(), false),
            [MarketCategory.Linear] = new(1000, BinanceFuturesLimiter, new RetryOptions(), false),
            [MarketCategory.Inverse] = new(1000, BinanceFuturesLimiter, new RetryOptions(), false),
        });

    // OKX: history-candles is 20 req / 2s (10/s), recent candles 40 req / 2s. Max limit 300 rows.
    private static readonly RateLimiterOptions OkxLimiter = new()
    {
        InitialRatePerSecond = 4, MinRatePerSecond = 0.25, MaxRatePerSecond = 8,
        IncreaseStep = 1, SuccessesBeforeIncrease = 12, DefaultBlock = TimeSpan.FromSeconds(2)
    };
    private static readonly ExchangeProfile OkxProfile = new(
        Exchange.Okx, "https://www.okx.com",
        new Dictionary<MarketCategory, ExchangeCategoryProfile>
        {
            [MarketCategory.Spot] = new(300, OkxLimiter, new RetryOptions(), false),
            [MarketCategory.Linear] = new(300, OkxLimiter, new RetryOptions(), false),
            [MarketCategory.Inverse] = new(300, OkxLimiter, new RetryOptions(), false),
        });

    // Kraken spot OHLC returns at most 720 rows and cannot page backward; the public API is deliberately
    // rate limited (keep it well under 1 req/s). Kraken Futures charts allow slightly more.
    private static readonly RateLimiterOptions KrakenSpotLimiter = new()
    {
        InitialRatePerSecond = 1, MinRatePerSecond = 0.2, MaxRatePerSecond = 2,
        IncreaseStep = 0.25, SuccessesBeforeIncrease = 20, DefaultBlock = TimeSpan.FromSeconds(5)
    };
    private static readonly RateLimiterOptions KrakenFuturesLimiter = new()
    {
        InitialRatePerSecond = 2, MinRatePerSecond = 0.25, MaxRatePerSecond = 5,
        IncreaseStep = 0.5, SuccessesBeforeIncrease = 15, DefaultBlock = TimeSpan.FromSeconds(5)
    };
    private static readonly ExchangeProfile KrakenProfile = new(
        Exchange.Kraken, "https://api.kraken.com",
        new Dictionary<MarketCategory, ExchangeCategoryProfile>
        {
            [MarketCategory.Spot] = new(720, KrakenSpotLimiter, new RetryOptions(), false),
            [MarketCategory.Linear] = new(720, KrakenFuturesLimiter, new RetryOptions(), false),
            [MarketCategory.Inverse] = new(720, KrakenFuturesLimiter, new RetryOptions(), false),
        });

    // KuCoin spot candles return up to 1500 rows; futures klines up to 500.
    private static readonly RateLimiterOptions KucoinSpotLimiter = new()
    {
        InitialRatePerSecond = 5, MinRatePerSecond = 0.5, MaxRatePerSecond = 15,
        IncreaseStep = 2, SuccessesBeforeIncrease = 12, DefaultBlock = TimeSpan.FromSeconds(10)
    };
    private static readonly RateLimiterOptions KucoinFuturesLimiter = new()
    {
        InitialRatePerSecond = 3, MinRatePerSecond = 0.25, MaxRatePerSecond = 8,
        IncreaseStep = 1, SuccessesBeforeIncrease = 12, DefaultBlock = TimeSpan.FromSeconds(10)
    };
    private static readonly ExchangeProfile KucoinProfile = new(
        Exchange.KuCoin, "https://api.kucoin.com",
        new Dictionary<MarketCategory, ExchangeCategoryProfile>
        {
            [MarketCategory.Spot] = new(1500, KucoinSpotLimiter, new RetryOptions(), false),
            [MarketCategory.Linear] = new(500, KucoinFuturesLimiter, new RetryOptions(), false),
            [MarketCategory.Inverse] = new(500, KucoinFuturesLimiter, new RetryOptions(), false),
        });

    public static IReadOnlyList<ExchangeProfile> All { get; } = [Bybit, Binance, Okx, Kraken, KuCoin];

    private static readonly Dictionary<Exchange, ExchangeProfile> Map = Build();

    private static Dictionary<Exchange, ExchangeProfile> Build() => new()
    {
        [Exchange.Bybit] = BybitProfile,
        [Exchange.Binance] = BinanceProfile,
        [Exchange.Okx] = OkxProfile,
        [Exchange.Kraken] = KrakenProfile,
        [Exchange.KuCoin] = KucoinProfile,
    };

    public static ExchangeProfile Get(Exchange exchange) => Map[exchange];

    public static ExchangeProfile Bybit => BybitProfile;
    public static ExchangeProfile Binance => BinanceProfile;
    public static ExchangeProfile Okx => OkxProfile;
    public static ExchangeProfile Kraken => KrakenProfile;
    public static ExchangeProfile KuCoin => KucoinProfile;
}