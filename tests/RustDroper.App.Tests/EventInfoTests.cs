using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Domain;
using RustDroper.Core.Media;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.Tests;

/// <summary>Kart ortasındaki etkinlik bilgisi: rozet, ad, ayrıntı, yerel tarihler, kalan süre (dakikada bir), bitmiş etkinlik kartları, platform yalıtımı.</summary>
public class EventInfoTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 11, 1, 23, 59, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Turkey = TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "TR", "TR");

    private static FacepunchEvent BaseInvaders(DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new("Base Invaders", ["Twitch Drops", "54"], "Hosted by Rustoria", "is-pending", "Upcoming", start ?? Start, end ?? End);

    private static (DashboardViewModel Dashboard, FakeMedia Media, FakeTimeProvider Time) Create(DateTimeOffset now, FacepunchEvent? twitch = null)
    {
        var media = new FakeMedia();
        if (twitch is not null)
        {
            media.Events[Platform.Twitch] = twitch;
        }

        var time = new FakeTimeProvider(now);
        return (new DashboardViewModel(new RecordingBrowser(), time, Turkey, media), media, time);
    }

    private static DashboardInput Input(DateTimeOffset now, PlatformSnapshot? campaigns = null, Platform platform = Platform.Twitch) =>
        new(Dx.Engine(), false, campaigns, null, new HashSet<string>(), now, platform);

    [Theory]
    [InlineData(99, 20, "4 gün 3 sa")]
    [InlineData(77, 0, "3 gün 5 sa")]
    [InlineData(48, 30, "2 gün")]
    [InlineData(5, 20, "5 sa 20 dk")]
    [InlineData(1, 0, "1 sa")]
    [InlineData(0, 12, "12 dk")]
    [InlineData(0, 0, "1 dk")]
    public void Remaining_FormatsDaysHoursMinutes_NoSeconds(int hours, int minutes, string expected) =>
        Assert.Equal(expected, Fmt.Remaining(TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(37)));

    [Fact]
    public void DateRange_IsShownInLocalTime()
    {
        var now = Start.AddDays(-5);

        Assert.Equal("23 Eki 21:00 → 2 Kas 02:59", Fmt.DateRange(Start, End, now, Turkey)); // 18:00 UTC = 21:00 TR; 23:59 UTC = ertesi gün 02:59 TR
        Assert.Equal("23 Eki 18:00 → 1 Kas 23:59", Fmt.DateRange(Start, End, now, TimeZoneInfo.Utc));
        Assert.Equal("23 Eki 2026 21:00 →", Fmt.DateRange(Start, null, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), Turkey));
        Assert.Equal(string.Empty, Fmt.DateRange(null, null, now, Turkey));
    }

    [Fact]
    public void Upcoming_ShowsBadgeNameDetailLocalDatesAndCountdownToStart()
    {
        var now = Start.AddDays(-4).AddHours(-3).AddMinutes(-20);
        var (dashboard, _, _) = Create(now, BaseInvaders());

        dashboard.Update(Input(now));

        var e = dashboard.Event;
        Assert.True(e.HasEvent);
        Assert.Equal("Yakında", e.StatusText);
        Assert.Equal(Severity.Info, e.StatusSeverity);
        Assert.Equal("Base Invaders", e.Title);
        Assert.Equal("Twitch Drops · 54 · Rustoria sunar", e.DetailText);
        Assert.Equal("23 Eki 21:00 → 2 Kas 02:59", e.DatesText);
        Assert.Equal("Başlamasına 4 gün 3 sa", e.CountdownText);
    }

    [Fact]
    public void Active_CountsDownToTheEnd_AndLastDayShowsHours()
    {
        var now = End.AddDays(-3).AddHours(-5).AddMinutes(-10);
        var (dashboard, _, _) = Create(now, BaseInvaders());

        dashboard.Update(Input(now));
        Assert.Equal("Aktif", dashboard.Event.StatusText);
        Assert.Equal(Severity.Ok, dashboard.Event.StatusSeverity);
        Assert.Equal("Bitmesine 3 gün 5 sa", dashboard.Event.CountdownText);

        dashboard.Update(Input(End.AddHours(-7).AddMinutes(-5)));
        Assert.Equal("Bitmesine 7 sa 5 dk", dashboard.Event.CountdownText);
    }

    [Fact]
    public void Ended_ShowsBittiWithoutCountdown_AndCatalogCardsBecomeEnded()
    {
        var now = End.AddDays(2);
        var (dashboard, media, _) = Create(now, BaseInvaders());
        media.Catalogs[Platform.Twitch] = [new DropMedia("Rug", 60, null, null, null, null, false, null, null), new DropMedia("Backpack", 30, null, null, null, null, true, "TwitchRivals", null)];

        dashboard.Update(Input(now));

        Assert.Equal("Bitti", dashboard.Event.StatusText);
        Assert.Equal(string.Empty, dashboard.Event.CountdownText);
        var general = dashboard.GeneralCampaigns.Single();
        Assert.Equal("Etkinlik bitti", general.StatusText);
        Assert.All(general.Drops, d =>
        {
            Assert.Equal(DropState.Ended, d.State);
            Assert.Equal("Etkinlik bitti", d.StateText);
        });
        Assert.All(dashboard.StreamerCampaigns.Single().Drops, d => Assert.Equal(DropState.Ended, d.State));
    }

    [Fact]
    public void BeforeAndDuringEvent_CatalogCardsAreWaiting_NotEnded()
    {
        var now = Start.AddDays(1);
        var (dashboard, media, _) = Create(now, BaseInvaders());
        media.Catalogs[Platform.Twitch] = [new DropMedia("Rug", 60, null, null, null, null, false, null, null)];

        dashboard.Update(Input(now));

        Assert.Equal(DropState.Waiting, dashboard.GeneralCampaigns.Single().Drops.Single().State);
    }

    [Fact]
    public void FacepunchEvent_WinsOverDiscoveredCampaigns_SoNonRustCampaignsNeverBecomeTheEvent()
    {
        var now = Start.AddDays(1);
        var (dashboard, _, _) = Create(now, BaseInvaders());
        var other = Dx.Campaign("c9", "SpookyDragon 2026", CampaignScope.Streamer, Start.AddDays(-8), End.AddDays(-2), null, null, ("a", 180));

        dashboard.Update(Input(now, Dx.Snapshot((other, []))));

        Assert.Equal("Base Invaders", dashboard.Event.Title); // Rust etkinliği (Facepunch), rozet kampanyası değil
        Assert.Equal("Twitch Drops · 54 · Rustoria sunar", dashboard.Event.DetailText);
        Assert.DoesNotContain("SpookyDragon", dashboard.Event.DatesText, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutFacepunchData_TheDiscoveredCampaignFillsTheEvent()
    {
        var now = Start.AddDays(1);
        var (dashboard, _, _) = Create(now);
        var campaign = Dx.Campaign("c1", "Base Invaders", CampaignScope.General, Start.AddHours(1), End.AddHours(1), null, null, ("a", 60));

        dashboard.Update(Input(now, Dx.Snapshot((campaign, []))));

        Assert.Equal("Base Invaders", dashboard.Event.Title);
        Assert.Equal("23 Eki 22:00 → 2 Kas 03:59", dashboard.Event.DatesText);
    }

    [Fact]
    public void CountdownIsRecomputedOncePerMinute_NotEverySecond()
    {
        var now = Start.AddHours(-2).AddSeconds(-10);
        var (dashboard, _, time) = Create(now, BaseInvaders());
        dashboard.Update(Input(now));
        Assert.Equal("Başlamasına 2 sa", dashboard.Event.CountdownText.Replace(" 0 dk", string.Empty));
        var changes = new List<string>();
        dashboard.Event.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);

        for (var i = 0; i < 30; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            dashboard.Tick();
        }

        Assert.True(changes.Count <= 1, "saniyelik değişiklik yok; gelen: " + string.Join(',', changes)); // en çok bir dakika sınırı geçişi
        time.Advance(TimeSpan.FromMinutes(3));
        dashboard.Tick();
        Assert.Equal("Başlamasına 1 sa 56 dk", dashboard.Event.CountdownText);
    }

    [Fact]
    public void NoData_NoEvent()
    {
        var (dashboard, _, _) = Create(Start);

        dashboard.Update(Input(Start));

        Assert.False(dashboard.Event.HasEvent);
        Assert.Equal(string.Empty, dashboard.Event.Title);
    }

    [Fact]
    public void BrokenPageWithOnlyBadgeClass_StillShowsStatus_WithoutDates()
    {
        var (dashboard, _, _) = Create(Start, new FacepunchEvent("Rastgele", [], null, "is-pending", "Upcoming", null, null));

        dashboard.Update(Input(Start));

        Assert.True(dashboard.Event.HasEvent);
        Assert.Equal("Yakında", dashboard.Event.StatusText);
        Assert.Equal(string.Empty, dashboard.Event.DatesText);
        Assert.Equal(string.Empty, dashboard.Event.CountdownText);
    }

    [Fact]
    public void KickScreen_UsesOnlyKicksEvent_WithGenericTitle_AndNeverTwitchData()
    {
        var (dashboard, media, _) = Create(Start, BaseInvaders());
        media.Events[Platform.Kick] = new FacepunchEvent(null, [], null, null, null, new DateTimeOffset(2026, 6, 11, 20, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 6, 21, 23, 0, 0, TimeSpan.Zero));

        dashboard.Update(Input(Start, platform: Platform.Kick));

        var e = dashboard.Event;
        Assert.Equal("Bitti", e.StatusText);
        Assert.Equal("Kick drop etkinliği", e.Title);
        var text = string.Join(' ', e.Title, e.DetailText, e.DatesText, e.CountdownText);
        Assert.DoesNotContain("Base Invaders", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Twitch", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rustoria", text, StringComparison.Ordinal);
    }

    [Fact]
    public void KickScreen_UsesKickApiCampaignNameAndDates()
    {
        var (dashboard, _, _) = Create(Start);
        var campaign = new Campaign(
            Platform.Kick, "k1", "Kick Rust Drops", "rust", "Rust", CampaignStatus.Active, Start.AddDays(-1), Start.AddDays(4), CampaignScope.General, [],
            [new DropReward("r", "Emote", 60, null)], null);

        dashboard.Update(Input(Start, Dx.Snapshot((campaign, [])), Platform.Kick));

        Assert.Equal("Kick Rust Drops", dashboard.Event.Title);
        Assert.Equal("Aktif", dashboard.Event.StatusText);
        Assert.Equal("Bitmesine 4 gün", dashboard.Event.CountdownText);
    }
}
