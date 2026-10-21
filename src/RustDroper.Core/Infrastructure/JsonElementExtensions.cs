using System.Globalization;
using System.Text.Json;

namespace RustDroper.Core.Infrastructure;

/// <summary>
/// Toleranslı JSON okuma: eksik/null/farklı tipte alan istisna fırlatmaz, <c>null</c> döner.
/// Referans projelerdeki <c>KeyError</c> sınıfı çökmelere karşı temel savunma.
/// </summary>
internal static class JsonElementExtensions
{
    public static bool TryGetProp(this JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        value = default;
        return false;
    }

    public static string? GetStringOrNull(this JsonElement element, string name)
    {
        if (!element.TryGetProp(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    public static int? GetInt32OrNull(this JsonElement element, string name)
    {
        if (!element.TryGetProp(name, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt32(out var i))
                {
                    return i;
                }

                return value.TryGetDouble(out var d) && d is >= int.MinValue and <= int.MaxValue
                    ? (int)Math.Round(d)
                    : null;
            case JsonValueKind.String:
                return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    public static DateTimeOffset? GetDateOrNull(this JsonElement element, string name)
    {
        var text = element.GetStringOrNull(name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }

    public static IEnumerable<JsonElement> GetArrayOrEmpty(this JsonElement element, string name) =>
        element.TryGetProp(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
}
