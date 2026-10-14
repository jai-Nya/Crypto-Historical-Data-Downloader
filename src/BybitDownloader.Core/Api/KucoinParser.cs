using System.Collections.Generic;
using System.Text.Json;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>Pure JSON -> typed model parsing for KuCoin (spot + futures). No I/O. Culture-invariant.</summary>
public static class KucoinParser
{
    /// <summary>KuCoin answers with {"code":"200000",...}; body rate-limit code is "429000".</summary>
    public static JsonElement RequireOk(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
        {
            var code = c.GetString();
            if (code != "200000")
            {
                var msg = JsonUtil.Str(root, "msg") ?? "";
                var ratelimited = code == "429000";
                throw new ApiException($"KuCoin error {code}: {msg}", ratelimited, isRateLimit: ratelimited);
            }
        }
        if (!root.TryGetProperty("data", out var data))
            throw new ApiException("KuCoin response missing 'data'", false);
        return data;
    }

    public static IReadOnlyList<Instrument> ParseSpotInstruments(string json)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Instrument>(data.GetArrayLength());
        foreach (var e in data.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;
            var trading = e.TryGetProperty("enableTrading", out var t) && t.ValueKind == JsonValueKind.True;
            var quote = JsonUtil.Str(e, "quoteCurrency") ?? "";
            result.Add(new Instrument(symbol, MarketCategory.Spot, "Spot", trading ? "Trading" : "Disabled",
                JsonUtil.Str(e, "baseCurrency") ?? "", quote, quote,
                null, JsonUtil.DecProp(e, "quoteIncrement"), "", ""));
        }
        return result;
    }

    public static IReadOnlyList<Instrument> ParseFuturesInstruments(string json)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Instrument>(data.GetArrayLength());
        foreach (var e in data.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;
            var quote = JsonUtil.Str(e, "quoteCurrency") ?? "";
            MarketCategory category;
            string contractType;
            if (quote.Equals("USDT", System.StringComparison.OrdinalIgnoreCase)) { category = MarketCategory.Linear; contractType = "LinearPerpetual"; }
            else { category = MarketCategory.Inverse; contractType = "InversePerpetual"; }

            var status = JsonUtil.Str(e, "status") ?? "";
            result.Add(new Instrument(symbol, category, contractType,
                status.Equals("Open", System.StringComparison.OrdinalIgnoreCase) ? "Trading" : status,
                JsonUtil.Str(e, "baseCurrency") ?? "", quote,
                JsonUtil.Str(e, "settleCurrency") ?? quote,
                JsonUtil.LongProp(e, "firstOpenDate"), JsonUtil.DecProp(e, "tickSize"), "", ""));
        }
        return result;
    }

    public static IReadOnlyList<Ticker> ParseSpotTickers(string json)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("ticker", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Ticker>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;
            result.Add(new Ticker(symbol,
                JsonUtil.DecProp(e, "last"), JsonUtil.DecProp(e, "high"), JsonUtil.DecProp(e, "low"),
                JsonUtil.DecProp(e, "volValue"), JsonUtil.DecProp(e, "vol"),
                null, null,
                JsonUtil.DecProp(e, "buy"), JsonUtil.DecProp(e, "sell")));
        }
        return result;
    }

    public static IReadOnlyList<Ticker> ParseFuturesTickers(string json)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Ticker>(data.GetArrayLength());
        foreach (var e in data.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;
            result.Add(new Ticker(symbol,
                JsonUtil.DecProp(e, "lastTradingPrice") ?? JsonUtil.DecProp(e, "lastPrice"),
                JsonUtil.DecProp(e, "highPrice"), JsonUtil.DecProp(e, "lowPrice"),
                JsonUtil.DecProp(e, "turnover"), JsonUtil.DecProp(e, "volume"),
                JsonUtil.DecProp(e, "openInterest"), null,
                JsonUtil.DecProp(e, "buy"), JsonUtil.DecProp(e, "sell")));
        }
        return result;
    }

    /// <summary>Spot klines: [time(sec), open, close, high, low, volume, turnover], newest first.</summary>
    public static IReadOnlyList<Candle> ParseSpotKlines(string json)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Candle>(data.GetArrayLength());
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7)
                throw new ApiException("Malformed KuCoin spot kline row", false);
            var t = JsonUtil.Lng(row[0]);
            var o = JsonUtil.Dec(row[1]); var c = JsonUtil.Dec(row[2]);
            var h = JsonUtil.Dec(row[3]); var l = JsonUtil.Dec(row[4]);
            var v = JsonUtil.Dec(row[5]); var tv = JsonUtil.Dec(row[6]);
            if (t is null || o is null || h is null || l is null || c is null || v is null)
                throw new ApiException("KuCoin spot kline row contains unparseable value", false);
            result.Add(new Candle(t.Value * 1000L, o.Value, h.Value, l.Value, c.Value, v.Value, tv ?? 0m));
        }
        return result;
    }

    /// <summary>Futures klines: [t(ms), open, high, low, close, volume, turnover], ascending.</summary>
    public static IReadOnlyList<Candle> ParseFuturesKlines(string json)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Candle>(data.GetArrayLength());
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7)
                throw new ApiException("Malformed KuCoin futures kline row", false);
            var t = JsonUtil.Lng(row[0]);
            var o = JsonUtil.Dec(row[1]); var h = JsonUtil.Dec(row[2]); var l = JsonUtil.Dec(row[3]);
            var c = JsonUtil.Dec(row[4]); var v = JsonUtil.Dec(row[5]); var tv = JsonUtil.Dec(row[6]);
            if (t is null || o is null || h is null || l is null || c is null || v is null)
                throw new ApiException("KuCoin futures kline row contains unparseable value", false);
            result.Add(new Candle(t.Value, o.Value, h.Value, l.Value, c.Value, v.Value, tv ?? 0m));
        }
        return result;
    }
}