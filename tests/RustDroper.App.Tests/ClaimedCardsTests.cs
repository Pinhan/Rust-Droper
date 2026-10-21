using Microsoft.Extensions.Time.Testing;
using RustDroper.App.ViewModels;
using RustDroper.Core.Domain;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.Tests;

/// <summary>"Claim edilen ödüller" görsel kartları: son 5, yeni → eski, "… ve N ödül daha", kaynak satırı, yalnız gösterilenlerin görseli, platform yalıtımı.</summary>
public class ClaimedCardsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T18:00:00Z");
    private static readonly Uri RustLink = new("https://twitch.facepunch.com/connect");
    private static readonly Uri KickRustLink = new("https://kick.facepunch.com/connect");

    private static ClaimedReward Reward(string id, int daysAgo, Platform platform = Platform.Twitch, string? game = null, Uri? link = null, string? image = "https://static-cdn.jtvnw.net/twitch-quests-assets/REWARD/{0}.jpeg", int count = 1, bool dated = true) => new(
        platform, id, "Sahte " + id, dated ? Now.AddDays(-daysAgo) : null, count, true, link,
        image is null ? null : new Uri(string.Format(System.Globalization.CultureInfo.InvariantCulture, image, id)), game);

    private static DashboardViewModel Dashboard(FakeMedia? media = null) => new(new RecordingBrowser(), new FakeTimeProvider(Now), TimeZoneInfo.Utc, media);

    private static DashboardInput Input(IEnumerable<ClaimedReward> claimed, Platform platform = Platform.Twitch) =>
        new(Dx.Engine(), false, null, new TwitchInventorySnapshot([.. claimed], []), new HashSet<string>(), Now, platform);

    [Fact]
    public void ShowsLastFive_NewestFirst_AndSummarizesTheRest()
    {
        var dashboard = Dashboard();
        // karışık sırada ver: sıralamayı VM yapmalı
        var rewards = new[] { 7, 2, 9, 1, 5, 3, 8, 4, 6 }.Select(d => Reward("r" + d, d));

        dashboard.Update(Input(rewards));

        Assert.Equal(["Sahte r1", "Sahte r2", "Sahte r3", "Sahte r4", "Sahte r5"], dashboard.ClaimedRewards.Select(c => c.Name));
        Assert.Equal("… ve 4 ödül daha", dashboard.ClaimedMoreText);
        Assert.True(dashboard.HasClaimed);
    }

    [Fact]
    public void FiveOrFewer_NoMoreText()
    {
        var dashboard = Dashboard();

        dashboard.Update(Input([Reward("a", 1), Reward("b", 2)]));
        Assert.Equal(2, dashboard.ClaimedRewards.Count);
        Assert.Equal(string.Empty, dashboard.ClaimedMoreText);

        dashboard.Update(Input([]));
        Assert.False(dashboard.HasClaimed);
    }

    [Fact]
    public void DateAndCountLine_UsesTurkishDate_AndOmitsUnknownDate()
    {
        var dashboard = Dashboard();

        dashboard.Update(Input([Reward("a", 1, count: 3), Reward("b", 2), Reward("c", 3, dated: false)]));

        var cards = dashboard.ClaimedRewards.ToDictionary(c => c.Id);
        Assert.Equal("×3 · 22 Eki 2026", cards["a"].DetailText);
        Assert.Equal("21 Eki 2026", cards["b"].DetailText);
        Assert.Equal(string.Empty, cards["c"].DetailText); // Kick gibi tarihsiz: satır boş
    }

    [Theory]
    [InlineData("Apex Legends", null, Platform.Twitch, "Apex Legends")]
    [InlineData(null, "https://twitch.facepunch.com/connect", Platform.Twitch, "Rust · Facepunch")]
    [InlineData(null, "https://kick.facepunch.com/connect", Platform.Kick, "Rust · Facepunch")]
    [InlineData(null, null, Platform.Twitch, "Twitch")]
    [InlineData(null, "https://example.org/connect", Platform.Twitch, "Twitch")]
    [InlineData(null, null, Platform.Kick, "Kick")]
    [InlineData("  ", null, Platform.Kick, "Kick")]
    public void SourceLine_GameNameThenFacepunchThenPlatform(string? game, string? link, Platform platform, string expected)
    {
        var reward = Reward("a", 1, platform, game, link is null ? null : new Uri(link));

        Assert.Equal(expected, DashboardViewModel.ClaimSource(reward, platform));
    }

    [Fact]
    public void OnlyTheFiveShownCards_RequestTheirImages_AndFailureLeavesPlaceholder()
    {
        var media = new FakeMedia();
        var dashboard = Dashboard(media);
        var rewards = Enumerable.Range(1, 8).Select(i => Reward("r" + i, i));

        dashboard.Update(Input(rewards));

        SpinWait.SpinUntil(() => { lock (media.Requested) { return media.Requested.Count >= 5; } }, 5000);
        Thread.Sleep(50);
        lock (media.Requested)
        {
            Assert.Equal(5, media.Requested.Count);
            Assert.DoesNotContain(media.Requested, u => u.EndsWith("r6.jpeg", StringComparison.Ordinal) || u.EndsWith("r8.jpeg", StringComparison.Ordinal));
        }

        Assert.All(dashboard.ClaimedRewards, c => Assert.False(c.HasImage)); // indirme başarısız → yer tutucu
    }

    [Fact]
    public void CachedImages_AreShown_AndCardsAreUpdatedInPlace()
    {
        var media = new FakeMedia();
        media.Cached["https://static-cdn.jtvnw.net/twitch-quests-assets/REWARD/a.jpeg"] = "C:/media/a.jpeg";
        var dashboard = Dashboard(media);
        dashboard.Update(Input([Reward("a", 1), Reward("b", 2)]));
        var card = dashboard.ClaimedRewards.First(c => c.Id == "a");
        Assert.True(card.HasImage);
        var changed = new List<string>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        dashboard.Update(Input([Reward("a", 1, count: 2), Reward("b", 2)]));

        Assert.Same(card, dashboard.ClaimedRewards.First(c => c.Id == "a"));
        Assert.Contains(nameof(ClaimedItem.DetailText), changed);
        Assert.DoesNotContain(nameof(ClaimedItem.ImagePath), changed);
    }

    [Fact]
    public void Platforms_AreIsolated_EachScreenShowsOnlyItsOwnInventory()
    {
        var dashboard = Dashboard();
        var twitch = new[] { Reward("t1", 1, link: RustLink) };
        var kick = new[] { Reward("k1", 1, Platform.Kick, link: KickRustLink, image: "https://ext.cdn.kick.com/drops/reward-image/{0}.png", dated: false) };

        dashboard.Update(Input(twitch));
        var twitchText = string.Join(' ', dashboard.ClaimedRewards.Select(c => c.Name + c.SourceText + c.DetailText));
        dashboard.Update(Input(kick, Platform.Kick));
        var kickText = string.Join(' ', dashboard.ClaimedRewards.Select(c => c.Name + c.SourceText + c.DetailText));

        Assert.DoesNotContain("k1", twitchText, StringComparison.Ordinal);
        Assert.DoesNotContain("Kick", twitchText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("t1", kickText, StringComparison.Ordinal);
        Assert.DoesNotContain("Twitch", kickText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Sahte k1", dashboard.ClaimedRewards.Single().Name);
    }
}
