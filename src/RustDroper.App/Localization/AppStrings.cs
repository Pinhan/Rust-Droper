using RustDroper.Core.Localization;

namespace RustDroper.App.Localization;

/// <summary>
/// Uygulama katmanındaki arayüz metinleri (anahtar → Türkçe + İngilizce). Türkçe, kaynak dildir ve önceki sabit metinlerle birebirdir.
/// Alan tabloları <c>AppStrings.*.cs</c> dosyalarındadır. Biçimli metinlerde <c>{0}</c> yer tutucu sayısı iki dilde aynı olmalıdır (test bunu doğrular).
/// </summary>
internal static partial class AppStrings
{
    private static LocEntry L(string key, string tr, string en) => new(key, tr, en);

    public static IEnumerable<LocEntry> All => [.. Common, .. Fmt, .. Settings, .. About, .. Health, .. ReleaseNotes];
}
