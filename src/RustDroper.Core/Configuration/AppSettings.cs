using RustDroper.Core.Infrastructure;

namespace RustDroper.Core.Configuration;

/// <summary>
/// Kullanıcı ayarları (<c>data\settings.json</c>). Değişmez kayıt: değişiklik <see cref="SettingsService.Update"/> ile yapılır.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Arayüz dili: "auto" (Windows görüntü dili Türkçe ise Türkçe, değilse İngilizce) | "tr" | "en". Yeni kurulumda "auto".</summary>
    public string Language { get; init; } = SupportedLanguages.Auto;

    /// <summary>Windows oturumu açılınca başlat (HKCU\...\Run).</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>Uygulama başlayınca pencereyi açma, doğrudan tepsiye küçül.</summary>
    public bool StartMinimized { get; init; }

    /// <summary>
    /// Tamamlanan drop'ları otomatik claim et. <b>Varsayılan AÇIK</b> (kullanıcı kararı, 2026-10-20: geri alınamaz oyun eşyası riskini kabul etti; kapalıyken
    /// bilgisayar başında olunmayan sürede ödüller etkinlik bitince kaçabilir).
    /// </summary>
    public bool AutoClaim { get; init; } = true;

    /// <summary>Varsayılanın "açık"a çevrilmesi eski ayar dosyalarına BİR KEZ uygulandı mı (sonra kullanıcının seçimi korunur).</summary>
    public bool AutoClaimDefaultMigrated { get; init; }

    /// <summary>Tepsi balonu bildirimleri (claim edildi/başarısız, giriş gerekli, kampanya başladı). Varsayılan AÇIK.</summary>
    public bool Notifications { get; init; } = true;

    /// <summary>Motor çalışırken bilgisayarın uykuya geçmesini engeller (ekran kapanabilir). Uyuyan bilgisayar akış izleyemez. Varsayılan AÇIK.</summary>
    public bool KeepAwake { get; init; } = true;

    /// <summary>
    /// Oyun Rust iken yalnız Rust ödülü veren kampanyalar izlenir/gösterilir (Rust yayınında bulunan Rust-dışı yayıncı kampanyaları, ör. sohbet rozeti, elenir).
    /// Varsayılan AÇIK. Yanlış eleme şüphesinde kapatılabilir.
    /// </summary>
    public bool RustRewardsOnly { get; init; } = true;

    /// <summary>Motor çalışırken uygulama çökerse/beklenmedik kapanırsa kendiliğinden yeniden açıp devam et (zamanlanmış gözcü görev). Varsayılan AÇIK.</summary>
    public bool AutoRestart { get; init; } = true;

    /// <summary>"Bir daha gösterme": Başlat'a basınca çıkan bilgi penceresi (başka canlı yayın izlemeyin) gizlenir. Varsayılan: gösterilir.</summary>
    public bool HideStartNotice { get; init; }

    /// <summary>Twitch yayıncı drop'ları öncelik sırası (kampanya kimlikleri, en öncelikli başta). Sürükle-bırakla ayarlanır; listede olmayanlar sona eklenir.</summary>
    public IReadOnlyList<string> TwitchStreamerPriority { get; init; } = [];

    /// <summary>Kick yayıncı drop'ları öncelik sırası (platform başına ayrı liste).</summary>
    public IReadOnlyList<string> KickStreamerPriority { get; init; } = [];

    /// <summary>İzlenecek oyun (Twitch kategori slug'ı).</summary>
    public string GameSlug { get; init; } = "rust";

    /// <summary>Arayüzde son seçilen platform ekranı ("Twitch" ya da "Kick"). Alan eski ayar dosyalarında yoksa Twitch.</summary>
    public string LastPlatform { get; init; } = PlatformNames.Twitch;

    /// <summary>Bozuk/bilinmeyen değerleri güvenli varsayılana çeker.</summary>
    public AppSettings Normalized() => this with
    {
        TwitchStreamerPriority = PriorityLists.Clean(TwitchStreamerPriority),
        KickStreamerPriority = PriorityLists.Clean(KickStreamerPriority),
        LastPlatform = string.Equals(LastPlatform?.Trim(), PlatformNames.Kick, StringComparison.OrdinalIgnoreCase) ? PlatformNames.Kick : PlatformNames.Twitch,
        Language = SupportedLanguages.Normalize(Language),
        GameSlug = string.IsNullOrWhiteSpace(GameSlug) ? "rust" : GameSlug.Trim().ToLowerInvariant(),
    };
}

/// <summary><see cref="AppSettings.LastPlatform"/> değerleri.</summary>
public static class PriorityLists
{
    /// <summary>Boş/yinelenen kimlikleri atar (sırayı korur); null → boş liste.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return [];
        }

        var cleaned = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        // Zaten temizse aynı örnek döner: kayıt eşitliği (Changed yalnız gerçekten değişince) bozulmasın.
        return ids is IReadOnlyList<string> same && same.SequenceEqual(cleaned, StringComparer.Ordinal) ? same : cleaned;
    }
}

public static class PlatformNames
{
    public const string Twitch = "Twitch";
    public const string Kick = "Kick";
}

public static class SupportedLanguages
{
    public const string Auto = "auto";
    public const string Turkish = "tr";
    public const string English = "en";

    /// <summary>"tr"/"en"/"auto" (büyük-küçük harf önemsiz); boş ya da bilinmeyen değer → "auto".</summary>
    public static string Normalize(string? language)
    {
        var value = language?.Trim();
        if (string.Equals(value, Turkish, StringComparison.OrdinalIgnoreCase))
        {
            return Turkish;
        }

        return string.Equals(value, English, StringComparison.OrdinalIgnoreCase) ? English : Auto;
    }
}

/// <summary>Ayarları yükler/saklar (atomik JSON). Dosya yok/bozuksa varsayılanlarla açılır, nedeni <see cref="LoadWarning"/>'dadır.</summary>
public sealed class SettingsService
{
    private const int SchemaVersion = 1;

    private readonly AtomicJsonStore<AppSettings> _store;
    private readonly Lock _gate = new();
    private AppSettings _current;

    public SettingsService(string path)
    {
        _store = new AtomicJsonStore<AppSettings>(path, SchemaVersion);
        var loaded = _store.Load();
        _current = loaded.Value.Normalized();
        LoadWarning = loaded.Warning;
        if (loaded.Source != FileSource.Missing && !FileMentionsLanguage(path))
        {
            // Dil alanı eklenmeden önce yazılmış ayar dosyası: Türkçe korunur ("auto" yalnız yeni kurulumun varsayılanıdır).
            _current = _current with { Language = SupportedLanguages.Turkish };
        }

        if (!_current.AutoClaimDefaultMigrated)
        {
            // Varsayılan "kapalı"dan "açık"a geçti (kullanıcı kararı): eski dosyadaki (varsayılan olarak yazılmış) kapalı değer bir kez açığa çekilir.
            _current = _current with { AutoClaim = true, AutoClaimDefaultMigrated = true };
            if (loaded.Source != FileSource.Missing)
            {
                _store.Save(_current);
            }
        }
    }

    public string? LoadWarning { get; }

    private static bool FileMentionsLanguage(string path)
    {
        try
        {
            return !File.Exists(path) || File.ReadAllText(path).Contains("\"language\"", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Ayar değişince (kaydedildikten sonra) tetiklenir.</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated;
        lock (_gate)
        {
            updated = change(_current).Normalized();
            if (updated == _current)
            {
                return updated;
            }

            _store.Save(updated);
            _current = updated;
        }

        Changed?.Invoke(updated);
        return updated;
    }
}
