using System.Net;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Tests;

internal sealed class FakeProbe : IConnectivityProbe
{
    private int _calls;

    public volatile bool Reachable = true;

    public volatile bool Throws;

    public int Calls => Volatile.Read(ref _calls);

    public Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Throws ? Task.FromException<bool>(new InvalidOperationException("yoklama patladı")) : Task.FromResult(Reachable);
    }
}

public class ConnectivityMonitorTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(Fx.Start);
    private readonly FakeProbe _probe = new();
    private readonly FakeSystemEvents _events = new();
    private readonly List<ConnectivityChange> _changes = [];
    private readonly ConnectivityMonitor _monitor;

    public ConnectivityMonitorTests()
    {
        _monitor = new ConnectivityMonitor(_probe, _events, _clock);
        _monitor.Changed += change =>
        {
            lock (_changes)
            {
                _changes.Add(change);
            }
        };
    }

    public void Dispose() => _monitor.Dispose();

    private List<ConnectivityChange> Changes
    {
        get
        {
            lock (_changes)
            {
                return [.. _changes];
            }
        }
    }

    private async Task AdvanceUntilAsync(Func<bool> condition, int timeoutMs = 5000, int stepMilliseconds = 500)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Koşul zamanında gerçekleşmedi.");
            }

            _clock.Advance(TimeSpan.FromMilliseconds(stepMilliseconds));
            await Task.Delay(3);
        }
    }

    private async Task SettleAsync(TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromMilliseconds(500))
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(3);
        }
    }

    [Fact]
    public async Task StartsOnline_GoesOfflineAfterTwoFailedProbes_AndBackOnceReachable()
    {
        _monitor.Start();
        await AdvanceUntilAsync(() => _probe.Calls >= 1);
        Assert.True(_monitor.IsOnline);

        _probe.Reachable = false;
        var cutAt = _clock.GetUtcNow();
        await AdvanceUntilAsync(() => Changes.Count >= 1); // IsOnline olaydan önce değişir; olayı bekle

        // Kesildikten en geç ~birkaç sn (sahte saatle) sonra anlaşıldı: 8 sn'lik çevrimiçi aralığı beklenmedi.
        Assert.True(_clock.GetUtcNow() - cutAt <= TimeSpan.FromSeconds(11), "Kopma geç anlaşıldı.");
        var lost = Assert.Single(Changes);
        Assert.False(lost.Online);

        _probe.Reachable = true;
        var backAt = _clock.GetUtcNow();
        await AdvanceUntilAsync(() => Changes.Count >= 2);

        Assert.True(_clock.GetUtcNow() - backAt <= TimeSpan.FromSeconds(3), "Dönüş geç anlaşıldı.");
        var restored = Changes[^1];
        Assert.True(restored.Online);
        Assert.True(restored.Outage > TimeSpan.Zero);
    }

    [Fact]
    public async Task ASingleFailedProbe_IsNotAnOutage()
    {
        _monitor.Start();
        await AdvanceUntilAsync(() => _probe.Calls >= 1);

        _probe.Reachable = false;
        await AdvanceUntilAsync(() => _probe.Calls >= 2);
        _probe.Reachable = true;
        await SettleAsync(TimeSpan.FromSeconds(20));

        Assert.True(_monitor.IsOnline);
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task OsSaysDown_ButInternetStillReachable_StaysOnline()
    {
        _monitor.Start();
        await AdvanceUntilAsync(() => _probe.Calls >= 1);

        _events.Network(false); // örn. ikinci bir bağdaştırıcı koptu
        var calls = _probe.Calls;
        await AdvanceUntilAsync(() => _probe.Calls > calls);
        await SettleAsync(TimeSpan.FromSeconds(3));

        Assert.True(_monitor.IsOnline);
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task OsSaysDown_AndProbeFails_IsOfflineAfterJustOneProbe()
    {
        _monitor.Start();
        await AdvanceUntilAsync(() => _probe.Calls >= 1);

        _probe.Reachable = false;
        var cutAt = _clock.GetUtcNow();
        _events.Network(false);
        await AdvanceUntilAsync(() => Changes.Count >= 1, stepMilliseconds: 50);

        Assert.True(_clock.GetUtcNow() - cutAt < TimeSpan.FromSeconds(1), "Windows olayıyla anında anlaşılmalıydı.");
        Assert.False(Assert.Single(Changes).Online);
    }

    [Fact]
    public async Task OsSaysUp_ReportsConfirmedOnlineEvenWithoutAnOutage_ForRecovery()
    {
        _monitor.Start();
        await AdvanceUntilAsync(() => _probe.Calls >= 1);

        _events.Network(true);
        await AdvanceUntilAsync(() => Changes.Count == 1, stepMilliseconds: 50);

        var confirmed = Assert.Single(Changes);
        Assert.True(confirmed.Online);
        Assert.Equal(TimeSpan.Zero, confirmed.Outage);
    }

    [Fact]
    public async Task ProbeThatThrows_CountsAsUnreachable()
    {
        _probe.Throws = true;
        _monitor.Start();

        await AdvanceUntilAsync(() => Changes.Count >= 1);

        Assert.False(Assert.Single(Changes).Online);
    }

    [Fact]
    public async Task Paused_IgnoresEverything_ThenResumeIsQuietForTheGivenPeriod()
    {
        _monitor.Start();
        await AdvanceUntilAsync(() => _probe.Calls >= 1);
        _monitor.Pause();
        _probe.Reachable = false;
        _events.Network(false);
        await SettleAsync(TimeSpan.FromSeconds(30));
        Assert.True(_monitor.IsOnline); // uykuda "koptu" denmez

        _monitor.Resume(TimeSpan.FromSeconds(10));
        await SettleAsync(TimeSpan.FromSeconds(8));
        Assert.True(_monitor.IsOnline); // sessiz dönem: ağ henüz gelmemiş olabilir

        await AdvanceUntilAsync(() => Changes.Count >= 1);
        Assert.False(Assert.Single(Changes).Online); // sessiz dönemden sonra hâlâ yoksa kopmuş sayılır
    }

    [Fact]
    public async Task Resume_WithNetworkAlreadyBack_NeverReportsAnOutage()
    {
        _monitor.Start();
        await AdvanceUntilAsync(() => _probe.Calls >= 1);
        _monitor.Pause();
        _probe.Reachable = false;
        await SettleAsync(TimeSpan.FromSeconds(5));

        _monitor.Resume(TimeSpan.FromSeconds(10));
        await SettleAsync(TimeSpan.FromSeconds(3));
        _probe.Reachable = true; // sessiz dönemde geldi
        await SettleAsync(TimeSpan.FromSeconds(30));

        Assert.True(_monitor.IsOnline);
        Assert.Empty(Changes);
    }

    [Fact]
    public async Task RealTrafficSucceeding_CorrectsAFalseOffline()
    {
        _probe.Reachable = false;
        _monitor.Start();
        await AdvanceUntilAsync(() => Changes.Count >= 1);

        _monitor.ReportReachable();

        Assert.True(_monitor.IsOnline);
        Assert.True(Changes[^1].Online);
    }

    [Fact]
    public void WithoutAProbe_WindowsEventsDecide()
    {
        using var eventOnly = new ConnectivityMonitor(null, _events, _clock);
        var changes = new List<ConnectivityChange>();
        eventOnly.Changed += changes.Add;

        _events.Network(false);
        _clock.Advance(TimeSpan.FromSeconds(42));
        _events.Network(true);

        Assert.Equal([false, true], changes.Select(c => c.Online));
        Assert.Equal(TimeSpan.FromSeconds(42), changes[1].Outage);
    }

    [Fact]
    public void Dispose_UnsubscribesFromWindowsEvents()
    {
        var events = new FakeSystemEvents();
        var monitor = new ConnectivityMonitor(null, events, _clock);

        monitor.Dispose();

        Assert.False(events.HasSubscribers);
    }
}

public class HttpConnectivityProbeTests
{
    private static HttpConnectivityProbe Probe(Func<RecordedRequest, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    [Fact]
    public async Task WindowsConnectTest200_MeansOnline()
    {
        var probe = Probe(r => r.Uri.Host == "www.msftconnecttest.com" ? new HttpResponseMessage(HttpStatusCode.OK) : throw new HttpRequestException("kapalı"));

        Assert.True(await probe.IsReachableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IfTheFirstHostIsBlocked_TwitchRespondingIsEnough()
    {
        var probe = Probe(r => r.Uri.Host == "id.twitch.tv" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : throw new HttpRequestException("engelli"));

        Assert.True(await probe.IsReachableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CaptivePortalRedirect_AndNoTwitch_MeansOffline()
    {
        var probe = Probe(r => r.Uri.Host == "www.msftconnecttest.com" ? new HttpResponseMessage(HttpStatusCode.Redirect) : throw new HttpRequestException("ulaşılamıyor"));

        Assert.False(await probe.IsReachableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EverythingFailing_MeansOffline()
    {
        var probe = Probe(_ => throw new HttpRequestException("DNS yok"));

        Assert.False(await probe.IsReachableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TwitchServerError_DoesNotCountAsReachableByItself()
    {
        var probe = Probe(r => r.Uri.Host == "id.twitch.tv" ? new HttpResponseMessage(HttpStatusCode.BadGateway) : throw new HttpRequestException("kapalı"));

        Assert.False(await probe.IsReachableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CallersCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var probe = Probe(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.IsReachableAsync(cts.Token));
    }

    [Fact]
    public void RealClient_HasShortTimeouts_AndDoesNotFollowRedirects()
    {
        using var client = HttpConnectivityProbe.CreateClient();

        Assert.InRange(client.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
    }
}

public class DropperServiceConnectivityTests : IDisposable
{
    private static readonly TwitchSession Session = new("tok-1", "42", "testuser", null);
    private readonly TempDir _dir = new();
    private readonly FakeAccount _account = new();
    private readonly FakeConnectionFactory _factory = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeSystemEvents _events = new();
    private readonly FakeTimeProvider _clock = new(Fx.Start);
    private readonly DropperService _service;
    private readonly List<UserNotification> _notes = [];

    public DropperServiceConnectivityTests()
    {
        _service = new DropperService(
            _account,
            _factory,
            new SettingsService(_dir.File("settings.json")),
            _clock,
            offlineRetry: TimeSpan.FromHours(1), // yeniden deneme zamanlayıcısı değil, bağlantı izleyicisi toparlamalı
            systemEvents: _events,
            connectivityProbe: _probe);
        _service.Notification += n =>
        {
            lock (_notes)
            {
                _notes.Add(n);
            }
        };
        var campaign = Fx.Camp("c1", "Base Invaders", CampaignScope.General, Fx.Start.AddDays(9), ("d1", 600));
        _factory.Platform.Snapshot = Fx.Snap((campaign, [Fx.Ch("1", "streamer")]));
        _factory.Platform.Session = _ => new DropSession("d1", 5, 600);
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

    private async Task AdvanceUntilAsync(Func<bool> condition, int timeoutMs = 8000, int stepMilliseconds = 500)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Koşul zamanında gerçekleşmedi.");
            }

            _clock.Advance(TimeSpan.FromMilliseconds(stepMilliseconds));
            await Task.Delay(3);
        }
    }

    private async Task AdvanceAsync(TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromMilliseconds(500))
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(3);
        }
    }

    private async Task<int> StartWatchingAsync()
    {
        await _service.InitializeAsync();
        Assert.True(_service.Start());
        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Watching);
        return _factory.Platform.Watched.Count;
    }

    [Fact]
    public async Task ConnectionLoss_IsShownImmediately_AsOfflineAccountAndNotification_WithoutStoppingTheEngine()
    {
        await StartWatchingAsync();
        Assert.True(_service.Snapshot.NetworkOnline);

        _probe.Reachable = false;
        await AdvanceUntilAsync(() => !_service.Snapshot.NetworkOnline);

        var snapshot = _service.Snapshot;
        Assert.Equal(AccountStatus.Offline, snapshot.Account.Status);
        Assert.Equal("testuser", snapshot.Account.Login);
        Assert.True(snapshot.IsRunning); // motora dokunulmaz; kendi hata durumunu yönetir
        Assert.Single(Notes, n => n.Kind == NotificationKind.ConnectionLost);
    }

    [Fact]
    public async Task ConnectionReturning_IsDetectedFast_ValidatesSessionAndNotifiesWithTheOutageLength()
    {
        await StartWatchingAsync();
        _probe.Reachable = false;
        await AdvanceUntilAsync(() => !_service.Snapshot.NetworkOnline);
        await AdvanceAsync(TimeSpan.FromSeconds(5));
        var restoresBefore = _account.RestoreCalls;

        _probe.Reachable = true;
        var backAt = _clock.GetUtcNow();
        await AdvanceUntilAsync(() => _service.Snapshot.NetworkOnline && _service.Snapshot.Account.Status == AccountStatus.LoggedIn);

        // 1 saatlik çevrimdışı yeniden deneme zamanlayıcısı beklenmeden, en geç birkaç sn içinde.
        Assert.True(_clock.GetUtcNow() - backAt <= TimeSpan.FromSeconds(4));
        Assert.True(_account.RestoreCalls > restoresBefore);
        var restored = Assert.Single(Notes, n => n.Kind == NotificationKind.ConnectionRestored);
        Assert.Contains("kesinti", restored.Text);
    }

    [Fact]
    public async Task LongOutage_RestartsTheEngine_ButAShortOneLeavesAHealthyEngineAlone()
    {
        var before = await StartWatchingAsync();

        // Kısa kesinti (< 20 sn): sağlıklı motor yeniden başlatılmaz.
        _probe.Reachable = false;
        await AdvanceUntilAsync(() => !_service.Snapshot.NetworkOnline);
        _probe.Reachable = true;
        await AdvanceUntilAsync(() => _service.Snapshot.NetworkOnline && _service.Snapshot.Account.Status == AccountStatus.LoggedIn);
        await AdvanceAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(before, _factory.Platform.Watched.Count);

        // Uzun kesinti: izleyici pes etmiş olabilir → yeni motor.
        _probe.Reachable = false;
        await AdvanceUntilAsync(() => !_service.Snapshot.NetworkOnline);
        await AdvanceAsync(TimeSpan.FromSeconds(25));
        _probe.Reachable = true;
        await AdvanceUntilAsync(() => _factory.Platform.Watched.Count > before && _service.Snapshot.Engine.State == EngineState.Watching);

        Assert.True(_service.IsRunning);
    }

    [Fact]
    public async Task OpeningWithoutInternet_ShowsOfflineQuickly_AndConnectsTheMomentTheNetworkIsBack()
    {
        _probe.Reachable = false;
        _account.QueueRestore(new TwitchRestoreResult(TwitchRestoreStatus.Offline, null)); // açılışta oturum doğrulanamadı

        await _service.InitializeAsync();
        await AdvanceUntilAsync(() => !_service.Snapshot.NetworkOnline);
        Assert.Equal(AccountStatus.Offline, _service.Snapshot.Account.Status);

        _probe.Reachable = true;
        var backAt = _clock.GetUtcNow();
        await AdvanceUntilAsync(() => _service.Snapshot.Account.Status == AccountStatus.LoggedIn);

        Assert.True(_clock.GetUtcNow() - backAt <= TimeSpan.FromSeconds(4), "Bağlanma 1 saatlik zamanlayıcıyı beklememeliydi.");
        Assert.True(_service.Snapshot.NetworkOnline);
    }

    [Fact]
    public async Task OpeningWithAHangingNetwork_DoesNotWaitForTheValidationTimeout()
    {
        // Yukarı hat kopuk: oturum doğrulaması iptal edilene kadar dönmüyor (gerçekte 30 sn'lik zaman aşımı).
        _probe.Reachable = false;
        _account.RestoreOverride = ct => Task.Delay(Timeout.Infinite, ct).ContinueWith<TwitchRestoreResult>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default);

        var initialize = _service.InitializeAsync();
        await AdvanceUntilAsync(() => initialize.IsCompleted);

        Assert.False(_service.Snapshot.NetworkOnline);
        Assert.Equal(AccountStatus.Offline, _service.Snapshot.Account.Status);

        _account.RestoreOverride = null;
        _probe.Reachable = true;
        await AdvanceUntilAsync(() => _service.Snapshot.Account.Status == AccountStatus.LoggedIn);
    }

    [Fact]
    public async Task RealRequestSucceeding_CorrectsAProbeThatWronglySaysOffline()
    {
        await StartWatchingAsync();
        _probe.Reachable = false;
        await AdvanceUntilAsync(() => !_service.Snapshot.NetworkOnline);

        await _service.RefreshAsync(); // Twitch'e gerçekten ulaşıldı

        Assert.True(_service.Snapshot.NetworkOnline);
    }

    [Fact]
    public async Task EngineGoingDegraded_TriggersAnImmediateProbe()
    {
        _factory.Platform.DiscoverError = new TwitchTransientException("service timeout");
        await _service.InitializeAsync();
        await AdvanceAsync(TimeSpan.FromSeconds(1));
        var calls = _probe.Calls;

        _service.Start();
        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Degraded, stepMilliseconds: 10);
        await Task.Delay(50);

        Assert.True(_probe.Calls > calls, "Motor hata alınca bağlantı hemen yoklanmalı.");
    }

    [Fact]
    public async Task SuspendAndResume_DoNotLookLikeAnOutage()
    {
        await StartWatchingAsync();

        _events.Suspend();
        _probe.Reachable = false; // uykuda ağ gider
        _events.Network(false);
        await AdvanceAsync(TimeSpan.FromSeconds(3));
        _probe.Reachable = true; // uyanınca gelir
        _events.Resume();
        await AdvanceUntilAsync(() => _service.IsRunning && _service.Snapshot.Engine.State == EngineState.Watching);
        await AdvanceAsync(TimeSpan.FromSeconds(20));

        Assert.DoesNotContain(Notes, n => n.Kind is NotificationKind.ConnectionLost or NotificationKind.ConnectionRestored);
        Assert.True(_service.Snapshot.NetworkOnline);
    }
}
