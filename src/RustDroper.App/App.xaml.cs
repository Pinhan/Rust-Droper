using System.Windows;
using Microsoft.Extensions.Logging;
using RustDroper.App.Infrastructure;
using RustDroper.App.Player;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Domain;
using RustDroper.Core.Media;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Kick;
using RustDroper.Core.Platforms.Twitch;

namespace RustDroper.App;

public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private RollingFileLoggerProvider? _logs;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private AppShell? _shell;
    private MainViewModel? _viewModel;
    private DropperService? _service;
    private DropperService? _kickService;
    private HttpClient? _kickHttp;
    private WebViewKickPlayerHost? _player;
    private WindowsSystemEvents? _systemEvents;
    private NotificationCoordinator? _notifications;
    private KeepAwakeCoordinator? _keepAwake;
    private MemoryMonitor? _memory;
    private HttpClient? _http;
    private HttpClient? _probeHttp;
    private HttpClient? _mediaHttp;
    private FacepunchMediaService? _media;
    private bool _exiting;
    private bool _sessionEnding;
    private KeepAliveCoordinator? _keepAlive;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Test süreçleri (görsel önizleme) `new App()` yaratır; gerçek açılış (tepsi, tek örnek kilidi, veri klasörü) çalışmamalı.
        if (Environment.GetEnvironmentVariable("RUSTDROPER_SKIP_STARTUP") == "1")
        {
            return;
        }

        // Yazılım çizimi (varsayılan): arayüz basit olduğundan (koyu tema, ilerleme çubukları) GPU'ya ihtiyaç yok. Ölçüm (docs/02 §2): donanım çizimi
        // GPU sürücüsü/D3D yüzünden tepside ~110 MB özel bellek tutuyor ve her pencere aç/kapa'da ~6 MB sızdırıyor; yazılım çiziminde ~53 MB ve sızıntı yok.
        // Gerekirse RUSTDROPER_RENDER=hardware ile eski davranışa dönülür.
        if (!string.Equals(Environment.GetEnvironmentVariable("RUSTDROPER_RENDER"), "hardware", StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        var paths = AppPaths.ForPortable();
        paths.EnsureCreated();

        var keepAliveLaunch = e.Args.Contains(StartupPlanner.KeepAliveArgument, StringComparer.OrdinalIgnoreCase);
        var runStateStore = new RunStateStore(Path.Combine(paths.DataDirectory, "run-state.json"));
        var priorState = runStateStore.Load();
        var startupMode = StartupPlanner.Decide(keepAliveLaunch, priorState);
        if (keepAliveLaunch)
        {
            KeepAliveLog.Append(Path.Combine(paths.DataDirectory, "keepalive.log"), $"gözcü başlatması: önceki durum={priorState?.ToString() ?? "yok"} karar={startupMode} pid={Environment.ProcessId} okumaHatası={runStateStore.LastLoadError ?? "-"}");
        }

        _guard = new SingleInstanceGuard(SingleInstanceGuard.KeyFor(paths.DataDirectory));
        if (!_guard.TryAcquire())
        {
            // Zaten çalışıyor: gözcü başlattıysa sessizce çık; kullanıcı başlattıysa çalışan örneğin penceresini öne getirt.
            if (!keepAliveLaunch)
            {
                _guard.SignalExisting();
            }
            else
            {
                KeepAliveLog.Append(Path.Combine(paths.DataDirectory, "keepalive.log"), "çalışan örnek var: sessizce çıkılıyor");
            }

            _guard.Dispose();
            _guard = null;
            Shutdown();
            return;
        }

        if (startupMode == StartupMode.ExitSilently)
        {
            // Gözcü görevi: çalışan örnek yok ama yeniden başlatılacak bir şey de yok (motor çalışmıyordu ya da kullanıcı çıktı).
            _guard.Dispose();
            _guard = null;
            Shutdown();
            return;
        }

        _logs = new RollingFileLoggerProvider(paths.LogsDirectory);
        _logger = _logs.CreateLogger("app");
        HookGlobalExceptionHandlers();

        if (paths.Migration is { } migration)
        {
            _logger.LogInformation(
                "Eski veri klasörü yeni konuma taşındı ({Source}): {Moved} öğe taşındı, {Skipped} atlandı, {Failed} taşınamadı.",
                migration.Source, migration.Moved, migration.Skipped.Count, migration.Failed.Count);
        }

        var settings = new SettingsService(paths.SettingsFile);
        if (settings.LoadWarning is { } warning)
        {
            _logger.LogWarning("Ayarlar: {Warning}", warning);
        }

        // Arayüz dili: ayar "auto" ise Windows görüntü dili (Türkçe → Türkçe, aksi halde İngilizce). Ayarlar'dan değişince anında uygulanır.
        RustDroper.Core.Localization.Loc.Configure(settings.Current.Language);

        // Çökme koruması: yalnız gerçek (taşınabilir exe, RUSTDROPER_DATA yok) çalıştırmada zamanlanmış görev kurulur; sınama/geliştirme koşuları sisteme dokunmaz.
        var productionRun = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUSTDROPER_DATA")) && Environment.ProcessPath is not null
            && string.Equals(Path.GetFileName(Environment.ProcessPath), "RustDroper.exe", StringComparison.OrdinalIgnoreCase);
        _keepAlive = new KeepAliveCoordinator(
            runStateStore,
            new SchtasksKeepAliveTask(_logger),
            () => settings.Current.AutoRestart && productionRun,
            Environment.ProcessPath ?? string.Empty,
            _logger);
        settings.Changed += _ => _keepAlive.OnSettingChanged();

        var autostart = RegistryAutostart.ForCurrentProcess();
        if (settings.Current.StartWithWindows)
        {
            autostart.Repair();
        }

        // Kurulum CLI'daki `run twitch` ile aynıdır (SMARTBOX profili, DPAPI'li sır deposu, claim geçmişi).
        var profile = TwitchClients.SmartboxTv;
        var secrets = new DpapiSecretStore(paths.SecretsFile);
        _http = TwitchConstants.CreateHttpClient(profile);
        var account = new TwitchSessionManager(new TwitchAuthClient(_http, profile), secrets, _logger);
        var history = new JsonLinesLog<ClaimRecord>(paths.HistoryFile); // tek örnek: Twitch ve Kick aynı dosyaya ardışık yazar
        var factory = new TwitchConnectionFactory(
            _http, profile, secrets, paths.HashesFile, history, logger: _logger,
            campaignFilter: campaign =>
                !settings.Current.RustRewardsOnly
                || !string.Equals(settings.Current.GameSlug, "rust", StringComparison.OrdinalIgnoreCase)
                || RustCampaignFilter.IsRust(campaign, RustCampaignFilter.KeysOf(_media?.Catalog(Platform.Twitch).Select(d => d.Name) ?? [])));
        _systemEvents = new WindowsSystemEvents(_logger);
        _probeHttp = HttpConnectivityProbe.CreateClient();
        _service = new DropperService(
            account,
            factory,
            settings,
            logger: _logger,
            stateStore: new StateStore(paths.StateFile, logger: _logger),
            systemEvents: _systemEvents,
            connectivityProbe: new HttpConnectivityProbe(_probeHttp, ProbeEndpointsFromEnvironment()));

        // Kick: ayrı servis/motor (Twitch'ten bağımsız, paralel). Giriş: telefondaki Kick uygulamasıyla QR (docs/PLATFORM-NOTES.md K6). Durum dosyası ayrı.
        _kickHttp = KickConstants.CreateHttpClient();
        var kickAccount = new KickAccount(new KickSessionManager(new KickAuthClient(_kickHttp), secrets, _logger));
        // Kick'te drop ilerlemesini yalnızca Kick'in gerçek web oynatıcısı verir (docs/PLATFORM-NOTES.md U3): gizli WebView2 penceresi (Tier B). WebView2 Runtime yoksa
        // ana bilgisayar "Unavailable" bildirir (çökme yok); profil data\webview2\'de, giriş yalnızca kullanıcı tarafından görünür pencerede yapılır.
        _player = new WebViewKickPlayerHost(
            Dispatcher,
            paths.WebView2Directory,
            Path.Combine(paths.DataDirectory, "player-session.json"),
            string.Equals(Environment.GetEnvironmentVariable("RUSTDROPER_PLAYER_MODE"), "visible", StringComparison.OrdinalIgnoreCase)
                ? PlayerWindowMode.Visible
                : PlayerWindowMode.OffScreen,
            logger: _logger,
            extraBrowserArguments: Environment.GetEnvironmentVariable("RUSTDROPER_PLAYER_ARGS"),
            allowGpu: Environment.GetEnvironmentVariable("RUSTDROPER_PLAYER_GPU") == "1");
        var kickFactory = new KickConnectionFactory(_kickHttp, history, () => settings.Current.GameSlug, logger: _logger, playerHost: _player);
        _kickService = new DropperService(
            kickAccount,
            kickFactory,
            settings,
            logger: _logger,
            stateStore: new StateStore(Path.Combine(paths.DataDirectory, "state-kick.json"), logger: _logger),
            systemEvents: _systemEvents,
            connectivityProbe: new HttpConnectivityProbe(_probeHttp, ProbeEndpointsFromEnvironment()),
            platform: PlatformProfile.Kick);
        var browser = new ShellBrowserLauncher(suppress: e.Args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase));
        var ui = new WpfUiDispatcher(Dispatcher);
        // Drop kartları: Facepunch sayfasından (açılışta + 6 saatte bir) drop adı/süre/görsel; diske ~20 MB'a kadar önbelleklenir, yalnız izinli HTTPS alan adları.
        _mediaHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _mediaHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        _media = new FacepunchMediaService(
            _mediaHttp,
            new DropMediaCache(paths.MediaDirectory, _mediaHttp, FacepunchMediaService.AllowedMediaHosts, logger: _logger),
            logger: _logger,
            kickPage: FacepunchMediaService.KickPage);
        _viewModel = new MainViewModel(settings, autostart, browser, _service, ui, logger: _logger, kickService: _kickService, clipboard: CopyToClipboard, kickPlayer: _player, media: _media, startNotice: new WpfStartNoticePrompt());
        _viewModel.CrashProtectionAvailable = productionRun;
        _viewModel.About.Configure(
            paths.DataDirectory,
            paths.LogsDirectory,
            CopyToClipboard,
            path => OpenFolder(path));
        var windows = new WindowController(() => new MainWindow { DataContext = _viewModel });
        _shell = new AppShell(_viewModel, windows);
        _shell.ExitRequested += () => _ = ExitAsync();

        // Bildirimler: servis olayı → süzgeç (ayar + tekrar önleme) → tepsi balonu. Balona tıklama: bağlantı varsa tarayıcıda aç, yoksa pencereyi göster.
        _notifications = new NotificationCoordinator(settings, _shell, ui, logger: _logger);
        _service.Notification += _notifications.Handle;
        _kickService.Notification += _notifications.Handle;
        _viewModel.Settings.TestNotificationRequested += _notifications.ShowTest;
        _shell.NotificationClicked += notification =>
        {
            if (notification.Link is { } link)
            {
                browser.Open(link);
            }
            else
            {
                _shell.ShowWindow();
            }
        };
        _guard.StartListening(() => Dispatcher.BeginInvoke(_shell.ShowWindow));

        // Tepsideyken 10 dk'da bir bellek kırpılır ve "bellek:" satırı günlüğe yazılır (RUSTDROPER_MEMORY_LOG_SECONDS ile ayarlanabilir; sınama).
        var memoryInterval = int.TryParse(Environment.GetEnvironmentVariable("RUSTDROPER_MEMORY_LOG_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(10);
        _memory = new MemoryMonitor(() => _viewModel.IsWindowVisible, memoryInterval, _logger);

        // Uyku engelleme: motor gerçekten iş yaparken (keşif/izleme/claim bekleme/toparlanma) sistem uykuya geçmesin; ayar KeepAwake.
        _keepAwake = new KeepAwakeCoordinator(new WindowsKeepAwake(ui), () => settings.Current.KeepAwake, _logger);
        void ReevaluateKeepAwake()
        {
            _keepAwake.Evaluate(_service.Snapshot, _kickService.Snapshot);
            _keepAlive.OnRunningChanged(_service.IsRunning || _kickService.IsRunning);
        }

        _service.Changed += ReevaluateKeepAwake;
        _kickService.Changed += ReevaluateKeepAwake;
        settings.Changed += _ => ReevaluateKeepAwake();

        SessionEnding += (_, _) =>
        {
            _sessionEnding = true; // Windows kapanıyor/oturum bitiyor: motor "çalışıyordu" olarak kalsın, açılışta devam etsin
            _ = ExitAsync();
        };

        if (!settings.Current.StartMinimized && startupMode != StartupMode.ResumeMinimized)
        {
            _shell.ShowWindow();
        }

        _media.Start();
        _ = StartAsync(startupMode == StartupMode.ResumeMinimized);

        _logger.LogInformation("Rust Droper başladı (veri: {Data}, tepsi simgesi: {Tray}).", paths.DataDirectory, _shell.TrayAdded ? "eklendi" : "EKLENEMEDİ");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keepAwake?.Shutdown();
        _memory?.Dispose();
        _shell?.Dispose();
        _service?.Dispose();
        _kickService?.Dispose();
        _player?.Dispose();
        _systemEvents?.Dispose();
        _media?.Dispose();
        _mediaHttp?.Dispose();
        _http?.Dispose();
        _kickHttp?.Dispose();
        _probeHttp?.Dispose();
        _guard?.Dispose();
        _logs?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Sınama kancası: <c>RUSTDROPER_PROBE_URLS</c> (';' ile ayrılmış) verilirse bağlantı yoklaması yalnız bu adreslere yapılır (yerel sahte sunucuyla "internet kesildi" denemesi).</summary>
    private static IReadOnlyList<HttpConnectivityProbe.Endpoint>? ProbeEndpointsFromEnvironment() =>
        Environment.GetEnvironmentVariable("RUSTDROPER_PROBE_URLS") is { Length: > 0 } urls
            ? [.. urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
                .Select(u => new HttpConnectivityProbe.Endpoint(new Uri(u), status => (int)status < 500))]
            : null;

    /// <summary>Klasörü dosya yöneticisinde açar (yalnız var olan klasör; hata sessizce yutulur).</summary>
    private static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Panoya metin koyar (Kick QR bağlantısı); pano başka bir süreç tarafından kilitliyse sessizce vazgeçer.</summary>
    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    private async Task StartAsync(bool resume)
    {
        await _viewModel!.InitializeAsync().ConfigureAwait(true);
        if (resume)
        {
            _logger.LogWarning("Önceki çalışma beklenmedik sonlanmış (çökme/kapanma): motor kendiliğinden yeniden başlatılıyor.");
            await _viewModel.ResumeAsync().ConfigureAwait(true);
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        if (_sessionEnding)
        {
            _keepAlive?.Freeze();
        }
        else
        {
            _keepAlive?.OnUserExit();
        }

        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.ShutdownAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kapanış sırasında hata");
        }
        finally
        {
            Shutdown();
        }
    }

    private void HookGlobalExceptionHandlers()
    {
        // Hiçbir istisna uygulamayı sessizce öldürmemeli: günlüğe yaz, arayüz iş parçacığındakini yut (tepsi uygulaması ayakta kalsın).
        DispatcherUnhandledException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Yakalanmamış arayüz istisnası");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _logger.LogCritical(args.ExceptionObject as Exception, "Yakalanmamış istisna (sonlanıyor: {Terminating})", args.IsTerminating);
            if (args.IsTerminating)
            {
                _keepAlive?.RelaunchAfterCrash();
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Gözlenmeyen görev istisnası");
            args.SetObserved();
        };
    }
}
