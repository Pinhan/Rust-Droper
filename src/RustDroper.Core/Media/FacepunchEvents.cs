using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace RustDroper.Core.Media;

public enum FacepunchEventStatus
{
    Unknown,
    Upcoming,
    Active,
    Ended,
}

/// <summary>
/// Facepunch drop sayfasındaki bir etkinlik bloğu (<c>campaign-N</c>): ad, tur/ayrıntı etiketleri, tarih aralığı.
/// Durum, tarihlerden hesaplanır (<see cref="StatusAt"/>); sayfadaki rozet sınıfı yalnız yedektir.
/// </summary>
/// <param name="Rounds">"Twitch Drops", "54" gibi etiketler (belge sırasıyla).</param>
/// <param name="Subtitle">"Hosted by Rustoria" gibi alt başlık (ham).</param>
/// <param name="BadgeClass">Rozet sınıf ekleri ("is-pending"...); bilinmiyorsa <c>null</c>.</param>
public sealed record FacepunchEvent(
    string? Title,
    IReadOnlyList<string> Rounds,
    string? Subtitle,
    string? BadgeClass,
    string? BadgeText,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt)
{
    /// <summary>Tarihlere göre durum; tarih yoksa rozet sınıfına bakılır; ikisi de yoksa <see cref="FacepunchEventStatus.Unknown"/>.</summary>
    public FacepunchEventStatus StatusAt(DateTimeOffset now)
    {
        if (StartsAt is { } start && now < start)
        {
            return FacepunchEventStatus.Upcoming;
        }

        if (EndsAt is { } end)
        {
            return now >= end ? FacepunchEventStatus.Ended : FacepunchEventStatus.Active;
        }

        if (StartsAt is not null)
        {
            return FacepunchEventStatus.Active; // başladı, bitiş bilinmiyor
        }

        var badge = BadgeClass ?? string.Empty;
        return badge.Contains("pending", StringComparison.OrdinalIgnoreCase) || badge.Contains("upcoming", StringComparison.OrdinalIgnoreCase) ? FacepunchEventStatus.Upcoming
            : badge.Contains("active", StringComparison.OrdinalIgnoreCase) || badge.Contains("live", StringComparison.OrdinalIgnoreCase) ? FacepunchEventStatus.Active
            : badge.Contains("ended", StringComparison.OrdinalIgnoreCase) || badge.Contains("finished", StringComparison.OrdinalIgnoreCase) || badge.Contains("over", StringComparison.OrdinalIgnoreCase) ? FacepunchEventStatus.Ended
            : FacepunchEventStatus.Unknown;
    }

    /// <summary>Birden çok etkinlikten şu an gösterilecek olan: aktif &gt; yaklaşan (en yakın başlangıç) &gt; en son biten.</summary>
    public static FacepunchEvent? Pick(IEnumerable<FacepunchEvent> events, DateTimeOffset now)
    {
        var list = events.ToList();
        return list.Where(e => e.StatusAt(now) == FacepunchEventStatus.Active).OrderByDescending(e => e.StartsAt ?? DateTimeOffset.MinValue).FirstOrDefault()
            ?? list.Where(e => e.StatusAt(now) == FacepunchEventStatus.Upcoming).OrderBy(e => e.StartsAt ?? DateTimeOffset.MaxValue).FirstOrDefault()
            ?? list.Where(e => e.StatusAt(now) == FacepunchEventStatus.Ended).OrderByDescending(e => e.EndsAt ?? DateTimeOffset.MinValue).FirstOrDefault()
            ?? list.FirstOrDefault();
    }
}

public static partial class FacepunchPageParser
{
    [GeneratedRegex("""<div\s+class\s*=\s*"campaign\s+campaign-(\d+)[^"]*"[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex EventStart();

    [GeneratedRegex("""class\s*=\s*"event-title"[^>]*>(.*?)</""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex EventTitleTag();

    [GeneratedRegex("""class\s*=\s*"event-round"[^>]*>(.*?)</span>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex EventRoundTag();

    [GeneratedRegex("""class\s*=\s*"event-subtitle"[^>]*>(.*?)</span>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex EventSubtitleTag();

    [GeneratedRegex("""<span\s+class\s*=\s*"status-badge\s*([^"]*)"[^>]*>(.*?)</span>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex EventBadgeTag();

    [GeneratedRegex("""data-date-id\s*=\s*"[^"]*?-(\d{9,13})"[^>]*>(.*?)</span>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex EventDateTag();

    [GeneratedRegex("""class\s*=\s*"date"[^>]*>(.*?)</span>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex EventDateText();

    [GeneratedRegex("""setupCountdown\(\s*'\.campaign-(\d+)'\s*,\s*(\d+)\s*,\s*(\d+)\s*\)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Countdown();

    private static readonly string[] DateFormats =
    [
        "MMMM d, yyyy 'at' h:mm tt 'UTC'",
        "MMMM d, yyyy 'at' h:mm tt",
        "MMM d, yyyy 'at' h:mm tt 'UTC'",
    ];

    /// <summary>
    /// Sayfadaki etkinlik bloklarını (<c>campaign-0</c>, <c>campaign-1</c>...) okur. Tarih önceliği: <c>data-date-id</c> Unix saniyesi → İngilizce tarih metni (UTC) →
    /// <c>setupCountdown</c> milisaniyeleri. Bozuk/boş sayfa → boş liste (istisna yok).
    /// </summary>
    public static IReadOnlyList<FacepunchEvent> ParseEvents(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        try
        {
            var countdowns = new Dictionary<int, (long A, long B)>();
            foreach (Match m in Countdown().Matches(html))
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                    && long.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                {
                    countdowns[index] = (a, b);
                }
            }

            var starts = EventStart().Matches(html);
            var events = new List<FacepunchEvent>();
            for (var i = 0; i < starts.Count; i++)
            {
                var begin = starts[i].Index;
                var end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
                _ = int.TryParse(starts[i].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index);
                if (ParseEventBlock(html[begin..end], countdowns.GetValueOrDefault(index)) is { } parsed)
                {
                    events.Add(parsed);
                }
            }

            if (events.Count == 0 && countdowns.Count > 0)
            {
                // etkinlik bloğu yok (ör. Kick sayfası): yalnız geri sayım tarihleri
                foreach (var (_, pair) in countdowns.OrderBy(c => c.Key))
                {
                    var (s, e) = FromCountdown(pair);
                    if (s is not null || e is not null)
                    {
                        events.Add(new FacepunchEvent(null, [], null, null, null, s, e));
                    }
                }
            }

            return events;
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    private static FacepunchEvent? ParseEventBlock(string block, (long A, long B) countdown)
    {
        var title = Text(EventTitleTag().Match(block));
        var rounds = EventRoundTag().Matches(block).Select(m => Text(m)).Where(t => t is not null).Select(t => t!).ToList();
        var subtitle = Text(EventSubtitleTag().Match(block));
        var badge = EventBadgeTag().Match(block);
        var badgeClass = badge.Success ? badge.Groups[1].Value.Trim() : null;
        var badgeText = badge.Success ? Text(badge, 2) : null;

        DateTimeOffset? startsAt = null;
        DateTimeOffset? endsAt = null;
        var stamps = EventDateTag().Matches(block);
        if (stamps.Count > 0 && long.TryParse(stamps[0].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var unixStart))
        {
            startsAt = TryUnix(unixStart);
        }

        if (stamps.Count > 1 && long.TryParse(stamps[1].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var unixEnd))
        {
            endsAt = TryUnix(unixEnd);
        }

        if (startsAt is null || endsAt is null)
        {
            var texts = EventDateText().Matches(block).Select(m => ParseDateText(Text(m))).ToList();
            startsAt ??= texts.Count > 0 ? texts[0] : null;
            endsAt ??= texts.Count > 1 ? texts[1] : null;
        }

        if (startsAt is null && endsAt is null)
        {
            (startsAt, endsAt) = FromCountdown(countdown);
        }

        if (title is null && startsAt is null && endsAt is null)
        {
            return null;
        }

        return new FacepunchEvent(title, rounds, subtitle, string.IsNullOrEmpty(badgeClass) ? null : badgeClass, badgeText, startsAt, endsAt);
    }

    /// <summary>İki sıfırdan farklı değer → (küçük = başlangıç, büyük = bitiş); tek değer → başlangıç.</summary>
    private static (DateTimeOffset? Start, DateTimeOffset? End) FromCountdown((long A, long B) pair)
    {
        var values = new[] { pair.A, pair.B }.Where(v => v > 0).Order().ToList();
        return values.Count switch
        {
            0 => (null, null),
            1 => (TryUnixMs(values[0]), null),
            _ => (TryUnixMs(values[0]), TryUnixMs(values[1])),
        };
    }

    private static DateTimeOffset? TryUnix(long seconds) =>
        seconds is > 946684800 and < 4102444800 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null; // 2000..2100

    private static DateTimeOffset? TryUnixMs(long milliseconds) =>
        milliseconds is > 946684800000 and < 4102444800000 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;

    private static DateTimeOffset? ParseDateText(string? text) =>
        text is not null && DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static string? Text(Match match, int group = 1)
    {
        if (!match.Success)
        {
            return null;
        }

        var raw = Regex.Replace(match.Groups[group].Value, "<[^>]*>", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        var decoded = WebUtility.HtmlDecode(raw).Trim();
        return decoded.Length == 0 ? null : Regex.Replace(decoded, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }
}
