using System.Text.Json;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>
/// <c>GET https://web.kick.com/api/v1/drops/campaigns</c> cevabını (kimlik doğrulamasız) alan modeline çevirir.
/// Tek bozuk kampanya/ödül tüm listeyi düşürmez; atlanır ve uyarı olarak raporlanır.
/// </summary>
public static class KickCampaignParser
{
    private static readonly Uri CdnBase = new("https://ext.cdn.kick.com/");
    private static readonly Uri SiteBase = new("https://kick.com/");

    public static ParseResult<Campaign> Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new PlatformDataException(Loc.T("err.kick.campaignJson"), ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProp("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new PlatformDataException(Loc.T("err.kick.campaignData"));
            }

            var items = new List<Campaign>();
            var warnings = new List<string>();
            var index = -1;
            foreach (var element in data.EnumerateArray())
            {
                index++;
                try
                {
                    var campaign = ParseCampaign(element, index, warnings);
                    if (campaign is not null)
                    {
                        items.Add(campaign);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
                {
                    warnings.Add($"data[{index}]: ayrıştırılamadı ({ex.Message}).");
                }
            }

            return new ParseResult<Campaign>(items, warnings);
        }
    }

    private static Campaign? ParseCampaign(JsonElement element, int index, List<string> warnings)
    {
        var id = element.GetStringOrNull("id");
        if (string.IsNullOrWhiteSpace(id))
        {
            warnings.Add($"data[{index}]: 'id' yok, atlandı.");
            return null;
        }

        var gameSlug = string.Empty;
        var gameName = string.Empty;
        if (element.TryGetProp("category", out var category))
        {
            gameSlug = category.GetStringOrNull("slug") ?? string.Empty;
            gameName = category.GetStringOrNull("name") ?? string.Empty;
        }

        // Kapsam ham diziden belirlenir: kanalları bozuk bir yayıncı kampanyası "genel" sanılmasın
        // (yanlış yayını izleyip hiç ilerleme alamamak en pahalı hata).
        var hasChannelList = false;
        var streamers = new List<Streamer>();
        foreach (var channel in element.GetArrayOrEmpty("channels"))
        {
            hasChannelList = true;
            var streamer = ParseStreamer(channel);
            if (streamer is null)
            {
                warnings.Add($"{id}: 'slug' olmayan kanal atlandı.");
                continue;
            }

            streamers.Add(streamer);
        }

        var rewards = new List<DropReward>();
        foreach (var reward in element.GetArrayOrEmpty("rewards"))
        {
            var parsed = ParseReward(reward);
            if (parsed is null)
            {
                warnings.Add($"{id}: id/required_units eksik ödül atlandı.");
                continue;
            }

            rewards.Add(parsed);
        }

        rewards.Sort((a, b) => a.RequiredMinutes.CompareTo(b.RequiredMinutes));

        Uri? connectUrl = null;
        if (element.GetStringOrNull("connect_url") is { Length: > 0 } connect
            && Uri.TryCreate(connect, UriKind.Absolute, out var connectUri))
        {
            connectUrl = connectUri;
        }

        return new Campaign(
            Platform.Kick,
            id,
            element.GetStringOrNull("name") ?? id,
            gameSlug,
            gameName,
            ParseStatus(element.GetStringOrNull("status")),
            element.GetDateOrNull("starts_at"),
            element.GetDateOrNull("ends_at"),
            hasChannelList ? CampaignScope.Streamer : CampaignScope.General,
            streamers,
            rewards,
            connectUrl);
    }

    private static Streamer? ParseStreamer(JsonElement channel)
    {
        var slug = channel.GetStringOrNull("slug");
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var displayName = slug;
        Uri? avatar = null;
        if (channel.TryGetProp("user", out var user))
        {
            displayName = user.GetStringOrNull("username") ?? slug;
            avatar = TryUri(null, user.GetStringOrNull("profile_picture"));
        }

        return new Streamer(
            channel.GetStringOrNull("id") ?? slug,
            slug,
            displayName,
            new Uri(SiteBase, Uri.EscapeDataString(slug)),
            avatar);
    }

    private static DropReward? ParseReward(JsonElement reward)
    {
        var id = reward.GetStringOrNull("id");
        var minutes = reward.GetInt32OrNull("required_units");
        if (string.IsNullOrWhiteSpace(id) || minutes is null or <= 0)
        {
            return null;
        }

        return new DropReward(
            id,
            reward.GetStringOrNull("name") ?? id,
            minutes.Value,
            TryUri(CdnBase, reward.GetStringOrNull("image_url")));
    }

    private static CampaignStatus ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "active" => CampaignStatus.Active,
        "upcoming" => CampaignStatus.Upcoming,
        "expired" => CampaignStatus.Expired,
        _ => CampaignStatus.Unknown,
    };

    private static Uri? TryUri(Uri? baseUri, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (baseUri is null)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute : null;
        }

        return Uri.TryCreate(baseUri, value, out var combined) ? combined : null;
    }
}
