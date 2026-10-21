using RustDroper.Core.Localization;

namespace RustDroper.App.Localization;

internal static partial class AppStrings
{
    private static readonly LocEntry[] About =
    [
        L("about.title", "HAKKINDA", "ABOUT"),
        L("about.back", "Pano", "Dashboard"),
        L("about.tagline",
            "Rust drop'larını Twitch ve Kick'te arka planda kendiliğinden toplar.",
            "Collects Rust drops on Twitch and Kick automatically in the background."),
        L("about.version", "Sürüm {0}", "Version {0}"),
        L("about.footer", "{0} v{1}", "{0} v{1}"),
        L("about.developer", "Geliştirici: {0}", "Developer: {0}"),
        L("about.contact", "İletişim: {0}", "Contact: {0}"),
        L("about.contactLabel", "İletişim:", "Contact:"),
        L("about.copyright", "© 2026 {0}. MIT Lisansı ile yayımlanmıştır.", "© 2026 {0}. Released under the MIT License."),
        L("about.sendMail", "E-posta gönder", "Send e-mail"),
        L("about.copy", "Kopyala", "Copy"),
        L("about.feedback",
            "Hata bildirimi ve öneriler için bu adrese yazabilirsiniz; mümkünse sürüm numarasını ve günlük dosyasının ilgili satırlarını ekleyin.",
            "You can write to this address for bug reports and suggestions; if possible, include the version number and the relevant lines of the log file."),
        L("about.whatTitle", "Ne yapar?", "What does it do?"),
        L("about.whatBody",
            "Rust'ın Twitch ve Kick drop kampanyalarını izler; uygun bir yayını sizin yerinize açık tutar, ilerlemeyi gösterir ve tamamlanan ödülleri claim eder. " +
            "Twitch'te izleme gerçek yayın akışıyla (tarayıcısız), Kick'te gizli bir tarayıcı oynatıcısıyla yapılır; ikisi de sessizdir ve ekranınızda görünmez. " +
            "Drop ilerlemesi hesap başına aynı anda tek kanal için işler; uygulama bu yüzden tek yayını izler.",
            "Follows Rust's Twitch and Kick drop campaigns; keeps a suitable stream open for you, shows progress and claims completed rewards. " +
            "On Twitch, watching uses the real stream feed (no browser); on Kick it uses a hidden browser player. Both are silent and don't show on your screen. " +
            "Drop progress counts for one channel at a time per account, so the app watches a single stream."),
        L("about.privacyTitle", "Gizlilik", "Privacy"),
        L("about.privacyBody",
            "Parolanız uygulamaya hiç girmez; girişi Twitch/Kick'in kendi sayfasında siz yaparsınız. Oturum anahtarları yalnız bu bilgisayarda, Windows kullanıcı hesabınıza bağlı " +
            "şifreyle (DPAPI) saklanır. Uygulama telemetri toplamaz; yalnızca Twitch, Kick ve Facepunch sitelerine bağlanır. Günlüklerde parola/anahtar bulunmaz.",
            "Your password never enters the app; you sign in on Twitch's or Kick's own page. Session keys are stored only on this PC, encrypted (DPAPI) and tied to your " +
            "Windows user account. The app collects no telemetry; it only connects to the Twitch, Kick and Facepunch sites. Logs contain no passwords or keys."),
        L("about.noteTitle", "Önemli not", "Important note"),
        L("about.noteBody",
            "Rust Droper bağımsız bir projedir; Twitch, Kick, Facepunch veya Rust ile bağlantılı değildir, onlarca onaylanmamıştır. Adlar ve markalar sahiplerine aittir. " +
            "Platformların kullanım koşulları otomasyonu kısıtlıyor olabilir; hesabınızda uyarı veya kısıtlama olasılığı sıfır değildir. Kullanım riski size aittir; " +
            "yazılım \"olduğu gibi\", herhangi bir garanti vermeden sunulur. Geri alınamaz oyun eşyalarının claim edilmesi dahil sonuçlardan geliştirici sorumlu değildir.",
            "Rust Droper is an independent project; it is not affiliated with or endorsed by Twitch, Kick, Facepunch or Rust. Names and trademarks belong to their owners. " +
            "The platforms' terms of use may restrict automation; the chance of a warning or restriction on your account is not zero. You use it at your own risk; " +
            "the software is provided \"as is\", without any warranty. The developer is not responsible for the consequences, including claiming irreversible in-game items."),
        L("about.componentsTitle", "Kullanılan bileşenler", "Components used"),
        L("about.third.dotnet", ".NET 10 / WPF (Microsoft, MIT)", ".NET 10 / WPF (Microsoft, MIT)"),
        L("about.third.webview2", "Microsoft Edge WebView2 (Microsoft; Kick oynatıcısı için)", "Microsoft Edge WebView2 (Microsoft; for the Kick player)"),
        L("about.third.qrcoder", "QRCoder (Raffael Herrmann, MIT; Kick giriş QR kodu)", "QRCoder (Raffael Herrmann, MIT; Kick sign-in QR code)"),
        L("about.openData", "Veri klasörünü aç", "Open data folder"),
        L("about.openLogs", "Günlük klasörünü aç", "Open logs folder"),
        L("about.copied", "E-posta adresi kopyalandı", "E-mail address copied"),
        L("about.noMailApp", "E-posta uygulaması bulunamadı; adres kopyalandı", "No e-mail app found; the address was copied"),
    ];
}
