using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Tests;

internal sealed class FakeAccount : IPlatformAccount
{
    private readonly Queue<TwitchRestoreResult> _restores = new();

    public TwitchRestoreResult Restore { get; set; } = new(TwitchRestoreStatus.NotLoggedIn, null);

    public int RestoreCalls { get; private set; }

    /// <summary>Verilirse geri yükleme bunu bekler (ör. "ağ askıda": iptal edilene kadar dönmeyen doğrulama).</summary>
    public Func<CancellationToken, Task<TwitchRestoreResult>>? RestoreOverride { get; set; }

    public int LogoutCalls { get; private set; }

    public Func<Action<TwitchDeviceCode>, CancellationToken, Task<TwitchSession>> Login { get; set; } =
        (_, _) => Task.FromResult(new TwitchSession("login-token", "7", "newuser", null));

    public void QueueRestore(TwitchRestoreResult result) => _restores.Enqueue(result);

    public Task<TwitchRestoreResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        RestoreCalls++;
        if (RestoreOverride is { } hang)
        {
            return hang(cancellationToken);
        }

        return Task.FromResult(_restores.Count > 0 ? _restores.Dequeue() : Restore);
    }

    public Task<TwitchSession> LoginAsync(Action<TwitchDeviceCode> onCode, CancellationToken cancellationToken = default) =>
        Login(onCode, cancellationToken);

    public void Logout() => LogoutCalls++;
}

internal sealed class FakeConnection(FakePlatform platform, FakeConnectionFactory owner) : IDropConnection
{
    public IDropPlatform Platform => platform;

    public Task<TwitchInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref owner.InventoryCalls);
        return owner.InventoryError is { } error
            ? Task.FromException<TwitchInventorySnapshot>(error)
            : Task.FromResult(owner.Inventory);
    }

    public void Dispose() => Interlocked.Increment(ref owner.Disposed);
}

internal sealed class FakeConnectionFactory : IDropConnectionFactory
{
    public FakePlatform Platform { get; } = new();

    public TwitchInventorySnapshot Inventory { get; set; } = new([], []);

    public Exception? InventoryError { get; set; }

    public IReadOnlyCollection<string> History { get; set; } = [];

    public List<(bool AutoClaim, string? Token)> Created { get; } = [];

    public int InventoryCalls;

    public int Disposed;

    public IDropConnection Create(TwitchSession session, Func<string?> accessToken, bool autoClaim)
    {
        lock (Created)
        {
            Created.Add((autoClaim, accessToken()));
        }

        return new FakeConnection(Platform, this);
    }

    public IReadOnlyCollection<string> LoadClaimedDropIds() => History;
}

public class DropperServiceTests : IDisposable
{
    private static readonly TwitchSession Session = new("tok-1", "42", "testuser", null);
    private readonly TempDir _dir = new();
    private readonly FakeAccount _account = new();
    private readonly FakeConnectionFactory _factory = new();
    private readonly FakeTimeProvider _clock = new(Fx.Start);
    private readonly SettingsService _settings;
    private readonly DropperService _service;

    public DropperServiceTests()
    {
        _settings = new SettingsService(_dir.File("settings.json"));
        _service = new DropperService(_account, _factory, _settings, _clock);
        var channel = Fx.Ch("1", "streamer");
        var campaign = Fx.Camp("c1", "Base Invaders", CampaignScope.General, Fx.Start.AddDays(9), ("d1", 60), ("d2", 180));
        _factory.Platform.Snapshot = Fx.Snap((campaign, [channel]));
        _factory.Platform.Session = _ => new DropSession("d1", 5, 60);
    }

    public void Dispose()
    {
        _service.Dispose();
        _dir.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
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

    private async Task LoggedInAsync()
    {
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, Session);
        await _service.InitializeAsync();
    }

    [Fact]
    public async Task Initialize_NoSavedSession_IsLoggedOut()
    {
        await _service.InitializeAsync();

        Assert.Equal(AccountStatus.LoggedOut, _service.Snapshot.Account.Status);
        Assert.False(_service.Start());
    }

    [Fact]
    public async Task Initialize_RestoredSession_LoadsInventoryAndCampaigns()
    {
        _factory.Inventory = new TwitchInventorySnapshot(
            [new ClaimedReward(Platform.Twitch, "r1", "Rug", Fx.Start, 1, true, new Uri("https://twitch.facepunch.com/connect"), null)],
            []);

        await LoggedInAsync();

        var snapshot = _service.Snapshot;
        Assert.Equal(AccountStatus.LoggedIn, snapshot.Account.Status);
        Assert.Equal("testuser", snapshot.Account.Login);
        Assert.Equal("Rug", snapshot.Inventory!.Claimed.Single().Name);
        Assert.Equal(["Base Invaders"], snapshot.Campaigns!.Campaigns.Select(c => c.Name));
        Assert.Null(snapshot.RefreshError);
    }

    [Theory]
    [InlineData(TwitchRestoreStatus.NeedsLogin, AccountStatus.NeedsLogin)]
    [InlineData(TwitchRestoreStatus.Offline, AccountStatus.Offline)]
    public async Task Initialize_MapsRestoreStatus(TwitchRestoreStatus restore, AccountStatus expected)
    {
        _account.Restore = new TwitchRestoreResult(restore, null);

        await _service.InitializeAsync();

        Assert.Equal(expected, _service.Snapshot.Account.Status);
    }

    [Fact]
    public async Task Offline_IsRetriedAndRecovers()
    {
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Offline, null);
        _account.QueueRestore(new TwitchRestoreResult(TwitchRestoreStatus.Offline, null));
        _account.QueueRestore(new TwitchRestoreResult(TwitchRestoreStatus.Restored, Session));

        await _service.InitializeAsync();
        Assert.Equal(AccountStatus.Offline, _service.Snapshot.Account.Status);

        // Yeniden deneme zamanlayıcısı arka planda kurulur: sahte saati döngüde ilerlet.
        await WaitUntilAsync(() =>
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            return _service.Snapshot.Account.Status == AccountStatus.LoggedIn;
        });
    }

    [Fact]
    public async Task Login_ReportsDeviceCode_ThenLoggedIn_AndSecondLoginIsIgnored()
    {
        var gate = new TaskCompletionSource<TwitchSession>();
        var seen = new List<TwitchDeviceCode>();
        _account.Login = (onCode, _) =>
        {
            onCode(new TwitchDeviceCode("dev", "ABCD-EFGH", new Uri("https://www.twitch.tv/activate"), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5)));
            return gate.Task;
        };
        _service.DeviceCodeIssued += seen.Add;

        var first = _service.LoginAsync();
        await WaitUntilAsync(() => _service.Snapshot.Account.DeviceCode is not null);
        await _service.LoginAsync(); // ikinci istek yok sayılmalı

        Assert.Equal(AccountStatus.LoggingIn, _service.Snapshot.Account.Status);
        Assert.Equal("ABCD-EFGH", _service.Snapshot.Account.DeviceCode!.UserCode);
        Assert.Single(seen);

        gate.SetResult(new TwitchSession("t", "9", "kullanici", null));
        await first;

        Assert.Equal(AccountStatus.LoggedIn, _service.Snapshot.Account.Status);
        Assert.Equal("kullanici", _service.Snapshot.Account.Login);
    }

    [Fact]
    public async Task Login_Expired_ShowsErrorAndStaysLoggedOut()
    {
        _account.Login = (_, _) => throw new TwitchLoginException(TwitchLoginFailure.Expired, "süre doldu");

        await _service.LoginAsync();

        Assert.Equal(AccountStatus.LoggedOut, _service.Snapshot.Account.Status);
        Assert.Contains("zamanında", _service.Snapshot.Account.Error);
    }

    [Fact]
    public async Task Login_Cancelled_ReturnsToLoggedOutWithoutError()
    {
        _account.Login = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Session;
        };

        var login = _service.LoginAsync();
        await WaitUntilAsync(() => _service.Snapshot.Account.Status == AccountStatus.LoggingIn);
        _service.CancelLogin();
        await login;

        Assert.Equal(AccountStatus.LoggedOut, _service.Snapshot.Account.Status);
        Assert.Null(_service.Snapshot.Account.Error);
    }

    [Fact]
    public async Task StartStop_RunsEngine_AndCanRestart()
    {
        await LoggedInAsync();

        Assert.True(_service.Start());
        Assert.False(_service.Start()); // zaten çalışıyor
        await WaitUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Watching);
        Assert.True(_service.IsRunning);
        Assert.Equal("streamer", _service.Snapshot.Engine.Target!.Channel.Login);

        await _service.StopAsync();

        Assert.False(_service.IsRunning);
        Assert.Equal(EngineState.Stopped, _service.Snapshot.Engine.State);
        Assert.Equal(2, Volatile.Read(ref _factory.Disposed)); // açılış yenilemesi + motor bağlantısı kapandı
        Assert.True(_service.Start());
        await _service.StopAsync();
    }

    [Fact]
    public async Task AutoClaim_FollowsSettingsAtStart()
    {
        _settings.Update(s => s with { AutoClaim = false });
        await LoggedInAsync();

        _service.Start();
        await _service.StopAsync();
        _settings.Update(s => s with { AutoClaim = true });
        _service.Start();
        await _service.StopAsync();

        List<(bool AutoClaim, string? Token)> created;
        lock (_factory.Created)
        {
            created = [.. _factory.Created];
        }

        // [0] = açılış yenilemesi (hiçbir zaman claim etmez), [1] = ilk Start (kapalı), [2] = ikinci Start (açık).
        Assert.False(created[0].AutoClaim);
        Assert.False(created[1].AutoClaim);
        Assert.True(created[2].AutoClaim);
    }

    [Fact]
    public async Task AlreadyClaimedDrops_FromHistoryAndInventory_AreNotWatched()
    {
        _factory.History = ["d1"];
        _factory.Inventory = new TwitchInventorySnapshot([], [new DropProgress("c1", "c1", "d2", "Ödül", 180, 180, true, null, true)]);
        await LoggedInAsync();

        _service.Start();
        await WaitUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Idle);
        await _service.StopAsync();

        Assert.Empty(_factory.Platform.Watched);
        Assert.Contains("d1", _service.Snapshot.ClaimedDropIds);
        Assert.Contains("d2", _service.Snapshot.ClaimedDropIds);
    }

    [Fact]
    public async Task Claim_TriggersInventoryRefresh()
    {
        await LoggedInAsync();
        _service.Start();
        await WaitUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Watching);
        var before = Volatile.Read(ref _factory.InventoryCalls);

        _factory.Platform.RaiseClaim(new ClaimNotice("d1", true, "ELIGIBLE_FOR_ALL"));

        await WaitUntilAsync(() => Volatile.Read(ref _factory.InventoryCalls) > before);
        await _service.StopAsync();
    }

    [Fact]
    public async Task AuthRequired_WithUnrecoverableSession_AsksForRelogin()
    {
        await LoggedInAsync();
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.NeedsLogin, null);
        _factory.Platform.DiscoverError = new PlatformAuthException("401");

        _service.Start();
        await WaitUntilAsync(() => _service.Snapshot.Account.Status == AccountStatus.NeedsLogin);
        await _service.StopAsync();

        Assert.Equal(EngineState.Stopped, _service.Snapshot.Engine.State);
    }

    [Fact]
    public async Task AuthRequired_SilentRefresh_SwapsTokenWithoutRestartingEngine()
    {
        await LoggedInAsync();
        _account.Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, new TwitchSession("tok-2", "42", "testuser", null));
        _factory.Platform.DiscoverError = new PlatformAuthException("401");

        _service.Start();
        await WaitUntilAsync(() => _account.RestoreCalls >= 2);
        await WaitUntilAsync(() => _service.Snapshot.Account.Status == AccountStatus.LoggedIn);
        await _service.StopAsync();

        // Yenilenen token sonraki bağlantılarda kullanılır; hesap hâlâ bağlı.
        Assert.Equal("testuser", _service.Snapshot.Account.Login);
        await _service.RefreshAsync();
        lock (_factory.Created)
        {
            Assert.Equal("tok-2", _factory.Created[^1].Token);
        }
    }

    [Fact]
    public async Task Logout_StopsEngine_ClearsDataAndCallsAccount()
    {
        await LoggedInAsync();
        _service.Start();
        await WaitUntilAsync(() => _service.Snapshot.Engine.State == EngineState.Watching);

        await _service.LogoutAsync();

        Assert.False(_service.IsRunning);
        Assert.Equal(1, _account.LogoutCalls);
        Assert.Equal(AccountStatus.LoggedOut, _service.Snapshot.Account.Status);
        Assert.Null(_service.Snapshot.Inventory);
        Assert.Null(_service.Snapshot.Campaigns);
        Assert.False(_service.Start());
    }

    [Fact]
    public async Task RefreshFailure_IsReportedNotThrown()
    {
        await LoggedInAsync();
        _factory.InventoryError = new HttpRequestException("ağ yok");

        await _service.RefreshAsync();

        Assert.Contains("ağ yok", _service.Snapshot.RefreshError);
        Assert.Equal(AccountStatus.LoggedIn, _service.Snapshot.Account.Status);

        _factory.InventoryError = null;
        await _service.RefreshAsync();
        Assert.Null(_service.Snapshot.RefreshError);
    }
}
