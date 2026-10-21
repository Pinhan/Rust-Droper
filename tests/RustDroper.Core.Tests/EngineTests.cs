using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Tests;

internal static class Fx
{
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T18:00:00Z");

    public static LiveChannel Ch(string id, string login, int viewers = 100) =>
        new(Platform.Twitch, id, login, login, viewers, null, new Uri("https://www.twitch.tv/" + login), null);

    public static Campaign Camp(string id, string name, CampaignScope scope, DateTimeOffset? ends, params (string Id, int Minutes)[] rewards) =>
        new(
            Platform.Twitch,
            id,
            name,
            "rust",
            "Rust",
            CampaignStatus.Active,
            null,
            ends,
            scope,
            [],
            [.. rewards.Select(r => new DropReward(r.Id, r.Id, r.Minutes, null))],
            null);

    public static PlatformSnapshot Snap(params (Campaign Campaign, LiveChannel[] Channels)[] items) =>
        new(
            [.. items.Select(i => i.Campaign)],
            items.ToDictionary(i => i.Campaign.Id, i => (IReadOnlyList<LiveChannel>)i.Channels, StringComparer.Ordinal));
}

public class DropPlannerTests
{
    private static readonly DateTimeOffset Now = Fx.Start;
    private static readonly HashSet<string> None = [];
    private static readonly Dictionary<string, DateTimeOffset> NoTimes = [];

    private static IReadOnlyList<Target> Plan(
        PlatformSnapshot snapshot,
        IReadOnlySet<string>? completed = null,
        IReadOnlyDictionary<string, DateTimeOffset>? cooldown = null,
        IReadOnlyDictionary<string, DateTimeOffset>? blocked = null,
        IReadOnlySet<string>? claimed = null) =>
        DropPlanner.Plan(new PlannerInput(snapshot, Now, completed ?? None, cooldown ?? NoTimes, blocked ?? NoTimes, claimed ?? None));

    [Fact]
    public void StreamerCampaign_ComesBeforeGeneral_EvenIfItEndsLater()
    {
        var general = Fx.Camp("g", "General", CampaignScope.General, Now.AddDays(1), ("g1", 60));
        var streamer = Fx.Camp("s", "Streamer", CampaignScope.Streamer, Now.AddDays(5), ("s1", 30));

        var plan = Plan(Fx.Snap((general, [Fx.Ch("1", "a")]), (streamer, [Fx.Ch("2", "b")])));

        Assert.Equal(["s", "g"], plan.Select(t => t.Campaign.Id));
    }

    [Fact]
    public void SoonerEndingCampaign_IsPreferred_AmongSameScope()
    {
        var late = Fx.Camp("late", "Late", CampaignScope.General, Now.AddDays(5), ("x", 60));
        var soon = Fx.Camp("soon", "Soon", CampaignScope.General, Now.AddDays(1), ("y", 600));

        var plan = Plan(Fx.Snap((late, [Fx.Ch("1", "a")]), (soon, [Fx.Ch("2", "b")])));

        Assert.Equal(["soon", "late"], plan.Select(t => t.Campaign.Id));
    }

    [Fact]
    public void EndedOrNotYetStartedCampaigns_AreExcluded()
    {
        var ended = Fx.Camp("e", "Ended", CampaignScope.General, Now.AddMinutes(-1), ("e1", 10));
        var upcoming = Fx.Camp("u", "Upcoming", CampaignScope.General, Now.AddDays(3), ("u1", 10)) with { StartsAt = Now.AddDays(1) };

        Assert.Empty(Plan(Fx.Snap((ended, [Fx.Ch("1", "a")]), (upcoming, [Fx.Ch("2", "b")]))));
    }

    [Fact]
    public void CampaignThatCannotFinishBeforeItEnds_IsExcluded()
    {
        var tooLate = Fx.Camp("t", "TooLate", CampaignScope.General, Now.AddMinutes(100), ("t1", 60), ("t2", 240));

        Assert.Empty(Plan(Fx.Snap((tooLate, [Fx.Ch("1", "a")]))));
    }

    [Fact]
    public void ClaimedDrops_AreExcluded_AndShortenTheRemainingRequirement()
    {
        var camp = Fx.Camp("c", "C", CampaignScope.General, Now.AddMinutes(100), ("c1", 60), ("c2", 240));
        var snapshot = Fx.Snap((camp, [Fx.Ch("1", "a")]));

        Assert.Empty(Plan(snapshot));
        Assert.Single(Plan(snapshot, claimed: new HashSet<string> { "c2" }));
        Assert.Empty(Plan(snapshot, claimed: new HashSet<string> { "c1", "c2" }));
    }

    [Fact]
    public void CompletedAndCooldownCampaigns_AreExcluded_UntilCooldownExpires()
    {
        var camp = Fx.Camp("c", "C", CampaignScope.General, Now.AddDays(1), ("c1", 60));
        var snapshot = Fx.Snap((camp, [Fx.Ch("1", "a")]));

        Assert.Empty(Plan(snapshot, completed: new HashSet<string> { "c" }));
        Assert.Empty(Plan(snapshot, cooldown: new Dictionary<string, DateTimeOffset> { ["c"] = Now.AddMinutes(10) }));
        Assert.Single(Plan(snapshot, cooldown: new Dictionary<string, DateTimeOffset> { ["c"] = Now.AddMinutes(-1) }));
    }

    [Fact]
    public void BlockedChannels_AreSkipped_AndBestViewerChannelWins()
    {
        var camp = Fx.Camp("c", "C", CampaignScope.General, Now.AddDays(1), ("c1", 60));
        var big = Fx.Ch("1", "big", 900);
        var small = Fx.Ch("2", "small", 10);
        var snapshot = Fx.Snap((camp, [small, big]));

        Assert.Equal("big", Assert.Single(Plan(snapshot)).Channel.Login);
        Assert.Equal("small", Assert.Single(Plan(snapshot, blocked: new Dictionary<string, DateTimeOffset> { ["1"] = Now.AddMinutes(5) })).Channel.Login);
        Assert.Empty(Plan(snapshot, blocked: new Dictionary<string, DateTimeOffset> { ["1"] = Now.AddMinutes(5), ["2"] = Now.AddMinutes(5) }));
    }
}

internal sealed class FakePlatform : IDropPlatform
{
    private int _watcherStarts;

    public PlatformSnapshot Snapshot { get; set; } = Fx.Snap();

    public Func<LiveChannel, DropSession?> Session { get; set; } = _ => null;

    /// <summary>Verilirse izleyici hemen bu sonuçla biter; <c>null</c> ise iptal edilene kadar çalışır.</summary>
    public Func<LiveChannel, int, WatchResult?> WatcherEnd { get; set; } = (_, _) => null;

    public Exception? DiscoverError { get; set; }

    public ConcurrentQueue<string> Watched { get; } = new();

    public Platform Platform => Platform.Twitch;

    public event Action<string>? ChannelWentOffline;

    public event Action<ClaimNotice>? DropClaimed;

    public void RaiseOffline(string channelId) => ChannelWentOffline?.Invoke(channelId);

    public void RaiseClaim(ClaimNotice notice) => DropClaimed?.Invoke(notice);

    /// <summary>Verilirse keşif bunu bekler (ör. "ağ askıda": iptal edilene kadar dönmeyen çağrı).</summary>
    public Func<CancellationToken, Task>? DiscoverBlock { get; set; }

    public async Task<PlatformSnapshot> DiscoverAsync(string gameSlug, CancellationToken cancellationToken)
    {
        if (DiscoverBlock is { } block)
        {
            await block(cancellationToken);
        }

        return DiscoverError is { } error ? throw error : Snapshot;
    }

    public IWatcher CreateWatcher() => new FakeWatcher(this);

    public Task<DropSession?> GetSessionAsync(LiveChannel channel, CancellationToken cancellationToken) =>
        Task.FromResult(Session(channel));

    public void OnTargetChanged(LiveChannel? channel)
    {
    }

    public async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class FakeWatcher(FakePlatform owner) : IWatcher
    {
        public WatcherStats Stats { get; } = new();

        public async Task<WatchResult> RunAsync(LiveChannel channel, CancellationToken cancellationToken)
        {
            owner.Watched.Enqueue(channel.Login);
            if (owner.WatcherEnd(channel, Interlocked.Increment(ref owner._watcherStarts)) is { } ended)
            {
                return ended;
            }

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

public class DropEngineTests
{
    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> condition, int maxSteps = 3000, int stepSeconds = 10)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(stepSeconds));
            await Task.Delay(2);
        }

        Assert.True(condition(), "Beklenen durum (sahte saatle) oluşmadı.");
    }

    private static (DropEngine Engine, FakeTimeProvider Clock, CancellationTokenSource Cts, Task Run) Start(FakePlatform platform, Action<EngineOptions>? _ = null)
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var engine = new DropEngine(platform, new EngineOptions { GameSlug = "rust" }, timeProvider: clock);
        var cts = new CancellationTokenSource();
        return (engine, clock, cts, engine.RunAsync(cts.Token));
    }

    private static async Task StopAsync(CancellationTokenSource cts, Task run)
    {
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static bool HasEvent(DropEngine engine, string fragment) =>
        engine.Snapshot.Recent.Any(e => e.Text.Contains(fragment, StringComparison.Ordinal));

    /// <summary>İlk görüldüğü andan itibaren dakikada +1 ilerleyen, 1 dk gecikmeli sunucu oturumu; <paramref name="required"/>'a varınca biter (null).</summary>
    private static Func<LiveChannel, DropSession?> Progressive(FakeTimeProvider clock, string dropId, int required, Func<LiveChannel, bool>? applies = null)
    {
        var firstSeen = new ConcurrentDictionary<string, DateTimeOffset>();
        return channel =>
        {
            if (applies?.Invoke(channel) == false)
            {
                return null;
            }

            var seen = firstSeen.GetOrAdd(channel.Id, _ => clock.GetUtcNow());
            var minutes = (int)(clock.GetUtcNow() - seen).TotalMinutes - 1;
            return minutes < 1 || minutes > required ? null : new DropSession(dropId, minutes, required);
        };
    }

    [Fact]
    public async Task CompletesCampaign_ThenMovesToTheNextOne()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);
        var alpha = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 5));
        var beta = Fx.Camp("B", "Beta", CampaignScope.General, Fx.Start.AddDays(2), ("dB", 5));
        platform.Snapshot = Fx.Snap((alpha, [Fx.Ch("1", "cha")]), (beta, [Fx.Ch("2", "chb")]));
        var progress = Progressive(clock, "d", 5);
        platform.Session = ch => progress(ch) is { } s ? s with { DropId = ch.Id == "1" ? "dA" : "dB" } : null;

        await AdvanceUntilAsync(clock, () => platform.Watched.Contains("chb"));

        Assert.Equal(["cha", "chb"], platform.Watched.Distinct());
        Assert.True(HasEvent(engine, "Kampanya tamamlandı: Alpha"));
        await StopAsync(cts, run);
        Assert.Equal(EngineState.Stopped, engine.Snapshot.State);
    }

    [Fact]
    public async Task StalledChannel_IsBlocked_AndAnotherChannelIsUsed()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);
        var camp = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 600));
        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "ch1", 500), Fx.Ch("2", "ch2", 100)]));
        platform.Session = Progressive(clock, "dA", 600, ch => ch.Id == "2");

        await AdvanceUntilAsync(clock, () => engine.Snapshot.Session is { CurrentMinutes: >= 2 });

        Assert.Equal(["ch1", "ch2"], platform.Watched.Distinct());
        Assert.True(HasEvent(engine, "ilerleme yok"));
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task ProgressStopsMidWatch_RestartsSameChannelOnce_AndDoesNotBlockWhenProgressResumes()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);
        var camp = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 600));
        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "ch1", 500), Fx.Ch("2", "ch2", 100)]));
        var started = clock.GetUtcNow();
        DateTimeOffset? restartedAt = null;
        platform.Session = ch =>
        {
            if (ch.Id != "1")
            {
                return null;
            }

            var restarted = platform.Watched.Count(w => w == "ch1") >= 2;
            if (restarted)
            {
                restartedAt ??= clock.GetUtcNow();
                return new DropSession("dA", 4 + (int)(clock.GetUtcNow() - restartedAt.Value).TotalMinutes, 600); // başka yayın bırakıldı: ilerleme geri geldi
            }

            var raw = (int)(clock.GetUtcNow() - started).TotalMinutes - 1;
            return raw < 1 ? null : new DropSession("dA", Math.Min(raw, 3), 600); // 3. dakikada "başka yerden izleme" yüzünden durur
        };

        await AdvanceUntilAsync(clock, () => engine.Snapshot.Session is { CurrentMinutes: >= 8 });

        Assert.True(HasEvent(engine, "izleme yeniden başlatılıyor"));
        Assert.False(HasEvent(engine, "ilerleme yok")); // kanal ENGELLENMEDİ
        Assert.DoesNotContain("ch2", platform.Watched);
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task ProgressStopsMidWatchAndNeverResumes_BlocksChannelAfterOneRestart()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);
        var camp = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 600));
        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "ch1", 500), Fx.Ch("2", "ch2", 100)]));
        var started = clock.GetUtcNow();
        platform.Session = ch =>
        {
            if (ch.Id == "2")
            {
                return Progressive(clock, "dA", 600)(ch);
            }

            var raw = (int)(clock.GetUtcNow() - started).TotalMinutes - 1;
            return raw < 1 ? null : new DropSession("dA", Math.Min(raw, 3), 600);
        };

        await AdvanceUntilAsync(clock, () => platform.Watched.Contains("ch2"));

        Assert.True(HasEvent(engine, "izleme yeniden başlatılıyor"));
        Assert.True(HasEvent(engine, "ilerleme yok")); // ikinci kesintide normal engelleme
        Assert.Equal(2, platform.Watched.Count(w => w == "ch1"));
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task StallOnEveryChannel_PutsCampaignOnCooldown_AndGoesIdle()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);
        var camp = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 60));
        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "ch1", 500), Fx.Ch("2", "ch2", 100)]));

        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Idle && HasEvent(engine, "ilerleme alınamıyor"));

        Assert.Equal(["ch1", "ch2"], platform.Watched.Distinct());
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task WatcherReportingOffline_BlocksChannel_AndSwitches()
    {
        var platform = new FakePlatform
        {
            WatcherEnd = (ch, starts) => ch.Id == "1" ? new WatchResult(WatchEnd.Offline, "yayın kapalı") : null,
        };
        var (engine, clock, cts, run) = Start(platform);
        var camp = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 600));
        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "ch1", 500), Fx.Ch("2", "ch2", 100)]));
        platform.Session = Progressive(clock, "dA", 600);

        await AdvanceUntilAsync(clock, () => platform.Watched.Contains("ch2"));

        Assert.Equal(["ch1", "ch2"], platform.Watched.Distinct());
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task PubSubStreamDown_SwitchesChannelImmediately()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);
        var camp = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 600));
        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "ch1", 500), Fx.Ch("2", "ch2", 100)]));
        platform.Session = Progressive(clock, "dA", 600);

        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Watching && engine.Snapshot.Target?.Channel.Id == "1");
        platform.RaiseOffline("1");
        await AdvanceUntilAsync(clock, () => platform.Watched.Contains("ch2"));

        Assert.True(HasEvent(engine, "yayını kapandı"));
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task NoCampaigns_IsIdle_ThenStartsWhenOneAppears()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);

        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Idle);
        platform.Snapshot = Fx.Snap((Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 60)), [Fx.Ch("1", "ch1")]));
        platform.Session = Progressive(clock, "dA", 60);
        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Watching);

        Assert.Contains("ch1", platform.Watched);
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task TransientDiscoveryFailure_IsDegraded_ThenRecovers()
    {
        var platform = new FakePlatform { DiscoverError = new TwitchTransientException("service timeout") };
        var (engine, clock, cts, run) = Start(platform);
        platform.Snapshot = Fx.Snap((Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 60)), [Fx.Ch("1", "ch1")]));
        platform.Session = Progressive(clock, "dA", 60);

        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Degraded);
        platform.DiscoverError = null;
        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Watching);

        await StopAsync(cts, run);
    }

    [Fact]
    public async Task SchemaDrift_IsDegraded_NotACrash()
    {
        var platform = new FakePlatform { DiscoverError = new TwitchHashException("DirectoryPage_Game") };
        var (engine, clock, cts, run) = Start(platform);

        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Degraded);

        Assert.False(run.IsFaulted);
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task AuthFailure_RequestsRelogin()
    {
        var platform = new FakePlatform { DiscoverError = new TwitchUnauthorizedException("401") };
        var (engine, clock, cts, run) = Start(platform);

        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.AuthRequired);

        await StopAsync(cts, run);
    }

    [Fact]
    public async Task ClaimNotices_AreCountedAndLogged()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);
        platform.Snapshot = Fx.Snap((Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 60)), [Fx.Ch("1", "ch1")]));
        platform.Session = Progressive(clock, "dA", 60);
        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Watching);

        platform.RaiseClaim(new ClaimNotice("dA", true, "ELIGIBLE_FOR_ALL"));
        platform.RaiseClaim(new ClaimNotice("dX", false, "NOT_ELIGIBLE"));

        Assert.Equal(1, engine.Snapshot.ClaimedThisRun);
        Assert.True(HasEvent(engine, "claim edildi"));
        Assert.True(HasEvent(engine, "EDİLEMEDİ"));
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task StreamerCampaignAppearing_PreemptsGeneral_AtRediscovery()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform);
        var general = Fx.Camp("G", "General", CampaignScope.General, Fx.Start.AddDays(1), ("dG", 600));
        platform.Snapshot = Fx.Snap((general, [Fx.Ch("1", "chg")]));
        platform.Session = Progressive(clock, "dG", 600);
        await AdvanceUntilAsync(clock, () => engine.Snapshot.Target?.Channel.Id == "1" && engine.Snapshot.Session is not null);

        var streamer = Fx.Camp("S", "Streamer", CampaignScope.Streamer, Fx.Start.AddDays(2), ("dS", 30));
        platform.Snapshot = Fx.Snap((general, [Fx.Ch("1", "chg")]), (streamer, [Fx.Ch("9", "chs")]));
        await AdvanceUntilAsync(clock, () => platform.Watched.Contains("chs"));

        Assert.True(HasEvent(engine, "Öncelikli kampanya"));
        await StopAsync(cts, run);
    }
}
