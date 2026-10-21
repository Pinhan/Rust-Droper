namespace RustDroper.Core.Localization;

internal static partial class CoreStrings
{
    private static readonly LocEntry[] Engine =
    [
        // Motor durum iletileri ve "Son olaylar" notları (DropEngine)
        L("engine.starting", "Başlatılıyor", "Starting"),
        L("engine.discovering", "Kampanyalar taranıyor", "Scanning campaigns"),
        L("engine.authInvalid", "Kimlik geçersiz: {0}", "Authentication invalid: {0}"),
        L("engine.authRequired", "Yeniden giriş gerekli", "Sign-in required again"),
        L("engine.discoveryFailed", "Keşif başarısız ({0}): {1}", "Discovery failed ({0}): {1}"),
        L("engine.degraded", "Platforma ulaşılamıyor/şema değişmiş olabilir; {0:0} sn sonra tekrar", "Platform unreachable or its format may have changed; retrying in {0:0} s"),
        L("engine.idle", "Uygun kampanya yok; {0:0} dk sonra tekrar bakılacak", "No suitable campaign; will check again in {0:0} min"),
        L("engine.watching", "İzleniyor: {0} → {1}", "Watching: {0} → {1}"),
        L("engine.channelOffline", "{0} yayını kapandı; başka kanala geçiliyor", "{0} went offline; switching to another channel"),
        L("engine.progressUnread", "İlerleme okunamadı: {0}", "Couldn't read progress: {0}"),
        L("engine.progressLost", "İlerleme art arda okunamadı; keşfe dönülüyor", "Progress couldn't be read repeatedly; going back to discovery"),
        L("engine.campaignDoneThreshold", "Kampanya tamamlandı (son drop eşiği aşıldı): {0}", "Campaign completed (last drop threshold passed): {0}"),
        L("engine.campaignDone", "Kampanya tamamlandı: {0}", "Campaign completed: {0}"),
        L("engine.dropCompleted", "Drop tamamlandı; claim bekleniyor", "Drop completed; waiting to claim"),
        L("engine.progress", "{0}: {1}/{2} dk", "{0}: {1}/{2} min"),
        L("engine.foreignSession",
            "{0}: ilerleme durdu; hesap başka bir yerden izleniyor olabilir — aynı kanalda izleme yeniden başlatılıyor",
            "{0}: progress stopped; the account may be watching somewhere else — restarting the watch on the same channel"),
        L("engine.channelBlocked", "{0} kanalında ilerleme yok; kanal {1:0} dk engellendi", "No progress on {0}; channel blocked for {1:0} min"),
        L("engine.campaignCooldown",
            "'{0}' kampanyasında ilerleme alınamıyor (hesap bağlı olmayabilir); {1:0} dk beklemeye alındı",
            "Can't get progress on '{0}' (the account may not be linked); put on hold for {1:0} min"),
        L("engine.remembered", "Önceki çalışmadan hatırlandı: {0} tamamlanan, {1} bekletilen kampanya", "Remembered from the previous run: {0} completed, {1} on-hold campaigns"),
        L("engine.priorityFound", "Öncelikli kampanya bulundu: {0}", "Higher-priority campaign found: {0}"),
        L("engine.background", "Arka plan işi durdu: {0}", "Background task stopped: {0}"),
        L("engine.claimed", "Drop claim edildi ({0})", "Drop claimed ({0})"),
        L("engine.claimFailed", "Drop claim EDİLEMEDİ: {0}. {1} envanterinden elle claim edebilirsiniz.", "Drop could NOT be claimed: {0}. You can claim it manually from your {1} inventory."),
    ];
}
