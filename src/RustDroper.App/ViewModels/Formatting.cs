using System.Globalization;
using RustDroper.Core.Localization;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.ViewModels;

/// <summary>Süre/zaman biçimleri ve rozet eşlemeleri (saf, test edilebilir; geçerli arayüz diline göre — <see cref="Loc"/>).</summary>
public static class Fmt
{
    /// <summary>Geçerli arayüz dilinin kültürü (tarih/saat biçimleri).</summary>
    public static CultureInfo Tr => Loc.Culture;

    /// <summary>"%37" (Türkçe) / "37%" (İngilizce); değer 0 ondalık basamağa yuvarlanır.</summary>
    public static string Percent(double percent) => Loc.T("fmt.percent", percent);

    public static string Minutes(int minutes)
    {
        minutes = Math.Max(0, minutes);
        if (minutes < 60)
        {
            return Loc.T("fmt.min", minutes);
        }

        return minutes % 60 == 0 ? Loc.T("fmt.hour", minutes / 60) : Loc.T("fmt.hourMin", minutes / 60, minutes % 60);
    }

    public static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(5))
        {
            return Loc.T("fmt.ago.now");
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return Loc.T("fmt.ago.sec", (int)elapsed.TotalSeconds);
        }

        return elapsed < TimeSpan.FromHours(1) ? Loc.T("fmt.ago.min", (int)elapsed.TotalMinutes) : Loc.T("fmt.ago.hour", (int)elapsed.TotalHours);
    }

    /// <summary>"bugün 23:40", "yarın 02:10", "24 Eki 14:30" / "today 23:40", "tomorrow 02:10", "Oct 24, 14:30" (yerel saat).</summary>
    public static string When(DateTimeOffset moment, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(moment, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = local.ToString("HH:mm", Loc.Culture);
        if (local.Date == today)
        {
            return Loc.T("fmt.when.today", time);
        }

        return local.Date == today.AddDays(1) ? Loc.T("fmt.when.tomorrow", time) : local.ToString(Loc.T("fmt.date.short"), Loc.Culture);
    }

    /// <summary>Kalan süre: "4 gün 3 sa", "5 sa 20 dk", "12 dk" (negatifse "0 dk"); saniye gösterilmez. İngilizce: "4 days 3 h", "5 h 20 min", "12 min".</summary>
    public static string Remaining(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var days = (int)span.TotalDays;
        if (days >= 1)
        {
            return span.Hours > 0
                ? Loc.Plural("fmt.dayHour", days, days, span.Hours)
                : Loc.Plural("fmt.day", days, days);
        }

        if (span.Hours >= 1)
        {
            return span.Minutes > 0 ? Loc.T("fmt.hourMin", span.Hours, span.Minutes) : Loc.T("fmt.hour", span.Hours);
        }

        return Loc.T("fmt.min", Math.Max(1, span.Minutes));
    }

    /// <summary>"23 Eki 21:00 → 2 Kas 02:59" / "Oct 23, 21:00 → Nov 2, 02:59" (yerel saat; yıl yalnız bugünkünden farklıysa).</summary>
    public static string DateRange(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now, TimeZoneInfo zone)
    {
        string Format(DateTimeOffset moment)
        {
            var local = TimeZoneInfo.ConvertTime(moment, zone);
            var year = TimeZoneInfo.ConvertTime(now, zone).Year;
            return local.ToString(Loc.T(local.Year == year ? "fmt.date.short" : "fmt.date.year"), Loc.Culture);
        }

        return (start, end) switch
        {
            ({ } s, { } e) => $"{Format(s)} → {Format(e)}",
            ({ } s, null) => $"{Format(s)} →",
            (null, { } e) => $"→ {Format(e)}",
            _ => string.Empty,
        };
    }

    public static (string Text, Severity Severity) EngineBadge(EngineState state, bool running) => (running, state) switch
    {
        (false, _) or (_, EngineState.Stopped) => (Loc.T("badge.stopped"), Severity.Neutral),
        (_, EngineState.Starting) => (Loc.T("badge.starting"), Severity.Info),
        (_, EngineState.Discovering) => (Loc.T("badge.discovering"), Severity.Info),
        (_, EngineState.Idle) => (Loc.T("badge.idle"), Severity.Neutral),
        (_, EngineState.Watching) => (Loc.T("badge.watching"), Severity.Ok),
        (_, EngineState.ClaimPending) => (Loc.T("badge.claimPending"), Severity.Warn),
        (_, EngineState.Degraded) => (Loc.T("badge.degraded"), Severity.Warn),
        (_, EngineState.AuthRequired) => (Loc.T("badge.authRequired"), Severity.Error),
        _ => (state.ToString(), Severity.Neutral),
    };

    public static Severity EventSeverity(string level) => level switch
    {
        "ok" => Severity.Ok,
        "warn" => Severity.Warn,
        "error" => Severity.Error,
        _ => Severity.Info,
    };
}
