using RustDroper.Core.Domain;

namespace RustDroper.Core.Orchestration;

public sealed record Target(Campaign Campaign, LiveChannel Channel);

public sealed record PlannerInput(
    PlatformSnapshot Snapshot,
    DateTimeOffset Now,
    IReadOnlySet<string> CompletedCampaigns,
    IReadOnlyDictionary<string, DateTimeOffset> CampaignCooldownUntil,
    IReadOnlyDictionary<string, DateTimeOffset> ChannelBlockedUntil,
    IReadOnlySet<string> ClaimedDropIds,
    IReadOnlyList<string>? StreamerPriority = null);

/// <summary>
/// Saf planlayıcı: şimdi hangi (kampanya, kanal) izlenmeli? Sıra: kanala özgü (yayıncı) kampanyalar önce — dar ve süreli oldukları için —,
/// kendi aralarında kullanıcının ÖNCELİK SIRASI (<see cref="PlannerInput.StreamerPriority"/>: kampanya kimlikleri; listede olmayanlar sona),
/// eşitlikte bitişi yakın olan, sonra kalan süresi kısa olan; en sonda genel kampanyalar (sıralanmaz).
/// Çevrimdışı (canlı kanalı olmayan/engelli), tamamlanmış, claim edilmiş ya da bitişe yetişemeyen kampanya otomatik ATLANIR → sıradaki ilk uygun seçilir.
/// Bir kampanyaya birden çok yayıncı atanmışsa (<c>ChannelsByCampaign</c>) hepsinden biri geçerlidir: çevrimiçi/engelsiz olanlardan en kalabalık seçilir.
/// </summary>
public static class DropPlanner
{
    public static IReadOnlyList<Target> Plan(PlannerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var targets = new List<(Target Target, int Remaining)>();

        foreach (var campaign in input.Snapshot.Campaigns)
        {
            if (!campaign.IsRunningAt(input.Now)
                || input.CompletedCampaigns.Contains(campaign.Id)
                || (input.CampaignCooldownUntil.TryGetValue(campaign.Id, out var cooldown) && cooldown > input.Now))
            {
                continue;
            }

            // Tüm drop'ları claim edilmişse yapılacak iş yok.
            var unclaimed = campaign.Rewards.Where(r => !input.ClaimedDropIds.Contains(r.Id)).ToList();
            if (unclaimed.Count == 0)
            {
                continue;
            }

            // Kalan en uzun eşik (drop'lar paralel/kümülatif varsayımı [Ç, U4]: konservatif). Bitişe yetişmiyorsa girişme.
            var remaining = unclaimed.Max(r => r.RequiredMinutes);
            if (campaign.EndsAt is { } end && (end - input.Now).TotalMinutes < remaining)
            {
                continue;
            }

            if (!input.Snapshot.ChannelsByCampaign.TryGetValue(campaign.Id, out var channels))
            {
                continue;
            }

            var channel = channels
                .Where(c => !input.ChannelBlockedUntil.TryGetValue(c.Id, out var until) || until <= input.Now)
                .OrderByDescending(c => c.Viewers)
                .FirstOrDefault();
            if (channel is not null)
            {
                targets.Add((new Target(campaign, channel), remaining));
            }
        }

        var priority = input.StreamerPriority ?? [];
        int Rank(Target target) => target.Campaign.Scope == CampaignScope.Streamer ? RankOf(priority, target.Campaign.Id) : 0;

        return
        [
            .. targets
                .OrderBy(t => t.Target.Campaign.Scope == CampaignScope.Streamer ? 0 : 1)
                .ThenBy(t => Rank(t.Target))
                .ThenBy(t => t.Target.Campaign.EndsAt ?? DateTimeOffset.MaxValue)
                .ThenBy(t => t.Remaining)
                .ThenBy(t => t.Target.Campaign.Name, StringComparer.Ordinal)
                .Select(t => t.Target),
        ];
    }

    /// <summary>Kampanyanın öncelik listesindeki sırası (0 en öncelikli); listede yoksa <see cref="int.MaxValue"/>.</summary>
    public static int RankOf(IReadOnlyList<string> priority, string campaignId)
    {
        for (var i = 0; i < priority.Count; i++)
        {
            if (string.Equals(priority[i], campaignId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
