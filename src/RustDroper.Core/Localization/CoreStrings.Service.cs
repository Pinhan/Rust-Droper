namespace RustDroper.Core.Localization;

internal static partial class CoreStrings
{
    private static readonly LocEntry[] Service =
    [
        // DropperService: hesap/yenileme hataları (arayüzde görünür)
        L("engine.notStarted", "Başlatılmadı", "Not started"),
        L("service.unreachable", "{0}'e ulaşılamadı: {1}", "Couldn't reach {0}: {1}"),
        L("service.sessionRejected", "{0} oturumu reddedildi", "{0} session was rejected"),
        L("service.refreshFailed", "Veriler yenilenemedi: {0}", "Couldn't refresh data: {0}"),
        L("service.verifyFailed", "Oturum doğrulanamadı: {0}", "Couldn't verify the session: {0}"),
        L("service.inventoryFailed", "Envanter yenilenemedi: {0}", "Couldn't refresh the inventory: {0}"),
        L("service.login.expired.kick", "QR/bağlantı zamanında onaylanmadı; tekrar deneyin.", "The QR/link wasn't confirmed in time; try again."),
        L("service.login.expired.twitch", "Kod zamanında onaylanmadı; tekrar deneyin.", "The code wasn't confirmed in time; try again."),
        L("service.login.denied", "Giriş {0} sayfasında reddedildi.", "Sign-in was denied on the {0} page."),
        L("service.login.revoked", "Oturum {0} tarafından iptal edildi; tekrar giriş yapın.", "The session was revoked by {0}; sign in again."),
        L("service.login.failed", "Giriş başarısız: {0}", "Sign-in failed: {0}"),
    ];
}
