using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.Core.Platforms.Kick;

/// <summary>
/// <see cref="KickSessionManager"/>'ı servisin platform-bağımsız hesap arayüzüne uyarlar. Kick tipleri taşıyıcı şekillere çevrilir:
/// QR girişi <see cref="TwitchDeviceCode"/> olarak iletilir (<c>VerificationUri</c> = uygulamayla açılacak tam bağlantı → QR içeriği,
/// <c>UserCode</c> = 6 haneli kod; <c>DeviceCode</c> = uuid), giriş hataları <see cref="TwitchLoginException"/>'a çevrilir.
/// </summary>
public sealed class KickAccount(KickSessionManager manager) : IPlatformAccount
{
    public async Task<TwitchRestoreResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var result = await manager.RestoreAsync(cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            KickRestoreStatus.Restored when result.Session is { } session => new TwitchRestoreResult(TwitchRestoreStatus.Restored, ToSession(session)),
            KickRestoreStatus.NeedsLogin => new TwitchRestoreResult(TwitchRestoreStatus.NeedsLogin, null),
            KickRestoreStatus.Offline => new TwitchRestoreResult(TwitchRestoreStatus.Offline, null),
            _ => new TwitchRestoreResult(TwitchRestoreStatus.NotLoggedIn, null),
        };
    }

    public async Task<TwitchSession> LoginAsync(Action<TwitchDeviceCode> onCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onCode);
        try
        {
            var session = await manager.LoginAsync(
                request => onCode(new TwitchDeviceCode(request.Uuid, request.Code, request.VerificationUri, request.ExpiresIn, request.Interval)),
                cancellationToken).ConfigureAwait(false);
            return ToSession(session);
        }
        catch (KickLoginException ex)
        {
            throw new TwitchLoginException(ex.Failure == KickLoginFailure.Expired ? TwitchLoginFailure.Expired : TwitchLoginFailure.Failed, ex.Message);
        }
    }

    public void Logout() => manager.Logout();

    /// <summary>Kullanıcı kimliği yoksa (adı alınamadıysa) oturum kimliği olarak görünen ad kullanılır: motor hafızası/önbellek bu kimliğe bağlanır.</summary>
    private static TwitchSession ToSession(KickSession session) =>
        new(session.AccessToken, session.UserId ?? session.Login, session.Login, null);
}
