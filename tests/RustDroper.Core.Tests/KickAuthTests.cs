using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Kick;
using static RustDroper.Core.Tests.StubHandler;
using static RustDroper.Core.Tests.TestTime;

namespace RustDroper.Core.Tests;

public class KickAuthClientTests
{
    // Gerçek sınamada (2026-10-16) gözlenen cevaplar.
    internal const string PendingJson = """{ "message": "Invalid setup UUID and Key" }""";
    internal const string ForbiddenJson = """{"data":{"type":"Forbidden"}}""";
    internal const string CloudflareHtml = "<!DOCTYPE html><html><head><title>Just a moment...</title></head></html>";
    internal const string TokenValue = "kick-token-value-1234567890";

    internal static KickAuthClient Client(StubHandler handler, FakeTimeProvider? time = null) => new(new HttpClient(handler), time);

    internal static HttpResponseMessage Html(HttpStatusCode status, string html) =>
        new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static KickLoginRequest Request(int expiresSeconds = 300) =>
        new("A1B2C3D4-0000-4000-8000-000000000001", "123456", new Uri("https://kick.com/tv/login"), TimeSpan.FromSeconds(expiresSeconds), TimeSpan.FromSeconds(4));

    [Fact]
    public void CreateLoginRequest_BuildsUuidCodeAndUrl()
    {
        var request = Client(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))).CreateLoginRequest();

        Assert.True(Guid.TryParse(request.Uuid, out _));
        Assert.Equal(request.Uuid.ToUpperInvariant(), request.Uuid);
        Assert.Matches("^[0-9]{6}$", request.Code);
        Assert.Equal($"https://kick.com/tv/login?uuid={request.Uuid}&code={request.Code}", request.VerificationUri.ToString());
        Assert.NotEqual(request.Uuid, Client(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))).CreateLoginRequest().Uuid);
    }

    [Fact]
    public async Task Poll_WaitsThroughPending_ThenReturnsToken_WithoutAuthHeader()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;
        var handler = new StubHandler(_ => ++calls < 3
            ? Json(HttpStatusCode.Forbidden, PendingJson)
            : Json(HttpStatusCode.OK, $$"""{"token":"{{TokenValue}}"}"""));

        var token = await PumpAsync(Client(handler, fake).PollForTokenAsync(Request()), fake);

        Assert.Equal(TokenValue, token);
        Assert.Equal(3, handler.Requests.Count);
        var first = handler.Requests[0];
        Assert.Equal("POST", first.Method);
        Assert.Equal("https://kick.com/api/tv/link/authenticate/A1B2C3D4-0000-4000-8000-000000000001", first.Uri.ToString());
        Assert.Equal("""{"key":"123456"}""", first.Body);
        Assert.Null(first.Authorization);
    }

    [Fact]
    public async Task Poll_TokenInsideDataEnvelope_IsAccepted()
    {
        var fake = new FakeTimeProvider();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, $$"""{"data":{"token":"{{TokenValue}}"},"message":"Success"}"""));

        Assert.Equal(TokenValue, await PumpAsync(Client(handler, fake).PollForTokenAsync(Request()), fake));
    }

    [Fact]
    public async Task Poll_SuccessWithoutToken_ThrowsDataException()
    {
        var fake = new FakeTimeProvider();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"data":{}}"""));

        await Assert.ThrowsAsync<PlatformDataException>(() => PumpAsync(Client(handler, fake).PollForTokenAsync(Request()), fake));
    }

    [Fact]
    public async Task Poll_TransientErrors_AreSurvived()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;
        var handler = new StubHandler(_ => ++calls switch
        {
            1 => Json(HttpStatusCode.ServiceUnavailable, "{}"),
            2 => Json(HttpStatusCode.TooManyRequests, "{}"),
            _ => Json(HttpStatusCode.OK, $$"""{"token":"{{TokenValue}}"}"""),
        });

        Assert.Equal(TokenValue, await PumpAsync(Client(handler, fake).PollForTokenAsync(Request()), fake));
    }

    [Fact]
    public async Task Poll_NeverApproved_ExpiresAtDeadline()
    {
        var fake = new FakeTimeProvider();
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, PendingJson));

        var ex = await Assert.ThrowsAsync<KickLoginException>(() => PumpAsync(Client(handler, fake).PollForTokenAsync(Request(expiresSeconds: 30)), fake));

        Assert.Equal(KickLoginFailure.Expired, ex.Failure);
    }

    [Fact]
    public async Task Poll_RepeatedCloudflareBlock_GivesUpWithHttpException()
    {
        var fake = new FakeTimeProvider();
        var handler = new StubHandler(_ => Html(HttpStatusCode.Forbidden, CloudflareHtml));

        var ex = await Assert.ThrowsAsync<PlatformHttpException>(() => PumpAsync(Client(handler, fake).PollForTokenAsync(Request()), fake));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Poll_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeTimeProvider();
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, PendingJson));
        var task = Client(handler, fake).PollForTokenAsync(Request(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task IsTokenValid_200_True_AndSendsBearer()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"data":[],"message":"Success"}"""));

        Assert.True(await Client(handler).IsTokenValidAsync("tok-abc-123456"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(KickConstants.ProgressUri, request.Uri);
        Assert.Equal("Bearer tok-abc-123456", request.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task IsTokenValid_KickRejection_False(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Json(status, ForbiddenJson));

        Assert.False(await Client(handler).IsTokenValidAsync("tok-abc-123456"));
    }

    [Fact]
    public async Task IsTokenValid_CloudflareBlock_ThrowsHttp_NotAuth()
    {
        var handler = new StubHandler(_ => Html(HttpStatusCode.Forbidden, CloudflareHtml));

        var ex = await Assert.ThrowsAsync<PlatformHttpException>(() => Client(handler).IsTokenValidAsync("tok-abc-123456"));

        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task IsTokenValid_ServerError_ThrowsTransient()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadGateway, "{}"));

        var ex = await Assert.ThrowsAsync<PlatformHttpException>(() => Client(handler).IsTokenValidAsync("tok-abc-123456"));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task GetUser_EmptyObject_IsNull_AndShapesAreTolerated()
    {
        Assert.Null(await Client(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))).GetUserAsync("tok-abc-123456"));

        foreach (var json in new[]
                 {
                     """{"id":42,"username":"rustfan"}""",
                     """{"data":{"id":"42","username":"rustfan"}}""",
                     """{"user":{"id":42,"username":"rustfan"}}""",
                 })
        {
            var user = await Client(new StubHandler(_ => Json(HttpStatusCode.OK, json))).GetUserAsync("tok-abc-123456");
            Assert.Equal(new KickUserInfo("42", "rustfan"), user);
        }
    }

    [Fact]
    public async Task GetUser_NonSuccessOrGarbage_IsNull()
    {
        Assert.Null(await Client(new StubHandler(_ => Json(HttpStatusCode.Forbidden, ForbiddenJson))).GetUserAsync("tok-abc-123456"));
        Assert.Null(await Client(new StubHandler(_ => Json(HttpStatusCode.OK, "not json"))).GetUserAsync("tok-abc-123456"));
    }
}

public class KickSessionManagerTests
{
    private static KickSessionManager Manager(StubHandler handler, InMemorySecretStore secrets, FakeTimeProvider? time = null) =>
        new(KickAuthClientTests.Client(handler, time), secrets);

    private static string Route(RecordedRequest r) => r.Uri.AbsolutePath switch
    {
        "/api/v1/drops/progress" => """{"data":[]}""",
        "/api/v1/user" => """{"id":7,"username":"rustfan"}""",
        _ => "{}",
    };

    private static StubHandler Valid() => new(r =>
        r.Uri.AbsolutePath == "/api/v1/drops/progress" && r.Authorization != "Bearer good-token-123456"
            ? Json(HttpStatusCode.Forbidden, KickAuthClientTests.ForbiddenJson)
            : Json(HttpStatusCode.OK, Route(r)));

    [Fact]
    public async Task Restore_NoToken_NotLoggedIn()
    {
        var result = await Manager(Valid(), new InMemorySecretStore()).RestoreAsync();

        Assert.Equal(KickRestoreStatus.NotLoggedIn, result.Status);
    }

    [Fact]
    public async Task Restore_ValidToken_RestoredWithStoredLogin()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(KickSessionManager.AccessKey, "good-token-123456");
        secrets.Set(KickSessionManager.LoginKey, "rustfan");

        var result = await Manager(Valid(), secrets).RestoreAsync();

        Assert.Equal(KickRestoreStatus.Restored, result.Status);
        Assert.Equal("rustfan", result.Session!.Login);
        Assert.Equal("good-token-123456", result.Session.AccessToken);
    }

    [Fact]
    public async Task Restore_ValidTokenWithoutStoredLogin_UsesFallbackName()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(KickSessionManager.AccessKey, "good-token-123456");

        var result = await Manager(Valid(), secrets).RestoreAsync();

        Assert.Equal(KickSessionManager.UnknownLogin, result.Session!.Login);
    }

    [Fact]
    public async Task Restore_RejectedToken_NeedsLogin_AndSecretsCleared()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(KickSessionManager.AccessKey, "stale-token-123456");
        secrets.Set(KickSessionManager.LoginKey, "rustfan");

        var result = await Manager(Valid(), secrets).RestoreAsync();

        Assert.Equal(KickRestoreStatus.NeedsLogin, result.Status);
        Assert.Null(secrets.Get(KickSessionManager.AccessKey));
        Assert.Null(secrets.Get(KickSessionManager.LoginKey));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Restore_NetworkOrBlock_Offline_TokenKept(HttpStatusCode status)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(KickSessionManager.AccessKey, "good-token-123456");
        var handler = new StubHandler(_ => KickAuthClientTests.Html(status, KickAuthClientTests.CloudflareHtml));

        var result = await Manager(handler, secrets).RestoreAsync();

        Assert.Equal(KickRestoreStatus.Offline, result.Status);
        Assert.Equal("good-token-123456", secrets.Get(KickSessionManager.AccessKey));
    }

    [Fact]
    public async Task Restore_ConnectionFailure_Offline_TokenKept()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(KickSessionManager.AccessKey, "good-token-123456");
        var handler = new StubHandler(_ => throw new HttpRequestException("no network"));

        var result = await Manager(handler, secrets).RestoreAsync();

        Assert.Equal(KickRestoreStatus.Offline, result.Status);
        Assert.Equal("good-token-123456", secrets.Get(KickSessionManager.AccessKey));
    }

    [Fact]
    public async Task Login_StoresTokenAndUser_ReportsRequestToCaller()
    {
        var fake = new FakeTimeProvider();
        var secrets = new InMemorySecretStore();
        var pollCalls = 0;
        var handler = new StubHandler(r =>
        {
            if (r.Uri.AbsolutePath.StartsWith("/api/tv/link/authenticate/", StringComparison.Ordinal))
            {
                return ++pollCalls < 2
                    ? Json(HttpStatusCode.Forbidden, KickAuthClientTests.PendingJson)
                    : Json(HttpStatusCode.OK, """{"token":"good-token-123456"}""");
            }

            return Json(HttpStatusCode.OK, Route(r));
        });
        KickLoginRequest? shown = null;

        var session = await PumpAsync(Manager(handler, secrets, fake).LoginAsync(r => shown = r), fake);

        Assert.NotNull(shown);
        Assert.Equal("rustfan", session.Login);
        Assert.Equal("7", session.UserId);
        Assert.Equal("good-token-123456", secrets.Get(KickSessionManager.AccessKey));
        Assert.Equal("rustfan", secrets.Get(KickSessionManager.LoginKey));
    }

    [Fact]
    public async Task Login_TokenFailsValidation_ThrowsAndStoresNothing()
    {
        var fake = new FakeTimeProvider();
        var secrets = new InMemorySecretStore();
        var handler = new StubHandler(r => r.Uri.AbsolutePath.StartsWith("/api/tv/link/authenticate/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"token":"bad-token-1234567"}""")
            : Json(HttpStatusCode.Forbidden, KickAuthClientTests.ForbiddenJson));

        await Assert.ThrowsAsync<KickLoginException>(() => PumpAsync(Manager(handler, secrets, fake).LoginAsync(_ => { }), fake));

        Assert.Null(secrets.Get(KickSessionManager.AccessKey));
    }

    [Fact]
    public void Logout_RemovesAllKickSecrets()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(KickSessionManager.AccessKey, "good-token-123456");
        secrets.Set(KickSessionManager.UserIdKey, "7");
        secrets.Set(KickSessionManager.LoginKey, "rustfan");

        Manager(Valid(), secrets).Logout();

        Assert.Null(secrets.Get(KickSessionManager.AccessKey));
        Assert.Null(secrets.Get(KickSessionManager.UserIdKey));
        Assert.Null(secrets.Get(KickSessionManager.LoginKey));
    }
}
