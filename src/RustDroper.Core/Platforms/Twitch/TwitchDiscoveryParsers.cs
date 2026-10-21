using System.Text.Json;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;

namespace RustDroper.Core.Platforms.Twitch;

/// <summary><c>DirectoryPage_Game</c> (Drops-Enabled filtreli) cevabından canlı kanalları çıkarır. Şema gerçek cevaptan (2026-10-15).</summary>
public static class TwitchDirectoryParser
{
    public static IReadOnlyList<LiveChannel> Parse(JsonElement data)
    {
        // game=null → oyun slug'ı bilinmiyor/dizin yok; boş liste (hata değil).
        if (!data.TryGetProp("game", out var game)
            || !game.TryGetProp("streams", out var streams))
        {
            return [];
        }

        var channels = new List<LiveChannel>();
        foreach (var edge in streams.GetArrayOrEmpty("edges"))
        {
            if (!edge.TryGetProp("node", out var node) || !node.TryGetProp("broadcaster", out var broadcaster))
            {
                continue;
            }

            var id = broadcaster.GetStringOrNull("id");
            var login = broadcaster.GetStringOrNull("login");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(login))
            {
                continue;
            }

            Uri.TryCreate(broadcaster.GetStringOrNull("profileImageURL"), UriKind.Absolute, out var avatar);
            channels.Add(new LiveChannel(
                Platform.Twitch,
                id,
                login,
                broadcaster.GetStringOrNull("displayName") ?? login,
                node.GetInt32OrNull("viewersCount") ?? 0,
                node.GetStringOrNull("title"),
                new Uri("https://www.twitch.tv/" + Uri.EscapeDataString(login)),
                avatar));
        }

        return channels;
    }
}

/// <param name="ChannelId">Sorgulanan kanal.</param>
/// <param name="Campaigns">İzleme süresiyle kazanılan (requiredMinutesWatched &gt; 0, abonelik şartı yok) kampanyalar.</param>
/// <param name="SkippedCampaigns">İzlemeyle kazanılamayan (abonelik/olay tabanlı) kampanya kimlikleri.</param>
public sealed record AvailableDropsResult(string? ChannelId, IReadOnlyList<Campaign> Campaigns, IReadOnlyList<string> SkippedCampaigns);

/// <summary>
/// <c>DropsHighlightService_AvailableDrops</c> cevabını ayrıştırır: kanalın izleyiciye sunduğu kampanyalar.
/// Önemli: bu kampanyalardan bazıları abonelik/rozet tabanlıdır (ör. "SPOOKY SEASON": requiredSubs=5) ve izlemeyle kazanılmaz.
/// </summary>
public static class TwitchAvailableDropsParser
{
    public static AvailableDropsResult Parse(JsonElement data)
    {
        if (!data.TryGetProp("channel", out var channel))
        {
            return new AvailableDropsResult(null, [], []);
        }

        var campaigns = new List<Campaign>();
        var skipped = new List<string>();
        foreach (var raw in channel.GetArrayOrEmpty("viewerDropCampaigns"))
        {
            var id = raw.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var rewards = new List<DropReward>();
            DateTimeOffset? earliestStart = null;
            foreach (var drop in raw.GetArrayOrEmpty("timeBasedDrops"))
            {
                var minutes = drop.GetInt32OrNull("requiredMinutesWatched") ?? 0;
                var subs = drop.GetInt32OrNull("requiredSubs") ?? 0;
                var dropId = drop.GetStringOrNull("id");
                if (minutes <= 0 || subs > 0 || string.IsNullOrWhiteSpace(dropId))
                {
                    continue;
                }

                var start = drop.GetDateOrNull("startAt");
                if (start is not null && (earliestStart is null || start < earliestStart))
                {
                    earliestStart = start;
                }

                var benefit = FirstBenefit(drop);
                Uri.TryCreate(benefit?.GetStringOrNull("imageAssetURL"), UriKind.Absolute, out var image);
                var name = benefit?.GetStringOrNull("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = drop.GetStringOrNull("name");
                }

                rewards.Add(new DropReward(dropId, string.IsNullOrWhiteSpace(name) ? dropId : name, minutes, image));
            }

            if (rewards.Count == 0)
            {
                skipped.Add(id);
                continue;
            }

            rewards.Sort((a, b) => a.RequiredMinutes.CompareTo(b.RequiredMinutes));
            string gameName = string.Empty;
            string gameId = string.Empty;
            if (raw.TryGetProp("game", out var game))
            {
                gameName = game.GetStringOrNull("name") ?? game.GetStringOrNull("displayName") ?? string.Empty;
                gameId = game.GetStringOrNull("id") ?? string.Empty;
            }

            Uri.TryCreate(raw.GetStringOrNull("detailsURL"), UriKind.Absolute, out var details);
            campaigns.Add(new Campaign(
                Platform.Twitch,
                id,
                raw.GetStringOrNull("name") ?? id,
                Slugify(gameName, gameId),
                gameName,
                CampaignStatus.Active,
                earliestStart,
                raw.GetDateOrNull("endAt"),
                CampaignScope.General,
                [],
                rewards,
                details));
        }

        return new AvailableDropsResult(channel.GetStringOrNull("id"), campaigns, skipped);
    }

    private static JsonElement? FirstBenefit(JsonElement drop)
    {
        foreach (var edge in drop.GetArrayOrEmpty("benefitEdges"))
        {
            if (edge.TryGetProp("benefit", out var benefit))
            {
                return benefit;
            }
        }

        return null;
    }

    // AvailableDrops oyun slug'ı vermez; ad → slug (ör. "Marvel Rivals" → "marvel-rivals"). Kesin eşleşme için GameId kullanılmalı.
    private static string Slugify(string gameName, string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return gameId;
        }

        var chars = gameName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
