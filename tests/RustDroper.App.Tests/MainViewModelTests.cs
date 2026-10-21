using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;

namespace RustDroper.App.Tests;

internal sealed class StubAccount : IPlatformAccount
{
    public TwitchRestoreResult Restore { get; set; } = new(TwitchRestoreStatus.NotLoggedIn, null);

    public Func<Action<TwitchDeviceCode>, CancellationToken, Task<TwitchSession>>? Login { get; set; }

    public Task<TwitchRestoreResult> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(Restore);

    public Task<TwitchSession> LoginAsync(Action<TwitchDeviceCode> onCode, CancellationToken cancellationToken = default) =>
        Login!(onCode, cancellationToken);

    public void Logout()
    {
    }
}

internal sealed class StubPlatform : IDropPlatform
{
    public PlatformSnapshot Snapshot { get; set; } = new([], new Dictionary<string, IReadOnlyList<LiveChannel>>());

    /// <summary>İzleme sırasında dönen sunucu oturumu (varsayılan: d1, 10/60 dk).</summary>
    public DropSession Session { get; set; } = new("d1", 10, 60);

    public Platform Platform => Platform.Twitch;

    public event Action<string>? ChannelWentOffline
    {
        add { }
        remove { }
    }

    public event Action<ClaimNotice>? DropClaimed
    {
        add { }
        remove { }
    }

    public Task<PlatformSnapshot> DiscoverAsync(string gameSlug, CancellationToken cancellationToken) => Task.FromResult(Snapshot);

    public IWatcher CreateWatcher() => new Watcher();

    public Task<DropSession?> GetSessionAsync(LiveChannel channel, CancellationToken cancellationToken) =>
        Task.FromResult<DropSession?>(Session);

    public void OnTargetChanged(LiveChannel? channel)
    {
    }

    public Task RunBackgroundAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

    private sealed class Watcher : IWatcher
    {
        public WatcherStats Stats { get; } = new();

        public async Task<WatchResult> RunAsync(LiveChannel channel, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return new WatchResult(WatchEnd.Cancelled, null);
        }
    }
}

internal sealed class StubFactory(StubPlatform platform) : IDropConnectionFactory
{
    public Exception? InventoryError { get; set; }

    public IDropConnection Create(TwitchSession session, Func<string?> accessToken, bool autoClaim) => new Connection(platform, this);

    public IReadOnlyCollection<string> LoadClaimedDropIds() => [];

    private sealed class Connection(StubPlatform platform, StubFactory owner) : IDropConnection
    {
        public IDropPlatform Platform => platform;

        public Task<TwitchInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken) =>
            owner.InventoryError is { } error ? Task.FromException<TwitchInventorySnapshot>(error) : Task.FromResult(new TwitchInventorySnapshot([], []));

        public void Dispose()
        {
        }
    }
}

public class MainViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");
    private readonly TempDir _dir = new();
    private readonly StubAccount _account = new();
    private readonly StubPlatform _platform = new();
    private readonly StubFactory _factory;
    private readonly RecordingBrowser _browser = new();
    private readonly DropperService _service;
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _factory = new StubFactory(_platform);
        var settings = new SettingsService(_dir.File("settings.json"));
        _service = new DropperService(_account, _factory, settings, engineOptions: s => new EngineOptions { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(20) });
        var campaign = Dx.Campaign("c1", "Base Invaders", CampaignScope.General, null, Now.AddYears(5), null, null, ("d1", 60));
        _platform.Snapshot = Dx.Snapshot((campaign, [Dx.Channel("1", "streamer")]));
        _vm = new MainViewModel(settings, new FakeAutostart(), _browser, _service, new ImmediateDispatcher(), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now), TimeZoneInfo.Utc);
    }

    public void Dispose()
    {
        _service.Dispose();
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

    private async Task LoggedInAsync()
    {
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, new TwitchSession("tok", "42", "testuser", null));
        await _vm.InitializeAsync();
    }

    [Fact]
    public void StartsOnDashboard_AndTogglesSettingsPage()
    {
        Assert.False(_vm.ShowSettings);
        Assert.Equal("Ayarlar", _vm.SettingsButtonLabel);

        _vm.ToggleSettingsCommand.Execute(null);

        Assert.True(_vm.ShowSettings);
        Assert.Equal("← Pano", _vm.SettingsButtonLabel);
    }

    [Fact]
    public void BeforeLogin_StartIsDisabled_AndCardOffersLogin()
    {
        Assert.False(_vm.CanStartPause);
        Assert.Equal("Başlat", _vm.StartPauseLabel);
        Assert.Equal("Kontrol ediliyor…", _vm.Twitch.StatusText);
        Assert.False(_vm.Kick.IsAvailable);
    }

    [Fact]
    public async Task LoggedIn_EnablesStart_AndShowsAccount()
    {
        await LoggedInAsync();

        Assert.Equal("Bağlı: testuser", _vm.Twitch.StatusText);
        Assert.True(_vm.CanStartPause);
        Assert.Equal("Rust Droper — duraklatıldı (testuser)", _vm.TrayTooltip);
    }

    [Fact]
    public async Task StartPause_TogglesEngine_LabelAndTooltip()
    {
        await LoggedInAsync();

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _vm.StartPauseLabel == "Duraklat" && _vm.TrayTooltip.Contains("10/60 dk"));
        Assert.Contains("STREAMER", _vm.TrayTooltip);

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _vm.StartPauseLabel == "Başlat");
        Assert.Equal("Rust Droper — duraklatıldı (testuser)", _vm.TrayTooltip);
    }

    [Fact]
    public async Task Dashboard_IsRebuiltOnlyWhileWindowIsVisible()
    {
        await LoggedInAsync();
        Assert.Empty(_vm.Dashboard.GeneralCampaigns); // pencere kapalı: kurulmadı

        _vm.IsWindowVisible = true;

        Assert.Equal(["Base Invaders"], _vm.Dashboard.GeneralCampaigns.Select(c => c.Name));
        Assert.Equal("Durduruldu", _vm.Dashboard.Now.BadgeText);

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _vm.Dashboard.Now.PercentText == "%17");
        Assert.Equal("STREAMER", _vm.Dashboard.Now.ChannelName);
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_vm.Dashboard.Now.HasTarget);
    }

    [Fact]
    public async Task DeviceCode_OpensActivationPageInBrowser_AndShowsCodeOnCard()
    {
        var gate = new TaskCompletionSource<TwitchSession>();
        _account.Login = (onCode, _) =>
        {
            onCode(new TwitchDeviceCode("dev", "ABCD-EFGH", new Uri("https://www.twitch.tv/activate?device-code=ABCD-EFGH"), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5)));
            return gate.Task;
        };

        _vm.LoginCommand.Execute(null);
        await WaitUntilAsync(() => _vm.Twitch.DeviceCode == "ABCD-EFGH");

        Assert.Equal([new Uri("https://www.twitch.tv/activate?device-code=ABCD-EFGH")], _browser.Opened);
        Assert.Equal("İptal", _vm.Twitch.ActionLabel);

        _vm.CancelLoginCommand.Execute(null);
        gate.TrySetCanceled();
        await WaitUntilAsync(() => _vm.Twitch.DeviceCode is null);
    }

    [Fact]
    public async Task RefreshError_IsShownAsNotice_AndClearedOnSuccess()
    {
        await LoggedInAsync();
        _factory.InventoryError = new HttpRequestException("ağ yok");

        _vm.RefreshCommand.Execute(null);
        await WaitUntilAsync(() => _vm.Notice is not null);
        Assert.Contains("ağ yok", _vm.Notice);

        _factory.InventoryError = null;
        _vm.RefreshCommand.Execute(null);
        await WaitUntilAsync(() => _vm.Notice is null);
    }

    [Fact]
    public async Task NeedsLogin_ShowsReloginPrompt_AndDisablesStart()
    {
        await LoggedInAsync();
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.NeedsLogin, null);

        await _service.RetryRestoreAsync(); // kayıtlı oturum artık geçersiz

        Assert.Equal("Oturum geçersiz — yeniden giriş gerekli", _vm.Twitch.StatusText);
        Assert.Equal("Yeniden giriş yap", _vm.Twitch.ActionLabel);
        Assert.False(_vm.CanStartPause);
        Assert.Equal("Rust Droper — yeniden giriş gerekli", _vm.TrayTooltip);
    }

    [Fact]
    public async Task Shutdown_StopsRunningEngine()
    {
        await LoggedInAsync();
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _service.IsRunning);

        await _vm.ShutdownAsync();

        Assert.False(_service.IsRunning);
    }

    [Fact]
    public void Tooltip_Variants()
    {
        var engine = new EngineSnapshot(EngineState.Idle, "x", null, null, null, 0, []);
        DropperSnapshot Snap(AccountSnapshot account, bool running = false) =>
            new(account, engine, running, null, null, new HashSet<string>(), null);

        Assert.Equal("Rust Droper — Twitch girişi yok", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.LoggedOut))));
        Assert.Equal("Rust Droper — çevrimdışı", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.Offline))));
        Assert.Equal("Rust Droper — Boşta", MainViewModel.BuildTooltip(Snap(new AccountSnapshot(AccountStatus.LoggedIn, "a"), running: true)));
        Assert.Equal("Rust Droper", MainViewModel.BuildTooltip(Snap(AccountSnapshot.Initial)));
    }

    [Theory]
    [InlineData(EngineState.Watching, true, false, true)]
    [InlineData(EngineState.ClaimPending, true, false, true)]
    [InlineData(EngineState.Idle, true, false, false)]
    [InlineData(EngineState.Watching, false, false, false)]
    [InlineData(EngineState.Watching, true, true, false)]
    public void ClaimOffNotice_AppearsOnlyWhileWorkingWithAutoClaimOff(EngineState state, bool running, bool autoClaim, bool expectNotice)
    {
        var snapshot = new DropperSnapshot(
            AccountSnapshot.Initial,
            new EngineSnapshot(state, "x", null, null, null, 0, []),
            running,
            null,
            null,
            new HashSet<string>(),
            null);

        var notice = MainViewModel.ClaimOffNotice(autoClaim, snapshot);

        Assert.Equal(expectNotice, notice is not null);
        if (expectNotice)
        {
            Assert.Contains("Otomatik claim KAPALI", notice, StringComparison.Ordinal);
        }
    }
}
