using System.Net;
using Microsoft.Extensions.Time.Testing;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;
using static RustDroper.Core.Tests.StubHandler;
using static RustDroper.Core.Tests.TestTime;

namespace RustDroper.Core.Tests;

public class TwitchAuthClientTests
{
    [Fact]
    public void DefaultHttpClient_CarriesProfileUserAgent()
    {
        using var http = TwitchConstants.CreateHttpClient(TwitchClients.SmartboxTv);

        Assert.Equal(TwitchClients.SmartboxTv.UserAgent, string.Join(' ', http.DefaultRequestHeaders.GetValues("User-Agent")));
    }

    private const string DeviceJson =
        """{"device_code":"dc123","expires_in":1800,"interval":5,"user_code":"ABCDEFGH","verification_uri":"https://www.twitch.tv/activate?public=true"}""";

    private const string TokenJson =
        """{"access_token":"access-token-value-123456","refresh_token":"refresh-token-value-123456","expires_in":14400,"token_type":"bearer"}""";

    private static TwitchAuthClient Client(StubHandler handler, FakeTimeProvider? time = null) =>
        new(new HttpClient(handler), TwitchClients.SmartboxTv, time);

    private static TwitchDeviceCode Code(int expiresSeconds = 1800) =>
        new("dc123", "ABCDEFGH", new Uri("https://www.twitch.tv/activate"), TimeSpan.FromSeconds(expiresSeconds), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task RequestDeviceCode_ParsesFields_AndSendsClientId()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, DeviceJson));

        var code = await Client(handler).RequestDeviceCodeAsync();

        Assert.Equal("ABCDEFGH", code.UserCode);
        Assert.Equal("dc123", code.DeviceCode);
        Assert.Equal(TimeSpan.FromSeconds(5), code.Interval);
        Assert.Equal(new Uri("https://www.twitch.tv/activate?public=true"), code.VerificationUri);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(TwitchConstants.DeviceCodeUri, request.Uri);
        Assert.Contains("client_id=" + TwitchClients.SmartboxTv.ClientId, request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestDeviceCode_MissingFields_ThrowsDataException()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"interval":5}"""));

        await Assert.ThrowsAsync<PlatformDataException>(() => Client(handler).RequestDeviceCodeAsync());
    }

    [Fact]
    public async Task Poll_WaitsThroughPending_ThenReturnsTokens()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;
        var handler = new StubHandler(_ => ++calls < 3
            ? Json(HttpStatusCode.BadRequest, """{"status":400,"message":"authorization_pending"}""")
            : Json(HttpStatusCode.OK, TokenJson));

        var tokens = await PumpAsync(Client(handler, fake).PollForTokensAsync(Code()), fake);

        Assert.Equal("access-token-value-123456", tokens.AccessToken);
        Assert.Equal("refresh-token-value-123456", tokens.RefreshToken);
        Assert.NotNull(tokens.ExpiresAt);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("device_code=dc123", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_TransientServerErrors_AreSurvived()
    {
        var fake = new FakeTimeProvider();
        var calls = 0;
        var handler = new StubHandler(_ => ++calls < 3
            ? Json(HttpStatusCode.ServiceUnavailable, "{}")
            : Json(HttpStatusCode.OK, TokenJson));

        var tokens = await PumpAsync(Client(handler, fake).PollForTokensAsync(Code()), fake);

        Assert.Equal("access-token-value-123456", tokens.AccessToken);
    }

    [Fact]
    public async Task Poll_AccessDenied_Throws()
    {
        var fake = new FakeTimeProvider();
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"status":400,"message":"access_denied"}"""));

        var ex = await Assert.ThrowsAsync<TwitchLoginException>(() => PumpAsync(Client(handler, fake).PollForTokensAsync(Code()), fake));

        Assert.Equal(TwitchLoginFailure.Denied, ex.Failure);
    }

    [Fact]
    public async Task Poll_NeverApproved_ExpiresAtDeadline()
    {
        var fake = new FakeTimeProvider();
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"status":400,"message":"authorization_pending"}"""));

        var ex = await Assert.ThrowsAsync<TwitchLoginException>(() => PumpAsync(Client(handler, fake).PollForTokensAsync(Code(expiresSeconds: 30)), fake));

        Assert.Equal(TwitchLoginFailure.Expired, ex.Failure);
    }

    [Fact]
    public async Task Validate_401_ReturnsNull_AndSendsOAuthHeader()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{"status":401}"""));

        var result = await Client(handler).ValidateAsync("tok-abc");

        Assert.Null(result);
        Assert.Equal("OAuth tok-abc", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task Validate_Success_ReturnsUser()
    {
        var json = $$"""{"client_id":"{{TwitchClients.SmartboxTv.ClientId}}","login":"testuser","user_id":"4242","expires_in":1000}""";
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, json));

        var result = await Client(handler).ValidateAsync("tok");

        Assert.Equal(new TwitchValidation("4242", "testuser"), result);
    }

    [Fact]
    public async Task Validate_TokenOfAnotherClient_IsTreatedAsInvalid()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"client_id":"someone-else","login":"x","user_id":"1"}"""));

        Assert.Null(await Client(handler).ValidateAsync("tok"));
    }

    [Fact]
    public async Task Validate_ServerError_Throws_NotNull()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadGateway, "{}"));

        var ex = await Assert.ThrowsAsync<PlatformHttpException>(() => Client(handler).ValidateAsync("tok"));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Refresh_Rejected_MeansRevoked()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"status":400,"message":"Invalid refresh token"}"""));

        var ex = await Assert.ThrowsAsync<TwitchLoginException>(() => Client(handler).RefreshAsync("r"));

        Assert.Equal(TwitchLoginFailure.Revoked, ex.Failure);
    }
}

public class TwitchSessionManagerTests
{
    private static readonly string ValidUserJson =
        $$"""{"client_id":"{{TwitchClients.SmartboxTv.ClientId}}","login":"testuser","user_id":"4242","expires_in":1000}""";

    private const string NewTokens =
        """{"access_token":"good-access-token-9999","refresh_token":"new-refresh-token-9999","expires_in":14400}""";

    private static HttpResponseMessage Route(RecordedRequest r, bool refreshWorks = true)
    {
        if (r.Uri == TwitchConstants.ValidateUri)
        {
            return r.Authorization == "OAuth good-access-token-9999"
                ? Json(HttpStatusCode.OK, ValidUserJson)
                : Json(HttpStatusCode.Unauthorized, """{"status":401}""");
        }

        if (r.Uri == TwitchConstants.TokenUri)
        {
            return refreshWorks
                ? Json(HttpStatusCode.OK, NewTokens)
                : Json(HttpStatusCode.BadRequest, """{"message":"Invalid refresh token"}""");
        }

        return Json(HttpStatusCode.NotFound, "{}");
    }

    private static TwitchSessionManager Manager(StubHandler handler, ISecretStore secrets, FakeTimeProvider? time = null) =>
        new(new TwitchAuthClient(new HttpClient(handler), TwitchClients.SmartboxTv, time), secrets);

    private static InMemorySecretStore StoreWith(string access, string? refresh = "old-refresh-token-1111")
    {
        var store = new InMemorySecretStore();
        store.Set(TwitchSessionManager.AccessKey, access);
        if (refresh is not null)
        {
            store.Set(TwitchSessionManager.RefreshKey, refresh);
        }

        return store;
    }

    [Fact]
    public async Task Restore_WithoutStoredToken_IsNotLoggedIn()
    {
        var result = await Manager(new StubHandler(_ => Route(_)), new InMemorySecretStore()).RestoreAsync();

        Assert.Equal(TwitchRestoreStatus.NotLoggedIn, result.Status);
    }

    [Fact]
    public async Task Restore_ValidToken_DoesNotTouchTokenEndpoint()
    {
        var handler = new StubHandler(r => Route(r));

        var result = await Manager(handler, StoreWith("good-access-token-9999")).RestoreAsync();

        Assert.Equal(TwitchRestoreStatus.Restored, result.Status);
        Assert.Equal("testuser", result.Session!.Login);
        Assert.DoesNotContain(handler.Requests, r => r.Uri == TwitchConstants.TokenUri);
    }

    [Fact]
    public async Task Restore_ExpiredAccessToken_RefreshesAndPersistsNewTokens()
    {
        var secrets = StoreWith("expired-access-token-0000");

        var result = await Manager(new StubHandler(r => Route(r)), secrets).RestoreAsync();

        Assert.Equal(TwitchRestoreStatus.Restored, result.Status);
        Assert.Equal("good-access-token-9999", secrets.Get(TwitchSessionManager.AccessKey));
        Assert.Equal("new-refresh-token-9999", secrets.Get(TwitchSessionManager.RefreshKey));
        Assert.Equal("testuser", secrets.Get(TwitchSessionManager.LoginKey));
    }

    [Fact]
    public async Task Restore_RevokedRefreshToken_NeedsLogin_AndClearsSecrets()
    {
        var secrets = StoreWith("expired-access-token-0000");

        var result = await Manager(new StubHandler(r => Route(r, refreshWorks: false)), secrets).RestoreAsync();

        Assert.Equal(TwitchRestoreStatus.NeedsLogin, result.Status);
        Assert.Null(secrets.Get(TwitchSessionManager.AccessKey));
        Assert.Null(secrets.Get(TwitchSessionManager.RefreshKey));
    }

    [Fact]
    public async Task Restore_TokenFromAnotherClient_NeedsLogin_WithoutCallingTwitch()
    {
        var secrets = StoreWith("good-access-token-9999");
        secrets.Set(TwitchSessionManager.ClientKey, TwitchClients.MobileWeb.ClientId);
        var handler = new StubHandler(r => Route(r));

        var result = await Manager(handler, secrets).RestoreAsync();

        Assert.Equal(TwitchRestoreStatus.NeedsLogin, result.Status);
        Assert.Empty(handler.Requests);
        Assert.Null(secrets.Get(TwitchSessionManager.AccessKey));
    }

    [Fact]
    public async Task Restore_NetworkDown_IsOffline_AndKeepsTokens()
    {
        var secrets = StoreWith("good-access-token-9999");
        var handler = new StubHandler(_ => throw new HttpRequestException("DNS"));

        var result = await Manager(handler, secrets).RestoreAsync();

        Assert.Equal(TwitchRestoreStatus.Offline, result.Status);
        Assert.Equal("good-access-token-9999", secrets.Get(TwitchSessionManager.AccessKey));
    }

    [Fact]
    public async Task Login_ShowsCode_StoresTokensAndUser()
    {
        var fake = new FakeTimeProvider();
        var secrets = new InMemorySecretStore();
        var handler = new StubHandler(r =>
            r.Uri == TwitchConstants.DeviceCodeUri
                ? Json(HttpStatusCode.OK, """{"device_code":"dc","expires_in":1800,"interval":5,"user_code":"WXYZ1234","verification_uri":"https://www.twitch.tv/activate"}""")
                : Route(r));
        TwitchDeviceCode? shown = null;

        var session = await PumpAsync(Manager(handler, secrets, fake).LoginAsync(c => shown = c), fake);

        Assert.Equal("WXYZ1234", shown!.UserCode);
        Assert.Equal("testuser", session.Login);
        Assert.Equal("good-access-token-9999", secrets.Get(TwitchSessionManager.AccessKey));
        Assert.Equal("4242", secrets.Get(TwitchSessionManager.UserIdKey));
    }

    [Fact]
    public void Logout_RemovesEverything()
    {
        var secrets = StoreWith("tok-1234567890");
        secrets.Set(TwitchSessionManager.LoginKey, "testuser");

        Manager(new StubHandler(r => Route(r)), secrets).Logout();

        Assert.Null(secrets.Get(TwitchSessionManager.AccessKey));
        Assert.Null(secrets.Get(TwitchSessionManager.LoginKey));
    }
}
