using RustDroper.Core.Localization;

namespace RustDroper.App.Localization;

internal static partial class AppStrings
{
    private static readonly LocEntry[] Health =
    [
        L("health.title", "SAĞLIK KONTROLÜ", "HEALTH CHECK"),
        L("health.recheck", "Yeniden kontrol et", "Re-check"),
        L("health.recheck.tip", "Envanteri/kampanyaları yeniden okuyup tekrar kontrol et", "Re-read the inventory and campaigns, then check again"),
        L("health.summary.fail", "{0} sorun, {1} uyarı", "Problems: {0}, warnings: {1}"),
        L("health.summary.warn", "{0} uyarı — sorun yok", "Warnings: {0} — no problems"),
        L("health.summary.ok", "Her şey hazır", "Everything is ready"),

        L("health.internet", "İnternet", "Internet"),
        L("health.internet.ok", "Bağlantı var.", "Connected."),
        L("health.internet.fail", "Bağlantı yok; geri gelince otomatik devam edilir.", "No connection; it resumes automatically when it's back."),

        L("health.account.title", "{0} girişi", "{0} sign-in"),
        L("health.account.ok", "Bağlı: {0}", "Connected: {0}"),
        L("health.account.loggingIn", "Giriş bekleniyor.", "Waiting for sign-in."),
        L("health.account.needsLogin", "Oturum reddedildi: yeniden giriş yapın.", "The session was rejected: sign in again."),
        L("health.account.offline", "Çevrimdışı; oturum doğrulanamadı (ağ gelince denenir).", "Offline; couldn't verify the session (it will retry when the network is back)."),
        L("health.account.verifying", "Oturum doğrulanıyor…", "Verifying the session…"),
        L("health.account.loggedOut", "Giriş yapılmamış.", "Not signed in."),

        L("health.link.title", "Facepunch/Steam", "Facepunch/Steam"),
        L("health.link.ok", "Hesap bağlı: ödüller Steam'e teslim edilir.", "Account linked: rewards are delivered to Steam."),
        L("health.link.no", "Hesap BAĞLI DEĞİL: {0} adresinden bağlayın, yoksa ödüller teslim edilmez.", "Account NOT linked: link it at {0}, otherwise rewards won't be delivered."),
        L("health.link.unknown", "Bağlantı durumu bilinmiyor (envanter henüz okunmadı).", "Link status unknown (the inventory hasn't been read yet)."),

        L("health.player.title", "Oynatıcı oturumu", "Player session"),
        L("health.player.ok", "Açık: Kick izlemesi yapılabilir.", "Active: Kick watching is available."),
        L("health.player.loggedOut", "YOK: Kick kartından 'Oynatıcı girişi' yapın, yoksa izleme başlamaz.", "MISSING: use 'Player sign-in' on the Kick card, otherwise watching won't start."),
        L("health.player.unavailable", "WebView2 bileşeni bu bilgisayarda yok; Kick izlemesi çalışmaz.", "The WebView2 component isn't installed on this PC; Kick watching won't work."),
        L("health.player.checking", "Doğrulanıyor…", "Verifying…"),
        L("health.player.unknown", "Henüz doğrulanmadı.", "Not verified yet."),

        L("health.campaigns.title", "Kampanya keşfi", "Campaign discovery"),
        L("health.campaigns.error", "Okunamadı: {0}", "Couldn't read: {0}"),
        L("health.campaigns.notRead", "Henüz okunmadı.", "Not read yet."),
        L("health.campaigns.none", "Aktif Rust kampanyası yok (etkinlik başlayınca otomatik görünür).", "No active Rust campaign (it appears automatically when the event starts)."),
        L("health.campaigns.found.one", "{0} kampanya bulundu.", "{0} campaign found."),
        L("health.campaigns.found.other", "{0} kampanya bulundu.", "{0} campaigns found."),
        L("health.campaigns.skipped.one",
            " Rust dışı {0} kampanya atlandı ({1}); yanlışsa Ayarlar'dan 'Yalnız Rust ödülü veren kampanyalar'ı kapatın.",
            " {0} non-Rust campaign was skipped ({1}); if that's wrong, turn off 'Rust-reward campaigns only' in Settings."),
        L("health.campaigns.skipped.other",
            " Rust dışı {0} kampanya atlandı ({1}); yanlışsa Ayarlar'dan 'Yalnız Rust ödülü veren kampanyalar'ı kapatın.",
            " {0} non-Rust campaigns were skipped ({1}); if that's wrong, turn off 'Rust-reward campaigns only' in Settings."),

        L("health.engine.title", "Motor", "Engine"),
        L("health.engine.degraded", "Çalışıyor ama sorunlu: {0}", "Running but with problems: {0}"),
        L("health.engine.authRequired", "Yeniden giriş gerekli.", "Sign-in required again."),
        L("health.engine.running", "Çalışıyor: {0}", "Running: {0}"),
        L("health.engine.paused", "Duraklatıldı (Başlat'a basın).", "Paused (press Start)."),

        L("health.autoClaim.title", "Otomatik claim", "Auto-claim"),
        L("health.autoClaim.on", "AÇIK: tamamlanan drop'lar kendiliğinden claim edilir.", "ON: completed drops are claimed automatically."),
        L("health.autoClaim.off", "KAPALI: drop'lar claim EDİLMEZ (Ayarlar'dan açın).", "OFF: drops will NOT be claimed (turn it on in Settings)."),
        L("health.keepAwake.title", "Uyku engelleme", "Sleep prevention"),
        L("health.keepAwake.on", "AÇIK: motor çalışırken bilgisayar uykuya geçmez.", "ON: the PC doesn't sleep while the engine is running."),
        L("health.keepAwake.off", "KAPALI: bilgisayar uyursa drop ilerlemez.", "OFF: drops make no progress if the PC sleeps."),
        L("health.crash.title", "Çökme koruması", "Crash protection"),
        L("health.crash.on", "AÇIK: beklenmedik kapanırsa en geç 5 dk içinde kendiliğinden açılır.", "ON: if the app closes unexpectedly, it reopens by itself within 5 min."),
        L("health.crash.devRun", "Bu çalıştırmada devre dışı (geliştirme/sınama).", "Disabled in this run (development/testing)."),
        L("health.crash.off", "KAPALI (Ayarlar).", "OFF (Settings)."),
        L("health.startWin.title", "Windows ile başlat", "Start with Windows"),
        L("health.startWin.on", "AÇIK: oturum açılınca tepside başlar.", "ON: starts in the tray when you sign in."),
        L("health.startWin.off", "KAPALI: bilgisayar yeniden başlarsa uygulamayı elle açmanız gerekir.", "OFF: if the PC restarts, you'll need to open the app yourself."),
    ];
}
