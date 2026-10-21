using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

/// <summary>
/// İki ekranlı pano: Twitch ekranında Kick'e, Kick ekranında Twitch'e dair HİÇBİR şey görünmez;
/// diğer platformun durumu yalnızca metinsiz dikkat noktası olarak geçişe yansır; seçim kalıcıdır.
/// </summary>
public class PlatformScreenTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

    private readonly TempDir _dir = new();
    private readonly SettingsService _settings;
    private readonly StubAccount _twitchAccount = new();
    private readonly StubAccount _kickAccount = new();
    private readonly StubPlatform _twitchPlatform = new();
    private readonly StubPlatform _kickPlatform = new();
    private readonly StubFactory _twitchFactory;
    private readonly StubFactory _kickFactory;
    private readonly DropperService _twitch;
    private readonly DropperService _kick;
    private readonly MainViewModel _vm;

    public PlatformScreenTests()
    {
        _settings = new SettingsService(_dir.File("settings.json"));
        EngineOptions Options(AppSettings s) => new() { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(20) };
        _twitchFactory = new StubFactory(_twitchPlatform);
        _kickFactory = new StubFactory(_kickPlatform);
        _twitch = new DropperService(_twitchAccount, _twitchFactory, _settings, engineOptions: Options);
        _kick = new DropperService(_kickAccount, _kickFactory, _settings, engineOptions: Options, platform: PlatformProfile.Kick);
        _twitchPlatform.Snapshot = Dx.Snapshot((Dx.Campaign("c1", "Base Invaders", CampaignScope.General, null, Now.AddYears(5), null, null, ("d1", 60)), [Dx.Channel("1", "streamer")]));
        _kickPlatform.Snapshot = Dx.Snapshot((Dx.Campaign("k1", "Rust Gece Drop", CampaignScope.General, null, Now.AddYears(5), null, null, ("kd1", 120)), [KickChannel("9", "gecekuşu")]));
        _vm = NewViewModel();
    }

    public void Dispose()
    {
        _twitch.Dispose();
        _kick.Dispose();
        _dir.Dispose();
    }

    private MainViewModel NewViewModel() => new(
        _settings, new FakeAutostart(), new RecordingBrowser(), _twitch, new ImmediateDispatcher(), new FakeTimeProvider(Now), TimeZoneInfo.Utc,
        kickService: _kick, clipboard: _ => { });

    private static LiveChannel KickChannel(string id, string login) =>
        new(Platform.Kick, id, login, login, 100, null, new Uri("https://kick.com/" + login), null);

    private static TwitchRestoreResult Restored(string login) => new(TwitchRestoreStatus.Restored, new TwitchSession("tok-" + login, "id-" + login, login, null));

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 20000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Koşul zamanında gerçekleşmedi.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Seçili ekranda kullanıcıya gösterilen tüm metinler (kart, uyarı şeridi, pano).</summary>
    private static string ScreenText(MainViewModel vm)
    {
        var d = vm.Dashboard;
        var card = vm.SelectedCard;
        var parts = new List<string?>
        {
            card.Name, card.StatusText, card.ActionLabel, card.Error, card.PlayerSessionText, card.DeviceCode, card.ActivateUrl?.ToString(),
            card.DeviceCode is null ? null : card.LoginLinkText,
            vm.Notice, d.TotalsText, d.LinkStatusText, d.ConnectUrl?.ToString(), d.ClaimedMoreText,
            d.Now.BadgeText, d.Now.Message, d.Now.ChannelName, d.Now.ChannelUrl?.ToString(), d.Now.CampaignName, d.Now.DropName, d.Now.MethodText, d.Now.LastProgressText,
        };
        foreach (var campaign in d.GeneralCampaigns.Concat(d.StreamerCampaigns))
        {
            parts.AddRange([campaign.Name, campaign.StatusText, campaign.TimeText, campaign.SummaryText, campaign.WatchableText]);
            parts.AddRange(campaign.Streamers.Select(s => s.Name + " " + s.Url));
            parts.AddRange(campaign.Drops.SelectMany(r => new[] { r.Name, r.StateText, r.ProgressText }));
        }

        parts.AddRange(d.ClaimedRewards.Select(r => r.Name + " " + r.DetailText));
        parts.AddRange(d.Events.Select(e => e.Text));
        return string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    [Fact]
    public async Task EachScreen_ShowsOnlyItsOwnPlatform_EvenWhileBothRunWithEvents()
    {
        _twitchAccount.Restore = Restored("twitchfan");
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();
        _vm.IsWindowVisible = true;
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _twitch.IsRunning && _kick.IsRunning);
        await WaitUntilAsync(() => _twitch.Snapshot.Engine.Recent.Count > 0 && _kick.Snapshot.Engine.Recent.Count > 0);

        // Görünüm modeli anlık görüntüyü UI iş parçacığına sonradan taşır: ekran metni beklenen içeriğe varana dek bekle (yük altında yarış).
        _vm.SelectedPlatform = Platform.Twitch;
        await WaitUntilAsync(() => ScreenText(_vm).Contains("Base Invaders", StringComparison.Ordinal));
        var twitchScreen = ScreenText(_vm);
        _vm.SelectedPlatform = Platform.Kick;
        await WaitUntilAsync(() => ScreenText(_vm).Contains("Rust Gece Drop", StringComparison.Ordinal));
        var kickScreen = ScreenText(_vm);

        Assert.Contains("Base Invaders", twitchScreen, StringComparison.Ordinal);
        Assert.DoesNotContain("kick", twitchScreen, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rust Gece Drop", twitchScreen, StringComparison.Ordinal);
        Assert.Contains("Rust Gece Drop", kickScreen, StringComparison.Ordinal);
        Assert.DoesNotContain("twitch", kickScreen, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Base Invaders", kickScreen, StringComparison.Ordinal);

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_twitch.IsRunning && !_kick.IsRunning);
    }

    [Fact]
    public async Task KickProblem_IsHiddenOnTwitchScreen_ShownOnKickScreen_AndOnlyADotOnTheToggle()
    {
        _twitchAccount.Restore = Restored("twitchfan");
        _kickAccount.Restore = Restored("kickfan");
        _kickFactory.InventoryError = new HttpRequestException("no route");
        await _vm.InitializeAsync();
        await WaitUntilAsync(() => _vm.KickAttention);

        // Twitch ekranı: şerit yok, metin yok; Kick segmentinde yalnız nokta
        Assert.True(_vm.IsTwitchSelected);
        Assert.Null(_vm.Notice);
        Assert.DoesNotContain("kick", ScreenText(_vm), StringComparison.OrdinalIgnoreCase);
        Assert.True(_vm.KickAttentionDot);
        Assert.False(_vm.TwitchAttentionDot);

        _vm.SelectedPlatform = Platform.Kick;

        Assert.StartsWith("Kick:", _vm.Notice, StringComparison.Ordinal);
        Assert.False(_vm.KickAttentionDot); // o ekran zaten açık: nokta yok, ayrıntı şeritte
        Assert.False(_vm.TwitchAttentionDot);
    }

    [Fact]
    public async Task TwitchProblem_IsHiddenOnKickScreen_AndShowsDotOnTwitchSegment()
    {
        _twitchAccount.Restore = Restored("twitchfan");
        _kickAccount.Restore = Restored("kickfan");
        _twitchFactory.InventoryError = new HttpRequestException("ağ yok");
        await _vm.InitializeAsync();
        await WaitUntilAsync(() => _vm.TwitchAttention);
        _vm.SelectedPlatform = Platform.Kick;

        Assert.Null(_vm.Notice);
        Assert.DoesNotContain("twitch", ScreenText(_vm), StringComparison.OrdinalIgnoreCase);
        Assert.True(_vm.TwitchAttentionDot);
        Assert.False(_vm.KickAttentionDot);

        _vm.SelectedPlatform = Platform.Twitch;

        Assert.Contains("ağ yok", _vm.Notice, StringComparison.Ordinal);
        Assert.False(_vm.TwitchAttentionDot);
    }

    [Fact]
    public async Task NoProblems_NoDots_AndSelectedCardFollowsPlatform()
    {
        _twitchAccount.Restore = Restored("twitchfan");
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();

        Assert.False(_vm.TwitchAttentionDot);
        Assert.False(_vm.KickAttentionDot);
        Assert.Same(_vm.Twitch, _vm.SelectedCard);

        _vm.SelectPlatformCommand.Execute("Kick");

        Assert.Same(_vm.Kick, _vm.SelectedCard);
        Assert.True(_vm.Kick.IsKick);
        Assert.False(_vm.Twitch.IsKick);
    }

    [Fact]
    public void SelectingAPlatform_ClosesSettings()
    {
        _vm.ToggleSettingsCommand.Execute(null);
        Assert.True(_vm.ShowSettings);

        _vm.SelectPlatformCommand.Execute("Kick");

        Assert.False(_vm.ShowSettings);
        Assert.True(_vm.IsKickSelected);
    }

    [Fact]
    public void SelectedPlatform_IsPersistedAndRestored()
    {
        Assert.Equal("Twitch", _settings.Current.LastPlatform);

        _vm.SelectPlatformCommand.Execute("Kick");

        Assert.Equal("Kick", _settings.Current.LastPlatform);
        var reopened = NewViewModel();
        Assert.True(reopened.IsKickSelected);
        Assert.Equal(Platform.Kick, reopened.SelectedPlatform);

        reopened.SelectPlatformCommand.Execute("Twitch");

        Assert.Equal("Twitch", _settings.Current.LastPlatform);
        Assert.True(NewViewModel().IsTwitchSelected);
    }

    [Fact]
    public void SavedKickSelection_IsIgnoredWithoutKickService()
    {
        _settings.Update(s => s with { LastPlatform = "Kick" });

        var vm = new MainViewModel(_settings, new FakeAutostart(), new RecordingBrowser(), _twitch, new ImmediateDispatcher());

        Assert.True(vm.IsTwitchSelected);
        Assert.False(vm.HasKick);
        Assert.False(vm.KickAttentionDot);
    }

    [Fact]
    public async Task IsRunning_FollowsEitherPlatform()
    {
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();
        Assert.False(_vm.IsRunning);

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _kick.IsRunning && _vm.IsRunning);

        Assert.Equal("Duraklat", _vm.StartPauseLabel);
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_kick.IsRunning && !_vm.IsRunning);
    }

    [Fact]
    public void NowMethod_NamesTheHiddenPlayerOnKick_AndHlsOnTwitch()
    {
        var dashboard = Dx.Dashboard();
        var engine = Dx.Engine();

        dashboard.Update(new DashboardInput(engine, true, null, null, new HashSet<string>(), Now, Platform.Kick));
        Assert.Equal("Gizli oynatıcı", dashboard.Now.MethodText);

        dashboard.Update(new DashboardInput(engine, true, null, null, new HashSet<string>(), Now, Platform.Twitch));
        Assert.Equal("HLS", dashboard.Now.MethodText);
    }
}
