using System.Net;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Kick;
using RustDroper.Core.Platforms.Twitch;
using static RustDroper.Core.Tests.StubHandler;
using static RustDroper.Core.Tests.TestTime;

namespace RustDroper.Core.Tests;

public class KickAccountTests
{
    private const string Token = "good-token-123456";

    private static (KickAccount Account, InMemorySecretStore Secrets) Create(Func<RecordedRequest, HttpResponseMessage> respond, FakeTimeProvider? time = null)
    {
        var secrets = new InMemorySecretStore();
        var auth = new KickAuthClient(new HttpClient(new StubHandler(respond)), time);
        return (new KickAccount(new KickSessionManager(auth, secrets)), secrets);
    }

    private static HttpResponseMessage Ok(RecordedRequest r) =>
        r.Uri.AbsolutePath == "/api/v1/user"
            ? Json(HttpStatusCode.OK, """{"id":7,"username":"rustfan"}""")
            : Json(HttpStatusCode.OK, """{"data":[]}""");

    [Fact]
    public async Task Restore_MapsAllStatuses()
    {
        var (none, _) = Create(Ok);
        Assert.Equal(TwitchRestoreStatus.NotLoggedIn, (await none.RestoreAsync()).Status);

        var (valid, secrets) = Create(Ok);
        secrets.Set(KickSessionManager.AccessKey, Token);
        secrets.Set(KickSessionManager.UserIdKey, "7");
        secrets.Set(KickSessionManager.LoginKey, "rustfan");
        var restored = await valid.RestoreAsync();
        Assert.Equal(TwitchRestoreStatus.Restored, restored.Status);
        Assert.Equal(new TwitchSession(Token, "7", "rustfan", null), restored.Session);

        var (stale, staleSecrets) = Create(_ => Json(HttpStatusCode.Forbidden, KickAuthClientTests.ForbiddenJson));
        staleSecrets.Set(KickSessionManager.AccessKey, Token);
        Assert.Equal(TwitchRestoreStatus.NeedsLogin, (await stale.RestoreAsync()).Status);

        var (offline, offlineSecrets) = Create(_ => Json(HttpStatusCode.BadGateway, "{}"));
        offlineSecrets.Set(KickSessionManager.AccessKey, Token);
        Assert.Equal(TwitchRestoreStatus.Offline, (await offline.RestoreAsync()).Status);
        Assert.Equal(Token, offlineSecrets.Get(KickSessionManager.AccessKey));
    }

    [Fact]
    public async Task Restore_WithoutStoredUserId_UsesLoginAsIdentity()
    {
        var (account, secrets) = Create(Ok);
        secrets.Set(KickSessionManager.AccessKey, Token);

        var restored = await account.RestoreAsync();

        Assert.Equal(KickSessionManager.UnknownLogin, restored.Session!.UserId);
    }

    [Fact]
    public async Task Login_ReportsLinkAsDeviceCode_AndReturnsSession()
    {
        var fake = new FakeTimeProvider();
        var polls = 0;
        var (account, secrets) = Create(r =>
        {
            if (r.Uri.AbsolutePath.StartsWith("/api/tv/link/authenticate/", StringComparison.Ordinal))
            {
                return ++polls < 2 ? Json(HttpStatusCode.Forbidden, KickAuthClientTests.PendingJson) : Json(HttpStatusCode.OK, $$"""{"token":"{{Token}}"}""");
            }

            return Ok(r);
        }, fake);
        TwitchDeviceCode? shown = null;

        var session = await PumpAsync(account.LoginAsync(c => shown = c), fake);

        Assert.NotNull(shown);
        Assert.Equal("kick.com", shown.VerificationUri.Host);
        Assert.Contains("uuid=" + shown.DeviceCode, shown.VerificationUri.Query, StringComparison.Ordinal);
        Assert.Matches("^[0-9]{6}$", shown.UserCode);
        Assert.Equal("rustfan", session.Login);
        Assert.Equal(Token, secrets.Get(KickSessionManager.AccessKey));
    }

    [Fact]
    public async Task Login_Expiry_BecomesTwitchStyleLoginException()
    {
        var fake = new FakeTimeProvider();
        var (account, _) = Create(_ => Json(HttpStatusCode.Forbidden, KickAuthClientTests.PendingJson), fake);

        var ex = await Assert.ThrowsAsync<TwitchLoginException>(() => PumpAsync(account.LoginAsync(_ => { }), fake, maxSteps: 2000));

        Assert.Equal(TwitchLoginFailure.Expired, ex.Failure);
    }

    [Fact]
    public void Logout_ClearsSecrets()
    {
        var (account, secrets) = Create(Ok);
        secrets.Set(KickSessionManager.AccessKey, Token);

        account.Logout();

        Assert.Null(secrets.Get(KickSessionManager.AccessKey));
    }
}

public class KickInventoryBuilderTests
{
    private static Campaign Camp(string id, string name, params (string Id, int Minutes)[] rewards) =>
        new(Platform.Kick, id, name, "rust", "Rust", CampaignStatus.Active, null, null, CampaignScope.General, [],
            [.. rewards.Select(r => new DropReward(r.Id, "Ödül " + r.Id, r.Minutes, null))], new Uri("https://kick.facepunch.com/connect"));

    [Fact]
    public void MapsProgressClaimedAndClaimable()
    {
        var campaign = Camp("C1", "Genel", ("R3", 360), ("R1", 120), ("R2", 240));
        var progress = new KickCampaignProgress("C1", 250, [new("R1", 1, true), new("R2", 1, false), new("R3", 0.69, false)], AppConnected: true);

        var inventory = KickInventoryBuilder.Build([progress], [campaign]);

        Assert.Equal(["R1", "R2", "R3"], inventory.InProgress.Select(p => p.DropId));
        var r1 = inventory.InProgress[0];
        Assert.True(r1.IsClaimed);
        Assert.Equal(120, r1.WatchedMinutes);
        var r2 = inventory.InProgress[1];
        Assert.False(r2.IsClaimed);
        Assert.Equal(240, r2.WatchedMinutes);
        Assert.True(r2.IsClaimable);
        Assert.Equal("R2", r2.ClaimId);
        var r3 = inventory.InProgress[2];
        Assert.Equal(250, r3.WatchedMinutes);
        Assert.False(r3.IsClaimable);
        var claimed = Assert.Single(inventory.Claimed);
        Assert.Equal("R1", claimed.Id);
        Assert.Equal(Platform.Kick, claimed.Platform);
        Assert.Equal("kick.facepunch.com", claimed.AccountLinkUrl!.Host);
        Assert.True(claimed.AccountLinked); // user_app_connected → Facepunch/Steam bağlantı durumu
    }

    [Fact]
    public void UnitsInferredFromRewardFractions_WhenServerGivesNone()
    {
        var campaign = Camp("C1", "Genel", ("R1", 120), ("R2", 240));
        var progress = new KickCampaignProgress("C1", null, [new("R1", 1, false), new("R2", 0.25, false)]);

        var inventory = KickInventoryBuilder.Build([progress], [campaign]);

        Assert.Equal(120, inventory.InProgress[0].WatchedMinutes);
        Assert.Equal(120, inventory.InProgress[1].WatchedMinutes);
    }

    [Fact]
    public void OtherGamesAndNoProgressCampaigns_AreIgnored()
    {
        var rust = Camp("C1", "Rust", ("R1", 120));
        var other = new KickCampaignProgress("MINECRAFT", 500, [new("M1", 1, true)]);

        Assert.Empty(KickInventoryBuilder.Build([other], [rust]).InProgress);
        Assert.Empty(KickInventoryBuilder.Build([], [rust]).Claimed);
    }
}

public class KickConnectionFactoryTests
{
    private static KickConnectionFactory Factory(FakeKickServer server, TempDir dir, JsonLinesLog<ClaimRecord>? history = null) =>
        new(new HttpClient(server.Handler), history ?? new JsonLinesLog<ClaimRecord>(dir.File("history.jsonl")), () => "rust",
            hlsHttp: () => new HttpClient(server.Handler), limiter: FakeKickServer.OpenLimiter());

    private static readonly TwitchSession Session = new("good-token-123456", "7", "rustfan", null);

    [Fact]
    public async Task Inventory_CombinesServerProgressWithRustCampaigns()
    {
        using var dir = new TempDir();
        var server = new FakeKickServer
        {
            Progress = FakeKickServer.ProgressList(
                (FakeKickServer.RustGeneralCampaign, [("R_G1", true, 1), ("R_G2", false, 0.6), ("R_G3", false, 0)]),
                ("C_MC", [("R_M1", true, 1)])),
        };
        using var connection = Factory(server, dir).Create(Session, () => Session.AccessToken, autoClaim: false);

        var inventory = await connection.GetInventoryAsync(CancellationToken.None);

        Assert.Equal(3, inventory.InProgress.Count);
        Assert.All(inventory.InProgress, p => Assert.Equal(FakeKickServer.RustGeneralCampaign, p.CampaignId));
        Assert.Equal(["R_G1"], inventory.Claimed.Select(c => c.Id));
    }

    [Fact]
    public void LoadClaimedDropIds_OnlyKickClaimed()
    {
        using var dir = new TempDir();
        var history = new JsonLinesLog<ClaimRecord>(dir.File("history.jsonl"));
        history.Append(new ClaimRecord(DateTimeOffset.UtcNow, "Kick", "R1", "C1", "", "Claimed", null, 1));
        history.Append(new ClaimRecord(DateTimeOffset.UtcNow, "Kick", "R2", "C1", "", "Rejected", "nope", 1));
        history.Append(new ClaimRecord(DateTimeOffset.UtcNow, "Twitch", "T1", "I1", "9", "Claimed", null, 1));

        var ids = Factory(new FakeKickServer(), dir, history).LoadClaimedDropIds();

        Assert.Equal(["R1"], ids);
    }

    [Fact]
    public async Task AutoClaimOff_NeverClaims_AutoClaimOn_ClaimsOnlyRustRewards()
    {
        using var dir = new TempDir();
        var server = new FakeKickServer
        {
            Progress = FakeKickServer.ProgressList(
                (FakeKickServer.RustGeneralCampaign, [("R_G1", false, 1)]),
                ("C_MC", [("R_M1", false, 1)])),
        };
        var factory = Factory(server, dir);
        using var off = factory.Create(Session, () => Session.AccessToken, autoClaim: false);
        using var on = factory.Create(Session, () => Session.AccessToken, autoClaim: true);
        await off.Platform.DiscoverAsync("rust", CancellationToken.None);
        await on.Platform.DiscoverAsync("rust", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var offRun = off.Platform.RunBackgroundAsync(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offRun);
        Assert.Empty(server.Claims);

        var claimer = ((KickDropPlatform)on.Platform);
        Assert.Contains(FakeKickServer.RustGeneralCampaign, claimer.KnownCampaignIds);
        Assert.DoesNotContain("C_MC", claimer.KnownCampaignIds);
    }
}

public class KickDropperServiceTests : IDisposable
{
    private const string Token = "good-token-123456";
    private readonly TempDir _dir = new();
    private readonly FakeKickServer _server = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly SettingsService _settings;
    private readonly DropperService _service;
    private readonly List<UserNotification> _notifications = [];

    public KickDropperServiceTests()
    {
        _settings = new SettingsService(_dir.File("settings.json"));
        _server.Channels["rustfan_one"] = FakeKickServer.ChannelJson("rustfan_one", "100001", live: true, viewers: 321);
        _server.Channels["rustfan_four"] = FakeKickServer.ChannelJson("rustfan_four", "100004", live: false);
        _server.Summaries[FakeKickServer.RustStreamerCampaign] = FakeKickServer.Summary(FakeKickServer.RustStreamerCampaign, 9, ("R_S1", false, 0.0375));
        _server.Progress = FakeKickServer.ProgressList((FakeKickServer.RustStreamerCampaign, [("R_S1", false, 0.0375)]));
        _secrets.Set(KickSessionManager.AccessKey, Token);
        _secrets.Set(KickSessionManager.UserIdKey, "7");
        _secrets.Set(KickSessionManager.LoginKey, "rustfan");

        var http = new HttpClient(_server.Handler);
        var auth = new KickAuthClient(http);
        var account = new KickAccount(new KickSessionManager(auth, _secrets));
        var factory = new KickConnectionFactory(
            http,
            new JsonLinesLog<ClaimRecord>(_dir.File("history.jsonl")),
            () => _settings.Current.GameSlug,
            hlsHttp: () => new HttpClient(_server.Handler),
            limiter: FakeKickServer.OpenLimiter(),
            claimPollInterval: TimeSpan.FromMilliseconds(50));
        _service = new DropperService(
            account,
            factory,
            _settings,
            engineOptions: s => new EngineOptions { GameSlug = s.GameSlug, PollInterval = TimeSpan.FromMilliseconds(30) },
            platform: PlatformProfile.Kick);
        _service.Notification += _notifications.Add;
    }

    public void Dispose()
    {
        _service.Dispose();
        _dir.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Koşul zamanında gerçekleşmedi.");
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Initialize_RestoresKickSession_AndLoadsCampaignsAndInventory()
    {
        await _service.InitializeAsync();

        var snapshot = _service.Snapshot;
        Assert.Equal(AccountStatus.LoggedIn, snapshot.Account.Status);
        Assert.Equal("rustfan", snapshot.Account.Login);
        Assert.Equal(PlatformProfile.Kick, _service.Profile);
        Assert.Contains(snapshot.Campaigns!.Campaigns, c => c.Id == FakeKickServer.RustStreamerCampaign);
        Assert.Equal(9, snapshot.Inventory!.InProgress.Single(p => p.DropId == "R_S1").WatchedMinutes);
    }

    [Fact]
    public async Task Start_WatchesLiveStreamerChannel_AndReportsServerProgress_WithoutClaiming()
    {
        await _service.InitializeAsync();

        Assert.True(_service.Start());
        await WaitUntilAsync(() => _service.Snapshot.Engine is { State: EngineState.Watching, Session: not null });

        var engine = _service.Snapshot.Engine;
        Assert.Equal("rustfan_one", engine.Target!.Channel.Login);
        Assert.Equal(new DropSession("R_S1", 9, 240), engine.Session);
        await _service.StopAsync();
        Assert.Empty(_server.Claims); // otomatik claim varsayılan kapalı: claim isteği hiç gitmedi
    }

    [Fact]
    public async Task AutoClaimOn_CompleteReward_IsClaimedOnlyAgainstFakeServer_AndNotified()
    {
        _settings.Update(s => s with { AutoClaim = true });
        _server.Summaries[FakeKickServer.RustStreamerCampaign] = FakeKickServer.Summary(FakeKickServer.RustStreamerCampaign, 241, ("R_S1", false, 1));
        _server.Progress = FakeKickServer.ProgressList((FakeKickServer.RustStreamerCampaign, [("R_S1", false, 1)]));
        await _service.InitializeAsync();

        Assert.True(_service.Start());
        await WaitUntilAsync(() => _server.Claims.Any());
        await _service.StopAsync();

        Assert.Equal("""{"reward_id":"R_S1","campaign_id":"C_STREAMER"}""", _server.Claims.First().Body);
        Assert.Contains(_notifications, n => n.Kind == NotificationKind.DropClaimed);
    }

    [Fact]
    public async Task RejectedToken_NeedsLogin_WithKickNamedNotification()
    {
        _server.Override = r => r.Uri.AbsolutePath == "/api/v1/drops/progress" ? Json(HttpStatusCode.Forbidden, KickAuthClientTests.ForbiddenJson) : null;

        await _service.InitializeAsync();

        Assert.Equal(AccountStatus.NeedsLogin, _service.Snapshot.Account.Status);
        var note = Assert.Single(_notifications, n => n.Kind == NotificationKind.LoginRequired);
        Assert.Equal("kick:login-required", note.Key);
        Assert.StartsWith("Kick", note.Text, StringComparison.Ordinal);
        Assert.Null(_secrets.Get(KickSessionManager.AccessKey));
    }

    [Fact]
    public void ClaimNotificationsPointToKickRewardsPage()
    {
        var ready = UserNotification.ClaimReady("R1", "Emote", PlatformProfile.Kick);
        var failed = UserNotification.ClaimFailed("R1", "Emote", PlatformProfile.Kick);

        Assert.Equal(PlatformProfile.Kick.InventoryUrl, ready.Link);
        Assert.Contains("Kick", ready.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Twitch", failed.Text, StringComparison.Ordinal);
        Assert.Equal(UserNotification.TwitchInventory, UserNotification.ClaimReady("d", "x").Link);
        Assert.Equal("login-required", UserNotification.LoginRequired().Key);
    }
}
