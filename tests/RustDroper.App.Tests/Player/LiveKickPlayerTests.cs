using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using RustDroper.App.Player;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms.Kick;
using RustDroper.Core.Watching;
using Xunit.Abstractions;

namespace RustDroper.App.Tests.Player;

/// <summary>Gerçek ağa çıkan + WebView2 isteyen test: yalnızca <c>RUSTDROPER_LIVE=1</c> ve WebView2 Runtime varken çalışır.</summary>
internal sealed class LiveWebView2FactAttribute : FactAttribute
{
    public LiveWebView2FactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUSTDROPER_LIVE") != "1")
        {
            Skip = "Canlı test: RUSTDROPER_LIVE=1 ayarlayarak çalıştır.";
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()))
            {
                Skip = "WebView2 Runtime yok";
            }
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or BadImageFormatException or DllNotFoundException)
        {
            Skip = "WebView2 Runtime yok";
        }
    }
}

internal sealed class OutputLogger(ITestOutputHelper output) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            output.WriteLine($"   [{logLevel}] {formatter(state, exception)}");
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// Anonim (girişsiz) canlı ölçüm: gizli pencerede gerçek Kick oynatıcısı gerçekten oynuyor mu, sayfa "görünür" kalıyor mu,
/// en düşük kalite seçilebiliyor mu, bellek nedir. Bu sınama hesapsızdır (Kick'in anonim izleyicisi gibi); drop ilerlemesi ÖLÇMEZ.
/// </summary>
[Trait("Category", "LiveWebView2")]
public class LiveKickPlayerTests(ITestOutputHelper output)
{
    [LiveWebView2Fact]
    public async Task HiddenRealPlayer_Anonymous_PlaysAtLowestQuality_AndStaysVisible()
    {
        using var http = KickConstants.CreateHttpClient();
        var api = new KickApiClient(http, () => null, new SlidingWindowRateLimiter(2, TimeSpan.FromSeconds(1)));
        var channel = Environment.GetEnvironmentVariable("RUSTDROPER_LIVE_CHANNEL");
        if (string.IsNullOrWhiteSpace(channel))
        {
            var live = await api.GetLiveStreamsAsync(limit: 5);
            channel = live.Items.OrderByDescending(c => c.Viewers).First().Login;
        }

        output.WriteLine($"kanal: {channel}");
        using var temp = new TempDir();
        using var sta = StaDispatcher.Start();
        var host = await sta.InvokeAsync(() => new WebViewKickPlayerHost(
            sta.Dispatcher,
            temp.File("webview2"),
            temp.File("player-session.json"),
            tickInterval: TimeSpan.FromSeconds(3),
            logger: new OutputLogger(output),
            extraBrowserArguments: Environment.GetEnvironmentVariable("RUSTDROPER_PLAYER_ARGS"),
            windowSize: Environment.GetEnvironmentVariable("RUSTDROPER_PLAYER_SIZE")?.Split('x') is [var w, var h]
                && double.TryParse(w, out var width) && double.TryParse(h, out var height) ? (width, height) : null,
            allowGpu: Environment.GetEnvironmentVariable("RUSTDROPER_PLAYER_GPU") == "1"));
        var probes = new List<PlayerProbe>();
        var statuses = new List<KickPlayerStatus>();
        host.ProbeObserved += probe =>
        {
            if (probe is not null)
            {
                lock (probes)
                {
                    probes.Add(probe);
                }
            }
        };
        host.StatusChanged += status =>
        {
            lock (statuses)
            {
                statuses.Add(status);
            }
        };
        try
        {
            await host.OpenAsync(channel, CancellationToken.None);
            var started = Stopwatch.StartNew();
            var lastCpu = TimeSpan.Zero;
            var lastAt = TimeSpan.Zero;
            while (started.Elapsed < TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("RUSTDROPER_LIVE_SECONDS"), out var liveSeconds) ? liveSeconds : 75))
            {
                await Task.Delay(5000);
                var memory = host.SampleMemory();
                var cpuText = "?";
                if (memory is { } sample)
                {
                    var wall = started.Elapsed - lastAt;
                    if (lastCpu > TimeSpan.Zero)
                    {
                        cpuText = $"{(sample.Cpu - lastCpu).TotalSeconds / wall.TotalSeconds * 100:F0}% bir çekirdek";
                    }

                    lastCpu = sample.Cpu;
                    lastAt = started.Elapsed;
                }

                PlayerProbe? last;
                lock (probes)
                {
                    last = probes.LastOrDefault();
                }

                output.WriteLine(
                    $"{started.Elapsed.TotalSeconds,5:F0}s durum={host.Status.State} vis={last?.Visibility} auth={last?.Auth} live={last?.Live} " +
                    $"video={(last?.Video is { } v ? $"{v.Width}x{v.Height} t={v.CurrentTime:F1} rs={v.ReadyState} paused={v.Paused}" : "yok")} " +
                    $"bellek={(memory is { } m ? $"{m.Bytes / 1048576.0:F0} MB/{m.Processes} işlem [{m.Breakdown}]" : "?")} cpu={cpuText}");
            }

            await host.ReleaseAsync();
            lock (statuses)
            {
                output.WriteLine("durum geçmişi: " + string.Join(" → ", statuses.Select(s => $"{s.State}{(s.VideoHeight is { } h ? $"({h}p)" : string.Empty)}")));
            }

            lock (probes)
            {
                Assert.Contains(probes, p => p.Video is { Paused: false, ReadyState: >= 3 });
                Assert.All(probes.Where(p => p.Video is not null), p => Assert.Equal("visible", p.Visibility));
                var heights = probes.Where(p => p.Video is { Height: > 0 }).Select(p => p.Video!.Height).ToList();
                output.WriteLine("görülen video yükseklikleri: " + string.Join(",", heights.Distinct()));
                Assert.True(heights.Count > 0 && heights[^1] <= 360, $"en düşük kaliteye inilmeli (son yükseklik: {heights.LastOrDefault()})");
            }
        }
        finally
        {
            await sta.InvokeAsync(() => host.Dispose());
        }
    }
}
