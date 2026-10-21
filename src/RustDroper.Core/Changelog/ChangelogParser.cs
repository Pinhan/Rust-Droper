using System.Globalization;
using System.Text.Json;

namespace RustDroper.Core.Changelog;

/// <summary>
/// <c>changelog.json</c> ayrıştırıcısı. Toleranslıdır: bozuk JSON → boş liste + uyarı; geçersiz giriş (sürüm/tarih/tür hatalı) atlanır ve nedeni
/// <see cref="ChangelogParseResult.Warnings"/>'a yazılır; bir dilde eksik metin diğer dile düşer; ikisi de boş madde atlanır. Hiçbir durumda istisna fırlatmaz.
/// Biçim: <c>{ "releases": [ { version, date, type, tag{tr,en}, summary{tr,en}, added[], changed[], fixed[], removed[] } ] }</c> (yeni → eski).
/// </summary>
public static class ChangelogParser
{
    public static ChangelogParseResult Parse(string? json)
    {
        var warnings = new List<string>();
        var entries = new List<ChangelogEntry>();
        if (string.IsNullOrWhiteSpace(json))
        {
            warnings.Add("boş içerik");
            return new ChangelogParseResult(entries, warnings);
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            var releases = root.ValueKind == JsonValueKind.Array
                ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("releases", out var r) ? r : default;
            if (releases.ValueKind != JsonValueKind.Array)
            {
                warnings.Add("'releases' dizisi yok");
                return new ChangelogParseResult(entries, warnings);
            }

            var index = 0;
            foreach (var item in releases.EnumerateArray())
            {
                index++;
                try
                {
                    if (TryParseEntry(item, out var entry, out var reason))
                    {
                        entries.Add(entry!);
                    }
                    else
                    {
                        warnings.Add($"releases[{index - 1}] atlandı: {reason}");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
                {
                    warnings.Add($"releases[{index - 1}] atlandı: {ex.Message}");
                }
            }
        }
        catch (JsonException ex)
        {
            entries.Clear();
            warnings.Add($"geçersiz JSON: {ex.Message}");
        }

        return new ChangelogParseResult(entries, warnings);
    }

    private static bool TryParseEntry(JsonElement item, out ChangelogEntry? entry, out string reason)
    {
        entry = null;
        reason = string.Empty;
        if (item.ValueKind != JsonValueKind.Object)
        {
            reason = "nesne değil";
            return false;
        }

        var version = Str(item, "version");
        if (!SemVer.TryParse(version, out _))
        {
            reason = $"geçersiz sürüm '{version}'";
            return false;
        }

        if (!DateOnly.TryParseExact(Str(item, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            reason = $"geçersiz tarih '{Str(item, "date")}'";
            return false;
        }

        if (!Enum.TryParse<ChangelogType>(Str(item, "type"), ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            reason = $"geçersiz tür '{Str(item, "type")}'";
            return false;
        }

        entry = new ChangelogEntry(
            version!,
            date,
            type,
            Text(item, "tag"),
            Text(item, "summary"),
            Items(item, "added"),
            Items(item, "changed"),
            Items(item, "fixed"),
            Items(item, "removed"));
        return true;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static LocalizedText Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ToText(value) ?? new LocalizedText(string.Empty, string.Empty) : new LocalizedText(string.Empty, string.Empty);

    private static LocalizedText? ToText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tr = Str(value, "tr")?.Trim() ?? string.Empty;
        var en = Str(value, "en")?.Trim() ?? string.Empty;
        return tr.Length == 0 && en.Length == 0 ? null : new LocalizedText(tr, en);
    }

    private static IReadOnlyList<LocalizedText> Items(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. array.EnumerateArray().Select(ToText).OfType<LocalizedText>()];
    }
}
