using System.Net;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Domain;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Kick;
using static RustDroper.Core.Tests.StubHandler;
using static RustDroper.Core.Tests.TestTime;

namespace RustDroper.Core.Tests;

public class KickApiParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Livestreams_Fixture_ParsedInOrder_WithPublicUrls()
    {
        var result = KickLiveParser.ParseLivestreams(Fixture("kick-livestreams-rust.json"));

        Assert.Empty(result.Warnings);
        Assert.Equal(["rustfan_one", "rustfan_two", "rustfan_three"], result.Items.Select(c => c.Login));
        var first = result.Items[0];
        Assert.Equal(Platform.Kick, first.Platform);
        Assert.Equal("100001", first.Id);
        Assert.Equal("RustFan_One", first.DisplayName);
        Assert.Equal(329, first.Viewers);
        Assert.Equal("Rust wipe day - base building", first.Title);
        Assert.Equal(new Uri("https://kick.com/rustfan_one"), first.Url);
        Assert.NotNull(first.AvatarUrl);
    }

    [Fact]
    public void Livestreams_BadItemSkipped_WithWarning_RestSurvive()
    {
        var json = """{"data":{"livestreams":[{"channel":{"id":1}},{"viewer_count":"7","channel":{"id":2,"slug":"ok"}}]}}""";

        var result = KickLiveParser.ParseLivestreams(json);

        var channel = Assert.Single(result.Items);
        Assert.Equal("ok", channel.Login);
        Assert.Equal(7, channel.Viewers);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"livestreams":{}}}""")]
    [InlineData("not json")]
    public void Livestreams_WrongShape_ThrowsDataException(string json) =>
        Assert.Throws<PlatformDataException>(() => KickLiveParser.ParseLivestreams(json));

    [Fact]
    public void Channel_Live_ExposesPlaybackUrlAndCategory()
    {
        var info = KickLiveParser.ParseChannel(Fixture("kick-channel-live.json"));

        Assert.True(info.IsLive);
        Assert.Equal("100001", info.Id);
        Assert.Equal("rustfan_one", info.Slug);
        Assert.Equal("RustFan_One", info.DisplayName);
        Assert.Equal(329, info.Viewers);
        Assert.Equal(13, info.CategoryId);
        Assert.Equal("rust", info.CategorySlug);
        Assert.Equal("example.invalid", info.PlaybackUrl!.Host);
        Assert.EndsWith(".m3u8", info.PlaybackUrl.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Channel_Offline_IsNotLive_AndHasNoPlaybackUrl()
    {
        var info = KickLiveParser.ParseChannel(Fixture("kick-channel-offline.json"));

        Assert.False(info.IsLive);
        Assert.Null(info.PlaybackUrl);
        Assert.Equal(0, info.Viewers);
        Assert.Null(info.CategoryId);
    }

    [Theory]
    [InlineData("""{"slug":"x"}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("[]")]
    public void Channel_MissingIdentity_ThrowsDataException(string json) =>
        Assert.Throws<PlatformDataException>(() => KickLiveParser.ParseChannel(json));

    [Fact]
    public void Progress_Fixture_ParsesCampaignsAndRewards()
    {
        var result = KickProgressParser.ParseProgress(Fixture("kick-progress.json"));

        Assert.Equal(2, result.Items.Count);
        var a = result.Items[0];
        Assert.Equal("CAMPAIGN_AAAA", a.CampaignId);
        Assert.Equal(130, a.ProgressUnits);
        Assert.Equal(3, a.Rewards.Count);
        Assert.True(a.Rewards[0].Claimed);
        Assert.True(a.Rewards[0].IsComplete);
        Assert.False(a.Rewards[1].Claimed);
        Assert.False(a.Rewards[1].IsComplete);
        Assert.Equal(0.5417, a.Rewards[1].Progress, 3);
        Assert.Null(result.Items[1].ProgressUnits);
        Assert.True(a.AppConnected); // user_app_connected (gerçek hesapla görüldü)
        Assert.False(result.Items[1].AppConnected);
    }

    [Theory]
    [InlineData("""{"data":{"campaigns":[{"id":"C1","rewards":[{"id":"R1","progress":"1","claimed":"true"}]}]}}""")]
    [InlineData("""[{"campaign_id":"C1","rewards":[{"reward_id":"R1","progress":1,"claimed":true}]}]""")]
    [InlineData("""{"data":[{"id":"C1","rewards":[{"id":"R1","progress":1.0,"claimed":true}]}]}""")]
    public void Progress_EnvelopeVariants_AreTolerated(string json)
    {
        var campaign = Assert.Single(KickProgressParser.ParseProgress(json).Items);

        Assert.Equal("C1", campaign.CampaignId);
        var reward = Assert.Single(campaign.Rewards);
        Assert.True(reward.Claimed);
        Assert.True(reward.IsComplete);
    }

    [Fact]
    public void Progress_EmptyData_IsEmptyList_AndGarbageThrows()
    {
        Assert.Empty(KickProgressParser.ParseProgress("""{"data":[]}""").Items);
        Assert.Throws<PlatformDataException>(() => KickProgressParser.ParseProgress("<html>"));
    }

    [Fact]
    public void Progress_RewardWithoutId_IsSkippedWithWarning()
    {
        var result = KickProgressParser.ParseProgress("""{"data":[{"id":"C1","rewards":[{"progress":1},{"id":"R2","progress":0}]}]}""");

        Assert.Single(Assert.Single(result.Items).Rewards);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Summary_Fixture_ReturnsProgressUnits()
    {
        var summary = KickProgressParser.ParseSummary(Fixture("kick-progress-summary.json"), "CAMPAIGN_AAAA");

        Assert.NotNull(summary);
        Assert.Equal(130, summary.ProgressUnits);
        Assert.Equal(3, summary.Rewards.Count);
    }

    [Fact]
    public void Summary_ListResponse_PicksMatchingCampaign_OrNull()
    {
        var json = Fixture("kick-progress.json");

        Assert.Equal("CAMPAIGN_BBBB", KickProgressParser.ParseSummary(json, "CAMPAIGN_BBBB")!.CampaignId);
        Assert.Null(KickProgressParser.ParseSummary(json, "UNKNOWN"));
        Assert.Null(KickProgressParser.ParseSummary("""{"data":{}}""", "CAMPAIGN_AAAA"));
    }

    [Fact]
    public void Summary_IdLessSingleObject_AdoptsRequestedId()
    {
        var summary = KickProgressParser.ParseSummary("""{"data":{"progress_units":12,"rewards":[]}}""", "C9");

        Assert.Equal("C9", summary!.CampaignId);
        Assert.Equal(12, summary.ProgressUnits);
    }
}

public class KickApiClientTests
{
    private const string Token = "kick-token-value-1234567890";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static KickApiClient Client(StubHandler handler, string? token = Token, FakeTimeProvider? time = null) =>
        new(new HttpClient(handler), () => token, timeProvider: time);

    [Fact]
    public async Task GetProgress_SendsBearer_AndParses()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Fixture("kick-progress.json")));

        var result = await Client(handler).GetProgressAsync();

        Assert.Equal(2, result.Items.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(KickConstants.ProgressUri, request.Uri);
        Assert.Equal("Bearer " + Token, request.Authorization);
    }

    [Fact]
    public async Task GetSummary_PutsCampaignIdInQuery()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Fixture("kick-progress-summary.json")));

        var summary = await Client(handler).GetSummaryAsync("CAMPAIGN_AAAA");

        Assert.Equal(130, summary!.ProgressUnits);
        Assert.Equal("https://web.kick.com/api/v1/drops/progress/summary?campaign_id=CAMPAIGN_AAAA", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Fact]
    public async Task KickJsonForbidden_IsAuthError_NotRetried()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, KickAuthClientTests.ForbiddenJson));

        await Assert.ThrowsAsync<PlatformAuthException>(() => Client(handler).GetProgressAsync());

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CloudflareBlock_IsHttpError_NotAuth_NotRetried()
    {
        var handler = new StubHandler(_ => KickAuthClientTests.Html(HttpStatusCode.Forbidden, KickAuthClientTests.CloudflareHtml));

        var ex = await Assert.ThrowsAsync<PlatformHttpException>(() => Client(handler).GetProgressAsync());

        Assert.False(ex.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NoToken_ThrowsAuth_WithoutNetwork()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<PlatformAuthException>(() => Client(handler, token: null).GetProgressAsync());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TransientServerError_IsRetried_ThenSucceeds()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;
        var handler = new StubHandler(_ => ++calls < 3
            ? Json(HttpStatusCode.BadGateway, "{}")
            : Json(HttpStatusCode.OK, Fixture("kick-progress.json")));

        var result = await PumpAsync(Client(handler, time: fake).GetProgressAsync(), fake);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task GarbageBody_IsDataException()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "<html>oops</html>"));

        await Assert.ThrowsAsync<PlatformDataException>(() => Client(handler).GetProgressAsync());
    }

    [Fact]
    public async Task Claim_Success_PostsRewardAndCampaign_WithBearer()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"message":"Success"}"""));

        var result = await Client(handler).ClaimAsync("REWARD_A2", "CAMPAIGN_AAAA");

        Assert.True(result.Success);
        Assert.False(result.Rejected);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(KickConstants.ClaimUri, request.Uri);
        Assert.Equal("""{"reward_id":"REWARD_A2","campaign_id":"CAMPAIGN_AAAA"}""", request.Body);
        Assert.Equal("Bearer " + Token, request.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Claim_Rejected_IsReportedNotThrown(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Json(status, """{"message":"Reward not claimable"}"""));

        var result = await Client(handler).ClaimAsync("R", "C");

        Assert.False(result.Success);
        Assert.True(result.Rejected);
        Assert.Equal("Reward not claimable", result.Detail);
    }

    [Fact]
    public async Task Claim_AuthRejected_Throws()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, KickAuthClientTests.ForbiddenJson));

        await Assert.ThrowsAsync<PlatformAuthException>(() => Client(handler).ClaimAsync("R", "C"));
    }

    [Fact]
    public async Task Claim_ServerError_Throws_AndIsNeverRetriedAutomatically()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"));

        var ex = await Assert.ThrowsAsync<PlatformHttpException>(() => Client(handler).ClaimAsync("R", "C"));

        Assert.True(ex.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LiveStreams_UsesMobileUserAgent_RustCategory_NoAuth()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Fixture("kick-livestreams-rust.json")));

        var result = await Client(handler).GetLiveStreamsAsync();

        Assert.Equal(3, result.Items.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://mobile.kick.com/api/v1/livestreams?limit=24&category_id=13&sort=viewer_count_desc", request.Uri.ToString());
        Assert.Equal(KickConstants.MobileUserAgent, request.UserAgent);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task GetChannel_Live_ParsesPlaybackUrl_AndWorksWithoutToken()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Fixture("kick-channel-live.json")));

        var info = await Client(handler, token: null).GetChannelAsync("rustfan_one");

        Assert.True(info!.IsLive);
        Assert.NotNull(info.PlaybackUrl);
        Assert.Equal("https://kick.com/api/v2/channels/rustfan_one", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Fact]
    public async Task GetChannel_NotFound_IsNull_Cloudflare_Throws()
    {
        Assert.Null(await Client(new StubHandler(_ => Json(HttpStatusCode.NotFound, "{}"))).GetChannelAsync("nobody"));

        var blocked = new StubHandler(_ => KickAuthClientTests.Html(HttpStatusCode.Forbidden, KickAuthClientTests.CloudflareHtml));
        await Assert.ThrowsAsync<PlatformHttpException>(() => Client(blocked).GetChannelAsync("x"));
    }
    [LiveFact]
    public async Task Live_PublicEndpoints_ListRustStreams_AndResolvePlaybackUrl()
    {
        using var http = KickConstants.CreateHttpClient();
        var client = new KickApiClient(http, () => null);

        var streams = await client.GetLiveStreamsAsync(limit: 3);
        Assert.NotEmpty(streams.Items);

        var info = await client.GetChannelAsync(streams.Items[0].Login);
        Assert.NotNull(info);
        Assert.True(info.IsLive);
        Assert.NotNull(info.PlaybackUrl);
    }
}
