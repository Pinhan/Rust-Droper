using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Tests;

internal sealed class FakeSystemEvents : ISystemEvents
{
    public event Action? Suspending;

    public event Action? Resumed;

    public event Action<bool>? NetworkAvailabilityChanged;

    public bool HasSubscribers => Suspending is not null || Resumed is not null || NetworkAvailabilityChanged is not null;

    public void Suspend() => Suspending?.Invoke();

    public void Resume() => Resumed?.Invoke();

    public void Network(bool available) => NetworkAvailabilityChanged?.Invoke(available);
}

public class PowerAndNetworkTests : IDisposable
{
    private static readonly TwitchSession Session = new("tok-1", "42", "testuser", null);
    private static readonly PowerOptions FastPower = new()
    {
        ResumeDelay = TimeSpan.FromSeconds(5),
        NetworkSettle = TimeSpan.FromSeconds(1),
        OutageRestartThreshold = TimeSpan.FromSeconds(60),
    };

    private readonly TempDir _dir = new();
    private readonly FakeAccount _account = new();
    private readonly FakeConnectionFactory _factory = new();
    private readonly FakeSystemEvents _events = new();
    private readonly FakeTimeProvider _clock = new(Fx.Start);
    private readonly DropperService _service;

    public PowerAndNetworkTests()
    {
        _service = new DropperService(
            _account,
            _factory,
            new SettingsService(_dir.File("settings.json")),
            _clock,
            offlineRetry: TimeSpan.FromHours(1), // yeniden deneme zamanlayıcısı testi etkilemesin: toparlama ağ/uyanış olayıyla olmalı
            systemEvents: _events,
            power: FastPower);
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

    /// <summary>Koşul gerçekleşene dek sahte saati 1 sn'lik adımlarla ilerletir (zamanlayıcıları da işletir).</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Koşul zamanında gerçekleşmedi.");
            }

            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
    }

    private async Task AdvanceAsync(TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(1))
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
    }

    private async Task<int> StartWatchingAsync()
    {
        await _service.InitializeAsync();
        Assert.True(_service.Start());
        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Watching);
        return _factory.Platform.Watched.Count;
    }

    private bool WatchingAgainAfter(int watchedBefore) =>
        _service.IsRunning && _service.Snapshot.Engine.State == EngineState.Watching && _factory.Platform.Watched.Count > watchedBefore;

    [Fact]
    public async Task SleepThenWake_StopsEngineGracefully_ThenValidatesSessionAndRestarts()
    {
        var before = await StartWatchingAsync();
        var restoresBefore = _account.RestoreCalls;
        var disposedBefore = Volatile.Read(ref _factory.Disposed);

        _events.Suspend();
        await AdvanceUntilAsync(() => !_service.IsRunning);

        Assert.Equal(AccountStatus.LoggedIn, _service.Snapshot.Account.Status);
        Assert.True(Volatile.Read(ref _factory.Disposed) > disposedBefore, "Bağlantı (izleyici/PubSub) kapatılmalı.");

        _events.Resume();
        await AdvanceUntilAsync(() => WatchingAgainAfter(before));

        Assert.True(_account.RestoreCalls > restoresBefore, "Uyanınca oturum doğrulanmalı.");
        Assert.Equal(AccountStatus.LoggedIn, _service.Snapshot.Account.Status);
    }

    [Fact]
    public async Task Wake_DoesNotRestartEngine_TheUserPaused()
    {
        await StartWatchingAsync();
        await _service.StopAsync(); // kullanıcı Duraklat
        var restoresBefore = _account.RestoreCalls;

        _events.Suspend();
        _events.Resume();
        await AdvanceUntilAsync(() => _account.RestoreCalls > restoresBefore); // oturum yine doğrulanır
        await AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task WakeWithoutNetwork_GoesOffline_ThenNetworkArrivalRestoresAndRestarts()
    {
        var before = await StartWatchingAsync();
        _events.Suspend();
        await AdvanceUntilAsync(() => !_service.IsRunning);
        _account.QueueRestore(new TwitchRestoreResult(TwitchRestoreStatus.Offline, null));

        _events.Resume();
        await AdvanceUntilAsync(() => _service.Snapshot.Account.Status == AccountStatus.Offline);
        await Task.Delay(100); // uyanış toparlaması tamamen bitsin
        Assert.False(_service.IsRunning);

        _events.Network(true); // 1 saatlik çevrimdışı yeniden deneme beklenmeden, ağ gelince hemen
        await AdvanceUntilAsync(() => WatchingAgainAfter(before));

        Assert.Equal(AccountStatus.LoggedIn, _service.Snapshot.Account.Status);
    }

    [Fact]
    public async Task LongNetworkOutage_RestartsRunningEngine_WhenNetworkReturns()
    {
        var before = await StartWatchingAsync();

        _events.Network(false);
        await AdvanceAsync(TimeSpan.FromSeconds(70));
        _events.Network(true);
        await AdvanceUntilAsync(() => WatchingAgainAfter(before));

        Assert.Equal(AccountStatus.LoggedIn, _service.Snapshot.Account.Status);
    }

    [Fact]
    public async Task BriefNetworkFlap_LeavesHealthyEngineAlone()
    {
        var before = await StartWatchingAsync();

        _events.Network(false);
        await AdvanceAsync(TimeSpan.FromSeconds(5));
        _events.Network(true);
        await AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.True(_service.IsRunning);
        Assert.Equal(before, _factory.Platform.Watched.Count);
    }

    [Fact]
    public async Task NetworkReturning_WhileEngineIsDegraded_RestartsItWithoutWaitingForBackoff()
    {
        _factory.Platform.DiscoverError = new TwitchTransientException("service timeout");
        await _service.InitializeAsync();
        _service.Start();
        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Degraded);

        _factory.Platform.DiscoverError = null;
        _events.Network(false);
        _events.Network(true);
        await AdvanceUntilAsync(() => WatchingAgainAfter(0));

        // Yeni motor: eski motorun "keşif başarısız" notu yok → geri çekilme beklenmeden yeniden başlatıldı.
        Assert.DoesNotContain(_service.Snapshot.Engine.Recent, e => e.Text.Contains("Keşif başarısız", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NetworkEvents_AreIgnoredWhileSuspended()
    {
        var before = await StartWatchingAsync();
        _events.Suspend();
        await AdvanceUntilAsync(() => !_service.IsRunning);

        _events.Network(false);
        _events.Network(true);
        await AdvanceAsync(TimeSpan.FromSeconds(5));

        Assert.False(_service.IsRunning); // yalnız uyanış motoru yeniden başlatır
        Assert.Equal(before, _factory.Platform.Watched.Count);
    }

    [Fact]
    public async Task SuspendWhileNetworkIsHanging_StillStopsPromptly()
    {
        // Ağ "askıda": keşif çağrısı iptal edilene kadar dönmüyor. Uyku bildirimi motoru yine de durdurabilmeli.
        await _service.InitializeAsync(); // açılış yenilemesi de keşif yapar: askıyı ondan sonra kur
        _factory.Platform.DiscoverBlock = ct => Task.Delay(Timeout.Infinite, ct);
        _service.Start();
        await AdvanceUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Discovering);

        _events.Suspend();

        await AdvanceUntilAsync(() => !_service.IsRunning);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromSystemEvents()
    {
        await _service.InitializeAsync();

        _service.Dispose();

        Assert.False(_events.HasSubscribers);
        _events.Suspend();
        _events.Resume();
        _events.Network(true);
    }

    [Fact]
    public void RealHttpClients_HaveABoundedTimeout_SoNothingHangsForever()
    {
        using var client = TwitchConstants.CreateHttpClient(TwitchClients.SmartboxTv);

        Assert.InRange(client.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
    }
}

public class OutageDoesNotLookLikeAStallTests
{
    [Fact]
    public async Task PollFailuresDuringAnOutage_DoNotBlockTheChannelOrCooldownTheCampaign()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var platform = new FakePlatform();
        var campaign = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 600));
        platform.Snapshot = Fx.Snap((campaign, [Fx.Ch("1", "cha")]));
        var polls = 0;
        platform.Session = _ =>
        {
            var n = Interlocked.Increment(ref polls);
            if (n is >= 3 and <= 6)
            {
                throw new HttpRequestException("ağ yok"); // art arda 4 başarısız yoklama (< 5) ≈ 8 dk > StallTimeout
            }

            return new DropSession("dA", n < 3 ? n : n - 4, 600);
        };
        var options = new EngineOptions { GameSlug = "rust", PollInterval = TimeSpan.FromMinutes(2) };
        var engine = new DropEngine(platform, options, timeProvider: clock);
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(cts.Token);

        for (var i = 0; i < 4000 && Volatile.Read(ref polls) < 9; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(2);
        }

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(polls >= 9, "Senaryo tamamlanmadı.");
        Assert.Equal(["cha"], platform.Watched.Distinct());
        Assert.DoesNotContain(engine.Snapshot.Recent, e => e.Text.Contains("ilerleme yok", StringComparison.Ordinal));
    }
}
