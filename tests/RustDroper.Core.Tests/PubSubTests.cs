using System.Collections.Concurrent;
using System.Text.Json;
using RustDroper.Core.Platforms.Twitch.PubSub;

namespace RustDroper.Core.Tests;

internal static class PubSubFrames
{
    public static string Message(string topic, string innerJson) =>
        JsonSerializer.Serialize(new { type = "MESSAGE", data = new { topic, message = innerJson } });

    public static string DropProgress(string userId = "1") =>
        Message($"user-drop-events.{userId}", """{"type":"drop-progress","data":{"drop_id":"d1","channel_id":"c1","current_progress_min":3,"required_progress_min":30}}""");

    public static string DropClaim(string userId = "1") =>
        Message($"user-drop-events.{userId}", """{"type":"drop-claim","data":{"drop_id":"d1","channel_id":"c1","drop_instance_id":"inst-77"}}""");
}

public class TwitchPubSubProtocolTests
{
    [Fact]
    public void DropProgress_And_DropClaim_AreParsed()
    {
        var progress = TwitchPubSubProtocol.Parse(PubSubFrames.DropProgress());
        var claim = TwitchPubSubProtocol.Parse(PubSubFrames.DropClaim());

        Assert.Equal(PubSubFrameKind.Message, progress.Kind);
        Assert.Equal(new DropProgressEvent("d1", "c1", 3, 30), progress.Event);
        Assert.Equal(new DropClaimEvent("d1", "c1", "inst-77"), claim.Event);
    }

    [Fact]
    public void StreamUpDown_AreParsedFromPlaybackTopic()
    {
        var down = TwitchPubSubProtocol.Parse(PubSubFrames.Message("video-playback-by-id.555", """{"type":"stream-down","server_time":1}"""));
        var up = TwitchPubSubProtocol.Parse(PubSubFrames.Message("video-playback-by-id.555", """{"type":"stream-up"}"""));
        var views = TwitchPubSubProtocol.Parse(PubSubFrames.Message("video-playback-by-id.555", """{"type":"viewcount","viewers":3}"""));

        Assert.Equal(new StreamStateEvent("555", false), down.Event);
        Assert.Equal(new StreamStateEvent("555", true), up.Event);
        Assert.Null(views.Event);
    }

    [Theory]
    [InlineData("""{"type":"PONG"}""", PubSubFrameKind.Pong)]
    [InlineData("""{"type":"RECONNECT"}""", PubSubFrameKind.Reconnect)]
    [InlineData("""{"type":"SOMETHING_NEW"}""", PubSubFrameKind.Unknown)]
    [InlineData("not json at all", PubSubFrameKind.Unknown)]
    public void SimpleFrames(string frame, PubSubFrameKind expected)
    {
        Assert.Equal(expected, TwitchPubSubProtocol.Parse(frame).Kind);
    }

    [Fact]
    public void Response_CarriesErrorText()
    {
        var frame = TwitchPubSubProtocol.Parse("""{"type":"RESPONSE","nonce":"n1","error":"ERR_BADAUTH"}""");

        Assert.Equal(PubSubFrameKind.Response, frame.Kind);
        Assert.Equal("n1", frame.Nonce);
        Assert.Equal("ERR_BADAUTH", frame.Error);
    }

    [Fact]
    public void UnrecognizedMessage_StillExposesItsType_ForDiagnostics()
    {
        var frame = TwitchPubSubProtocol.Parse(PubSubFrames.Message("user-drop-events.1", """{"type":"reward-redeemed","data":{"secret":"x"}}"""));

        Assert.Null(frame.Event);
        Assert.Equal("user-drop-events.1", frame.Topic);
        Assert.Equal("reward-redeemed", frame.MessageType);
    }

    [Fact]
    public void UnknownOrMalformedInnerMessages_AreIgnoredNotFatal()
    {
        Assert.Null(TwitchPubSubProtocol.Parse(PubSubFrames.Message("user-drop-events.1", """{"type":"reward-redeemed","data":{}}""")).Event);
        Assert.Null(TwitchPubSubProtocol.Parse(PubSubFrames.Message("user-drop-events.1", "{bozuk")).Event);
        Assert.Null(TwitchPubSubProtocol.Parse(PubSubFrames.Message("user-drop-events.1", """{"type":"drop-claim","data":{"drop_id":"d1"}}""")).Event);
    }

    [Fact]
    public void BuildListen_HasTopicsAndAuthToken()
    {
        using var doc = JsonDocument.Parse(TwitchPubSubProtocol.BuildListen(["a.1", "b.2"], "secret-token-123", "n9"));
        var root = doc.RootElement;

        Assert.Equal("LISTEN", root.GetProperty("type").GetString());
        Assert.Equal("n9", root.GetProperty("nonce").GetString());
        Assert.Equal(["a.1", "b.2"], root.GetProperty("data").GetProperty("topics").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("secret-token-123", root.GetProperty("data").GetProperty("auth_token").GetString());
    }
}

public class TwitchPubSubClientTests
{
    private static readonly PubSubOptions Fast = new()
    {
        PingInterval = TimeSpan.FromMilliseconds(150),
        PongTimeout = TimeSpan.FromMilliseconds(100),
        ReconnectBaseDelay = TimeSpan.FromMilliseconds(40),
        ReconnectMaxDelay = TimeSpan.FromMilliseconds(120),
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 6000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Beklenen koşul zamanında gerçekleşmedi.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Listens_AndDeliversEventsToSubscribers()
    {
        await using var server = new FakePubSubServer();
        var client = new TwitchPubSubClient(() => "tok-for-test-12345", server.Endpoint, Fast);
        var events = new ConcurrentQueue<TwitchPubSubEvent>();
        client.Subscribe(events.Enqueue);
        await client.SetTopicsAsync(["user-drop-events.1"]);
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => server.CountReceived("LISTEN") == 1);
        var listen = server.Received.First(f => f.Contains("LISTEN", StringComparison.Ordinal));
        Assert.Contains("user-drop-events.1", listen, StringComparison.Ordinal);
        Assert.Contains("\"auth_token\":\"tok-for-test-12345\"", listen, StringComparison.Ordinal);

        await server.SendAsync(PubSubFrames.DropProgress());
        await server.SendAsync(PubSubFrames.DropClaim());
        await WaitUntilAsync(() => events.Count == 2);

        Assert.IsType<DropProgressEvent>(events.ToArray()[0]);
        Assert.Equal(new DropClaimEvent("d1", "c1", "inst-77"), events.ToArray()[1]);
        Assert.True(client.IsConnected);
        Assert.NotNull(client.LastMessageAt);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectionDrop_ReconnectsAndListensAgain()
    {
        await using var server = new FakePubSubServer();
        var client = new TwitchPubSubClient(() => "tok-for-test-12345", server.Endpoint, Fast);
        await client.SetTopicsAsync(["user-drop-events.1"]);
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => server.CountReceived("LISTEN") == 1);
        server.DropAllConnections();
        await WaitUntilAsync(() => server.ConnectionCount >= 2 && server.CountReceived("LISTEN") >= 2);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MissingPong_ForcesReconnect()
    {
        await using var server = new FakePubSubServer { AutoPong = false };
        var client = new TwitchPubSubClient(() => "tok-for-test-12345", server.Endpoint, Fast);
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => server.ConnectionCount >= 2);

        Assert.True(server.CountReceived("PING") >= 1);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HealthyPong_KeepsSingleConnection()
    {
        await using var server = new FakePubSubServer();
        var client = new TwitchPubSubClient(() => "tok-for-test-12345", server.Endpoint, Fast);
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => server.CountReceived("PING") >= 3);

        Assert.Equal(1, server.ConnectionCount);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BadAuthResponse_RaisesAuthFailedStatus()
    {
        await using var server = new FakePubSubServer
        {
            Responder = f => f.Contains("LISTEN", StringComparison.Ordinal) ? """{"type":"RESPONSE","nonce":"x","error":"ERR_BADAUTH"}""" : null,
        };
        var client = new TwitchPubSubClient(() => "bad-token-123456", server.Endpoint, Fast);
        var statuses = new ConcurrentQueue<PubSubStatus>();
        client.StatusChanged += (status, _) => statuses.Enqueue(status);
        await client.SetTopicsAsync(["user-drop-events.1"]);
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => statuses.Contains(PubSubStatus.AuthFailed));

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TopicsChangedWhileConnected_AreSentWithoutReconnect()
    {
        await using var server = new FakePubSubServer();
        var client = new TwitchPubSubClient(() => "tok-for-test-12345", server.Endpoint, Fast);
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => client.IsConnected);
        await client.SetTopicsAsync(["video-playback-by-id.42"]);
        await WaitUntilAsync(() => server.CountReceived("video-playback-by-id.42") == 1);
        await client.SetTopicsAsync([]);
        await WaitUntilAsync(() => server.CountReceived("UNLISTEN") == 1);

        Assert.Equal(1, server.ConnectionCount);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotBreakOthersOrConnection()
    {
        await using var server = new FakePubSubServer();
        var client = new TwitchPubSubClient(() => "tok-for-test-12345", server.Endpoint, Fast);
        var delivered = new ConcurrentQueue<TwitchPubSubEvent>();
        client.Subscribe(_ => throw new InvalidOperationException("kötü abone"));
        client.Subscribe(delivered.Enqueue);
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(cts.Token);
        await WaitUntilAsync(() => client.IsConnected);
        await server.SendAsync(PubSubFrames.DropClaim());
        await WaitUntilAsync(() => delivered.Count == 1);

        Assert.True(client.IsConnected);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
