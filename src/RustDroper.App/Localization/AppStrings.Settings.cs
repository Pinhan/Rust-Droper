using RustDroper.Core.Localization;

namespace RustDroper.App.Localization;

internal static partial class AppStrings
{
    private static readonly LocEntry[] Settings =
    [
        L("settings.title", "AYARLAR", "SETTINGS"),
        L("settings.startWithWindows", "Windows ile başlat", "Start with Windows"),
        L("settings.startWithWindows.hint",
            "Oturum açıldığında Rust Droper tepside başlar (HKCU\\...\\Run; başka kayıt defteri girdisi yazılmaz).",
            "Rust Droper starts in the tray when you sign in (HKCU\\...\\Run; no other registry entries are written)."),
        L("settings.startMinimized", "Küçültülmüş başlat", "Start minimized"),
        L("settings.startMinimized.hint",
            "Pencere açılmaz, doğrudan sistem tepsisine yerleşir.",
            "The window stays closed; the app goes straight to the system tray."),
        L("settings.autoClaim", "Otomatik claim", "Auto-claim"),
        L("settings.autoClaim.hint",
            "Tamamlanan drop'ları kendiliğinden claim eder. Varsayılan AÇIK (geri alınamaz oyun eşyaları da claim edilir). Motor çalışırken yapılan değişiklik bir sonraki Başlat'ta geçerli olur.",
            "Claims completed drops automatically. ON by default (irreversible in-game items are claimed too). A change made while the engine is running applies on the next Start."),
        L("settings.keepAwake", "Çalışırken uykuya geçmeyi engelle", "Prevent sleep while running"),
        L("settings.keepAwake.hint",
            "Motor izlerken bilgisayarın uykuya dalmasını önler (ekran yine kapanabilir); uyuyan bilgisayar drop ilerletemez. Boşta iken uyku engellenmez. Varsayılan AÇIK.",
            "Keeps the PC from falling asleep while the engine is watching (the screen may still turn off); a sleeping PC can't make drop progress. Sleep is not blocked while idle. ON by default."),
        L("settings.rustOnly", "Yalnız Rust ödülü veren kampanyalar", "Rust-reward campaigns only"),
        L("settings.rustOnly.hint",
            "Rust yayınında bulunan ama Rust eşyası vermeyen yayıncı kampanyalarını (ör. sohbet rozeti) izlemez ve göstermez. Bir Rust kampanyası görünmüyorsa kapatıp deneyin. Değişiklik Duraklat → Başlat ile geçerli olur.",
            "Ignores and hides streamer campaigns that appear in the Rust directory but don't give Rust items (e.g. chat badges). If a Rust campaign doesn't show up, turn this off and try again. Takes effect after Pause → Start."),
        L("settings.autoRestart", "Çökerse otomatik yeniden başlat", "Restart automatically after a crash"),
        L("settings.autoRestart.hint",
            "Motor çalışırken uygulama beklenmedik kapanırsa (çökme, Görev Yöneticisi'nden sonlandırma, bilgisayarın yeniden başlaması) en geç 5 dk içinde tepside açılıp kaldığı yerden devam eder. Duraklat ya da tepsiden Çıkış yapınca devre dışı kalır.",
            "If the app closes unexpectedly while the engine is running (crash, ended from Task Manager, PC restart), it reopens in the tray within 5 min and resumes where it left off. Turned off by Pause or by Exit from the tray."),
        L("settings.startNotice", "Başlat uyarısını göster", "Show the Start reminder"),
        L("settings.startNotice.hint",
            "Başlat'a basınca (pencere açıkken) aynı hesapla başka canlı yayın izlememeniz gerektiğini hatırlatan kısa bir pencere gösterir.",
            "When you press Start (with the window open), shows a short reminder not to watch another live stream with the same account."),
        L("settings.notifications", "Bildirimler", "Notifications"),
        L("settings.notifications.hint",
            "Drop claim edilince, claim başarısız olunca, yeniden giriş gerekince ve kampanya başlayınca tepside bildirim gösterir (pencere kapalıyken de). Varsayılan AÇIK.",
            "Shows a tray notification when a drop is claimed, a claim fails, sign-in is needed again, or a campaign starts (even while the window is closed). ON by default."),
        L("settings.testNotification", "Bildirimi dene", "Test notification"),
        L("settings.language", "Dil / Language", "Dil / Language"),
        L("settings.language.auto", "Otomatik (Windows)", "Automatic (Windows)"),
        L("settings.language.tr", "Türkçe", "Türkçe"),
        L("settings.language.en", "English", "English"),
        L("settings.error.autostart", "Windows ile başlatma ayarı değiştirilemedi: {0}", "Couldn't change the start-with-Windows setting: {0}"),
    ];
}
