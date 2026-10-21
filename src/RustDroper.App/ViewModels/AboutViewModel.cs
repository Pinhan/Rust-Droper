using System.Diagnostics;
using System.Reflection;
using RustDroper.Core.Localization;

namespace RustDroper.App.ViewModels;

/// <summary>"Hakkında" sayfası: sürüm, geliştirici/iletişim, ne yaptığı, gizlilik, sorumluluk reddi, üçüncü taraf bileşenler. Ağ/hesap verisi içermez.</summary>
public sealed class AboutViewModel : ViewModelBase
{
    public const string AppName = "Rust Droper";
    public const string Author = "Pinhan";
    public const string ContactEmail = "thepinhan@gmail.com";

    private Action<string> _clipboard = _ => { };
    private Action<string> _openPath = _ => { };
    private string _dataDirectory = string.Empty;
    private string _logsDirectory = string.Empty;
    private string? _copied;

    public AboutViewModel()
    {
        ObserveLanguage();
        MailCommand = new RelayCommand(_ => OpenMail());
        CopyMailCommand = new RelayCommand(_ =>
        {
            _clipboard(ContactEmail);
            Copied = Loc.T("about.copied");
        });
        OpenDataFolderCommand = new RelayCommand(_ => Open(_dataDirectory), _ => _dataDirectory.Length > 0);
        OpenLogsFolderCommand = new RelayCommand(_ => Open(_logsDirectory), _ => _logsDirectory.Length > 0);
    }

    /// <summary>Uygulama kurulurken: gerçek klasörler ve eylemler (pano, dosya yöneticisi). Testte sahte verilir.</summary>
    public void Configure(string dataDirectory, string logsDirectory, Action<string> clipboard, Action<string> openPath)
    {
        _dataDirectory = dataDirectory;
        _logsDirectory = logsDirectory;
        _clipboard = clipboard;
        _openPath = openPath;
        OpenDataFolderCommand.RaiseCanExecuteChanged();
        OpenLogsFolderCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(DataDirectory));
    }

    public RelayCommand MailCommand { get; }

    public RelayCommand CopyMailCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public RelayCommand OpenLogsFolderCommand { get; }

    public string Title => AppName;

    public string Tagline => Loc.T("about.tagline");

    public string VersionText => Loc.T("about.version", Version);

    public static string Version
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
                ?? "0.0.0";
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? info[..plus] : info;
        }
    }

    /// <summary>Pencerenin en altındaki satır: ad ve sürüm ("Rust Droper v1.0.1"); sürüm derleme sürümünden (Directory.Build.props) gelir.</summary>
    public string FooterText => Loc.T("about.footer", AppName, Version);

    public string Developer => Loc.T("about.developer", Author);

    public string Contact => Loc.T("about.contact", ContactEmail);

    public string Copyright => Loc.T("about.copyright", Author);

    public string DataDirectory => _dataDirectory;

    public string? Copied
    {
        get => _copied;
        private set => SetProperty(ref _copied, value);
    }

    public string WhatItDoes => Loc.T("about.whatBody");

    public string Privacy => Loc.T("about.privacyBody");

    public string Disclaimer => Loc.T("about.noteBody");

    public IReadOnlyList<string> ThirdParty =>
    [
        Loc.T("about.third.dotnet"),
        Loc.T("about.third.webview2"),
        Loc.T("about.third.qrcoder"),
    ];

    /// <summary>Dil değişince geçici "kopyalandı" iletisi silinir; diğer metinler yeniden okunur.</summary>
    protected override void OnLanguageChanged()
    {
        _copied = null;
        base.OnLanguageChanged();
    }

    private void OpenMail()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"mailto:{ContactEmail}?subject=Rust%20Droper%20{Version}") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Varsayılan e-posta uygulaması yoksa adresi panoya kopyala.
            _clipboard(ContactEmail);
            Copied = Loc.T("about.noMailApp");
        }
    }

    private void Open(string path)
    {
        if (path.Length > 0)
        {
            _openPath(path);
        }
    }
}
