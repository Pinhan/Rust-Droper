using RustDroper.Core.Domain;
using RustDroper.Core.Media;

namespace RustDroper.Core.Platforms;

/// <summary>
/// "Yalnız Rust ödülü veren kampanyalar" süzgeci. Rust kategorisinde yayın yapan biri kendi Rust-dışı kampanyasını (ör. sohbet rozeti) çalıştırıyorsa
/// keşif onu da "Rust yayını" olarak görür; bu süzgeç onları eler. Bir kampanya aşağıdakilerden biri varsa Rust sayılır (herhangi biri yeter, bilinmezlikte
/// yanlış eleme riskini azaltmak için bilerek cömert):
/// <list type="bullet">
/// <item>kampanyanın oyun adı "rust" içeriyor;</item>
/// <item>kampanya/hesap bağlantı adresi <c>*.facepunch.com</c>;</item>
/// <item>bir ödül adı resmî Facepunch drop kataloğundaki adlardan biri (örn. Rug, Bed, Backpack).</item>
/// </list>
/// </summary>
public static class RustCampaignFilter
{
    public static bool IsRust(Campaign campaign, IReadOnlySet<string> knownRewardKeys)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(knownRewardKeys);

        if (campaign.GameName.Contains("rust", StringComparison.OrdinalIgnoreCase)
            || string.Equals(campaign.GameSlug, "rust", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (campaign.ConnectUrl is { Host: { } host }
            && (host.Equals("facepunch.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".facepunch.com", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return campaign.Rewards.Any(r => knownRewardKeys.Contains(DropNames.Key(r.Name)));
    }

    /// <summary>Katalog adlarını karşılaştırma anahtarlarına çevirir.</summary>
    public static IReadOnlySet<string> KeysOf(IEnumerable<string> catalogNames) =>
        catalogNames.Select(DropNames.Key).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
}
