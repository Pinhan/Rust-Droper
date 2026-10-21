using System.Globalization;
using System.Text.Json;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>
/// <c>GET /api/v1/drops/progress</c> ve <c>/progress/summary</c> cevapları. Alan adları referans projenin (Alorf/TwitchDropsBot)
/// modellerinden alındı (<c>id</c>, <c>progress_units</c>, <c>rewards[].claimed/progress</c>); gerçek hesapla henüz yakalanmadı →
/// ayrıştırıcı bilinçli olarak toleranslıdır (zarf <c>data</c> dizi/nesne/<c>campaigns</c>, <c>id</c>/<c>campaign_id</c>, sayı/metin).
/// </summary>
public static class KickProgressParser
{
    public static ParseResult<KickCampaignProgress> ParseProgress(string json)
    {
        using var document = ParseDocument(json, Loc.T("err.what.progress"));
        var data = KickHttp.Unwrap(document.RootElement);
        var warnings = new List<string>();
        var items = new List<KickCampaignProgress>();
        foreach (var element in EnumerateCampaigns(data))
        {
            if (ParseCampaign(element, warnings) is { } progress)
            {
                items.Add(progress);
            }
        }

        return new ParseResult<KickCampaignProgress>(items, warnings);
    }

    /// <summary>Tek kampanya özeti; cevap birden çok kampanya içeriyorsa <paramref name="campaignId"/> eşleşen alınır. Yoksa <c>null</c>.</summary>
    public static KickCampaignProgress? ParseSummary(string json, string campaignId)
    {
        using var document = ParseDocument(json, Loc.T("err.what.progressSummary"));
        var data = KickHttp.Unwrap(document.RootElement);
        var parsed = EnumerateCampaigns(data)
            .Select(e => ParseCampaign(e, []))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
        return parsed.FirstOrDefault(p => string.Equals(p.CampaignId, campaignId, StringComparison.Ordinal))
            ?? (parsed.Count == 1 && string.IsNullOrEmpty(parsed[0].CampaignId) ? parsed[0] with { CampaignId = campaignId } : null);
    }

    private static IEnumerable<JsonElement> EnumerateCampaigns(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray();
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "campaigns", "progress", "items" })
            {
                if (data.TryGetProp(name, out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    return inner.EnumerateArray();
                }
            }

            return data.TryGetProp("rewards", out _) || data.TryGetProp("progress_units", out _) ? [data] : [];
        }

        return [];
    }

    private static KickCampaignProgress? ParseCampaign(JsonElement element, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = element.GetStringOrNull("id") ?? element.GetStringOrNull("campaign_id") ?? string.Empty;
        var rewards = new List<KickRewardProgress>();
        foreach (var reward in element.GetArrayOrEmpty("rewards"))
        {
            var rewardId = reward.GetStringOrNull("id") ?? reward.GetStringOrNull("reward_id");
            if (string.IsNullOrEmpty(rewardId))
            {
                warnings.Add($"{id}: kimliksiz ödül atlandı.");
                continue;
            }

            rewards.Add(new KickRewardProgress(rewardId, GetDouble(reward, "progress") ?? 0, GetBool(reward, "claimed")));
        }

        var units = GetDouble(element, "progress_units");
        if (string.IsNullOrEmpty(id) && rewards.Count == 0 && units is null)
        {
            return null;
        }

        bool? appConnected = element.TryGetProp("user_app_connected", out var connected) && connected.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? connected.GetBoolean()
            : null;
        return new KickCampaignProgress(id, units, rewards, appConnected);
    }

    internal static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProp(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    internal static bool GetBool(JsonElement element, string name) =>
        element.TryGetProp(name, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));

    internal static JsonDocument ParseDocument(string json, string what) => KickHttp.ParseJson(json, what);
}

/// <summary>Canlı yayın listesi (<c>mobile.kick.com/api/v1/livestreams</c>) ve kanal bilgisi (<c>kick.com/api/v2/channels/{slug}</c>).</summary>
public static class KickLiveParser
{
    public static ParseResult<LiveChannel> ParseLivestreams(string json)
    {
        using var document = KickHttp.ParseJson(json, Loc.T("err.what.livestreams"));
        var data = KickHttp.Unwrap(document.RootElement);

        // Kategori listesi: data.livestreams[]; kampanya listesi (/drops/campaigns/{id}/livestreams): data[] (gerçek sınama, 2026-10-16).
        var list = data;
        if (data.ValueKind != JsonValueKind.Array && !data.TryGetProp("livestreams", out list))
        {
            throw new PlatformDataException(Loc.T("err.kick.noLivestreams"));
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new PlatformDataException(Loc.T("err.kick.noLivestreams"));
        }

        var items = new List<LiveChannel>();
        var warnings = new List<string>();
        var index = -1;
        foreach (var stream in list.EnumerateArray())
        {
            index++;
            if (!stream.TryGetProp("channel", out var channel) || channel.GetStringOrNull("slug") is not { Length: > 0 } slug)
            {
                warnings.Add($"livestreams[{index}]: kanal/slug yok, atlandı.");
                continue;
            }

            items.Add(new LiveChannel(
                Platform.Kick,
                channel.GetStringOrNull("id") ?? slug,
                slug,
                channel.GetStringOrNull("username") ?? slug,
                stream.GetInt32OrNull("viewer_count") ?? 0,
                stream.GetStringOrNull("title"),
                new Uri(KickConstants.SiteBase, Uri.EscapeDataString(slug)),
                TryUri(channel.GetStringOrNull("profile_pic"))));
        }

        return new ParseResult<LiveChannel>(items, warnings);
    }

    /// <summary>Kanal bilgisi; kimlik/slug yoksa <see cref="PlatformDataException"/>. Çevrimdışı kanalda <c>livestream</c> null → <c>IsLive=false</c>.</summary>
    public static KickChannelInfo ParseChannel(string json)
    {
        using var document = KickHttp.ParseJson(json, Loc.T("err.what.channel"));
        var root = document.RootElement;
        var id = root.GetStringOrNull("id");
        var slug = root.GetStringOrNull("slug");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(slug))
        {
            throw new PlatformDataException(Loc.T("err.kick.channelIdSlug"));
        }

        var displayName = slug;
        Uri? avatar = null;
        if (root.TryGetProp("user", out var user))
        {
            displayName = user.GetStringOrNull("username") ?? slug;
            avatar = TryUri(user.GetStringOrNull("profile_pic"));
        }

        var isLive = false;
        var viewers = 0;
        string? title = null;
        int? categoryId = null;
        string? categorySlug = null;
        if (root.TryGetProp("livestream", out var live))
        {
            isLive = KickProgressParser.GetBool(live, "is_live");
            viewers = live.GetInt32OrNull("viewer_count") ?? 0;
            title = live.GetStringOrNull("session_title");
            if (live.GetArrayOrEmpty("categories").FirstOrDefault() is { ValueKind: JsonValueKind.Object } category)
            {
                categoryId = category.GetInt32OrNull("id");
                categorySlug = category.GetStringOrNull("slug");
            }
        }

        return new KickChannelInfo(id, slug, displayName, isLive, TryUri(root.GetStringOrNull("playback_url")), viewers, title, avatar, categoryId, categorySlug);
    }

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}
