namespace RustDroper.Core.Localization;

internal static partial class CoreStrings
{
    private static readonly LocEntry[] Watch =
    [
        // İzleyici sonuç/neden metinleri ("Son olaylar"da görünür)
        L("engine.watchingState", "{0} izleniyor", "Watching {0}"),
        L("engine.watcherEnded", "{0} izleyicisi sona erdi ({1}): {2}", "{0} watcher ended ({1}): {2}"),
        L("watch.noVariant", "ana listede varyant yok.", "no variant in the master playlist."),
        L("watch.media404", "medya listesi 404 (yayın bitti).", "media playlist 404 (stream ended)."),
        L("watch.mediaFailed", "medya listesi {0} kez alınamadı: {1}", "media playlist couldn't be fetched {0} times: {1}"),
        L("watch.endlist", "yayın sona erdi (ENDLIST).", "stream ended (ENDLIST)."),
        L("watch.kick.infoFailed", "Kanal bilgisi alınamadı: {0}", "Couldn't get channel info: {0}"),
        L("watch.kick.notLive", "Kanal canlı değil.", "The channel isn't live."),
        L("watch.kick.noPlayback", "Kanal bilgisinde playback_url yok.", "No playback_url in the channel info."),
        L("watch.kick.master", "ana liste: {0}", "master playlist: {0}"),
        L("watch.kick.master404", "ana liste 404: yayın kapalı.", "master playlist 404: the stream is offline."),
        L("watch.variant.webview", "WebView2 oynatıcı", "WebView2 player"),
        L("watch.kick.switched", "Oynatıcı başka bir kanala geçti ({0}).", "The player switched to another channel ({0})."),
        L("watch.kick.noSession", "Oynatıcı oturumu yok: Kick kartından 'Oynatıcı girişi' yapın.", "No player session: use 'Player sign-in' on the Kick card."),
        L("watch.kick.unavailable", "Oynatıcı bileşeni (WebView2) kullanılamıyor.", "The player component (WebView2) isn't available."),
        L("watch.kick.restarted",
            "Oynatıcı {0:0} dk içinde {1} kez yeniden başlatıldı; vazgeçildi (son durum: {2}{3}).",
            "The player was restarted {1} times within {0:0} min; giving up (last state: {2}{3})."),
        L("watch.kick.error", "Oynatıcı hatası: {0}", "Player error: {0}"),
        L("watch.twitch.tokenEmpty", "PlaybackAccessToken boş döndü (yayın kapalı olabilir).", "PlaybackAccessToken came back empty (the stream may be offline)."),
        L("watch.twitch.tokenFailed", "PlaybackAccessToken alınamadı: {0}", "Couldn't get PlaybackAccessToken: {0}"),
        L("watch.twitch.usher404", "usher 404: yayın kapalı.", "usher 404: the stream is offline."),

        // Kick gizli oynatıcı durumları (Kick kartı / izleme nedeni)
        L("kick.unknownLogin", "Kick hesabı", "Kick account"),
        L("player.pageLoading", "Sayfa yükleniyor", "Page is loading"),
        L("player.scriptDead", "Sayfa betiği yanıt vermiyor", "Page script isn't responding"),
        L("player.loading", "Oynatıcı yükleniyor", "Player is loading"),
        L("player.noVideo", "Sayfada video öğesi yok", "No video element on the page"),
        L("player.videoEnded", "Video bitti", "Video ended"),
        L("player.videoPaused", "Video duraklatılmış", "Video is paused"),
        L("player.buffering", "Video tamponluyor (readyState={0})", "Video is buffering (readyState={0})"),
        L("player.verifying", "Oynatma doğrulanıyor", "Verifying playback"),
        L("player.stalled", "Video zamanı ilerlemiyor", "Video time isn't advancing"),
        L("player.badChannel", "Geçersiz kanal adı", "Invalid channel name"),
        L("player.opening", "Oynatıcı açılıyor", "Opening the player"),
        L("player.restarting", "Oynatıcı yeniden başlatılıyor", "Restarting the player"),
        L("player.closing", "Uygulama kapanıyor", "The app is closing"),
        L("player.noRuntime", "WebView2 Runtime yok", "WebView2 Runtime missing"),
        L("player.noRuntime.long",
            "Microsoft Edge WebView2 Runtime bulunamadı (https://developer.microsoft.com/microsoft-edge/webview2 adresinden 'Evergreen' kurulumu yükleyin).",
            "Microsoft Edge WebView2 Runtime was not found (install the 'Evergreen' runtime from https://developer.microsoft.com/microsoft-edge/webview2)."),
        L("player.webviewFailed", "WebView2 başlatılamadı ({0})", "WebView2 couldn't start ({0})"),
        L("player.windowTitle", "Rust Droper oynatıcı", "Rust Droper player"),
        L("player.processFailed", "Tarayıcı işlemi sorunu ({0})", "Browser process problem ({0})"),
        L("player.lastVerified", "son doğrulama {0:dd.MM HH:mm}", "last verified {0:MM/dd HH:mm}"),
        L("player.error", "Oynatıcı hatası ({0})", "Player error ({0})"),
    ];
}
