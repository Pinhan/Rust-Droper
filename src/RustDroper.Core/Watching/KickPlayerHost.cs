namespace RustDroper.Core.Watching;

/// <summary>Gizli Kick oynatıcısının anlık durumu.</summary>
public enum KickPlayerState
{
    /// <summary>Oynatıcı açık değil.</summary>
    Idle,

    /// <summary>Sayfa yükleniyor / oynatma henüz başlamadı.</summary>
    Starting,

    /// <summary>Video gerçekten oynuyor (zaman ilerliyor).</summary>
    Playing,

    /// <summary>Sayfa açık ama video oynamıyor (duraklatılmış, tamponlama, donma).</summary>
    Stalled,

    /// <summary>Yayın kapalı (kanal canlı değil).</summary>
    Offline,

    /// <summary>Oynatıcı oturumu (kick.com girişi) yok: izleme kredisi verilemez.</summary>
    LoginRequired,

    /// <summary>Oynatıcı başlatılamadı/çöktü (ağ, sayfa hatası vb.); <see cref="KickPlayerStatus.Detail"/>'da neden.</summary>
    Failed,

    /// <summary>Oynatıcı ana bilgisayarı bu makinede çalışamaz (ör. WebView2 Runtime yok); yeniden denemek anlamsız.</summary>
    Unavailable,
}

/// <summary>Oynatıcının anlık durumu; <see cref="Slug"/> hangi kanal için olduğunu söyler. Hiçbir kimlik bilgisi taşımaz.</summary>
public sealed record KickPlayerStatus(KickPlayerState State, string? Slug = null, string? Detail = null, int? VideoHeight = null)
{
    public static KickPlayerStatus Idle { get; } = new(KickPlayerState.Idle);
}

/// <summary>
/// Kick'in gerçek web oynatıcısını (gizli bir tarayıcı penceresinde) çalıştıran ana bilgisayar. Core yalnızca bu sözleşmeyi bilir;
/// uygulama (WebView2) App projesindedir. <b>Tek örnek:</b> aynı anda tek kanal oynatılır; kanal değişince aynı örnek yeniden kullanılır.
/// Hiçbir yöntem istisna fırlatmamalıdır (iptal hariç): sonuç <see cref="Status"/>'ta görünür.
/// </summary>
public interface IKickPlayerHost
{
    /// <summary>Anlık durum (iş parçacığı güvenli okunabilir).</summary>
    KickPlayerStatus Status { get; }

    /// <summary>Durum değişince tetiklenir (herhangi bir iş parçacığında).</summary>
    event Action<KickPlayerStatus>? StatusChanged;

    /// <summary>
    /// Kanal için oynatıcıyı başlatır (açık pencere varsa yeniden kullanır). Döndüğünde <see cref="Status"/> bu kanal için en az
    /// <see cref="KickPlayerState.Starting"/>'tir; oynatmanın başlaması beklenmez (durum olaylarla ilerler).
    /// </summary>
    Task OpenAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Aynı kanal için sayfayı yeniden yükler (donma/ağ kopması sonrası).</summary>
    Task RestartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Oynatmayı derhal durdurur (izleme kredisi biter) ve pencereyi yok eder. Kanal geçişinde yeniden kullanım için
    /// uygulama pencereyi kısa bir süre ayakta tutabilir; ama video oynamaya devam etmemelidir.
    /// </summary>
    Task ReleaseAsync();
}

/// <summary>Oynatıcı tarayıcı profilinin Kick oturumu durumu.</summary>
public enum KickPlayerSessionState
{
    /// <summary>Henüz doğrulanmadı.</summary>
    Unknown,

    /// <summary>Doğrulama sürüyor.</summary>
    Checking,

    /// <summary>Profil kick.com'da giriş yapmış görünüyor.</summary>
    LoggedIn,

    /// <summary>Profil kick.com'da giriş yapmamış.</summary>
    LoggedOut,

    /// <summary>Oynatıcı bileşeni (WebView2) bu makinede yok.</summary>
    Unavailable,
}

public sealed record KickPlayerSessionStatus(KickPlayerSessionState State, string? Detail = null)
{
    public static KickPlayerSessionStatus Unknown { get; } = new(KickPlayerSessionState.Unknown);
}

/// <summary>
/// Oynatıcı tarayıcı profilinin (kalıcı çerezler) Kick oturumu. Giriş <b>yalnızca kullanıcı tarafından</b>, görünür bir pencerede yapılır;
/// uygulama hiçbir kimlik bilgisini okumaz, saklamaz, günlüğe yazmaz.
/// </summary>
public interface IKickPlayerSession
{
    KickPlayerSessionStatus Status { get; }

    event Action<KickPlayerSessionStatus>? StatusChanged;

    /// <summary>Oturum durumunu yoklar (gerekirse kısa süreli gizli bir tarayıcı açılır). İstisna fırlatmaz.</summary>
    Task<KickPlayerSessionStatus> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Görünür giriş penceresini açar; pencere kapanınca durum yenilenir. Hiçbir şey otomatik girilmez.</summary>
    Task LoginAsync(CancellationToken cancellationToken);
}
