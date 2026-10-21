using System.Text.Json;
using RustDroper.Core.Domain;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Tests;

public class TwitchInventoryParserTests
{
    private static TwitchInventorySnapshot ParseFixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "twitch-inventory-claimed.json")));
        return TwitchInventoryParser.Parse(doc.RootElement);
    }

    private static TwitchInventorySnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TwitchInventoryParser.Parse(doc.RootElement);
    }

    [Fact]
    public void RealInventory_ParsesClaimedRewards_NewestFirst()
    {
        var snapshot = ParseFixture();

        Assert.Equal(8, snapshot.Claimed.Count);
        Assert.Equal(0, snapshot.CampaignsInProgress);
        Assert.All(snapshot.Claimed, r => Assert.Equal(Platform.Twitch, r.Platform));
        var dates = snapshot.Claimed.Select(r => r.LastAwardedAt).ToList();
        Assert.Equal(dates.OrderByDescending(d => d).ToList(), dates);
    }

    [Fact]
    public void RealInventory_ExposesFacepunchLinkStatus()
    {
        var snapshot = ParseFixture();

        Assert.True(snapshot.IsLinkedTo("twitch.facepunch.com"));
        Assert.Null(snapshot.IsLinkedTo("example.org"));
        var rust = snapshot.Claimed.First(r => r.Name.StartsWith("Rust", StringComparison.Ordinal));
        Assert.Equal(new Uri("https://twitch.facepunch.com/connect"), rust.AccountLinkUrl);
        Assert.NotNull(rust.ImageUrl);
    }

    [Fact]
    public void ClaimedReward_GameName_ComesFromGameDisplayName_OrIsNullForRust()
    {
        var snapshot = Parse("""
            {"currentUser":{"inventory":{"gameEventDrops":[
              {"id":"a","name":"Rug","lastAwardedAt":"2026-10-20T10:00:00Z","game":null,"imageURL":"https://static-cdn.jtvnw.net/twitch-quests-assets/REWARD/a.jpeg"},
              {"id":"b","name":"Emote","lastAwardedAt":"2026-10-19T10:00:00Z","game":{"displayName":"Apex Legends"}},
              {"id":"c","name":"Badge","lastAwardedAt":"2026-10-18T10:00:00Z"}
            ]}}}
            """);

        var byId = snapshot.Claimed.ToDictionary(r => r.Id);
        Assert.Null(byId["a"].GameName);
        Assert.Equal("Apex Legends", byId["b"].GameName);
        Assert.Null(byId["c"].GameName);
        Assert.Equal("static-cdn.jtvnw.net", byId["a"].ImageUrl!.Host);
    }

    [Fact]
    public void NotLinkedAnywhere_ReportsFalse()
    {
        var snapshot = Parse("""
            {"currentUser":{"inventory":{"gameEventDrops":[
              {"id":"a","name":"X","isConnected":false,"requiredAccountLink":"https://twitch.facepunch.com/connect","totalCount":1}
            ]}}}
            """);

        Assert.False(snapshot.IsLinkedTo("twitch.facepunch.com"));
    }

    [Fact]
    public void NullCurrentUser_MeansUnauthorized()
    {
        Assert.Throws<TwitchUnauthorizedException>(() => Parse("""{"currentUser":null}"""));
    }

    [Fact]
    public void MissingInventory_ThrowsDataException()
    {
        Assert.Throws<PlatformDataException>(() => Parse("""{"currentUser":{"id":"1"}}"""));
    }

    [Fact]
    public void MalformedRewardsAreSkipped_AndNullInProgressIsZero()
    {
        var snapshot = Parse("""
            {"currentUser":{"inventory":{"dropCampaignsInProgress":null,"gameEventDrops":[{"name":"no id"},{"id":"ok"}]}}}
            """);

        Assert.Equal("ok", Assert.Single(snapshot.Claimed).Id);
        Assert.Equal(1, snapshot.Claimed[0].Count);
        Assert.Equal(0, snapshot.CampaignsInProgress);
    }
}
