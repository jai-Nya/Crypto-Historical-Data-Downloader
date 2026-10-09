using System.Globalization;
using System.Text.Json;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Api;

/// <summary>Pure JSON -> typed model parsing. No I/O. Culture-invariant.</summary>
public static class BybitParser
{
    public static InstrumentsPage ParseInstruments(string json, MarketCategory category)
    {
        using var doc = Open(json);
        var result = GetResult(doc.RootElement);
        var items = new List<Instrument>();
        if (result.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                var symbol = Str(e, "symbol");
                if (string.IsNullOrEmpty(symbol))
                    throw new ApiException("Instrument entry without symbol", false);

                decimal? tick = null;
                if (e.TryGetProperty("priceFilter", out var pf) && pf.ValueKind == JsonValueKind.Object
                    && pf.TryGetProperty("tickSize", out var ts))
                    tick = Dec(ts);

                items.Add(new Instrument(
                    symbol, category,
                    Str(e, "contractType") ?? "", Str(e, "status") ?? "",
                    Str(e, "baseCoin") ?? "", Str(e, "quoteCoin") ?? "", Str(e, "settleCoin") ?? "",
                    LongProp(e, "launchTime"), tick));
            }
        }
        var cursor = Str(result, "nextPageCursor");
        return new InstrumentsPage(items, string.IsNullOrEmpty(cursor) ? null : cursor);
    }

    public static IReadOnlyList<Ticker> ParseTickers(string json)
    {
        using var doc = Open(json);
        var result = GetResult(doc.RootElement);
        if (!result.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new ApiException("Ticker response missing 'list'", false);

        var tickers = new List<Ticker>(list.GetArrayLength());
        foreach (var e in list.EnumerateArray())
        {
            var symbol = Str(e, "symbol");
            if (string.IsNullOrEmpty(symbol)) continue;
            tickers.Add(new Ticker(symbol,
                DecProp(e, "lastPrice"), DecProp(e, "highPrice24h"), DecProp(e, "lowPrice24h"),
                DecProp(e, "turnover24h"), DecProp(e, "volume24h"),
                DecProp(e, "openInterest"), DecProp(e, "openInterestValue"),
                DecProp(e, "bid1Price"), DecProp(e, "ask1Price")));
        }
        return tickers;
    }

    /// <summary>Returns rows in RESPONSE order (Bybit returns newest first). Callers must sort/dedupe.</summary>
    public static IReadOnlyList<Candle> ParseKlines(string json)
    {
        using var doc = Open(json);
        var result = GetResult(doc.RootElement);
        if (!result.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new ApiException("Kline response missing 'list'", false);

        var candles = new List<Candle>(list.GetArrayLength());
        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7)
                throw new ApiException("Malformed kline row", false);

            var ts = Lng(row[0]);
            var o = Dec(row[1]); var h = Dec(row[2]); var l = Dec(row[3]);
            var c = Dec(row[4]); var v = Dec(row[5]); var t = Dec(row[6]);
            if (ts is null || o is null || h is null || l is null || c is null || v is null || t is null)
                throw new ApiException("Kline row contains unparseable value", false);

            candles.Add(new Candle(ts.Value, o.Value, h.Value, l.Value, c.Value, v.Value, t.Value));
        }
        return candles;
    }

    // ---- helpers ----

    private static JsonDocument Open(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ApiException("Malformed JSON in response", true, null, ex); }
    }

    private static JsonElement GetResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("retCode", out var rc) || rc.ValueKind != JsonValueKind.Number)
            throw new ApiException("Malformed Bybit response: missing retCode", false);

        var code = rc.GetInt32();
        if (code != 0)
        {
            var msg = Str(root, "retMsg") ?? "";
            throw new BybitApiException(code, msg);
        }
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new ApiException("Malformed Bybit response: missing result", false);
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static decimal? DecProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? Dec(p) : null;

    private static long? LongProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? Lng(p) : null;

    private static decimal? Dec(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var n)) return n;
        return null;
    }

    private static long? Lng(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
            return long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) return n;
        return null;
    }
}
