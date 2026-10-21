using RustDroper.App.Player;
using RustDroper.Core.Watching;

namespace RustDroper.App.Tests.Player;

public class PlayerMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static PlayerProbe Probe(
        double? time = null,
        bool paused = false,
        bool ended = false,
        int readyState = 4,
        int height = 160,
        string? auth = "in",
        bool? live = true,
        int offlineChecks = 0,
        bool noVideo = false) =>
        new(
            "/rustfan",
            "complete",
            "visible",
            auth,
            live,
            offlineChecks,
            noVideo ? null : new PlayerVideo(paused, ended, readyState, time ?? 10, 284, height));

    private static PlayerMonitor NewMonitor(TimeSpan? grace = null) => new("rustfan", T0, grace);

    [Fact]
    public void FirstSample_IsStarting_ThenPlayingWhenTimeAdvances()
    {
        var monitor = NewMonitor();

        var first = monitor.Update(Probe(time: 10), T0.AddSeconds(5));
        var second = monitor.Update(Probe(time: 15), T0.AddSeconds(10));

        Assert.Equal(KickPlayerState.Starting, first.State);
        Assert.Equal(KickPlayerState.Playing, second.State);
        Assert.Equal(160, second.VideoHeight);
        Assert.Equal("rustfan", second.Slug);
    }

    [Fact]
    public void TimeNotAdvancing_IsStalled_EvenIfNotPaused()
    {
        var monitor = NewMonitor();
        monitor.Update(Probe(time: 10), T0.AddSeconds(5));
        monitor.Update(Probe(time: 15), T0.AddSeconds(10));

        var frozen = monitor.Update(Probe(time: 15), T0.AddSeconds(15));

        Assert.Equal(KickPlayerState.Stalled, frozen.State);
        Assert.Contains("ilerlemiyor", frozen.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, 4, "duraklat")]
    [InlineData(false, true, 4, "bitti")]
    [InlineData(false, false, 2, "tampon")]
    public void PausedEndedOrBuffering_IsStalled(bool paused, bool ended, int readyState, string detail)
    {
        var monitor = NewMonitor();
        monitor.Update(Probe(time: 10), T0.AddSeconds(5));

        var status = monitor.Update(Probe(time: 11, paused: paused, ended: ended, readyState: readyState), T0.AddSeconds(10));

        Assert.Equal(KickPlayerState.Stalled, status.State);
        Assert.Contains(detail, status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoVideo_IsStartingWithinGrace_ThenStalled()
    {
        var monitor = NewMonitor(TimeSpan.FromSeconds(25));

        var early = monitor.Update(Probe(noVideo: true), T0.AddSeconds(10));
        var late = monitor.Update(Probe(noVideo: true), T0.AddSeconds(40));

        Assert.Equal(KickPlayerState.Starting, early.State);
        Assert.Equal(KickPlayerState.Stalled, late.State);
    }

    [Fact]
    public void LoggedOut_Twice_RequiresLogin_ButSingleBlipDoesNot()
    {
        var monitor = NewMonitor();

        var blip = monitor.Update(Probe(time: 10, auth: "out"), T0.AddSeconds(5));
        var recovered = monitor.Update(Probe(time: 15, auth: "in"), T0.AddSeconds(10));
        var out1 = monitor.Update(Probe(time: 20, auth: "out"), T0.AddSeconds(15));
        var out2 = monitor.Update(Probe(time: 25, auth: "out"), T0.AddSeconds(20));

        Assert.NotEqual(KickPlayerState.LoginRequired, blip.State);
        Assert.NotEqual(KickPlayerState.LoginRequired, recovered.State);
        Assert.NotEqual(KickPlayerState.LoginRequired, out1.State);
        Assert.Equal(KickPlayerState.LoginRequired, out2.State);
        Assert.Contains("Oynatıcı girişi", out2.Detail, StringComparison.Ordinal);
        Assert.Equal("out", monitor.LastAuth);
    }

    [Fact]
    public void Offline_RequiresConfirmedChecks()
    {
        var monitor = NewMonitor();

        var once = monitor.Update(Probe(time: 10, live: false, offlineChecks: 1), T0.AddSeconds(5));
        var confirmed = monitor.Update(Probe(time: 15, live: false, offlineChecks: 2), T0.AddSeconds(10));

        Assert.NotEqual(KickPlayerState.Offline, once.State);
        Assert.Equal(KickPlayerState.Offline, confirmed.State);
    }

    [Fact]
    public void UnknownLiveState_DoesNotMeanOffline()
    {
        var monitor = NewMonitor();
        monitor.Update(Probe(time: 10, live: null), T0.AddSeconds(5));

        var status = monitor.Update(Probe(time: 15, live: null, offlineChecks: 5), T0.AddSeconds(10));

        Assert.Equal(KickPlayerState.Playing, status.State);
    }

    [Fact]
    public void SilentPage_IsStartingWhileLoading_ThenFailed()
    {
        var monitor = NewMonitor(TimeSpan.FromSeconds(25));

        var loading = monitor.Update(null, T0.AddSeconds(5));
        var s2 = monitor.Update(null, T0.AddSeconds(30));
        var s3 = monitor.Update(null, T0.AddSeconds(35));
        var s4 = monitor.Update(null, T0.AddSeconds(40));

        Assert.Equal(KickPlayerState.Starting, loading.State);
        Assert.Equal(KickPlayerState.Starting, s2.State);
        Assert.Equal(KickPlayerState.Starting, s3.State);
        Assert.Equal(KickPlayerState.Failed, s4.State);
    }

    [Fact]
    public void SilentPage_RecoversAfterAProbeSucceeds()
    {
        var monitor = NewMonitor();
        monitor.Update(null, T0.AddSeconds(30));
        monitor.Update(null, T0.AddSeconds(35));
        monitor.Update(Probe(time: 10), T0.AddSeconds(40));

        var status = monitor.Update(Probe(time: 15), T0.AddSeconds(45));

        Assert.Equal(KickPlayerState.Playing, status.State);
    }
}

public class PlayerHelpersTests
{
    [Fact]
    public void Parse_ReadsAgentPayload()
    {
        const string json = """
            {"path":"/rustfan","ready":"complete","vis":"visible","auth":"in","live":true,"offlineChecks":0,"gates":["cookie"],
             "v":{"paused":false,"ended":false,"rs":4,"ct":12.5,"w":284,"h":160}}
            """;

        var probe = PlayerProbe.Parse(json);

        Assert.NotNull(probe);
        Assert.Equal("visible", probe.Visibility);
        Assert.Equal("in", probe.Auth);
        Assert.True(probe.Live);
        Assert.Equal(12.5, probe.Video!.CurrentTime);
        Assert.Equal(160, probe.Video.Height);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("{bozuk")]
    public void Parse_NullOrGarbage_ReturnsNull(string? json) => Assert.Null(PlayerProbe.Parse(json));

    [Fact]
    public void Parse_UnknownLiveAndMissingVideo_AreTolerated()
    {
        var probe = PlayerProbe.Parse("""{"path":"/x","live":null,"v":null}""");

        Assert.NotNull(probe);
        Assert.Null(probe.Live);
        Assert.Null(probe.Video);
    }

    [Fact]
    public void PickLowestQuality_ChoosesSmallestResolution_SkipsAuto()
    {
        const string items = """
            [{"t":"Auto","checked":true,"c":{"x":1,"y":1}},
             {"t":"1080p60 Giriş gerekli","checked":false,"c":{"x":2,"y":2}},
             {"t":"720p60","checked":false,"c":{"x":3,"y":3}},
             {"t":"480p","checked":false,"c":{"x":4,"y":4}},
             {"t":"160p","checked":false,"c":{"x":5.5,"y":6.5}}]
            """;

        var lowest = WebViewKickPlayerHost.PickLowestQuality(items);

        Assert.NotNull(lowest);
        Assert.Equal(160, lowest.Value.Height);
        Assert.Equal(5.5, lowest.Value.X);
        Assert.Equal(6.5, lowest.Value.Y);
        Assert.False(lowest.Value.Checked);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""[{"t":"Auto","checked":true,"c":{"x":1,"y":1}}]""")]
    [InlineData("""[{"t":"160p","checked":false,"c":null}]""")]
    [InlineData("{bozuk")]
    public void PickLowestQuality_NothingUsable_ReturnsNull(string items) => Assert.Null(WebViewKickPlayerHost.PickLowestQuality(items));

    [Theory]
    [InlineData("https://kick.com/rustfan", true)]
    [InlineData("https://www.kick.com/", true)]
    [InlineData("https://player.kick.com/x", true)]
    [InlineData("http://kick.com/", true)]
    [InlineData("https://evilkick.com/", false)]
    [InlineData("https://kick.com.evil.example/", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("ftp://kick.com/", false)]
    public void IsKickHost_OnlyKickDomains(string url, bool expected) =>
        Assert.Equal(expected, WebViewKickPlayerHost.IsKickHost(new Uri(url)));
}
