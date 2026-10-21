using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustDroper.Core.Configuration;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;
using RustDroper.Core.Platforms;
using RustDroper.Core.Platforms.Twitch;
using RustDroper.Core.Watching;

namespace RustDroper.Core.Orchestration;

/// <summary>
/// Platform hesabı: oturum geri yükleme, cihaz-kodu/QR girişi, çıkış (Twitch: <see cref="TwitchSessionManager"/>, Kick: <c>KickAccount</c>).
/// Taşıyıcı tipler (<see cref="TwitchSession"/>, <see cref="TwitchRestoreResult"/>, <see cref="TwitchDeviceCode"/>) adlarına rağmen platformdan bağımsız şekillerdir;
/// Kick bağdaştırıcısı kendi tiplerini bunlara çevirir.
/// </summary>
public interface IPlatformAccount
{
    Task<TwitchRestoreResult> RestoreAsync(CancellationToken cancellationToken = default);

    Task<TwitchSession> LoginAsync(Action<TwitchDeviceCode> onCode, CancellationToken cancellationToken = default);

    void Logout();
}

/// <summary>Bir oturum için kurulmuş platform bağlantısı (GQL, izleyici, PubSub, isteğe bağlı otomatik claim).</summary>
public interface IDropConnection : IDisposable
{
    IDropPlatform Platform { get; }

    Task<TwitchInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken);
}

public interface IDropConnectionFactory
{
    /// <param name="accessToken">Güncel token'ı döndürür (yeniden girişte motor yeniden başlamadan yeni token'ı kullansın diye).</param>
    /// <param name="autoClaim"><c>false</c> ise hiçbir drop claim edilmez.</param>
    IDropConnection Create(TwitchSession session, Func<string?> accessToken, bool autoClaim);

    /// <summary>Kalıcı claim geçmişinden başarıyla claim edilmiş drop kimlikleri.</summary>
    IReadOnlyCollection<string> LoadClaimedDropIds();
}

/// <summary>Uyku/uyanış ve ağ kesintisi toparlama zamanlamaları (testte kısaltılır).</summary>
public sealed record PowerOptions
{
    /// <summary>Uyanıştan sonra ağın gelmesi için beklenen süre.</summary>
    public TimeSpan ResumeDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Bağlantı geri geldi (yoklamayla doğrulandı) olayından sonra toparlamadan önce ek bekleme; vars. yok (doğrulama zaten yapıldı).</summary>
    public TimeSpan NetworkSettle { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Bu süreyi aşan ağ kesintisinden sonra çalışan motor, durumu ne olursa olsun yeniden başlatılır (izleyici ~30 sn kesintide pes edip
    /// kanalı engeller; yeni motor engelleri sıfırlar).
    /// </summary>
    public TimeSpan OutageRestartThreshold { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>Arayüzün tek seferde tutarlı okuyacağı görüntü.</summary>
public sealed record DropperSnapshot(
    AccountSnapshot Account,
    EngineSnapshot Engine,
    bool IsRunning,
    PlatformSnapshot? Campaigns,
    TwitchInventorySnapshot? Inventory,
    IReadOnlySet<string> ClaimedDropIds,
    string? RefreshError,
    bool NetworkOnline = true);

/// <summary>
/// Uygulama katmanı: hesap + motor yaşam döngüsü. UI bunu çağırır ve <see cref="Changed"/> ile izler; iş mantığı burada/Core'dadır.
/// Hiçbir hata uygulamayı çökertmez; hesap sorunları <see cref="AccountSnapshot"/>, motor sorunları <see cref="EngineSnapshot"/>'ta görünür.
/// </summary>
public sealed class DropperService : IDisposable
{
    private readonly IPlatformAccount _account;
    private readonly PlatformProfile _profile;
    private readonly IDropConnectionFactory _factory;
    private readonly SettingsService _settings;
    private readonly StateStore? _state;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<AppSettings, EngineOptions> _options;
    private readonly TimeSpan _inventoryInterval;
    private readonly TimeSpan _offlineRetry;
    private readonly ISystemEvents? _events;
    private readonly ConnectivityMonitor _connectivity;
    private readonly PowerOptions _power;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private AccountSnapshot _accountState = AccountSnapshot.Initial;
    private TwitchSession? _session;
    private EngineSnapshot _engine = new(EngineState.Stopped, Loc.T("engine.notStarted"), null, null, null, 0, []);
    private PlatformSnapshot? _campaigns;
    private TwitchInventorySnapshot? _inventory;

    /// <summary>Gösterilen envanterin ait olduğu hesap (önbellekten yüklendiyse oturum doğrulanana dek doğrulanmamıştır).</summary>
    private string? _inventoryOwner;
    private HashSet<string> _historyClaimed = new(StringComparer.Ordinal);
    private string? _refreshError;
    private CancellationTokenSource? _loginCts;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _running;
    private bool _authRecovering;

    /// <summary>Kullanıcı motoru çalıştırmak istiyor (Başlat verildi, Duraklat/çıkış verilmedi). Uyku/ağ kesintisinden sonra yeniden başlatmanın kaynağı.</summary>
    private bool _wantRunning;
    private int _disposed;
    private bool _suspended;
    private bool _lostNotified;
    private CancellationTokenSource? _resumeCts;
    private CancellationTokenSource? _networkCts;
    private CancellationTokenSource? _restoreCts;
    private int _lastClaimCount;
    private bool _autoClaimThisRun;
    private bool _needsLoginNotified;

    /// <summary>Motor boşta (kampanya yok) görüldü; bir sonraki "izleniyor" geçişi "kampanya başladı" bildirimidir.</summary>
    private bool _idleSeen;
    private string? _claimReadyDropId;
    private TaskCompletionSource _nudge = NewNudge();

    public DropperService(
        IPlatformAccount account,
        IDropConnectionFactory factory,
        SettingsService settings,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        Func<AppSettings, EngineOptions>? engineOptions = null,
        TimeSpan? inventoryInterval = null,
        TimeSpan? offlineRetry = null,
        StateStore? stateStore = null,
        ISystemEvents? systemEvents = null,
        PowerOptions? power = null,
        IConnectivityProbe? connectivityProbe = null,
        ConnectivityOptions? connectivity = null,
        PlatformProfile? platform = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(settings);
        _account = account;
        _profile = platform ?? PlatformProfile.Twitch;
        _factory = factory;
        _settings = settings;
        _state = stateStore;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _options = engineOptions ?? (s => new EngineOptions
        {
            GameSlug = s.GameSlug,
            StreamerPriority = () => _profile == PlatformProfile.Kick ? _settings.Current.KickStreamerPriority : _settings.Current.TwitchStreamerPriority,
        });
        _inventoryInterval = inventoryInterval ?? TimeSpan.FromMinutes(5);
        _offlineRetry = offlineRetry ?? TimeSpan.FromSeconds(15);
        _power = power ?? new PowerOptions();
        _events = systemEvents;
        _connectivity = new ConnectivityMonitor(connectivityProbe, systemEvents, _time, connectivity, _logger);
        _connectivity.Changed += OnConnectivityChanged;
        if (_events is not null)
        {
            _events.Suspending += OnSuspending;
            _events.Resumed += OnResumed;
        }
    }

    /// <summary>Herhangi bir görüntü değişince (hesap, motor, envanter, keşif) tetiklenir; herhangi bir iş parçacığında.</summary>
    public event Action? Changed;

    /// <summary>Cihaz-kodu girişinde kod üretilince (arayüz tarayıcıyı açar).</summary>
    public event Action<TwitchDeviceCode>? DeviceCodeIssued;

    /// <summary>Bu servisin platformu (kart/bildirim metinleri için).</summary>
    public PlatformProfile Profile => _profile;

    /// <summary>
    /// Kullanıcıya bildirilecek olay (claim edildi/başarısız/hazır, giriş gerekli, kampanya başladı). Herhangi bir iş parçacığında gelir;
    /// tekrar önleme ve "bildirimler açık mı" kararı dinleyicidedir (<see cref="NotificationGate"/>).
    /// </summary>
    public event Action<UserNotification>? Notification;

    public DropperSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var claimed = new HashSet<string>(_historyClaimed, StringComparer.Ordinal);
                claimed.UnionWith((_inventory?.InProgress ?? []).Where(p => p.IsClaimed).Select(p => p.DropId));
                return new DropperSnapshot(_accountState, _engine, IsRunningUnsafe, _campaigns, _inventory, claimed, _refreshError, _connectivity.IsOnline);
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return IsRunningUnsafe;
            }
        }
    }

    private bool IsRunningUnsafe => _running;

    /// <summary>Açılış: kayıtlı oturumu doğrular ve (bağlıysa) envanter + kampanyaları bir kez yükler.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _connectivity.Start(); // açılışta internet yoksa birkaç sn içinde "bağlantı yok" görünür; gelince oturum hemen doğrulanır

        // Son bilinen envanter (önbellek): oturum doğrulanıp taze veri gelene dek arayüz boş görünmesin.
        var cached = _state?.LoadInventory();
        lock (_gate)
        {
            _historyClaimed = [.. _factory.LoadClaimedDropIds()];
            if (cached is not null && _inventory is null)
            {
                _inventory = cached.Snapshot;
                _inventoryOwner = cached.UserId;
            }
        }

        await RestoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cihaz-kodu girişi. Zaten sürüyorsa yok sayılır. İptal: <see cref="CancelLogin"/>.</summary>
    public async Task LoginAsync()
    {
        CancellationTokenSource cts;
        AccountSnapshot before;
        lock (_gate)
        {
            if (_accountState.Status == AccountStatus.LoggingIn)
            {
                return;
            }

            before = _accountState;
            cts = _loginCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }

        SetAccount(new AccountSnapshot(AccountStatus.LoggingIn));
        try
        {
            var session = await _account.LoginAsync(
                code =>
                {
                    SetAccount(new AccountSnapshot(AccountStatus.LoggingIn, DeviceCode: code));
                    DeviceCodeIssued?.Invoke(code);
                },
                cts.Token).ConfigureAwait(false);

            lock (_gate)
            {
                _session = session;
                _authRecovering = false;
                DropForeignInventory(session.UserId);
            }

            SetAccount(new AccountSnapshot(AccountStatus.LoggedIn, session.Login));
            await RefreshAsync(cts.Token).ConfigureAwait(false);
            StartIfWanted();
        }
        catch (OperationCanceledException)
        {
            SetAccount(before.Status is AccountStatus.Unknown or AccountStatus.Restoring or AccountStatus.LoggingIn ? new AccountSnapshot(AccountStatus.LoggedOut) : before);
        }
        catch (TwitchLoginException ex)
        {
            _logger.LogWarning("{Platform} girişi başarısız ({Failure})", _profile.DisplayName, ex.Failure);
            SetAccount(new AccountSnapshot(AccountStatus.LoggedOut, Error: LoginErrorText(ex)));
        }
        catch (Exception ex) when (Retry.IsTransient(ex) || ex is PlatformDataException)
        {
            _logger.LogWarning("{Platform} girişi sırasında ağ/sunucu hatası: {Reason}", _profile.DisplayName, ex.Message);
            SetAccount(new AccountSnapshot(AccountStatus.LoggedOut, Error: Loc.T("service.unreachable", _profile.DisplayName, ex.Message)));
        }
        finally
        {
            lock (_gate)
            {
                _loginCts = null;
            }

            cts.Dispose();
        }
    }

    public void CancelLogin()
    {
        lock (_gate)
        {
            _loginCts?.Cancel();
        }
    }

    /// <summary>Oturum kapatma retry'ı: kayıtlı oturum yeniden doğrulanır (çevrimdışıyken ya da geçici hatadan sonra).</summary>
    public Task RetryRestoreAsync() => RestoreAsync(_lifetime.Token);

    /// <summary>Motoru durdurur, oturumu siler ve her şeyi temizler.</summary>
    public async Task LogoutAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _account.Logout();
        lock (_gate)
        {
            _session = null;
            _campaigns = null;
            _inventory = null;
            _inventoryOwner = null;
            _refreshError = null;
        }

        _state?.Clear();

        SetAccount(new AccountSnapshot(AccountStatus.LoggedOut));
    }

    /// <summary>Motoru başlatır. Giriş yapılmamışsa ya da zaten çalışıyorsa <c>false</c>.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (IsRunningUnsafe || _session is not { } session || _accountState.Status != AccountStatus.LoggedIn)
            {
                return false;
            }

            var settings = _settings.Current;
            var connection = _factory.Create(session, CurrentToken, settings.AutoClaim);
            var observing = new ObservingPlatform(connection.Platform, OnDiscovered, OnClaimNotice);
            var claimed = new HashSet<string>(_historyClaimed, StringComparer.Ordinal);
            claimed.UnionWith((_inventory?.InProgress ?? []).Where(p => p.IsClaimed).Select(p => p.DropId));
            var engine = new DropEngine(observing, _options(settings), claimed, _time, _logger, _state?.ForUser(session.UserId));
            engine.Changed += OnEngineChanged;

            var cts = _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _lastClaimCount = 0;
            _autoClaimThisRun = settings.AutoClaim;
            _idleSeen = false;
            _claimReadyDropId = null;
            _engine = engine.Snapshot;
            _running = true;
            _wantRunning = true;
            _runTask = Task.Run(() => RunAsync(engine, connection, observing, cts));
        }

        _logger.LogInformation("Motor başlatıldı (otomatik claim: {AutoClaim}).", _settings.Current.AutoClaim);
        RaiseChanged();
        return true;
    }

    /// <summary>Kullanıcı isteğiyle motoru temiz durdurur (HLS izleyici ve PubSub kapanır); uyku/ağ sonrası otomatik yeniden başlatma da iptal olur.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            _wantRunning = false;
        }

        return StopCoreAsync();
    }

    /// <summary>Motoru durdurur ama kullanıcının "çalışsın" isteğini korur (uyku/ağ kurtarması için). Çalışmıyorsa hiçbir şey yapmaz.</summary>
    private async Task StopCoreAsync()
    {
        Task? run;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            run = _runTask;
            cts = _runCts;
        }

        if (run is null)
        {
            return;
        }

        try
        {
            await cts!.CancelAsync().ConfigureAwait(false);
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Koşu zaten bitmiş ve iptal kaynağını bırakmış: durdurulacak bir şey yok.
        }
    }

    /// <summary>Envanteri (ve motor çalışmıyorsa kampanyaları da) yeniden okur. Hatalar <see cref="DropperSnapshot.RefreshError"/>'a yazılır.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        TwitchSession? session;
        bool running;
        lock (_gate)
        {
            session = _session;
            running = IsRunningUnsafe;
        }

        if (session is null)
        {
            return;
        }

        try
        {
            using var connection = _factory.Create(session, CurrentToken, autoClaim: false);
            var inventory = await connection.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
            PlatformSnapshot? campaigns = null;
            if (!running)
            {
                campaigns = await connection.Platform.DiscoverAsync(_settings.Current.GameSlug, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _inventory = inventory;
                _inventoryOwner = session.UserId;
                if (campaigns is not null)
                {
                    _campaigns = campaigns;
                }

                _refreshError = null;
                _historyClaimed = [.. _factory.LoadClaimedDropIds()];
            }

            _state?.SaveInventory(session.UserId, inventory);
            _connectivity.ReportReachable(); // gerçek istek başarılı: yoklama yanlışlıkla "yok" demişse düzelt
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (PlatformAuthException ex)
        {
            _logger.LogWarning("Yenileme: kimlik reddedildi: {Reason}", ex.Message);
            SetRefreshError(Loc.T("service.sessionRejected", _profile.DisplayName));
            _ = RecoverAuthAsync();
            return;
        }
        catch (Exception ex) when (Retry.IsTransient(ex) || ex is PlatformDataException)
        {
            _logger.LogWarning("Yenileme başarısız ({Type}): {Reason}", ex.GetType().Name, ex.Message);
            SetRefreshError(Loc.T("service.refreshFailed", ex.Message));
            _connectivity.CheckNow(); // ağ mı koptu? hemen yokla
            return;
        }

        RaiseChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_events is not null)
        {
            _events.Suspending -= OnSuspending;
            _events.Resumed -= OnResumed;
        }

        _connectivity.Changed -= OnConnectivityChanged;
        _connectivity.Dispose();

        _lifetime.Cancel();
        try
        {
            StopCoreAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Motor kapatılırken hata");
        }

        _lifetime.Dispose();
        _lifecycle.Dispose();
    }

    private static TaskCompletionSource NewNudge() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string LoginErrorText(TwitchLoginException ex) => ex.Failure switch
    {
        TwitchLoginFailure.Expired => Loc.T(_profile.Platform == Platform.Kick ? "service.login.expired.kick" : "service.login.expired.twitch"),
        TwitchLoginFailure.Denied => Loc.T("service.login.denied", _profile.DisplayName),
        TwitchLoginFailure.Revoked => Loc.T("service.login.revoked", _profile.DisplayName),
        _ => Loc.T("service.login.failed", ex.Message),
    };

    private string? CurrentToken()
    {
        lock (_gate)
        {
            return _session?.AccessToken;
        }
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetAccount(new AccountSnapshot(AccountStatus.Restoring));
            TwitchRestoreResult result;

            // Bağlantı koparsa bu doğrulama zaman aşımını (30 sn) beklemeden iptal edilir: arayüz hemen "çevrimdışı" olur.
            using var restoreCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate)
            {
                _restoreCts = restoreCts;
            }

            try
            {
                result = await _account.RestoreAsync(restoreCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                SetAccount(new AccountSnapshot(AccountStatus.Offline));
                ScheduleOfflineRetry();
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Oturum geri yüklenemedi");
                SetAccount(new AccountSnapshot(AccountStatus.Offline, Error: Loc.T("service.verifyFailed", ex.Message)));
                ScheduleOfflineRetry();
                return;
            }

            lock (_gate)
            {
                if (ReferenceEquals(_restoreCts, restoreCts))
                {
                    _restoreCts = null;
                }
            }

            switch (result.Status)
            {
                case TwitchRestoreStatus.Restored when result.Session is { } session:
                    lock (_gate)
                    {
                        _session = session;
                        DropForeignInventory(session.UserId);
                    }

                    SetAccount(new AccountSnapshot(AccountStatus.LoggedIn, session.Login));
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                    StartIfWanted();
                    break;
                case TwitchRestoreStatus.NeedsLogin:
                    DiscardCachedInventory();
                    SetAccount(new AccountSnapshot(AccountStatus.NeedsLogin));
                    break;
                case TwitchRestoreStatus.Offline:
                    SetAccount(new AccountSnapshot(AccountStatus.Offline));
                    ScheduleOfflineRetry();
                    break;
                default:
                    DiscardCachedInventory();
                    SetAccount(new AccountSnapshot(AccountStatus.LoggedOut));
                    break;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // ---- Uyku/uyanış ve ağ ------------------------------------------------------------------------------------------------

    /// <summary>Sistem uykuya geçiyor: HLS izleyiciyi/PubSub'ı düzgün kapat (uyanınca bağlantılar ölü olur). Bildirim kısa sürede dönmeli → durdurma arka planda.</summary>
    private void OnSuspending()
    {
        bool wasRunning;
        lock (_gate)
        {
            _suspended = true;
            _connectivity.Pause();
            wasRunning = _running;
            CancelQuietly(_resumeCts);
            CancelQuietly(_networkCts);
        }

        _logger.LogInformation("Sistem uykuya geçiyor (motor çalışıyordu: {Running}).", wasRunning);
        if (wasRunning)
        {
            _ = Task.Run(StopCoreAsync);
        }
    }

    /// <summary>Uyanış: ağın gelmesini bekle, oturumu doğrula, kullanıcı çalıştırıyorduysa motoru yeniden başlat.</summary>
    private void OnResumed()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _suspended = false;
            _connectivity.Resume(_power.ResumeDelay); // uyanışta ağ henüz gelmemiş olabilir: bu süre "koptu" denmez
            CancelQuietly(_networkCts);
            CancelQuietly(_resumeCts);
            cts = _resumeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }

        _logger.LogInformation("Sistem uyandı; {Delay:0} sn sonra oturum doğrulanıp motor toparlanacak.", _power.ResumeDelay.TotalSeconds);
        _ = Task.Run(() => RecoverAfterResumeAsync(cts));
    }

    private async Task RecoverAfterResumeAsync(CancellationTokenSource cts)
    {
        try
        {
            var ct = cts.Token;
            await Task.Delay(_power.ResumeDelay, _time, ct).ConfigureAwait(false);

            // Uykudan önce kalan bağlantılar ölüdür: ne çalışıyorsa durdur (kullanıcının "çalışsın" isteği korunur).
            await StopCoreAsync().ConfigureAwait(false);
            AccountStatus status;
            lock (_gate)
            {
                status = _accountState.Status;
            }

            // Yalnızca zaten bir oturumumuz olan durumlarda doğrula; giriş ekranını/cihaz kodunu bozma.
            if (status is AccountStatus.LoggedIn or AccountStatus.Offline)
            {
                await RestoreAsync(ct).ConfigureAwait(false); // Restored → StartIfWanted; Offline → periyodik yeniden deneme
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Uyanış sonrası toparlama başarısız");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_resumeCts, cts))
                {
                    _resumeCts = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Bağlantı izleyicisi durumu değiştirdi. Koptuysa: arayüze "bağlantı yok" + hesap "çevrimdışı" + bildirim (motor kendi hata
    /// durumuna düşer; ona dokunulmaz). Geldiyse: bildirim + hemen toparlama (<see cref="RecoverAfterNetworkAsync"/>).
    /// </summary>
    private void OnConnectivityChanged(ConnectivityChange change)
    {
        UserNotification? notification = null;
        CancellationTokenSource? recovery = null;
        lock (_gate)
        {
            CancelQuietly(_networkCts);
            if (change.Online)
            {
                if (_lostNotified)
                {
                    _lostNotified = false;
                    notification = UserNotification.ConnectionRestored(change.Outage, _wantRunning);
                }

                recovery = _networkCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            }
            else
            {
                CancelQuietly(_restoreCts);
                _lostNotified = true;
                notification = UserNotification.ConnectionLost(_wantRunning);
                if (_accountState.Status == AccountStatus.LoggedIn)
                {
                    _accountState = new AccountSnapshot(AccountStatus.Offline, _accountState.Login);
                }
            }
        }

        RaiseChanged();
        if (notification is not null)
        {
            RaiseNotification(notification);
        }

        if (recovery is not null)
        {
            _ = Task.Run(() => RecoverAfterNetworkAsync(change.Outage, recovery));
        }
    }

    /// <summary>
    /// Bağlantı geri geldi (yoklamayla doğrulandı → ilk istek başarılı olur, yeniden deneme zamanlayıcısı beklenmez). Hesap "çevrimdışı"ysa
    /// oturumu doğrula (başarılıysa kullanıcı çalıştırıyorduysa motor başlar); motor çalışıyor ve hata durumundaysa ya da kesinti uzunsa
    /// yeniden başlat (izleyici pes edip kanalı engellemiş olabilir); yalnız envanter bayatsa yenile. Kısa kesintide sağlıklı motora dokunma.
    /// </summary>
    private async Task RecoverAfterNetworkAsync(TimeSpan outage, CancellationTokenSource cts)
    {
        try
        {
            var ct = cts.Token;
            if (_power.NetworkSettle > TimeSpan.Zero)
            {
                await Task.Delay(_power.NetworkSettle, _time, ct).ConfigureAwait(false);
            }

            AccountStatus status;
            lock (_gate)
            {
                if (_suspended)
                {
                    return;
                }

                status = _accountState.Status;
            }

            if (status == AccountStatus.Offline)
            {
                await RestoreAsync(ct).ConfigureAwait(false); // Restored → LoggedIn → StartIfWanted
            }

            bool restart;
            bool stale;
            lock (_gate)
            {
                restart = _running && !_suspended && (_engine.State == EngineState.Degraded || outage >= _power.OutageRestartThreshold);
                stale = _refreshError is not null && _accountState.Status == AccountStatus.LoggedIn;
            }

            if (restart)
            {
                _logger.LogInformation("Ağ geri geldi; motor yeniden başlatılıyor.");
                await StopCoreAsync().ConfigureAwait(false);
                StartIfWanted();
            }
            else if (stale)
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Ağ sonrası toparlama başarısız");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_networkCts, cts))
                {
                    _networkCts = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>Kullanıcı motoru istiyorsa (ve uykuda değilsek, çalışmıyorsa) başlatır. Giriş yoksa sessizce vazgeçer.</summary>
    private void StartIfWanted()
    {
        bool want;
        lock (_gate)
        {
            want = _wantRunning && !_suspended && !_running;
        }

        if (want && Start())
        {
            _logger.LogInformation("Motor otomatik olarak yeniden başlatıldı.");
        }
    }

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // İş bitmiş ve kaynağını bırakmış; yapılacak bir şey yok.
        }
    }

    /// <summary>Çağıran <c>_gate</c>'i tutar. Önbellekteki envanter başka bir hesaba aitse gösterilmez.</summary>
    private void DropForeignInventory(string userId)
    {
        if (_inventory is not null && _inventoryOwner != userId)
        {
            _inventory = null;
        }

        _inventoryOwner = _inventory is null ? null : userId;
    }

    /// <summary>Oturum doğrulanamadı/yok: kimin olduğu belirsiz önbellek envanteri gösterilmez (dosya kalır, aynı hesap dönerse kullanılır).</summary>
    private void DiscardCachedInventory()
    {
        lock (_gate)
        {
            _inventory = null;
            _inventoryOwner = null;
        }
    }

    private void ScheduleOfflineRetry() =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_offlineRetry, _time, _lifetime.Token).ConfigureAwait(false);
                if (_accountState.Status == AccountStatus.Offline)
                {
                    await RestoreAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });

    private async Task RunAsync(DropEngine engine, IDropConnection connection, IDisposable observing, CancellationTokenSource runCts)
    {
        var cancellationToken = runCts.Token;
        try
        {
            var inventoryLoop = InventoryLoopAsync(connection, cancellationToken);
            await Task.WhenAll(engine.RunAsync(cancellationToken), inventoryLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Motor kendi hatalarını yönetir; buraya düşen beklenmeyen bir şeydir: günlüğe yaz, çökme.
            _logger.LogError(ex, "Motor beklenmedik şekilde sonlandı");
        }
        finally
        {
            engine.Changed -= OnEngineChanged;
            observing.Dispose();
            connection.Dispose();
            runCts.Dispose();
            lock (_gate)
            {
                _engine = engine.Snapshot;
                _running = false;
            }

            _logger.LogInformation("Motor durdu.");
            RaiseChanged();
        }
    }

    /// <summary>Çalışırken envanteri aralıklarla ve claim sonrası hemen yeniler (sunucu-gerçeği).</summary>
    private async Task InventoryLoopAsync(IDropConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task nudge;
                lock (_gate)
                {
                    nudge = _nudge.Task;
                }

                await Task.WhenAny(Task.Delay(_inventoryInterval, _time, cancellationToken), nudge).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_nudge.Task.IsCompleted)
                    {
                        _nudge = NewNudge();
                    }
                }

                try
                {
                    var inventory = await connection.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
                    string? userId;
                    lock (_gate)
                    {
                        _inventory = inventory;
                        userId = _inventoryOwner = _session?.UserId;
                        _refreshError = null;
                        _historyClaimed = [.. _factory.LoadClaimedDropIds()];
                    }

                    if (userId is not null)
                    {
                        _state?.SaveInventory(userId, inventory);
                    }

                    RaiseChanged();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && (Retry.IsTransient(ex) || ex is PlatformDataException || ex is PlatformAuthException))
                {
                    _logger.LogWarning("Envanter yenilenemedi: {Reason}", ex.Message);
                    SetRefreshError(Loc.T("service.inventoryFailed", ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnDiscovered(PlatformSnapshot snapshot)
    {
        lock (_gate)
        {
            _campaigns = snapshot;
        }

        RaiseChanged();
    }

    private void OnEngineChanged(EngineSnapshot snapshot)
    {
        var recover = false;
        UserNotification? notification = null;
        lock (_gate)
        {
            _engine = snapshot;
            if (snapshot.ClaimedThisRun > _lastClaimCount)
            {
                _lastClaimCount = snapshot.ClaimedThisRun;
                _nudge.TrySetResult();
            }

            if (snapshot.State == EngineState.Degraded)
            {
                _connectivity.CheckNow(); // motor hata aldı: bağlantı koptuysa bunu hemen öğren
            }

            switch (snapshot.State)
            {
                case EngineState.Idle:
                    _idleSeen = true;
                    break;
                case EngineState.Watching when _idleSeen && snapshot.Target is { } target:
                    // Boşta → İzliyor: bir kampanya başladı (ya da kanal çıktı). Uygulama kampanya ortasında açıldıysa (boşta yok) bildirilmez.
                    _idleSeen = false;
                    notification = UserNotification.CampaignStarted(target.Campaign.Id, target.Campaign.Name, target.Channel.DisplayName);
                    break;
                case EngineState.ClaimPending when !_autoClaimThisRun && snapshot.Session is { } session && _claimReadyDropId != session.DropId:
                    _claimReadyDropId = session.DropId;
                    notification = UserNotification.ClaimReady(session.DropId, RewardName(session.DropId), _profile);
                    break;
            }

            if (snapshot.State == EngineState.AuthRequired)
            {
                recover = !_authRecovering;
                _authRecovering = true;
            }
            else
            {
                _authRecovering = false;
            }
        }

        RaiseChanged();
        if (notification is not null)
        {
            RaiseNotification(notification);
        }

        if (recover)
        {
            _ = RecoverAuthAsync();
        }
    }

    private void OnClaimNotice(ClaimNotice notice)
    {
        var name = RewardName(notice.DropId);
        RaiseNotification(notice.Success ? UserNotification.DropClaimed(notice.DropId, name) : UserNotification.ClaimFailed(notice.DropId, name, _profile));
    }

    /// <summary>Drop kimliğinden ödül adı (keşfedilen kampanyalardan, sonra envanterden); bulunamazsa genel bir ad.</summary>
    private string RewardName(string dropId)
    {
        lock (_gate)
        {
            var reward = _campaigns?.Campaigns.SelectMany(c => c.Rewards).FirstOrDefault(r => r.Id == dropId);
            if (reward is not null && !string.IsNullOrWhiteSpace(reward.Name))
            {
                return reward.Name;
            }

            var progress = _inventory?.InProgress.FirstOrDefault(p => p.DropId == dropId);
            return progress is not null && !string.IsNullOrWhiteSpace(progress.RewardName) ? progress.RewardName : "Drop";
        }
    }

    private void RaiseNotification(UserNotification notification)
    {
        try
        {
            Notification?.Invoke(notification);
        }
        catch (Exception ex)
        {
            // Bildirim gösterilemese de motor etkilenmemeli.
            _logger.LogError(ex, "Bildirim dinleyicisi hata verdi");
        }
    }

    /// <summary>
    /// Kimlik reddedilince bir kez sessizce oturumu yenilemeyi dener (refresh token); olmazsa hesap "yeniden giriş gerekli" olur.
    /// Token geçerli görünüp yine de reddediliyorsa da kullanıcıdan yeniden giriş istenir.
    /// </summary>
    private async Task RecoverAuthAsync()
    {
        string? oldToken;
        lock (_gate)
        {
            oldToken = _session?.AccessToken;
        }

        try
        {
            var result = await _account.RestoreAsync(_lifetime.Token).ConfigureAwait(false);
            if (result.Status == TwitchRestoreStatus.Restored && result.Session is { } session && session.AccessToken != oldToken)
            {
                lock (_gate)
                {
                    _session = session;
                    _authRecovering = false;
                }

                _logger.LogInformation("{Platform} oturumu sessizce yenilendi.", _profile.DisplayName);
                SetAccount(new AccountSnapshot(AccountStatus.LoggedIn, session.Login));
                return;
            }

            if (result.Status == TwitchRestoreStatus.Offline)
            {
                SetAccount(new AccountSnapshot(AccountStatus.Offline));
                ScheduleOfflineRetry();
                return;
            }

            SetAccount(new AccountSnapshot(AccountStatus.NeedsLogin));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Oturum kurtarma başarısız");
            SetAccount(new AccountSnapshot(AccountStatus.NeedsLogin, Error: ex.Message));
        }
    }

    private void SetAccount(AccountSnapshot state)
    {
        var becameNeedsLogin = false;
        lock (_gate)
        {
            // Giriş gerekli bildirimi, sonraki başarılı girişe kadar bir kez (arada Restoring/Offline dalgalanması tekrar bildirmez).
            if (state.Status == AccountStatus.LoggedIn)
            {
                _needsLoginNotified = false;
            }
            else if (state.Status == AccountStatus.NeedsLogin && !_needsLoginNotified)
            {
                _needsLoginNotified = becameNeedsLogin = true;
            }

            _accountState = state;
        }

        RaiseChanged();
        if (becameNeedsLogin)
        {
            RaiseNotification(UserNotification.LoginRequired(_profile));
        }
    }

    private void SetRefreshError(string message)
    {
        lock (_gate)
        {
            _refreshError = message;
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // Bir dinleyicinin hatası motoru/servisi etkilememeli.
            _logger.LogError(ex, "Changed dinleyicisi hata verdi");
        }
    }

    /// <summary>Platformu sarar: keşif sonuçlarını arayüze iletmek için (motor değişmeden).</summary>
    private sealed class ObservingPlatform : IDropPlatform, IDisposable
    {
        private readonly IDropPlatform inner;
        private readonly Action<PlatformSnapshot> onDiscovered;

        public ObservingPlatform(IDropPlatform inner, Action<PlatformSnapshot> onDiscovered, Action<ClaimNotice> onClaim)
        {
            this.inner = inner;
            this.onDiscovered = onDiscovered;
            this.onClaim = onClaim;
            inner.DropClaimed += onClaim;
        }

        private readonly Action<ClaimNotice> onClaim;

        /// <summary>Motor durunca dinleyiciyi bırakır (aynı platform nesnesi yeniden kullanılırsa eski çalışmanın bildirimi tekrarlanmasın).</summary>
        public void Dispose() => inner.DropClaimed -= onClaim;

        public Platform Platform => inner.Platform;

        public event Action<string>? ChannelWentOffline
        {
            add => inner.ChannelWentOffline += value;
            remove => inner.ChannelWentOffline -= value;
        }

        public event Action<ClaimNotice>? DropClaimed
        {
            add => inner.DropClaimed += value;
            remove => inner.DropClaimed -= value;
        }

        public async Task<PlatformSnapshot> DiscoverAsync(string gameSlug, CancellationToken cancellationToken)
        {
            var snapshot = await inner.DiscoverAsync(gameSlug, cancellationToken).ConfigureAwait(false);
            onDiscovered(snapshot);
            return snapshot;
        }

        public IWatcher CreateWatcher() => inner.CreateWatcher();

        public Task<DropSession?> GetSessionAsync(LiveChannel channel, CancellationToken cancellationToken) =>
            inner.GetSessionAsync(channel, cancellationToken);

        public void OnTargetChanged(LiveChannel? channel) => inner.OnTargetChanged(channel);

        public Task RunBackgroundAsync(CancellationToken cancellationToken) => inner.RunBackgroundAsync(cancellationToken);
    }
}
