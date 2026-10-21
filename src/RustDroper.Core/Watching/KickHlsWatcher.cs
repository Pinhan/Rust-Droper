using Microsoft.Extensions.Logging;
using RustDroper.Core.Domain;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Kick;
using RustDroper.Core.Localization;

namespace RustDroper.Core.Watching;

/// <summary>
/// Kick için Tier A izleyici: kanal API'sinden <c>playback_url</c> (IVS HLS ana listesi) alınır, en düşük kalite seçilir, medya listesi
/// yoklanıp yeni parçalar indirilir (gerçek oynatıcı trafiği; tarayıcı/telemetri yok). Bu yöntemin Kick'te sunucu dakika sayacını
/// ilerletip ilerletmediği <b>U3 deneyiyle ölçülür</b> (docs/PLATFORM-NOTES.md); ilerletmezse Tier B/C kararı kullanıcıyla verilir.
/// Playback token'ı sorgu parametresindedir: günlüğe/istisnaya yazılmaz.
/// </summary>
public sealed class KickHlsWatcher : HlsWatcherBase
{
    private readonly KickApiClient _api;

    public KickHlsWatcher(
        KickApiClient api,
        HttpClient http,
        HlsQuality quality = HlsQuality.LowestVideo,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : base(http, quality, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    protected override async Task<OpenResult> OpenAsync(LiveChannel channel, CancellationToken ct)
    {
        KickChannelInfo? info;
        try
        {
            info = await _api.GetChannelAsync(channel.Login, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlatformDataException or PlatformHttpException)
        {
            return OpenResult.Gone(Loc.T("watch.kick.infoFailed", ex.Message), offline: false);
        }

        if (info is null || !info.IsLive)
        {
            return OpenResult.Gone(Loc.T("watch.kick.notLive"), offline: true);
        }

        if (info.PlaybackUrl is null)
        {
            return OpenResult.Gone(Loc.T("watch.kick.noPlayback"), offline: false);
        }

        string? master;
        try
        {
            master = await FetchMasterAsync(info.PlaybackUrl, ct).ConfigureAwait(false);
        }
        catch (PlatformHttpException ex)
        {
            return OpenResult.Gone(Loc.T("watch.kick.master", ex.Message), offline: false);
        }

        return master is null
            ? OpenResult.Gone(Loc.T("watch.kick.master404"), offline: true)
            : SelectVariant(master, info.PlaybackUrl, channel.Login);
    }
}
