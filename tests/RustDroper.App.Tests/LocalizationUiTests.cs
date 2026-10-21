using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Localization;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;

namespace RustDroper.App.Tests;

/// <summary>İngilizce görünüm modeli metinleri (dil yalnız bu testin akışı için <see cref="Loc.Use"/> ile seçilir).</summary>
public class EnglishViewModelTests
{
    private static readonly Campaign Base = Dx.Campaign("c1", "Base Invaders", CampaignScope.General, null, Dx.Now.AddDays(9), null, null, ("d1", 60), ("d2", 180));

    [Fact]
    public void Dashboard_NowSection_InEnglish()
    {
        using var _ = Loc.Use("en");
        var channel = Dx.Channel("42", "twitchrivals");
        var engine = Dx.Engine(EngineState.Watching, "watching", new Target(Base, channel), new DropSession("d1", 30, 60), Dx.Now.AddSeconds(-12));
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(engine, campaigns: Dx.Snapshot((Base, [channel]))));

        var now = dashboard.Now;
        Assert.Equal("Watching", now.BadgeText);
        Assert.Equal("50%", now.PercentText);
        Assert.Equal("30 min", now.WatchedText);
        Assert.Equal("1 h", now.RequiredText);
        Assert.Equal("30 min", now.RemainingText);
        Assert.Equal("today 21:00", now.FinishText);
        Assert.Equal("12 s ago", now.LastProgressText);
        Assert.StartsWith("Last check ", now.InfoText, StringComparison.Ordinal);
        Assert.Contains("every 6 h", now.InfoText, StringComparison.Ordinal);
        Assert.Contains("every 60 s", now.InfoTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_Cards_InEnglish()
    {
        using var _ = Loc.Use("en");
        var channel = Dx.Channel("42", "a");
        var inventory = new TwitchInventorySnapshot([], [Dx.Progress("c1", "d1", 60, 60, claimed: true)]);
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(EngineState.Idle, "idle"), campaigns: Dx.Snapshot((Base, [channel])), inventory: inventory));

        var campaign = dashboard.GeneralCampaigns.Single();
        Assert.Equal("Active", campaign.StatusText);
        Assert.Equal("ends: Nov 1, 18:00", campaign.TimeText);
        Assert.Equal("1/2 claimed", campaign.SummaryText);
        Assert.Equal("1 live channel can be watched", campaign.WatchableText);
        Assert.Equal("Claimed ✔", campaign.Drops[0].StateText);
        Assert.Equal("Queued", campaign.Drops[1].StateText);
        Assert.Equal("2 drops", dashboard.GeneralCountText);
        Assert.Equal("Total: 1/2 claimed", dashboard.TotalsText);
        Assert.Equal("3 h left", campaign.Drops[1].RemainingText);
    }

    [Fact]
    public void EventInfo_Countdown_InEnglish()
    {
        using var _ = Loc.Use("en");
        var vm = new EventInfoViewModel();
        var data = new EventData("Base Invaders", "Twitch Drops · 54", Dx.Now.AddDays(3).AddHours(9), Dx.Now.AddDays(12), null);

        vm.Update(data, Dx.Now, TimeZoneInfo.Utc);

        Assert.Equal("Upcoming", vm.StatusText);
        Assert.Equal("Starts in 3 days 9 h", vm.CountdownText);
        Assert.Equal("Oct 27, 03:00 → Nov 4, 18:00", vm.DatesText);
    }

    [Fact]
    public void EventInfo_Active_And_HostedBy_InEnglish()
    {
        using var _ = Loc.Use("en");
        var vm = new EventInfoViewModel();
        vm.Update(new EventData("X", null, Dx.Now.AddDays(-1), Dx.Now.AddHours(5), null), Dx.Now, TimeZoneInfo.Utc);
        Assert.Equal("Active", vm.StatusText);
        Assert.Equal("Ends in 5 h", vm.CountdownText);
        var detail = EventInfoViewModel.DetailFrom(new RustDroper.Core.Media.FacepunchEvent("T", ["Twitch Drops", "54"], "Hosted by Rustoria", null, null, null, null));
        Assert.Equal("Twitch Drops · 54 · Hosted by Rustoria", detail);
    }

    [Fact]
    public void PlatformCard_InEnglish()
    {
        using var _ = Loc.Use("en");
        var login = new RelayCommand(_ => { });
        var card = new PlatformCardViewModel("Twitch", login, login, login, login);

        Assert.Equal("Checking…", card.StatusText);
        card.Update(new AccountSnapshot(AccountStatus.LoggedIn, "testuser"));
        Assert.Equal("Connected: testuser", card.StatusText);
        Assert.Equal("Sign out", card.ActionLabel);
        Assert.Equal("IconLogOut", card.ActionIcon);
        card.Update(new AccountSnapshot(AccountStatus.NeedsLogin));
        Assert.Equal("Sign in again", card.ActionLabel);
        Assert.Equal("IconLogIn", card.ActionIcon);
        card.Update(new AccountSnapshot(AccountStatus.Offline, "a"));
        Assert.Equal("Retry", card.ActionLabel);
        Assert.Equal("IconRefresh", card.ActionIcon);
        card.Update(new AccountSnapshot(AccountStatus.LoggedOut));
        Assert.Equal("Sign in", card.ActionLabel);
        Assert.Equal("Open twitch.tv/activate ↗", card.LoginLinkText);
        Assert.Equal("Open twitch.facepunch.com in the browser", card.SiteTooltip);
    }

    [Fact]
    public void PlatformCard_Kick_PlayerSession_InEnglish()
    {
        using var _ = Loc.Use("en");
        var cmd = new RelayCommand(_ => { });
        var card = new PlatformCardViewModel("Kick", cmd, cmd, cmd, cmd, Platform.Kick, playerLogin: cmd, playerRefresh: cmd);

        card.UpdatePlayerSession(new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut));

        Assert.Equal("Player session: missing — use 'Player sign-in' to start watching", card.PlayerSessionText);
        card.Update(new AccountSnapshot(AccountStatus.LoggingIn));
        Assert.Contains("scan the QR code", card.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Twitch", card.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Tooltips_InEnglish_FitTheTrayLimit()
    {
        using var _ = Loc.Use("en");
        var engine = new EngineSnapshot(EngineState.Idle, "x", null, null, null, 0, []);
        DropperSnapshot Snap(AccountSnapshot account, bool running = false, EngineSnapshot? e = null, bool online = true) =>
            new(account, e ?? engine, running, null, null, new HashSet<string>(), null, online);

        Assert.Equal("Rust Droper — not signed in to Twitch", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.LoggedOut))));
        Assert.Equal("Rust Droper — offline", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.Offline))));
        Assert.Equal("Rust Droper — Idle", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.LoggedIn, "a"), running: true)));
        Assert.Equal("Rust Droper — paused (a)", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.LoggedIn, "a"))));
        Assert.Equal("Rust Droper — no internet connection", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.LoggedIn, "a"), online: false)));
        Assert.Equal("Rust Droper — sign-in required", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.NeedsLogin))));

        var channel = Dx.Channel("42", "averyveryverylongstreamername");
        var watching = Dx.Engine(EngineState.Watching, "x", new Target(Base, channel), new DropSession("d1", 123, 180), Dx.Now);
        var twitch = Snap(new AccountSnapshot(AccountStatus.LoggedIn, "a"), running: true, e: watching);
        Assert.Equal("Rust Droper — AVERYVERYVERYLONGSTREAMERNAME: 123/180 min (68%)", MainViewModel.BuildTooltip(twitch));
        var combined = MainViewModel.BuildTooltip(twitch, Snap(new AccountSnapshot(AccountStatus.LoggedIn, "k"), running: true, e: watching));
        Assert.True(combined.Length <= 127, combined);
        Assert.StartsWith("Rust Droper — Twitch: ", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_InEnglish_KeepsPlatformSeparation()
    {
        using var _ = Loc.Use("en");
        var snapshot = new DropperSnapshot(
            new AccountSnapshot(AccountStatus.LoggedIn, "me"),
            new EngineSnapshot(EngineState.Idle, "idle", null, null, null, 0, []),
            false, null, null, new HashSet<string>(), null);

        var twitch = HealthChecker.Build(new HealthInput(Platform.Twitch, snapshot, new AppSettings { AutoClaim = false }, true, null));
        var kick = HealthChecker.Build(new HealthInput(Platform.Kick, snapshot, new AppSettings(), true, new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut)));

        Assert.Equal("Internet", twitch[0].Title);
        Assert.Equal("Connected.", twitch[0].Detail);
        Assert.Equal("Twitch sign-in", twitch[1].Title);
        Assert.Equal("Connected: me", twitch[1].Detail);
        Assert.Contains(twitch, i => i.Title == "Auto-claim" && i.Detail.StartsWith("OFF:", StringComparison.Ordinal) && i.Level == HealthLevel.Warn);
        Assert.DoesNotContain("kick", string.Join(' ', twitch.Select(i => i.Title + " " + i.Detail)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("twitch", string.Join(' ', kick.Select(i => i.Title + " " + i.Detail)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(kick, i => i.Title == "Kick sign-in");
        Assert.Contains(kick, i => i.Title == "Player session" && i.Level == HealthLevel.Fail && i.Detail.Contains("Player sign-in", StringComparison.Ordinal));
        Assert.Contains(twitch, i => i.Title == "Campaign discovery" && i.Detail == "Not read yet.");
    }

    [Fact]
    public void Health_CampaignCounts_UsePlurals()
    {
        using var _ = Loc.Use("en");
        var one = new PlatformSnapshot([Base], new Dictionary<string, IReadOnlyList<LiveChannel>>());
        var snapshot = new DropperSnapshot(new AccountSnapshot(AccountStatus.LoggedIn, "me"), new EngineSnapshot(EngineState.Idle, "i", null, null, null, 0, []), false, one, null, new HashSet<string>(), null);

        var items = HealthChecker.Build(new HealthInput(Platform.Twitch, snapshot, new AppSettings(), true, null));

        Assert.Equal("1 campaign found.", items.Single(i => i.Title == "Campaign discovery").Detail);
    }
}

/// <summary>Dil değişince canlı görünüm modellerinin yenilenmesi (küresel dil → paralel çalışmaz).</summary>
[Collection("Loc global")]
public class LanguageRefreshTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StubAccount _account = new();
    private readonly StubPlatform _platform = new();
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new(Dx.Now);

    public LanguageRefreshTests() => Loc.Configure("tr");

    public void Dispose()
    {
        Loc.Configure("tr");
        _dir.Dispose();
    }

    [Fact]
    public async Task MainViewModel_RefreshesEverythingWhenLanguageChanges()
    {
        var settings = new SettingsService(_dir.File("settings.json"));
        using var service = new DropperService(_account, new StubFactory(_platform), settings, engineOptions: s => new EngineOptions { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(20) });
        var campaign = Dx.Campaign("c1", "Base Invaders", CampaignScope.General, null, Dx.Now.AddYears(5), null, null, ("d1", 60));
        _platform.Snapshot = Dx.Snapshot((campaign, [Dx.Channel("1", "streamer")]));
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, new TwitchSession("tok", "42", "testuser", null));
        var vm = new MainViewModel(settings, new FakeAutostart(), new RecordingBrowser(), service, new ImmediateDispatcher(), _time, TimeZoneInfo.Utc);
        await vm.InitializeAsync();
        vm.IsWindowVisible = true;
        vm.ToggleHealthCommand.Execute(null);
        Assert.Equal("Başlat", vm.StartPauseLabel);
        Assert.Equal("Bağlı: testuser", vm.Twitch.StatusText);
        Assert.Equal("Rust Droper — duraklatıldı (testuser)", vm.TrayTooltip);
        Assert.Equal("İnternet", vm.HealthItems[0].Title);
        Assert.Equal("Aktif", vm.Dashboard.GeneralCampaigns.Single().StatusText);

        vm.Settings.Language = "en";

        Assert.Equal("Start", vm.StartPauseLabel);
        Assert.Equal("Connected: testuser", vm.Twitch.StatusText);
        Assert.Equal("Rust Droper — paused (testuser)", vm.TrayTooltip);
        Assert.Equal("Internet", vm.HealthItems[0].Title);
        Assert.DoesNotContain("uyarı", vm.HealthSummary, StringComparison.Ordinal);
        Assert.Equal("Active", vm.Dashboard.GeneralCampaigns.Single().StatusText);
        Assert.Equal("← Dashboard", vm.HealthButtonLabel);
        Assert.Equal("Sign out", vm.Twitch.ActionLabel);
        Assert.StartsWith("Version ", vm.About.VersionText, StringComparison.Ordinal);

        vm.Settings.Language = "tr";

        Assert.Equal("Başlat", vm.StartPauseLabel);
        Assert.Equal("Çıkış yap", vm.Twitch.ActionLabel);
        Assert.Equal("Aktif", vm.Dashboard.GeneralCampaigns.Single().StatusText);
    }
}
