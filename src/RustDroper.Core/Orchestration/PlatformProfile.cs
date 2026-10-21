using RustDroper.Core.Domain;

namespace RustDroper.Core.Orchestration;

/// <summary>
/// Bir platformun kullanıcıya görünen kimliği (ad, elle claim sayfası, bildirim anahtarı ön eki). <see cref="DropperService"/> platform başına
/// bir örnek olarak çalışır; metinler ve bildirim anahtarları buradan gelir (Twitch varsayılan: anahtarlar eskisi gibi ön eksiz).
/// </summary>
public sealed record PlatformProfile(Platform Platform, string DisplayName, Uri InventoryUrl, string KeyPrefix)
{
    public static PlatformProfile Twitch { get; } = new(Platform.Twitch, "Twitch", new Uri("https://www.twitch.tv/drops/inventory"), string.Empty);

    /// <summary>Kick'te ödüller <c>kick.com/drops/rewards</c> sayfasından görülür/claim edilir (<c>drops/inventory</c> buraya yönleniyor).</summary>
    public static PlatformProfile Kick { get; } = new(Platform.Kick, "Kick", new Uri("https://kick.com/drops/rewards"), "kick:");

    public static PlatformProfile For(Platform platform) => platform == Platform.Kick ? Kick : Twitch;
}
