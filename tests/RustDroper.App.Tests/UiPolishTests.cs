using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Domain;
using RustDroper.Core.Media;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

/// <summary>Dilim A: drop sayıları, boş-durum metinleri, bilgi satırı, kart bağlantı satırı + site düğmesi.</summary>
public class UiPolishTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

    private static DashboardViewModel Dashboard(FakeMedia? media = null) => new(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc, media);

    private static RelayCommand Noop() => new(_ => { });

    [Fact]
    public void SectionCounts_ShowDropTotals_OrNothing()
    {
        var dashboard = Dashboard();
        var general = Dx.Campaign("c1", "Base Invaders", CampaignScope.General, null, Now.AddDays(9), null, null, ("a", 60), ("b", 120), ("c", 180), ("d", 240), ("e", 300));
        var rivals = new Streamer("s1", "tr", "TwitchRivals", new Uri("https://www.twitch.tv/TwitchRivals"), null);
        var streamer = Dx.Campaign("c2", "Rivals", CampaignScope.Streamer, null, Now.AddDays(9), [rivals], null, ("r1", 30));

        dashboard.Update(Dx.Input(Dx.Engine(), campaigns: Dx.Snapshot((general, []), (streamer, []))));

        Assert.Equal("5 Drop", dashboard.GeneralCountText);
        Assert.Equal("1 Drop", dashboard.StreamerCountText);

        dashboard.Update(Dx.Input(Dx.Engine()));
        Assert.Equal(string.Empty, dashboard.GeneralCountText);
        Assert.Equal(string.Empty, dashboard.StreamerCountText);
    }

    [Fact]
    public void CatalogPlaceholderCampaign_HasNoSubheading_ButKeepsStatus()
    {
        var media = new FakeMedia();
        media.Catalogs[Platform.Twitch] = [new DropMedia("Rug", 60, null, null, null, null, false, null, null)];
        var dashboard = Dashboard(media);

        dashboard.Update(Dx.Input(Dx.Engine()));

        var item = dashboard.GeneralCampaigns.Single();
        Assert.Equal(string.Empty, item.Name); // "Rust drop'ları" ara başlığı kaldırıldı
        Assert.Equal("Etkinlik bekleniyor", item.StatusText);
        Assert.Equal("1 Drop", dashboard.GeneralCountText);
    }

    [Fact]
    public void NoCampaigns_NoTotalsLine_SectionsOwnEmptyStates()
    {
        var dashboard = Dashboard();

        dashboard.Update(Dx.Input(Dx.Engine()));

        Assert.Equal(string.Empty, dashboard.TotalsText);
        Assert.False(dashboard.HasGeneral);
        Assert.False(dashboard.HasStreamer);
    }

    [Fact]
    public void InfoLine_ShowsLastCheckTime_WhenRunning_AndIntervalsAlways()
    {
        var dashboard = Dashboard();
        var events = new[] { new EngineEvent(Now.AddMinutes(-3), "info", "x") };

        dashboard.Update(Dx.Input(Dx.Engine(EngineState.Watching, "izleniyor", null, null, Now.AddSeconds(-20), events), running: true));
        Assert.StartsWith("Son kontrol 17:59 · motor ~60 sn'de bir ilerlemeyi", dashboard.Now.InfoText, StringComparison.Ordinal);
        Assert.Contains("katalog 6 saatte bir yeniler", dashboard.Now.InfoText, StringComparison.Ordinal);

        dashboard.Update(Dx.Input(Dx.Engine(), running: false));
        Assert.StartsWith("motor ~60 sn'de bir", dashboard.Now.InfoText, StringComparison.Ordinal); // çalışmıyorsa saat yok
        Assert.Contains("10 dk", dashboard.Now.InfoTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Card_KnowsLoggedIn_AndHasItsPlatformSiteOnly()
    {
        var twitch = new PlatformCardViewModel("Twitch", Noop(), Noop(), Noop(), Noop());
        var kick = new PlatformCardViewModel("Kick", Noop(), Noop(), Noop(), Noop(), Platform.Kick);

        Assert.False(twitch.IsLoggedIn);
        twitch.Update(new AccountSnapshot(AccountStatus.LoggedIn, "fan"));
        Assert.True(twitch.IsLoggedIn);
        twitch.Update(new AccountSnapshot(AccountStatus.NeedsLogin));
        Assert.False(twitch.IsLoggedIn);

        Assert.Equal("https://twitch.facepunch.com/", twitch.SiteUrl.ToString());
        Assert.Equal("https://kick.facepunch.com/", kick.SiteUrl.ToString());
        Assert.DoesNotContain("kick", twitch.SiteTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("twitch", kick.SiteTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, "Bağlı: Facepunch/Steam Hesabı", Severity.Ok)]
    [InlineData(false, "Bağlı değil: Facepunch/Steam Hesabı", Severity.Error)]
    [InlineData(null, "Facepunch/Steam: bilinmiyor", Severity.Neutral)]
    public void LinkStatus_UsesShortWording_PerPlatformHost(bool? linked, string text, Severity severity)
    {
        var dashboard = Dashboard();
        var claimed = linked is null ? [] : new List<ClaimedReward> { new(Platform.Kick, "k", "R", null, 1, linked, new Uri("https://kick.facepunch.com/connect"), null) };

        dashboard.Update(new DashboardInput(Dx.Engine(), false, null, new TwitchInventorySnapshot(claimed, []), new HashSet<string>(), Now, Platform.Kick));

        Assert.Equal(text, dashboard.LinkStatusText);
        Assert.Equal(severity, dashboard.LinkSeverity);
    }
}
