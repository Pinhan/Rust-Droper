using System.Net;
using RustDroper.Core.Domain;
using RustDroper.Core.Media;

namespace RustDroper.Core.Tests;

public class FacepunchPageParserTests
{
    private static readonly Uri Page = new("https://twitch.facepunch.com/");

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "facepunch-twitch-drops.html"));

    [Fact]
    public void RealPage_YieldsFiveGeneralDropsAndOneStreamerDrop()
    {
        var drops = FacepunchPageParser.Parse(Fixture(), Page);

        var general = drops.Where(d => !d.IsStreamerDrop).ToList();
        Assert.Equal(["Rug", "Bed", "Garage Door", "Thompson", "Large Wood Box"], general.Select(d => d.Name));
        Assert.Equal([60, 120, 180, 240, 300], general.Select(d => d.Minutes));
        Assert.All(general, d =>
        {
            Assert.EndsWith(".jpg", d.ImageUrl!.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith(".mp4", d.VideoUrl!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("files.facepunch.com", d.ImageUrl.Host);
            Assert.Null(d.StreamerName);
        });

        var streamer = Assert.Single(drops, d => d.IsStreamerDrop);
        Assert.Equal("Backpack", streamer.Name);
        Assert.Equal(30, streamer.Minutes);
        Assert.Equal("TwitchRivals", streamer.StreamerName);
        Assert.Equal("https://www.twitch.tv/TwitchRivals", streamer.StreamerUrl!.ToString());
        Assert.Equal("backpack.jpg", Path.GetFileName(streamer.ImageUrl!.AbsolutePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>hiç drop yok</body></html>")]
    [InlineData("<div class=\"drop-box\"><span class=\"drop-type\"></span></div>")]
    [InlineData("<a class=\"drop-box\" <<< bozuk >>> <video><source src=")]
    public void BrokenOrEmptyPages_ReturnEmptyWithoutThrowing(string html)
    {
        Assert.Empty(FacepunchPageParser.Parse(html, Page));
        Assert.Empty(FacepunchPageParser.Parse(null, Page));
    }

    [Fact]
    public void BoxWithoutMedia_StillYieldsNameAndDuration_AndRelativeUrlsResolve()
    {
        const string html = """
            <a class="drop-box"><div class="drop-box-body"><video><source src="/media/x.mp4"><img src="/media/x.jpg"></video></div>
            <div class="drop-box-footer"><span class="drop-type">Tire &amp; Rim</span><div class="drop-time"><i></i><span>1 hour 30 minutes</span></div></div></a>
            <a class="drop-box"><div class="drop-box-footer"><span class="drop-type">Çıplak</span></div></a>
            """;

        var drops = FacepunchPageParser.Parse(html, Page);

        Assert.Equal(2, drops.Count);
        Assert.Equal("Tire & Rim", drops[0].Name);
        Assert.Equal(90, drops[0].Minutes);
        Assert.Equal("https://twitch.facepunch.com/media/x.jpg", drops[0].ImageUrl!.ToString());
        Assert.Equal("Çıplak", drops[1].Name);
        Assert.Null(drops[1].ImageUrl);
        Assert.Null(drops[1].Minutes);
    }

    [Theory]
    [InlineData("1 Hour", 60)]
    [InlineData("2 Hours", 120)]
    [InlineData("30 minutes", 30)]
    [InlineData("1 hour 30 minutes", 90)]
    [InlineData("5h", 300)]
    public void ParseMinutes_Understands_Durations(string text, int expected) => Assert.Equal(expected, FacepunchPageParser.ParseMinutes(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData(null)]
    public void ParseMinutes_ReturnsNullForUnknown(string? text) => Assert.Null(FacepunchPageParser.ParseMinutes(text));

    [Theory]
    [InlineData("Large Wood Box", "large-wood-box")]
    [InlineData("Garage Door", "GARAGE_DOOR")]
    [InlineData("  Rug ", "rug")]
    [InlineData("Çanta", "canta")]
    public void DropNames_MatchTolerantly(string a, string b) => Assert.Equal(DropNames.Key(a), DropNames.Key(b));

    [Fact]
    public void DropNames_DifferentNames_DoNotCollide() => Assert.NotEqual(DropNames.Key("Rug"), DropNames.Key("Bed"));
}

public class DropMediaCacheTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 2];
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static HttpResponseMessage Ok(byte[] bytes, string type) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("" + type) } } };

    private DropMediaCache Cache(StubHandler handler, long total = DropMediaCache.DefaultMaxTotalBytes, long file = DropMediaCache.DefaultMaxFileBytes) =>
        new(Path.Combine(_dir.Path, "media"), new HttpClient(handler), ["files.facepunch.com"], total, file);

    [Fact]
    public async Task Download_StoresFile_ThenServesFromDiskWithoutNetwork()
    {
        var handler = new StubHandler(_ => Ok(Jpeg, "image/jpeg"));
        var cache = Cache(handler);
        var url = new Uri("https://files.facepunch.com/lewis/2026/October/rug.jpg");

        Assert.Null(cache.TryGet(url));
        var path = await cache.EnsureAsync(url);
        var again = await cache.EnsureAsync(url);

        Assert.NotNull(path);
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(path));
        Assert.Equal(path, again);
        Assert.Equal(path, cache.TryGet(url));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://files.facepunch.com/x.jpg")]
    [InlineData("https://evil.example.com/x.jpg")]
    [InlineData("https://files.facepunch.com.evil.com/x.jpg")]
    public async Task DisallowedAddresses_AreNeverRequested(string address)
    {
        var handler = new StubHandler(_ => Ok(Jpeg, "image/jpeg"));

        var path = await Cache(handler).EnsureAsync(new Uri(address));

        Assert.Null(path);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WrongContentType_BadSignature_OrHttpError_AreRejected()
    {
        var url = new Uri("https://files.facepunch.com/a");
        Assert.Null(await Cache(new StubHandler(_ => Ok(Jpeg, "text/html"))).EnsureAsync(url));
        Assert.Null(await Cache(new StubHandler(_ => Ok(new byte[64], "image/jpeg"))).EnsureAsync(url)); // imza yok
        Assert.Null(await Cache(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).EnsureAsync(url));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir.Path, "media")));
    }

    [Fact]
    public async Task OversizedFile_IsRejectedAndLeavesNoPartialFile()
    {
        var big = new byte[2048];
        Jpeg.CopyTo(big, 0);

        var path = await Cache(new StubHandler(_ => Ok(big, "image/jpeg")), file: 1024).EnsureAsync(new Uri("https://files.facepunch.com/big.jpg"));

        Assert.Null(path);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir.Path, "media")));
    }

    [Fact]
    public async Task Mp4_IsAccepted()
    {
        var path = await Cache(new StubHandler(_ => Ok(Mp4, "video/mp4"))).EnsureAsync(new Uri("https://files.facepunch.com/rug.mp4"));

        Assert.EndsWith(".mp4", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purge_RemovesOldestFilesBeyondTotalLimit()
    {
        var handler = new StubHandler(_ => Ok(Jpeg, "image/jpeg"));
        var cache = Cache(handler, total: Jpeg.Length * 2 + 1);
        var a = await cache.EnsureAsync(new Uri("https://files.facepunch.com/a.jpg"));
        File.SetLastWriteTimeUtc(a!, DateTime.UtcNow.AddHours(-3));
        var b = await cache.EnsureAsync(new Uri("https://files.facepunch.com/b.jpg"));
        File.SetLastWriteTimeUtc(b!, DateTime.UtcNow.AddHours(-2));

        var c = await cache.EnsureAsync(new Uri("https://files.facepunch.com/c.jpg"));

        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.True(File.Exists(c));
    }

    [Fact]
    public async Task NetworkFailure_ReturnsNullWithoutThrowing()
    {
        var path = await Cache(new StubHandler(_ => throw new HttpRequestException("ağ yok"))).EnsureAsync(new Uri("https://files.facepunch.com/a.jpg"));

        Assert.Null(path);
    }
}

public class FacepunchMediaServiceTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 2];

    private readonly TempDir _dir = new();
    private bool _pageDown;

    public void Dispose() => _dir.Dispose();

    private StubHandler Handler() => new(request =>
    {
        if (request.Uri.Host == "twitch.facepunch.com")
        {
            return _pageDown
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "facepunch-twitch-drops.html")), System.Text.Encoding.UTF8, "text/html") };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) { Headers = { ContentType = new("image/jpeg") } } };
    });

    private FacepunchMediaService Service(StubHandler handler)
    {
        var http = new HttpClient(handler);
        return new FacepunchMediaService(http, new DropMediaCache(Path.Combine(_dir.Path, "media"), http, FacepunchMediaService.AllowedMediaHosts));
    }

    [Fact]
    public async Task Refresh_FillsCatalogForTwitchOnly_AndDownloadsImages()
    {
        using var service = Service(Handler());
        var changes = 0;
        service.Changed += () => changes++;

        Assert.True(await service.RefreshAsync());

        var twitch = service.Catalog(Platform.Twitch);
        Assert.Equal(6, twitch.Count);
        Assert.All(twitch, d => Assert.True(File.Exists(d.ImagePath)));
        Assert.Empty(service.Catalog(Platform.Kick)); // Kick ekranında Twitch kataloğu yok
        Assert.Equal(2, changes); // önce katalog, sonra görseller
    }

    [Fact]
    public async Task Failure_KeepsPreviousCatalog_AndDoesNotThrow()
    {
        using var service = Service(Handler());
        Assert.True(await service.RefreshAsync());

        _pageDown = true;
        Assert.False(await service.RefreshAsync());

        Assert.Equal(6, service.Catalog(Platform.Twitch).Count);
    }

    [Fact]
    public async Task Offline_LeavesCatalogEmpty_WithoutThrowing()
    {
        using var service = Service(new StubHandler(_ => throw new HttpRequestException("ağ yok")));

        Assert.False(await service.RefreshAsync());

        Assert.Empty(service.Catalog(Platform.Twitch));
    }

    [Fact]
    public async Task MinimalPage_IsParsedFromSameHost()
    {
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<a class=\"drop-box\"><span class=\"drop-type\">X</span></a>") }));
        using var service = new FacepunchMediaService(http, new DropMediaCache(Path.Combine(_dir.Path, "m"), http, FacepunchMediaService.AllowedMediaHosts), new Uri("https://twitch.facepunch.com/"));

        Assert.True(await service.RefreshAsync()); // aynı host: kabul
        Assert.Single(service.Drops);
    }
}

public class ClaimMediaTests
{
    [Fact]
    public void AllowedHosts_AreExactlyTheKnownImageHosts()
    {
        Assert.Equal(
            ["ext.cdn.kick.com", "files.facepunch.com", "files.kick.com", "static-cdn.jtvnw.net"],
            FacepunchMediaService.AllowedMediaHosts.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("https://static-cdn.jtvnw.net/twitch-quests-assets/REWARD/abc.jpeg", true)]
    [InlineData("http://static-cdn.jtvnw.net/x.jpeg", false)]
    [InlineData("https://static-cdn.jtvnw.net.evil.example/x.jpeg", false)]
    [InlineData("https://jtvnw.net/x.jpeg", false)]
    [InlineData("https://www.twitch.tv/x.jpeg", false)]
    public void Cache_AcceptsOnlyTheTwitchRewardCdnOverHttps(string address, bool allowed)
    {
        using var dir = new TempDir();
        var cache = new DropMediaCache(dir.Path, new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), FacepunchMediaService.AllowedMediaHosts);

        Assert.Equal(allowed, cache.IsAllowed(new Uri(address)));
    }

    [Fact]
    public async Task TwitchRewardJpeg_IsDownloadedAndCached()
    {
        using var dir = new TempDir();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 2 };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) { Headers = { ContentType = new("image/jpeg") } } });
        var cache = new DropMediaCache(dir.Path, new HttpClient(handler), FacepunchMediaService.AllowedMediaHosts);
        var url = new Uri("https://static-cdn.jtvnw.net/twitch-quests-assets/REWARD/abc.jpeg");

        var path = await cache.EnsureAsync(url);

        Assert.NotNull(path);
        Assert.Equal(path, cache.TryGet(url));
    }
}

public class FacepunchEventParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void RealTwitchPage_ParsesEventNameDetailsAndUnixDates()
    {
        var events = FacepunchPageParser.ParseEvents(Fixture("facepunch-twitch-event.html"));

        var e = Assert.Single(events);
        Assert.Equal("Base Invaders", e.Title);
        Assert.Equal(["Twitch Drops", "54"], e.Rounds);
        Assert.Equal("Hosted by Rustoria", e.Subtitle);
        Assert.Equal("is-pending", e.BadgeClass);
        Assert.Equal("Upcoming", e.BadgeText);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792778400), e.StartsAt); // 23 Eki 2026 18:00 UTC
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793577540), e.EndsAt);   // 1 Kas 2026 23:59 UTC
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 18, 0, 0, TimeSpan.Zero), e.StartsAt);
    }

    [Fact]
    public void Status_IsComputedFromDates_NotFromTheBadge()
    {
        var e = FacepunchPageParser.ParseEvents(Fixture("facepunch-twitch-event.html")).Single();

        Assert.Equal(FacepunchEventStatus.Upcoming, e.StatusAt(e.StartsAt!.Value.AddSeconds(-1)));
        Assert.Equal(FacepunchEventStatus.Active, e.StatusAt(e.StartsAt!.Value));
        Assert.Equal(FacepunchEventStatus.Active, e.StatusAt(e.EndsAt!.Value.AddSeconds(-1)));
        Assert.Equal(FacepunchEventStatus.Ended, e.StatusAt(e.EndsAt!.Value)); // rozet hâlâ "pending" olsa bile
    }

    [Fact]
    public void BadgeClass_IsOnlyAFallback_WhenThereAreNoDates()
    {
        var pending = new FacepunchEvent("X", [], null, "is-pending", "Upcoming", null, null);
        var ended = new FacepunchEvent("X", [], null, "is-ended", "Ended", null, null);
        var unknown = new FacepunchEvent("X", [], null, "is-weird", "?", null, null);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(FacepunchEventStatus.Upcoming, pending.StatusAt(now));
        Assert.Equal(FacepunchEventStatus.Ended, ended.StatusAt(now));
        Assert.Equal(FacepunchEventStatus.Unknown, unknown.StatusAt(now));
    }

    [Fact]
    public void EnglishDateText_IsUsedWhenNoUnixStamp()
    {
        const string html = """
            <div class="campaign campaign-0"><h2 class="event-title">Metin Tarihli</h2>
            <span class="date">October 23, 2026 at 6:00 PM UTC</span><span class="date-sep">-</span><span class="date">November 1, 2026 at 11:59 PM UTC</span></div>
            """;

        var e = FacepunchPageParser.ParseEvents(html).Single();

        Assert.Equal(new DateTimeOffset(2026, 10, 23, 18, 0, 0, TimeSpan.Zero), e.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.Zero), e.EndsAt);
    }

    [Fact]
    public void CountdownMilliseconds_AreTheLastFallback()
    {
        const string html = """
            <div class="campaign campaign-0"><h2 class="event-title">Geri Sayım</h2></div>
            <script>setupCountdown('.campaign-0', 1792778400000, 1793577540000);</script>
            """;

        var e = FacepunchPageParser.ParseEvents(html).Single();

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792778400000), e.StartsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793577540000), e.EndsAt);
    }

    [Fact]
    public void MultipleBlocks_PickActiveThenUpcomingThenLatestEnded()
    {
        var now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        FacepunchEvent Event(string title, string start, string end) => new(title, [], null, null, null, DateTimeOffset.Parse(start), DateTimeOffset.Parse(end));
        var old = Event("Eski", "2026-06-01T00:00:00Z", "2026-06-10T00:00:00Z");
        var older = Event("Daha eski", "2026-03-01T00:00:00Z", "2026-03-10T00:00:00Z");
        var active = Event("Aktif", "2026-10-23T18:00:00Z", "2026-11-01T23:59:00Z");
        var soon = Event("Yaklaşan", "2026-12-01T00:00:00Z", "2026-12-10T00:00:00Z");
        var later = Event("Daha sonra", "2027-01-01T00:00:00Z", "2027-01-10T00:00:00Z");

        Assert.Equal("Aktif", FacepunchEvent.Pick([old, soon, active, older], now)!.Title);
        Assert.Equal("Yaklaşan", FacepunchEvent.Pick([old, later, soon, older], now)!.Title);
        Assert.Equal("Eski", FacepunchEvent.Pick([older, old], now)!.Title);
        Assert.Null(FacepunchEvent.Pick([], now));
    }

    [Fact]
    public void KickPage_HasNoDropBoxes_OnlyCountdownDates()
    {
        var html = Fixture("facepunch-kick-page.html");

        Assert.Empty(FacepunchPageParser.Parse(html, new Uri("https://kick.facepunch.com/")));
        var e = FacepunchPageParser.ParseEvents(html).Single();
        Assert.Null(e.Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1781208000000), e.StartsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1782082800000), e.EndsAt);
        Assert.Equal(FacepunchEventStatus.Ended, e.StatusAt(DateTimeOffset.Parse("2026-10-19T00:00:00Z")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>boş</html>")]
    [InlineData("<div class=\"campaign campaign-0\"><<< bozuk")]
    [InlineData("<div class=\"campaign campaign-0\"></div>")]
    public void BrokenPages_YieldNoEventsAndNoException(string html) => Assert.Empty(FacepunchPageParser.ParseEvents(html));

    [Fact]
    public async Task Service_ExposesCurrentEventPerPlatform_AndKeepsItOnFailure()
    {
        using var dir = new TempDir();
        var down = false;
        var handler = new StubHandler(r =>
        {
            if (r.Uri.Host is "twitch.facepunch.com" or "kick.facepunch.com")
            {
                if (down)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                var file = r.Uri.Host == "twitch.facepunch.com" ? "facepunch-twitch-drops.html" : "facepunch-kick-page.html";
                var extra = r.Uri.Host == "twitch.facepunch.com" ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "facepunch-twitch-event.html")) : string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(extra + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), System.Text.Encoding.UTF8, "text/html") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var http = new HttpClient(handler);
        using var service = new FacepunchMediaService(http, new DropMediaCache(dir.Path, http, FacepunchMediaService.AllowedMediaHosts), kickPage: FacepunchMediaService.KickPage);
        var now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");

        Assert.True(await service.RefreshAsync());

        Assert.Equal("Base Invaders", service.CurrentEvent(RustDroper.Core.Domain.Platform.Twitch, now)!.Title);
        Assert.Null(service.CurrentEvent(RustDroper.Core.Domain.Platform.Kick, now)!.Title); // yalnız geri sayım tarihleri
        Assert.Empty(service.Catalog(RustDroper.Core.Domain.Platform.Kick));

        down = true;
        Assert.False(await service.RefreshAsync());
        Assert.Equal("Base Invaders", service.CurrentEvent(RustDroper.Core.Domain.Platform.Twitch, now)!.Title);
    }
}
