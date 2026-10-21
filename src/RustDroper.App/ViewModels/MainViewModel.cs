using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.App.Infrastructure;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Localization;
using RustDroper.Core.Media;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Watching;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App.ViewModels;

/// <summary>
/// Ana pencerenin ve tepsinin ortak görünüm modeli; pencere kapanıp yok edilse de yaşar. İş mantığı <see cref="DropperService"/>'tedir:
/// burası yalnızca onun görüntüsünü (<see cref="DropperSnapshot"/>) arayüz iş parçacığına taşır ve komutları ona iletir.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(8);

    private readonly SettingsService _settings;
    private readonly IStartNoticePrompt? _startNotice;
    private readonly DropperService _service;
    private readonly DropperService? _kick;
    private readonly IKickPlayerSession? _kickPlayer;
    private readonly Action<string>? _clipboard;
    private readonly IUiDispatcher _ui;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private int _updatePending;
    private bool _showSettings;
    private bool _isWindowVisible;
    private string _trayTooltip = "Rust Droper";
    private string _startPauseLabel = Loc.T("main.start");
    private string? _notice;
    private string? _twitchCommandError;
    private string? _kickCommandError;
    private bool _isRunning;
    private bool _twitchAttention;
    private bool _kickAttention;
    private bool _showHealth;
    private bool _showAbout;
    private bool _showReleaseNotes;
    private IReadOnlyList<HealthItem> _healthItems = [];
    private string _healthSummary = string.Empty;
    private HealthLevel _healthOverall;
    private DropperSnapshot _latest;
    private DropperSnapshot? _kickLatest;
    private Platform _selectedPlatform = Platform.Twitch;
    private string? _kickBlocked;

    public MainViewModel(
        SettingsService settings,
        IAutostart autostart,
        IBrowserLauncher browser,
        DropperService service,
        IUiDispatcher ui,
        TimeProvider? time = null,
        TimeZoneInfo? zone = null,
        ILogger? logger = null,
        DropperService? kickService = null,
        Action<string>? clipboard = null,
        IKickPlayerSession? kickPlayer = null,
        IDropMediaProvider? media = null,
        IStartNoticePrompt? startNotice = null)
    {
        _startNotice = startNotice;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(autostart);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(ui);
        _settings = settings;
        _service = service;
        _ui = ui;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _latest = service.Snapshot;
        _kick = kickService;
        _kickPlayer = kickService is null ? null : kickPlayer;
        _clipboard = clipboard;
        _kickLatest = kickService?.Snapshot;
        if (kickService is not null && string.Equals(settings.Current.LastPlatform, PlatformNames.Kick, StringComparison.Ordinal))
        {
            _selectedPlatform = Platform.Kick; // son seçilen ekran (ayar dosyasında yoksa Twitch)
        }

        Settings = new SettingsViewModel(settings, autostart);
        Dashboard = new DashboardViewModel(browser, _time, zone, media)
        {
            // yayıncı drop'ları öncelik sırası: platform başına ayrı liste, AppSettings'te kalıcı
            PriorityProvider = () => _selectedPlatform == Platform.Kick ? settings.Current.KickStreamerPriority : settings.Current.TwitchStreamerPriority,
            PriorityChanged = list => settings.Update(s => _selectedPlatform == Platform.Kick ? s with { KickStreamerPriority = list } : s with { TwitchStreamerPriority = list }),
        };
        if (media is not null)
        {
            media.Changed += OnServiceChanged; // katalog/görsel güncellendi → pano yeniden kurulur (UI iş parçacığına kuyruklanır)
            Dashboard.ImagesReady += OnServiceChanged;
        }

        LoginCommand = Guarded(new RelayCommand(_ => _service.LoginAsync()), Platform.Twitch);
        LogoutCommand = Guarded(new RelayCommand(_ => _service.LogoutAsync()), Platform.Twitch);
        CancelLoginCommand = new RelayCommand(_ => _service.CancelLogin());
        RetryCommand = Guarded(new RelayCommand(_ => _service.RetryRestoreAsync()), Platform.Twitch);
        RefreshCommand = Guarded(new RelayCommand(_ => RefreshAllAsync(), _ => AnyLoggedIn));
        StartPauseCommand = Guarded(new RelayCommand(_ => StartOrPauseAsync(), _ => AnyRunning || AnyLoggedIn));
        SelectPlatformCommand = new RelayCommand(p =>
        {
            ShowSettings = false; // geçişten bir platform seçmek Ayarlar/Sağlık/Hakkında/Güncelleme notları sayfasını kapatır
            ShowHealth = false;
            ShowAbout = false;
            ShowReleaseNotes = false;
            SelectedPlatform = p is Platform platform ? platform : p is string text && Enum.TryParse<Platform>(text, out var parsed) ? parsed : _selectedPlatform;
        });
        ToggleSettingsCommand = new RelayCommand(_ =>
        {
            ShowHealth = false;
            ShowAbout = false;
            ShowReleaseNotes = false;
            ShowSettings = !ShowSettings;
        });
        ToggleHealthCommand = new RelayCommand(_ =>
        {
            ShowSettings = false;
            ShowAbout = false;
            ShowReleaseNotes = false;
            ShowHealth = !ShowHealth;
        });
        ToggleAboutCommand = new RelayCommand(_ =>
        {
            ShowSettings = false;
            ShowHealth = false;
            ShowReleaseNotes = false;
            ShowAbout = !ShowAbout;
        });
        ToggleReleaseNotesCommand = new RelayCommand(_ =>
        {
            ShowSettings = false;
            ShowHealth = false;
            ShowAbout = false;
            ShowReleaseNotes = !ShowReleaseNotes;
        });
        CheckHealthCommand = new RelayCommand(async _ =>
        {
            if (AnyLoggedIn)
            {
                await RefreshAllAsync().ConfigureAwait(true);
            }

            RecomputeHealth();
        });
        Twitch = new PlatformCardViewModel("Twitch", LoginCommand, LogoutCommand, CancelLoginCommand, RetryCommand, clipboard: _clipboard);
        if (kickService is null)
        {
            Kick = new PlatformCardViewModel("Kick", Loc.T("card.comingSoon"));
        }
        else
        {
            var kickLogin = Guarded(new RelayCommand(_ => kickService.LoginAsync()), Platform.Kick);
            var kickLogout = Guarded(new RelayCommand(_ => kickService.LogoutAsync()), Platform.Kick);
            var kickCancel = new RelayCommand(_ => kickService.CancelLogin());
            var kickRetry = Guarded(new RelayCommand(_ => kickService.RetryRestoreAsync()), Platform.Kick);
            var player = _kickPlayer;
            var playerLogin = player is null ? null : Guarded(new RelayCommand(_ => player.LoginAsync(CancellationToken.None)), Platform.Kick);
            var playerRefresh = player is null ? null : Guarded(new RelayCommand(_ => player.RefreshAsync(CancellationToken.None)), Platform.Kick);
            Kick = new PlatformCardViewModel("Kick", kickLogin, kickLogout, kickCancel, kickRetry, Platform.Kick, _clipboard, playerLogin, playerRefresh);
            kickService.Changed += OnServiceChanged;
            if (player is not null)
            {
                player.StatusChanged += _ => OnServiceChanged();
            }
        }

        _service.Changed += OnServiceChanged;
        _service.DeviceCodeIssued += OnDeviceCodeIssued;
        settings.Changed += _ => OnServiceChanged(); // "Otomatik claim kapalı" uyarısı ayar değişince güncellensin
        ObserveLanguage();
        Apply();
    }

    /// <summary>Arayüz dili değişti: tüm yerelleştirilmiş özellikler yeniden okunur, pano ve sağlık metinleri yeniden kurulur.</summary>
    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Apply();
        if (_showHealth)
        {
            RecomputeHealth();
        }
    }

    public SettingsViewModel Settings { get; }

    public DashboardViewModel Dashboard { get; }

    public PlatformCardViewModel Twitch { get; }

    public PlatformCardViewModel Kick { get; }

    /// <summary>Kick servisi bağlıysa <c>true</c> (pano platform seçici gösterilir).</summary>
    public bool HasKick => _kick is not null;

    /// <summary>Panonun hangi platformu gösterdiği (Twitch/Kick).</summary>
    public Platform SelectedPlatform
    {
        get => _selectedPlatform;
        set
        {
            if (value == Platform.Kick && _kick is null)
            {
                value = Platform.Twitch;
            }

            if (SetProperty(ref _selectedPlatform, value))
            {
                OnPropertyChanged(nameof(IsTwitchSelected));
                OnPropertyChanged(nameof(IsKickSelected));
                OnPropertyChanged(nameof(SelectedCard));
                OnPropertyChanged(nameof(TwitchAttentionDot));
                OnPropertyChanged(nameof(KickAttentionDot));
                _settings.Update(s => s with { LastPlatform = value == Platform.Kick ? PlatformNames.Kick : PlatformNames.Twitch });
                Apply();
            }
        }
    }

    /// <summary>Seçili platformun kartı (ekranda yalnız o gösterilir).</summary>
    public PlatformCardViewModel SelectedCard => _selectedPlatform == Platform.Kick ? Kick : Twitch;

    public bool IsTwitchSelected => _selectedPlatform == Platform.Twitch;

    public bool IsKickSelected => _selectedPlatform == Platform.Kick;

    /// <summary>Parametre: <see cref="Platform"/> ya da adı ("Twitch"/"Kick").</summary>
    public RelayCommand SelectPlatformCommand { get; }

    public RelayCommand ToggleSettingsCommand { get; }

    public RelayCommand StartPauseCommand { get; }

    public RelayCommand LoginCommand { get; }

    public RelayCommand LogoutCommand { get; }

    public RelayCommand CancelLoginCommand { get; }

    public RelayCommand RetryCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public bool ShowSettings
    {
        get => _showSettings;
        set
        {
            if (SetProperty(ref _showSettings, value))
            {
                OnPropertyChanged(nameof(SettingsButtonLabel));
            }
        }
    }

    public string SettingsButtonLabel => Loc.T(ShowSettings ? "main.back" : "main.settings");

    public RelayCommand ToggleAboutCommand { get; }

    /// <summary>"Hakkında" sayfası (Ayarlar'daki düğmeden açılır).</summary>
    public AboutViewModel About { get; } = new();

    public bool ShowAbout
    {
        get => _showAbout;
        set => SetProperty(ref _showAbout, value);
    }

    public RelayCommand ToggleReleaseNotesCommand { get; }

    /// <summary>"Güncelleme notları" sayfası (sürüm kartları; veri yalnız sayfa açıkken yüklenir).</summary>
    public ReleaseNotesViewModel ReleaseNotes { get; } = new();

    public bool ShowReleaseNotes
    {
        get => _showReleaseNotes;
        set
        {
            if (SetProperty(ref _showReleaseNotes, value))
            {
                OnPropertyChanged(nameof(ReleaseNotesButtonLabel));
                if (value)
                {
                    ReleaseNotes.Load();
                }
                else
                {
                    ReleaseNotes.Unload();
                }
            }
        }
    }

    public string ReleaseNotesButtonLabel => Loc.T(ShowReleaseNotes ? "main.back" : "main.releaseNotes.tip");

    public RelayCommand ToggleHealthCommand { get; }

    public RelayCommand CheckHealthCommand { get; }

    /// <summary>Sağlık kontrolü sayfası açık mı (pano/ayarlar yerine).</summary>
    public bool ShowHealth
    {
        get => _showHealth;
        set
        {
            if (SetProperty(ref _showHealth, value))
            {
                OnPropertyChanged(nameof(HealthButtonLabel));
                if (value)
                {
                    RecomputeHealth();
                }
            }
        }
    }

    public string HealthButtonLabel => Loc.T(ShowHealth ? "main.back" : "main.health");

    /// <summary>Seçili platformun sağlık maddeleri (diğer platformdan söz etmez).</summary>
    public IReadOnlyList<HealthItem> HealthItems
    {
        get => _healthItems;
        private set => SetProperty(ref _healthItems, value);
    }

    /// <summary>Tek satır özet: "Hazır" / "N uyarı" / "N sorun".</summary>
    public string HealthSummary
    {
        get => _healthSummary;
        private set => SetProperty(ref _healthSummary, value);
    }

    public HealthLevel HealthOverall
    {
        get => _healthOverall;
        private set => SetProperty(ref _healthOverall, value);
    }

    /// <summary>Gözcü (çökme koruması) bu çalıştırmada kurulabilir mi (yalnız gerçek taşınabilir exe; App ayarlar).</summary>
    public bool CrashProtectionAvailable { get; set; }

    private void RecomputeHealth()
    {
        var kick = _selectedPlatform == Platform.Kick && _kickLatest is not null;
        var items = HealthChecker.Build(new HealthInput(
            kick ? Platform.Kick : Platform.Twitch,
            kick ? _kickLatest! : _latest,
            _settings.Current,
            CrashProtectionAvailable,
            kick ? _kickPlayer?.Status : null));
        HealthItems = items;
        HealthOverall = HealthChecker.Overall(items);
        var fails = items.Count(i => i.Level == HealthLevel.Fail);
        var warns = items.Count(i => i.Level == HealthLevel.Warn);
        HealthSummary = fails > 0 ? Loc.T("health.summary.fail", fails, warns) : warns > 0 ? Loc.T("health.summary.warn", warns) : Loc.T("health.summary.ok");
    }

    /// <summary>"Başlat" ya da "Duraklat" (motor çalışıyorsa).</summary>
    public string StartPauseLabel
    {
        get => _startPauseLabel;
        private set => SetProperty(ref _startPauseLabel, value);
    }

    public bool CanStartPause => StartPauseCommand.CanExecute(null);

    /// <summary>Herhangi bir platformda motor çalışıyor (Başlat/Duraklat ikonu için).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>Twitch'te dikkat gerektiren durum var (geçişte diğer segmentte metinsiz nötr nokta).</summary>
    public bool TwitchAttention
    {
        get => _twitchAttention;
        private set
        {
            if (SetProperty(ref _twitchAttention, value))
            {
                OnPropertyChanged(nameof(TwitchAttentionDot));
            }
        }
    }

    /// <summary>Geçişte Twitch segmentinde metinsiz nokta: Twitch'te dikkat gerekiyor VE o ekran açık değil.</summary>
    public bool TwitchAttentionDot => TwitchAttention && !IsTwitchSelected;

    /// <summary>Geçişte Kick segmentinde metinsiz nokta: Kick'te dikkat gerekiyor VE o ekran açık değil.</summary>
    public bool KickAttentionDot => KickAttention && !IsKickSelected;

    /// <summary>Kick'te dikkat gerektiren durum var (geçişte diğer segmentte metinsiz nötr nokta).</summary>
    public bool KickAttention
    {
        get => _kickAttention;
        private set
        {
            if (SetProperty(ref _kickAttention, value))
            {
                OnPropertyChanged(nameof(KickAttentionDot));
            }
        }
    }

    /// <summary>Tepsi simgesi ipucu (≤127 karakter): hesap/motor durumu ve canlı ilerleme.</summary>
    public string TrayTooltip
    {
        get => _trayTooltip;
        private set => SetProperty(ref _trayTooltip, value);
    }

    /// <summary>
    /// Seçili platformun ekranındaki uyarı/hata notu (veriler yenilenemedi, kimlik hatası, "Otomatik claim KAPALI"...).
    /// Diğer platformun uyarıları burada ASLA görünmez; yalnızca geçişte metinsiz nokta olarak (<see cref="TwitchAttention"/>/<see cref="KickAttention"/>).
    /// </summary>
    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    /// <summary>Pencere açıkken <c>true</c>. Kapalıyken pano listeleri yeniden kurulmaz (CPU/RAM tasarrufu).</summary>
    public bool IsWindowVisible
    {
        get => _isWindowVisible;
        set
        {
            if (SetProperty(ref _isWindowVisible, value) && value)
            {
                Apply();
            }
        }
    }

    /// <summary>Başlangıçta: kayıtlı oturumu doğrula + verileri yükle (hataları yutar, günlüğe yazar).</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var initializations = new List<Task> { _service.InitializeAsync() };
            if (_kick is not null)
            {
                initializations.Add(_kick.InitializeAsync());
            }

            await Task.WhenAll(initializations).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Başlangıç yüklemesi başarısız");
        }
    }

    /// <summary>Uygulama çıkarken: motoru temiz durdurur (zaman aşımıyla), servisi kapatır.</summary>
    public async Task ShutdownAsync()
    {
        _service.Changed -= OnServiceChanged;
        if (_kick is not null)
        {
            _kick.Changed -= OnServiceChanged;
        }

        try
        {
            var stops = new List<Task> { _service.StopAsync() };
            if (_kick is not null)
            {
                stops.Add(_kick.StopAsync());
            }

            await Task.WhenAll(stops).WaitAsync(ShutdownWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Motor {Seconds} sn içinde durmadı; kapatılıyor.", ShutdownWait.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Motor durdurulurken hata");
        }
    }

    /// <summary>
    /// İki platformlu ipucu: Kick'e giriş yapılmışsa ya da çalışıyorsa "Twitch: … · Kick: …" (≤127 karakter); aksi halde yalnız Twitch'inki.
    /// </summary>
    internal static string BuildTooltip(DropperSnapshot twitch, DropperSnapshot? kick)
    {
        const string prefix = "Rust Droper — ";
        if (kick is null || !twitch.NetworkOnline || (kick.Account.Status is AccountStatus.LoggedOut or AccountStatus.Unknown or AccountStatus.Restoring && !kick.IsRunning))
        {
            return BuildTooltip(twitch);
        }

        static string Short(string tooltip) => tooltip.StartsWith(prefix, StringComparison.Ordinal) ? tooltip[prefix.Length..] : tooltip;
        var combined = $"{prefix}Twitch: {Short(BuildTooltip(twitch, "Twitch"))} · Kick: {Short(BuildTooltip(kick, "Kick"))}";
        return combined.Length <= 127 ? combined : combined[..126] + "…";
    }

    internal static string BuildTooltip(DropperSnapshot snapshot) => BuildTooltip(snapshot, "Twitch");

    private static string BuildTooltip(DropperSnapshot snapshot, string platformName)
    {
        const string name = "Rust Droper";
        if (!snapshot.NetworkOnline)
        {
            return $"{name} — {Loc.T("tip.offline")}";
        }

        var account = snapshot.Account.Status;
        if (account != AccountStatus.LoggedIn)
        {
            return account switch
            {
                AccountStatus.LoggingIn => $"{name} — {Loc.T("tip.loggingIn")}",
                AccountStatus.NeedsLogin => $"{name} — {Loc.T("tip.needsLogin")}",
                AccountStatus.Offline => $"{name} — {Loc.T("tip.accountOffline")}",
                AccountStatus.LoggedOut => $"{name} — {Loc.T("tip.loggedOut", platformName)}",
                _ => name,
            };
        }

        if (!snapshot.IsRunning)
        {
            return $"{name} — {Loc.T("tip.paused", snapshot.Account.Login)}";
        }

        var engine = snapshot.Engine;
        if (engine.State == EngineState.Watching && engine.Target is { } target)
        {
            var progress = engine.Session is { } s ? " " + Loc.T("tip.progress", s.CurrentMinutes, s.RequiredMinutes, s.Percent) : string.Empty;
            return $"{name} — {target.Channel.DisplayName}:{progress}";
        }

        var (badge, _) = Fmt.EngineBadge(engine.State, running: true);
        return $"{name} — {badge}";
    }

    private IEnumerable<DropperService> Services => _kick is null ? [_service] : [_service, _kick];

    private bool AnyRunning => Services.Any(s => s.IsRunning);

    private bool AnyLoggedIn => _latest.Account.Status == AccountStatus.LoggedIn || _kickLatest?.Account.Status == AccountStatus.LoggedIn;

    /// <summary>Çalışan varsa hepsini durdurur; yoksa giriş yapılmış tüm platformlarda motoru başlatır (Twitch ve Kick bağımsız, paralel).</summary>
    private async Task StartOrPauseAsync()
    {
        if (AnyRunning)
        {
            await Task.WhenAll(Services.Select(s => RunForAsync(s, () => s.StopAsync()))).ConfigureAwait(true);
        }
        else
        {
            await ShowStartNoticeAsync().ConfigureAwait(true);
            _kickBlocked = null;
            foreach (var service in Services)
            {
                if (ReferenceEquals(service, _kick) && await KickPlayerBlockReasonAsync().ConfigureAwait(true) is { } reason)
                {
                    // Kick'te izleme kredisini yalnızca oturum açmış gerçek oynatıcı verir: oturum yokken motoru boşuna başlatma.
                    _kickBlocked = reason;
                    Apply();
                    continue;
                }

                service.Start();
            }
        }
    }

    /// <summary>Beklenmedik kapanıştan sonra: bilgi penceresi göstermeden, giriş yapılmış ve çalışmayan platformlarda motoru başlatır.</summary>
    public async Task ResumeAsync()
    {
        // Bilgisayar yeni açıldıysa ağ/oturum doğrulaması gecikebilir: giriş görünene dek (en çok 5 dk) bekle.
        for (var i = 0; i < ResumeLoginAttempts && !AnyLoggedIn && !AnyRunning; i++)
        {
            await Task.Delay(ResumeLoginPoll).ConfigureAwait(true);
        }

        if (AnyRunning || !AnyLoggedIn)
        {
            return;
        }

        _kickBlocked = null;
        foreach (var service in Services)
        {
            if (ReferenceEquals(service, _kick) && await KickPlayerBlockReasonAsync().ConfigureAwait(true) is { } reason)
            {
                _kickBlocked = reason;
                Apply();
                continue;
            }

            service.Start();
        }
    }

    internal static int ResumeLoginAttempts { get; set; } = 30;

    internal static TimeSpan ResumeLoginPoll { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Başlat'a basılınca kısa bilgi penceresi (aynı hesapla başka canlı yayın izlemeyin): yalnız pencere açıkken, ayarla kapatılmadıysa. "Bir daha gösterme" kalıcıdır.
    /// </summary>
    private async Task ShowStartNoticeAsync()
    {
        if (_startNotice is null || !IsWindowVisible || _settings.Current.HideStartNotice)
        {
            return;
        }

        var answer = await _startNotice.ShowAsync().ConfigureAwait(true);
        if (answer.DontShowAgain)
        {
            _settings.Update(s => s with { HideStartNotice = true });
        }
    }

    /// <summary>Kick'i başlatmayı engelleyen oynatıcı oturumu sorunu (yoksa <c>null</c>). Doğrulanmamışsa önce kısa bir yoklama yapılır.</summary>
    private async Task<string?> KickPlayerBlockReasonAsync()
    {
        if (_kick is null || _kickPlayer is null || _kick.Snapshot.Account.Status != AccountStatus.LoggedIn)
        {
            return null; // Kick'e giriş yoksa servis zaten başlamaz; oynatıcı yoksa (CLI/test) eski davranış
        }

        var status = _kickPlayer.Status;
        if (status.State == KickPlayerSessionState.Unknown)
        {
            status = await _kickPlayer.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }

        return status.State switch
        {
            KickPlayerSessionState.LoggedOut => Loc.T("main.kickBlocked.loggedOut"),
            KickPlayerSessionState.Unavailable => Loc.T("main.kickBlocked.unavailable"),
            _ => null,
        };
    }

    private Task RefreshAllAsync() => Task.WhenAll(Services.Select(s => RunForAsync(s, () => s.RefreshAsync())));

    /// <summary>Servis komutunu çalıştırır; hata o servisin platformuna yazılır (diğer platformun ekranında görünmez).</summary>
    private async Task RunForAsync(DropperService service, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportCommandFailure(ReferenceEquals(service, _kick) ? Platform.Kick : Platform.Twitch, ex);
        }
    }

    private RelayCommand Guarded(RelayCommand command, Platform? platform = null)
    {
        command.Failed += ex => ReportCommandFailure(platform ?? _selectedPlatform, ex);
        return command;
    }

    private void ReportCommandFailure(Platform platform, Exception ex)
    {
        _logger.LogError(ex, "Komut başarısız ({Platform})", platform);
        var text = Loc.T("main.commandFailed", ex.Message);
        if (platform == Platform.Kick)
        {
            _kickCommandError = text;
        }
        else
        {
            _twitchCommandError = text;
        }

        Notice = NoticeForPlatform(_selectedPlatform);
    }

    private void OnDeviceCodeIssued(TwitchDeviceCode code) => _browser.Open(code.VerificationUri);

    private void OnServiceChanged()
    {
        // Birçok olay art arda gelebilir: arayüz iş parçacığına tek bir güncelleme kuyruğa alınır.
        if (Interlocked.Exchange(ref _updatePending, 1) == 0)
        {
            _ui.Post(() =>
            {
                Interlocked.Exchange(ref _updatePending, 0);
                Apply();
            });
        }
    }

    private void Apply()
    {
        var snapshot = _service.Snapshot;
        _latest = snapshot;
        var kick = _kick?.Snapshot;
        _kickLatest = kick;

        Twitch.Update(snapshot.Account);
        if (kick is not null)
        {
            Kick.Update(kick.Account);
            if (_kickPlayer is not null)
            {
                Kick.UpdatePlayerSession(_kickPlayer.Status);
                if (_kickBlocked is not null && (kick.IsRunning || _kickPlayer.Status.State == KickPlayerSessionState.LoggedIn))
                {
                    _kickBlocked = null; // sorun giderildi / Kick başladı
                }
            }
        }

        _twitchCommandError = null; // geçici komut hataları bir sonraki görüntü güncellemesinde silinir
        _kickCommandError = null;
        IsRunning = snapshot.IsRunning || kick?.IsRunning == true;
        StartPauseLabel = Loc.T(IsRunning ? "main.pause" : "main.start");
        TrayTooltip = BuildTooltip(snapshot, kick);
        Notice = NoticeForPlatform(_selectedPlatform);
        TwitchAttention = NeedsAttention(snapshot, "Twitch", blocked: null);
        KickAttention = kick is not null && NeedsAttention(kick, "Kick", _kickBlocked);
        StartPauseCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartPause));

        if (_showHealth)
        {
            RecomputeHealth();
        }

        // Pencere kapalıyken liste/ilerleme çubukları kurulmaz; pencere açılınca (IsWindowVisible) tek seferde güncellenir.
        if (IsWindowVisible)
        {
            var shown = _selectedPlatform == Platform.Kick && kick is not null ? kick : snapshot;
            Dashboard.Update(new DashboardInput(
                shown.Engine,
                shown.IsRunning,
                shown.Campaigns,
                shown.Inventory,
                shown.ClaimedDropIds,
                _time.GetUtcNow(),
                _selectedPlatform == Platform.Kick && kick is not null ? Platform.Kick : Platform.Twitch));
        }
    }

    /// <summary>Seçili platformun ekranındaki uyarı: yalnız o platformun servis durumu, komut hataları ve claim uyarısı.</summary>
    private string? NoticeForPlatform(Platform platform) => platform == Platform.Kick && _kickLatest is { } kick
        ? _kickCommandError ?? NoticeFor(kick, "Kick", prefix: true) ?? _kickBlocked ?? ClaimOffNotice(Settings.AutoClaim, kick)
        : _twitchCommandError ?? NoticeFor(_latest, "Twitch") ?? ClaimOffNotice(Settings.AutoClaim, _latest);

    /// <summary>Platformda kullanıcının bakması gereken bir durum var mı (hata/çevrimdışı/oturum reddi/giriş gerekli/engellenmiş başlatma). "Otomatik claim KAPALI" bir tercih olduğundan sayılmaz.</summary>
    private static bool NeedsAttention(DropperSnapshot snapshot, string platformName, string? blocked) =>
        blocked is not null
        || NoticeFor(snapshot, platformName) is not null
        || snapshot.Account.Status is AccountStatus.NeedsLogin;

    /// <summary>Motor çalışıp izlerken "Otomatik claim" kapalıysa kalıcı uyarı: unutulan ayar yüzünden drop kaçmasın. Platform başına: yalnız o platform izliyorsa gösterilir.</summary>
    internal static string? ClaimOffNotice(bool autoClaim, DropperSnapshot snapshot)
    {
        if (autoClaim)
        {
            return null;
        }

        static bool Working(DropperSnapshot s) => s.IsRunning && s.Engine.State is EngineState.Watching or EngineState.ClaimPending;
        return Working(snapshot)
            ? Loc.T("main.notice.claimOff")
            : null;
    }

    private static string? NoticeFor(DropperSnapshot snapshot, string platformName, bool prefix = false)
    {
        string? text = !snapshot.NetworkOnline
            ? Loc.T("main.notice.offline")
            : snapshot.RefreshError
                ?? (snapshot.Engine.State == EngineState.AuthRequired && snapshot.IsRunning ? Loc.T("main.notice.authRejected", platformName) : null);
        return text is not null && prefix && snapshot.NetworkOnline && !text.StartsWith(platformName, StringComparison.Ordinal) ? $"{platformName}: {text}" : text;
    }
}
