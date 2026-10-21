using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using RustDroper.App.Player;
using RustDroper.Core.Watching;

namespace RustDroper.App.Tests.Player;

/// <summary>WebView2 Runtime ve etkileşimli bir masaüstü gerektirir; yoksa (ya da <c>RUSTDROPER_SKIP_WEBVIEW2=1</c>) atlanır.</summary>
internal sealed class WebView2FactAttribute : FactAttribute
{
    public WebView2FactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUSTDROPER_SKIP_WEBVIEW2") == "1")
        {
            Skip = "RUSTDROPER_SKIP_WEBVIEW2=1";
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
            Skip = "WebView2 Runtime yok: " + ex.GetType().Name;
        }
    }
}

/// <summary>Kendi <see cref="Dispatcher"/>'ı olan STA iş parçacığı (WPF/WebView2 için).</summary>
internal sealed class StaDispatcher : IDisposable
{
    private readonly Thread _thread;

    private StaDispatcher(Thread thread, Dispatcher dispatcher)
    {
        _thread = thread;
        Dispatcher = dispatcher;
    }

    public Dispatcher Dispatcher { get; }

    public static StaDispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "sta-test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return new StaDispatcher(thread, dispatcher!);
    }

    public Task<T> InvokeAsync<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    public Task InvokeAsync(Func<Task> func) => Dispatcher.InvokeAsync(func).Task.Unwrap();

    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => Dispatcher.InvokeAsync(func).Task.Unwrap();

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}

[Trait("Category", "WebView2")]
public class WebViewHostTests
{
    /// <summary>Ağsız oynatma: tuval → MediaStream → video (zaman gerçekten ilerler).</summary>
    private const string TestPage =
        "data:text/html,<html><body style='margin:0'><video id='v' autoplay playsinline muted width='284' height='160'></video><canvas id='c' width='64' height='36'></canvas>" +
        "<script>const c=document.getElementById('c');const x=c.getContext('2d');let n=0;setInterval(()=>{x.fillStyle='rgb('+((n++)%255)+',80,80)';x.fillRect(0,0,64,36);},50);" +
        "const v=document.getElementById('v');v.srcObject=c.captureStream(20);v.play();</script></body></html>";

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, string what)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(what + " beklenen sürede olmadı.");
    }

    [WebView2Fact]
    public async Task HiddenPlayer_PlaysOffScreen_ReleasesAndDestroysTheWindow()
    {
        using var temp = new TempDir();
        using var sta = StaDispatcher.Start();
        var host = await sta.InvokeAsync(() => new WebViewKickPlayerHost(
            sta.Dispatcher,
            temp.File("webview2"),
            temp.File("player-session.json"),
            idleGrace: TimeSpan.FromSeconds(2),
            tickInterval: TimeSpan.FromSeconds(1),
            channelUrl: _ => TestPage,
            allowNavigation: _ => true));
        try
        {
            var states = new List<KickPlayerState>();
            host.StatusChanged += status =>
            {
                lock (states)
                {
                    states.Add(status.State);
                }
            };

            await host.OpenAsync("testkanal", CancellationToken.None);
            Assert.Equal(KickPlayerState.Starting, host.Status.State);

            await WaitForAsync(() => host.Status.State == KickPlayerState.Playing ? host.Status : null, TimeSpan.FromSeconds(60), "Playing durumu");
            Assert.Equal("testkanal", host.Status.Slug);

            // Gizlilik: görev çubuğunda yok, ekran dışında, ses kapalı, sayfa yine de "görünür" (oynatıcı duraklatılmasın).
            var facts = await sta.InvokeAsync(async () =>
            {
                var window = host.PlayerWindow!;
                var web = (Microsoft.Web.WebView2.Wpf.WebView2)window.Content;
                var visibility = await web.CoreWebView2.ExecuteScriptAsync("document.visibilityState");
                return (window.ShowInTaskbar, window.Left, window.Top, window.IsActive, web.CoreWebView2.IsMuted, visibility);
            });
            Assert.False(facts.ShowInTaskbar);
            Assert.True(facts.Left < -10000 && facts.Top < -10000, $"pencere ekran dışında olmalı ({facts.Left},{facts.Top})");
            Assert.False(facts.IsActive);
            Assert.True(facts.IsMuted);
            Assert.Equal("\"visible\"", facts.visibility);

            var memory = host.SampleMemory();
            Assert.NotNull(memory);
            Assert.True(memory.Processes >= 2);

            await host.ReleaseAsync();
            Assert.Equal(KickPlayerState.Idle, host.Status.State);

            await WaitForAsync(() => sta.InvokeAsync(() => host.PlayerWindow is null ? new object() : null).Result, TimeSpan.FromSeconds(15), "pencerenin yok edilmesi");
        }
        finally
        {
            await sta.InvokeAsync(() => host.Dispose());
        }
    }

    [Fact]
    public async Task BrowserArguments_AlwaysMuteAudio_AndAllowAutoplay()
    {
        using var temp = new TempDir();
        using var sta = StaDispatcher.Start();
        var host = await sta.InvokeAsync(() => new WebViewKickPlayerHost(sta.Dispatcher, temp.File("webview2"), temp.File("player-session.json")));
        var withGpu = await sta.InvokeAsync(() => new WebViewKickPlayerHost(sta.Dispatcher, temp.File("webview2b"), temp.File("player-session2.json"), allowGpu: true));
        try
        {
            Assert.Contains("--mute-audio", host.EffectiveBrowserArguments, StringComparison.Ordinal);
            Assert.Contains("--mute-audio", withGpu.EffectiveBrowserArguments, StringComparison.Ordinal); // GPU seçeneği sesi etkilemez
            Assert.Contains("--autoplay-policy=no-user-gesture-required", host.EffectiveBrowserArguments, StringComparison.Ordinal);
            Assert.Contains("--disable-gpu", host.EffectiveBrowserArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("--disable-gpu", withGpu.EffectiveBrowserArguments, StringComparison.Ordinal);
        }
        finally
        {
            await sta.InvokeAsync(() => host.Dispose());
            await sta.InvokeAsync(() => withGpu.Dispose());
        }
    }

    [WebView2Fact]
    public async Task Player_StaysMuted_AcrossOpenRestartAndNavigation()
    {
        using var temp = new TempDir();
        using var sta = StaDispatcher.Start();
        var host = await sta.InvokeAsync(() => new WebViewKickPlayerHost(
            sta.Dispatcher,
            temp.File("webview2"),
            temp.File("player-session.json"),
            idleGrace: TimeSpan.FromSeconds(30),
            tickInterval: TimeSpan.FromSeconds(1),
            channelUrl: _ => TestPage,
            allowNavigation: _ => true));
        try
        {
            async Task<bool> IsMutedAsync() => await sta.InvokeAsync(() => ((Microsoft.Web.WebView2.Wpf.WebView2)host.PlayerWindow!.Content).CoreWebView2.IsMuted);

            await host.OpenAsync("sessiz", CancellationToken.None);
            await WaitForAsync(() => host.Status.State == KickPlayerState.Playing ? host.Status : null, TimeSpan.FromSeconds(60), "Playing");
            Assert.True(await IsMutedAsync());

            await host.RestartAsync(CancellationToken.None);
            await WaitForAsync(() => host.Status.State == KickPlayerState.Playing ? host.Status : null, TimeSpan.FromSeconds(60), "yeniden Playing");
            Assert.True(await IsMutedAsync());

            await host.ReleaseAsync();
            await host.OpenAsync("baska", CancellationToken.None);
            await WaitForAsync(() => host.Status is { State: KickPlayerState.Playing, Slug: "baska" } s ? s : null, TimeSpan.FromSeconds(60), "yeni kanal Playing");
            Assert.True(await IsMutedAsync());
        }
        finally
        {
            await sta.InvokeAsync(() => host.Dispose());
        }
    }

    [WebView2Fact]
    public async Task Reopen_WithinGrace_ReusesTheSameWindow_ForAnotherChannel()
    {
        using var temp = new TempDir();
        using var sta = StaDispatcher.Start();
        var host = await sta.InvokeAsync(() => new WebViewKickPlayerHost(
            sta.Dispatcher,
            temp.File("webview2"),
            temp.File("player-session.json"),
            idleGrace: TimeSpan.FromSeconds(30),
            tickInterval: TimeSpan.FromSeconds(1),
            channelUrl: _ => TestPage,
            allowNavigation: _ => true));
        try
        {
            await host.OpenAsync("birinci", CancellationToken.None);
            await WaitForAsync(() => host.Status.State == KickPlayerState.Playing ? host.Status : null, TimeSpan.FromSeconds(60), "ilk kanal Playing");
            var first = await sta.InvokeAsync(() => host.PlayerWindow);

            await host.ReleaseAsync();
            await host.OpenAsync("ikinci", CancellationToken.None);
            await WaitForAsync(() => host.Status is { State: KickPlayerState.Playing, Slug: "ikinci" } s ? s : null, TimeSpan.FromSeconds(60), "ikinci kanal Playing");
            var second = await sta.InvokeAsync(() => host.PlayerWindow);

            Assert.Same(first, second);
        }
        finally
        {
            await sta.InvokeAsync(() => host.Dispose());
        }
    }

    [WebView2Fact]
    public async Task OffKickNavigation_IsBlocked_ByDefault()
    {
        using var temp = new TempDir();
        using var sta = StaDispatcher.Start();
        // Varsayılan izin listesi (yalnız kick.com): test sayfası (data:) engellenir → oynatma başlamaz.
        var host = await sta.InvokeAsync(() => new WebViewKickPlayerHost(
            sta.Dispatcher,
            temp.File("webview2"),
            temp.File("player-session.json"),
            tickInterval: TimeSpan.FromSeconds(1),
            channelUrl: _ => TestPage));
        try
        {
            await host.OpenAsync("engelli", CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(8));

            Assert.NotEqual(KickPlayerState.Playing, host.Status.State);
        }
        finally
        {
            await sta.InvokeAsync(() => host.Dispose());
        }
    }

    [Fact]
    public async Task Session_StartsUnknown_AndMarkerIsNotCreatedWithoutAVerdict()
    {
        using var temp = new TempDir();
        using var sta = StaDispatcher.Start();
        var host = await sta.InvokeAsync(() => new WebViewKickPlayerHost(sta.Dispatcher, temp.File("webview2"), temp.File("player-session.json")));
        try
        {
            var session = (IKickPlayerSession)host;
            Assert.Equal(KickPlayerSessionState.Unknown, session.Status.State);
            Assert.False(File.Exists(temp.File("player-session.json")));
            Assert.Equal(KickPlayerState.Idle, host.Status.State);
            Assert.Null(host.SampleMemory()); // hiçbir tarayıcı başlatılmadı
        }
        finally
        {
            await sta.InvokeAsync(() => host.Dispose());
        }
    }

    [Fact]
    public async Task Session_RestoresLastKnownState_FromMarker_WithoutStartingABrowser()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(
            temp.File("player-session.json"),
            """{"schemaVersion":1,"data":{"state":"loggedIn","checkedAt":"2026-10-19T10:00:00+00:00"}}""");
        using var sta = StaDispatcher.Start();
        var host = await sta.InvokeAsync(() => new WebViewKickPlayerHost(sta.Dispatcher, temp.File("webview2"), temp.File("player-session.json")));
        try
        {
            Assert.Equal(KickPlayerSessionState.LoggedIn, ((IKickPlayerSession)host).Status.State);
            Assert.False(Directory.Exists(temp.File("webview2")));
        }
        finally
        {
            await sta.InvokeAsync(() => host.Dispose());
        }
    }
}
