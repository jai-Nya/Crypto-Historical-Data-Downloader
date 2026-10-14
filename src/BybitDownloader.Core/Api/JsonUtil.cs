using System.Globalization;
using System.Text.Json;

namespace BybitDownloader.Core.Api;

/// <summary>Culture-invariant JSON helpers shared by all exchange parsers. No I/O.</summary>
public static class JsonUtil
{
    public static JsonDocument Open(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ApiException("Malformed JSON in response", true, null, ex); }
    }

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static decimal? DecProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? Dec(p) : null;

    public static long? LongProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? Lng(p) : null;

    public static decimal? Dec(JsonElement e)
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

    public static long? Lng(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
            return long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) return n;
        return null;
    }
}