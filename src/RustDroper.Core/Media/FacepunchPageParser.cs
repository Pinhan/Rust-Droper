using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RustDroper.Core.Media;

/// <summary>Facepunch drop sayfasındaki (twitch.facepunch.com) bir drop kutusu.</summary>
/// <param name="Minutes">Süre ("1 Hour", "30 minutes") dakikaya çevrilmişse; yoksa <c>null</c>.</param>
public sealed record FacepunchDrop(
    string Name,
    int? Minutes,
    string? DurationText,
    Uri? ImageUrl,
    Uri? VideoUrl,
    bool IsStreamerDrop,
    string? StreamerName,
    Uri? StreamerUrl);

/// <summary>
/// Facepunch drop sayfası ayrıştırıcısı. Toleranslı: yapı değişirse/bozuksa hata atmaz, bulabildiğini (ya da boş listeyi) döndürür.
/// Yalnızca görünen bilgiyi okur (ad, süre, görsel/video adresi, yayıncı adı); hesap/kişisel veri sayfada yoktur.
/// </summary>
public static partial class FacepunchPageParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [GeneratedRegex("""<(?:a|div)\b[^>]*\bclass\s*=\s*"[^"]*\bdrop-box\b(?!-)[^"]*"[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex BoxStart();

    [GeneratedRegex("""\bclass\s*=\s*"[^"]*\bsection\b[^"]*\bstreamer-drops\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex StreamerSection();

    [GeneratedRegex("""class\s*=\s*"drop-type"[^>]*>(.*?)</""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex NameTag();

    [GeneratedRegex("""class\s*=\s*"drop-time"[^>]*>.*?<span[^>]*>(.*?)</span>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex TimeTag();

    [GeneratedRegex("""<video\b.*?</video>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex VideoBlock();

    [GeneratedRegex("""<source\b[^>]*\bsrc\s*=\s*"([^"]+)"[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex SourceTag();

    [GeneratedRegex("""<img\b[^>]*\bsrc\s*=\s*"([^"]+)"[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex ImgTag();

    [GeneratedRegex("""class\s*=\s*"streamer-name"[^>]*>(.*?)</""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex StreamerNameTag();

    [GeneratedRegex("""<a\b[^>]*\bhref\s*=\s*"([^"]+)"[^>]*\bclass\s*=\s*"streamer-info"|<a\b[^>]*\bclass\s*=\s*"streamer-info"[^>]*\bhref\s*=\s*"([^"]+)"|<a\b[^>]*\bhref\s*=\s*"([^"]+)"[^>]*\bclass\s*=\s*"drop-box-body"[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex StreamerLink();

    [GeneratedRegex("""(\d+)\s*(hours?|hrs?|h|minutes?|mins?|m)\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex DurationPart();

    /// <summary>Sayfadaki tüm drop kutularını (genel + yayıncı) belge sırasıyla döndürür; hata olursa boş liste.</summary>
    /// <param name="baseUrl">Göreli adresleri çözmek için sayfa adresi.</param>
    public static IReadOnlyList<FacepunchDrop> Parse(string? html, Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        try
        {
            var streamerStart = StreamerSection().Match(html) is { Success: true } section ? section.Index : int.MaxValue;
            var starts = BoxStart().Matches(html);
            var drops = new List<FacepunchDrop>();
            for (var i = 0; i < starts.Count; i++)
            {
                var begin = starts[i].Index;
                var end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
                if (ParseBox(html[begin..end], begin >= streamerStart, baseUrl) is { } drop)
                {
                    drops.Add(drop);
                }
            }

            return drops;
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    private static FacepunchDrop? ParseBox(string box, bool streamer, Uri baseUrl)
    {
        var name = Text(NameTag().Match(box));
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var durationText = Text(TimeTag().Match(box));
        var video = VideoBlock().Match(box) is { Success: true } block ? block.Value : null;
        var imageUrl = Resolve(ImgTag().Match(video ?? string.Empty), baseUrl);
        var videoUrl = Resolve(SourceTag().Match(video ?? string.Empty), baseUrl);
        if (imageUrl is null && video is null)
        {
            // video yoksa: avatar dışındaki ilk görsel
            foreach (Match match in ImgTag().Matches(box))
            {
                var url = Resolve(match, baseUrl);
                if (url is not null && !match.Value.Contains("avatar", StringComparison.OrdinalIgnoreCase))
                {
                    imageUrl = url;
                    break;
                }
            }
        }

        string? streamerName = null;
        Uri? streamerUrl = null;
        if (streamer)
        {
            streamerName = Text(StreamerNameTag().Match(box));
            var link = StreamerLink().Match(box);
            if (link.Success)
            {
                var raw = link.Groups[1].Success ? link.Groups[1].Value : link.Groups[2].Success ? link.Groups[2].Value : link.Groups[3].Value;
                streamerUrl = Absolute(raw, baseUrl);
            }
        }

        return new FacepunchDrop(
            name,
            durationText is null ? null : ParseMinutes(durationText),
            durationText,
            imageUrl,
            videoUrl,
            streamer,
            string.IsNullOrEmpty(streamerName) ? null : streamerName,
            streamerUrl);
    }

    /// <summary>"1 Hour" → 60, "30 minutes" → 30, "1 hour 30 minutes" → 90; anlaşılmazsa <c>null</c>.</summary>
    public static int? ParseMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var total = 0;
        var found = false;
        try
        {
            foreach (Match part in DurationPart().Matches(text))
            {
                if (!int.TryParse(part.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }

                found = true;
                total += part.Groups[2].Value.StartsWith('h') || part.Groups[2].Value.StartsWith('H') ? value * 60 : value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return found ? total : null;
    }

    private static string? Text(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        var raw = Regex.Replace(match.Groups[1].Value, "<[^>]*>", string.Empty, RegexOptions.CultureInvariant, RegexTimeout);
        var decoded = WebUtility.HtmlDecode(raw).Trim();
        return decoded.Length == 0 ? null : CollapseSpaces(decoded);
    }

    private static string CollapseSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static Uri? Resolve(Match match, Uri baseUrl) => match.Success ? Absolute(WebUtility.HtmlDecode(match.Groups[1].Value), baseUrl) : null;

    private static Uri? Absolute(string? raw, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(baseUrl, raw.Trim(), out var uri))
        {
            return null;
        }

        return uri.Scheme is "https" or "http" ? uri : null;
    }
}
