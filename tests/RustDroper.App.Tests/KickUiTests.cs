using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

/// <summary>İki platformlu arayüz: Kick kartı (QR girişi), ortak Başlat/Duraklat, ipucu, pano seçici.</summary>
public class KickUiTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");
    private static readonly Uri KickLink = new("https://kick.com/tv/login?uuid=A1B2C3D4-0000-4000-8000-000000000001&code=123456");

    private readonly TempDir _dir = new();
    private readonly StubAccount _twitchAccount = new();
    private readonly StubAccount _kickAccount = new();
    private readonly StubPlatform _twitchPlatform = new();
    private readonly StubPlatform _kickPlatform = new();
    private readonly StubFactory _kickFactory;
    private readonly RecordingBrowser _browser = new();
    private readonly List<string> _clipboard = [];
    private readonly DropperService _twitch;
    private readonly DropperService _kick;
    private readonly MainViewModel _vm;

    public KickUiTests()
    {
        var settings = new SettingsService(_dir.File("settings.json"));
        EngineOptions Options(AppSettings s) => new() { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(20) };
        _twitch = new DropperService(_twitchAccount, new StubFactory(_twitchPlatform), settings, engineOptions: Options);
        _kickFactory = new StubFactory(_kickPlatform);
        _kick = new DropperService(_kickAccount, _kickFactory, settings, engineOptions: Options, platform: PlatformProfile.Kick);
        _twitchPlatform.Snapshot = Dx.Snapshot((Dx.Campaign("c1", "Base Invaders", CampaignScope.General, null, Now.AddYears(5), null, null, ("d1", 60)), [Dx.Channel("1", "streamer")]));
        _kickPlatform.Snapshot = Dx.Snapshot((Dx.Campaign("k1", "Kick Rust Drops", CampaignScope.General, null, Now.AddYears(5), null, null, ("kd1", 120)), [Dx.Channel("9", "kickstreamer")]));
        _vm = new MainViewModel(
            settings, new FakeAutostart(), _browser, _twitch, new ImmediateDispatcher(), new FakeTimeProvider(Now), TimeZoneInfo.Utc,
            kickService: _kick, clipboard: _clipboard.Add);
    }

    public void Dispose()
    {
        _twitch.Dispose();
        _kick.Dispose();
        _dir.Dispose();
    }

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

    private static TwitchRestoreResult Restored(string login) => new(TwitchRestoreStatus.Restored, new TwitchSession("tok-" + login, "id-" + login, login, null));

    [Fact]
    public async Task KickCard_IsAvailable_AndShowsStateAfterInitialize()
    {
        await _vm.InitializeAsync();

        Assert.True(_vm.HasKick);
        Assert.True(_vm.Kick.IsAvailable);
        Assert.Equal(1.0, _vm.Kick.CardOpacity);
        Assert.Equal("Giriş yapılmadı", _vm.Kick.StatusText);
        Assert.Equal("Giriş yap", _vm.Kick.ActionLabel);
    }

    [Fact]
    public async Task KickLogin_ShowsQrOnCard_DoesNotOpenBrowser_ThenClearsOnSuccess()
    {
        await _vm.InitializeAsync();
        var gate = new TaskCompletionSource<TwitchSession>();
        _kickAccount.Login = (onCode, _) =>
        {
            onCode(new TwitchDeviceCode("A1B2C3D4-0000-4000-8000-000000000001", "123456", KickLink, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4)));
            return gate.Task;
        };

        _vm.Kick.ActionCommand!.Execute(null);
        await WaitUntilAsync(() => _vm.Kick.QrPng is not null);

        Assert.Equal(KickLink, _vm.Kick.ActivateUrl);
        Assert.Null(_vm.Kick.DeviceCode); // Kick'te kod değil QR gösterilir
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], _vm.Kick.QrPng!.Take(4)); // PNG
        Assert.Contains("telefondaki Kick uygulamasıyla", _vm.Kick.StatusText, StringComparison.Ordinal);
        Assert.Empty(_browser.Opened); // bağlantı masaüstü tarayıcıda 404 verir: açılmaz
        Assert.Equal("İptal", _vm.Kick.ActionLabel);

        gate.SetResult(new TwitchSession("kick-token", "7", "kickfan", null));
        await WaitUntilAsync(() => _vm.Kick.StatusText == "Bağlı: kickfan");
        Assert.Null(_vm.Kick.QrPng);
        Assert.Null(_vm.Kick.ActivateUrl);
    }

    [Fact]
    public async Task KickLogin_Cancel_ClearsQr()
    {
        await _vm.InitializeAsync();
        var gate = new TaskCompletionSource<TwitchSession>();
        _kickAccount.Login = (onCode, _) =>
        {
            onCode(new TwitchDeviceCode("u", "123456", KickLink, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4)));
            return gate.Task;
        };
        _vm.Kick.ActionCommand!.Execute(null);
        await WaitUntilAsync(() => _vm.Kick.QrPng is not null);

        _vm.Kick.ActionCommand!.Execute(null); // "İptal"
        gate.TrySetCanceled();
        await WaitUntilAsync(() => _vm.Kick.QrPng is null);

        Assert.Equal("Giriş yapılmadı", _vm.Kick.StatusText);
    }

    [Fact]
    public void CopyLink_PutsLinkOnClipboard()
    {
        _vm.Kick.CopyLinkCommand!.Execute(KickLink);

        Assert.Equal([KickLink.ToString()], _clipboard);
    }

    [Fact]
    public async Task StartPause_DrivesBothPlatforms_Independently()
    {
        _twitchAccount.Restore = Restored("twitchfan");
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _twitch.IsRunning && _kick.IsRunning);
        Assert.Equal("Duraklat", _vm.StartPauseLabel);

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_twitch.IsRunning && !_kick.IsRunning);
        Assert.Equal("Başlat", _vm.StartPauseLabel);
    }

    [Fact]
    public async Task StartPause_OnlyKickLoggedIn_StartsKickOnly()
    {
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();

        Assert.True(_vm.CanStartPause);
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _kick.IsRunning);

        Assert.False(_twitch.IsRunning);
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_kick.IsRunning);
    }

    [Fact]
    public async Task Tooltip_MentionsKickOnlyWhenItIsInUse_AndFitsTrayLimit()
    {
        _twitchAccount.Restore = Restored("twitchfan");
        await _vm.InitializeAsync();
        Assert.Equal("Rust Droper — duraklatıldı (twitchfan)", _vm.TrayTooltip); // Kick girişi yok: eski metin

        _kickAccount.Restore = Restored("kickfan");
        await _kick.RetryRestoreAsync();
        await WaitUntilAsync(() => _vm.TrayTooltip.Contains("Kick:", StringComparison.Ordinal));
        Assert.Contains("Twitch: duraklatıldı (twitchfan)", _vm.TrayTooltip, StringComparison.Ordinal);
        Assert.Contains("Kick: duraklatıldı (kickfan)", _vm.TrayTooltip, StringComparison.Ordinal);
        Assert.True(_vm.TrayTooltip.Length <= 127);

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _vm.TrayTooltip.Contains("KICKSTREAMER", StringComparison.Ordinal));
        Assert.True(_vm.TrayTooltip.Length <= 127);
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_kick.IsRunning && !_twitch.IsRunning);
    }

    [Fact]
    public async Task PlatformSelector_SwitchesDashboardSource()
    {
        _twitchAccount.Restore = Restored("twitchfan");
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();
        _vm.IsWindowVisible = true;
        Assert.Equal(["Base Invaders"], _vm.Dashboard.GeneralCampaigns.Select(c => c.Name));
        Assert.True(_vm.IsTwitchSelected);

        _vm.SelectPlatformCommand.Execute("Kick");

        Assert.True(_vm.IsKickSelected);
        Assert.Equal(Platform.Kick, _vm.SelectedPlatform);
        Assert.Equal(["Kick Rust Drops"], _vm.Dashboard.GeneralCampaigns.Select(c => c.Name));
        Assert.Contains("kick.facepunch.com", _vm.Dashboard.ConnectUrl!.Host, StringComparison.Ordinal);

        _vm.SelectPlatformCommand.Execute(Platform.Twitch);

        Assert.Equal(["Base Invaders"], _vm.Dashboard.GeneralCampaigns.Select(c => c.Name));
        Assert.Equal("twitch.facepunch.com", _vm.Dashboard.ConnectUrl!.Host);
    }

    [Fact]
    public async Task KickRefreshError_ShowsPrefixedNotice()
    {
        _kickAccount.Restore = Restored("kickfan");
        _kickFactory.InventoryError = new HttpRequestException("no route");
        await _vm.InitializeAsync();
        _vm.SelectedPlatform = Platform.Kick; // Kick uyarıları yalnız Kick ekranında görünür

        await WaitUntilAsync(() => _vm.Notice is not null);

        Assert.StartsWith("Kick:", _vm.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutKickService_KickCardStaysComingSoon_AndSelectorIgnoresKick()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("s.json"));
        using var service = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings);
        var vm = new MainViewModel(settings, new FakeAutostart(), new RecordingBrowser(), service, new ImmediateDispatcher());

        vm.SelectPlatformCommand.Execute("Kick");

        Assert.False(vm.HasKick);
        Assert.False(vm.Kick.IsAvailable);
        Assert.Equal(0.6, vm.Kick.CardOpacity);
        Assert.Equal("Yakında", vm.Kick.StatusText);
        Assert.Equal(Platform.Twitch, vm.SelectedPlatform);
    }
}
