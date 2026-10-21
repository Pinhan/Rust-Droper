using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Tests;

internal sealed class FakeEngineStateStore : IEngineStateStore
{
    public EngineMemory Memory { get; set; } = new();

    public Exception? LoadError { get; set; }

    public Exception? SaveError { get; set; }

    public int Saves { get; private set; }

    public EngineMemory Load() => LoadError is { } error ? throw error : Memory;

    public void Save(EngineMemory memory)
    {
        Saves++;
        if (SaveError is { } error)
        {
            throw error;
        }

        Memory = memory;
    }
}

public class StateStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = Fx.Start;
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string StatePath => _dir.File("state.json");

    private static TwitchInventorySnapshot Inventory() =>
        new(
            [new ClaimedReward(Platform.Twitch, "r1", "Rug", Now, 1, true, new Uri("https://twitch.facepunch.com/connect"), null)],
            [new DropProgress("c1", "Base Invaders", "d1", "Bed", 120, 30, false, null, true)]);

    [Fact]
    public void ClaimedReward_GameNameAndImage_SurviveARestart_AndOldFilesWithoutItLoad()
    {
        var first = new StateStore(StatePath);
        first.SaveInventory("42", new TwitchInventorySnapshot(
            [new ClaimedReward(Platform.Twitch, "r1", "Emote", Now, 2, true, null, new Uri("https://static-cdn.jtvnw.net/x/a.jpeg"), "Apex Legends")],
            []));

        var reloaded = new StateStore(StatePath).LoadInventory()!.Snapshot.Claimed.Single();

        Assert.Equal("Apex Legends", reloaded.GameName);
        Assert.Equal("static-cdn.jtvnw.net", reloaded.ImageUrl!.Host);

        // GameName alanı olmayan eski dosya: null olarak yüklenir
        var text = File.ReadAllText(StatePath);
        File.WriteAllText(StatePath, System.Text.RegularExpressions.Regex.Replace(text, """,\s*"gameName"\s*:\s*"[^"]*" """.Trim(), string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        var old = new StateStore(StatePath).LoadInventory()!.Snapshot.Claimed.Single();
        Assert.Null(old.GameName);
        Assert.Equal("Emote", old.Name);
    }

    [Fact]
    public void MemoryAndInventory_SurviveARestart()
    {
        var first = new StateStore(StatePath);
        first.ForUser("42").Save(new EngineMemory
        {
            CompletedUntil = { ["c-done"] = Now.AddHours(3) },
            CooldownUntil = { ["c-wait"] = Now.AddMinutes(40) },
        });
        first.SaveInventory("42", Inventory());

        var second = new StateStore(StatePath);
        var memory = second.ForUser("42").Load();
        var inventory = second.LoadInventory();

        Assert.Null(second.LoadWarning);
        Assert.Equal(Now.AddHours(3), memory.CompletedUntil["c-done"]);
        Assert.Equal(Now.AddMinutes(40), memory.CooldownUntil["c-wait"]);
        Assert.Equal("42", inventory!.UserId);
        Assert.Equal("Rug", inventory.Snapshot.Claimed.Single().Name);
        Assert.Equal("https://twitch.facepunch.com/connect", inventory.Snapshot.Claimed.Single().AccountLinkUrl!.AbsoluteUri);
        Assert.Equal("Bed", inventory.Snapshot.InProgress.Single().RewardName);
    }

    [Fact]
    public void OtherAccount_SeesNothing_AndOverwritingResetsTheOldAccountsData()
    {
        var store = new StateStore(StatePath);
        store.ForUser("42").Save(new EngineMemory { CompletedUntil = { ["c"] = Now.AddHours(1) } });
        store.SaveInventory("42", Inventory());

        Assert.Empty(store.ForUser("7").Load().CompletedUntil);

        store.ForUser("7").Save(new EngineMemory { CooldownUntil = { ["x"] = Now.AddHours(1) } });

        Assert.Empty(store.ForUser("42").Load().CompletedUntil);
        Assert.Null(store.LoadInventory()); // 42'nin envanteri 7'ye gösterilmez
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var store = new StateStore(StatePath);
        store.ForUser("42").Save(new EngineMemory { CompletedUntil = { ["c"] = Now.AddHours(1) } });
        store.SaveInventory("42", Inventory());

        store.Clear();

        var reloaded = new StateStore(StatePath);
        Assert.Empty(reloaded.ForUser("42").Load().CompletedUntil);
        Assert.Null(reloaded.LoadInventory());
    }

    [Fact]
    public void CorruptFile_FallsBackToDefault_WithoutThrowing()
    {
        File.WriteAllText(StatePath, "{ bu json degil");

        var store = new StateStore(StatePath);

        Assert.NotNull(store.LoadWarning);
        Assert.Empty(store.ForUser("42").Load().CompletedUntil);
        Assert.Null(store.LoadInventory());
        store.SaveInventory("42", Inventory()); // sonraki yazma çalışır
        Assert.NotNull(new StateStore(StatePath).LoadInventory());
    }

    [Fact]
    public void NewerSchemaVersion_IsIgnored()
    {
        File.WriteAllText(StatePath, """{ "schemaVersion": 99, "data": { "userId": "42", "memory": { "completedUntil": { "c": "2099-01-01T00:00:00Z" } } } }""");

        var store = new StateStore(StatePath);

        Assert.NotNull(store.LoadWarning);
        Assert.Empty(store.ForUser("42").Load().CompletedUntil);
    }

    [Fact]
    public void NullFieldsInFile_DoNotCrash()
    {
        File.WriteAllText(StatePath, """{ "schemaVersion": 1, "data": { "userId": "42", "memory": null, "inventory": { "claimed": null, "inProgress": null } } }""");

        var store = new StateStore(StatePath);

        Assert.Empty(store.ForUser("42").Load().CompletedUntil);
        Assert.Empty(store.LoadInventory()!.Snapshot.Claimed);
    }

    [Fact]
    public void WriteFailure_IsReportedNotThrown()
    {
        Directory.CreateDirectory(StatePath); // dosya yolu bir klasör: yazma başarısız olur
        var store = new StateStore(StatePath);

        store.ForUser("42").Save(new EngineMemory());

        Assert.NotNull(store.SaveWarning);
    }
}

public class EngineMemoryTests
{
    private static readonly Campaign Alpha = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 5));

    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> condition, int maxSteps = 3000)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(2);
        }

        Assert.True(condition(), "Beklenen durum (sahte saatle) oluşmadı.");
    }

    private static Func<LiveChannel, DropSession?> Progressive(FakeTimeProvider clock, string dropId, int required)
    {
        var firstSeen = new ConcurrentDictionary<string, DateTimeOffset>();
        return channel =>
        {
            var seen = firstSeen.GetOrAdd(channel.Id, _ => clock.GetUtcNow());
            var minutes = (int)(clock.GetUtcNow() - seen).TotalMinutes - 1;
            return minutes < 1 || minutes > required ? null : new DropSession(dropId, minutes, required);
        };
    }

    private static (DropEngine Engine, CancellationTokenSource Cts, Task Run) Start(FakePlatform platform, FakeTimeProvider clock, IEngineStateStore? store)
    {
        var engine = new DropEngine(platform, new EngineOptions { GameSlug = "rust" }, timeProvider: clock, stateStore: store);
        var cts = new CancellationTokenSource();
        return (engine, cts, engine.RunAsync(cts.Token));
    }

    private static async Task StopAsync(CancellationTokenSource cts, Task run)
    {
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static bool HasEvent(DropEngine engine, string fragment) =>
        engine.Snapshot.Recent.Any(e => e.Text.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public async Task CompletedCampaign_IsPersisted_AndNotReEnteredAfterRestart()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var store = new FakeEngineStateStore();
        var platform = new FakePlatform { Snapshot = Fx.Snap((Alpha, [Fx.Ch("1", "cha")])) };
        platform.Session = Progressive(clock, "dA", 5);
        var (engine, cts, run) = Start(platform, clock, store);

        await AdvanceUntilAsync(clock, () => HasEvent(engine, "Kampanya tamamlandı: Alpha"));
        await StopAsync(cts, run);

        Assert.True(store.Memory.CompletedUntil.ContainsKey("A"));
        Assert.True(store.Memory.CompletedUntil["A"] > clock.GetUtcNow());

        // "Yeniden başlatma": aynı hafızayla yeni motor; kampanyaya hiç girmemeli.
        var platform2 = new FakePlatform { Snapshot = Fx.Snap((Alpha, [Fx.Ch("1", "cha")])) };
        var (engine2, cts2, run2) = Start(platform2, clock, store);
        await AdvanceUntilAsync(clock, () => engine2.Snapshot.State == EngineState.Idle);
        await StopAsync(cts2, run2);

        Assert.Empty(platform2.Watched);
        Assert.True(HasEvent(engine2, "hatırlandı"));
    }

    [Fact]
    public async Task CompletedMark_ExpiresAtCampaignEnd_WhenThatIsSooner()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var store = new FakeEngineStateStore();
        var endsSoon = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddHours(2), ("dA", 5));
        var platform = new FakePlatform { Snapshot = Fx.Snap((endsSoon, [Fx.Ch("1", "cha")])) };
        platform.Session = Progressive(clock, "dA", 5);
        var (engine, cts, run) = Start(platform, clock, store);

        await AdvanceUntilAsync(clock, () => HasEvent(engine, "Kampanya tamamlandı"));
        await StopAsync(cts, run);

        Assert.Equal(Fx.Start.AddHours(2), store.Memory.CompletedUntil["A"]);
    }

    [Fact]
    public async Task StalledCampaignCooldown_IsPersisted_AndHonouredAfterRestart()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var store = new FakeEngineStateStore();
        var campaign = Fx.Camp("A", "Alpha", CampaignScope.General, Fx.Start.AddDays(1), ("dA", 60));
        var snapshot = Fx.Snap((campaign, [Fx.Ch("1", "ch1", 500), Fx.Ch("2", "ch2", 100)]));
        var platform = new FakePlatform { Snapshot = snapshot };
        var (engine, cts, run) = Start(platform, clock, store);

        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Idle && HasEvent(engine, "ilerleme alınamıyor"));
        await StopAsync(cts, run);

        Assert.True(store.Memory.CooldownUntil["A"] > clock.GetUtcNow());

        var platform2 = new FakePlatform { Snapshot = snapshot };
        var (engine2, cts2, run2) = Start(platform2, clock, store);
        await AdvanceUntilAsync(clock, () => engine2.Snapshot.State == EngineState.Idle);
        await StopAsync(cts2, run2);

        Assert.Empty(platform2.Watched);
    }

    [Fact]
    public async Task ExpiredEntries_AreIgnored()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var store = new FakeEngineStateStore
        {
            Memory = new EngineMemory
            {
                CompletedUntil = { ["A"] = Fx.Start.AddMinutes(-1) },
                CooldownUntil = { ["A"] = Fx.Start.AddSeconds(-1) },
            },
        };
        var platform = new FakePlatform { Snapshot = Fx.Snap((Alpha, [Fx.Ch("1", "cha")])) };
        platform.Session = Progressive(clock, "dA", 5);
        var (engine, cts, run) = Start(platform, clock, store);

        await AdvanceUntilAsync(clock, () => engine.Snapshot.State == EngineState.Watching);
        await StopAsync(cts, run);

        Assert.Contains("cha", platform.Watched);
        Assert.False(HasEvent(engine, "hatırlandı"));
    }

    [Fact]
    public async Task FailingStore_NeverStopsTheEngine()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var store = new FakeEngineStateStore { LoadError = new IOException("disk"), SaveError = new IOException("disk dolu") };
        var platform = new FakePlatform { Snapshot = Fx.Snap((Alpha, [Fx.Ch("1", "cha")])) };
        platform.Session = Progressive(clock, "dA", 5);
        var (engine, cts, run) = Start(platform, clock, store);

        await AdvanceUntilAsync(clock, () => HasEvent(engine, "Kampanya tamamlandı: Alpha"));

        Assert.False(run.IsFaulted);
        Assert.True(store.Saves >= 1);
        await StopAsync(cts, run);
    }
}

public class DropperServiceStateTests : IDisposable
{
    private static readonly TwitchSession Session = new("tok-1", "42", "testuser", null);
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _clock = new(Fx.Start);

    public void Dispose() => _dir.Dispose();

    private (DropperService Service, FakeAccount Account, FakeConnectionFactory Factory) Create()
    {
        var account = new FakeAccount();
        var factory = new FakeConnectionFactory();
        var service = new DropperService(
            account,
            factory,
            new SettingsService(_dir.File("settings.json")),
            _clock,
            stateStore: new StateStore(_dir.File("state.json")));
        return (service, account, factory);
    }

    private static TwitchInventorySnapshot Inventory(string rewardName) =>
        new([new ClaimedReward(Platform.Twitch, "r1", rewardName, Fx.Start, 1, true, null, null)], []);

    [Fact]
    public async Task CachedInventory_IsShownAfterRestart_EvenWhileOffline()
    {
        var (first, firstAccount, firstFactory) = Create();
        firstFactory.Inventory = Inventory("Rug");
        firstAccount.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, Session);
        await first.InitializeAsync();
        first.Dispose();

        var (second, secondAccount, _) = Create();
        secondAccount.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Offline, null);
        await second.InitializeAsync();

        Assert.Equal(AccountStatus.Offline, second.Snapshot.Account.Status);
        Assert.Equal("Rug", second.Snapshot.Inventory!.Claimed.Single().Name);
        second.Dispose();
    }

    [Fact]
    public async Task CachedInventory_IsDropped_WhenSessionIsInvalidOrADifferentAccount()
    {
        var (first, firstAccount, firstFactory) = Create();
        firstFactory.Inventory = Inventory("Rug");
        firstAccount.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, Session);
        await first.InitializeAsync();
        first.Dispose();

        var (needsLogin, needsLoginAccount, _) = Create();
        needsLoginAccount.Restore = new TwitchRestoreResult(TwitchRestoreStatus.NeedsLogin, null);
        await needsLogin.InitializeAsync();
        Assert.Null(needsLogin.Snapshot.Inventory);
        needsLogin.Dispose();

        var (other, otherAccount, otherFactory) = Create();
        otherFactory.InventoryError = new HttpRequestException("ağ yok"); // taze envanter gelmesin
        otherAccount.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, new TwitchSession("t", "999", "baskasi", null));
        await other.InitializeAsync();
        Assert.Null(other.Snapshot.Inventory); // 42'nin envanteri 999'a gösterilmez
        other.Dispose();
    }

    [Fact]
    public async Task Logout_ClearsThePersistedState()
    {
        var (first, firstAccount, firstFactory) = Create();
        firstFactory.Inventory = Inventory("Rug");
        firstAccount.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, Session);
        await first.InitializeAsync();
        await first.LogoutAsync();
        first.Dispose();

        Assert.Null(new StateStore(_dir.File("state.json")).LoadInventory());
    }

    [Fact]
    public async Task PersistedCooldown_IsHonouredByTheEngineTheServiceStarts()
    {
        var campaign = Fx.Camp("c1", "Base Invaders", CampaignScope.General, Fx.Start.AddDays(9), ("d1", 60));
        new StateStore(_dir.File("state.json")).ForUser("42").Save(new EngineMemory { CooldownUntil = { ["c1"] = Fx.Start.AddHours(1) } });

        var (service, account, factory) = Create();
        factory.Platform.Snapshot = Fx.Snap((campaign, [Fx.Ch("1", "streamer")]));
        account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, Session);
        await service.InitializeAsync();
        service.Start();
        var started = Environment.TickCount64;
        while (service.Snapshot.Engine.State != EngineState.Idle && Environment.TickCount64 - started < 5000)
        {
            await Task.Delay(10);
        }

        var state = service.Snapshot.Engine.State;
        await service.StopAsync();

        Assert.Equal(EngineState.Idle, state);
        Assert.Empty(factory.Platform.Watched);
        service.Dispose();
    }
}
