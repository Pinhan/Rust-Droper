using System.Collections.Concurrent;
using System.Globalization;
using RustDroper.Core.Configuration;

namespace RustDroper.Core.Localization;

/// <summary>Bir arayüz metni: anahtar + Türkçe (kaynak dil) + İngilizce. <c>{0}</c> biçimli yer tutucular iki dilde aynı sayıda olmalıdır.</summary>
public readonly record struct LocEntry(string Key, string Tr, string En);

/// <summary>
/// Arayüz yerelleştirici (Türkçe + İngilizce). Ek paket/uydu derleme yoktur: metinler kod içi tablolardadır (tek dosya yayınla uyumlu, açılışta
/// dosya okuma/çözümleme maliyeti yok). Geçerli dil süreç geneli tek değerdir (<see cref="Configure"/>); testler başka testleri etkilemeden
/// <see cref="Use"/> ile yalnız kendi eşzamansız akışları için dil seçer (AsyncLocal).
/// </summary>
public static class Loc
{
    private static readonly ConcurrentDictionary<string, LocEntry> Table = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> Registered = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<string?> Override = new();
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly string WindowsLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    private static volatile string _language = SupportedLanguages.Turkish;

    static Loc()
    {
        Register("core", CoreStrings.All);
    }

    /// <summary>Çözümlenmiş geçerli dil: "tr" ya da "en" (asla "auto" değil).</summary>
    public static string Language => Override.Value ?? _language;

    public static bool IsEnglish => Language == SupportedLanguages.English;

    /// <summary>Tarih/sayı biçimlerinde kullanılan kültür (dile göre tr-TR / en-US).</summary>
    public static CultureInfo Culture => IsEnglish ? EnglishCulture : TurkishCulture;

    /// <summary>Küresel dil değişince (yalnız <see cref="Configure"/>) tetiklenir; arayüz metinlerini yeniden üretir.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Yerelleştirilmiş metinler yoksa anahtar döner (arayüzde anahtar görünür = eksik çeviri belli olur; test yakalar).</summary>
    public static string T(string key)
    {
        if (!Table.TryGetValue(key, out var entry))
        {
            return key;
        }

        return IsEnglish ? entry.En : entry.Tr;
    }

    /// <summary>Biçimli metin: <c>Loc.T(anahtar, 3)</c> → <c>string.Format(Culture, metin, 3)</c>.</summary>
    public static string T(string key, params object?[] args)
    {
        var text = T(key);
        return args.Length == 0 ? text : string.Format(Culture, text, args);
    }

    /// <summary>
    /// Çoğul biçim: <c>key.one</c> (n = 1, yalnız İngilizce'de anlamlı) ya da <c>key.other</c>. <paramref name="args"/> biçime olduğu gibi verilir
    /// (genellikle ilki <paramref name="count"/>).
    /// </summary>
    public static string Plural(string key, long count, params object?[] args) =>
        T(key + (count == 1 ? ".one" : ".other"), args);

    public static bool Has(string key) => Table.ContainsKey(key);

    /// <summary>Tablodaki her kayıt (testler için).</summary>
    public static IReadOnlyCollection<LocEntry> All => [.. Table.Values];

    /// <summary>Bir tabloyu kaydeder (aynı ad iki kez eklenmez; aynı anahtar iki tabloda olamaz).</summary>
    public static void Register(string tableName, IEnumerable<LocEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(entries);
        if (!Registered.TryAdd(tableName, true))
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!Table.TryAdd(entry.Key, entry))
            {
                throw new InvalidOperationException($"Yerelleştirme anahtarı yinelendi: {entry.Key}");
            }
        }
    }

    /// <summary>
    /// Ayar değerinden ("auto" | "tr" | "en") geçerli dili belirler; "auto" = Windows görüntü dili Türkçe ise Türkçe, değilse İngilizce.
    /// Dil gerçekten değiştiyse <see cref="LanguageChanged"/> tetiklenir.
    /// </summary>
    public static string Configure(string? setting, string? windowsLanguage = null)
    {
        var resolved = Resolve(setting, windowsLanguage ?? WindowsLanguage);
        var changed = !string.Equals(_language, resolved, StringComparison.Ordinal);
        _language = resolved;
        if (changed)
        {
            LanguageChanged?.Invoke();
        }

        return resolved;
    }

    /// <summary>"auto"/"tr"/"en" + Windows dili → "tr" ya da "en".</summary>
    public static string Resolve(string? setting, string? windowsLanguage)
    {
        var normalized = SupportedLanguages.Normalize(setting);
        if (normalized == SupportedLanguages.Auto)
        {
            return string.Equals(windowsLanguage, "tr", StringComparison.OrdinalIgnoreCase) ? SupportedLanguages.Turkish : SupportedLanguages.English;
        }

        return normalized;
    }

    /// <summary>Yalnız çağıranın eşzamansız akışı için dili ezer (testler; başka testleri/küresel durumu etkilemez). <c>using</c> ile geri alınır.</summary>
    public static IDisposable Use(string language)
    {
        var previous = Override.Value;
        Override.Value = SupportedLanguages.Normalize(language) == SupportedLanguages.English ? SupportedLanguages.English : SupportedLanguages.Turkish;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
