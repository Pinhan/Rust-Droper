using System.Net;
using System.Text.Json;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Twitch;

public sealed record TwitchDeviceCode(string DeviceCode, string UserCode, Uri VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval);

public sealed record TwitchTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

public sealed record TwitchValidation(string UserId, string Login);

public enum TwitchLoginFailure
{
    /// <summary>Kullanıcı kodu zamanında onaylamadı.</summary>
    Expired,

    /// <summary>Kullanıcı Twitch sayfasında reddetti.</summary>
    Denied,

    /// <summary>Yenileme belirteci geçersiz/iptal; yeniden giriş gerekir.</summary>
    Revoked,

    Failed,
}

public sealed class TwitchLoginException(TwitchLoginFailure failure, string message) : Exception(message)
{
    public TwitchLoginFailure Failure { get; } = failure;
}

/// <summary>Twitch OAuth cihaz-kodu akışı (<c>id.twitch.tv</c>). Parola hiçbir zaman uygulamadan geçmez.</summary>
public sealed class TwitchAuthClient
{
    private const int MaxConsecutivePollErrors = 10;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public TwitchAuthClient(HttpClient http, TwitchClientProfile profile, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(profile);
        _http = http;
        Profile = profile;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TwitchClientProfile Profile { get; }

    public async Task<TwitchDeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await PostFormAsync(
            TwitchConstants.DeviceCodeUri,
            new Dictionary<string, string> { ["client_id"] = Profile.ClientId, ["scopes"] = string.Empty },
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PlatformHttpException(response.StatusCode, TwitchConstants.DeviceCodeUri);
        }

        using var json = ParseJson(body, Loc.T("err.what.deviceCode"));
        var root = json.RootElement;
        var deviceCode = root.GetStringOrNull("device_code");
        var userCode = root.GetStringOrNull("user_code");
        var verification = root.GetStringOrNull("verification_uri");
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode)
            || !Uri.TryCreate(verification, UriKind.Absolute, out var verificationUri))
        {
            throw new PlatformDataException(Loc.T("err.twitch.deviceCodeMissing"));
        }

        return new TwitchDeviceCode(
            deviceCode,
            userCode,
            verificationUri,
            TimeSpan.FromSeconds(root.GetInt32OrNull("expires_in") ?? 1800),
            TimeSpan.FromSeconds(Math.Max(1, root.GetInt32OrNull("interval") ?? 5)));
    }

    /// <summary>Kullanıcı Twitch sayfasında kodu onaylayana kadar bekler (yoklama aralığına uyarak).</summary>
    public async Task<TwitchTokens> PollForTokensAsync(TwitchDeviceCode code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        var deadline = _time.GetUtcNow() + code.ExpiresIn;
        var interval = code.Interval;
        var consecutiveErrors = 0;

        while (true)
        {
            await Task.Delay(interval, _time, cancellationToken).ConfigureAwait(false);
            if (_time.GetUtcNow() > deadline)
            {
                throw new TwitchLoginException(TwitchLoginFailure.Expired, Loc.T("err.twitch.codeExpired"));
            }

            try
            {
                using var response = await PostFormAsync(
                    TwitchConstants.TokenUri,
                    new Dictionary<string, string>
                    {
                        ["client_id"] = Profile.ClientId,
                        ["device_code"] = code.DeviceCode,
                        ["grant_type"] = TwitchConstants.DeviceGrantType,
                    },
                    cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                consecutiveErrors = 0;

                if (response.IsSuccessStatusCode)
                {
                    return ParseTokens(body);
                }

                switch (ExtractError(body))
                {
                    case var e when e.Contains("pending", StringComparison.Ordinal):
                        break;
                    case var e when e.Contains("slow_down", StringComparison.Ordinal):
                        interval += TimeSpan.FromSeconds(5);
                        break;
                    case var e when e.Contains("denied", StringComparison.Ordinal):
                        throw new TwitchLoginException(TwitchLoginFailure.Denied, Loc.T("err.twitch.denied"));
                    case var e when e.Contains("expired", StringComparison.Ordinal) || e.Contains("invalid device code", StringComparison.Ordinal):
                        throw new TwitchLoginException(TwitchLoginFailure.Expired, Loc.T("err.twitch.codeInvalid"));
                    default:
                        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            break;
                        }

                        throw new TwitchLoginException(TwitchLoginFailure.Failed, Loc.T("err.twitch.unexpected", (int)response.StatusCode));
                }
            }
            catch (HttpRequestException) when (++consecutiveErrors < MaxConsecutivePollErrors)
            {
                // Geçici ağ kesintisi: bir sonraki aralıkta tekrar dene.
            }
        }
    }

    /// <summary>Token'ı doğrular. Geçersiz/süresi dolmuşsa (401) <c>null</c>; ağ/sunucu hatasında istisna.</summary>
    public async Task<TwitchValidation?> ValidateAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, TwitchConstants.ValidateUri);
        request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + accessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PlatformHttpException(response.StatusCode, TwitchConstants.ValidateUri);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var json = ParseJson(body, Loc.T("err.what.validation"));
        var root = json.RootElement;
        var userId = root.GetStringOrNull("user_id");
        var login = root.GetStringOrNull("login");
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(login))
        {
            throw new PlatformDataException(Loc.T("err.twitch.validationMissing"));
        }

        // Başka bir istemciye ait token bu istemcide çalışmaz → geçersiz say.
        var clientId = root.GetStringOrNull("client_id");
        return clientId is not null && !string.Equals(clientId, Profile.ClientId, StringComparison.Ordinal)
            ? null
            : new TwitchValidation(userId, login);
    }

    public async Task<TwitchTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        using var response = await PostFormAsync(
            TwitchConstants.TokenUri,
            new Dictionary<string, string>
            {
                ["client_id"] = Profile.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            },
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return ParseTokens(body);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw new TwitchLoginException(TwitchLoginFailure.Revoked, Loc.T("err.twitch.refreshInvalid"));
        }

        throw new PlatformHttpException(response.StatusCode, TwitchConstants.TokenUri);
    }

    private Task<HttpResponseMessage> PostFormAsync(Uri uri, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        return SendAndDisposeRequestAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private TwitchTokens ParseTokens(string body)
    {
        using var json = ParseJson(body, "token");
        var root = json.RootElement;
        var access = root.GetStringOrNull("access_token");
        if (string.IsNullOrEmpty(access))
        {
            throw new PlatformDataException(Loc.T("err.twitch.noAccessToken"));
        }

        SecretMasker.Register(access);
        var refresh = root.GetStringOrNull("refresh_token");
        SecretMasker.Register(refresh);
        var expiresIn = root.GetInt32OrNull("expires_in");
        return new TwitchTokens(access, refresh, expiresIn is > 0 ? _time.GetUtcNow().AddSeconds(expiresIn.Value) : null);
    }

    private static JsonDocument ParseJson(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new PlatformDataException(Loc.T("err.twitch.badJson", what), ex);
        }
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var text = json.RootElement.GetStringOrNull("message") ?? json.RootElement.GetStringOrNull("error") ?? string.Empty;
            return text.ToLowerInvariant();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
