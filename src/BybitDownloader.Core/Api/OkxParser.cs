using System.Collections.Generic;
using System.Text.Json;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>Pure JSON -> typed model parsing for OKX. No I/O. Culture-invariant.</summary>
public static class OkxParser
{
    /// <summary>OKX signals many errors with HTTP 200 and a body code (e.g. "50011" rate limit).</summary>
    public static JsonElement RequireOk(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            && c.GetString() is { } code && code != "0")
        {
            var msg = JsonUtil.Str(root, "msg") ?? "";
            throw new ApiException($"OKX error {code}: {msg}", code == "50011",
                isRateLimit: code == "50011");
        }
        if (!root.TryGetProperty("data", out var data))
            throw new ApiException("OKX response missing 'data'", false);
        return data;
    }

    public static IReadOnlyList<Instrument> ParseInstruments(string json, MarketCategory category)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<Instrument>(data.GetArrayLength());
        foreach (var e in data.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "instId");
            if (string.IsNullOrEmpty(symbol)) continue;
            if (category != MarketCategory.Spot && !MatchesSwap(symbol, category)) continue;

            var ctType = JsonUtil.Str(e, "ctType");
            string contractType = category == MarketCategory.Spot ? "Spot"
                : ctType == "inverse" ? "InversePerpetual" : "LinearPerpetual";

            var quote = JsonUtil.Str(e, "quoteCcy") ?? "";
            var settle = JsonUtil.Str(e, "settleCcy") ?? quote;

            result.Add(new Instrument(
                symbol, category, contractType, NormalizeState(JsonUtil.Str(e, "state")),
                JsonUtil.Str(e, "baseCcy") ?? "", quote, settle,
                JsonUtil.LongProp(e, "listTime"), JsonUtil.DecProp(e, "tickSz"), "", ""));
        }
        return result;
    }

    public static IReadOnlyList<Ticker> ParseTickers(string json, MarketCategory category)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Ticker>(data.GetArrayLength());
        foreach (var e in data.EnumerateArray())
        {
            var symbol = JsonUtil.Str(e, "instId");
            if (string.IsNullOrEmpty(symbol)) continue;

            // SWAP tickers carry no ctType; OKX naming is consistent: X-USDT-SWAP (linear) vs X-USD-SWAP (inverse).
            if (category != MarketCategory.Spot && !symbol.EndsWith(category == MarketCategory.Linear ? "-USDT-SWAP" : "-USD-SWAP", System.StringComparison.Ordinal))
                continue;

            result.Add(new Ticker(symbol,
                JsonUtil.DecProp(e, "last"), JsonUtil.DecProp(e, "high24h"), JsonUtil.DecProp(e, "low24h"),
                JsonUtil.DecProp(e, "volCcy24h"), JsonUtil.DecProp(e, "vol24h"),
                null, null,
                JsonUtil.DecProp(e, "bidPx"), JsonUtil.DecProp(e, "askPx")));
        }
        return result;
    }

    /// <summary>Rows are [ts, o, h, l, c, vol, volCcy, volCcyQuote, confirm], newest first. Callers sort.</summary>
    public static IReadOnlyList<Candle> ParseKlines(string json)
    {
        using var doc = JsonUtil.Open(json);
        var data = RequireOk(doc.RootElement);
        if (data.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Candle>(data.GetArrayLength());
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6)
                throw new ApiException("Malformed OKX kline row", false);

            var t = JsonUtil.Lng(row[0]);
            var o = JsonUtil.Dec(row[1]); var h = JsonUtil.Dec(row[2]); var l = JsonUtil.Dec(row[3]);
            var c = JsonUtil.Dec(row[4]); var v = JsonUtil.Dec(row[5]);
            var turnover = row.GetArrayLength() > 7 ? JsonUtil.Dec(row[7]) : JsonUtil.Dec(row[6]);
            if (t is null || o is null || h is null || l is null || c is null || v is null)
                throw new ApiException("OKX kline row contains unparseable value", false);

            result.Add(new Candle(t.Value, o.Value, h.Value, l.Value, c.Value, v.Value, turnover ?? 0m));
        }
        return result;
    }

    private static bool MatchesSwap(string symbol, MarketCategory category) =>
        category == MarketCategory.Linear
            ? symbol.EndsWith("-USDT-SWAP", System.StringComparison.Ordinal)
            : symbol.EndsWith("-USD-SWAP", System.StringComparison.Ordinal);

    private static string NormalizeState(string? raw) =>
        string.Equals(raw, "live", System.StringComparison.OrdinalIgnoreCase) ? "Trading"
        : string.IsNullOrEmpty(raw) ? "" : raw;
}