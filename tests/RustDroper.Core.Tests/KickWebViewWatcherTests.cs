using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Domain;
using RustDroper.Core.Platforms.Kick;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Tests;

/// <summary>Betikli sahte oynatıcı ana bilgisayarı: durum elle ayarlanır, çağrılar sayılır.</summary>
internal sealed class FakeKickPlayerHost : IKickPlayerHost
{
    private KickPlayerStatus _status = KickPlayerStatus.Idle;

    public KickPlayerStatus Status => Volatile.Read(ref _status);

    public event Action<KickPlayerStatus>? StatusChanged;

    public List<string> Opened { get; } = [];

    public int Restarts { get; private set; }

    public int Releases { get; private set; }

    /// <summary>Doluysa <see cref="OpenAsync"/> bu istisnayı fırlatır (sözleşme ihlali senaryosu).</summary>
    public Exception? OpenThrows { get; set; }

    /// <summary>Doluysa açılışta durum buna ayarlanır (varsayılan: Starting).</summary>
    public KickPlayerState StateAfterOpen { get; set; } = KickPlayerState.Starting;

    /// <summary>Doluysa yeniden başlatmada durum buna ayarlanır (varsayılan: Starting).</summary>
    public KickPlayerState StateAfterRestart { get; set; } = KickPlayerState.Starting;

    public string? CurrentSlug { get; private set; }

    public void Set(KickPlayerState state, string? detail = null, string? slug = null)
    {
        Volatile.Write(ref _status, new KickPlayerStatus(state, slug ?? CurrentSlug, detail));
        StatusChanged?.Invoke(Status);
    }

    public Task OpenAsync(string slug, CancellationToken cancellationToken)
    {
        if (OpenThrows is { } ex)
        {
            throw ex;
        }

        Opened.Add(slug);
        CurrentSlug = slug;
        Set(StateAfterOpen);
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken cancellationToken)
    {
        Restarts++;
        Set(StateAfterRestart);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync()
    {
        Releases++;
        Volatile.Write(ref _status, KickPlayerStatus.Idle);
        return Task.CompletedTask;
    }
}

public class KickWebViewWatcherTests
{
    private static readonly LiveChannel Channel = new(
        Platform.Kick, "100001", "rustfan_one", "RustFan_One", 321, "Rust", new Uri("https://kick.com/rustfan_one"), null);

    private static readonly KickWebViewOptions FastOptions = new()
    {
        CheckInterval = TimeSpan.FromSeconds(5),
        StartTimeout = TimeSpan.FromSeconds(30),
        StallRestartAfter = TimeSpan.FromSeconds(20),
        MaxRestarts = 3,
        RestartWindow = TimeSpan.FromMinutes(10),
    };

    private static (KickWebViewWatcher Watcher, FakeKickPlayerHost Host, FakeTimeProvider Clock) Create(KickWebViewOptions? options = null)
    {
        var host = new FakeKickPlayerHost();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        return (new KickWebViewWatcher(host, options ?? FastOptions, clock), host, clock);
    }

    private static async Task RunUntilAsync(FakeTimeProvider clock, Func<bool> condition, int maxSteps = 600)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task Start_OpensPlayerForChannelSlug_AndReportsPlayback()
    {
        var (watcher, host, clock) = Create();
        using var cts = new CancellationTokenSource();

        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);
        host.Set(KickPlayerState.Playing);
        await RunUntilAsync(clock, () => watcher.Stats.LastSegmentAt is not null);

        Assert.Equal(["rustfan_one"], host.Opened);
        Assert.Equal(KickWebViewWatcher.VariantName, watcher.Stats.Variant);
        Assert.NotNull(watcher.Stats.LastSegmentAt);
        Assert.Equal(0, host.Restarts);
        Assert.False(run.IsCompleted);

        await cts.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WatchEnd.Cancelled, result.End);
    }

    [Fact]
    public async Task Stop_ReleasesPlayer_SoVideoDoesNotKeepPlaying()
    {
        var (watcher, host, clock) = Create();
        using var cts = new CancellationTokenSource();

        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);
        host.Set(KickPlayerState.Playing);
        await cts.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WatchEnd.Cancelled, result.End);
        Assert.Equal(1, host.Releases);
        Assert.Equal(KickPlayerState.Idle, host.Status.State);
    }

    [Fact]
    public async Task Offline_EndsWithOffline_AndReleasesPlayer()
    {
        var (watcher, host, clock) = Create();
        var run = watcher.RunAsync(Channel, CancellationToken.None);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);
        host.Set(KickPlayerState.Playing);
        await RunUntilAsync(clock, () => watcher.Stats.LastSegmentAt is not null);

        host.Set(KickPlayerState.Offline, "Kanal canlı değil.");
        var result = await TestTime.PumpAsync(run, clock);

        Assert.Equal(WatchEnd.Offline, result.End);
        Assert.Equal(1, host.Releases);
        Assert.Equal(0, host.Restarts);
    }

    [Fact]
    public async Task Stall_RestartsPageOnce_ThenRecovers()
    {
        var (watcher, host, clock) = Create();
        using var cts = new CancellationTokenSource();
        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);
        host.Set(KickPlayerState.Playing);
        await RunUntilAsync(clock, () => watcher.Stats.LastSegmentAt is not null);

        host.Set(KickPlayerState.Stalled);
        await RunUntilAsync(clock, () => host.Restarts == 1);
        Assert.Equal(1, host.Restarts);

        host.Set(KickPlayerState.Playing);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(20);
        Assert.Equal(1, host.Restarts); // oynuyor: başka yeniden başlatma yok
        Assert.False(run.IsCompleted);

        await cts.CancelAsync();
        Assert.Equal(WatchEnd.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(5))).End);
    }

    [Fact]
    public async Task ShortStall_BelowThreshold_DoesNotRestart()
    {
        var (watcher, host, clock) = Create();
        using var cts = new CancellationTokenSource();
        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);
        host.Set(KickPlayerState.Playing);
        await RunUntilAsync(clock, () => watcher.Stats.LastSegmentAt is not null);

        host.Set(KickPlayerState.Stalled);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(20);
        host.Set(KickPlayerState.Playing);
        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(20);

        Assert.Equal(0, host.Restarts);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StartTimeout_RestartsPage()
    {
        var (watcher, host, clock) = Create();
        using var cts = new CancellationTokenSource();
        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);

        // Oynatma hiç başlamıyor (Starting'de takılı).
        await RunUntilAsync(clock, () => host.Restarts >= 1);

        Assert.True(host.Restarts >= 1);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailedPlayer_IsRestarted_UntilBudgetExhausted_ThenGivesUp()
    {
        var (watcher, host, clock) = Create();
        host.StateAfterOpen = KickPlayerState.Failed;
        host.StateAfterRestart = KickPlayerState.Failed;

        var run = watcher.RunAsync(Channel, CancellationToken.None);
        var result = await TestTime.PumpAsync(run, clock);

        Assert.Equal(WatchEnd.Failed, result.End);
        Assert.Equal(FastOptions.MaxRestarts, host.Restarts);
        Assert.Contains("vazgeçildi", result.Reason, StringComparison.Ordinal);
        Assert.Equal(1, host.Releases);
    }

    [Fact]
    public async Task RestartBudget_ForgetsOldRestarts_AfterWindow()
    {
        var (watcher, host, clock) = Create();
        using var cts = new CancellationTokenSource();
        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);

        // Her 15 dk'da bir donma: pencere (10 dk) dışına çıktığı için bütçe tükenmez; izleyici vazgeçmemeli.
        for (var round = 1; round <= FastOptions.MaxRestarts + 2; round++)
        {
            host.Set(KickPlayerState.Stalled);
            await RunUntilAsync(clock, () => host.Restarts == round);
            Assert.Equal(round, host.Restarts);
            host.Set(KickPlayerState.Playing);
            clock.Advance(TimeSpan.FromMinutes(15));
            await Task.Delay(20);
            Assert.False(run.IsCompleted);
        }

        await cts.CancelAsync();
        Assert.Equal(WatchEnd.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(5))).End);
    }

    [Fact]
    public async Task LoginRequired_FailsFast_WithActionableReason_NoRestart()
    {
        var (watcher, host, clock) = Create();
        host.StateAfterOpen = KickPlayerState.LoginRequired;

        var run = watcher.RunAsync(Channel, CancellationToken.None);
        var result = await TestTime.PumpAsync(run, clock);

        Assert.Equal(WatchEnd.Failed, result.End);
        Assert.Contains("Oynatıcı girişi", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, host.Restarts);
        Assert.Equal(1, host.Releases);
    }

    [Fact]
    public async Task Unavailable_FailsWithoutRestart()
    {
        var (watcher, host, clock) = Create();
        host.StateAfterOpen = KickPlayerState.Unavailable;

        var run = watcher.RunAsync(Channel, CancellationToken.None);
        var result = await TestTime.PumpAsync(run, clock);

        Assert.Equal(WatchEnd.Failed, result.End);
        Assert.Contains("WebView2", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, host.Restarts);
    }

    [Fact]
    public async Task HostThrowingOnOpen_IsContained_AndStillReleases()
    {
        var (watcher, host, clock) = Create();
        host.OpenThrows = new InvalidOperationException("boom");

        var result = await watcher.RunAsync(Channel, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WatchEnd.Failed, result.End);
        Assert.Contains("InvalidOperationException", result.Reason, StringComparison.Ordinal);
        Assert.Equal(1, host.Releases);
        _ = clock;
    }

    [Fact]
    public async Task PlayerSwitchedToAnotherChannel_FailsInsteadOfCreditingTheWrongOne()
    {
        var (watcher, host, clock) = Create();
        var run = watcher.RunAsync(Channel, CancellationToken.None);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);
        host.Set(KickPlayerState.Playing, slug: "someone_else");

        var result = await TestTime.PumpAsync(run, clock);

        Assert.Equal(WatchEnd.Failed, result.End);
        Assert.Contains("someone_else", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChannelSwitch_NewWatcherReusesSameHost()
    {
        var host = new FakeKickPlayerHost();
        var clock = new FakeTimeProvider();
        var first = new KickWebViewWatcher(host, FastOptions, clock);
        var second = new KickWebViewWatcher(host, FastOptions, clock);
        var other = Channel with { Id = "100002", Login = "rustfan_two", DisplayName = "RustFan_Two" };

        using var cts1 = new CancellationTokenSource();
        var run1 = first.RunAsync(Channel, cts1.Token);
        await RunUntilAsync(clock, () => host.Opened.Count == 1);
        host.Set(KickPlayerState.Playing);
        await cts1.CancelAsync();
        await run1.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts2 = new CancellationTokenSource();
        var run2 = second.RunAsync(other, cts2.Token);
        await RunUntilAsync(clock, () => host.Opened.Count == 2);
        host.Set(KickPlayerState.Playing);
        await cts2.CancelAsync();
        await run2.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["rustfan_one", "rustfan_two"], host.Opened);
        Assert.Equal(2, host.Releases);
    }

    [Fact]
    public void Platform_UsesWebViewWatcher_WhenPlayerHostGiven_ElseHls()
    {
        var http = new HttpClient(new StubHandler(_ => StubHandler.Json(System.Net.HttpStatusCode.NotFound, "{}")));
        var api = new KickApiClient(http, () => "kick-token-value-1234567890", FakeKickServer.OpenLimiter());

        var withHost = new KickDropPlatform(new KickPublicClient(http), api, http, playerHost: new FakeKickPlayerHost());
        var withoutHost = new KickDropPlatform(new KickPublicClient(http), api, http);

        Assert.IsType<KickWebViewWatcher>(withHost.CreateWatcher());
        Assert.IsType<KickHlsWatcher>(withoutHost.CreateWatcher());
    }
}
