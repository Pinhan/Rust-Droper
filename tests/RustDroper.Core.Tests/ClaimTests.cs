using System.Net;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Platforms.Twitch.PubSub;
using static RustDroper.Core.Tests.StubHandler;
using static RustDroper.Core.Tests.TestTime;

namespace RustDroper.Core.Tests;

public class JsonLinesLogTests
{
    private sealed record Row(string Name, int Value);

    [Fact]
    public void AppendThenReadAll_RoundTrips_InOrder()
    {
        using var dir = new TempDir();
        var log = new JsonLinesLog<Row>(dir.File("h.jsonl"));
        log.Append(new Row("a", 1));
        log.Append(new Row("b", 2));

        Assert.Equal([new Row("a", 1), new Row("b", 2)], new JsonLinesLog<Row>(dir.File("h.jsonl")).ReadAll());
    }

    [Fact]
    public void TornOrCorruptLines_AreSkipped()
    {
        using var dir = new TempDir();
        var log = new JsonLinesLog<Row>(dir.File("h.jsonl"));
        log.Append(new Row("a", 1));
        File.AppendAllText(dir.File("h.jsonl"), "{\"name\":\"yarım\n\nçöp\n");
        log.Append(new Row("b", 2));

        Assert.Equal(["a", "b"], log.ReadAll().Select(r => r.Name));
    }

    [Fact]
    public void MissingFile_IsEmpty()
    {
        using var dir = new TempDir();

        Assert.Empty(new JsonLinesLog<Row>(dir.File("yok.jsonl")).ReadAll());
    }
}

public class TwitchClaimServiceTests
{
    private static TwitchClaimService Service(StubHandler handler) =>
        new(new TwitchGqlClient(
            new HttpClient(handler), TwitchClients.SmartboxTv, () => "tok-for-tests-123456", new GqlHashRegistry(), "device-id-0123456789abcdef"));

    [Theory]
    [InlineData("ELIGIBLE_FOR_ALL", ClaimOutcome.Claimed)]
    [InlineData("DROP_INSTANCE_ALREADY_CLAIMED", ClaimOutcome.AlreadyClaimed)]
    [InlineData("ELIGIBLE_FOR_NONE", ClaimOutcome.Rejected)]
    public async Task Claim_MapsServerStatus(string status, ClaimOutcome expected)
    {
        var payload = "{\"data\":{\"claimDropRewards\":{\"status\":\"" + status + "\",\"isUserAccountConnected\":true}}}";
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, payload));

        var result = await Service(handler).ClaimAsync("inst-1", CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(status, result.Status);
        Assert.Equal(expected != ClaimOutcome.Rejected, result.IsSuccess);
    }

    [Fact]
    public async Task Claim_SendsDropInstanceId_WithPersistedClaimOperation()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"data":{"claimDropRewards":{"status":"ELIGIBLE_FOR_ALL"}}}"""));

        await Service(handler).ClaimAsync("inst-42", CancellationToken.None);

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("\"operationName\":\"DropsPage_ClaimDropRewards\"", body, StringComparison.Ordinal);
        Assert.Contains("\"input\":{\"dropInstanceID\":\"inst-42\"}", body, StringComparison.Ordinal);
        Assert.Contains(GqlOperations.ClaimDrop.DefaultHash, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claim_NullPayload_IsRetriableRejection()
    {
        // Canlı (2026-10-16): geçersiz dropInstanceID → {"claimDropRewards":null}, hata değil.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"data":{"claimDropRewards":null}}"""));

        var result = await Service(handler).ClaimAsync("inst-1", CancellationToken.None);

        Assert.Equal(ClaimOutcome.Rejected, result.Outcome);
        Assert.Equal("NULL_PAYLOAD", result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Claim_MissingPayload_ThrowsDataException()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"data":{"somethingElse":1}}"""));

        await Assert.ThrowsAsync<PlatformDataException>(() => Service(handler).ClaimAsync("inst-1", CancellationToken.None));
    }

    [Fact]
    public async Task Claim_RealResponseShape_IsParsed()
    {
        // Canlı yanıt (2026-10-16; kimlikler sadeleştirildi): tamamlanmış drop için ELIGIBLE_FOR_ALL.
        var payload = """
            {"data":{"claimDropRewards":{"status":"ELIGIBLE_FOR_ALL","isUserAccountConnected":false,
            "dropType":{"id":"drop-1","campaign":{"id":"camp-1","detailsURL":"https://help.twitch.tv/s/article/twitch-chat-badges-guide","__typename":"DropCampaign"},"__typename":"TimeBasedDrop"},
            "__typename":"ClaimDropRewardsPayload"}}}
            """;
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, payload));

        var result = await Service(handler).ClaimAsync("1234#camp-1#drop-1", CancellationToken.None);

        Assert.Equal(ClaimOutcome.Claimed, result.Outcome);
        Assert.Equal("ELIGIBLE_FOR_ALL", result.Status);
    }
}

public class TwitchCurrentSessionShapeTests
{
    [Fact]
    public void CompletedDrop_ReturnsEmptyObject_ThatPlatformTreatsAsNoSession()
    {
        // Canlı (2026-10-16): tamamlanınca dropCurrentSession `null` değil, boş alanlı nesne döner (dropID:"", 0/0, channel:null).
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"currentUser":{"id":"1","dropCurrentSession":{"channel":null,"game":null,"currentMinutesWatched":0,"requiredMinutesWatched":0,"dropID":"","__typename":"DropCurrentSession"},"__typename":"User"}}""");

        var session = TwitchCurrentSessionParser.Parse(doc.RootElement);

        // Ayrıştırıcı boş kimliği olduğu gibi verir; TwitchDropPlatform.GetSessionAsync bunu `null` oturuma çevirir (motorun
        // "art arda 3 yoklamada oturum yok = tamamlandı" kuralı buna dayanır).
        Assert.NotNull(session);
        Assert.True(string.IsNullOrEmpty(session!.DropId));
    }

    [Fact]
    public async Task Platform_MapsEmptySession_ToNull()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.OK,
            """{"data":{"currentUser":{"id":"1","dropCurrentSession":{"channel":null,"game":null,"currentMinutesWatched":0,"requiredMinutesWatched":0,"dropID":"","__typename":"DropCurrentSession"}}}}"""));
        var gql = new TwitchGqlClient(
            new HttpClient(handler), TwitchClients.SmartboxTv, () => "tok-for-tests-123456", new GqlHashRegistry(), "device-id-0123456789abcdef");
        var platform = new TwitchDropPlatform(gql, new HttpClient(handler), "1");
        var channel = new RustDroper.Core.Domain.LiveChannel(
            RustDroper.Core.Domain.Platform.Twitch, "c1", "chan", "Chan", 5, null, new Uri("https://www.twitch.tv/chan"), null);

        Assert.Null(await platform.GetSessionAsync(channel, CancellationToken.None));
    }
}

public class TwitchClaimFallbackTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private static async Task AdvanceAsync(FakeTimeProvider clock, TimeSpan by)
    {
        for (var i = 0; i < 60; i++)
        {
            clock.Advance(by / 60);
            await Task.Delay(2);
        }
    }

    private static (TwitchClaimFallback Fallback, List<DropClaimEvent> Queued, FakeTimeProvider Clock) Create(Func<string, string?>? campaigns = null)
    {
        var clock = new FakeTimeProvider();
        var queued = new List<DropClaimEvent>();
        var fallback = new TwitchClaimFallback("42", e => { lock (queued) { queued.Add(e); } }, campaigns ?? (_ => "camp-1"), clock, Grace);
        return (fallback, queued, clock);
    }

    [Fact]
    public void BuildDropInstanceId_UsesUserCampaignDropFormat() =>
        Assert.Equal("42#camp-1#drop-1", TwitchClaimFallback.BuildDropInstanceId("42", "camp-1", "drop-1"));

    [Fact]
    public async Task CompletedWithoutClaimEvent_QueuesConstructedClaim_AfterGrace()
    {
        var (fallback, queued, clock) = Create();

        fallback.Handle(new DropProgressEvent("drop-1", "chan-1", 30, 30));
        await AdvanceAsync(clock, Grace / 2);
        Assert.Empty(queued);

        await AdvanceAsync(clock, Grace);

        var claim = Assert.Single(queued);
        Assert.Equal("drop-1", claim.DropId);
        Assert.Equal("chan-1", claim.ChannelId);
        Assert.Equal("42#camp-1#drop-1", claim.DropInstanceId);
    }

    [Fact]
    public async Task ClaimEventWithinGrace_SuppressesFallback()
    {
        var (fallback, queued, clock) = Create();

        fallback.Handle(new DropProgressEvent("drop-1", "chan-1", 30, 30));
        await AdvanceAsync(clock, Grace / 3);
        fallback.Handle(new DropClaimEvent("drop-1", "chan-1", "real-instance"));
        await AdvanceAsync(clock, Grace * 2);

        Assert.Empty(queued);
    }

    [Fact]
    public async Task IncompleteProgress_NeverClaims()
    {
        var (fallback, queued, clock) = Create();

        fallback.Handle(new DropProgressEvent("drop-1", "chan-1", 29, 30));
        fallback.Handle(new DropProgressEvent("drop-2", "chan-1", 5, 0)); // gereken bilinmiyor: tamamlandı sayılmaz
        await AdvanceAsync(clock, Grace * 3);

        Assert.Empty(queued);
    }

    [Fact]
    public async Task RepeatedCompletionEvents_QueueOnlyOnce()
    {
        var (fallback, queued, clock) = Create();

        fallback.Handle(new DropProgressEvent("drop-1", "chan-1", 60, 60));
        fallback.Handle(new DropProgressEvent("drop-1", "chan-1", 60, 60));
        await AdvanceAsync(clock, Grace * 2);
        fallback.Handle(new DropProgressEvent("drop-1", "chan-1", 61, 60));
        await AdvanceAsync(clock, Grace * 2);

        Assert.Single(queued);
    }

    [Fact]
    public async Task UnknownCampaign_IsSkipped_WithoutThrowing()
    {
        var (fallback, queued, clock) = Create(_ => null);

        fallback.Handle(new DropProgressEvent("drop-1", "chan-1", 30, 30));
        await AdvanceAsync(clock, Grace * 2);

        Assert.Empty(queued);
    }

    [Fact]
    public async Task Dispose_CancelsPendingFallback()
    {
        var (fallback, queued, clock) = Create();

        fallback.Handle(new DropProgressEvent("drop-1", "chan-1", 30, 30));
        fallback.Dispose();
        await AdvanceAsync(clock, Grace * 2);

        Assert.Empty(queued);
    }
}

public class TwitchAutoClaimerTests
{
    private sealed class ScriptedClaimer(params Func<ClaimResult>[] script) : IDropClaimer
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ClaimResult> ClaimAsync(string dropInstanceId, CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _calls) - 1, script.Length - 1);
            return Task.FromResult(script[index]());
        }
    }

    private static readonly DropClaimEvent Claim = new("drop-1", "chan-1", "inst-1");

    private static async Task<(ClaimRecord Record, IReadOnlyList<ClaimRecord> History)> RunOneAsync(
        IDropClaimer claimer, TempDir dir, FakeTimeProvider clock, int maxAttempts = 5)
    {
        var history = new JsonLinesLog<ClaimRecord>(dir.File("history.jsonl"));
        var claimed = new TaskCompletionSource<ClaimRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TwitchAutoClaimer(claimer, history, clock, maxAttempts: maxAttempts, baseDelay: TimeSpan.FromSeconds(2));
        service.Completed += record => claimed.TrySetResult(record);
        using var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);

        service.Enqueue(Claim);
        var record = await PumpAsync(claimed.Task, clock);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        return (record, history.ReadAll());
    }

    [Fact]
    public async Task RejectedThenAccepted_RetriesAndRecordsSuccess()
    {
        using var dir = new TempDir();
        var claimer = new ScriptedClaimer(
            () => new ClaimResult(ClaimOutcome.Rejected, "NOT_YET"),
            () => new ClaimResult(ClaimOutcome.Rejected, "NOT_YET"),
            () => new ClaimResult(ClaimOutcome.Claimed, "ELIGIBLE_FOR_ALL"));

        var (record, history) = await RunOneAsync(claimer, dir, new FakeTimeProvider());

        Assert.Equal(3, claimer.Calls);
        Assert.Equal("Claimed", record.Outcome);
        Assert.Equal(3, record.Attempts);
        Assert.Equal("inst-1", record.DropInstanceId);
        Assert.Equal(record, Assert.Single(history));
    }

    [Fact]
    public async Task TransientFailures_AreRetried()
    {
        using var dir = new TempDir();
        var calls = 0;
        var claimer = new ScriptedClaimer(() => ++calls < 2 ? throw new TwitchTransientException("service timeout") : new ClaimResult(ClaimOutcome.AlreadyClaimed, "DROP_INSTANCE_ALREADY_CLAIMED"));

        var (record, _) = await RunOneAsync(claimer, dir, new FakeTimeProvider());

        Assert.Equal("AlreadyClaimed", record.Outcome);
        Assert.Equal(2, record.Attempts);
    }

    [Fact]
    public async Task PermanentError_StopsImmediately_AndIsRecordedAsFailed()
    {
        using var dir = new TempDir();
        var claimer = new ScriptedClaimer(() => throw new TwitchHashException("DropsPage_ClaimDropRewards"));

        var (record, history) = await RunOneAsync(claimer, dir, new FakeTimeProvider());

        Assert.Equal(1, claimer.Calls);
        Assert.Equal("Failed", record.Outcome);
        Assert.Contains("TwitchHashException", record.Status, StringComparison.Ordinal);
        Assert.Single(history);
    }

    [Fact]
    public async Task AlwaysRejected_GivesUpAfterMaxAttempts()
    {
        using var dir = new TempDir();
        var claimer = new ScriptedClaimer(() => new ClaimResult(ClaimOutcome.Rejected, "NO"));

        var (record, _) = await RunOneAsync(claimer, dir, new FakeTimeProvider(), maxAttempts: 3);

        Assert.Equal(3, claimer.Calls);
        Assert.Equal("Failed", record.Outcome);
        Assert.Equal("NO", record.Status);
    }

    [Fact]
    public async Task SameInstance_IsNotClaimedTwice()
    {
        using var dir = new TempDir();
        var clock = new FakeTimeProvider();
        var claimer = new ScriptedClaimer(() => new ClaimResult(ClaimOutcome.Claimed, "ELIGIBLE_FOR_ALL"));
        var history = new JsonLinesLog<ClaimRecord>(dir.File("history.jsonl"));
        var service = new TwitchAutoClaimer(claimer, history, clock);
        var completed = 0;
        service.Completed += _ => Interlocked.Increment(ref completed);
        using var cts = new CancellationTokenSource();
        var run = service.RunAsync(cts.Token);

        service.Enqueue(Claim);
        service.Enqueue(Claim);
        service.Enqueue(new DropProgressEvent("drop-1", "chan-1", 5, 30)); // claim değil: yok sayılır
        for (var i = 0; i < 100 && completed < 1; i++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(100);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, claimer.Calls);
        Assert.Equal(1, completed);
    }
}
