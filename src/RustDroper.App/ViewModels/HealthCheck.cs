using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Localization;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Watching;

namespace RustDroper.App.ViewModels;

public enum HealthLevel
{
    /// <summary>Her şey yolunda.</summary>
    Ok,

    /// <summary>Bilgi: sorun değil.</summary>
    Info,

    /// <summary>Dikkat: drop kaçırma ya da beklenmedik davranış riski.</summary>
    Warn,

    /// <summary>Çalışmayı engeller.</summary>
    Fail,
}

public sealed record HealthItem(string Title, string Detail, HealthLevel Level);

/// <summary>Sağlık kontrolünün girdileri (yalnız görüntülenen platforma ait veri).</summary>
public sealed record HealthInput(
    Platform Platform,
    DropperSnapshot Snapshot,
    AppSettings Settings,
    bool CrashProtectionAvailable,
    KickPlayerSessionStatus? KickPlayer);

/// <summary>
/// Sağlık kontrolü: mevcut durum görüntülerinden "23 Ekim'e hazır mıyız" listesi üretir (ağ çağrısı yok, saf). Her platformun ekranında yalnız o platformun
/// maddeleri görünür; genel maddeler (internet, otomatik claim, çökme koruması…) iki ekranda da aynıdır ve diğer platformdan söz etmez.
/// </summary>
public static class HealthChecker
{
    public static IReadOnlyList<HealthItem> Build(HealthInput input)
    {
        var (platform, snapshot, settings) = (input.Platform, input.Snapshot, input.Settings);
        var name = platform == Platform.Kick ? "Kick" : "Twitch";
        var items = new List<HealthItem>
        {
            snapshot.NetworkOnline
                ? new(Loc.T("health.internet"), Loc.T("health.internet.ok"), HealthLevel.Ok)
                : new(Loc.T("health.internet"), Loc.T("health.internet.fail"), HealthLevel.Fail),
            Account(name, snapshot.Account),
        };

        if (platform == Platform.Kick)
        {
            items.Add(Player(input.KickPlayer));
        }
        else
        {
            items.Add(Link(snapshot, "twitch.facepunch.com", Loc.T("health.link.title"), "https://twitch.facepunch.com/connect"));
        }

        items.Add(Campaigns(snapshot));
        items.Add(Engine(snapshot));

        // Otomatik claim yalnız Twitch'te uygulanıyor (Kick claim'i canlıda sınanmadı; ayar iki platformu da yönetir ama uyarı yalnız ilgili ekranda).
        items.Add(settings.AutoClaim
            ? new(Loc.T("health.autoClaim.title"), Loc.T("health.autoClaim.on"), HealthLevel.Ok)
            : new(Loc.T("health.autoClaim.title"), Loc.T("health.autoClaim.off"), HealthLevel.Warn));
        items.Add(settings.KeepAwake
            ? new(Loc.T("health.keepAwake.title"), Loc.T("health.keepAwake.on"), HealthLevel.Ok)
            : new(Loc.T("health.keepAwake.title"), Loc.T("health.keepAwake.off"), HealthLevel.Warn));
        items.Add(settings.AutoRestart && input.CrashProtectionAvailable
            ? new(Loc.T("health.crash.title"), Loc.T("health.crash.on"), HealthLevel.Ok)
            : new(Loc.T("health.crash.title"), Loc.T(settings.AutoRestart ? "health.crash.devRun" : "health.crash.off"), HealthLevel.Warn));
        items.Add(settings.StartWithWindows
            ? new(Loc.T("health.startWin.title"), Loc.T("health.startWin.on"), HealthLevel.Ok)
            : new(Loc.T("health.startWin.title"), Loc.T("health.startWin.off"), HealthLevel.Info));
        return items;
    }

    /// <summary>Genel durum: en kötü seviye.</summary>
    public static HealthLevel Overall(IEnumerable<HealthItem> items) => items.Select(i => i.Level).DefaultIfEmpty(HealthLevel.Ok).Max();

    private static HealthItem Account(string name, AccountSnapshot account)
    {
        var title = Loc.T("health.account.title", name);
        return account.Status switch
        {
            AccountStatus.LoggedIn => new(title, Loc.T("health.account.ok", account.Login), HealthLevel.Ok),
            AccountStatus.LoggingIn => new(title, Loc.T("health.account.loggingIn"), HealthLevel.Warn),
            AccountStatus.NeedsLogin => new(title, Loc.T("health.account.needsLogin"), HealthLevel.Fail),
            AccountStatus.Offline => new(title, Loc.T("health.account.offline"), HealthLevel.Warn),
            AccountStatus.Restoring or AccountStatus.Unknown => new(title, Loc.T("health.account.verifying"), HealthLevel.Info),
            _ => new(title, Loc.T("health.account.loggedOut"), HealthLevel.Fail),
        };
    }

    private static HealthItem Link(DropperSnapshot snapshot, string host, string title, string connectUrl) =>
        snapshot.Inventory?.IsLinkedTo(host) switch
        {
            true => new(title, Loc.T("health.link.ok"), HealthLevel.Ok),
            false => new(title, Loc.T("health.link.no", connectUrl), HealthLevel.Fail),
            _ => new(title, Loc.T("health.link.unknown"), HealthLevel.Warn),
        };

    private static HealthItem Player(KickPlayerSessionStatus? status)
    {
        var title = Loc.T("health.player.title");
        return status?.State switch
        {
            KickPlayerSessionState.LoggedIn => new(title, Loc.T("health.player.ok"), HealthLevel.Ok),
            KickPlayerSessionState.LoggedOut => new(title, Loc.T("health.player.loggedOut"), HealthLevel.Fail),
            KickPlayerSessionState.Unavailable => new(title, Loc.T("health.player.unavailable"), HealthLevel.Fail),
            KickPlayerSessionState.Checking => new(title, Loc.T("health.player.checking"), HealthLevel.Info),
            _ => new(title, Loc.T("health.player.unknown"), HealthLevel.Warn),
        };
    }

    private static HealthItem Campaigns(DropperSnapshot snapshot)
    {
        var title = Loc.T("health.campaigns.title");
        if (snapshot.RefreshError is { Length: > 0 } error)
        {
            return new(title, Loc.T("health.campaigns.error", error), HealthLevel.Warn);
        }

        if (snapshot.Campaigns is null)
        {
            return new(title, Loc.T("health.campaigns.notRead"), HealthLevel.Info);
        }

        var count = snapshot.Campaigns.Campaigns.Count;
        var excluded = snapshot.Campaigns.Excluded;
        var skipped = excluded.Count > 0
            ? Loc.Plural("health.campaigns.skipped", excluded.Count, excluded.Count, string.Join(", ", excluded.Take(3)))
            : string.Empty;
        return count == 0
            ? new(title, Loc.T("health.campaigns.none") + skipped, HealthLevel.Info)
            : new(title, Loc.Plural("health.campaigns.found", count, count) + skipped, HealthLevel.Ok);
    }

    private static HealthItem Engine(DropperSnapshot snapshot)
    {
        var title = Loc.T("health.engine.title");
        return snapshot.IsRunning
            ? snapshot.Engine.State switch
            {
                EngineState.Degraded => new(title, Loc.T("health.engine.degraded", snapshot.Engine.Message), HealthLevel.Warn),
                EngineState.AuthRequired => new(title, Loc.T("health.engine.authRequired"), HealthLevel.Fail),
                _ => new(title, Loc.T("health.engine.running", snapshot.Engine.Message), HealthLevel.Ok),
            }
            : new(title, Loc.T("health.engine.paused"), HealthLevel.Info);
    }
}
