using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

internal static class Dx
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

    public static LiveChannel Channel(string id, string login) =>
        new(Platform.Twitch, id, login, login.ToUpperInvariant(), 100, null, new Uri("https://www.twitch.tv/" + login), null);

    public static Campaign Campaign(
        string id,
        string name,
        CampaignScope scope,
        DateTimeOffset? starts,
        DateTimeOffset? ends,
        IReadOnlyList<Streamer>? streamers = null,
        Uri? connect = null,
        params (string Id, int Minutes)[] drops) =>
        new(
            Platform.Twitch,
            id,
            name,
            "rust",
            "Rust",
            CampaignStatus.Active,
            starts,
            ends,
            scope,
            streamers ?? [],
            [.. drops.Select(d => new DropReward(d.Id, "Ödül " + d.Id, d.Minutes, null))],
            connect);

    public static PlatformSnapshot Snapshot(params (Campaign Campaign, LiveChannel[] Channels)[] items) =>
        new(
            [.. items.Select(i => i.Campaign)],
            items.ToDictionary(i => i.Campaign.Id, i => (IReadOnlyList<LiveChannel>)i.Channels, StringComparer.Ordinal));

    public static EngineSnapshot Engine(
        EngineState state = EngineState.Stopped,
        string message = "Durduruldu",
        Target? target = null,
        DropSession? session = null,
        DateTimeOffset? lastProgress = null,
        params EngineEvent[] events) =>
        new(state, message, target, session, lastProgress, 0, events);

    public static DashboardInput Input(
        EngineSnapshot engine,
        bool running = true,
        PlatformSnapshot? campaigns = null,
        TwitchInventorySnapshot? inventory = null,
        params string[] claimedIds) =>
        new(engine, running, campaigns, inventory, claimedIds.ToHashSet(StringComparer.Ordinal), Now);

    public static DropProgress Progress(string campaign, string drop, int required, int watched, bool claimed = false, string? claimId = null) =>
        new(campaign, campaign, drop, "Ödül " + drop, required, watched, claimed, claimId, true);

    public static DashboardViewModel Dashboard(RecordingBrowser? browser = null, TimeProvider? time = null) =>
        new(browser ?? new RecordingBrowser(), time ?? new FakeTimeProvider(Now), TimeZoneInfo.Utc);
}

public class DashboardNowTests
{
    private static readonly Campaign Base = Dx.Campaign("c1", "Base Invaders", CampaignScope.General, null, Dx.Now.AddDays(9), null, null, ("d1", 60), ("d2", 180));

    [Fact]
    public void NoData_ShowsStoppedBadgeAndEmptyLists()
    {
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(), running: false));

        Assert.Equal("Durduruldu", dashboard.Now.BadgeText);
        Assert.False(dashboard.Now.HasTarget);
        Assert.Empty(dashboard.GeneralCampaigns);
        Assert.Empty(dashboard.StreamerCampaigns);
        Assert.Equal(string.Empty, dashboard.TotalsText); // belirsiz "Henüz kampanya bilgisi yok" metni kaldırıldı
        Assert.False(dashboard.HasClaimed);
    }

    [Fact]
    public void Watching_ShowsChannelCampaignProgressAndEstimate()
    {
        var channel = Dx.Channel("42", "twitchrivals");
        var engine = Dx.Engine(EngineState.Watching, "izleniyor", new Target(Base, channel), new DropSession("d1", 30, 60), Dx.Now.AddSeconds(-12));
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(engine, campaigns: Dx.Snapshot((Base, [channel]))));

        var now = dashboard.Now;
        Assert.Equal("İzleniyor", now.BadgeText);
        Assert.Equal(Severity.Ok, now.BadgeSeverity);
        Assert.True(now.HasTarget);
        Assert.Equal("TWITCHRIVALS", now.ChannelName);
        Assert.Equal(new Uri("https://www.twitch.tv/twitchrivals"), now.ChannelUrl);
        Assert.Equal("Base Invaders", now.CampaignName);
        Assert.Equal(50, now.Percent);
        Assert.Equal("%50", now.PercentText);
        Assert.Equal("30 dk", now.WatchedText);
        Assert.Equal("1 sa", now.RequiredText);
        Assert.Equal("30 dk", now.RemainingText);
        Assert.Equal("Ödül d1", now.DropName);

        // Drop'lar paralel varsayımı: kampanyada en uzun kalan eşik = d2 (180 dk) → 18:00 + 3 sa.
        Assert.Equal("bugün 21:00", now.FinishText);
        Assert.Equal("12 sn önce", now.LastProgressText);
    }

    [Fact]
    public void Stopped_HidesTargetEvenIfSnapshotStillHasOne()
    {
        var channel = Dx.Channel("42", "a");
        var engine = Dx.Engine(EngineState.Watching, "x", new Target(Base, channel), new DropSession("d1", 5, 60), Dx.Now);
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(engine, running: false, campaigns: Dx.Snapshot((Base, [channel]))));

        Assert.False(dashboard.Now.HasTarget);
        Assert.Equal(string.Empty, dashboard.Now.LastProgressText);
        Assert.Equal("Durduruldu", dashboard.Now.BadgeText);
    }

    [Fact]
    public void Tick_AdvancesRelativeProgressText()
    {
        var time = new FakeTimeProvider(Dx.Now);
        var channel = Dx.Channel("42", "a");
        var engine = Dx.Engine(EngineState.Watching, "x", new Target(Base, channel), new DropSession("d1", 5, 60), Dx.Now);
        var dashboard = Dx.Dashboard(time: time);
        dashboard.Update(Dx.Input(engine, campaigns: Dx.Snapshot((Base, [channel]))));

        Assert.Equal("az önce", dashboard.Now.LastProgressText);
        time.Advance(TimeSpan.FromMinutes(3));
        dashboard.Tick();

        Assert.Equal("3 dk önce", dashboard.Now.LastProgressText);
    }

    [Fact]
    public void CompletedDrop_ShowsClaimPendingBadge()
    {
        var channel = Dx.Channel("42", "a");
        var engine = Dx.Engine(EngineState.ClaimPending, "Drop tamamlandı", new Target(Base, channel), new DropSession("d1", 60, 60), Dx.Now);
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(engine, campaigns: Dx.Snapshot((Base, [channel]))));

        Assert.Equal("Claim bekleniyor", dashboard.Now.BadgeText);
        Assert.Equal(Severity.Warn, dashboard.Now.BadgeSeverity);
        Assert.Equal("%100", dashboard.Now.PercentText);
        Assert.Equal("Claim bekleniyor", dashboard.GeneralCampaigns.Single().Drops[0].StateText);
    }

    [Theory]
    [InlineData(EngineState.Idle, "Boşta", Severity.Neutral)]
    [InlineData(EngineState.Degraded, "Sorun — yeniden deneniyor", Severity.Warn)]
    [InlineData(EngineState.AuthRequired, "Yeniden giriş gerekli", Severity.Error)]
    [InlineData(EngineState.Discovering, "Kampanyalar taranıyor", Severity.Info)]
    public void EngineStates_MapToBadges(EngineState state, string text, Severity severity)
    {
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(state, "m")));

        Assert.Equal(text, dashboard.Now.BadgeText);
        Assert.Equal(severity, dashboard.Now.BadgeSeverity);
        Assert.Equal("m", dashboard.Now.Message);
    }
}

public class DashboardCampaignTests
{
    [Fact]
    public void GeneralAndStreamerCampaigns_AreListedSeparately_ExpiredHidden()
    {
        var general = Dx.Campaign("g", "Genel", CampaignScope.General, null, Dx.Now.AddDays(5), null, null, ("g1", 60));
        var streamer = Dx.Campaign("s", "Yayıncı", CampaignScope.Streamer, null, Dx.Now.AddDays(2), [new Streamer("1", "tr", "TR", new Uri("https://www.twitch.tv/tr"), null)], null, ("s1", 30));
        var expired = Dx.Campaign("e", "Bitti", CampaignScope.General, null, Dx.Now.AddDays(-1), null, null, ("e1", 30));
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(), campaigns: Dx.Snapshot((general, []), (streamer, []), (expired, []))));

        Assert.Equal(["Genel"], dashboard.GeneralCampaigns.Select(c => c.Name));
        Assert.Equal(["Yayıncı"], dashboard.StreamerCampaigns.Select(c => c.Name));
        Assert.True(dashboard.HasGeneral);
        Assert.True(dashboard.HasStreamer);
    }

    [Fact]
    public void UpcomingCampaign_ShowsStartTime()
    {
        var upcoming = Dx.Campaign("u", "Yakında", CampaignScope.General, Dx.Now.AddDays(1), Dx.Now.AddDays(9), null, null, ("u1", 60));
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(), campaigns: Dx.Snapshot((upcoming, []))));

        var item = dashboard.GeneralCampaigns.Single();
        Assert.Equal("Yaklaşıyor", item.StatusText);
        Assert.Equal("başlangıç: yarın 18:00", item.TimeText);
    }

    [Fact]
    public void StreamerCampaign_MarksLiveStreamerAndKeepsProfileLink()
    {
        var streamers = new[]
        {
            new Streamer("1", "alpha", "Alpha", new Uri("https://www.twitch.tv/alpha"), null),
            new Streamer("2", "beta", "Beta", new Uri("https://www.twitch.tv/beta"), null),
        };
        var campaign = Dx.Campaign("s", "Yayıncı", CampaignScope.Streamer, null, Dx.Now.AddDays(2), streamers, null, ("s1", 30));
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(), campaigns: Dx.Snapshot((campaign, [Dx.Channel("1", "alpha")]))));

        var links = dashboard.StreamerCampaigns.Single().Streamers;
        Assert.True(links.Single(l => l.Name == "Alpha").IsLive);
        Assert.False(links.Single(l => l.Name == "Beta").IsLive);
        Assert.Equal(new Uri("https://www.twitch.tv/beta"), links.Single(l => l.Name == "Beta").Url);
    }

    [Fact]
    public void DropRows_ReflectInventoryClaimsAndLiveSession()
    {
        var campaign = Dx.Campaign("c", "Kampanya", CampaignScope.General, null, Dx.Now.AddDays(9), null, null, ("a", 60), ("b", 120), ("c", 180), ("d", 240));
        var channel = Dx.Channel("42", "x");
        var inventory = new TwitchInventorySnapshot(
            [],
            [
                Dx.Progress("c", "a", 60, 60, claimed: true),
                Dx.Progress("c", "b", 120, 120, claimId: "inst-1"),
                Dx.Progress("c", "c", 180, 30),
            ]);
        var engine = Dx.Engine(EngineState.Watching, "x", new Target(campaign, channel), new DropSession("c", 45, 180), Dx.Now);
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(engine, campaigns: Dx.Snapshot((campaign, [channel])), inventory: inventory));

        var item = dashboard.GeneralCampaigns.Single();
        var rows = item.Drops.ToDictionary(r => r.Id);
        Assert.True(item.IsCurrent);
        Assert.Equal("Claim edildi ✔", rows["a"].StateText);
        Assert.Equal(100, rows["a"].Percent);
        Assert.Equal("Claim edilebilir", rows["b"].StateText);
        Assert.Equal("İzleniyor", rows["c"].StateText);
        Assert.Equal(25, rows["c"].Percent); // canlı oturum (45/180) envanterden (30) yeni
        Assert.True(rows["c"].IsCurrent);
        Assert.Equal("kalan 2 sa 15 dk", rows["c"].RemainingText);
        Assert.Equal("Sırada", rows["d"].StateText);
        Assert.Equal("1/4 claim edildi", item.SummaryText);
        Assert.Equal("Toplam: 1/4 claim edildi", dashboard.TotalsText);
    }

    [Fact]
    public void ClaimedIdsFromHistory_MarkRowsClaimed()
    {
        var campaign = Dx.Campaign("c", "K", CampaignScope.General, null, Dx.Now.AddDays(9), null, null, ("a", 60), ("b", 120));
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(), true, Dx.Snapshot((campaign, [])), null, "a"));

        Assert.Equal("Toplam: 1/2 claim edildi", dashboard.TotalsText);
        Assert.True(dashboard.GeneralCampaigns.Single().Drops.Single(r => r.Id == "a").IsClaimed);
    }

    [Fact]
    public void GeneralCampaign_ShowsWatchableChannelCount()
    {
        var campaign = Dx.Campaign("c", "K", CampaignScope.General, null, Dx.Now.AddDays(9), null, null, ("a", 60));
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(), campaigns: Dx.Snapshot((campaign, [Dx.Channel("1", "a"), Dx.Channel("2", "b")]))));

        Assert.Equal("2 canlı kanal izlenebilir", dashboard.GeneralCampaigns.Single().WatchableText);
    }
}

public class DashboardRewardsAndEventsTests
{
    private static ClaimedReward Reward(string id, bool? linked, string? link = "https://twitch.facepunch.com/connect", int count = 1, int daysAgo = 1) =>
        new(Platform.Twitch, id, "Ödül " + id, Dx.Now.AddDays(-daysAgo), count, linked, link is null ? null : new Uri(link), null);

    [Theory]
    [InlineData(true, "Bağlı: Facepunch/Steam Hesabı", Severity.Ok)]
    [InlineData(false, "Bağlı değil: Facepunch/Steam Hesabı", Severity.Error)]
    [InlineData(null, "Facepunch/Steam: bilinmiyor", Severity.Neutral)]
    public void FacepunchLinkStatus_IsReadFromInventory(bool? linked, string text, Severity severity)
    {
        var inventory = new TwitchInventorySnapshot([Reward("r1", linked)], []);
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(), inventory: inventory));

        Assert.Equal(text, dashboard.LinkStatusText);
        Assert.Equal(severity, dashboard.LinkSeverity);
        Assert.Equal(new Uri("https://twitch.facepunch.com/connect"), dashboard.ConnectUrl);
    }

    [Fact]
    public void ClaimedRewards_ListedWithCountAndDate_OverflowSummarized()
    {
        var rewards = Enumerable.Range(1, 15).Select(i => Reward("r" + i, true, count: i == 1 ? 3 : 1, daysAgo: i)).ToList();
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(), inventory: new TwitchInventorySnapshot(rewards, [])));

        Assert.True(dashboard.HasClaimed);
        Assert.Equal(5, dashboard.ClaimedRewards.Count); // görsel kartlar: son 5
        Assert.Equal("Ödül r1", dashboard.ClaimedRewards[0].Name);
        Assert.Equal("×3 · 22 Eki 2026", dashboard.ClaimedRewards[0].DetailText);
        Assert.Equal("… ve 10 ödül daha", dashboard.ClaimedMoreText);
    }

    [Fact]
    public void NoInventory_FallsBackToDefaultConnectUrl()
    {
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine()));

        Assert.Equal(new Uri("https://twitch.facepunch.com/connect"), dashboard.ConnectUrl);
        Assert.Equal("Facepunch/Steam: bilinmiyor", dashboard.LinkStatusText);
    }

    [Fact]
    public void Events_AreNewestFirst_WithSeverity()
    {
        var events = new[]
        {
            new EngineEvent(Dx.Now.AddMinutes(-2), "info", "İzleniyor: a"),
            new EngineEvent(Dx.Now.AddMinutes(-1), "warn", "İlerleme yok"),
            new EngineEvent(Dx.Now, "ok", "Drop claim edildi"),
        };
        var dashboard = Dx.Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine(events: events)));

        Assert.Equal(["Drop claim edildi", "İlerleme yok", "İzleniyor: a"], dashboard.Events.Select(e => e.Text));
        Assert.Equal([Severity.Ok, Severity.Warn, Severity.Info], dashboard.Events.Select(e => e.Severity));
        Assert.Equal("18:00:00", dashboard.Events[0].TimeText);
        Assert.True(dashboard.HasEvents);
    }

    [Fact]
    public void OpenUrlCommand_UsesBrowser_AndIgnoresNonUriParameters()
    {
        var browser = new RecordingBrowser();
        var dashboard = Dx.Dashboard(browser);

        dashboard.OpenUrlCommand.Execute(new Uri("https://www.twitch.tv/x"));
        dashboard.OpenUrlCommand.Execute("not a uri");
        dashboard.OpenUrlCommand.Execute(null);

        Assert.Equal([new Uri("https://www.twitch.tv/x")], browser.Opened);
    }
}

public class PlatformCardTests
{
    private static (PlatformCardViewModel Card, RelayCommand Login, RelayCommand Logout, RelayCommand Cancel, RelayCommand Retry) Create()
    {
        RelayCommand Cmd() => new(_ => { });
        var (login, logout, cancel, retry) = (Cmd(), Cmd(), Cmd(), Cmd());
        return (new PlatformCardViewModel("Twitch", login, logout, cancel, retry), login, logout, cancel, retry);
    }

    [Fact]
    public void LoggedIn_ShowsUserAndLogoutAction()
    {
        var (card, _, logout, _, _) = Create();

        card.Update(new AccountSnapshot(AccountStatus.LoggedIn, "testuser"));

        Assert.Equal("Bağlı: testuser", card.StatusText);
        Assert.Equal(Severity.Ok, card.Severity);
        Assert.Equal("Çıkış yap", card.ActionLabel);
        Assert.Same(logout, card.ActionCommand);
    }

    [Fact]
    public void LoggingIn_ShowsDeviceCodeAndCancel()
    {
        var (card, _, _, cancel, _) = Create();
        var code = new TwitchDeviceCode("dev", "ABCD-EFGH", new Uri("https://www.twitch.tv/activate"), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5));

        card.Update(new AccountSnapshot(AccountStatus.LoggingIn, DeviceCode: code));

        Assert.Equal("ABCD-EFGH", card.DeviceCode);
        Assert.Equal(new Uri("https://www.twitch.tv/activate"), card.ActivateUrl);
        Assert.Equal("İptal", card.ActionLabel);
        Assert.Same(cancel, card.ActionCommand);

        card.Update(new AccountSnapshot(AccountStatus.LoggedIn, "x"));
        Assert.Null(card.DeviceCode);
        Assert.Null(card.ActivateUrl);
    }

    [Theory]
    [InlineData(AccountStatus.LoggedOut, "Giriş yapılmadı", "Giriş yap", Severity.Neutral)]
    [InlineData(AccountStatus.NeedsLogin, "Oturum geçersiz — yeniden giriş gerekli", "Yeniden giriş yap", Severity.Error)]
    [InlineData(AccountStatus.Offline, "Çevrimdışı — oturum korunuyor, bağlantı bekleniyor", "Yeniden dene", Severity.Warn)]
    public void States_MapToTextActionAndSeverity(AccountStatus status, string text, string action, Severity severity)
    {
        var (card, login, _, _, retry) = Create();

        card.Update(new AccountSnapshot(status));

        Assert.Equal(text, card.StatusText);
        Assert.Equal(action, card.ActionLabel);
        Assert.Equal(severity, card.Severity);
        Assert.Same(status == AccountStatus.Offline ? retry : login, card.ActionCommand);
    }

    [Fact]
    public void Unknown_HasNoAction_AndErrorIsShown()
    {
        var (card, _, _, _, _) = Create();

        Assert.Null(card.ActionLabel);
        card.Update(new AccountSnapshot(AccountStatus.LoggedOut, Error: "Kod zamanında onaylanmadı"));
        Assert.Equal("Kod zamanında onaylanmadı", card.Error);
    }

    [Fact]
    public void KickCard_IsDisabledComingSoon_AndIgnoresUpdates()
    {
        var kick = new PlatformCardViewModel("Kick", "Yakında");

        kick.Update(new AccountSnapshot(AccountStatus.LoggedIn, "x"));

        Assert.False(kick.IsAvailable);
        Assert.Equal("Yakında", kick.StatusText);
        Assert.Null(kick.ActionLabel);
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 dk")]
    [InlineData(45, "45 dk")]
    [InlineData(60, "1 sa")]
    [InlineData(86, "1 sa 26 dk")]
    [InlineData(300, "5 sa")]
    [InlineData(-5, "0 dk")]
    public void Minutes_AreFormattedInTurkish(int minutes, string expected) => Assert.Equal(expected, Fmt.Minutes(minutes));

    [Theory]
    [InlineData(2, "az önce")]
    [InlineData(12, "12 sn önce")]
    [InlineData(125, "2 dk önce")]
    [InlineData(7300, "2 sa önce")]
    public void Ago_IsRelative(int seconds, string expected) => Assert.Equal(expected, Fmt.Ago(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void When_UsesTodayTomorrowOrDate()
    {
        var now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

        Assert.Equal("bugün 23:40", Fmt.When(DateTimeOffset.Parse("2026-10-23T23:40:00Z"), now, TimeZoneInfo.Utc));
        Assert.Equal("yarın 02:10", Fmt.When(DateTimeOffset.Parse("2026-10-24T02:10:00Z"), now, TimeZoneInfo.Utc));
        Assert.Equal("1 Kas 14:30", Fmt.When(DateTimeOffset.Parse("2026-11-01T14:30:00Z"), now, TimeZoneInfo.Utc));
    }
}
