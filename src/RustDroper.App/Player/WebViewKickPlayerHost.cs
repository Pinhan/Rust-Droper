using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RustDroper.App.Infrastructure;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Watching;
using RustDroper.Core.Localization;

namespace RustDroper.App.Player;

/// <summary>Gizli oynatıcı penceresinin nerede/nasıl durduğu (sınama ve tanılama için; varsayılan <see cref="OffScreen"/>).</summary>
public enum PlayerWindowMode
{
    /// <summary>Ekran dışında (görev çubuğunda yok, odak almaz); sayfa "görünür" sayılsın diye pencere gerçekten gösterilir.</summary>
    OffScreen,

    /// <summary>Hata ayıklama: normal, ekranda görünür pencere.</summary>
    Visible,
}

/// <summary>
/// Kick'in <b>gerçek web oynatıcısını</b> (Tier B) gizli bir WebView2 penceresinde çalıştırır: sessiz (ses tarayıcı düzeyinde kapalı), en düşük kalite,
/// ekran dışı, görev çubuğunda yok, odak çalmaz. Tek örnek: kanal değişince aynı pencere/sayfa yeniden kullanılır; oynatıcı bırakılınca sayfa boşaltılır
/// (video oynamayı keser) ve pencere kısa bir bekleme sonra yok edilir (bellek). Kalıcı profil <c>data\webview2\</c>'dadır; oturum açmayı
/// <b>yalnızca kullanıcı</b> görünür bir pencerede yapar — uygulama kimlik bilgisi/çerez okumaz, saklamaz, günlüğe yazmaz.
/// </summary>
public sealed class WebViewKickPlayerHost : IKickPlayerHost, IKickPlayerSession, IDisposable
{
    private const string HomeUrl = "https://kick.com/";
    private const string BrowserArguments =
        "--autoplay-policy=no-user-gesture-required " +
        "--disable-features=CalculateNativeWinOcclusion " +
        "--disable-background-timer-throttling " +
        "--disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows " +
        "--mute-audio"; // ses çıkışı hiç açılmasın (CoreWebView2.IsMuted'a ek, ikinci katman)

    /// <summary>
    /// Yazılım oluşturma: GPU işlemi ~190 MB özel bellek tutuyor, <c>--disable-gpu</c> ile ~30 MB (ölçüldü, docs/02 §2); 160p video yazılımla sorunsuz çözülüyor,
    /// CPU farkı ölçülemedi. <c>RUSTDROPER_PLAYER_GPU=1</c> ile eski davranış.
    /// </summary>
    private const string SoftwareRenderingArgument = "--disable-gpu";

    private static readonly Regex SlugPattern = new("^[A-Za-z0-9_-]{1,50}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dispatcher _ui;
    private readonly string _userDataFolder;
    private readonly string _markerFile;
    private readonly PlayerWindowMode _mode;
    private readonly TimeSpan _idleGrace;
    private readonly TimeSpan _tickInterval;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<string, string> _channelUrl;
    private readonly Func<Uri, bool> _allowNavigation;
    private readonly string _browserArguments;
    private readonly (double Width, double Height) _windowSize;
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly Lock _statusGate = new();

    private CoreWebView2Environment? _env;
    private Window? _window;
    private WebView2? _web;
    private DispatcherTimer? _tickTimer;
    private DispatcherTimer? _destroyTimer;
    private Window? _loginWindow;
    private PlayerMonitor? _monitor;
    private string? _slug;
    private bool _processFailed;
    private bool _ticking;
    private bool _qualityDone;
    private int _qualityTries;
    private bool _disposed;
    private DateTimeOffset _lastDiagnostic = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMemoryLog = DateTimeOffset.MinValue;
    private KickPlayerStatus _status = KickPlayerStatus.Idle;
    private KickPlayerSessionStatus _sessionStatus = KickPlayerSessionStatus.Unknown;
    private Action<KickPlayerSessionStatus>? _sessionChanged;

    public WebViewKickPlayerHost(
        Dispatcher dispatcher,
        string userDataFolder,
        string markerFile,
        PlayerWindowMode mode = PlayerWindowMode.OffScreen,
        TimeSpan? idleGrace = null,
        TimeSpan? tickInterval = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        Func<string, string>? channelUrl = null,
        Func<Uri, bool>? allowNavigation = null,
        string? extraBrowserArguments = null,
        (double Width, double Height)? windowSize = null,
        bool allowGpu = false)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerFile);
        _ui = dispatcher;
        _userDataFolder = userDataFolder;
        _markerFile = markerFile;
        _mode = mode;
        _idleGrace = idleGrace ?? TimeSpan.FromSeconds(20);
        _tickInterval = tickInterval ?? TimeSpan.FromSeconds(5);
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _channelUrl = channelUrl ?? (slug => $"https://kick.com/{slug}");
        _allowNavigation = allowNavigation ?? (uri => uri.Scheme == "about" || IsKickHost(uri));
        var baseArguments = allowGpu ? BrowserArguments : BrowserArguments + " " + SoftwareRenderingArgument;
        _browserArguments = string.IsNullOrWhiteSpace(extraBrowserArguments) ? baseArguments : baseArguments + " " + extraBrowserArguments.Trim();
        // Masaüstü yerleşimi gerekli: Kick dar pencerede (< ~1024 px) mobil yerleşime geçer; oynatıcı denetimleri/kalite menüsü orada çalışmaz (ölçüldü).
        _windowSize = windowSize ?? (1280, 720);
        _sessionStatus = LoadMarker();
    }

    // ───────────── IKickPlayerHost ─────────────

    public KickPlayerStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    public event Action<KickPlayerStatus>? StatusChanged;

    /// <summary>Tanılama/ölçüm: her yoklamanın ham görüntüsü (sayfa görünürlüğü, video boyutu/zamanı…); kimlik bilgisi içermez.</summary>
    internal event Action<PlayerProbe?>? ProbeObserved;

    public Task OpenAsync(string slug, CancellationToken cancellationToken) =>
        RunOnUi(() => OpenCoreAsync(slug, cancellationToken), cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken) =>
        RunOnUi(() => RestartCoreAsync(cancellationToken), cancellationToken);

    public Task ReleaseAsync() => RunOnUi(ReleaseCoreAsync, CancellationToken.None);

    // ───────────── IKickPlayerSession (açık arayüz uygulaması: üye adları IKickPlayerHost ile çakışır) ─────────────

    KickPlayerSessionStatus IKickPlayerSession.Status
    {
        get
        {
            lock (_statusGate)
            {
                return _sessionStatus;
            }
        }
    }

    event Action<KickPlayerSessionStatus>? IKickPlayerSession.StatusChanged
    {
        add => _sessionChanged += value;
        remove => _sessionChanged -= value;
    }

    Task<KickPlayerSessionStatus> IKickPlayerSession.RefreshAsync(CancellationToken cancellationToken) =>
        RunOnUiResult(() => RefreshSessionCoreAsync(cancellationToken), cancellationToken);

    Task IKickPlayerSession.LoginAsync(CancellationToken cancellationToken) =>
        RunOnUi(() => LoginCoreAsync(), cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_ui.CheckAccess())
            {
                DestroyEverything();
            }
            else if (!_ui.HasShutdownStarted)
            {
                _ui.Invoke(DestroyEverything);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kick oynatıcı kapatılırken hata.");
        }
    }

    /// <summary>Bellek/CPU örneği: toplam özel bellek, işlem sayısı, toplam CPU süresi, türe göre döküm.</summary>
    internal sealed record MemorySample(long Bytes, int Processes, TimeSpan Cpu, string Breakdown);

    /// <summary>Test: tarayıcıya verilen bayraklar (ses kapalı, yazılım oluşturma… kontrolü için).</summary>
    internal string EffectiveBrowserArguments => _browserArguments;

    /// <summary>Test/tanılama: gizli oynatıcı penceresi (yoksa <c>null</c>).</summary>
    internal Window? PlayerWindow => _window;

    /// <summary>Ölçüm: WebView2'ye ait tüm işlemlerin (tarayıcı, GPU, oluşturucu…) toplam özel belleği; açık tarayıcı yoksa <c>null</c>.</summary>
    internal MemorySample? SampleMemory()
    {
        if (_disposed || _ui.HasShutdownStarted)
        {
            return null;
        }

        return _ui.CheckAccess() ? SampleMemoryOnUi() : _ui.Invoke(SampleMemoryOnUi);
    }

    private MemorySample? SampleMemoryOnUi()
    {
        if (_env is null || (_web?.CoreWebView2 is null && _loginWindow is null))
        {
            return null;
        }

        try
        {
            long total = 0;
            var count = 0;
            var cpu = TimeSpan.Zero;
            var byKind = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var info in _env.GetProcessInfos())
            {
                try
                {
                    using var process = Process.GetProcessById(info.ProcessId);
                    total += process.PrivateMemorySize64;
                    cpu += process.TotalProcessorTime;
                    byKind[info.Kind.ToString()] = byKind.GetValueOrDefault(info.Kind.ToString()) + process.PrivateMemorySize64;
                    count++;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // işlem bu arada kapanmış / erişilemiyor
                }
            }

            return count == 0 ? null : new MemorySample(total, count, cpu, string.Join(", ", byKind.Select(kv => $"{kv.Key} {kv.Value / 1048576.0:F0} MB")));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    // ───────────── açılış / yeniden başlatma / bırakma ─────────────

    private async Task OpenCoreAsync(string slug, CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            _destroyTimer?.Stop();
            if (!SlugPattern.IsMatch(slug))
            {
                Publish(new KickPlayerStatus(KickPlayerState.Failed, slug, Loc.T("player.badChannel")));
                return;
            }

            _slug = slug;
            _monitor = new PlayerMonitor(slug, _time.GetUtcNow());
            _qualityDone = false;
            _qualityTries = 0;
            Publish(new KickPlayerStatus(KickPlayerState.Starting, slug, Loc.T("player.opening")));
            if (!await EnsureWindowAsync().ConfigureAwait(true))
            {
                return;
            }

            NavigateTo(_channelUrl(slug));
            StartTicking();
        }
        finally
        {
            _op.Release();
        }
    }

    private async Task RestartCoreAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_slug is not { } slug)
            {
                return;
            }

            _destroyTimer?.Stop();
            _monitor = new PlayerMonitor(slug, _time.GetUtcNow());
            _qualityDone = false;
            _qualityTries = 0;
            Publish(new KickPlayerStatus(KickPlayerState.Starting, slug, Loc.T("player.restarting")));
            if (_processFailed)
            {
                DestroyWindow(); // tarayıcı işlemi çöktü: pencereyi baştan kur
            }

            if (!await EnsureWindowAsync().ConfigureAwait(true))
            {
                return;
            }

            NavigateTo(_channelUrl(slug));
            StartTicking();
        }
        finally
        {
            _op.Release();
        }
    }

    private async Task ReleaseCoreAsync()
    {
        await _op.WaitAsync().ConfigureAwait(true);
        try
        {
            _tickTimer?.Stop();
            _slug = null;
            _monitor = null;
            if (_web?.CoreWebView2 is { } core)
            {
                // Sayfayı boşalt: oynatıcı kapanır, izleme sinyali/trafik hemen kesilir.
                core.Navigate("about:blank");
                ScheduleDestroy();
            }
            else
            {
                DestroyWindow();
            }

            Publish(KickPlayerStatus.Idle);
        }
        finally
        {
            _op.Release();
        }
    }

    private void ScheduleDestroy()
    {
        _destroyTimer ??= new DispatcherTimer(DispatcherPriority.Background, _ui);
        _destroyTimer.Stop();
        _destroyTimer.Interval = _idleGrace;
        _destroyTimer.Tick -= OnDestroyTimer;
        _destroyTimer.Tick += OnDestroyTimer;
        _destroyTimer.Start();
    }

    private void OnDestroyTimer(object? sender, EventArgs e)
    {
        _destroyTimer?.Stop();
        if (_slug is null && _op.CurrentCount > 0)
        {
            DestroyWindow();
            _logger.LogInformation("Kick oynatıcı penceresi yok edildi (boşta).");
        }
    }

    // ───────────── pencere / WebView2 ─────────────

    private async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_env is not null)
        {
            return _env;
        }

        Directory.CreateDirectory(_userDataFolder);
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = _browserArguments };
        _env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options).ConfigureAwait(true);
        return _env;
    }

    /// <summary>Pencere ve WebView2 hazırsa <c>true</c>; değilse durum yayımlanmıştır (Unavailable/Failed) ve <c>false</c> döner.</summary>
    private async Task<bool> EnsureWindowAsync()
    {
        if (_disposed)
        {
            Publish(new KickPlayerStatus(KickPlayerState.Failed, _slug, Loc.T("player.closing")));
            return false;
        }

        if (_web?.CoreWebView2 is not null && !_processFailed)
        {
            return true;
        }

        DestroyWindow();
        try
        {
            var env = await GetEnvironmentAsync().ConfigureAwait(true);
            var window = CreateHiddenWindow();
            var web = new WebView2();
            window.Content = web;
            _window = window;
            _web = web;
            window.Show();
            await web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            ConfigureCore(web.CoreWebView2);
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(KickPlayerScripts.Agent).ConfigureAwait(true);
            _processFailed = false;
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            DestroyWindow();
            SetSession(new KickPlayerSessionStatus(KickPlayerSessionState.Unavailable, Loc.T("player.noRuntime")));
            Publish(new KickPlayerStatus(
                KickPlayerState.Unavailable,
                _slug,
                Loc.T("player.noRuntime.long")));
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DestroyWindow();
            _logger.LogError(ex, "Kick oynatıcı penceresi/WebView2 başlatılamadı.");
            Publish(new KickPlayerStatus(KickPlayerState.Failed, _slug, Loc.T("player.webviewFailed", ex.GetType().Name)));
            return false;
        }
    }

    private Window CreateHiddenWindow()
    {
        var window = new Window
        {
            Title = Loc.T("player.windowTitle"),
            Width = _windowSize.Width,
            Height = _windowSize.Height,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = System.Windows.Media.Brushes.Black,
            Focusable = false,
        };

        if (_mode == PlayerWindowMode.OffScreen)
        {
            window.Left = -20000;
            window.Top = -20000;
            window.SourceInitialized += (_, _) => MakeToolWindow(window);
        }
        else
        {
            window.Left = 80;
            window.Top = 80;
            window.ShowInTaskbar = true;
        }

        return window;
    }

    /// <summary>Alt+Tab/görev çubuğunda görünmesin, etkinleşmesin (odak çalmasın).</summary>
    private static void MakeToolWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        core.IsMuted = true; // ses tarayıcı düzeyinde kapalı (sayfanın kendi ses durumu değişmez)
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationCompleted += (_, _) => core.IsMuted = true;
        core.NewWindowRequested += (_, e) => e.Handled = true; // açılır pencere/reklam sekmesi yok
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NavigationStarting += OnNavigationStarting;
        core.ProcessFailed += (_, e) =>
        {
            _processFailed = e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                || _processFailed;
            _logger.LogWarning("Kick oynatıcı tarayıcı işlemi sorunu: {Kind}", e.ProcessFailedKind);
            if (_slug is not null)
            {
                Publish(new KickPlayerStatus(KickPlayerState.Failed, _slug, Loc.T("player.processFailed", e.ProcessFailedKind)));
            }
        };
    }

    /// <summary>Üst düzey gezinti yalnızca kick.com'a (ve about:blank'e) izin verir; reklam/yönlendirme sayfaları açılmaz.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        if (_allowNavigation(uri))
        {
            return;
        }

        e.Cancel = true;
        _logger.LogInformation("Kick oynatıcı: kick.com dışı gezinti engellendi ({Host}).", uri.Host);
    }

    internal static bool IsKickHost(Uri uri) =>
        uri.Scheme is "http" or "https"
        && (uri.Host.Equals("kick.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".kick.com", StringComparison.OrdinalIgnoreCase));

    private void NavigateTo(string url)
    {
        if (_web?.CoreWebView2 is { } core)
        {
            core.IsMuted = true; // her gezintide yeniden uygula: oynatıcı sessiz kalmalı (kullanıcı kuralı: SES KAPALI)
            core.Navigate(url);
        }
    }

    private void DestroyWindow()
    {
        _tickTimer?.Stop();
        _destroyTimer?.Stop();
        var web = _web;
        var window = _window;
        _web = null;
        _window = null;
        try
        {
            web?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebView2 yok edilirken hata.");
        }

        try
        {
            window?.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void DestroyEverything()
    {
        DestroyWindow();
        try
        {
            _loginWindow?.Close();
        }
        catch (InvalidOperationException)
        {
        }

        _loginWindow = null;
    }

    // ───────────── yoklama döngüsü ─────────────

    private void StartTicking()
    {
        _tickTimer ??= new DispatcherTimer(DispatcherPriority.Background, _ui);
        _tickTimer.Stop();
        _tickTimer.Interval = _tickInterval;
        _tickTimer.Tick -= OnTick;
        _tickTimer.Tick += OnTick;
        _tickTimer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_ticking || _slug is null || _monitor is null || _web?.CoreWebView2 is not { } core || _op.CurrentCount == 0)
        {
            return;
        }

        _ticking = true;
        try
        {
            var slug = _slug;
            var monitor = _monitor;
            var json = await core.ExecuteScriptAsync($"window.__rdAgent ? window.__rdAgent.tick({JsonSerializer.Serialize(slug)}) : null").ConfigureAwait(true);
            if (!ReferenceEquals(monitor, _monitor))
            {
                return; // bu arada yeniden başlatıldı/bırakıldı
            }

            var probe = PlayerProbe.Parse(json);
            var now = _time.GetUtcNow();
            var status = monitor.Update(probe, now);
            ProbeObserved?.Invoke(probe);
            ObserveSession(monitor.LastAuth);
            Publish(status);
            LogDiagnostics(probe, status, now);
            if (probe?.Video is { Paused: false, ReadyState: >= 3 } && !_qualityDone && _qualityTries < 6)
            {
                _qualityTries++;
                _qualityDone = await TryApplyLowestQualityAsync(core).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Kick oynatıcı yoklaması başarısız.");
        }
        finally
        {
            _ticking = false;
        }
    }

    private void ObserveSession(string? auth)
    {
        if (auth == "in" && _sessionStatus.State != KickPlayerSessionState.LoggedIn)
        {
            SetSession(new KickPlayerSessionStatus(KickPlayerSessionState.LoggedIn));
        }
        else if (auth == "out" && _sessionStatus.State != KickPlayerSessionState.LoggedOut)
        {
            SetSession(new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut));
        }
    }

    private void LogDiagnostics(PlayerProbe? probe, KickPlayerStatus status, DateTimeOffset now)
    {
        if (now - _lastDiagnostic >= TimeSpan.FromMinutes(1))
        {
            _lastDiagnostic = now;
            _logger.LogInformation(
                "Kick oynatıcı: {State} {Slug} (görünürlük: {Visibility}, video: {Height}p, ayrıntı: {Detail})",
                status.State,
                status.Slug,
                probe?.Visibility ?? "?",
                status.VideoHeight?.ToString(CultureInfo.InvariantCulture) ?? "?",
                status.Detail ?? "-");
        }

        if (now - _lastMemoryLog >= TimeSpan.FromMinutes(5) && SampleMemory() is { } memory)
        {
            _lastMemoryLog = now;
            _logger.LogInformation("Kick oynatıcı bellek: toplam özel {Mb:F0} MB ({Processes} WebView2 işlemi; {Breakdown}).", memory.Bytes / 1048576.0, memory.Processes, memory.Breakdown);
        }
    }

    // ───────────── en düşük kalite ─────────────

    /// <summary>
    /// Kick oynatıcısının ayar menüsünden en düşük çözünürlüğü seçer. Denetimler fare üzerine gelince oluştuğu için GERÇEK fare olayları
    /// DevTools protokolüyle gönderilir (sayfaya sentetik olay enjekte etmek denetimleri açmıyor). Seçim sayfa yenilenince kaybolur → her açılışta yinelenir.
    /// </summary>
    private async Task<bool> TryApplyLowestQualityAsync(CoreWebView2 core)
    {
        try
        {
            if (await EvalPointAsync(core, "videoCenter()").ConfigureAwait(true) is not { } center)
            {
                _logger.LogInformation("Kick oynatıcı kalite: video konumu bulunamadı (deneme {Try}).", _qualityTries);
                return false;
            }

            (double X, double Y)? gearPoint = null;
            for (var attempt = 0; attempt < 4 && gearPoint is null; attempt++)
            {
                // Kick'in denetimleri fare hareketiyle belirir: birkaç küçük hareket (gerçek bir izleyicinin fareyi gezdirmesi gibi).
                for (var step = 0; step < 4; step++)
                {
                    await MouseAsync(core, "mouseMoved", center.X + (step * 14) - 20, center.Y + (step * 9) - 12).ConfigureAwait(true);
                    await Task.Delay(90).ConfigureAwait(true);
                }

                await Task.Delay(500).ConfigureAwait(true);
                gearPoint = await EvalPointAsync(core, "gear()").ConfigureAwait(true);
            }

            if (gearPoint is not { } gear)
            {
                var debug = await core.ExecuteScriptAsync("window.__rdAgent ? window.__rdAgent.debugControls() : null").ConfigureAwait(true);
                _logger.LogInformation("Kick oynatıcı kalite: ayar düğmesi bulunamadı (deneme {Try}): {Debug}", _qualityTries, debug);
                return false;
            }

            await ClickAsync(core, gear.X, gear.Y).ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);
            var itemsJson = await core.ExecuteScriptAsync("window.__rdAgent ? window.__rdAgent.qualityItems() : []").ConfigureAwait(true);
            var lowest = PickLowestQuality(itemsJson);
            if (lowest is null)
            {
                _logger.LogInformation("Kick oynatıcı kalite: menü öğesi bulunamadı (deneme {Try}).", _qualityTries);
                await EscapeAsync(core).ConfigureAwait(true);
                return false;
            }

            if (lowest.Value.Checked)
            {
                await EscapeAsync(core).ConfigureAwait(true);
                return true;
            }

            await ClickAsync(core, lowest.Value.X, lowest.Value.Y).ConfigureAwait(true);
            await Task.Delay(1200).ConfigureAwait(true);
            _logger.LogInformation("Kick oynatıcı: en düşük kalite seçildi ({Quality}).", lowest.Value.Label);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Kick oynatıcı kalite seçimi başarısız.");
            return false;
        }
    }

    internal readonly record struct QualityItem(string Label, int Height, bool Checked, double X, double Y);

    /// <summary>Menü öğelerinden en küçük çözünürlüklüyü (ör. "160p") seçer; "Auto" ve boş koordinatlılar atlanır.</summary>
    internal static QualityItem? PickLowestQuality(string? itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(itemsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            QualityItem? best = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var label = item.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
                var match = Regex.Match(label, @"^(\d{3,4})p", RegexOptions.CultureInvariant);
                if (!match.Success
                    || !item.TryGetProperty("c", out var c)
                    || c.ValueKind != JsonValueKind.Object
                    || !c.TryGetProperty("x", out var x)
                    || !c.TryGetProperty("y", out var y))
                {
                    continue;
                }

                var height = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var isChecked = item.TryGetProperty("checked", out var ch) && ch.ValueKind == JsonValueKind.True;
                if (best is null || height < best.Value.Height)
                {
                    best = new QualityItem(label, height, isChecked, x.GetDouble(), y.GetDouble());
                }
            }

            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<(double X, double Y)?> EvalPointAsync(CoreWebView2 core, string call)
    {
        var json = await core.ExecuteScriptAsync($"window.__rdAgent ? window.__rdAgent.{call} : null").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y)
            ? (x.GetDouble(), y.GetDouble())
            : null;
    }

    private static Task MouseAsync(CoreWebView2 core, string type, double x, double y)
    {
        var parameters = new
        {
            type,
            x = Math.Round(x, 1),
            y = Math.Round(y, 1),
            button = type == "mouseMoved" ? "none" : "left",
            buttons = type == "mousePressed" ? 1 : 0,
            clickCount = type == "mouseMoved" ? 0 : 1,
        };
        return core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(parameters));
    }

    private static async Task ClickAsync(CoreWebView2 core, double x, double y)
    {
        await MouseAsync(core, "mouseMoved", x, y).ConfigureAwait(true);
        await MouseAsync(core, "mousePressed", x, y).ConfigureAwait(true);
        await MouseAsync(core, "mouseReleased", x, y).ConfigureAwait(true);
    }

    private static async Task EscapeAsync(CoreWebView2 core)
    {
        const string down = """{"type":"keyDown","key":"Escape","code":"Escape","windowsVirtualKeyCode":27}""";
        const string up = """{"type":"keyUp","key":"Escape","code":"Escape","windowsVirtualKeyCode":27}""";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", down).ConfigureAwait(true);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", up).ConfigureAwait(true);
    }

    // ───────────── oturum (profil) ─────────────

    private async Task<KickPlayerSessionStatus> RefreshSessionCoreAsync(CancellationToken ct)
    {
        // Oynatıcı zaten çalışıyorsa oturum bilgisi yoklamalardan gelir (ObserveSession); ikinci bir tarayıcı açma.
        if (_slug is not null)
        {
            return ((IKickPlayerSession)this).Status;
        }

        await _op.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_slug is not null)
            {
                return ((IKickPlayerSession)this).Status;
            }

            SetSession(new KickPlayerSessionStatus(KickPlayerSessionState.Checking));
            if (!await EnsureWindowAsync().ConfigureAwait(true))
            {
                return ((IKickPlayerSession)this).Status;
            }

            var core = _web!.CoreWebView2;
            NavigateTo(HomeUrl);
            string? auth = null;
            for (var i = 0; i < 20 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(1500, ct).ConfigureAwait(true);
                var raw = await core.ExecuteScriptAsync("window.__rdAgent ? window.__rdAgent.auth() : null").ConfigureAwait(true);
                auth = raw?.Trim('"');
                if (auth is "in" or "out")
                {
                    break;
                }
            }

            SetSession(auth switch
            {
                "in" => new KickPlayerSessionStatus(KickPlayerSessionState.LoggedIn),
                "out" => new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut),
                _ => new KickPlayerSessionStatus(KickPlayerSessionState.Unknown, "Doğrulanamadı"),
            });
            core.Navigate("about:blank");
            ScheduleDestroy();
            return ((IKickPlayerSession)this).Status;
        }
        catch (OperationCanceledException)
        {
            return ((IKickPlayerSession)this).Status;
        }
        finally
        {
            _op.Release();
        }
    }

    private async Task LoginCoreAsync()
    {
        if (_loginWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        CoreWebView2Environment env;
        try
        {
            env = await GetEnvironmentAsync().ConfigureAwait(true);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            SetSession(new KickPlayerSessionStatus(KickPlayerSessionState.Unavailable, Loc.T("player.noRuntime")));
            return;
        }

        var window = new KickLoginWindow(HomeUrl);
        _loginWindow = window;
        window.Closed += (_, _) =>
        {
            _loginWindow = null;
            _ = RefreshAfterLoginAsync();
        };
        window.Show();
        await window.InitializeAsync(env).ConfigureAwait(true);
    }

    private async Task RefreshAfterLoginAsync()
    {
        try
        {
            await Task.Delay(1500).ConfigureAwait(true);
            await RefreshSessionCoreAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Giriş sonrası oturum doğrulaması başarısız.");
        }
    }

    private void SetSession(KickPlayerSessionStatus next)
    {
        lock (_statusGate)
        {
            if (_sessionStatus == next)
            {
                return;
            }

            _sessionStatus = next;
        }

        if (next.State is KickPlayerSessionState.LoggedIn or KickPlayerSessionState.LoggedOut)
        {
            SaveMarker(next.State);
        }

        _sessionChanged?.Invoke(next);
    }

    /// <summary>Son bilinen oturum durumu (yalnızca "açık/yok" ve zaman; kimlik bilgisi değil) — uygulama açılışında WebView2 başlatmadan göstermek için.</summary>
    private sealed class SessionMarker
    {
        public string State { get; set; } = "unknown";

        public DateTimeOffset? CheckedAt { get; set; }
    }

    private KickPlayerSessionStatus LoadMarker()
    {
        try
        {
            var marker = new AtomicJsonStore<SessionMarker>(_markerFile, 1).Load().Value;
            return marker.State switch
            {
                "loggedIn" => new KickPlayerSessionStatus(KickPlayerSessionState.LoggedIn, marker.CheckedAt is { } at ? Loc.T("player.lastVerified", at.ToLocalTime()) : null),
                "loggedOut" => new KickPlayerSessionStatus(KickPlayerSessionState.LoggedOut),
                _ => KickPlayerSessionStatus.Unknown,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return KickPlayerSessionStatus.Unknown;
        }
    }

    private void SaveMarker(KickPlayerSessionState state)
    {
        try
        {
            new AtomicJsonStore<SessionMarker>(_markerFile, 1).Save(new SessionMarker
            {
                State = state == KickPlayerSessionState.LoggedIn ? "loggedIn" : "loggedOut",
                CheckedAt = _time.GetUtcNow(),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Oynatıcı oturum işareti yazılamadı.");
        }
    }

    // ───────────── yardımcılar ─────────────

    private void Publish(KickPlayerStatus next)
    {
        lock (_statusGate)
        {
            if (_status == next)
            {
                return;
            }

            _status = next;
        }

        try
        {
            StatusChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Oynatıcı durum dinleyicisi hata verdi.");
        }
    }

    private async Task RunOnUi(Func<Task> action, CancellationToken ct)
    {
        if (_ui.HasShutdownStarted)
        {
            return;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            await _ui.InvokeAsync(action).Task.Unwrap().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (_ui.HasShutdownStarted)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Kick oynatıcı işlemi beklenmedik hata verdi.");
            Publish(new KickPlayerStatus(KickPlayerState.Failed, _slug, Loc.T("player.error", ex.GetType().Name)));
        }
    }

    private async Task<T> RunOnUiResult<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (_ui.HasShutdownStarted)
        {
            return ((IKickPlayerSession)this).Status is T same ? same : default!;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            return await _ui.InvokeAsync(action).Task.Unwrap().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Kick oynatıcı işlemi beklenmedik hata verdi.");
            return ((IKickPlayerSession)this).Status is T status ? status : default!;
        }
    }
}
