using Microsoft.Extensions.Logging;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Watching;

/// <summary>
/// Tier A izleyici (Twitch): tarayıcısız, gerçek bir oynatıcı gibi yalnızca gerçek yayın trafiği çeker
/// (erişim token'ı → usher ana liste → en düşük kalite → medya listesini yoklayıp yeni parçaları indirir).
/// Bu yöntem Twitch'te drop ilerlemesi sağlıyor (U1, M3.1). Ortak mantık <see cref="HlsWatcherBase"/>'de.
/// </summary>
public sealed class TwitchHlsWatcher : HlsWatcherBase
{
    private static readonly Uri UsherBase = new("https://usher.ttvnw.net/api/channel/hls/");

    private readonly TwitchGqlClient _gql;

    public TwitchHlsWatcher(
        TwitchGqlClient gql,
        HttpClient http,
        HlsQuality quality = HlsQuality.LowestVideo,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : base(http, quality, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(gql);
        _gql = gql;
    }

    protected override async Task<OpenResult> OpenAsync(LiveChannel channel, CancellationToken ct)
    {
        string token;
        string signature;
        try
        {
            using var result = await _gql.ExecuteAsync(
                GqlOperations.PlaybackAccessToken,
                new Dictionary<string, object?>
                {
                    ["isLive"] = true,
                    ["isVod"] = false,
                    ["login"] = channel.Login,
                    ["platform"] = "web",
                    ["playerType"] = "site",
                    ["vodID"] = string.Empty,
                },
                ct).ConfigureAwait(false);
            if (!result.Data.TryGetProp("streamPlaybackAccessToken", out var access)
                || access.GetStringOrNull("value") is not { Length: > 0 } value
                || access.GetStringOrNull("signature") is not { Length: > 0 } sig)
            {
                return OpenResult.Gone(Loc.T("watch.twitch.tokenEmpty"), offline: true);
            }

            token = value;
            signature = sig;
        }
        catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException or TwitchUnauthorizedException or TwitchTransientException)
        {
            return OpenResult.Gone(Loc.T("watch.twitch.tokenFailed", ex.Message), offline: false);
        }

        var usher = new UriBuilder(new Uri(UsherBase, Uri.EscapeDataString(channel.Login) + ".m3u8"))
        {
            Query = string.Join(
                '&',
                "player=twitchweb",
                "allow_source=true",
                "allow_audio_only=true",
                "fast_bread=true",
                "sig=" + Uri.EscapeDataString(signature),
                "token=" + Uri.EscapeDataString(token),
                "p=" + Random.Shared.Next(100000, 999999)),
        }.Uri;

        string? master;
        try
        {
            master = await FetchMasterAsync(usher, ct).ConfigureAwait(false);
        }
        catch (PlatformHttpException ex)
        {
            return OpenResult.Gone($"usher: {ex.Message}", offline: false);
        }

        return master is null
            ? OpenResult.Gone(Loc.T("watch.twitch.usher404"), offline: true)
            : SelectVariant(master, usher, channel.Login);
    }
}
