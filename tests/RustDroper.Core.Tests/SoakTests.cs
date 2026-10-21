using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;
using static RustDroper.Core.Tests.StubHandler;

namespace RustDroper.Core.Tests;

[CollectionDefinition("Soak", DisableParallelization = true)]
public sealed class SoakCollection;

/// <summary>Süreç genelinde (testin kendi) yönetilen yığın / tutamaç / iş parçacığı ölçümü.</summary>
internal readonly record struct ProcessUsage(long ManagedBytes, int Handles, int Threads)
{
    public static ProcessUsage Capture()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        using var process = Process.GetCurrentProcess();
        return new ProcessUsage(GC.GetTotalMemory(forceFullCollection: true), process.HandleCount, process.Threads.Count);
    }

    public override string ToString() => $"yığın={ManagedBytes / 1024.0 / 1024.0:0.00} MB, tutamaç={Handles}, iş parçacığı={Threads}";
}

/// <summary>
/// Hızlandırılmış (sahte saatli) soak: gerçek izleyiciyi/motoru/servisi saatlerce çalışmış gibi sürer ve bellek, tutamaç, iş parçacığı
/// büyümesinin sınırlı kaldığını doğrular. Gerçek 24 saatlik soak değildir (ertelendi); mantıksal sızıntıları (biriken olay/CTS/bağlantı) yakalar.
/// Diğer testlerin gürültüsü olmasın diye paralel çalışmaz.
/// </summary>
[Collection("Soak")]
public class SoakTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Süre/tekrar çarpanı (varsayılan 1 ≈ dakikalar içinde biter). Uzun yerel koşu: <c>RUSTDROPER_SOAK_SCALE=6</c>.</summary>
    private static readonly int Scale = int.TryParse(Environment.GetEnvironmentVariable("RUSTDROPER_SOAK_SCALE"), out var scale) && scale > 0 ? scale : 1;

    private static readonly LiveChannel Channel = new(
        Platform.Twitch, "42", "streamer", "Streamer", 100, "t", new Uri("https://www.twitch.tv/streamer"), null);

    private const string MasterText = """
        #EXTM3U
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=230000,RESOLUTION=284x160,VIDEO="160p30"
        https://edge.example/160p30.m3u8
        """;

    private int _playlistCalls;

    private HttpResponseMessage Serve(RecordedRequest r)
    {
        if (r.Uri.Host == "gql.twitch.tv")
        {
            return Json(HttpStatusCode.OK, """{"data":{"streamPlaybackAccessToken":{"value":"tok","signature":"sig"}}}""");
        }

        if (r.Uri.Host == "usher.ttvnw.net")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MasterText, Encoding.UTF8, "application/vnd.apple.mpegurl") };
        }

        if (r.Uri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal))
        {
            // Her yoklamada 2 yeni parça (gerçek yayında ~4 sn'lik iki parça): sıra numarası yoklama sayısından türer.
            var first = Interlocked.Increment(ref _playlistCalls) * 2L;
            var text = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:2\n")
                .Append("#EXT-X-MEDIA-SEQUENCE:").Append(first).Append('\n')
                .Append("#EXTINF:2.000,live\nhttps://edge.example/seg").Append(first).Append(".ts\n")
                .Append("#EXTINF:2.000,live\nhttps://edge.example/seg").Append(first + 1).Append(".ts\n")
                .ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/vnd.apple.mpegurl") };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100_000]) }; // ~160p parçası
    }

    [Fact]
    public async Task HlsWatcher_AcceleratedMinutes_DoesNotGrowMemoryHandlesOrThreads()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHandler(Serve);
        var gql = new TwitchGqlClient(
            new HttpClient(handler), TwitchClients.SmartboxTv, () => "tok-for-tests-123456", new GqlHashRegistry(), "device-id-0123456789abcdef", timeProvider: clock);
        var watcher = new TwitchHlsWatcher(gql, new HttpClient(handler), HlsQuality.LowestVideo, clock);
        using var cts = new CancellationTokenSource();
        var run = watcher.RunAsync(Channel, cts.Token);

        async Task AdvanceAsync(TimeSpan span)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(1))
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }
        }

        await AdvanceAsync(TimeSpan.FromMinutes(1)); // ısınma
        var before = ProcessUsage.Capture();
        var segmentsBefore = watcher.Stats.SegmentsFetched;

        await AdvanceAsync(TimeSpan.FromMinutes(10 * Scale));
        var after = ProcessUsage.Capture();
        output.WriteLine($"HLS {10 * Scale} dk (hızlandırılmış): {watcher.Stats.SegmentsFetched - segmentsBefore} parça; önce [{before}] → sonra [{after}]");

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(watcher.Stats.SegmentsFetched - segmentsBefore > 100, "İzleyici gerçekten çalışmalıydı.");
        Assert.True(after.ManagedBytes - before.ManagedBytes < 3 * 1024 * 1024, $"Yığın büyüdü: önce {before}, sonra {after}");
        Assert.True(after.Handles - before.Handles <= 15, $"Tutamaç büyüdü: önce {before}, sonra {after}");
        Assert.True(after.Threads - before.Threads <= 5, $"İş parçacığı büyüdü: önce {before}, sonra {after}");
    }

    [Fact]
    public async Task Service_ManyStartStopAndSleepWakeCycles_ReleaseEverything()
    {
        using var dir = new TempDir();
        var account = new FakeAccount { Restore = new TwitchRestoreResult(TwitchRestoreStatus.Restored, new TwitchSession("tok", "42", "testuser", null)) };
        var factory = new FakeConnectionFactory();
        var events = new FakeSystemEvents();
        var clock = new FakeTimeProvider(Fx.Start);
        var campaign = Fx.Camp("c1", "Base Invaders", CampaignScope.General, Fx.Start.AddDays(9), ("d1", 600));
        factory.Platform.Snapshot = Fx.Snap((campaign, [Fx.Ch("1", "streamer")]));
        factory.Platform.Session = _ => new DropSession("d1", 5, 600);
        using var service = new DropperService(
            account,
            factory,
            new SettingsService(dir.File("settings.json")),
            clock,
            systemEvents: events,
            power: new PowerOptions { ResumeDelay = TimeSpan.FromSeconds(2), NetworkSettle = TimeSpan.FromSeconds(1), OutageRestartThreshold = TimeSpan.FromSeconds(30) },
            stateStore: new StateStore(dir.File("state.json")));
        await service.InitializeAsync();

        async Task UntilAsync(Func<bool> condition)
        {
            var started = Environment.TickCount64;
            while (!condition())
            {
                if (Environment.TickCount64 - started > 10_000)
                {
                    throw new TimeoutException("Koşul zamanında gerçekleşmedi.");
                }

                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }
        }

        async Task WarmUpAsync()
        {
            Assert.True(service.Start());
            await UntilAsync(() => service.Snapshot.Engine.State == EngineState.Watching);
            await service.StopAsync();
        }

        await WarmUpAsync();
        var before = ProcessUsage.Capture();

        for (var i = 0; i < 40 * Scale; i++)
        {
            await WarmUpAsync();
        }

        // Uyku/uyanış döngüleri: her seferinde motor durur, uyanınca yeniden başlar.
        Assert.True(service.Start());
        await UntilAsync(() => service.Snapshot.Engine.State == EngineState.Watching);
        for (var i = 0; i < 10 * Scale; i++)
        {
            var watched = factory.Platform.Watched.Count;
            events.Suspend();
            await UntilAsync(() => !service.IsRunning);
            events.Resume();
            await UntilAsync(() => service.IsRunning && factory.Platform.Watched.Count > watched);
        }

        await service.StopAsync();
        var after = ProcessUsage.Capture();
        output.WriteLine($"Servis: {40 * Scale} başlat/durdur + {10 * Scale} uyku/uyanış döngüsü; önce [{before}] → sonra [{after}]");

        int created;
        lock (factory.Created)
        {
            created = factory.Created.Count;
        }

        Assert.Equal(created, Volatile.Read(ref factory.Disposed)); // her kurulan bağlantı kapatıldı
        Assert.False(service.IsRunning);
        Assert.True(after.ManagedBytes - before.ManagedBytes < 3 * 1024 * 1024, $"Yığın büyüdü: önce {before}, sonra {after}");
        Assert.True(after.Handles - before.Handles <= 20, $"Tutamaç büyüdü: önce {before}, sonra {after}");
        Assert.True(after.Threads - before.Threads <= 5, $"İş parçacığı büyüdü: önce {before}, sonra {after}");
    }

    [Fact]
    public async Task Engine_AcceleratedHours_WithFlappingChannelsAndErrors_StaysBounded()
    {
        var clock = new FakeTimeProvider(Fx.Start);
        var platform = new FakePlatform();
        var campaign = Fx.Camp("c1", "Base Invaders", CampaignScope.General, Fx.Start.AddDays(9), ("d1", 600));
        platform.Snapshot = Fx.Snap((campaign, [Fx.Ch("1", "ch1", 500), Fx.Ch("2", "ch2", 300), Fx.Ch("3", "ch3", 100)]));
        var polls = 0;
        platform.Session = channel =>
        {
            var n = Interlocked.Increment(ref polls);
            if (n % 53 == 0)
            {
                throw new HttpRequestException("geçici ağ hatası");
            }

            return new DropSession("d1", n, 100_000);
        };
        var engine = new DropEngine(platform, new EngineOptions { GameSlug = "rust" }, timeProvider: clock);
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(cts.Token);

        async Task AdvanceAsync(TimeSpan span)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(30))
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                if (((int)elapsed.TotalSeconds / 30) % 45 == 0)
                {
                    platform.RaiseOffline(engine.Snapshot.Target?.Channel.Id ?? "1"); // ~22 dk'da bir yayın kapanır → kanal değişir
                }

                await Task.Delay(1);
            }
        }

        await AdvanceAsync(TimeSpan.FromMinutes(30)); // ısınma
        var before = ProcessUsage.Capture();

        await AdvanceAsync(TimeSpan.FromHours(3 * Scale));
        var after = ProcessUsage.Capture();
        output.WriteLine($"Motor {3 * Scale} sa (hızlandırılmış), {polls} yoklama, {platform.Watched.Count} izleyici başlangıcı; önce [{before}] → sonra [{after}]");

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(polls > 150, "Motor gerçekten yoklamalıydı.");
        Assert.True(platform.Watched.Distinct().Count() >= 2, "Kanal değişimleri yaşanmalıydı.");
        Assert.True(engine.Snapshot.Recent.Count <= 50, "Olay listesi sınırlı kalmalı.");
        Assert.True(after.ManagedBytes - before.ManagedBytes < 3 * 1024 * 1024, $"Yığın büyüdü: önce {before}, sonra {after}");
        Assert.True(after.Handles - before.Handles <= 15, $"Tutamaç büyüdü: önce {before}, sonra {after}");
        Assert.True(after.Threads - before.Threads <= 5, $"İş parçacığı büyüdü: önce {before}, sonra {after}");
    }
}
