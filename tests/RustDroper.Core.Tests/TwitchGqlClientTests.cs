using System.Net;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;
using static RustDroper.Core.Tests.StubHandler;
using static RustDroper.Core.Tests.TestTime;

namespace RustDroper.Core.Tests;

public class TwitchGqlClientTests
{
    private static TwitchGqlClient Client(StubHandler handler, FakeTimeProvider? time = null, GqlHashRegistry? hashes = null) =>
        new(
            new HttpClient(handler),
            TwitchClients.SmartboxTv,
            () => "access-token-for-tests-123",
            hashes ?? new GqlHashRegistry(),
            deviceId: "device-id-0123456789abcdef",
            timeProvider: time);

    [Fact]
    public async Task Execute_SendsPersistedQueryBody_AndHeaders()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"data":{"currentUser":{"id":"1"}}}"""));
        var variables = new Dictionary<string, object?> { ["fetchRewardCampaigns"] = false };

        using var result = await Client(handler).ExecuteAsync(GqlOperations.Inventory, variables);

        Assert.Equal("1", result.Data.GetProperty("currentUser").GetProperty("id").GetString());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(TwitchConstants.GqlUri, request.Uri);
        Assert.Equal("OAuth access-token-for-tests-123", request.Authorization);
        Assert.Contains("\"operationName\":\"Inventory\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"sha256Hash\":\"" + GqlOperations.Inventory.DefaultHash + "\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"fetchRewardCampaigns\":false", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_UsesHashOverride()
    {
        var hashes = new GqlHashRegistry();
        hashes.SetOverride(GqlOperations.Inventory, "feedface");
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"data":{}}"""));

        using var _ = await Client(handler, hashes: hashes).ExecuteAsync(GqlOperations.Inventory);

        Assert.Contains("\"sha256Hash\":\"feedface\"", Assert.Single(handler.Requests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistedQueryNotFound_ThrowsHashException_WithoutRetry()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """[{"errors":[{"message":"PersistedQueryNotFound"}]}]"""));

        var ex = await Assert.ThrowsAsync<TwitchHashException>(() => Client(handler).ExecuteAsync(GqlOperations.GameDirectory));

        Assert.Equal("DirectoryPage_Game", ex.OperationName);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task IntegrityFailure_IsReportedAsIntegrityException()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"errors":[{"message":"failed integrity check"}]}"""));

        await Assert.ThrowsAsync<TwitchIntegrityException>(() => Client(handler).ExecuteAsync(GqlOperations.ClaimDrop));
    }

    [Fact]
    public async Task Http401_ThrowsUnauthorized()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "{}"));

        await Assert.ThrowsAsync<TwitchUnauthorizedException>(() => Client(handler).ExecuteAsync(GqlOperations.Inventory));
    }

    [Fact]
    public async Task TransientServiceErrors_AreRetried_ThenSucceed()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;
        var handler = new StubHandler(_ => ++calls switch
        {
            1 => Json(HttpStatusCode.OK, """{"errors":[{"message":"service timeout"}]}"""),
            2 => Json(HttpStatusCode.InternalServerError, "{}"),
            _ => Json(HttpStatusCode.OK, """{"data":{"ok":true}}"""),
        });

        using var result = await PumpAsync(Client(handler, fake).ExecuteAsync(GqlOperations.Inventory), fake);

        Assert.True(result.Data.GetProperty("ok").GetBoolean());
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task PartialErrors_WithData_AreTolerated()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"errors":[{"message":"some field is null","path":["a","b"]}],"data":{"x":1}}"""));

        using var result = await Client(handler).ExecuteAsync(GqlOperations.Inventory);

        Assert.Equal(1, result.Data.GetProperty("x").GetInt32());
        Assert.Equal("some field is null (path: a.b)", Assert.Single(result.PartialErrors));
    }

    [Fact]
    public async Task ErrorsWithoutData_ThrowDataException()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"errors":[{"message":"something odd"}]}"""));

        await Assert.ThrowsAsync<PlatformDataException>(() => Client(handler).ExecuteAsync(GqlOperations.Inventory));
    }

    [Fact]
    public async Task InvalidJson_ThrowsDataException()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "<html>blocked</html>"));

        await Assert.ThrowsAsync<PlatformDataException>(() => Client(handler).ExecuteAsync(GqlOperations.Inventory));
    }
}

public class GqlHashRegistryTests
{
    [Fact]
    public void Overrides_PersistAcrossInstances_AndFallBackToDefaults()
    {
        using var dir = new TempDir();
        new GqlHashRegistry(dir.File("hashes.json")).SetOverride(GqlOperations.ClaimDrop, "newhash");

        var again = new GqlHashRegistry(dir.File("hashes.json"));

        Assert.Equal("newhash", again.GetHash(GqlOperations.ClaimDrop));
        Assert.Equal(GqlOperations.Inventory.DefaultHash, again.GetHash(GqlOperations.Inventory));
    }

    [Fact]
    public void DeviceId_IsStableAcrossCalls()
    {
        var secrets = new InMemorySecretStore();

        var first = TwitchDeviceId.GetOrCreate(secrets);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, TwitchDeviceId.GetOrCreate(secrets));
    }
}
