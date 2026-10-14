using System.Globalization;
using System.Text.Json;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>Pure JSON -> typed model parsing for Binance. No I/O. Culture-invariant.</summary>
public static class BinanceParser
{
    /// <summary>Throws a transient error when the response body is an error object {"code":...,"msg":...}.</summary>
    public static void ThrowIfError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
            && c.GetInt32() != 200)
        {
            var msg = JsonUtil.Str(root, "msg") ?? "";
            var code = c.GetInt32();
            var transient = code is -1000 or -1001 or -1003 or -1021; // disconnected / internal error / overloaded / timestamp
            throw new ApiException($"Binance error {code}: {msg}", transient, isRateLimit: code == -1003);
        }
    }

    public static IReadOnlyList<Instrument> ParseInstruments(string json, MarketCategory category)
    {
        using var doc = JsonUtil.Open(json);
        var root = doc.RootElement;
        ThrowIfError(root);
        if (!root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Array)
            throw new ApiException("Binance exchangeInfo response missing 'symbols'", false);

        var result = new List<Instrument>(symbols.GetArrayLength());
        foreach (var e in symbols.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;

            var contractType = JsonUtil.Str(e, "contractType");
            string normalizedType;
            if (category == MarketCategory.Spot) normalizedType = "Spot";
            else normalizedType = contractType == "PERPETUAL"
                ? (category == MarketCategory.Linear ? "LinearPerpetual" : "InversePerpetual")
                : contractType ?? "";

            var settle = category == MarketCategory.Spot
                ? JsonUtil.Str(e, "quoteAsset") ?? ""
                : JsonUtil.Str(e, "marginAsset") ?? JsonUtil.Str(e, "quoteAsset") ?? "";
            var baseCoin = JsonUtil.Str(e, "baseAsset") ?? "";
            var quoteCoin = JsonUtil.Str(e, "quoteAsset") ?? "";

            decimal? tick = null;
            if (e.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in filters.EnumerateArray())
                {
                    if (JsonUtil.Str(f, "filterType") == "PRICE_FILTER" && f.TryGetProperty("tickSize", out var ts)
                        && JsonUtil.Dec(ts) is { } t)
                    {
                        tick = t;
                        break;
                    }
                }
            }

            result.Add(new Instrument(
                symbol, category, normalizedType, NormalizeStatus(JsonUtil.Str(e, "status") ?? ""),
                baseCoin, quoteCoin, settle,
                category == MarketCategory.Spot ? null : JsonUtil.LongProp(e, "onboardDate"), tick, "", ""));
        }
        return result;
    }

    public static IReadOnlyList<Ticker> ParseTickers(string json)
    {
        using var doc = JsonUtil.Open(json);
        var root = doc.RootElement;
        ThrowIfError(root);
        if (root.ValueKind != JsonValueKind.Array)
            throw new ApiException("Binance ticker response is not an array", false);

        var result = new List<Ticker>(root.GetArrayLength());
        foreach (var e in root.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;
            result.Add(new Ticker(symbol,
                JsonUtil.DecProp(e, "lastPrice"), JsonUtil.DecProp(e, "highPrice"), JsonUtil.DecProp(e, "lowPrice"),
                JsonUtil.DecProp(e, "quoteVolume"), JsonUtil.DecProp(e, "volume"),
                null, null,
                JsonUtil.DecProp(e, "bidPrice"), JsonUtil.DecProp(e, "askPrice")));
        }
        return result;
    }

    /// <summary>Kl5 rows are [openTime, open, high, low, close, volume, closeTime, quoteVolume, ...]. Ascending.</summary>
    public static IReadOnlyList<Candle> ParseKlines(string json)
    {
        using var doc = JsonUtil.Open(json);
        var root = doc.RootElement;
        ThrowIfError(root);
        if (root.ValueKind != JsonValueKind.Array)
            throw new ApiException("Binance kline response is not an array", false);

        var result = new List<Candle>(root.GetArrayLength());
        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7)
                throw new ApiException("Malformed Binance kline row", false);

            var t = JsonUtil.Lng(row[0]);
            var o = JsonUtil.Dec(row[1]); var h = JsonUtil.Dec(row[2]); var l = JsonUtil.Dec(row[3]);
            var c = JsonUtil.Dec(row[4]); var v = JsonUtil.Dec(row[5]);
            var turnover = row.GetArrayLength() > 7 ? JsonUtil.Dec(row[7]) : null;
            if (t is null || o is null || h is null || l is null || c is null || v is null)
                throw new ApiException("Binance kline row contains unparseable value", false);

            result.Add(new Candle(t.Value, o.Value, h.Value, l.Value, c.Value, v.Value, turnover ?? 0m));
        }
        return result;
    }

    private static string NormalizeStatus(string raw) =>
        string.Equals(raw, "TRADING", StringComparison.OrdinalIgnoreCase) ? "Trading" : raw;
}