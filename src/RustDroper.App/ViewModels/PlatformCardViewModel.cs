using System.Windows.Input;
using RustDroper.Core.Domain;
using RustDroper.Core.Infrastructure;
using RustDroper.Core.Localization;
using RustDroper.Core.Orchestration;
using RustDroper.Core.Watching;

namespace RustDroper.App.ViewModels;

/// <summary>
/// Bir platformun (Twitch/Kick) bağlantı kartı: durum metni, tek eylem düğmesi ve giriş paneli.
/// Twitch: cihaz kodu + etkinleştirme sayfası. Kick: telefondaki Kick uygulamasıyla okutulacak QR (bağlantı masaüstü tarayıcıda 404 verir).
/// </summary>
public sealed class PlatformCardViewModel : ViewModelBase
{
    private readonly ICommand? _login;
    private readonly ICommand? _logout;
    private readonly ICommand? _cancel;
    private readonly ICommand? _retry;
    private readonly Platform _platform;
    private string _statusText;
    private Severity _severity = Severity.Neutral;
    private string? _actionLabel;
    private ICommand? _actionCommand;
    private string? _deviceCode;
    private Uri? _activateUrl;
    private byte[]? _qrPng;
    private string? _qrFor;
    private string? _error;
    private readonly Action<string>? _clipboard;
    private readonly TimeProvider _time = TimeProvider.System;
    private int _copyVersion;
    private bool _codeCopied;
    private bool _linkCopied;
    private bool _isLoggedIn;
    private string? _playerSessionText;
    private Severity _playerSessionSeverity = Severity.Neutral;
    private AccountSnapshot _lastAccount = AccountSnapshot.Initial;
    private KickPlayerSessionStatus? _lastPlayerStatus;
    private ActionKind _actionKind;

    private enum ActionKind
    {
        None,
        Login,
        Logout,
        Cancel,
        Relogin,
        Retry,
    }

    /// <summary>Henüz desteklenmeyen platform: devre dışı, "yakında".</summary>
    public PlatformCardViewModel(string name, string comingSoonText)
    {
        ObserveLanguage();
        Name = name;
        IsAvailable = false;
        _platform = Platform.Twitch;
        _statusText = comingSoonText;
    }

    public PlatformCardViewModel(
        string name,
        ICommand login,
        ICommand logout,
        ICommand cancel,
        ICommand retry,
        Platform platform = Platform.Twitch,
        Action<string>? clipboard = null,
        ICommand? playerLogin = null,
        ICommand? playerRefresh = null,
        TimeProvider? time = null)
    {
        ObserveLanguage();
        Name = name;
        IsAvailable = true;
        _platform = platform;
        _login = login;
        _logout = logout;
        _cancel = cancel;
        _retry = retry;
        _clipboard = clipboard;
        _time = time ?? TimeProvider.System;
        if (clipboard is not null)
        {
            CopyCodeCommand = new RelayCommand(_ => Copy(DeviceCode, code: true));
            CopyLinkCommand = new RelayCommand(p => Copy((p as Uri ?? ActivateUrl)?.ToString(), code: false));
        }

        PlayerLoginCommand = playerLogin;
        PlayerRefreshCommand = playerRefresh;
        if (playerLogin is not null)
        {
            _playerSessionText = Loc.T("card.player.unchecked");
        }

        _statusText = Loc.T("card.checking");
        Update(AccountSnapshot.Initial);
    }

    public string Name { get; }

    public bool IsAvailable { get; }

    /// <summary>Hesap bağlı (giriş yapılmış): Facepunch/Steam bağlantı satırı yalnız bu durumda gösterilir.</summary>
    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set => SetProperty(ref _isLoggedIn, value);
    }

    /// <summary>Platformun resmî drop sitesi (sabit adres; varsayılan tarayıcıda açılır).</summary>
    public Uri SiteUrl => _platform == Platform.Kick ? KickSite : TwitchSite;

    public string SiteTooltip => Loc.T("card.siteTip", SiteUrl.Host);

    public static readonly Uri TwitchSite = new("https://twitch.facepunch.com/");

    public static readonly Uri KickSite = new("https://kick.facepunch.com/");

    /// <summary>Kick kartı (marka işareti ve platforma özgü paneller için).</summary>
    public bool IsKick => _platform == Platform.Kick;

    /// <summary>Devre dışı ("yakında") kart soluk gösterilir.</summary>
    public double CardOpacity => IsAvailable ? 1.0 : 0.6;

    /// <summary>"Bağlantıyı kopyala" (Kick: bağlantıyı telefona göndermek için); parametre <see cref="Uri"/> (yoksa etkinleştirme adresi).</summary>
    public ICommand? CopyLinkCommand { get; }

    /// <summary>"Kopyala": giriş sırasında gösterilen kodu (<see cref="DeviceCode"/>) panoya koyar; panoya YALNIZ bu kod yazılır, günlüğe yazılmaz.</summary>
    public ICommand? CopyCodeCommand { get; }

    /// <summary>Kod kopyalandıktan sonra ~2 sn "Kopyalandı ✓" geri bildirimi.</summary>
    public bool CodeCopied
    {
        get => _codeCopied;
        private set => SetProperty(ref _codeCopied, value);
    }

    /// <summary>Bağlantı kopyalandıktan sonra ~2 sn "Kopyalandı ✓" geri bildirimi.</summary>
    public bool LinkCopied
    {
        get => _linkCopied;
        private set => SetProperty(ref _linkCopied, value);
    }

    private void Copy(string? text, bool code)
    {
        if (string.IsNullOrEmpty(text) || _clipboard is null)
        {
            return;
        }

        _clipboard(text);
        var version = Interlocked.Increment(ref _copyVersion);
        SetCopied(code, true);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _time).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Volatile.Read(ref _copyVersion) == version)
            {
                SetCopied(code, false);
            }
        });
    }

    private void SetCopied(bool code, bool value)
    {
        if (code)
        {
            CodeCopied = value;
        }
        else
        {
            LinkCopied = value;
        }
    }

    /// <summary>"Oynatıcı girişi": Kick'in gerçek web oynatıcısının (gizli WebView2) oturumu için görünür giriş penceresi; yalnız Kick kartında.</summary>
    public ICommand? PlayerLoginCommand { get; }

    /// <summary>Oynatıcı oturumunu yeniden yoklar (kısa süreli gizli tarayıcı).</summary>
    public ICommand? PlayerRefreshCommand { get; }

    /// <summary>"Oynatıcı oturumu: açık/yok/…" satırı; oynatıcı oturumu olmayan kartta <c>null</c> (satır gizlenir).</summary>
    public string? PlayerSessionText
    {
        get => _playerSessionText;
        private set => SetProperty(ref _playerSessionText, value);
    }

    public Severity PlayerSessionSeverity
    {
        get => _playerSessionSeverity;
        private set => SetProperty(ref _playerSessionSeverity, value);
    }

    public void UpdatePlayerSession(KickPlayerSessionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (PlayerLoginCommand is null)
        {
            return;
        }

        _lastPlayerStatus = status;
        (PlayerSessionText, PlayerSessionSeverity) = status.State switch
        {
            KickPlayerSessionState.LoggedIn => (
                status.Detail is { Length: > 0 } detail ? Loc.T("card.player.open.detail", detail) : Loc.T("card.player.open"),
                Severity.Ok),
            KickPlayerSessionState.LoggedOut => (Loc.T("card.player.none"), Severity.Warn),
            KickPlayerSessionState.Checking => (Loc.T("card.player.checking"), Severity.Info),
            KickPlayerSessionState.Unavailable => (Loc.T("card.player.unavailable"), Severity.Error),
            _ => (
                status.Detail is { Length: > 0 } detail ? Loc.T("card.player.unverified.detail", detail) : Loc.T("card.player.unverified"),
                Severity.Neutral),
        };
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Severity Severity
    {
        get => _severity;
        private set => SetProperty(ref _severity, value);
    }

    /// <summary>Tek eylem düğmesinin etiketi (yoksa <c>null</c>: düğme gösterilmez).</summary>
    public string? ActionLabel
    {
        get => _actionLabel;
        private set
        {
            if (SetProperty(ref _actionLabel, value))
            {
                OnPropertyChanged(nameof(ActionIcon));
            }
        }
    }

    /// <summary>Eylem düğmesinin ikonu (Themes\Icons.xaml kaynak anahtarı).</summary>
    public string ActionIcon => _actionKind switch
    {
        ActionKind.Logout => "IconLogOut",
        ActionKind.Cancel => "IconClose",
        ActionKind.Retry => "IconRefresh",
        _ => "IconLogIn",
    };

    public ICommand? ActionCommand
    {
        get => _actionCommand;
        private set => SetProperty(ref _actionCommand, value);
    }

    /// <summary>Twitch girişi sürerken kullanıcıya gösterilen kod (ör. "ABCD-EFGH"); Kick'te <c>null</c> (QR kullanılır).</summary>
    public string? DeviceCode
    {
        get => _deviceCode;
        private set => SetProperty(ref _deviceCode, value);
    }

    /// <summary>Giriş sürerken açılacak/okutulacak adres (Twitch: etkinleştirme sayfası; Kick: uygulamayla açılacak tam bağlantı).</summary>
    public Uri? ActivateUrl
    {
        get => _activateUrl;
        private set => SetProperty(ref _activateUrl, value);
    }

    /// <summary>Kick girişi sürerken QR (PNG baytları, yerelde üretilir); yoksa <c>null</c>.</summary>
    public byte[]? QrPng
    {
        get => _qrPng;
        private set => SetProperty(ref _qrPng, value);
    }

    /// <summary>Twitch: etkinleştirme sayfası bağlantı metni.</summary>
    public string LoginLinkText => Loc.T("card.loginLink");

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public void Update(AccountSnapshot account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!IsAvailable)
        {
            return;
        }

        _lastAccount = account;
        IsLoggedIn = account.Status == AccountStatus.LoggedIn;
        var loggingIn = Loc.T(_platform == Platform.Kick ? "card.loggingIn.kick" : "card.loggingIn.twitch");
        var (text, severity, kind, command) = account.Status switch
        {
            AccountStatus.LoggedIn => (Loc.T("card.connected", account.Login), Severity.Ok, ActionKind.Logout, _logout),
            AccountStatus.LoggingIn => (loggingIn, Severity.Info, ActionKind.Cancel, _cancel),
            AccountStatus.NeedsLogin => (Loc.T("card.needsLogin"), Severity.Error, ActionKind.Relogin, _login),
            AccountStatus.Offline => (
                account.Login is { Length: > 0 } login
                    ? Loc.T("card.offline.login", login)
                    : Loc.T("card.offline"),
                Severity.Warn,
                ActionKind.Retry,
                _retry),
            AccountStatus.LoggedOut => (Loc.T("card.loggedOut"), Severity.Neutral, ActionKind.Login, _login),
            _ => (Loc.T("card.checking"), Severity.Neutral, ActionKind.None, (ICommand?)null),
        };
        _actionKind = kind;
        (StatusText, Severity, ActionLabel, ActionCommand) = (text, severity, ActionText(kind), command);

        var code = account.Status == AccountStatus.LoggingIn ? account.DeviceCode : null;
        if (code is null)
        {
            CodeCopied = false; // giriş bitti: geri bildirim kalmasın
            LinkCopied = false;
        }

        DeviceCode = _platform == Platform.Twitch ? code?.UserCode : null;
        ActivateUrl = code?.VerificationUri;
        UpdateQr(_platform == Platform.Kick ? code?.VerificationUri : null);
        Error = account.Error;
    }

    private static string? ActionText(ActionKind kind) => kind switch
    {
        ActionKind.Login => Loc.T("card.action.login"),
        ActionKind.Logout => Loc.T("card.action.logout"),
        ActionKind.Cancel => Loc.T("card.action.cancel"),
        ActionKind.Relogin => Loc.T("card.action.relogin"),
        ActionKind.Retry => Loc.T("card.action.retry"),
        _ => null,
    };

    /// <summary>Dil değişti: durum, eylem düğmesi ve oynatıcı oturumu satırı son verilerle yeniden yazılır (QR yeniden üretilmez).</summary>
    protected override void OnLanguageChanged()
    {
        if (IsAvailable)
        {
            Update(_lastAccount);
            if (_lastPlayerStatus is { } player)
            {
                UpdatePlayerSession(player);
            }
            else if (PlayerLoginCommand is not null)
            {
                PlayerSessionText = Loc.T("card.player.unchecked");
            }
        }

        base.OnLanguageChanged();
    }

    /// <summary>Aynı bağlantı için QR bir kez üretilir (her görüntü güncellemesinde yeniden üretilmez).</summary>
    private void UpdateQr(Uri? link)
    {
        if (link is null)
        {
            _qrFor = null;
            QrPng = null;
            return;
        }

        var text = link.ToString();
        if (_qrFor != text)
        {
            _qrFor = text;
            QrPng = QrCodes.ToPng(text, 8);
        }
    }
}
