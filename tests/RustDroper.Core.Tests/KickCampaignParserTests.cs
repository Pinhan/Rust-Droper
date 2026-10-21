using RustDroper.Core.Domain;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Kick;

namespace RustDroper.Core.Tests;

public class KickCampaignParserTests
{
    private const string GeneralId = "01KT4SZSEBG99SDNCD0FF6K1ZQ";
    private const string CoconutId = "01KT52V2T857FJJAQJRF6A66JQ";

    private static ParseResult<Campaign> ParseFixture() =>
        KickCampaignParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kick-campaigns.json")));

    [Fact]
    public void Fixture_ParsesWithoutWarnings()
    {
        var result = ParseFixture();

        Assert.Equal(14, result.Items.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Fixture_RustFilter_FindsGeneralAndStreamerCampaigns()
    {
        var rust = ParseFixture().Items.ForGame("RUST").ToList();

        Assert.Equal(12, rust.Count);
        Assert.Single(rust, c => c.Scope == CampaignScope.General);
        Assert.Equal(11, rust.Count(c => c.Scope == CampaignScope.Streamer));
    }

    [Fact]
    public void Fixture_GeneralCampaign_HasCumulativeMinutesSortedAscending()
    {
        var general = ParseFixture().Items.Single(c => c.Id == GeneralId);

        Assert.Equal(Platform.Kick, general.Platform);
        Assert.Equal("rust", general.GameSlug);
        Assert.Empty(general.Streamers);
        Assert.Equal([120, 240, 360], general.Rewards.Select(r => r.RequiredMinutes));
        Assert.Equal(["Kick Large Box", "Kick Garage Door", "Kick Locker"], general.Rewards.Select(r => r.Name));
    }

    [Fact]
    public void Fixture_StreamerCampaign_MapsChannelsAndRewardImages()
    {
        var team = ParseFixture().Items.Single(c => c.Id == CoconutId);

        Assert.Equal(CampaignScope.Streamer, team.Scope);
        Assert.Equal(["winnie", "coconutb"], team.Streamers.Select(s => s.Login));
        Assert.Equal("CoconutB", team.Streamers[1].DisplayName);
        Assert.Equal(new Uri("https://kick.com/coconutb"), team.Streamers[1].ProfileUrl);
        var reward = Assert.Single(team.Rewards);
        Assert.Equal(120, reward.RequiredMinutes);
        Assert.StartsWith("https://ext.cdn.kick.com/drops/reward-image/", reward.ImageUrl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(new Uri("https://kick.facepunch.com"), team.ConnectUrl);
    }

    [Fact]
    public void Fixture_CampaignWithoutCategory_HasEmptyGameAndIsExcludedFromRust()
    {
        var result = ParseFixture();

        Assert.Contains(result.Items, c => c.GameSlug.Length == 0);
        Assert.DoesNotContain(result.Items.ForGame("rust"), c => c.GameSlug.Length == 0);
    }

    [Fact]
    public void IsRunningAt_UsesDatesNotStaleStatus()
    {
        var general = ParseFixture().Items.Single(c => c.Id == GeneralId);

        Assert.False(general.IsRunningAt(DateTimeOffset.Parse("2026-06-11T19:59:59Z")));
        Assert.True(general.IsRunningAt(DateTimeOffset.Parse("2026-06-15T00:00:00Z")));
        Assert.False(general.IsRunningAt(DateTimeOffset.Parse("2026-06-21T23:00:00Z")));
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("expired", false)]
    [InlineData("upcoming", false)]
    [InlineData("whatever", false)]
    public void IsRunningAt_WithoutDates_FallsBackToStatus(string status, bool expected)
    {
        var json = $$"""{"data":[{"id":"A","status":"{{status}}"}]}""";

        var campaign = Assert.Single(KickCampaignParser.Parse(json).Items);

        Assert.Equal(expected, campaign.IsRunningAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Parse_SkipsMalformedParts_AndKeepsTheRest()
    {
        const string json = """
            {"data":[
              {"name":"no id"},
              {"id":"A","name":"ok","status":"ACTIVE","category":{"slug":"rust","name":"Rust"},
               "channels":[{"id":1},{"id":2,"slug":"abc","user":{"username":"Abc"}}],
               "rewards":[{"id":"r1","name":"x","required_units":"60"},{"id":"r2","name":"no units"},{"id":"r3","name":"zero","required_units":0}],
               "some_new_field":{"a":[1,2,3]}}
            ]}
            """;

        var result = KickCampaignParser.Parse(json);

        var campaign = Assert.Single(result.Items);
        Assert.Equal(CampaignStatus.Active, campaign.Status);
        Assert.Equal("Abc", Assert.Single(campaign.Streamers).DisplayName);
        Assert.Equal(60, Assert.Single(campaign.Rewards).RequiredMinutes);
        Assert.Equal(4, result.Warnings.Count);
    }

    [Fact]
    public void Parse_StreamerCampaignWithAllChannelsMalformed_StaysStreamerScoped()
    {
        const string json = """{"data":[{"id":"A","channels":[{"id":1},{"id":2}]}]}""";

        var campaign = Assert.Single(KickCampaignParser.Parse(json).Items);

        Assert.Equal(CampaignScope.Streamer, campaign.Scope);
        Assert.Empty(campaign.Streamers);
    }

    [Fact]
    public void Parse_EmptyData_ReturnsEmptyResult()
    {
        var result = KickCampaignParser.Parse("""{"data":[]}""");

        Assert.Empty(result.Items);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("<html>Cloudflare</html>")]
    [InlineData("""{"error":"x"}""")]
    [InlineData("""{"data":{"a":1}}""")]
    public void Parse_UnexpectedPayload_ThrowsPlatformDataException(string payload)
    {
        Assert.Throws<PlatformDataException>(() => KickCampaignParser.Parse(payload));
    }

    [LiveFact]
    public async Task Live_PublicEndpoint_ReturnsCampaigns()
    {
        using var http = KickPublicClient.CreateDefaultHttpClient();

        var result = await new KickPublicClient(http).GetCampaignsAsync();

        Assert.NotEmpty(result.Items);
    }
}
