using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;

namespace RustDroper.Core.Tests;

/// <summary>Yayıncı drop'ları öncelik sırası + çok yayıncılı drop: planlayıcı (saf) ve motor davranışı.</summary>
public class PriorityPlannerTests
{
    private static readonly DateTimeOffset Now = Fx.Start;
    private static readonly HashSet<string> None = [];
    private static readonly Dictionary<string, DateTimeOffset> NoTimes = [];

    private static IReadOnlyList<Target> Plan(
        PlatformSnapshot snapshot,
        IReadOnlyList<string>? priority = null,
        IReadOnlySet<string>? completed = null,
        IReadOnlySet<string>? claimed = null,
        IReadOnlyDictionary<string, DateTimeOffset>? blocked = null) =>
        DropPlanner.Plan(new PlannerInput(snapshot, Now, completed ?? None, NoTimes, blocked ?? NoTimes, claimed ?? None, priority));

    private static Campaign Streamer(string id, int endsInDays = 5, params (string Id, int Minutes)[] drops) =>
        Fx.Camp(id, "Yayıncı " + id, CampaignScope.Streamer, Now.AddDays(endsInDays), drops.Length == 0 ? [(id + "-d", 30)] : drops);

    private static (Campaign, LiveChannel[]) Live(Campaign campaign, params LiveChannel[] channels) => (campaign, channels);

    [Fact]
    public void UserOrder_DecidesAmongStreamerCampaigns_UnrankedGoLast_GeneralComesAfterAll()
    {
        var a = Streamer("a", 1);
        var b = Streamer("b", 2);
        var c = Streamer("c", 3);
        var g = Fx.Camp("g", "Genel", CampaignScope.General, Now.AddDays(1), ("g1", 60));
        var snapshot = Fx.Snap(Live(a, Fx.Ch("1", "a")), Live(b, Fx.Ch("2", "b")), Live(c, Fx.Ch("3", "c")), Live(g, Fx.Ch("4", "g")));

        Assert.Equal(["a", "b", "c", "g"], Plan(snapshot).Select(t => t.Campaign.Id)); // sıra yok: bitişe göre (eski davranış)
        Assert.Equal(["c", "b", "a", "g"], Plan(snapshot, ["c", "b", "a"]).Select(t => t.Campaign.Id));
        Assert.Equal(["b", "a", "c", "g"], Plan(snapshot, ["b"]).Select(t => t.Campaign.Id)); // listede olmayanlar sona, kendi varsayılan sırasıyla
        Assert.Equal("g", Plan(snapshot, ["g", "c"]).Last().Campaign.Id); // genel drop sıralanmaz: öncelik listesinde olsa da yayıncılardan sonra
    }

    [Fact]
    public void OfflineStreamer_IsSkipped_NextEligibleOneIsChosen()
    {
        var first = Streamer("first");
        var second = Streamer("second");
        var offline = Fx.Snap((first, []), Live(second, Fx.Ch("2", "two")));

        Assert.Equal("second", Plan(offline, ["first", "second"])[0].Campaign.Id);

        var back = Fx.Snap(Live(first, Fx.Ch("1", "one")), Live(second, Fx.Ch("2", "two")));
        Assert.Equal("first", Plan(back, ["first", "second"])[0].Campaign.Id); // geri gelince öncelikli olana dönülür
    }

    [Fact]
    public void CompletedOrClaimedStreamer_IsSkipped()
    {
        var first = Streamer("first");
        var second = Streamer("second");
        var snapshot = Fx.Snap(Live(first, Fx.Ch("1", "one")), Live(second, Fx.Ch("2", "two")));

        Assert.Equal("second", Plan(snapshot, ["first", "second"], completed: new HashSet<string> { "first" })[0].Campaign.Id);
        Assert.Equal("second", Plan(snapshot, ["first", "second"], claimed: new HashSet<string> { "first-d" })[0].Campaign.Id);
    }

    [Fact]
    public void WhenNoStreamerIsAvailable_GeneralIsTheFallback()
    {
        var a = Streamer("a");
        var g = Fx.Camp("g", "Genel", CampaignScope.General, Now.AddDays(3), ("g1", 60));
        var snapshot = Fx.Snap((a, []), Live(g, Fx.Ch("9", "anyone")));

        var plan = Plan(snapshot, ["a"]);

        Assert.Equal("g", Assert.Single(plan).Campaign.Id);
    }

    [Fact]
    public void MultipleStreamersPerDrop_AnyOnlineOneWorks_BiggestChosen_BlockedSkipped_AllOfflineSkipsCampaign()
    {
        var camp = Streamer("duo");
        var one = Fx.Ch("1", "one", 50);
        var two = Fx.Ch("2", "two", 400);
        var three = Fx.Ch("3", "three", 10);
        var snapshot = Fx.Snap(Live(camp, one, two, three));

        Assert.Equal("two", Assert.Single(Plan(snapshot)).Channel.Login);
        Assert.Equal("one", Assert.Single(Plan(snapshot, blocked: new Dictionary<string, DateTimeOffset> { ["2"] = Now.AddMinutes(3) })).Channel.Login);
        Assert.Equal("three", Assert.Single(Plan(snapshot, blocked: new Dictionary<string, DateTimeOffset> { ["2"] = Now.AddMinutes(3), ["1"] = Now.AddMinutes(3) })).Channel.Login);
        Assert.Empty(Plan(snapshot, blocked: new Dictionary<string, DateTimeOffset> { ["1"] = Now.AddMinutes(3), ["2"] = Now.AddMinutes(3), ["3"] = Now.AddMinutes(3) }));
        Assert.Equal("one", Assert.Single(Plan(Fx.Snap(Live(camp, one)))).Channel.Login); // biri çevrimdışına düşünce kalan
    }

    [Fact]
    public void ChangingTheOrder_IsReflectedByTheNextPlan()
    {
        var a = Streamer("a");
        var b = Streamer("b");
        var snapshot = Fx.Snap(Live(a, Fx.Ch("1", "a")), Live(b, Fx.Ch("2", "b")));

        Assert.Equal("a", Plan(snapshot, ["a", "b"])[0].Campaign.Id);
        Assert.Equal("b", Plan(snapshot, ["b", "a"])[0].Campaign.Id);
    }
}

public class PriorityEngineTests
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

    private sealed class Priority
    {
        public IReadOnlyList<string> Current { get; set; } = [];
    }

    private static (DropEngine Engine, FakeTimeProvider Clock, CancellationTokenSource Cts, Task Run) Start(FakePlatform platform, Priority priority)
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var engine = new DropEngine(platform, new EngineOptions { GameSlug = "rust", StreamerPriority = () => priority.Current }, timeProvider: clock);
        var cts = new CancellationTokenSource();
        return (engine, clock, cts, engine.RunAsync(cts.Token));
    }

    private static async Task StopAsync(CancellationTokenSource cts, Task run)
    {
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Campaign Streamer(string id) => Fx.Camp(id, "Yayıncı " + id, CampaignScope.Streamer, Fx.Start.AddDays(5), (id + "-d", 600));

    /// <summary>Dakikada +1 ilerleyen oturum (takılma sayılmasın); <paramref name="startMinutes"/>'tan başlar.</summary>
    private static Func<LiveChannel, DropSession?> Moving(FakeTimeProvider clock, Func<LiveChannel, string> dropId, int startMinutes)
    {
        var started = clock.GetUtcNow();
        return channel => new DropSession(dropId(channel), Math.Min(599, startMinutes + (int)(clock.GetUtcNow() - started).TotalMinutes), 600);
    }

    [Fact]
    public async Task OrderChange_SwitchesToTheNewTopStreamer_WithinAPoll()
    {
        var platform = new FakePlatform();
        var priority = new Priority { Current = ["a", "b"] };
        var (engine, clock, cts, run) = Start(platform, priority);
        platform.Snapshot = Fx.Snap((Streamer("a"), [Fx.Ch("1", "cha")]), (Streamer("b"), [Fx.Ch("2", "chb")]));
        platform.Session = Moving(clock, c => c.Id == "1" ? "a-d" : "b-d", 30);
        await AdvanceUntilAsync(clock, () => engine.Snapshot.Target?.Channel.Login == "cha" && engine.Snapshot.Session is not null);

        priority.Current = ["b", "a"]; // kullanıcı sürükleyip bıraktı
        await AdvanceUntilAsync(clock, () => engine.Snapshot.Target?.Channel.Login == "chb");

        Assert.Contains(engine.Snapshot.Recent, e => e.Text.Contains("Öncelikli kampanya", StringComparison.Ordinal));
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task OrderChange_DoesNotInterruptADropThatIsAlmostDone()
    {
        var platform = new FakePlatform();
        var priority = new Priority { Current = ["a", "b"] };
        var (engine, clock, cts, run) = Start(platform, priority);
        platform.Snapshot = Fx.Snap((Streamer("a"), [Fx.Ch("1", "cha")]), (Streamer("b"), [Fx.Ch("2", "chb")]));
        platform.Session = Moving(clock, _ => "a-d", 540); // ~%90+: bitmeye yakın
        await AdvanceUntilAsync(clock, () => engine.Snapshot.Target?.Channel.Login == "cha" && engine.Snapshot.Session is not null);

        priority.Current = ["b", "a"];
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(2);
        }

        Assert.Equal("cha", engine.Snapshot.Target?.Channel.Login);
        Assert.DoesNotContain("chb", platform.Watched);
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task HigherPriorityStreamerOffline_KeepsTheCurrentOne_WithoutChannelHopping()
    {
        var platform = new FakePlatform();
        var priority = new Priority { Current = ["a", "b"] };
        var (engine, clock, cts, run) = Start(platform, priority);
        platform.Snapshot = Fx.Snap((Streamer("a"), []), (Streamer("b"), [Fx.Ch("2", "chb")])); // a çevrimdışı
        platform.Session = Moving(clock, _ => "b-d", 30);
        await AdvanceUntilAsync(clock, () => engine.Snapshot.Target?.Channel.Login == "chb" && engine.Snapshot.Session is not null);

        for (var i = 0; i < 80; i++) // birkaç keşif döngüsü
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(2);
        }

        Assert.Equal("chb", engine.Snapshot.Target?.Channel.Login);
        Assert.Equal(1, platform.Watched.Count(w => w == "chb")); // yeniden başlatılmadı
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task SameCampaignGainsABiggerChannel_CurrentChannelIsNotSwapped()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform, new Priority());
        var camp = Streamer("duo");
        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "small", 10)]));
        platform.Session = Moving(clock, _ => "duo-d", 30);
        await AdvanceUntilAsync(clock, () => engine.Snapshot.Target?.Channel.Login == "small" && engine.Snapshot.Session is not null);

        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "small", 10), Fx.Ch("2", "huge", 9000)]));
        for (var i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(2);
        }

        Assert.Equal("small", engine.Snapshot.Target?.Channel.Login);
        Assert.DoesNotContain("huge", platform.Watched);
        await StopAsync(cts, run);
    }

    [Fact]
    public async Task WatchedStreamerGoesOffline_AnotherAssignedStreamerOfTheSameDropTakesOver()
    {
        var platform = new FakePlatform();
        var (engine, clock, cts, run) = Start(platform, new Priority());
        var camp = Streamer("duo");
        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("1", "first", 900), Fx.Ch("2", "second", 100)]));
        platform.Session = Moving(clock, _ => "duo-d", 30);
        await AdvanceUntilAsync(clock, () => engine.Snapshot.Target?.Channel.Login == "first" && engine.Snapshot.Session is not null);

        platform.Snapshot = Fx.Snap((camp, [Fx.Ch("2", "second", 100)]));
        platform.RaiseOffline("1");
        await AdvanceUntilAsync(clock, () => engine.Snapshot.Target?.Channel.Login == "second");

        Assert.NotEqual(EngineState.Degraded, engine.Snapshot.State);
        await StopAsync(cts, run);
    }
}

public class PrioritySettingsTests
{
    [Fact]
    public void Lists_AreCleanedPerPlatform_AndOldFilesLoadEmpty()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"data\":{\"autoClaim\":true}}");

        var settings = new SettingsService(path);

        Assert.Empty(settings.Current.TwitchStreamerPriority);
        Assert.Empty(settings.Current.KickStreamerPriority);
        Assert.True(settings.Current.AutoClaim);

        settings.Update(s => s with { TwitchStreamerPriority = ["a", " b ", "a", "", "c"], KickStreamerPriority = ["k1"] });

        Assert.Equal(["a", "b", "c"], settings.Current.TwitchStreamerPriority);
        var reloaded = new SettingsService(path);
        Assert.Equal(["a", "b", "c"], reloaded.Current.TwitchStreamerPriority);
        Assert.Equal(["k1"], reloaded.Current.KickStreamerPriority); // Twitch ve Kick ayrı listeler
    }
}
