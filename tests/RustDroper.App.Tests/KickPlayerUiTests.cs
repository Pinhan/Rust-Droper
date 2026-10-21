using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;

namespace RustDroper.App.Tests;

internal sealed class FakePlayerSession : IKickPlayerSession
{
    private KickPlayerSessionStatus _status = KickPlayerSessionStatus.Unknown;

    public KickPlayerSessionStatus Status => _status;

    public event Action<KickPlayerSessionStatus>? StatusChanged;

    public int Refreshes { get; private set; }

    public int Logins { get; private set; }

    /// <summary>Yoklama sonucunda durum buna ayarlanır.</summary>
    public KickPlayerSessionStatus RefreshResult { get; set; } = new(KickPlayerSessionState.LoggedIn);

    public void Set(KickPlayerSessionStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public Task<KickPlayerSessionStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        Refreshes++;
        Set(RefreshResult);
        return Task.FromResult(RefreshResult);
    }

    public Task LoginAsync(CancellationToken cancellationToken)
    {
        Logins++;
        return Task.CompletedTask;
    }
}

/// <summary>Kick kartındaki "Oynatıcı oturumu" satırı + "Oynatıcı girişi" düğmesi + oturum yokken Kick'i başlatmama.</summary>
public class KickPlayerUiTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

    private readonly TempDir _dir = new();
    private readonly StubAccount _twitchAccount = new();
    private readonly StubAccount _kickAccount = new();
    private readonly FakePlayerSession _player = new();
    private readonly DropperService _twitch;
    private readonly DropperService _kick;
    private readonly MainViewModel _vm;

    public KickPlayerUiTests()
    {
        var settings = new SettingsService(_dir.File("settings.json"));
        EngineOptions Options(AppSettings s) => new() { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(20) };
        var twitchPlatform = new StubPlatform();
        var kickPlatform = new StubPlatform();
        _twitch = new DropperService(_twitchAccount, new StubFactory(twitchPlatform), settings, engineOptions: Options);
        _kick = new DropperService(_kickAccount, new StubFactory(kickPlatform), settings, engineOptions: Options, platform: PlatformProfile.Kick);
        kickPlatform.Snapshot = Dx.Snapshot((Dx.Campaign("k1", "Kick Rust Drops", CampaignScope.General, null, Now.AddYears(5), null, null, ("kd1", 120)), [Dx.Channel("9", "kickstreamer")]));
        _vm = new MainViewModel(
            settings, new FakeAutostart(), new RecordingBrowser(), _twitch, new ImmediateDispatcher(), new FakeTimeProvider(Now), TimeZoneInfo.Utc,
            kickService: _kick, kickPlayer: _player);
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

    [Theory]
    [InlineData(KickPlayerSessionState.LoggedIn, "Oynatıcı oturumu: açık", Severity.Ok)]
    [InlineData(KickPlayerSessionState.LoggedOut, "Oynatıcı oturumu: yok", Severity.Warn)]
    [InlineData(KickPlayerSessionState.Unknown, "Oynatıcı oturumu: doğrulanmadı", Severity.Neutral)]
    [InlineData(KickPlayerSessionState.Checking, "kontrol ediliyor", Severity.Info)]
    [InlineData(KickPlayerSessionState.Unavailable, "WebView2 Runtime bulunamadı", Severity.Error)]
    public async Task Card_ShowsPlayerSessionState(KickPlayerSessionState state, string expectedText, Severity severity)
    {
        await _vm.InitializeAsync();

        _player.Set(new KickPlayerSessionStatus(state));
        await WaitUntilAsync(() => _vm.Kick.PlayerSessionSeverity == severity);

        Assert.Contains(expectedText, _vm.Kick.PlayerSessionText, StringComparison.Ordinal);
        Assert.NotNull(_vm.Kick.PlayerLoginCommand);
    }

    [Fact]
    public async Task Card_ShowsLastVerificationDetail_WhenLoggedIn()
    {
        await _vm.InitializeAsync();

        _player.Set(new KickPlayerSessionStatus(KickPlayerSessionState.LoggedIn, "son doğrulama 19.10 13:05"));
        await WaitUntilAsync(() => _vm.Kick.PlayerSessionSeverity == Severity.Ok);

        Assert.Equal("Oynatıcı oturumu: açık (son doğrulama 19.10 13:05)", _vm.Kick.PlayerSessionText);
    }

    [Fact]
    public async Task PlayerLoginButton_OpensLoginWindow_ViaSession()
    {
        await _vm.InitializeAsync();

        _vm.Kick.PlayerLoginCommand!.Execute(null);
        await WaitUntilAsync(() => _player.Logins == 1);

        Assert.Equal(1, _player.Logins);
    }

    [Fact]
    public async Task PlayerRefreshButton_ChecksSession()
    {
        await _vm.InitializeAsync();

        _vm.Kick.PlayerRefreshCommand!.Execute(null);
        await WaitUntilAsync(() => _player.Refreshes == 1);

        await WaitUntilAsync(() => _vm.Kick.PlayerSessionSeverity == Severity.Ok);
    }

    [Fact]
    public async Task Start_WithPlayerSessionMissing_DoesNotStartKick_AndExplainsWhy()
    {
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();
        _vm.SelectedPlatform = Platform.Kick; // Kick uyarıları yalnız Kick ekranında görünür
        _player.Set(new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut));

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _vm.Notice?.Contains("Oynatıcı girişi", StringComparison.Ordinal) == true);

        Assert.False(_kick.IsRunning);
        Assert.Contains("oynatıcı oturumu yok", _vm.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_WithUnknownSession_VerifiesFirst_ThenStartsWhenLoggedIn()
    {
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();
        _player.RefreshResult = new KickPlayerSessionStatus(KickPlayerSessionState.LoggedIn);

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _kick.IsRunning);

        Assert.Equal(1, _player.Refreshes);
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_kick.IsRunning);
    }

    [Fact]
    public async Task Start_WithUnknownSession_ThatTurnsOutLoggedOut_DoesNotStart()
    {
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();
        _vm.SelectedPlatform = Platform.Kick; // Kick uyarıları yalnız Kick ekranında görünür
        _player.RefreshResult = new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut);

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _vm.Notice?.Contains("Oynatıcı girişi", StringComparison.Ordinal) == true);

        Assert.False(_kick.IsRunning);
    }

    [Fact]
    public async Task Start_WithWebView2Missing_DoesNotStartKick_ButTwitchStillStarts()
    {
        _twitchAccount.Restore = Restored("twitchfan");
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();
        _vm.SelectedPlatform = Platform.Kick; // Kick uyarıları yalnız Kick ekranında görünür
        _player.Set(new KickPlayerSessionStatus(KickPlayerSessionState.Unavailable, "WebView2 Runtime yok"));

        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _twitch.IsRunning);

        Assert.False(_kick.IsRunning);
        Assert.Contains("WebView2", _vm.Notice, StringComparison.Ordinal);
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_twitch.IsRunning);
    }

    [Fact]
    public async Task BlockedNotice_ClearsOnceSessionBecomesLoggedIn()
    {
        _kickAccount.Restore = Restored("kickfan");
        await _vm.InitializeAsync();
        _vm.SelectedPlatform = Platform.Kick; // Kick uyarıları yalnız Kick ekranında görünür
        _player.Set(new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut));
        _vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _vm.Notice?.Contains("Oynatıcı girişi", StringComparison.Ordinal) == true);

        _player.Set(new KickPlayerSessionStatus(KickPlayerSessionState.LoggedIn));
        await WaitUntilAsync(() => _vm.Notice?.Contains("Oynatıcı girişi", StringComparison.Ordinal) != true);

        Assert.DoesNotContain("oynatıcı oturumu yok", _vm.Notice ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutPlayerSession_KickCardHasNoPlayerRow_AndStartIsUnchanged()
    {
        // Oynatıcı oturumu verilmezse (CLI/eski kurulum) kart satırı yok ve Kick eskisi gibi başlar.
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        var kickAccount = new StubAccount { Restore = Restored("kickfan") };
        var kickPlatform = new StubPlatform();
        kickPlatform.Snapshot = Dx.Snapshot((Dx.Campaign("k1", "K", CampaignScope.General, null, Now.AddYears(5), null, null, ("kd1", 120)), [Dx.Channel("9", "ks")]));
        EngineOptions Options(AppSettings s) => new() { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(20) };
        using var twitch = new DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings, engineOptions: Options);
        using var kick = new DropperService(kickAccount, new StubFactory(kickPlatform), settings, engineOptions: Options, platform: PlatformProfile.Kick);
        var vm = new MainViewModel(settings, new FakeAutostart(), new RecordingBrowser(), twitch, new ImmediateDispatcher(), new FakeTimeProvider(Now), TimeZoneInfo.Utc, kickService: kick);

        await vm.InitializeAsync();
        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => kick.IsRunning);

        Assert.Null(vm.Kick.PlayerSessionText);
        Assert.Null(vm.Kick.PlayerLoginCommand);
        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !kick.IsRunning);
    }
}
