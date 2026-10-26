using System.Net;
using System.Text.Json;
using RustDroper.Core.Domain;
using RustDroper.Core.Platforms.Twitch;
using static RustDroper.Core.Tests.StubHandler;

namespace RustDroper.Core.Tests;

internal static class TwitchFixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static JsonDocument Doc(string name) => JsonDocument.Parse(Read(name));
}

public class TwitchDirectoryParserTests
{
    [Fact]
    public void RealDirectory_ParsesLiveChannels()
    {
        using var doc = TwitchFixtures.Doc("twitch-directory-drops.json");

        var channels = TwitchDirectoryParser.Parse(doc.RootElement);

        Assert.Equal(["jay3", "lilshark4r", "bogur"], channels.Select(c => c.Login));
        var first = channels[0];
        Assert.Equal("133220545", first.Id);
        Assert.Equal("Jay3", first.DisplayName);
        Assert.Equal(2553, first.Viewers);
        Assert.Equal(new Uri("https://www.twitch.tv/jay3"), first.Url);
        Assert.NotNull(first.AvatarUrl);
        Assert.All(channels, c => Assert.Equal(Platform.Twitch, c.Platform));
    }

    [Fact]
    public void UnknownGame_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"game":null}""");

        Assert.Empty(TwitchDirectoryParser.Parse(doc.RootElement));
    }
}

public class TwitchAvailableDropsParserTests
{
    [Fact]
    public void WatchTimeCampaign_ParsesDropsSortedWithBenefitNames()
    {
        using var doc = TwitchFixtures.Doc("twitch-available-marvel.json");

        var result = TwitchAvailableDropsParser.Parse(doc.RootElement);

        Assert.Equal("1362574163", result.ChannelId);
        var campaign = Assert.Single(result.Campaigns);
        Assert.Equal("Season 10.5 Twitch Drops", campaign.Name);
        Assert.Equal(Platform.Twitch, campaign.Platform);
        Assert.Equal("Marvel Rivals", campaign.GameName);
        Assert.Equal("marvel-rivals", campaign.GameSlug);
        Assert.Equal(DateTimeOffset.Parse("2026-10-09T12:00:00Z"), campaign.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T08:59:59.999Z"), campaign.EndsAt);
        Assert.Equal([30, 60, 120, 240], campaign.Rewards.Select(r => r.RequiredMinutes));
        Assert.Equal("Angela Spray", campaign.Rewards[0].Name);
        Assert.Equal("f73b9ab1-bb1f-11f1-a209-0a58a9feac02", campaign.Rewards[0].Id);
        Assert.NotNull(campaign.Rewards[0].ImageUrl);
    }

    [Fact]
    public void SubscriptionBasedCampaign_IsSkipped_NotTreatedAsWatchDrop()
    {
        using var doc = TwitchFixtures.Doc("twitch-available-sub-only.json");

        var result = TwitchAvailableDropsParser.Parse(doc.RootElement);

        Assert.Empty(result.Campaigns);
        Assert.Equal(["f754a865-0a42-42ae-8e40-e830c8d2bdc3"], result.SkippedCampaigns);
    }

    [Fact]
    public void FiveDropCampaign_ParsesAllMilestones()
    {
        using var doc = TwitchFixtures.Doc("twitch-available-five-drops.json");

        var campaign = Assert.Single(TwitchAvailableDropsParser.Parse(doc.RootElement).Campaigns);

        Assert.Equal("Ignite Stage2 W4", campaign.Name);
        Assert.Equal([30, 60, 120, 240, 360], campaign.Rewards.Select(r => r.RequiredMinutes));
    }

    [Fact]
    public void MixedChannel_KeepsWatchCampaigns_AndSkipsSubscriptionOnes()
    {
        var marvel = System.Text.Json.Nodes.JsonNode.Parse(TwitchFixtures.Read("twitch-available-marvel.json"))!;
        var sub = System.Text.Json.Nodes.JsonNode.Parse(TwitchFixtures.Read("twitch-available-sub-only.json"))!;
        var campaigns = marvel["channel"]!["viewerDropCampaigns"]!.AsArray();
        campaigns.Add(sub["channel"]!["viewerDropCampaigns"]![0]!.DeepClone());
        using var doc = JsonDocument.Parse(marvel.ToJsonString());

        var result = TwitchAvailableDropsParser.Parse(doc.RootElement);

        Assert.Equal("Season 10.5 Twitch Drops", Assert.Single(result.Campaigns).Name);
        Assert.Single(result.SkippedCampaigns);
    }

    [Fact]
    public void ChannelNull_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"channel":null}""");

        var result = TwitchAvailableDropsParser.Parse(doc.RootElement);

        Assert.Null(result.ChannelId);
        Assert.Empty(result.Campaigns);
    }
}

public class TwitchCampaignDiscoveryTests
{
    private static HttpResponseMessage Route(RecordedRequest request, Func<string, HttpResponseMessage?>? channelOverride = null)
    {
        using var body = JsonDocument.Parse(request.Body);
        var operation = body.RootElement.GetProperty("operationName").GetString();
        if (operation == GqlOperations.GameDirectory.Name)
        {
            return Json(HttpStatusCode.OK, Wrap(TwitchFixtures.Read("twitch-directory-drops.json")));
        }

        var channelId = body.RootElement.GetProperty("variables").GetProperty("channelID").GetString()!;
        if (channelOverride?.Invoke(channelId) is { } overridden)
        {
            return overridden;
        }

        // Gerçek gözlem: jay3 ve bogur "Ignite Stage2 W4" (genel), lilshark4r yalnızca kanala özgü "Season 10.5" sunar.
        var fixture = channelId == "1362574163" ? "twitch-available-marvel.json" : "twitch-available-five-drops.json";
        return Json(HttpStatusCode.OK, Wrap(TwitchFixtures.Read(fixture)));
    }

    private static string Wrap(string data) => "{\"data\":" + data + "}";

    private static TwitchCampaignDiscovery Discovery(StubHandler handler) =>
        new(new TwitchGqlClient(
            new HttpClient(handler),
            TwitchClients.SmartboxTv,
            () => "token-for-tests-123456",
            new GqlHashRegistry(),
            "device-id-0123456789abcdef"));

    [Fact]
    public async Task Discover_MergesCampaignsAcrossChannels_AndClassifiesScope()
    {
        var handler = new StubHandler(r => Route(r));

        var result = await Discovery(handler).DiscoverAsync("marvel-rivals", channelsToProbe: 3);

        Assert.Equal(3, result.Channels.Count);
        Assert.Equal(2, result.Campaigns.Count);

        // "Ignite" iki kanalda görüldü → genel; "Season 10.5" yalnızca lilshark4r'da → kanala özgü.
        var ignite = result.Campaigns.Single(c => c.Name == "Ignite Stage2 W4");
        Assert.Equal(CampaignScope.General, ignite.Scope);
        Assert.Equal(["jay3", "bogur"], result.ChannelsByCampaign[ignite.Id].Select(c => c.Login));

        var season = result.Campaigns.Single(c => c.Name == "Season 10.5 Twitch Drops");
        Assert.Equal(CampaignScope.Streamer, season.Scope);
        Assert.Equal("lilshark4r", Assert.Single(season.Streamers).Login);
        Assert.Equal(4, handler.Requests.Count); // 1 dizin + 3 kanal
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Discover_CampaignSeenOnOneChannel_IsStreamerScoped()
    {
        var handler = new StubHandler(r => Route(r));

        var result = await Discovery(handler).DiscoverAsync("marvel-rivals", channelsToProbe: 1);

        // Yalnızca ilk kanal (jay3) sorgulandı: kampanya tek kanalda görüldü → yayıncıya özel varsayılır.
        var campaign = Assert.Single(result.Campaigns);
        Assert.Equal(CampaignScope.Streamer, campaign.Scope);
        Assert.Equal("jay3", Assert.Single(campaign.Streamers).Login);
    }

    [Fact]
    public async Task Discover_OneChannelFailing_BecomesWarning_NotFailure()
    {
        var handler = new StubHandler(r => Route(
            r,
            id => id == "1362574163" ? Json(HttpStatusCode.OK, """{"errors":[{"message":"something odd"}]}""") : null));

        var result = await Discovery(handler).DiscoverAsync("marvel-rivals", channelsToProbe: 3);

        Assert.Single(result.Warnings);
        Assert.Contains("lilshark4r", result.Warnings[0], StringComparison.Ordinal);
        Assert.Equal("Ignite Stage2 W4", Assert.Single(result.Campaigns).Name);
    }

    private const string NoDrops = "{\"data\":{\"channel\":{\"id\":\"x\",\"viewerDropCampaigns\":[],\"__typename\":\"Channel\"}}}";

    [Fact]
    public async Task Discover_ChannelsWithoutDrops_AreSkipped_AndLaterChannelsAreProbed()
    {
        // Gerçek gözlem (2026-10-26): "Drops Enabled" dizininde ilk 30 kanaldan yalnız 1'i etkinlik kampanyasını taşıyordu.
        // jay3 ve lilshark4r kampanyasız; üçüncü kanal (bogur) kampanyayı taşır → keşif durmadan ona ulaşmalı.
        var handler = new StubHandler(r => Route(r, id => id == "133220545" || id == "1362574163" ? Json(HttpStatusCode.OK, NoDrops) : null));

        var result = await Discovery(handler).DiscoverAsync("marvel-rivals", channelsToProbe: 3);

        var campaign = Assert.Single(result.Campaigns);
        Assert.Equal("Ignite Stage2 W4", campaign.Name);
        Assert.Equal(["bogur"], result.ChannelsByCampaign[campaign.Id].Select(c => c.Login));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Discover_StopsProbing_OnceEnoughEligibleChannelsAreFound()
    {
        var handler = new StubHandler(r => Route(r));

        var result = await Discovery(handler).DiscoverAsync("marvel-rivals", channelsToProbe: 3, stopAfterEligible: 1);

        Assert.Single(result.ChannelsByCampaign.Values.SelectMany(l => l));
        Assert.Equal(2, handler.Requests.Count); // 1 dizin + 1 kanal
    }

    [Fact]
    public async Task Discover_KnownEligibleChannels_AreProbedFirst()
    {
        var handler = new StubHandler(r => Route(r, id => id == "133220545" || id == "1362574163" ? Json(HttpStatusCode.OK, NoDrops) : null));
        var directory = new StubHandler(r => Route(r));
        var all = await Discovery(directory).DiscoverAsync("marvel-rivals", channelsToProbe: 3);
        var bogurId = all.Channels.Single(c => c.Login == "bogur").Id;

        var result = await Discovery(handler).DiscoverAsync("marvel-rivals", channelsToProbe: 3, stopAfterEligible: 1, knownEligibleChannelIds: [bogurId]);

        Assert.Equal("bogur", Assert.Single(result.ChannelsByCampaign.Values.SelectMany(l => l)).Login);
        Assert.Equal(2, handler.Requests.Count); // bilinen kanal ilk sorgulandı, yeterli → durdu
    }
}
