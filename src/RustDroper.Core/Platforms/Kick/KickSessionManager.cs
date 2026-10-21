using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Kick;

public sealed record KickSession(string AccessToken, string? UserId, string Login);

public enum KickRestoreStatus
{
    /// <summary>Kayıtlı oturum yok.</summary>
    NotLoggedIn,

    Restored,

    /// <summary>Kayıtlı token geçersiz; kullanıcı yeniden giriş yapmalı.</summary>
    NeedsLogin,

    /// <summary>Ağ/sunucu sorunu: oturum durumu bilinmiyor, kayıtlı token KORUNDU (sonra tekrar denenir).</summary>
    Offline,
}

public sealed record KickRestoreResult(KickRestoreStatus Status, KickSession? Session);

/// <summary>Kick token'ını DPAPI'li sır deposunda tutar; açılışta doğrular (Twitch'teki <c>TwitchSessionManager</c> kalıbı).</summary>
public sealed class KickSessionManager
{
    internal const string AccessKey = "kick.accessToken";
    internal const string UserIdKey = "kick.userId";
    internal const string LoginKey = "kick.login";

    /// <summary>Kullanıcı adı alınamazsa gösterilen ad (token geçerli, yalnız ad bilinmiyor).</summary>
    public static string UnknownLogin => Loc.T("kick.unknownLogin");

    private readonly KickAuthClient _auth;
    private readonly ISecretStore _secrets;
    private readonly ILogger _logger;

    public KickSessionManager(KickAuthClient auth, ISecretStore secrets, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(secrets);
        _auth = auth;
        _secrets = secrets;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<KickRestoreResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var access = _secrets.Get(AccessKey);
        if (string.IsNullOrEmpty(access))
        {
            return new KickRestoreResult(KickRestoreStatus.NotLoggedIn, null);
        }

        try
        {
            if (!await _auth.IsTokenValidAsync(access, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Kick oturumu geçersiz; yeniden giriş gerekiyor.");
                Logout();
                return new KickRestoreResult(KickRestoreStatus.NeedsLogin, null);
            }
        }
        catch (Exception ex) when (Retry.IsTransient(ex) || ex is PlatformHttpException)
        {
            _logger.LogWarning("Kick oturumu doğrulanamadı (geçici): {Reason}. Token korunuyor.", ex.Message);
            return new KickRestoreResult(KickRestoreStatus.Offline, null);
        }

        return new KickRestoreResult(
            KickRestoreStatus.Restored,
            new KickSession(access, _secrets.Get(UserIdKey), _secrets.Get(LoginKey) ?? UnknownLogin));
    }

    /// <summary>TV-login girişi: <paramref name="onRequest"/> kullanıcıya gösterilecek bağlantıyı/kodu alır.</summary>
    public async Task<KickSession> LoginAsync(Action<KickLoginRequest> onRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onRequest);
        var request = _auth.CreateLoginRequest();
        onRequest(request);

        var token = await _auth.PollForTokenAsync(request, cancellationToken).ConfigureAwait(false);
        if (!await _auth.IsTokenValidAsync(token, cancellationToken).ConfigureAwait(false))
        {
            throw new KickLoginException(KickLoginFailure.Failed, Loc.T("err.kick.tokenVerify"));
        }

        KickUserInfo? user = null;
        try
        {
            user = await _auth.GetUserAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Kick kullanıcı adı alınamadı: {Reason}", ex.Message);
        }

        _secrets.Set(AccessKey, token);
        if (user is not null)
        {
            _secrets.Set(UserIdKey, user.UserId);
            _secrets.Set(LoginKey, user.Login);
        }
        else
        {
            _secrets.Remove(UserIdKey);
            _secrets.Remove(LoginKey);
        }

        _logger.LogInformation("Kick girişi tamamlandı ({Login}).", user?.Login ?? UnknownLogin);
        return new KickSession(token, user?.UserId, user?.Login ?? UnknownLogin);
    }

    public void Logout()
    {
        foreach (var key in new[] { AccessKey, UserIdKey, LoginKey })
        {
            _secrets.Remove(key);
        }

        _logger.LogInformation("Kick oturumu kapatıldı.");
    }
}
