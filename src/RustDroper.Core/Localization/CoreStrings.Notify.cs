namespace RustDroper.Core.Localization;

internal static partial class CoreStrings
{
    private static readonly LocEntry[] Notify =
    [
        // Tepsi bildirimleri (UserNotification)
        L("notify.claimed.title", "Drop claim edildi", "Drop claimed"),
        L("notify.claimed.text", "{0} hesabınıza eklendi.", "{0} was added to your account."),
        L("notify.claimFailed.title", "Drop claim edilemedi", "Couldn't claim drop"),
        L("notify.claimFailed.text", "{0}: {1} elle claim edin (tıklayınca açılır).", "{0}: claim it manually from {1} (click to open)."),
        L("notify.claimReady.title", "Drop claim'e hazır", "Drop ready to claim"),
        L("notify.claimReady.text",
            "{0} tamamlandı. Otomatik claim kapalı: {1} claim edin (tıklayınca açılır).",
            "{0} is complete. Auto-claim is off: claim it from {1} (click to open)."),
        L("notify.place.twitch", "Twitch envanterinden", "your Twitch inventory"),
        L("notify.place.kick", "Kick ödüller sayfasından", "the Kick rewards page"),
        L("notify.campaignStarted.title", "Kampanya başladı", "Campaign started"),
        L("notify.campaignStarted.text", "{0} — {1} izleniyor.", "{0} — watching {1}."),
        L("notify.login.title", "Yeniden giriş gerekli", "Sign-in required again"),
        L("notify.login.text",
            "{0} oturumu geçersiz; drop toplamaya devam etmek için Rust Droper'dan yeniden giriş yapın.",
            "Your {0} session is no longer valid; sign in again from Rust Droper to keep collecting drops."),
        L("notify.lost.title", "İnternet bağlantısı koptu", "Internet connection lost"),
        L("notify.lost.collecting", "Bağlantı gelince drop toplamaya otomatik devam edilecek.", "Drop collection will resume automatically when the connection returns."),
        L("notify.lost.idle", "Bağlantı gelince otomatik yeniden bağlanılacak.", "It will reconnect automatically when the connection returns."),
        L("notify.restored.title", "İnternet bağlantısı geri geldi", "Internet connection restored"),
        L("notify.restored.collecting", "{0} süren kesinti bitti; drop toplama kaldığı yerden devam ediyor.", "The {0} outage is over; drop collection is resuming where it left off."),
        L("notify.restored.idle", "{0} süren kesinti bitti.", "The {0} outage is over."),
        L("notify.dur.s", "{0} sn", "{0} s"),
        L("notify.dur.ms", "{0} dk {1} sn", "{0} min {1} s"),
        L("notify.dur.hm", "{0} sa {1} dk", "{0} h {1} min"),
        L("notify.test.text",
            "Bildirimler çalışıyor. Claim edilen dropları ve önemli durumları böyle göreceksiniz.",
            "Notifications are working. This is how you'll see claimed drops and important events."),
    ];
}
