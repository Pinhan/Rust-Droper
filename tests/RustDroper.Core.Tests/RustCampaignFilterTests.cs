using RustDroper.Core.Domain;
using RustDroper.Core.Platforms;

namespace RustDroper.Core.Tests;

public class RustCampaignFilterTests
{
    private static readonly IReadOnlySet<string> Catalog = RustCampaignFilter.KeysOf(["Rug", "Bed", "Garage Door", "Thompson", "Large Wood Box", "Backpack"]);

    private static Campaign Make(string name, string gameName = "", string gameSlug = "", string? connect = null, params string[] rewards) =>
        new(
            Platform.Twitch, name, name, gameSlug, gameName, CampaignStatus.Active, null, null, CampaignScope.Streamer, [],
            [.. rewards.Select((r, i) => new DropReward("d" + i, r, 60, null))],
            connect is null ? null : new Uri(connect));

    [Fact]
    public void ChatBadgeCampaign_WithoutGameOrFacepunchLink_IsNotRust()
    {
        var spooky = Make("SpookyDragon 2026", rewards: "DragonWitch 2026");
        Assert.False(RustCampaignFilter.IsRust(spooky, Catalog));
    }

    [Theory]
    [InlineData("Rust", "", null, "Backpackx")]
    [InlineData("", "rust", null, "Xyz")]
    [InlineData("", "", "https://twitch.facepunch.com/", "Xyz")]
    [InlineData("", "", null, "Rug")]
    [InlineData("", "", null, "large  wood-box")]
    public void AnyRustSignalIsEnough(string gameName, string gameSlug, string? connect, string reward)
    {
        Assert.True(RustCampaignFilter.IsRust(Make("C", gameName, gameSlug, connect, reward), Catalog));
    }

    [Fact]
    public void LookalikeHostIsNotFacepunch()
    {
        Assert.False(RustCampaignFilter.IsRust(Make("C", connect: "https://evilfacepunch.com/", rewards: "Xyz"), Catalog));
    }

    [Fact]
    public void WithoutCatalog_OnlyGameOrLinkSignalsCount()
    {
        var empty = RustCampaignFilter.KeysOf([]);
        Assert.False(RustCampaignFilter.IsRust(Make("C", rewards: "Rug"), empty));
        Assert.True(RustCampaignFilter.IsRust(Make("C", "Rust", rewards: "Rug"), empty));
    }
}
