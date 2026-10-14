using System.Collections.Generic;
using System.Text.Json;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>Pure JSON -> typed model parsing for Kraken (spot + futures). No I/O. Culture-invariant.</summary>
public static class KrakenParser
{
    public static JsonElement RequireOk(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err)
            && err.ValueKind == JsonValueKind.Array && err.GetArrayLength() > 0)
        {
            var first = err[0].GetString() ?? "unknown";
            throw new ApiException($"Kraken error: {first}", false);
        }
        if (!root.TryGetProperty("result", out var result))
            throw new ApiException("Kraken response missing 'result'", false);
        return result;
    }

    public static IReadOnlyList<Instrument> ParseSpotInstruments(string json)
    {
        using var doc = JsonUtil.Open(json);
        var result = RequireOk(doc.RootElement);
        var list = new List<Instrument>();
        if (result.ValueKind != JsonValueKind.Object) return list;

        foreach (var prop in result.EnumerateObject())
        {
            var e = prop.Value;
            var symbol = JsonUtil.Str(e, "altname") ?? prop.Name;
            var wsname = JsonUtil.Str(e, "wsname") ?? "";
            var parts = wsname.Split('/');
            var baseCoin = parts.Length == 2 ? parts[0] : (JsonUtil.Str(e, "base") ?? "");
            var quoteCoin = parts.Length == 2 ? parts[1] : (JsonUtil.Str(e, "quote") ?? "");
            list.Add(new Instrument(symbol, MarketCategory.Spot, "Spot", NormalizeStatus(JsonUtil.Str(e, "status")),
                baseCoin, quoteCoin, quoteCoin,
                null, JsonUtil.DecProp(e, "tick_size"), "", ""));
        }
        return list;
    }

    public static IReadOnlyList<Instrument> ParseFuturesInstruments(string json)
    {
        using var doc = JsonUtil.Open(json);
        var root = doc.RootElement;
        var result = JsonUtil.Str(root, "result");
        if (!string.Equals(result, "success", System.StringComparison.OrdinalIgnoreCase))
            throw new ApiException($"Kraken futures API error: {result ?? "unknown"}", false);
        if (!root.TryGetProperty("instruments", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        var list = new List<Instrument>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "symbol") ?? "";
            MarketCategory category;
            if (symbol.StartsWith("PI_", System.StringComparison.Ordinal)) category = MarketCategory.Inverse;
            else if (symbol.StartsWith("PF_", System.StringComparison.Ordinal)) category = MarketCategory.Linear;
            else continue;

            var tradeable = e.TryGetProperty("tradeable", out var t) && t.ValueKind == JsonValueKind.True;
            list.Add(new Instrument(symbol, category,
                category == MarketCategory.Linear ? "LinearPerpetual" : "InversePerpetual",
                tradeable ? "Trading" : "Closed",
                symbol.Contains("_XBT") ? "XBT" : symbol.Split('_').LastOrDefault() ?? "",
                category == MarketCategory.Linear ? "USD" : "USD",
                category == MarketCategory.Linear ? "USD" : "XBT",
                null, JsonUtil.DecProp(e, "tickSize"), "", ""));
        }
        return list;
    }

    /// <summary>Spot ticker: result is a map of pair -> {a,b,c,h,l,o,p,t,v}. Arrays hold [today, 24h].</summary>
    public static IReadOnlyList<Ticker> ParseSpotTickers(string json)
    {
        using var doc = JsonUtil.Open(json);
        var result = RequireOk(doc.RootElement);
        var list = new List<Ticker>();
        if (result.ValueKind != JsonValueKind.Object) return list;

        foreach (var prop in result.EnumerateObject())
        {
            var e = prop.Value;
            var last = Arr(e, "c", 0);
            var high = Arr(e, "h", 1);
            var low = Arr(e, "l", 1);
            var vwap = Arr(e, "p", 1);
            var vol = Arr(e, "v", 1);
            var turnover = vwap is { } p && vol is { } v ? (decimal?)(p * v) : null;
            list.Add(new Ticker(prop.Name,
                last, high, low, turnover, vol,
                null, null,
                Arr(e, "b", 0), Arr(e, "a", 0)));
        }
        return list;
    }

    /// <summary>Futures ticker: array of {symbol,bid,ask,last,vol24h,vol24hQuote,vol24hNtnl,openInterest,...}.</summary>
    public static IReadOnlyList<Ticker> ParseFuturesTickers(string json)
    {
        using var doc = JsonUtil.Open(json);
        var root = doc.RootElement;
        var status = JsonUtil.Str(root, "result");
        if (!string.Equals(status, "success", System.StringComparison.OrdinalIgnoreCase))
            throw new ApiException($"Kraken futures API error: {status ?? "unknown"}", false);
        if (!root.TryGetProperty("tickers", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        var list = new List<Ticker>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;
            list.Add(new Ticker(symbol,
                JsonUtil.DecProp(e, "last"), null, null,
                JsonUtil.DecProp(e, "vol24hNtnl") ?? JsonUtil.DecProp(e, "vol24hQuote"),
                JsonUtil.DecProp(e, "vol24h"),
                JsonUtil.DecProp(e, "openInterest"), null,
                JsonUtil.DecProp(e, "bid"), JsonUtil.DecProp(e, "ask")));
        }
        return list;
    }

    /// <summary>Spot OHLC rows are [time(sec), open, high, low, close, vwap, volume, count]; ascending.</summary>
    public static IReadOnlyList<Candle> ParseSpotKlines(string json)
    {
        using var doc = JsonUtil.Open(json);
        var result = RequireOk(doc.RootElement);
        var list = new List<Candle>();
        if (result.ValueKind != JsonValueKind.Object) return list;

        var pairKey = result.EnumerateObject().FirstOrDefault(p => p.Name != "last");
        if (pairKey.Name is null || pairKey.Value.ValueKind != JsonValueKind.Array) return list;

        foreach (var row in pairKey.Value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7)
                throw new ApiException("Malformed Kraken OHLC row", false);
            var t = SecToMs(JsonUtil.Lng(row[0]));
            var o = JsonUtil.Dec(row[1]); var h = JsonUtil.Dec(row[2]); var l = JsonUtil.Dec(row[3]);
            var c = JsonUtil.Dec(row[4]); var vw = JsonUtil.Dec(row[5]); var v = JsonUtil.Dec(row[6]);
            if (t is null || o is null || h is null || l is null || c is null || v is null)
                throw new ApiException("Kraken OHLC row contains unparseable value", false);
            var turnover = vw is { } p && v is { } vol ? p * vol : 0m;
            list.Add(new Candle(t.Value, o.Value, h.Value, l.Value, c.Value, v.Value, turnover));
        }
        return list;
    }

    /// <summary>Futures chart rows are {time(ms), open, high, low, close, volume}; ascending, max 2000 per page.</summary>
    public static (IReadOnlyList<Candle> Candles, bool MoreCandles) ParseFuturesKlinesPage(string json)
    {
        using var doc = JsonUtil.Open(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("candles", out var arr) || arr.ValueKind != JsonValueKind.Array) return ([], false);

        var list = new List<Candle>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            var t = e.TryGetProperty("time", out var tp) ? JsonUtil.Lng(tp) : null;
            var o = JsonUtil.DecProp(e, "open"); var h = JsonUtil.DecProp(e, "high");
            var l = JsonUtil.DecProp(e, "low"); var c = JsonUtil.DecProp(e, "close");
            var v = JsonUtil.DecProp(e, "volume");
            if (t is null || o is null || h is null || l is null || c is null || v is null)
                throw new ApiException("Kraken futures candle contains unparseable value", false);
            list.Add(new Candle(t.Value, o.Value, h.Value, l.Value, c.Value, v.Value, 0m));
        }
        var more = root.TryGetProperty("more_candles", out var mc) && mc.ValueKind == JsonValueKind.True;
        return (list, more);
    }

    /// <summary>For tests/simple use: a single page with no paging signal.</summary>
    public static IReadOnlyList<Candle> ParseFuturesKlines(string json) => ParseFuturesKlinesPage(json).Candles;

    private static decimal? Arr(JsonElement e, string name, int index)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array || p.GetArrayLength() <= index)
            return null;
        return JsonUtil.Dec(p[index]);
    }

    private static long? SecToMs(long? sec) => sec is { } s ? s * 1000L : null;

    private static string NormalizeStatus(string? raw) =>
        string.Equals(raw, "online", System.StringComparison.OrdinalIgnoreCase) ? "Trading"
        : string.IsNullOrEmpty(raw) ? "" : raw;
}