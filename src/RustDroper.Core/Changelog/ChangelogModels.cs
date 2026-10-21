using RustDroper.Core.Configuration;

namespace RustDroper.Core.Changelog;

/// <summary>Sürüm türü (Semantic Versioning kuralları: <c>docs/08-surum-ve-degisiklik-kaydi.md</c>). <see cref="Baseline"/> yalnız ilk (referans) sürümdür.</summary>
public enum ChangelogType
{
    /// <summary>Referans sürüm: projenin ilk yayını (önceki sürüm yok).</summary>
    Baseline,

    /// <summary>Mimari değişiklik, büyük refaktör, geriye uyumsuz değişiklik.</summary>
    Major,

    /// <summary>Yeni özellik, sayfa, modül, kullanıcı fonksiyonu.</summary>
    Minor,

    /// <summary>Hata düzeltme, CSS, performans, güvenlik, metin ve küçük UX düzeltmeleri.</summary>
    Patch,
}

/// <summary>İki dilli metin. Eksik dilde diğerine düşer (arayüz hiçbir zaman boş metin göstermez); yayın dosyasında ikisi de dolu olmalıdır (test).</summary>
public sealed record LocalizedText(string Tr, string En)
{
    public string Get(string language) =>
        string.Equals(language, SupportedLanguages.English, StringComparison.OrdinalIgnoreCase)
            ? (En.Length > 0 ? En : Tr)
            : (Tr.Length > 0 ? Tr : En);

    public bool IsComplete => Tr.Trim().Length > 0 && En.Trim().Length > 0;
}

/// <summary>Bir sürümün kaydı: sürüm, tarih, tür, etiket, özet ve dört bölüm (Eklenenler/Değişenler/Düzeltilenler/Kaldırılanlar).</summary>
public sealed record ChangelogEntry(
    string Version,
    DateOnly Date,
    ChangelogType Type,
    LocalizedText Tag,
    LocalizedText Summary,
    IReadOnlyList<LocalizedText> Added,
    IReadOnlyList<LocalizedText> Changed,
    IReadOnlyList<LocalizedText> Fixed,
    IReadOnlyList<LocalizedText> Removed)
{
    /// <summary>Boş olmayan en az bir bölüm var mı.</summary>
    public bool HasItems => Added.Count + Changed.Count + Fixed.Count + Removed.Count > 0;
}

/// <summary>Ayrıştırma sonucu: geçerli girişler (dosyadaki sırayla) ve atlanan girişlerin nedenleri (günlük/test için).</summary>
public sealed record ChangelogParseResult(IReadOnlyList<ChangelogEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>Katı <c>MAJOR.MINOR.PATCH</c> sürüm numarası (ön ek/etiket yok).</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 3 })
        {
            return false;
        }

        if (!TryPart(parts[0], out var major) || !TryPart(parts[1], out var minor) || !TryPart(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVer(major, minor, patch);
        return true;
    }

    private static bool TryPart(string part, out int value)
    {
        value = 0;
        return part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0')
            && int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(SemVer other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    /// <summary>
    /// <paramref name="previous"/>'tan bu sürüme artışın türü (MAJOR/MINOR/PATCH); artış yoksa (eşit/geri) <c>null</c>.
    /// MAJOR artınca MINOR/PATCH sıfırlanmalıdır; MINOR artınca PATCH sıfırlanmalıdır (SemVer): aksi halde <c>null</c> (geçersiz artış).
    /// </summary>
    public ChangelogType? BumpFrom(SemVer previous)
    {
        if (Major > previous.Major)
        {
            return Minor == 0 && Patch == 0 ? ChangelogType.Major : null;
        }

        if (Major == previous.Major && Minor > previous.Minor)
        {
            return Patch == 0 ? ChangelogType.Minor : null;
        }

        return Major == previous.Major && Minor == previous.Minor && Patch > previous.Patch ? ChangelogType.Patch : null;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
