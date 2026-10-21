using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Kick;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;
using static RustDroper.Core.Tests.StubHandler;

namespace RustDroper.Core.Tests;

/// <summary>Sahte Kick sunucusu: yol → cevap tablosu; tüm istekleri kaydeder. Gerçek ağa çıkılmaz.</summary>
internal sealed class FakeKickServer
{
    /// <summary>Sahte saat ilerlemediğinden gerçek hız sınırlayıcı testi kilitler; testlerde geniş pencere kullanılır.</summary>
    public static SlidingWindowRateLimiter OpenLimiter() => new(100_000, TimeSpan.FromSeconds(1));

    public const string RustStreamerCampaign = "C_STREAMER";
    public const string RustGeneralCampaign = "C_GENERAL";

    public Dictionary<string, string> Channels { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Summaries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> CampaignStreams { get; } = new(StringComparer.Ordinal);

    public string Progress { get; set; } = """{"data":[]}""";

    public string CategoryStreams { get; set; } = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kick-livestreams-rust.json"));

    public Func<RecordedRequest, HttpResponseMessage?>? Override { get; set; }

    private int _mediaSequence;

    public StubHandler Handler { get; }

    public FakeKickServer()
    {
        Handler = new StubHandler(Respond);
    }

    public IEnumerable<RecordedRequest> Claims => Handler.Requests.Where(r => r.Method == "POST" && r.Uri.AbsolutePath.EndsWith("/drops/claim", StringComparison.Ordinal));

    public static string CampaignsJson => $$$"""
        {"data":[
          {"id":"{{{RustStreamerCampaign}}}","name":"Streamer Drop","status":"active","starts_at":"2026-10-15T16:00:00Z","ends_at":"2026-10-30T21:00:00Z",
           "category":{"id":13,"slug":"rust","name":"Rust"},
           "channels":[{"id":"100001","slug":"rustfan_one","user":{"username":"RustFan_One"}},{"id":"100004","slug":"rustfan_four","user":{"username":"RustFan_Four"}}],
           "rewards":[{"id":"R_S1","name":"Emote","required_units":240}],"connect_url":""},
          {"id":"{{{RustGeneralCampaign}}}","name":"General Drops","status":"active","starts_at":"2026-10-15T16:00:00Z","ends_at":"2026-10-30T21:00:00Z",
           "category":{"id":13,"slug":"rust","name":"Rust"},"channels":[],
           "rewards":[{"id":"R_G3","name":"Three","required_units":360},{"id":"R_G1","name":"One","required_units":120},{"id":"R_G2","name":"Two","required_units":240}]},
          {"id":"C_OLD","name":"Old Rust","status":"expired","starts_at":"2026-06-11T00:00:00Z","ends_at":"2026-06-21T00:00:00Z",
           "category":{"id":13,"slug":"rust","name":"Rust"},"channels":[],"rewards":[{"id":"R_O1","required_units":120}]},
          {"id":"C_MC","name":"Minecraft","status":"active","starts_at":"2026-10-01T00:00:00Z","ends_at":"2026-10-30T00:00:00Z",
           "category":{"id":10,"slug":"minecraft","name":"Minecraft"},"channels":[],"rewards":[{"id":"R_M1","required_units":60}]}
        ]}
        """;

    public static string ChannelJson(string slug, string id, bool live, string categorySlug = "rust", int viewers = 100)
    {
        object? livestream = live
            ? new { is_live = true, viewer_count = viewers, session_title = "t", categories = new[] { new { id = 13, slug = categorySlug } } }
            : null;
        return JsonSerializer.Serialize(new
        {
            id = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture),
            slug,
            playback_url = live ? $"https://playback.example.invalid/{slug}.m3u8?token=FAKE" : null,
            livestream,
            user = new { username = slug },
        });
    }

    public static string Summary(string campaignId, double units, params (string Id, bool Claimed, double Progress)[] rewards) =>
        JsonSerializer.Serialize(new
        {
            data = new { id = campaignId, progress_units = units, rewards = rewards.Select(r => new { id = r.Id, claimed = r.Claimed, progress = r.Progress }) },
        });

    public static string ProgressList(params (string Campaign, (string Id, bool Claimed, double Progress)[] Rewards)[] campaigns) =>
        JsonSerializer.Serialize(new
        {
            data = campaigns.Select(c => new { id = c.Campaign, rewards = c.Rewards.Select(r => new { id = r.Id, claimed = r.Claimed, progress = r.Progress }) }),
        });

    private HttpResponseMessage Respond(RecordedRequest request)
    {
        if (Override?.Invoke(request) is { } custom)
        {
            return custom;
        }

        var path = request.Uri.AbsolutePath;
        if (request.Uri.Host == "web.kick.com" && path == "/api/v1/drops/campaigns")
        {
            return Json(HttpStatusCode.OK, CampaignsJson);
        }

        if (request.Uri.Host == "kick.com" && path.StartsWith("/api/v2/channels/", StringComparison.Ordinal))
        {
            var slug = path["/api/v2/channels/".Length..];
            return Channels.TryGetValue(slug, out var body) ? Json(HttpStatusCode.OK, body) : Json(HttpStatusCode.NotFound, "{}");
        }

        if (path.StartsWith("/api/v1/drops/campaigns/", StringComparison.Ordinal) && path.EndsWith("/livestreams", StringComparison.Ordinal))
        {
            var id = path["/api/v1/drops/campaigns/".Length..^"/livestreams".Length];
            return Json(HttpStatusCode.OK, CampaignStreams.GetValueOrDefault(id, """{"data":[],"message":"Success"}"""));
        }

        if (request.Uri.Host == "mobile.kick.com")
        {
            return Json(HttpStatusCode.OK, CategoryStreams);
        }

        if (path == "/api/v1/drops/progress/summary")
        {
            var id = System.Web.HttpUtility.ParseQueryString(request.Uri.Query)["campaign_id"]!;
            return Summaries.TryGetValue(id, out var body) ? Json(HttpStatusCode.OK, body) : Json(HttpStatusCode.NotFound, "{}");
        }

        if (path == "/api/v1/drops/progress")
        {
            return Json(HttpStatusCode.OK, Progress);
        }

        if (path == "/api/v1/drops/claim")
        {
            return Json(HttpStatusCode.OK, """{"message":"Success"}""");
        }

        // IVS benzeri oynatma: ana liste → tek varyant → artan medya listesi → parçalar.
        if (request.Uri.Host == "playback.example.invalid" && path.EndsWith(".m3u8", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=230000,RESOLUTION=284x160,NAME=\"160p\"\nhttps://media.example.invalid/v1/160p/playlist.m3u8\n", Encoding.UTF8, "application/vnd.apple.mpegurl"),
            };
        }

        if (request.Uri.Host == "media.example.invalid")
        {
            if (path.EndsWith("/playlist.m3u8", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref _mediaSequence);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{n}\n#EXTINF:2.0,\nseg{n}.ts\n", Encoding.UTF8, "application/vnd.apple.mpegurl"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024]) };
        }

        return Json(HttpStatusCode.NotFound, "{}");
    }
}

public class KickPlatformTests
{
    private const string Token = "kick-token-value-1234567890";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static (KickDropPlatform Platform, FakeKickServer Server) Create(Action<FakeKickServer>? setup = null, KickAutoClaimer? claimer = null)
    {
        var server = new FakeKickServer();
        server.Channels["rustfan_one"] = FakeKickServer.ChannelJson("rustfan_one", "100001", live: true, viewers: 321);
        server.Channels["rustfan_four"] = FakeKickServer.ChannelJson("rustfan_four", "100004", live: false);
        setup?.Invoke(server);
        var http = new HttpClient(server.Handler);
        var fake = new FakeTimeProvider(Now);
        var api = new KickApiClient(http, () => Token, FakeKickServer.OpenLimiter(), fake);
        return (new KickDropPlatform(new KickPublicClient(http), api, http, claimer, timeProvider: fake), server);
    }

    private static LiveChannel Channel(string id, string login) =>
        new(Platform.Kick, id, login, login, 100, null, new Uri("https://kick.com/" + login), null);

    [Fact]
    public async Task Discover_KeepsOnlyRunningRustCampaigns_AndLiveStreamerChannels()
    {
        var (platform, _) = Create();

        var snapshot = await platform.DiscoverAsync("rust", CancellationToken.None);

        Assert.Equal([FakeKickServer.RustStreamerCampaign, FakeKickServer.RustGeneralCampaign], snapshot.Campaigns.Select(c => c.Id));
        var streamerChannels = snapshot.ChannelsByCampaign[FakeKickServer.RustStreamerCampaign];
        var channel = Assert.Single(streamerChannels);
        Assert.Equal("rustfan_one", channel.Login);
        Assert.Equal("100001", channel.Id);
        Assert.Equal(321, channel.Viewers);
        Assert.Equal(Platform.Kick, platform.Platform);
        Assert.Equal(new HashSet<string> { FakeKickServer.RustStreamerCampaign, FakeKickServer.RustGeneralCampaign }, platform.KnownCampaignIds.ToHashSet());
    }

    [Fact]
    public async Task Discover_StreamerLiveInAnotherCategory_IsSkipped()
    {
        var (platform, _) = Create(s => s.Channels["rustfan_one"] = FakeKickServer.ChannelJson("rustfan_one", "100001", live: true, categorySlug: "just-chatting"));

        var snapshot = await platform.DiscoverAsync("rust", CancellationToken.None);

        Assert.Empty(snapshot.ChannelsByCampaign[FakeKickServer.RustStreamerCampaign]);
    }

    [Fact]
    public async Task Discover_GeneralCampaign_UsesCampaignEligibleStreams()
    {
        var eligible = """{"data":[{"channel":{"id":"777","slug":"eligible_a","username":"Eligible_A"},"viewer_count":50},{"channel":{"id":"778","slug":"eligible_b","username":"Eligible_B"},"viewer_count":90}],"message":"Success"}""";
        var (platform, server) = Create(s => s.CampaignStreams[FakeKickServer.RustGeneralCampaign] = eligible);

        var snapshot = await platform.DiscoverAsync("rust", CancellationToken.None);

        Assert.Equal(["eligible_b", "eligible_a"], snapshot.ChannelsByCampaign[FakeKickServer.RustGeneralCampaign].Select(c => c.Login));
        Assert.DoesNotContain(server.Handler.Requests, r => r.Uri.Host == "mobile.kick.com");
        Assert.Contains(server.Handler.Requests, r => r.Uri.Query.Contains("lang_code=en", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discover_GeneralCampaign_FallsBackToRustCategoryStreams_WhenNoEligibleList()
    {
        var (platform, _) = Create();

        var snapshot = await platform.DiscoverAsync("rust", CancellationToken.None);

        Assert.Equal(["rustfan_one", "rustfan_two", "rustfan_three"], snapshot.ChannelsByCampaign[FakeKickServer.RustGeneralCampaign].Select(c => c.Login));
    }

    [Fact]
    public async Task Discover_OneChannelFailing_DoesNotKillDiscovery()
    {
        var (platform, server) = Create();
        server.Override = r => r.Uri.AbsolutePath.EndsWith("/rustfan_one", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Forbidden, "<html>blocked</html>")
            : null;

        var snapshot = await platform.DiscoverAsync("rust", CancellationToken.None);

        Assert.Empty(snapshot.ChannelsByCampaign[FakeKickServer.RustStreamerCampaign]);
        Assert.Equal(2, snapshot.Campaigns.Count);
    }

    [Fact]
    public async Task Discover_CampaignListFailure_Propagates()
    {
        var (platform, server) = Create();
        server.Override = r => r.Uri.AbsolutePath == "/api/v1/drops/campaigns" ? Json(HttpStatusCode.Forbidden, "<html>blocked</html>") : null;

        await Assert.ThrowsAsync<PlatformHttpException>(() => platform.DiscoverAsync("rust", CancellationToken.None));
    }

    [Fact]
    public async Task Session_ReportsFirstUnreachedReward_FromServerMinutes()
    {
        var (platform, _) = Create(s => s.Summaries[FakeKickServer.RustGeneralCampaign] =
            FakeKickServer.Summary(FakeKickServer.RustGeneralCampaign, 130.7, ("R_G1", true, 1), ("R_G2", false, 0.54), ("R_G3", false, 0)));
        await platform.DiscoverAsync("rust", CancellationToken.None);

        var session = await platform.GetSessionAsync(Channel("100002", "rustfan_two"), CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal("R_G2", session.DropId);
        Assert.Equal(130, session.CurrentMinutes);
        Assert.Equal(240, session.RequiredMinutes);
        Assert.False(session.IsComplete);
    }

    [Fact]
    public async Task Session_StreamerCampaignChannel_AsksThatCampaignSummary()
    {
        var (platform, server) = Create(s => s.Summaries[FakeKickServer.RustStreamerCampaign] =
            FakeKickServer.Summary(FakeKickServer.RustStreamerCampaign, 9, ("R_S1", false, 0.0375)));
        await platform.DiscoverAsync("rust", CancellationToken.None);

        var session = await platform.GetSessionAsync(Channel("100001", "rustfan_one"), CancellationToken.None);

        Assert.Equal(new DropSession("R_S1", 9, 240), session);
        Assert.Contains(server.Handler.Requests, r => r.Uri.Query.Contains("campaign_id=" + FakeKickServer.RustStreamerCampaign, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_NoProgressYet_OrUnknownChannel_IsNull()
    {
        var (platform, _) = Create();
        await platform.DiscoverAsync("rust", CancellationToken.None);

        Assert.Null(await platform.GetSessionAsync(Channel("100001", "rustfan_one"), CancellationToken.None)); // özet 404 → ilerleme yok
        Assert.Null(await platform.GetSessionAsync(Channel("999", "nobody"), CancellationToken.None)); // hiçbir kampanyada değil
    }

    [Fact]
    public async Task Session_BeforeAnyDiscovery_IsNull()
    {
        var (platform, _) = Create();

        Assert.Null(await platform.GetSessionAsync(Channel("100001", "rustfan_one"), CancellationToken.None));
    }

    [Fact]
    public async Task Session_AllThresholdsReached_WithoutAutoClaim_IsNull_CampaignDone()
    {
        var (platform, _) = Create(s => s.Summaries[FakeKickServer.RustStreamerCampaign] =
            FakeKickServer.Summary(FakeKickServer.RustStreamerCampaign, 250, ("R_S1", false, 1)));
        await platform.DiscoverAsync("rust", CancellationToken.None);

        Assert.Null(await platform.GetSessionAsync(Channel("100001", "rustfan_one"), CancellationToken.None));
    }

    [Fact]
    public async Task Session_AllThresholdsReached_WithAutoClaim_IsCompletePendingClaim()
    {
        using var dir = new TempDir();
        var server = new FakeKickServer();
        server.Channels["rustfan_one"] = FakeKickServer.ChannelJson("rustfan_one", "100001", live: true);
        server.Summaries[FakeKickServer.RustStreamerCampaign] = FakeKickServer.Summary(FakeKickServer.RustStreamerCampaign, 250, ("R_S1", false, 1));
        var http = new HttpClient(server.Handler);
        var api = new KickApiClient(http, () => Token);
        var claimer = new KickAutoClaimer(api, new JsonLinesLog<ClaimRecord>(dir.File("h.jsonl")), () => new HashSet<string>());
        var platform = new KickDropPlatform(new KickPublicClient(http), api, http, claimer);
        await platform.DiscoverAsync("rust", CancellationToken.None);

        var session = await platform.GetSessionAsync(Channel("100001", "rustfan_one"), CancellationToken.None);

        Assert.NotNull(session);
        Assert.True(session.IsComplete);
        Assert.Equal("R_S1", session.DropId);
    }

    [Fact]
    public async Task Session_AuthRejected_Throws()
    {
        var (platform, server) = Create();
        await platform.DiscoverAsync("rust", CancellationToken.None);
        server.Override = r => r.Uri.AbsolutePath.EndsWith("/summary", StringComparison.Ordinal) ? Json(HttpStatusCode.Forbidden, KickAuthClientTests.ForbiddenJson) : null;

        await Assert.ThrowsAsync<PlatformAuthException>(() => platform.GetSessionAsync(Channel("100001", "rustfan_one"), CancellationToken.None));
    }

    [Fact]
    public async Task CreateWatcher_ReturnsKickHlsWatcher()
    {
        var (platform, _) = Create();

        Assert.IsType<KickHlsWatcher>(platform.CreateWatcher());
        await Task.CompletedTask;
    }
}

public class KickAutoClaimerTests
{
    private const string Token = "kick-token-value-1234567890";

    private static (KickAutoClaimer Claimer, FakeKickServer Server, TempDir Dir, List<ClaimRecord> Done, FakeTimeProvider Time) Create(params string[] allowed)
    {
        var dir = new TempDir();
        var server = new FakeKickServer();
        var fake = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var api = new KickApiClient(new HttpClient(server.Handler), () => Token, FakeKickServer.OpenLimiter(), fake);
        var claimer = new KickAutoClaimer(api, new JsonLinesLog<ClaimRecord>(dir.File("history.jsonl")), () => allowed.ToHashSet(), fake);
        var done = new List<ClaimRecord>();
        claimer.Completed += done.Add;
        return (claimer, server, dir, done, fake);
    }

    [Fact]
    public async Task ClaimsOnlyCompleteUnclaimedRewards_OfAllowedCampaigns()
    {
        var (claimer, server, dir, done, _) = Create("RUST1");
        using var _ = dir;
        server.Progress = FakeKickServer.ProgressList(
            ("RUST1", [("A", true, 1), ("B", false, 1), ("C", false, 0.4)]),
            ("MINECRAFT", [("M", false, 1)]));

        await claimer.CheckOnceAsync(CancellationToken.None);

        var claim = Assert.Single(server.Claims);
        Assert.Equal("""{"reward_id":"B","campaign_id":"RUST1"}""", claim.Body);
        var record = Assert.Single(done);
        Assert.Equal("Claimed", record.Outcome);
        Assert.Equal("B", record.DropId);
        Assert.Equal("Kick", record.Platform);
        Assert.Single(new JsonLinesLog<ClaimRecord>(dir.File("history.jsonl")).ReadAll());
    }

    [Fact]
    public async Task AlreadyClaimedByUs_IsNotClaimedTwice_EvenIfServerListIsStale()
    {
        var (claimer, server, dir, _, _) = Create("RUST1");
        using var _ = dir;
        server.Progress = FakeKickServer.ProgressList(("RUST1", [("B", false, 1)]));

        await claimer.CheckOnceAsync(CancellationToken.None);
        await claimer.CheckOnceAsync(CancellationToken.None);

        Assert.Single(server.Claims);
    }

    [Fact]
    public async Task NoAllowedCampaigns_MeansNoNetworkAtAll()
    {
        var (claimer, server, dir, _, _) = Create();
        using var _ = dir;

        await claimer.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(server.Handler.Requests);
    }

    [Fact]
    public async Task RejectedClaim_IsRetriedOnlyAfterInterval_AndOnlyUpToMaxAttempts()
    {
        var (claimer, server, dir, done, fake) = Create("RUST1");
        using var _ = dir;
        server.Progress = FakeKickServer.ProgressList(("RUST1", [("B", false, 1)]));
        server.Override = r => r.Uri.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal) ? Json(HttpStatusCode.BadRequest, """{"message":"nope"}""") : null;

        await claimer.CheckOnceAsync(CancellationToken.None);
        await claimer.CheckOnceAsync(CancellationToken.None); // aralık dolmadı → yeni deneme yok
        Assert.Single(server.Claims);

        for (var i = 0; i < 4; i++)
        {
            fake.Advance(TimeSpan.FromMinutes(11));
            await claimer.CheckOnceAsync(CancellationToken.None);
        }

        Assert.Equal(3, server.Claims.Count()); // en çok 3 deneme
        Assert.All(done, r => Assert.Equal("Rejected", r.Outcome));
    }

    [Fact]
    public async Task ServerAndAuthErrors_AreRecordedAsFailed_NotThrown()
    {
        var (claimer, server, dir, done, _) = Create("RUST1");
        using var _ = dir;
        server.Progress = FakeKickServer.ProgressList(("RUST1", [("B", false, 1)]));
        server.Override = r => r.Uri.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal) ? Json(HttpStatusCode.InternalServerError, "{}") : null;

        await claimer.CheckOnceAsync(CancellationToken.None);

        Assert.Equal("Failed", Assert.Single(done).Outcome);
    }

    [Fact]
    public async Task ProgressFailure_IsSwallowed()
    {
        var (claimer, server, dir, done, _) = Create("RUST1");
        using var _ = dir;
        server.Override = r => r.Uri.AbsolutePath == "/api/v1/drops/progress" ? Json(HttpStatusCode.Forbidden, KickAuthClientTests.ForbiddenJson) : null;

        await claimer.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(done);
        Assert.Empty(server.Claims);
    }

    [Fact]
    public async Task Platform_RaisesDropClaimed_ForClaimerResults()
    {
        using var dir = new TempDir();
        var server = new FakeKickServer();
        server.Progress = FakeKickServer.ProgressList(("C_STREAMER", [("R_S1", false, 1)]));
        var http = new HttpClient(server.Handler);
        var api = new KickApiClient(http, () => Token);
        KickDropPlatform? platform = null;
        var claimer = new KickAutoClaimer(api, new JsonLinesLog<ClaimRecord>(dir.File("h.jsonl")), () => platform!.KnownCampaignIds);
        platform = new KickDropPlatform(new KickPublicClient(http), api, http, claimer);
        var notices = new List<ClaimNotice>();
        platform.DropClaimed += notices.Add;
        server.Channels["rustfan_one"] = FakeKickServer.ChannelJson("rustfan_one", "100001", live: true);
        await platform.DiscoverAsync("rust", CancellationToken.None);

        await claimer.CheckOnceAsync(CancellationToken.None);

        var notice = Assert.Single(notices);
        Assert.Equal(new ClaimNotice("R_S1", true, "Success"), notice);
    }
}

public class KickHlsWatcherTests
{
    private const string Master = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=8000000,RESOLUTION=1920x1080,NAME="1080p60"
        https://media.example.invalid/v1/1080p60/playlist.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=230000,RESOLUTION=284x160,NAME="160p"
        https://media.example.invalid/v1/160p/playlist.m3u8
        """;

    private static LiveChannel Channel() =>
        new(Platform.Kick, "100001", "rustfan_one", "RustFan_One", 100, null, new Uri("https://kick.com/rustfan_one"), null);

    private static StubHandler Server(Func<RecordedRequest, HttpResponseMessage?> custom, Func<int>? mediaCalls = null)
    {
        var sequence = 0;
        return new StubHandler(r =>
        {
            if (custom(r) is { } response)
            {
                return response;
            }

            if (r.Uri.Host == "kick.com")
            {
                return Json(HttpStatusCode.OK, FakeKickServer.ChannelJson("rustfan_one", "100001", live: true));
            }

            if (r.Uri.AbsolutePath.EndsWith("/rustfan_one.m3u8", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Master, Encoding.UTF8, "application/vnd.apple.mpegurl") };
            }

            if (r.Uri.AbsolutePath.EndsWith("/playlist.m3u8", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref sequence);
                var media = $"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{n}\n#EXTINF:2.0,\nseg{n}.ts\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(media, Encoding.UTF8, "application/vnd.apple.mpegurl") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) };
        });
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider fake, Func<bool> condition)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            fake.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task PicksLowestVideoVariant_AndDownloadsFreshSegments()
    {
        var fake = new FakeTimeProvider();
        var handler = Server(_ => null);
        var http = new HttpClient(handler);
        var watcher = new KickHlsWatcher(new KickApiClient(http, () => null), http, timeProvider: fake);
        using var cts = new CancellationTokenSource();

        var run = watcher.RunAsync(Channel(), cts.Token);
        await AdvanceUntilAsync(fake, () => watcher.Stats.SegmentsFetched >= 3);
        await cts.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WatchEnd.Cancelled, result.End);
        Assert.True(watcher.Stats.SegmentsFetched >= 3);
        Assert.Equal("160p", watcher.Stats.Variant);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("1080p60", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("seg1.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfflineChannel_EndsAsOffline_WithoutTouchingPlayback()
    {
        var handler = Server(r => r.Uri.Host == "kick.com" ? Json(HttpStatusCode.OK, FakeKickServer.ChannelJson("rustfan_one", "100001", live: false)) : null);
        var http = new HttpClient(handler);
        var watcher = new KickHlsWatcher(new KickApiClient(http, () => null), http);

        var result = await watcher.RunAsync(Channel(), CancellationToken.None);

        Assert.Equal(WatchEnd.Offline, result.End);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host.Contains("playback", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MasterNotFound_IsOffline_AndMasterError_IsFailed_WithoutLeakingToken()
    {
        var notFound = new HttpClient(Server(r => r.Uri.AbsolutePath.EndsWith("/rustfan_one.m3u8", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NotFound) : null));
        Assert.Equal(WatchEnd.Offline, (await new KickHlsWatcher(new KickApiClient(notFound, () => null), notFound).RunAsync(Channel(), CancellationToken.None)).End);

        var forbidden = new HttpClient(Server(r => r.Uri.AbsolutePath.EndsWith("/rustfan_one.m3u8", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null));
        var failed = await new KickHlsWatcher(new KickApiClient(forbidden, () => null), forbidden).RunAsync(Channel(), CancellationToken.None);

        Assert.Equal(WatchEnd.Failed, failed.End);
        Assert.DoesNotContain("token=", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChannelInfoFailure_IsFailedNotThrown()
    {
        var http = new HttpClient(Server(r => r.Uri.Host == "kick.com" ? Json(HttpStatusCode.Forbidden, "<html>blocked</html>") : null));
        var watcher = new KickHlsWatcher(new KickApiClient(http, () => null), http);

        var result = await watcher.RunAsync(Channel(), CancellationToken.None);

        Assert.Equal(WatchEnd.Failed, result.End);
    }
    /// <summary>Gerçek IVS ana/medya listesi ve parçaları (anonim, hesap yok, ~15 sn): ayrıştırma + parça indirme mekaniği doğrulanır.</summary>
    [LiveFact]
    public async Task Live_AnonymousPlayback_FetchesRealSegments()
    {
        using var http = KickConstants.CreateHttpClient();
        var api = new KickApiClient(http, () => null);
        var streams = await api.GetLiveStreamsAsync(limit: 1);
        var channel = Assert.Single(streams.Items);
        var watcher = new KickHlsWatcher(api, http);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await watcher.RunAsync(channel, cts.Token);

        Assert.Equal(WatchEnd.Cancelled, result.End);
        Assert.NotNull(watcher.Stats.Variant);
        Assert.True(watcher.Stats.SegmentsFetched >= 1, "En az bir parça inmeliydi.");
        Console.WriteLine($"[kick-live] varyant={watcher.Stats.Variant} parça={watcher.Stats.SegmentsFetched} bayt={watcher.Stats.BytesFetched}");
    }
}
