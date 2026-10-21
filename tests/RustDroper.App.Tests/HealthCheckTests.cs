using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Watching;

namespace RustDroper.App.Tests;

public class HealthCheckTests
{
    private static DropperSnapshot Snap(AccountStatus status = AccountStatus.LoggedIn, bool running = false, bool online = true, EngineState state = EngineState.Stopped) =>
        new(new AccountSnapshot(status, status == AccountStatus.LoggedIn ? "kullanici" : null),
            new EngineSnapshot(state, "ileti", null, null, null, 0, []),
            running,
            null,
            null,
            new HashSet<string>(),
            null,
            online);

    private static IReadOnlyList<HealthItem> Build(Platform platform, DropperSnapshot snapshot, AppSettings? settings = null, bool crash = true, KickPlayerSessionStatus? player = null) =>
        HealthChecker.Build(new HealthInput(platform, snapshot, settings ?? new AppSettings(), crash, player));

    [Fact]
    public void LoggedOutAccount_IsAFailure_AndOverallIsFail()
    {
        var items = Build(Platform.Twitch, Snap(AccountStatus.LoggedOut));
        Assert.Equal(HealthLevel.Fail, items.Single(i => i.Title == "Twitch girişi").Level);
        Assert.Equal(HealthLevel.Fail, HealthChecker.Overall(items));
    }

    [Fact]
    public void AutoClaimOff_IsAWarning_AndOnIsOk()
    {
        Assert.Equal(HealthLevel.Warn, Build(Platform.Twitch, Snap(), new AppSettings { AutoClaim = false }).Single(i => i.Title == "Otomatik claim").Level);
        Assert.Equal(HealthLevel.Ok, Build(Platform.Twitch, Snap(), new AppSettings()).Single(i => i.Title == "Otomatik claim").Level);
    }

    [Fact]
    public void Offline_IsAFailure()
    {
        Assert.Equal(HealthLevel.Fail, Build(Platform.Twitch, Snap(online: false)).Single(i => i.Title == "İnternet").Level);
    }

    [Fact]
    public void KickPlayerSessionMissing_IsAFailure_OnlyOnKickScreen()
    {
        var kick = Build(Platform.Kick, Snap(), player: new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut));
        Assert.Equal(HealthLevel.Fail, kick.Single(i => i.Title == "Oynatıcı oturumu").Level);
        Assert.DoesNotContain(Build(Platform.Twitch, Snap()), i => i.Title == "Oynatıcı oturumu");
    }

    [Fact]
    public void EachScreenMentionsOnlyItsOwnPlatform()
    {
        var twitch = string.Join(' ', Build(Platform.Twitch, Snap()).Select(i => i.Title + " " + i.Detail));
        var kick = string.Join(' ', Build(Platform.Kick, Snap(), player: new KickPlayerSessionStatus(KickPlayerSessionState.LoggedIn)).Select(i => i.Title + " " + i.Detail));
        Assert.DoesNotContain("kick", twitch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("twitch", kick, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kick girişi", kick, StringComparison.Ordinal);
        Assert.Contains("Twitch girişi", twitch, StringComparison.Ordinal);
    }

    [Fact]
    public void CrashProtection_ReflectsSettingAndAvailability()
    {
        Assert.Equal(HealthLevel.Ok, Build(Platform.Twitch, Snap()).Single(i => i.Title == "Çökme koruması").Level);
        Assert.Equal(HealthLevel.Warn, Build(Platform.Twitch, Snap(), crash: false).Single(i => i.Title == "Çökme koruması").Level);
        Assert.Equal(HealthLevel.Warn, Build(Platform.Twitch, Snap(), new AppSettings { AutoRestart = false }).Single(i => i.Title == "Çökme koruması").Level);
    }

    [Fact]
    public void DegradedEngine_IsAWarning_AuthRequiredIsAFailure()
    {
        Assert.Equal(HealthLevel.Warn, Build(Platform.Twitch, Snap(running: true, state: EngineState.Degraded)).Single(i => i.Title == "Motor").Level);
        Assert.Equal(HealthLevel.Fail, Build(Platform.Twitch, Snap(running: true, state: EngineState.AuthRequired)).Single(i => i.Title == "Motor").Level);
        Assert.Equal(HealthLevel.Info, Build(Platform.Twitch, Snap()).Single(i => i.Title == "Motor").Level);
    }

    [Fact]
    public void ExcludedNonRustCampaigns_AreMentionedWithTheEscapeHatch()
    {
        var snapshot = Snap() with
        {
            Campaigns = new PlatformSnapshot([], new Dictionary<string, IReadOnlyList<LiveChannel>>(), ["SpookyDragon 2026"]),
        };
        var item = Build(Platform.Twitch, snapshot).Single(i => i.Title == "Kampanya keşfi");
        Assert.Contains("SpookyDragon 2026", item.Detail, StringComparison.Ordinal);
        Assert.Contains("Yalnız Rust ödülü veren kampanyalar", item.Detail, StringComparison.Ordinal);
    }
}
