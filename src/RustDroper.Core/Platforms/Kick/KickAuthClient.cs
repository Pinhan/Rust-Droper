using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>Kullanıcının tarayıcıda açıp onaylayacağı TV-login isteği. <see cref="Code"/> yalnızca göstermek içindir.</summary>
public sealed record KickLoginRequest(string Uuid, string Code, Uri VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval);

public sealed record KickUserInfo(string UserId, string Login);

public enum KickLoginFailure
{
    /// <summary>Kullanıcı bağlantıyı zamanında onaylamadı.</summary>
    Expired,

    Failed,
}

public sealed class KickLoginException(KickLoginFailure failure, string message) : Exception(message)
{
    public KickLoginFailure Failure { get; } = failure;
}

/// <summary>
/// Kick "TV login" akışı (referans: Alorf/TwitchDropsBot): uygulama <c>uuid</c> + 6 haneli kod üretir →
/// <c>kick.com/tv/login?uuid=…&amp;code=…</c> kullanıcıya açılır (kullanıcı kendi hesabıyla onaylar) →
/// <c>POST kick.com/api/tv/link/authenticate/{uuid} {"key": kod}</c> yoklanır, onaylanınca <c>token</c> döner.
/// Parola uygulamadan hiç geçmez. Onay bekleyen/bilinmeyen çift <c>403 {"message":"Invalid setup UUID and Key"}</c> döndürür
/// (gerçek sınama, 2026-10-16) → "henüz onaylanmadı" sayılır.
/// </summary>
public sealed class KickAuthClient
{
    private const int MaxConsecutiveErrors = 10;
    private const int MaxBlockedResponses = 6;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public KickAuthClient(HttpClient http, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _time = timeProvider ?? TimeProvider.System;
    }

    public KickLoginRequest CreateLoginRequest(TimeSpan? expiresIn = null, TimeSpan? interval = null)
    {
        var uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var uri = new UriBuilder(KickConstants.TvLoginPage) { Query = $"uuid={uuid}&code={code}" }.Uri;
        return new KickLoginRequest(uuid, code, uri, expiresIn ?? TimeSpan.FromMinutes(5), interval ?? TimeSpan.FromSeconds(4));
    }

    /// <summary>Kullanıcı onaylayana (token dönene) ya da süre dolana kadar yoklar.</summary>
    public async Task<string> PollForTokenAsync(KickLoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = KickConstants.TvLinkPollUri(request.Uuid);
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["key"] = request.Code });
        var deadline = _time.GetUtcNow() + request.ExpiresIn;
        var interval = request.Interval;
        var errors = 0;
        var blocked = 0;

        while (true)
        {
            await Task.Delay(interval, _time, cancellationToken).ConfigureAwait(false);
            if (_time.GetUtcNow() > deadline)
            {
                throw new KickLoginException(KickLoginFailure.Expired, Loc.T("err.kick.linkExpired"));
            }

            try
            {
                using var message = KickHttp.Request(HttpMethod.Post, uri);
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                errors = 0;

                if (response.IsSuccessStatusCode)
                {
                    return ParseToken(text);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    interval += TimeSpan.FromSeconds(2);
                    continue;
                }

                // Onay bekleyen çift Kick'in kendi JSON'uyla döner; JSON olmayan sert ret (Cloudflare engeli) tekrarlanıyorsa vazgeç.
                if (!KickHttp.IsKickJson(text) && ++blocked >= MaxBlockedResponses)
                {
                    throw new PlatformHttpException(response.StatusCode, uri);
                }
            }
            catch (HttpRequestException) when (++errors < MaxConsecutiveErrors)
            {
                // Geçici ağ kesintisi: bir sonraki aralıkta tekrar dene.
            }
        }
    }

    /// <summary>
    /// Kullanıcı bilgisi (en iyi çaba): <c>GET kick.com/api/v1/user</c>. Anonim/geçersiz token'da <c>{}</c> döner → <c>null</c>.
    /// Token geçerliliğinin kesin kaynağı <see cref="IsTokenValidAsync"/>'dır; bu yalnızca görünen ad içindir.
    /// </summary>
    public async Task<KickUserInfo?> GetUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        using var message = KickHttp.Request(HttpMethod.Get, KickConstants.UserUri, accessToken, KickConstants.MobileUserAgent);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            foreach (var candidate in new[] { root, KickHttp.Unwrap(root), root.TryGetProp("user", out var user) ? user : root })
            {
                var id = candidate.GetStringOrNull("id");
                var login = candidate.GetStringOrNull("username") ?? candidate.GetStringOrNull("slug");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(login))
                {
                    return new KickUserInfo(id, login);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Token geçerli mi? <c>GET web.kick.com/api/v1/drops/progress</c> (uygulamanın zaten kullandığı kimlikli uç): 2xx = geçerli,
    /// 401 / Kick-JSON'lu 403 = geçersiz (<c>false</c>); ağ/sunucu/engel hatasında istisna (token korunur).
    /// </summary>
    public async Task<bool> IsTokenValidAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        using var message = KickHttp.Request(HttpMethod.Get, KickConstants.ProgressUri, accessToken);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var error = KickHttp.Classify(response, KickConstants.ProgressUri, text, authenticated: true);
        return error is PlatformAuthException ? false : throw error;
    }

    private static string ParseToken(string text)
    {
        using var document = KickHttp.ParseJson(text, Loc.T("err.what.signin"));
        var root = document.RootElement;
        foreach (var candidate in new[] { root, KickHttp.Unwrap(root) })
        {
            var token = candidate.GetStringOrNull("token") ?? candidate.GetStringOrNull("access_token");
            if (!string.IsNullOrEmpty(token))
            {
                SecretMasker.Register(token);
                return token;
            }
        }

        throw new PlatformDataException(Loc.T("err.kick.noToken2"));
    }
}
