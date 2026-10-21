using System.Text.Json;
using RustDroper.Core.Watching;
using RustDroper.Core.Localization;

namespace RustDroper.App.Player;

/// <summary>Ajan betiğinin bir yoklamada döndürdüğü görüntü (kimlik bilgisi içermez).</summary>
internal sealed record PlayerProbe(
    string? Path,
    string? Ready,
    string? Visibility,
    string? Auth,
    bool? Live,
    int OfflineChecks,
    PlayerVideo? Video)
{
    /// <summary>Betik sonucunu (ExecuteScriptAsync'in JSON'u) ayrıştırır; <c>null</c>/bozuk → <c>null</c> (sayfada ajan yok).</summary>
    public static PlayerProbe? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            PlayerVideo? video = null;
            if (root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                video = new PlayerVideo(
                    Bool(v, "paused"),
                    Bool(v, "ended"),
                    (int)Number(v, "rs"),
                    Number(v, "ct"),
                    (int)Number(v, "w"),
                    (int)Number(v, "h"));
            }

            return new PlayerProbe(
                Text(root, "path"),
                Text(root, "ready"),
                Text(root, "vis"),
                Text(root, "auth"),
                root.TryGetProperty("live", out var live) && live.ValueKind is JsonValueKind.True or JsonValueKind.False ? live.GetBoolean() : null,
                (int)Number(root, "offlineChecks"),
                video);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;
}

internal sealed record PlayerVideo(bool Paused, bool Ended, int ReadyState, double CurrentTime, int Width, int Height);

/// <summary>
/// Yoklamalardan oynatıcı durumunu çıkarır (saf mantık; WebView2 gerekmez). "Oynuyor" yalnız zaman GERÇEKTEN ilerliyorsa söylenir
/// (iki yoklama arasında <c>currentTime</c> artmış, duraklatılmamış, yeterli tampon): sayfa açık ama video takılıysa <see cref="KickPlayerState.Stalled"/>.
/// </summary>
internal sealed class PlayerMonitor
{
    /// <summary>Üst çubukta "Giriş Yap" art arda bu kadar yoklamada görülürse oturum yok sayılır.</summary>
    public const int LoggedOutConfirmations = 2;

    /// <summary>Kanal sorgusu art arda bu kadar kez "yayın yok" derse Offline.</summary>
    public const int OfflineConfirmations = 2;

    /// <summary>Sayfa betiği art arda bu kadar yoklamada yanıt vermezse Failed (yükleme süresi dışında).</summary>
    public const int MaxSilentProbes = 4;

    private readonly string _slug;
    private readonly TimeSpan _loadGrace;
    private readonly DateTimeOffset _openedAt;
    private PlayerVideo? _previous;
    private int _loggedOutStreak;
    private int _silentProbes;

    public PlayerMonitor(string slug, DateTimeOffset openedAt, TimeSpan? loadGrace = null)
    {
        _slug = slug;
        _openedAt = openedAt;
        _loadGrace = loadGrace ?? TimeSpan.FromSeconds(25);
    }

    /// <summary>Son yoklamadaki oturum işareti (<c>in</c>/<c>out</c>/<c>unknown</c>); oturum durumunu güncellemek için.</summary>
    public string? LastAuth { get; private set; }

    public KickPlayerStatus Update(PlayerProbe? probe, DateTimeOffset now)
    {
        if (probe is null)
        {
            _silentProbes++;
            _previous = null;
            return now - _openedAt < _loadGrace || _silentProbes < MaxSilentProbes
                ? new KickPlayerStatus(KickPlayerState.Starting, _slug, Loc.T("player.pageLoading"))
                : new KickPlayerStatus(KickPlayerState.Failed, _slug, Loc.T("player.scriptDead"));
        }

        _silentProbes = 0;
        LastAuth = probe.Auth;
        _loggedOutStreak = probe.Auth == "out" ? _loggedOutStreak + 1 : 0;
        if (_loggedOutStreak >= LoggedOutConfirmations)
        {
            return new KickPlayerStatus(
                KickPlayerState.LoginRequired,
                _slug,
                Loc.T("watch.kick.noSession"));
        }

        if (probe.Live == false && probe.OfflineChecks >= OfflineConfirmations)
        {
            return new KickPlayerStatus(KickPlayerState.Offline, _slug, Loc.T("watch.kick.notLive"));
        }

        var video = probe.Video;
        var previous = _previous;
        _previous = video;
        if (video is null)
        {
            return now - _openedAt < _loadGrace
                ? new KickPlayerStatus(KickPlayerState.Starting, _slug, Loc.T("player.loading"))
                : new KickPlayerStatus(KickPlayerState.Stalled, _slug, Loc.T("player.noVideo"));
        }

        var height = video.Height > 0 ? video.Height : (int?)null;
        if (video.Ended)
        {
            return new KickPlayerStatus(KickPlayerState.Stalled, _slug, Loc.T("player.videoEnded"), height);
        }

        if (video.Paused)
        {
            return new KickPlayerStatus(KickPlayerState.Stalled, _slug, Loc.T("player.videoPaused"), height);
        }

        if (video.ReadyState < 3)
        {
            return new KickPlayerStatus(KickPlayerState.Stalled, _slug, Loc.T("player.buffering", video.ReadyState), height);
        }

        if (previous is null)
        {
            // İlk örnek: ilerleme henüz doğrulanamaz.
            return new KickPlayerStatus(KickPlayerState.Starting, _slug, Loc.T("player.verifying"), height);
        }

        return Math.Abs(video.CurrentTime - previous.CurrentTime) > 0.25
            ? new KickPlayerStatus(KickPlayerState.Playing, _slug, null, height)
            : new KickPlayerStatus(KickPlayerState.Stalled, _slug, Loc.T("player.stalled"), height);
    }
}
