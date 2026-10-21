using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Platforms.Twitch;

public sealed record TwitchSession(string AccessToken, string UserId, string Login, DateTimeOffset? ExpiresAt);

public enum TwitchRestoreStatus
{
    /// <summary>Kayıtlı oturum yok.</summary>
    NotLoggedIn,

    Restored,

    /// <summary>Kayıtlı oturum geçersiz ve yenilenemedi; kullanıcı yeniden giriş yapmalı.</summary>
    NeedsLogin,

    /// <summary>Ağ/sunucu sorunu: oturum durumu bilinmiyor, kayıtlı token'lar KORUNDU (sonra tekrar denenir).</summary>
    Offline,
}

public sealed record TwitchRestoreResult(TwitchRestoreStatus Status, TwitchSession? Session);

/// <summary>Token'ları DPAPI'li sır deposunda tutar; açılışta doğrular, gerekirse yeniler.</summary>
public sealed class TwitchSessionManager : Orchestration.IPlatformAccount
{
    internal const string AccessKey = "twitch.accessToken";
    internal const string RefreshKey = "twitch.refreshToken";
    internal const string ExpiresKey = "twitch.expiresAt";
    internal const string UserIdKey = "twitch.userId";
    internal const string LoginKey = "twitch.login";
    internal const string ClientKey = "twitch.clientId";

    private readonly TwitchAuthClient _auth;
    private readonly ISecretStore _secrets;
    private readonly ILogger _logger;

    public TwitchSessionManager(TwitchAuthClient auth, ISecretStore secrets, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(secrets);
        _auth = auth;
        _secrets = secrets;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<TwitchRestoreResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var access = _secrets.Get(AccessKey);
        if (string.IsNullOrEmpty(access))
        {
            return new TwitchRestoreResult(TwitchRestoreStatus.NotLoggedIn, null);
        }

        // Token'lar istemci kimliğine bağlıdır: profil değiştiyse eski token başka istemcide çalışmaz.
        if (_secrets.Get(ClientKey) is { Length: > 0 } storedClient
            && !string.Equals(storedClient, _auth.Profile.ClientId, StringComparison.Ordinal))
        {
            return Invalidate(Loc.T("err.twitch.otherClient"));
        }

        try
        {
            var validation = await _auth.ValidateAsync(access, cancellationToken).ConfigureAwait(false);
            if (validation is not null)
            {
                return Restored(access, validation);
            }

            var refresh = _secrets.Get(RefreshKey);
            if (string.IsNullOrEmpty(refresh))
            {
                return Invalidate(Loc.T("err.twitch.noRefresh"));
            }

            var tokens = await _auth.RefreshAsync(refresh, cancellationToken).ConfigureAwait(false);
            var refreshed = await _auth.ValidateAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            if (refreshed is null)
            {
                return Invalidate(Loc.T("err.twitch.refreshedUnverified"));
            }

            StoreTokens(tokens, refreshed);
            _logger.LogInformation("Twitch oturumu yenilendi ({Login}).", refreshed.Login);
            return Restored(tokens.AccessToken, refreshed, tokens.ExpiresAt);
        }
        catch (TwitchLoginException ex) when (ex.Failure == TwitchLoginFailure.Revoked)
        {
            return Invalidate(ex.Message);
        }
        catch (Exception ex) when (Retry.IsTransient(ex))
        {
            _logger.LogWarning("Twitch oturumu doğrulanamadı (geçici): {Reason}. Token'lar korunuyor.", ex.Message);
            return new TwitchRestoreResult(TwitchRestoreStatus.Offline, null);
        }
    }

    /// <summary>Cihaz-kodu girişi: <paramref name="onCode"/> kullanıcıya gösterilecek kodu/URL'yi alır.</summary>
    public async Task<TwitchSession> LoginAsync(Action<TwitchDeviceCode> onCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onCode);
        var code = await _auth.RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        onCode(code);

        var tokens = await _auth.PollForTokensAsync(code, cancellationToken).ConfigureAwait(false);
        var validation = await _auth.ValidateAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false)
            ?? throw new TwitchLoginException(TwitchLoginFailure.Failed, Loc.T("err.twitch.newUnverified"));

        StoreTokens(tokens, validation);
        _logger.LogInformation("Twitch girişi tamamlandı ({Login}).", validation.Login);
        return new TwitchSession(tokens.AccessToken, validation.UserId, validation.Login, tokens.ExpiresAt);
    }

    public void Logout()
    {
        foreach (var key in new[] { AccessKey, RefreshKey, ExpiresKey, UserIdKey, LoginKey, ClientKey })
        {
            _secrets.Remove(key);
        }

        _logger.LogInformation("Twitch oturumu kapatıldı.");
    }

    private TwitchRestoreResult Restored(string access, TwitchValidation validation, DateTimeOffset? expiresAt = null)
    {
        var storedExpiry = DateTimeOffset.TryParse(_secrets.Get(ExpiresKey), out var parsed) ? parsed : (DateTimeOffset?)null;
        return new TwitchRestoreResult(
            TwitchRestoreStatus.Restored,
            new TwitchSession(access, validation.UserId, validation.Login, expiresAt ?? storedExpiry));
    }

    private TwitchRestoreResult Invalidate(string reason)
    {
        _logger.LogWarning("Twitch oturumu geçersiz: {Reason}", reason);
        Logout();
        return new TwitchRestoreResult(TwitchRestoreStatus.NeedsLogin, null);
    }

    private void StoreTokens(TwitchTokens tokens, TwitchValidation validation)
    {
        _secrets.Set(AccessKey, tokens.AccessToken);
        if (tokens.RefreshToken is { Length: > 0 } refresh)
        {
            _secrets.Set(RefreshKey, refresh);
        }

        if (tokens.ExpiresAt is { } expiresAt)
        {
            _secrets.Set(ExpiresKey, expiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            _secrets.Remove(ExpiresKey);
        }

        _secrets.Set(UserIdKey, validation.UserId);
        _secrets.Set(LoginKey, validation.Login);
        _secrets.Set(ClientKey, _auth.Profile.ClientId);
    }
}
