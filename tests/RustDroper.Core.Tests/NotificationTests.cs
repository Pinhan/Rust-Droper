using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Tests;

public class NotificationGateTests
{
    private static readonly DateTimeOffset Now = Fx.Start;

    [Fact]
    public void SameKey_IsSuppressedUntilTheWindowPasses()
    {
        var gate = new NotificationGate();
        var note = UserNotification.CampaignStarted("c1", "Base Invaders", "streamer");

        Assert.True(gate.TryPass(note, Now));
        Assert.False(gate.TryPass(note, Now.AddMinutes(30)));
        Assert.True(gate.TryPass(note, Now.AddHours(7)));
    }

    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var gate = new NotificationGate();

        Assert.True(gate.TryPass(UserNotification.DropClaimed("d1", "Rug"), Now));
        Assert.True(gate.TryPass(UserNotification.DropClaimed("d2", "Bed"), Now));
        Assert.False(gate.TryPass(UserNotification.DropClaimed("d1", "Rug"), Now.AddSeconds(1)));
    }

    [Fact]
    public void TestNotification_AlwaysPasses()
    {
        var gate = new NotificationGate();

        Assert.True(gate.TryPass(UserNotification.Test(), Now));
        Assert.True(gate.TryPass(UserNotification.Test(), Now));
    }

    [Fact]
    public void ClaimFailureAndReady_CarryTheInventoryLink()
    {
        Assert.Equal(UserNotification.TwitchInventory, UserNotification.ClaimFailed("d1", "Rug").Link);
        Assert.Equal(UserNotification.TwitchInventory, UserNotification.ClaimReady("d1", "Rug").Link);
        Assert.Contains("elle claim", UserNotification.ClaimFailed("d1", "Rug").Text);
    }
}

public class DropperServiceNotificationTests : IDisposable
{
    private static readonly TwitchSession Session = new("tok-1", "42", "testuser", null);
    private readonly TempDir _dir = new();
    private readonly FakeAccount _account = new();
    private readonly FakeConnectionFactory _factory = new();
    private readonly FakeTimeProvider _clock = new(Fx.Start);
    private readonly SettingsService _settings;
    private readonly DropperService _service;
    private readonly List<UserNotification> _notes = [];

    public DropperServiceNotificationTests()
    {
        _settings = new SettingsService(_dir.File("settings.json"));
        _service = new DropperService(_account, _factory, _settings, _clock);
        _service.Notification += n =>
        {
            lock (_notes)
            {
                _notes.Add(n);
            }
        };
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, Session);
    }

    public void Dispose()
    {
        _service.Dispose();
        _dir.Dispose();
    }

    private List<UserNotification> Notes
    {
        get
        {
            lock (_notes)
            {
                return [.. _notes];
            }
        }
    }

    private static Campaign Campaign() =>
        Fx.Camp("c1", "Base Invaders", CampaignScope.General, Fx.Start.AddDays(9), ("d1", 60)) with
        {
            Rewards = [new DropReward("d1", "Rug", 60, null)],
        };

    private async Task AdvanceUntilAsync(Func<bool> condition, int timeoutMs = 8000, int stepSeconds = 1)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Koşul zamanında gerçekleşmedi.");
            }

            _clock.Advance(TimeSpan.FromSeconds(stepSeconds));
            await Task.Delay(5);
        }
    }

    private async Task StartWatchingAsync()
    {
        _factory.Platform.Snapshot = Fx.Snap((Campaign(), [Fx.Ch("1", "streamer")]));
        _factory.Platform.Session = _ => new DropSession("d1", 5, 60);
        await _service.InitializeAsync();
        _service.Start();
        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Watching);
    }

    [Fact]
    public async Task SuccessfulClaim_NotifiesWithTheRewardName()
    {
        await StartWatchingAsync();

        _factory.Platform.RaiseClaim(new ClaimNotice("d1", true, "ELIGIBLE_FOR_ALL"));

        var note = Assert.Single(Notes, n => n.Kind == NotificationKind.DropClaimed);
        Assert.Contains("Rug", note.Text);
        Assert.Null(note.Link);
        await _service.StopAsync();
    }

    [Fact]
    public async Task FailedClaim_NotifiesWithManualClaimAdviceAndInventoryLink()
    {
        await StartWatchingAsync();

        _factory.Platform.RaiseClaim(new ClaimNotice("d1", false, "Rejected"));

        var note = Assert.Single(Notes, n => n.Kind == NotificationKind.ClaimFailed);
        Assert.Contains("Twitch envanterinden elle claim edin", note.Text);
        Assert.Equal(UserNotification.TwitchInventory, note.Link);
        await _service.StopAsync();
    }

    [Fact]
    public async Task ClaimAfterTheEngineStopped_IsNotReportedByAStaleSubscription()
    {
        await StartWatchingAsync();
        await _service.StopAsync();

        _factory.Platform.RaiseClaim(new ClaimNotice("d1", true, "ELIGIBLE_FOR_ALL"));

        Assert.DoesNotContain(Notes, n => n.Kind == NotificationKind.DropClaimed);
    }

    [Fact]
    public async Task CompletedDrop_WithAutoClaimOff_NotifiesOnce_ThatItIsReadyToClaim()
    {
        _settings.Update(s => s with { AutoClaim = false });
        _factory.Platform.Snapshot = Fx.Snap((Campaign(), [Fx.Ch("1", "streamer")]));
        _factory.Platform.Session = _ => new DropSession("d1", 60, 60);
        await _service.InitializeAsync();
        _service.Start();

        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.ClaimPending);
        await AdvanceUntilAsync(() => _clock.GetUtcNow() > Fx.Start.AddMinutes(4), stepSeconds: 10); // birkaç yoklama daha
        await _service.StopAsync();

        var note = Assert.Single(Notes, n => n.Kind == NotificationKind.ClaimReady);
        Assert.Contains("Rug", note.Text);
        Assert.Equal(UserNotification.TwitchInventory, note.Link);
    }

    [Fact]
    public async Task CompletedDrop_WithAutoClaimOn_DoesNotAskForManualClaim()
    {
        _settings.Update(s => s with { AutoClaim = true });
        _factory.Platform.Snapshot = Fx.Snap((Campaign(), [Fx.Ch("1", "streamer")]));
        _factory.Platform.Session = _ => new DropSession("d1", 60, 60);
        await _service.InitializeAsync();
        _service.Start();

        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.ClaimPending);
        await _service.StopAsync();

        Assert.DoesNotContain(Notes, n => n.Kind == NotificationKind.ClaimReady);
    }

    [Fact]
    public async Task IdleThenWatching_NotifiesCampaignStarted_ButJoiningMidCampaignDoesNot()
    {
        // Kampanya ortasında açılış: boşta görülmedi → bildirim yok.
        await StartWatchingAsync();
        Assert.DoesNotContain(Notes, n => n.Kind == NotificationKind.CampaignStarted);
        await _service.StopAsync();

        // Boşta bekleyen motor: kampanya belirince tek bildirim.
        _factory.Platform.Snapshot = Fx.Snap();
        _service.Start();
        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Idle);

        _factory.Platform.Snapshot = Fx.Snap((Campaign(), [Fx.Ch("1", "streamer")]));
        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Watching, stepSeconds: 10);
        await AdvanceUntilAsync(() => _clock.GetUtcNow() > Fx.Start.AddMinutes(20), stepSeconds: 10);
        await _service.StopAsync();

        var note = Assert.Single(Notes, n => n.Kind == NotificationKind.CampaignStarted);
        Assert.Contains("Base Invaders", note.Text);
        Assert.Contains("streamer", note.Text);
    }

    [Fact]
    public async Task SessionBecomingInvalid_NotifiesLoginRequiredOnce()
    {
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.NeedsLogin, null);

        await _service.InitializeAsync();
        await _service.RetryRestoreAsync(); // yine NeedsLogin: tekrar bildirilmemeli

        var note = Assert.Single(Notes, n => n.Kind == NotificationKind.LoginRequired);
        Assert.Contains("yeniden giriş", note.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailingNotificationListener_DoesNotBreakTheEngine()
    {
        _service.Notification += _ => throw new InvalidOperationException("balon gösterilemedi");
        await StartWatchingAsync();

        _factory.Platform.RaiseClaim(new ClaimNotice("d1", true, "ELIGIBLE_FOR_ALL"));

        Assert.True(_service.IsRunning);
        await _service.StopAsync();
    }
}
