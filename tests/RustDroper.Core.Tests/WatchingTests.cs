using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Domain;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;
using static RustDroper.Core.Tests.StubHandler;

namespace RustDroper.Core.Tests;

public class HlsParserTests
{
    private const string Master = """
        #EXT-X-TWITCH-INFO:NODE="video-edge",MANIFEST-NODE-TYPE="weaver_cluster"
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="1080p60 (source)",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=8000000,RESOLUTION=1920x1080,CODECS="avc1.64002A,mp4a.40.2",VIDEO="chunked",FRAME-RATE=60.000
        https://edge.example/v1/chunked.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="160p30",NAME="160p",AUTOSELECT=YES,DEFAULT=YES
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=230000,RESOLUTION=284x160,CODECS="avc1.4D400C,mp4a.40.2",VIDEO="160p30",FRAME-RATE=30.000
        https://edge.example/v1/160p30.m3u8
        #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="audio_only",NAME="audio_only",AUTOSELECT=NO,DEFAULT=NO
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=160000,CODECS="mp4a.40.2",VIDEO="audio_only"
        /v1/audio_only.m3u8
        """;

    private static readonly Uri Base = new("https://usher.example/api/channel/hls/x.m3u8");

    [Fact]
    public void Master_ParsesVariants_WithVideoFlag_AndResolvesRelativeUrls()
    {
        var variants = HlsParser.ParseMaster(Master, Base);

        Assert.Equal(["chunked", "160p30", "audio_only"], variants.Select(v => v.Name));
        Assert.Equal([8000000, 230000, 160000], variants.Select(v => v.Bandwidth));
        Assert.Equal([true, true, false], variants.Select(v => v.HasVideo));
        Assert.Equal(new Uri("https://usher.example/v1/audio_only.m3u8"), variants[2].Uri);
    }

    [Fact]
    public void Media_ParsesSequenceDurationsPrefetchAndEnd()
    {
        const string media = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:100
            #EXTINF:2.000,live
            https://edge.example/seg100.ts
            #EXTINF:2.000,live
            seg101.ts
            #EXT-X-TWITCH-PREFETCH:https://edge.example/seg102.ts
            #EXT-X-ENDLIST
            """;

        var playlist = HlsParser.ParseMedia(media, new Uri("https://edge.example/v1/160p30.m3u8"));

        Assert.Equal(6, playlist.TargetDurationSeconds);
        Assert.Equal(100, playlist.MediaSequence);
        Assert.True(playlist.Ended);
        Assert.Equal([100L, 101L, 102L], playlist.Segments.Select(s => s.Sequence));
        Assert.Equal([false, false, true], playlist.Segments.Select(s => s.IsPrefetch));
        Assert.Equal(new Uri("https://edge.example/v1/seg101.ts"), playlist.Segments[1].Uri);
    }
}

public class TwitchHlsWatcherTests
{
    private static readonly LiveChannel Channel = new(
        Platform.Twitch, "42", "streamer", "Streamer", 100, "t", new Uri("https://www.twitch.tv/streamer"), null);

    private const string MasterText = """
        #EXTM3U
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=8000000,RESOLUTION=1920x1080,VIDEO="chunked"
        https://edge.example/chunked.m3u8
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=230000,RESOLUTION=284x160,VIDEO="160p30"
        https://edge.example/160p30.m3u8
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=160000,VIDEO="audio_only"
        https://edge.example/audio_only.m3u8
        """;

    // Canlı (2026-10-16, küçük kanal): alt kalite yok, "en düşük video" = kaynak (6,67 Mbps).
    private const string SourceOnlyMasterText = """
        #EXTM3U
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=6670556,RESOLUTION=1920x1080,VIDEO="chunked"
        https://edge.example/chunked.m3u8
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=160000,VIDEO="audio_only"
        https://edge.example/audio_only.m3u8
        """;

    private sealed class FakeTwitch
    {
        public int TokenCalls;
        public int PlaylistCalls;
        public int Forbidden403At = -1;
        public bool UsherNotFound;

        /// <summary>Küçük kanal: yalnızca kaynak kalite (chunked) + yalnız ses.</summary>
        public bool SourceOnly;

        public readonly List<string> FetchedMedia = [];
        public readonly List<string> FetchedSegments = [];

        public HttpResponseMessage Handle(RecordedRequest r)
        {
            if (r.Uri.Host == "gql.twitch.tv")
            {
                Interlocked.Increment(ref TokenCalls);
                return Json(HttpStatusCode.OK, """{"data":{"streamPlaybackAccessToken":{"value":"tok","signature":"sig"}}}""");
            }

            if (r.Uri.Host == "usher.ttvnw.net")
            {
                return UsherNotFound ? new HttpResponseMessage(HttpStatusCode.NotFound) : Text(SourceOnly ? SourceOnlyMasterText : MasterText);
            }

            if (r.Uri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal))
            {
                var call = Interlocked.Increment(ref PlaylistCalls);
                lock (FetchedMedia)
                {
                    FetchedMedia.Add(r.Uri.AbsolutePath);
                }

                if (call == Forbidden403At)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                var first = call * 2L;
                var text = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:2\n")
                    .Append("#EXT-X-MEDIA-SEQUENCE:").Append(first).Append('\n')
                    .Append("#EXTINF:2.000,live\nhttps://edge.example/seg").Append(first).Append(".ts\n")
                    .Append("#EXTINF:2.000,live\nhttps://edge.example/seg").Append(first + 1).Append(".ts\n")
                    .ToString();
                return Text(text);
            }

            lock (FetchedSegments)
            {
                FetchedSegments.Add(r.Uri.AbsolutePath);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1000]) };
        }

        private static HttpResponseMessage Text(string text) =>
            new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/vnd.apple.mpegurl") };
    }

    private static (TwitchHlsWatcher Watcher, FakeTwitch Server, FakeTimeProvider Clock) Create(HlsQuality quality = HlsQuality.LowestVideo, Action<FakeTwitch>? configure = null)
    {
        var server = new FakeTwitch();
        configure?.Invoke(server);
        var handler = new StubHandler(server.Handle);
        var clock = new FakeTimeProvider();
        var gql = new TwitchGqlClient(
            new HttpClient(handler), TwitchClients.SmartboxTv, () => "tok-for-tests-123456", new GqlHashRegistry(), "device-id-0123456789abcdef", timeProvider: clock);
        return (new TwitchHlsWatcher(gql, new HttpClient(handler), quality, clock), server, clock);
    }

    private static async Task RunUntilAsync(FakeTimeProvider clock, Func<bool> condition, int maxSteps = 400)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task Watcher_PicksLowestVideoVariant_AndFetchesLiveSegments()
    {
        var (watcher, server, clock) = Create();
        using var cts = new CancellationTokenSource();

        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => watcher.Stats.SegmentsFetched >= 4);
        await cts.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WatchEnd.Cancelled, result.End);
        Assert.Equal("160p30", watcher.Stats.Variant);
        Assert.True(watcher.Stats.SegmentsFetched >= 4);
        Assert.Equal(watcher.Stats.SegmentsFetched * 1000, watcher.Stats.BytesFetched);
        Assert.All(server.FetchedMedia, p => Assert.EndsWith("160p30.m3u8", p, StringComparison.Ordinal));

        // Her turda yalnızca yeni parçalar: tekrar indirme yok.
        Assert.Equal(server.FetchedSegments.Count, server.FetchedSegments.Distinct().Count());
    }

    [Fact]
    public async Task Watcher_CanPreferAudioOnly()
    {
        var (watcher, server, clock) = Create(HlsQuality.AudioOnly);
        using var cts = new CancellationTokenSource();

        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => watcher.Stats.SegmentsFetched >= 1);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("audio_only", watcher.Stats.Variant);
        Assert.All(server.FetchedMedia, p => Assert.EndsWith("audio_only.m3u8", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watcher_SourceOnlyChannel_FallsBackToAudioOnly_ToSaveBandwidth()
    {
        var (watcher, server, clock) = Create(configure: s => s.SourceOnly = true);
        using var cts = new CancellationTokenSource();

        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => watcher.Stats.SegmentsFetched >= 1);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("audio_only", watcher.Stats.Variant);
        Assert.All(server.FetchedMedia, p => Assert.EndsWith("audio_only.m3u8", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watcher_OfflineChannel_ReturnsOffline()
    {
        var (watcher, _, clock) = Create(configure: s => s.UsherNotFound = true);

        var run = watcher.RunAsync(Channel, CancellationToken.None);
        await RunUntilAsync(clock, () => run.IsCompleted);

        Assert.Equal(WatchEnd.Offline, (await run.WaitAsync(TimeSpan.FromSeconds(5))).End);
    }

    [Fact]
    public async Task Watcher_Forbidden_RefreshesAccessToken_AndContinues()
    {
        var (watcher, server, clock) = Create(configure: s => s.Forbidden403At = 2);
        using var cts = new CancellationTokenSource();

        var run = watcher.RunAsync(Channel, cts.Token);
        await RunUntilAsync(clock, () => server.TokenCalls >= 2 && watcher.Stats.SegmentsFetched >= 3);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(server.TokenCalls >= 2);
        Assert.True(watcher.Stats.SegmentsFetched >= 3);
    }
}

public class TwitchProgressParsingTests
{
    [Fact]
    public void Inventory_InProgress_ParsesDropProgressAndClaimability()
    {
        const string json = """
            {"currentUser":{"id":"1","inventory":{"gameEventDrops":[],"dropCampaignsInProgress":[
              {"id":"camp1","name":"Campaign One","timeBasedDrops":[
                {"id":"d1","name":"","requiredMinutesWatched":30,"requiredSubs":0,
                 "benefitEdges":[{"benefit":{"name":"Cool Badge"}}],
                 "self":{"currentMinutesWatched":30,"isClaimed":false,"dropInstanceID":"inst-1","hasPreconditionsMet":true}},
                {"id":"d2","name":"Second","requiredMinutesWatched":120,"requiredSubs":0,
                 "benefitEdges":[],"self":{"currentMinutesWatched":45,"isClaimed":false,"dropInstanceID":null,"hasPreconditionsMet":false}},
                {"id":"sub","name":"Sub drop","requiredMinutesWatched":0,"requiredSubs":5,"benefitEdges":[]}
              ]}]}}}
            """;
        using var doc = JsonDocument.Parse(json);

        var snapshot = TwitchInventoryParser.Parse(doc.RootElement);

        Assert.Equal(1, snapshot.CampaignsInProgress);
        Assert.Equal(2, snapshot.InProgress.Count);
        var first = snapshot.InProgress[0];
        Assert.Equal("Cool Badge", first.RewardName);
        Assert.True(first.IsClaimable);
        Assert.Equal(100.0, first.Percent);
        var second = snapshot.InProgress[1];
        Assert.False(second.IsClaimable);
        Assert.False(second.PreconditionsMet);
        Assert.Equal(75, second.RemainingMinutes);
        Assert.Equal(37.5, second.Percent);
    }

    [Fact]
    public void CurrentSession_RealLiveResponse_Parses()
    {
        // Gerçek cevap (2026-10-15): HLS izlemesi sırasında sunucunun döndürdüğü oturum (10. dakika).
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "twitch-current-session.json")));

        var session = TwitchCurrentSessionParser.Parse(doc.RootElement);

        Assert.Equal(new TwitchCurrentSession("94911c9d-c7cb-11f1-ae09-0a58a9feac02", 10, 60), session);
    }

    [Fact]
    public void CurrentSession_ParsesMinutes_AndNullWhenNoSession()
    {
        using var active = JsonDocument.Parse("""{"currentUser":{"dropCurrentSession":{"dropID":"d1","currentMinutesWatched":7,"requiredMinutesWatched":30}}}""");
        using var none = JsonDocument.Parse("""{"currentUser":{"dropCurrentSession":null}}""");

        Assert.Equal(new TwitchCurrentSession("d1", 7, 30), TwitchCurrentSessionParser.Parse(active.RootElement));
        Assert.Null(TwitchCurrentSessionParser.Parse(none.RootElement));
    }
}
