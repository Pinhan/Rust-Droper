using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

internal sealed class FakeStartNotice(bool dontShowAgain = false) : IStartNoticePrompt
{
    public int Shown { get; private set; }

    public Task<StartNoticeAnswer> ShowAsync()
    {
        Shown++;
        return Task.FromResult(new StartNoticeAnswer(dontShowAgain));
    }
}

/// <summary>Başlat uyarısı: yalnız Başlat'a basılınca ve pencere açıkken; Duraklat'ta ve gizli pencerede değil; "Bir daha gösterme" kalıcı.</summary>
public class StartNoticeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SettingsService _settings;
    private readonly StubAccount _account = new();
    private readonly DropperService _service;

    public StartNoticeTests()
    {
        _settings = new SettingsService(_dir.File("settings.json"));
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, new TwitchSession("tok", "42", "fan", null));
        _service = new DropperService(
            _account, new StubFactory(new StubPlatform()), _settings, engineOptions: s => new EngineOptions { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(20) });
    }

    public void Dispose()
    {
        _service.Dispose();
        _dir.Dispose();
    }

    private MainViewModel Create(IStartNoticePrompt? prompt, bool windowVisible = true)
    {
        var vm = new MainViewModel(
            _settings, new FakeAutostart(), new RecordingBrowser(), _service, new ImmediateDispatcher(), new FakeTimeProvider(DateTimeOffset.UtcNow), TimeZoneInfo.Utc, startNotice: prompt);
        vm.IsWindowVisible = windowVisible;
        return vm;
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

    [Fact]
    public async Task Start_ShowsTheNoticeOnce_ThenStarts_AndPauseDoesNotShowIt()
    {
        var prompt = new FakeStartNotice();
        var vm = Create(prompt);
        await vm.InitializeAsync();

        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _service.IsRunning && vm.StartPauseCommand.CanExecute(null)); // komut bitene kadar bekle (yeniden giriş korumalı)
        Assert.Equal(1, prompt.Shown);

        vm.StartPauseCommand.Execute(null); // Duraklat
        await WaitUntilAsync(() => !_service.IsRunning && vm.StartPauseCommand.CanExecute(null));
        Assert.Equal(1, prompt.Shown);

        vm.StartPauseCommand.Execute(null); // tekrar Başlat: ayar kapatılmadığı için yine gösterilir
        await WaitUntilAsync(() => _service.IsRunning && vm.StartPauseCommand.CanExecute(null)); // komut bitene kadar bekle (yeniden giriş korumalı)
        Assert.Equal(2, prompt.Shown);
        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_service.IsRunning && vm.StartPauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task HiddenWindow_NoNotice_ButEngineStarts()
    {
        var prompt = new FakeStartNotice();
        var vm = Create(prompt, windowVisible: false); // tepsi menüsünden Başlat: pencere görünmüyor
        await vm.InitializeAsync();

        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _service.IsRunning && vm.StartPauseCommand.CanExecute(null)); // komut bitene kadar bekle (yeniden giriş korumalı)

        Assert.Equal(0, prompt.Shown);
        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_service.IsRunning && vm.StartPauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task DontShowAgain_IsPersisted_AndSuppressesFutureNotices()
    {
        var prompt = new FakeStartNotice(dontShowAgain: true);
        var vm = Create(prompt);
        await vm.InitializeAsync();

        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _service.IsRunning && vm.StartPauseCommand.CanExecute(null)); // komut bitene kadar bekle (yeniden giriş korumalı)
        Assert.True(_settings.Current.HideStartNotice);
        Assert.True(new SettingsService(_dir.File("settings.json")).Current.HideStartNotice); // kalıcı
        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_service.IsRunning && vm.StartPauseCommand.CanExecute(null));

        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _service.IsRunning && vm.StartPauseCommand.CanExecute(null)); // komut bitene kadar bekle (yeniden giriş korumalı)
        Assert.Equal(1, prompt.Shown); // artık gösterilmedi
        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_service.IsRunning && vm.StartPauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutPrompt_StartWorksAsBefore()
    {
        var vm = Create(null);
        await vm.InitializeAsync();

        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => _service.IsRunning && vm.StartPauseCommand.CanExecute(null)); // komut bitene kadar bekle (yeniden giriş korumalı)

        vm.StartPauseCommand.Execute(null);
        await WaitUntilAsync(() => !_service.IsRunning && vm.StartPauseCommand.CanExecute(null));
    }

    [Fact]
    public void SettingsSwitch_IsTheInverseOfHideStartNotice()
    {
        var vm = Create(null);

        Assert.True(vm.Settings.ShowStartNotice); // varsayılan: gösterilir
        vm.Settings.ShowStartNotice = false;
        Assert.True(_settings.Current.HideStartNotice);
        vm.Settings.ShowStartNotice = true;
        Assert.False(_settings.Current.HideStartNotice);
    }

    [Fact]
    public void OldSettingsFile_DefaultsToShowing()
    {
        File.WriteAllText(_dir.File("old.json"), "{\"schemaVersion\":1,\"data\":{\"autoClaim\":true}}");

        Assert.False(new SettingsService(_dir.File("old.json")).Current.HideStartNotice);
    }

    [Fact]
    public void NoticeText_WarnsAboutWatchingAnotherLiveStream()
    {
        Assert.Contains("başka bir canlı yayın izlemeyin", StartNoticeText.Message, StringComparison.Ordinal);
        Assert.Contains("Twitch/Kick", StartNoticeText.Message, StringComparison.Ordinal);
    }
}
