using RustDroper.Core.Domain;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Orchestration;

public enum NotificationKind
{
    /// <summary>Boşta bekleyen motor bir kampanyayı izlemeye başladı.</summary>
    CampaignStarted,

    DropClaimed,

    /// <summary>Drop tamamlandı ama otomatik claim kapalı: kullanıcı elle claim etmeli.</summary>
    ClaimReady,

    ClaimFailed,

    /// <summary>Oturum geçersiz; kullanıcının yeniden giriş yapması gerekiyor.</summary>
    LoginRequired,

    /// <summary>İnternet bağlantısı koptu.</summary>
    ConnectionLost,

    /// <summary>İnternet bağlantısı geri geldi (önceden "koptu" bildirimi verildiyse).</summary>
    ConnectionRestored,

    /// <summary>Ayarlar sayfasından "bildirimi dene".</summary>
    Test,
}

/// <summary>
/// Kullanıcıya gösterilecek bildirim (pencere kapalıyken de görünür). <see cref="Key"/> aynı olayı tanımlar;
/// <see cref="SuppressFor"/> süresi içinde aynı anahtar bir daha gösterilmez (spam yok).
/// </summary>
public sealed record UserNotification(NotificationKind Kind, string Key, string Title, string Text, Uri? Link, TimeSpan SuppressFor)
{
    public static readonly Uri TwitchInventory = PlatformProfile.Twitch.InventoryUrl;

    private static readonly TimeSpan OncePerDrop = TimeSpan.FromDays(7);

    public static UserNotification DropClaimed(string dropId, string rewardName) =>
        new(NotificationKind.DropClaimed, $"claimed:{dropId}", Loc.T("notify.claimed.title"), Loc.T("notify.claimed.text", rewardName), null, OncePerDrop);

    public static UserNotification ClaimFailed(string dropId, string rewardName, PlatformProfile? platform = null) =>
        new(
            NotificationKind.ClaimFailed,
            $"claim-failed:{dropId}",
            Loc.T("notify.claimFailed.title"),
            Loc.T("notify.claimFailed.text", rewardName, ClaimPlace(platform)),
            (platform ?? PlatformProfile.Twitch).InventoryUrl,
            TimeSpan.FromHours(6));

    public static UserNotification ClaimReady(string dropId, string rewardName, PlatformProfile? platform = null) =>
        new(
            NotificationKind.ClaimReady,
            $"claim-ready:{dropId}",
            Loc.T("notify.claimReady.title"),
            Loc.T("notify.claimReady.text", rewardName, ClaimPlace(platform)),
            (platform ?? PlatformProfile.Twitch).InventoryUrl,
            OncePerDrop);

    /// <summary>"Twitch envanterinden" / "Kick ödüller sayfasından" (ayrılma ekiyle).</summary>
    private static string ClaimPlace(PlatformProfile? platform) =>
        Loc.T(platform?.Platform == Platform.Kick ? "notify.place.kick" : "notify.place.twitch");

    public static UserNotification CampaignStarted(string campaignId, string campaignName, string channelName) =>
        new(
            NotificationKind.CampaignStarted,
            $"campaign-started:{campaignId}",
            Loc.T("notify.campaignStarted.title"),
            Loc.T("notify.campaignStarted.text", campaignName, channelName),
            null,
            TimeSpan.FromHours(6));

    public static UserNotification LoginRequired(PlatformProfile? platform = null) =>
        new(
            NotificationKind.LoginRequired,
            (platform ?? PlatformProfile.Twitch).KeyPrefix + "login-required",
            Loc.T("notify.login.title"),
            Loc.T("notify.login.text", (platform ?? PlatformProfile.Twitch).DisplayName),
            null,
            TimeSpan.FromHours(3));

    /// <param name="collecting">Kullanıcı drop toplamayı başlatmıştı (motor "çalışsın" durumunda): metin buna göre.</param>
    public static UserNotification ConnectionLost(bool collecting = true) =>
        new(
            NotificationKind.ConnectionLost,
            "net-lost",
            Loc.T("notify.lost.title"),
            Loc.T(collecting ? "notify.lost.collecting" : "notify.lost.idle"),
            null,
            TimeSpan.FromSeconds(20));

    public static UserNotification ConnectionRestored(TimeSpan outage, bool collecting = true) =>
        new(
            NotificationKind.ConnectionRestored,
            "net-restored",
            Loc.T("notify.restored.title"),
            Loc.T(collecting ? "notify.restored.collecting" : "notify.restored.idle", FormatDuration(outage)),
            null,
            TimeSpan.FromSeconds(20));

    /// <summary>"45 sn", "2 dk 10 sn", "1 sa 5 dk" (İngilizce: "45 s", "2 min 10 s", "1 h 5 min").</summary>
    public static string FormatDuration(TimeSpan span)
    {
        var total = (long)Math.Max(0, Math.Round(span.TotalSeconds));
        return total switch
        {
            < 60 => Loc.T("notify.dur.s", total),
            < 3600 => Loc.T("notify.dur.ms", total / 60, total % 60),
            _ => Loc.T("notify.dur.hm", total / 3600, total % 3600 / 60),
        };
    }

    public static UserNotification Test() =>
        new(
            NotificationKind.Test,
            "test",
            "Rust Droper",
            Loc.T("notify.test.text"),
            null,
            TimeSpan.Zero);
}

/// <summary>Aynı bildirimin süre dolmadan yeniden gösterilmesini engeller. İş parçacığı güvenlidir.</summary>
public sealed class NotificationGate
{
    private readonly Dictionary<string, DateTimeOffset> _shownUntil = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Bildirim şimdi gösterilmeli mi? <c>true</c> dönerse gösterilmiş sayılır.</summary>
    public bool TryPass(UserNotification notification, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
        {
            foreach (var expired in _shownUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            {
                _shownUntil.Remove(expired);
            }

            if (_shownUntil.ContainsKey(notification.Key))
            {
                return false;
            }

            if (notification.SuppressFor > TimeSpan.Zero)
            {
                _shownUntil[notification.Key] = now + notification.SuppressFor;
            }

            return true;
        }
    }
}
