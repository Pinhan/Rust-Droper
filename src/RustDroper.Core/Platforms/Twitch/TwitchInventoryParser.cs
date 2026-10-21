using System.Text.Json;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Twitch;

/// <param name="Claimed">Talep edilmiş ödüller (<c>gameEventDrops</c>), en yeni önce.</param>
/// <param name="InProgress">İlerlemekte olan izleme süreli drop'lar (<c>dropCampaignsInProgress</c>; yoksa boş). [Ç] şema: canlı ilerleme verisiyle doğrulanacak.</param>
public sealed record TwitchInventorySnapshot(IReadOnlyList<ClaimedReward> Claimed, IReadOnlyList<DropProgress> InProgress)
{
    public int CampaignsInProgress => InProgress.Select(p => p.CampaignId).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// Verilen siteye (ör. <c>twitch.facepunch.com</c>) hesap bağlı mı? O siteyi gerektiren ödül yoksa <c>null</c> (bilinmiyor);
    /// herhangi biri "bağlı" diyorsa <c>true</c>.
    /// </summary>
    public bool? IsLinkedTo(string host)
    {
        var relevant = Claimed
            .Where(r => r.AccountLinkUrl is not null && string.Equals(r.AccountLinkUrl.Host, host, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.AccountLinked)
            .Where(linked => linked is not null)
            .ToList();
        return relevant.Count == 0 ? null : relevant.Any(linked => linked == true);
    }
}

/// <summary><c>Inventory</c> işleminin <c>data</c> öğesini ayrıştırır. Şema gerçek hesap cevabından alındı (2026-10-15).</summary>
public static class TwitchInventoryParser
{
    public static TwitchInventorySnapshot Parse(JsonElement data)
    {
        // Geçersiz token'da Twitch hata yerine currentUser=null döndürebilir → yetki sorunu say.
        if (!data.TryGetProp("currentUser", out var user))
        {
            throw new TwitchUnauthorizedException(Loc.T("err.twitch.inventoryUser"));
        }

        if (!user.TryGetProp("inventory", out var inventory))
        {
            throw new PlatformDataException(Loc.T("err.twitch.inventoryMissing"));
        }

        var claimed = new List<ClaimedReward>();
        foreach (var item in inventory.GetArrayOrEmpty("gameEventDrops"))
        {
            var id = item.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            claimed.Add(new ClaimedReward(
                Platform.Twitch,
                id,
                item.GetStringOrNull("name") ?? id,
                item.GetDateOrNull("lastAwardedAt"),
                item.GetInt32OrNull("totalCount") ?? 1,
                item.TryGetProp("isConnected", out var connected) && connected.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? connected.GetBoolean()
                    : null,
                ToUri(item.GetStringOrNull("requiredAccountLink")),
                ToUri(item.GetStringOrNull("imageURL")),
                item.TryGetProp("game", out var game) && game.ValueKind == JsonValueKind.Object ? game.GetStringOrNull("displayName") : null));
        }

        claimed.Sort((a, b) => Nullable.Compare(b.LastAwardedAt, a.LastAwardedAt));
        return new TwitchInventorySnapshot(claimed, ParseInProgress(inventory));
    }

    private static List<DropProgress> ParseInProgress(JsonElement inventory)
    {
        var progress = new List<DropProgress>();
        foreach (var campaign in inventory.GetArrayOrEmpty("dropCampaignsInProgress"))
        {
            var campaignId = campaign.GetStringOrNull("id");
            if (string.IsNullOrWhiteSpace(campaignId))
            {
                continue;
            }

            var campaignName = campaign.GetStringOrNull("name") ?? campaignId;
            foreach (var drop in campaign.GetArrayOrEmpty("timeBasedDrops"))
            {
                var dropId = drop.GetStringOrNull("id");
                var required = drop.GetInt32OrNull("requiredMinutesWatched") ?? 0;
                if (string.IsNullOrWhiteSpace(dropId) || required <= 0 || (drop.GetInt32OrNull("requiredSubs") ?? 0) > 0)
                {
                    continue;
                }

                var self = drop.TryGetProp("self", out var s) ? s : default;
                var reward = drop.GetArrayOrEmpty("benefitEdges")
                    .Select(e => e.TryGetProp("benefit", out var b) ? b.GetStringOrNull("name") : null)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                    ?? drop.GetStringOrNull("name")
                    ?? dropId;
                progress.Add(new DropProgress(
                    campaignId,
                    campaignName,
                    dropId,
                    reward,
                    required,
                    self.GetInt32OrNull("currentMinutesWatched") ?? 0,
                    self.TryGetProp("isClaimed", out var claimed) && claimed.ValueKind == JsonValueKind.True,
                    self.GetStringOrNull("dropInstanceID"),
                    !self.TryGetProp("hasPreconditionsMet", out var pre) || pre.ValueKind != JsonValueKind.False));
            }
        }

        return progress;
    }

    private static Uri? ToUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}
