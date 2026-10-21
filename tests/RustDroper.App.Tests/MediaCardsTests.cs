using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Media;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

internal sealed class FakeMedia : IDropMediaProvider
{
    public Dictionary<Platform, List<DropMedia>> Catalogs { get; } = [];

    public Dictionary<string, string> Cached { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Downloads { get; } = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    public Dictionary<Platform, FacepunchEvent> Events { get; } = [];

    public FacepunchEvent? CurrentEvent(Platform platform, DateTimeOffset now) => Events.GetValueOrDefault(platform);

    public event Action? Changed;

    public IReadOnlyList<DropMedia> Catalog(Platform platform) => Catalogs.TryGetValue(platform, out var list) ? list : [];

    public string? CachedPath(Uri url) => Cached.GetValueOrDefault(url.AbsoluteUri);

    public Task<string?> EnsureAsync(Uri url, CancellationToken cancellationToken)
    {
        lock (Requested)
        {
            Requested.Add(url.AbsoluteUri);
        }

        if (Downloads.TryGetValue(url.AbsoluteUri, out var path))
        {
            Cached[url.AbsoluteUri] = path;
            return Task.FromResult<string?>(path);
        }

        return Task.FromResult<string?>(null);
    }

    public void RaiseChanged() => Changed?.Invoke();
}

/// <summary>Drop kartları: Facepunch kataloğundan kartlar (etkinlik başlamadan da), görsel eşleştirme, yerinde güncelleme, platform yalıtımı.</summary>
public class MediaCardsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");
    private static readonly Uri Rivals = new("https://www.twitch.tv/TwitchRivals");

    private static DropMedia Media(string name, int minutes, bool streamer = false, string? image = "C:\\media\\x.jpg") =>
        new(name, minutes, null, new Uri("https://files.facepunch.com/" + DropNames.Key(name) + ".jpg"), new Uri("https://files.facepunch.com/" + DropNames.Key(name) + ".mp4"),
            image, streamer, streamer ? "TwitchRivals" : null, streamer ? Rivals : null);

    private static FakeMedia TwitchMedia()
    {
        var media = new FakeMedia();
        media.Catalogs[Platform.Twitch] =
        [
            Media("Rug", 60, image: "C:\\media\\rug.jpg"),
            Media("Bed", 120),
            Media("Garage Door", 180),
            Media("Thompson", 240),
            Media("Large Wood Box", 300),
            Media("Backpack", 30, streamer: true),
        ];
        return media;
    }

    private static DashboardViewModel Dashboard(FakeMedia? media) => new(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc, media);

    private static Campaign GeneralCampaign(params (string Id, string Name, int Minutes)[] drops) => new(
        Platform.Twitch, "c1", "Base Invaders", "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(9), CampaignScope.General, [],
        [.. drops.Select(d => new DropReward(d.Id, d.Name, d.Minutes, null))], null);

    private static DashboardInput Input(PlatformSnapshot? campaigns, TwitchInventorySnapshot? inventory = null, EngineSnapshot? engine = null, bool running = false, Platform platform = Platform.Twitch) =>
        new(engine ?? Dx.Engine(), running, campaigns, inventory, new HashSet<string>(), Now, platform);

    [Fact]
    public void NoCampaigns_StillShowsCatalogCards_AsWaitingWithZeroProgress()
    {
        var dashboard = Dashboard(TwitchMedia());

        dashboard.Update(Input(null));

        var general = dashboard.GeneralCampaigns.Single();
        Assert.Equal(["Rug", "Bed", "Garage Door", "Thompson", "Large Wood Box"], general.Drops.Select(d => d.Name));
        Assert.All(general.Drops, d =>
        {
            Assert.Equal(DropState.Waiting, d.State);
            Assert.Equal("Etkinlik bekleniyor", d.StateText);
            Assert.Equal(0, d.Percent);
            Assert.True(d.HasImage);
        });
        Assert.Equal(["1 sa", "2 sa", "3 sa", "4 sa", "5 sa"], general.Drops.Select(d => d.DurationText.Replace(' ', ' ')));
        Assert.All(general.Drops, d => Assert.DoesNotContain(' ', d.DurationText)); // satır sonunda bölünmez
        var streamer = dashboard.StreamerCampaigns.Single();
        Assert.Equal("TwitchRivals", streamer.Name);
        var card = Assert.Single(streamer.Drops);
        Assert.Equal("Backpack", card.Name);
        Assert.Equal("TwitchRivals", card.StreamerName);
        Assert.Equal(Rivals, card.StreamerUrl);
        Assert.True(dashboard.HasGeneral);
        Assert.True(dashboard.HasStreamer);
        Assert.Equal(string.Empty, dashboard.TotalsText); // belirsiz "Henüz kampanya bilgisi yok" metni kaldırıldı
    }

    [Fact]
    public void WithoutMediaProvider_NoCatalogCards_ButNothingBreaks()
    {
        var dashboard = Dashboard(null);

        dashboard.Update(Input(null));

        Assert.Empty(dashboard.GeneralCampaigns);
        Assert.False(dashboard.HasGeneral);
    }

    [Fact]
    public void RealCampaign_GetsImagesByTolerantNameMatch_AndReplacesCatalogPlaceholder()
    {
        var dashboard = Dashboard(TwitchMedia());
        var campaign = GeneralCampaign(("a", "large-wood-box", 300), ("b", "GARAGE DOOR", 180), ("c", "Bilinmeyen Ödül", 60));

        dashboard.Update(Input(Dx.Snapshot((campaign, []))));

        var rows = dashboard.GeneralCampaigns.Single().Drops.ToDictionary(r => r.Id);
        Assert.Equal("c1", dashboard.GeneralCampaigns.Single().Id); // katalog yer tutucusu değil, gerçek kampanya
        Assert.True(rows["a"].HasImage);
        Assert.True(rows["b"].HasImage);
        Assert.False(rows["c"].HasImage); // eşleşmeyen: yer tutucu ikon
        Assert.Equal(DropState.Queued, rows["a"].State);
    }

    [Fact]
    public void States_MapToCardStates()
    {
        var dashboard = Dashboard(TwitchMedia());
        var campaign = GeneralCampaign(("a", "Rug", 60), ("b", "Bed", 120), ("c", "Garage Door", 180), ("d", "Thompson", 240), ("e", "Large Wood Box", 300));
        var inventory = new TwitchInventorySnapshot(
            [],
            [
                Dx.Progress("c1", "a", 60, 60, claimed: true),
                Dx.Progress("c1", "b", 120, 120, claimId: "claim-b"),
                Dx.Progress("c1", "d", 240, 30),
            ]);
        var target = new Target(campaign, Dx.Channel("1", "rustfan"));
        var engine = Dx.Engine(EngineState.Watching, "izleniyor", target, new DropSession("c", 50, 180));

        dashboard.Update(Input(Dx.Snapshot((campaign, [])), inventory, engine, running: true));

        var rows = dashboard.GeneralCampaigns.Single().Drops.ToDictionary(r => r.Id);
        Assert.Equal(DropState.Claimed, rows["a"].State);
        Assert.Equal(DropState.ClaimReady, rows["b"].State);
        Assert.Equal(DropState.Watching, rows["c"].State);
        Assert.Equal(DropState.InProgress, rows["d"].State);
        Assert.Equal(DropState.Queued, rows["e"].State);
        Assert.Equal(28, rows["c"].Percent, 0);
    }

    [Fact]
    public void Updates_ReuseTheSameCardInstances_SoProgressCanAnimate()
    {
        var dashboard = Dashboard(TwitchMedia());
        var campaign = GeneralCampaign(("a", "Rug", 60), ("b", "Bed", 120));
        var snapshot = Dx.Snapshot((campaign, []));
        dashboard.Update(Input(snapshot, new TwitchInventorySnapshot([], [Dx.Progress("c1", "a", 60, 10)])));
        var item = dashboard.GeneralCampaigns.Single();
        var card = item.Drops.First(r => r.Id == "a");
        var changed = new List<string>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        dashboard.Update(Input(snapshot, new TwitchInventorySnapshot([], [Dx.Progress("c1", "a", 60, 30)])));

        Assert.Same(item, dashboard.GeneralCampaigns.Single());
        Assert.Same(card, item.Drops.First(r => r.Id == "a"));
        Assert.Equal(50, card.Percent);
        Assert.Contains(nameof(DropRow.Percent), changed);
        Assert.DoesNotContain(nameof(DropRow.ImagePath), changed); // görsel yeniden yüklenmez
    }

    [Fact]
    public void KickScreen_NeverShowsTwitchCatalog_AndUsesRewardImageUrl()
    {
        var media = TwitchMedia();
        var image = new Uri("https://ext.cdn.kick.com/drops/reward-image/a.png");
        media.Downloads[image.AbsoluteUri] = "C:\\media\\kick-a.png";
        var dashboard = Dashboard(media);
        var campaign = new Campaign(
            Platform.Kick, "k1", "Kick Rust", "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(5), CampaignScope.General, [],
            [new DropReward("kd1", "Rug", 60, image), new DropReward("kd2", "Resimsiz", 120, null)], null);
        var ready = new ManualResetEventSlim();
        dashboard.ImagesReady += ready.Set;

        dashboard.Update(Input(Dx.Snapshot((campaign, [])), platform: Platform.Kick));

        var first = dashboard.GeneralCampaigns.Single();
        Assert.Equal("k1", first.Id);
        Assert.DoesNotContain(first.Drops, d => d.Id.StartsWith("fp:", StringComparison.Ordinal));
        Assert.False(first.Drops.First(r => r.Id == "kd2").HasImage); // yer tutucu ikon
        Assert.True(ready.Wait(5000)); // görsel arka planda indi → pano yenilensin
        dashboard.Update(Input(Dx.Snapshot((campaign, [])), platform: Platform.Kick));
        Assert.Equal("C:\\media\\kick-a.png", dashboard.GeneralCampaigns.Single().Drops.First(r => r.Id == "kd1").ImagePath);
        Assert.Empty(dashboard.StreamerCampaigns); // Twitch'in yayıncı kataloğu Kick'te yok
    }

    [Fact]
    public void KickScreen_WithNoCampaigns_ShowsNothingFromTwitchCatalog()
    {
        var dashboard = Dashboard(TwitchMedia());

        dashboard.Update(Input(null, platform: Platform.Kick));

        Assert.Empty(dashboard.GeneralCampaigns);
        Assert.Empty(dashboard.StreamerCampaigns);
    }

    [Fact]
    public void ImageDownloadFailure_LeavesPlaceholder_AndRetriesAfterTenMinutes()
    {
        var media = new FakeMedia();
        var url = new Uri("https://ext.cdn.kick.com/x.png");
        var dashboard = Dashboard(media);
        var campaign = new Campaign(
            Platform.Kick, "k1", "K", "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(5), CampaignScope.General, [],
            [new DropReward("kd1", "X", 60, url)], null);
        var snapshot = Dx.Snapshot((campaign, []));

        dashboard.Update(Input(snapshot, platform: Platform.Kick));
        SpinWait.SpinUntil(() => media.Requested.Count == 1, 5000);
        dashboard.Update(Input(snapshot, platform: Platform.Kick)); // 10 dk dolmadı: yeniden istenmez

        Assert.Single(media.Requested);
        Assert.False(dashboard.GeneralCampaigns.Single().Drops.Single().HasImage);

        dashboard.Update(new DashboardInput(Dx.Engine(), false, snapshot, null, new HashSet<string>(), Now.AddMinutes(11), Platform.Kick));
        SpinWait.SpinUntil(() => media.Requested.Count == 2, 5000);
        Assert.Equal(2, media.Requested.Count);
    }

    [Fact]
    public async Task MainViewModel_RebuildsCards_WhenCatalogArrivesLater_AndTwitchScreenHasNoKickText()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        using var twitch = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings);
        using var kick = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings, platform: PlatformProfile.Kick);
        var media = new FakeMedia();
        var vm = new MainViewModel(settings, new FakeAutostart(), new RecordingBrowser(), twitch, new ImmediateDispatcher(), new FakeTimeProvider(Now), TimeZoneInfo.Utc, kickService: kick, media: media);
        vm.IsWindowVisible = true;
        Assert.Empty(vm.Dashboard.GeneralCampaigns);

        media.Catalogs[Platform.Twitch] = TwitchMedia().Catalogs[Platform.Twitch];
        media.RaiseChanged();
        await Task.Delay(10);

        Assert.Equal(5, vm.Dashboard.GeneralCampaigns.Single().Drops.Count);
        var text = string.Join(' ', vm.Dashboard.GeneralCampaigns.SelectMany(c => c.Drops).Select(d => d.Name + d.StateText + d.StreamerName));
        Assert.DoesNotContain("kick", text, StringComparison.OrdinalIgnoreCase);

        vm.SelectedPlatform = Platform.Kick;
        Assert.Empty(vm.Dashboard.GeneralCampaigns);
    }

    [Fact]
    public void StreamerCampaign_CardsCarryStreamerNameLinkAndLiveFlag()
    {
        var dashboard = Dashboard(TwitchMedia());
        var streamer = new Streamer("s1", "twitchrivals", "TwitchRivals", Rivals, null);
        var campaign = new Campaign(
            Platform.Twitch, "c2", "Rivals", "rust", "Rust", CampaignStatus.Active, Now.AddHours(-1), Now.AddDays(5), CampaignScope.Streamer, [streamer],
            [new DropReward("r1", "Backpack", 30, null)], null);
        var live = Dx.Channel("s1", "twitchrivals");

        dashboard.Update(Input(Dx.Snapshot((campaign, [live]))));

        var card = dashboard.StreamerCampaigns.Single().Drops.Single();
        Assert.True(card.HasStreamer);
        Assert.Equal("TwitchRivals", card.StreamerName);
        Assert.Equal(Rivals, card.StreamerUrl);
        Assert.True(card.StreamerLive);
        Assert.True(card.HasImage); // "Backpack" kataloğuyla eşleşti
    }
}
