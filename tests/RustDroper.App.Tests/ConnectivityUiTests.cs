using RustDroper.App.Infrastructure;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

internal sealed class StubSystemEvents : ISystemEvents
{
    public event Action? Suspending
    {
        add { }
        remove { }
    }

    public event Action? Resumed
    {
        add { }
        remove { }
    }

    public event Action<bool>? NetworkAvailabilityChanged;

    public void Network(bool available) => NetworkAvailabilityChanged?.Invoke(available);
}

public class ConnectivityUiTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StubAccount _account = new();
    private readonly StubSystemEvents _events = new();
    private readonly DropperService _service;
    private readonly MainViewModel _vm;

    public ConnectivityUiTests()
    {
        var settings = new SettingsService(_dir.File("settings.json"));
        _service = new DropperService(_account, new StubFactory(new StubPlatform()), settings, systemEvents: _events);
        _vm = new MainViewModel(settings, new FakeAutostart(), new RecordingBrowser(), _service, new ImmediateDispatcher());
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

    [Fact]
    public async Task LosingTheConnection_ShowsNoticeTooltipAndOfflineCard_ThenClearsWhenItReturns()
    {
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, new TwitchSession("tok", "42", "testuser", null));
        await _vm.InitializeAsync();
        Assert.Null(_vm.Notice);

        _events.Network(false);

        Assert.Contains("İnternet bağlantısı yok", _vm.Notice);
        Assert.Contains("internet bağlantısı yok", _vm.TrayTooltip);
        Assert.Contains("Çevrimdışı (testuser)", _vm.Twitch.StatusText);

        _events.Network(true);
        await WaitUntilAsync(() => _vm.Notice is null);

        await WaitUntilAsync(() => _vm.Twitch.StatusText.StartsWith("Bağlı", StringComparison.Ordinal));
        Assert.DoesNotContain("internet", _vm.TrayTooltip, StringComparison.Ordinal);
    }
}

public class ConnectionNotificationTests
{
    [Fact]
    public void OutageDuration_IsFormattedReadably()
    {
        Assert.Equal("45 sn", UserNotification.FormatDuration(TimeSpan.FromSeconds(45)));
        Assert.Equal("2 dk 10 sn", UserNotification.FormatDuration(TimeSpan.FromSeconds(130)));
        Assert.Equal("1 sa 5 dk", UserNotification.FormatDuration(TimeSpan.FromMinutes(65)));
        Assert.Equal("0 sn", UserNotification.FormatDuration(TimeSpan.Zero));
    }

    [Fact]
    public void LostAndRestored_AreDistinctNotificationsWithShortWindows()
    {
        var lost = UserNotification.ConnectionLost();
        var restored = UserNotification.ConnectionRestored(TimeSpan.FromSeconds(95));

        Assert.NotEqual(lost.Key, restored.Key);
        Assert.Contains("1 dk 35 sn", restored.Text);
        Assert.True(lost.SuppressFor <= TimeSpan.FromMinutes(1));
        Assert.True(restored.SuppressFor <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Coordinator_ShowsThem_AndSuppressesRapidFlapping()
    {
        using var dir = new TempDir();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T18:00:00Z"));
        var notifier = new RecordingNotifier();
        var coordinator = new NotificationCoordinator(new SettingsService(dir.File("settings.json")), notifier, new ImmediateDispatcher(), clock);

        coordinator.Handle(UserNotification.ConnectionLost());
        coordinator.Handle(UserNotification.ConnectionLost()); // çırpma: aynı saniyede tekrar
        clock.Advance(TimeSpan.FromSeconds(30));
        coordinator.Handle(UserNotification.ConnectionLost());
        coordinator.Handle(UserNotification.ConnectionRestored(TimeSpan.FromSeconds(30)));

        Assert.Equal(
            [NotificationKind.ConnectionLost, NotificationKind.ConnectionLost, NotificationKind.ConnectionRestored],
            notifier.Shown.Select(n => n.Kind));
    }
}
