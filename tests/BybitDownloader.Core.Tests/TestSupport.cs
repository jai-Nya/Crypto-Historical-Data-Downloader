using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Tests;

/// <summary>Virtual clock: Delay advances time instantly, so no test ever really sleeps.</summary>
public sealed class VirtualClock
{
    private readonly object _l = new();
    public DateTimeOffset Now { get; private set; } = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public List<TimeSpan> Delays { get; } = new();

    public DateTimeOffset Read() { lock (_l) return Now; }

    public Task DelayAsync(TimeSpan ts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_l) { Now += ts; Delays.Add(ts); }
        return Task.CompletedTask;
    }
}

public static class Fx
{
    public const long B = 1_700_000_040_000; // multiple of 60_000

    public static Candle C(long ts, decimal o = 100, decimal h = 101, decimal l = 99, decimal c = 100, decimal v = 10, decimal t = 1000)
        => new(ts, o, h, l, c, v, t);

    public static string KlineJson(params long[] timestampsDescending)
    {
        var rows = string.Join(",", timestampsDescending.Select(ts => $"[\"{ts}\",\"100\",\"101\",\"99\",\"100.5\",\"10\",\"1005\"]"));
        return $$"""{"retCode":0,"retMsg":"OK","result":{"category":"linear","symbol":"BTCUSDT","list":[{{rows}}]},"retExtInfo":{},"time":1}""";
    }

    public static string Error(int code, string msg) =>
        $$"""{"retCode":{{code}},"retMsg":"{{msg}}","result":{},"retExtInfo":{},"time":1}""";

    public static Instrument Inst(string symbol = "BTCUSDT", string type = "LinearPerpetual", string status = "Trading",
        MarketCategory cat = MarketCategory.Linear) =>
        new(symbol, cat, type, status, "BTC", cat == MarketCategory.Linear ? "USDT" : "USD",
            cat == MarketCategory.Linear ? "USDT" : "BTC", 1_585_526_400_000, 0.1m);
}
