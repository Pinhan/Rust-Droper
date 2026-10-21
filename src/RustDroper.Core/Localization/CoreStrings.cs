namespace RustDroper.Core.Localization;

/// <summary>Core katmanındaki kullanıcıya gösterilen metinler (motor notları, bildirimler, durum iletileri). Anahtar = anlamlı kimlik; Türkçe kaynak dildir.</summary>
internal static partial class CoreStrings
{
    private static LocEntry L(string key, string tr, string en) => new(key, tr, en);

    public static IEnumerable<LocEntry> All => [.. Engine, .. Service, .. Notify, .. Watch, .. Errors];
}
