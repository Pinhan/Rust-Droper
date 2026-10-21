using RustDroper.Core.Localization;

namespace RustDroper.App.Localization;

internal static partial class AppStrings
{
    private static readonly LocEntry[] Fmt =
    [
        // süre / zaman (Fmt)
        L("fmt.min", "{0} dk", "{0} min"),
        L("fmt.hour", "{0} sa", "{0} h"),
        L("fmt.hourMin", "{0} sa {1} dk", "{0} h {1} min"),
        L("fmt.day.one", "{0} gün", "{0} day"),
        L("fmt.day.other", "{0} gün", "{0} days"),
        L("fmt.dayHour.one", "{0} gün {1} sa", "{0} day {1} h"),
        L("fmt.dayHour.other", "{0} gün {1} sa", "{0} days {1} h"),
        L("fmt.ago.now", "az önce", "just now"),
        L("fmt.ago.sec", "{0} sn önce", "{0} s ago"),
        L("fmt.ago.min", "{0} dk önce", "{0} min ago"),
        L("fmt.ago.hour", "{0} sa önce", "{0} h ago"),
        L("fmt.when.today", "bugün {0}", "today {0}"),
        L("fmt.when.tomorrow", "yarın {0}", "tomorrow {0}"),
        L("fmt.date.short", "d MMM HH:mm", "MMM d, HH:mm"),
        L("fmt.date.year", "d MMM yyyy HH:mm", "MMM d, yyyy HH:mm"),
        L("fmt.date.dayYear", "d MMM yyyy", "MMM d, yyyy"),
        L("fmt.time.hms", "HH:mm:ss", "HH:mm:ss"),
        L("fmt.time.hm", "HH:mm", "HH:mm"),
        L("fmt.percent", "%{0:0}", "{0:0}%"),

        // motor durumu rozetleri
        L("badge.stopped", "Durduruldu", "Stopped"),
        L("badge.starting", "Başlatılıyor", "Starting"),
        L("badge.discovering", "Kampanyalar taranıyor", "Scanning campaigns"),
        L("badge.idle", "Boşta", "Idle"),
        L("badge.watching", "İzleniyor", "Watching"),
        L("badge.claimPending", "Claim bekleniyor", "Waiting to claim"),
        L("badge.degraded", "Sorun — yeniden deneniyor", "Problem — retrying"),
        L("badge.authRequired", "Yeniden giriş gerekli", "Sign-in required again"),
    ];
}
