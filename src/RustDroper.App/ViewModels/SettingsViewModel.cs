using RustDroper.App.Infrastructure;
using RustDroper.Core.Configuration;
using RustDroper.Core.Localization;

namespace RustDroper.App.ViewModels;

/// <summary>
/// Ayarlar sayfası. Her değişiklik anında <see cref="SettingsService"/>'e (data\settings.json) yazılır; "Windows ile başlat"
/// ayrıca HKCU\...\Run kaydını günceller (başarısız olursa anahtar geri alınır ve <see cref="Error"/> gösterilir).
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IAutostart _autostart;
    private string? _error;

    public SettingsViewModel(SettingsService settings, IAutostart autostart)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(autostart);
        _settings = settings;
        _autostart = autostart;
        _settings.Changed += _ => Refresh();
        ObserveLanguage();
        LanguageOptions =
        [
            new LanguageOption(this, SupportedLanguages.Auto, "settings.language.auto"),
            new LanguageOption(this, SupportedLanguages.Turkish, "settings.language.tr"),
            new LanguageOption(this, SupportedLanguages.English, "settings.language.en"),
        ];
        TestNotificationCommand = new RelayCommand(_ => TestNotificationRequested?.Invoke());
    }

    /// <summary>"Bildirimi dene" istendi (uygulama bir deneme balonu gösterir).</summary>
    public event Action? TestNotificationRequested;

    public RelayCommand TestNotificationCommand { get; }

    /// <summary>Dil seçenekleri: Otomatik (Windows) · Türkçe · English. Etiketler arayüz diliyle birlikte yenilenir.</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    /// <summary>Seçili dil ayarı: "auto" | "tr" | "en". Değiştirince ayar kaydedilir ve arayüz ANINDA yeni dile geçer (yeniden başlatma gerekmez).</summary>
    public string Language
    {
        get => SupportedLanguages.Normalize(_settings.Current.Language);
        set
        {
            var code = SupportedLanguages.Normalize(value);
            if (string.IsNullOrEmpty(value) || code == Language)
            {
                return; // ComboBox öğe listesi yenilenirken gelen boş değer ayarı bozmasın
            }

            _settings.Update(s => s with { Language = code });
            Loc.Configure(code);
        }
    }

    protected override void OnLanguageChanged()
    {
        foreach (var option in LanguageOptions)
        {
            option.Refresh();
        }

        base.OnLanguageChanged();
    }

    public bool StartWithWindows
    {
        get => _settings.Current.StartWithWindows;
        set
        {
            if (value == _settings.Current.StartWithWindows)
            {
                return;
            }

            try
            {
                _autostart.SetEnabled(value);
                Error = null;
                _settings.Update(s => s with { StartWithWindows = value });
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Error = Loc.T("settings.error.autostart", ex.Message);
                OnPropertyChanged();
            }
        }
    }

    public bool StartMinimized
    {
        get => _settings.Current.StartMinimized;
        set => _settings.Update(s => s with { StartMinimized = value });
    }

    public bool AutoClaim
    {
        get => _settings.Current.AutoClaim;
        set => _settings.Update(s => s with { AutoClaim = value });
    }

    public bool Notifications
    {
        get => _settings.Current.Notifications;
        set => _settings.Update(s => s with { Notifications = value });
    }

    /// <summary>Başlat'a basınca "aynı hesapla başka canlı yayın izlemeyin" uyarısını göster (ters: <c>HideStartNotice</c>).</summary>
    public bool ShowStartNotice
    {
        get => !_settings.Current.HideStartNotice;
        set => _settings.Update(s => s with { HideStartNotice = !value });
    }

    public bool KeepAwake
    {
        get => _settings.Current.KeepAwake;
        set => _settings.Update(s => s with { KeepAwake = value });
    }

    public bool RustRewardsOnly
    {
        get => _settings.Current.RustRewardsOnly;
        set => _settings.Update(s => s with { RustRewardsOnly = value });
    }

    public bool AutoRestart
    {
        get => _settings.Current.AutoRestart;
        set => _settings.Update(s => s with { AutoRestart = value });
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(StartMinimized));
        OnPropertyChanged(nameof(AutoClaim));
        OnPropertyChanged(nameof(Notifications));
        OnPropertyChanged(nameof(KeepAwake));
        OnPropertyChanged(nameof(AutoRestart));
        OnPropertyChanged(nameof(RustRewardsOnly));
        OnPropertyChanged(nameof(ShowStartNotice));
        OnPropertyChanged(nameof(Language));
        foreach (var option in LanguageOptions)
        {
            option.Refresh();
        }
    }
}

/// <summary>Dil seçici öğesi (radyo düğmesi): kod ("auto"/"tr"/"en") + yerelleştirilmiş etiket + seçili mi.</summary>
public sealed class LanguageOption(SettingsViewModel owner, string code, string labelKey) : ViewModelBase
{
    public string Code { get; } = code;

    public string Label => Loc.T(labelKey);

    public bool IsSelected
    {
        get => owner.Language == Code;
        set
        {
            if (value)
            {
                owner.Language = Code;
            }
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsSelected));
    }
}
