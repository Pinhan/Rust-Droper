using Microsoft.Extensions.Time.Testing;
using RustDroper.App.Infrastructure;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.Tests;

internal sealed class RecordingNotifier : INotifier
{
    public List<UserNotification> Shown { get; } = [];

    public Exception? FailWith { get; set; }

    public void Show(UserNotification notification)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Shown.Add(notification);
    }
}

public class NotificationCoordinatorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SettingsService _settings;
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-23T18:00:00Z"));
    private readonly NotificationCoordinator _coordinator;

    public NotificationCoordinatorTests()
    {
        _settings = new SettingsService(_dir.File("settings.json"));
        _coordinator = new NotificationCoordinator(_settings, _notifier, new ImmediateDispatcher(), _clock);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void NotificationsAreOnByDefault_AndShown()
    {
        Assert.True(_settings.Current.Notifications);

        _coordinator.Handle(UserNotification.DropClaimed("d1", "Rug"));

        Assert.Equal("Rug hesabınıza eklendi.", Assert.Single(_notifier.Shown).Text);
    }

    [Fact]
    public void WhenSwitchedOff_NothingIsShown_ExceptTheExplicitTest()
    {
        _settings.Update(s => s with { Notifications = false });

        _coordinator.Handle(UserNotification.DropClaimed("d1", "Rug"));
        _coordinator.Handle(UserNotification.LoginRequired());
        Assert.Empty(_notifier.Shown);

        _coordinator.ShowTest();
        Assert.Equal(NotificationKind.Test, Assert.Single(_notifier.Shown).Kind);
    }

    [Fact]
    public void SameEvent_IsNotRepeated_UntilItsWindowPasses()
    {
        var note = UserNotification.LoginRequired();

        _coordinator.Handle(note);
        _coordinator.Handle(note);
        _clock.Advance(TimeSpan.FromHours(1));
        _coordinator.Handle(note);
        Assert.Single(_notifier.Shown);

        _clock.Advance(TimeSpan.FromHours(3));
        _coordinator.Handle(note);
        Assert.Equal(2, _notifier.Shown.Count);
    }

    [Fact]
    public void EventsSuppressedWhileOff_DoNotConsumeTheirWindow()
    {
        var note = UserNotification.DropClaimed("d1", "Rug");
        _settings.Update(s => s with { Notifications = false });
        _coordinator.Handle(note);

        _settings.Update(s => s with { Notifications = true });
        _coordinator.Handle(note);

        Assert.Single(_notifier.Shown);
    }

    [Fact]
    public void ANotifierThatFails_NeverThrowsIntoTheCaller()
    {
        _notifier.FailWith = new InvalidOperationException("tepsi simgesi yok");

        var ex = Record.Exception(() => _coordinator.Handle(UserNotification.DropClaimed("d1", "Rug")));

        Assert.Null(ex);
    }

    [Fact]
    public void ShowTest_RepeatsEveryTime()
    {
        _coordinator.ShowTest();
        _coordinator.ShowTest();

        Assert.Equal(2, _notifier.Shown.Count);
    }
}

public class NotificationSettingsTests
{
    [Fact]
    public void Notifications_DefaultOn_AndToggleIsPersisted()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        var vm = new SettingsViewModel(settings, new FakeAutostart());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(vm.Notifications);
        vm.Notifications = false;

        Assert.False(new SettingsService(dir.File("settings.json")).Current.Notifications);
        Assert.Contains(nameof(SettingsViewModel.Notifications), changed);
    }

    [Fact]
    public void OldSettingsFileWithoutTheKey_StillDefaultsToOn()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), """{ "schemaVersion": 1, "data": { "autoClaim": false, "startMinimized": true } }""");

        var settings = new SettingsService(dir.File("settings.json"));

        Assert.True(settings.Current.Notifications);
        Assert.True(settings.Current.StartMinimized);
    }

    [Fact]
    public void TestNotificationCommand_RaisesTheRequest()
    {
        using var dir = new TempDir();
        var vm = new SettingsViewModel(new SettingsService(dir.File("settings.json")), new FakeAutostart());
        var raised = 0;
        vm.TestNotificationRequested += () => raised++;

        vm.TestNotificationCommand.Execute(null);

        Assert.Equal(1, raised);
    }
}
