using System.Globalization;
using System.Text;
using RustDroper.Core.Configuration;

namespace RustDroper.Core.Changelog;

/// <summary>
/// <c>changelog.json</c>'dan GitHub için <c>CHANGELOG.md</c> (İngilizce) ve <c>CHANGELOG.tr.md</c> (Türkçe) üretir (Keep a Changelog biçimi: Added/Changed/Fixed/Removed).
/// Çıktı yalnız LF satır sonu kullanır; bu sayede dosya ile üretim birebir karşılaştırılabilir (test).
/// </summary>
public static class ChangelogMarkdown
{
    public const string EnglishFile = "CHANGELOG.md";
    public const string TurkishFile = "CHANGELOG.tr.md";

    public static string Render(IReadOnlyList<ChangelogEntry> entries, string language)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var english = string.Equals(language, SupportedLanguages.English, StringComparison.OrdinalIgnoreCase);
        var lang = english ? SupportedLanguages.English : SupportedLanguages.Turkish;
        var text = new StringBuilder();
        text.Append(english ? "# Changelog\n\n" : "# Değişiklik Günlüğü\n\n");
        text.Append(english
            ? "All notable changes to Rust Droper are listed here, newest first. Versions follow [Semantic Versioning](https://semver.org/): **MAJOR** = architecture changes, big refactors, backwards-incompatible changes; **MINOR** = new features, pages, modules, user functions; **PATCH** = bug fixes, CSS, performance, security, text and small UX fixes.\n\n"
            : "Rust Droper'daki tüm önemli değişiklikler burada, en yeniden eskiye listelenir. Sürümler [Anlamsal Sürümleme](https://semver.org/lang/tr/) kuralını izler: **MAJOR** = mimari değişiklik, büyük refaktör, geriye uyumsuz değişiklik; **MINOR** = yeni özellik, sayfa, modül, kullanıcı fonksiyonu; **PATCH** = hata düzeltme, CSS, performans, güvenlik, metin ve küçük UX düzeltmeleri.\n\n");
        text.Append(english
            ? "> This file is generated from `src/RustDroper.App/Resources/changelog.json` (`powershell -File build\\changelog.ps1`). Do not edit it by hand. Turkish version: [CHANGELOG.tr.md](CHANGELOG.tr.md).\n\n"
            : "> Bu dosya `src/RustDroper.App/Resources/changelog.json` içinden üretilir (`powershell -File build\\changelog.ps1`). Elle düzenlemeyin. İngilizce sürüm: [CHANGELOG.md](CHANGELOG.md).\n\n");

        foreach (var entry in entries)
        {
            var type = TypeLabel(entry.Type);
            var tag = entry.Tag.Get(lang);
            text.Append("## [").Append(entry.Version).Append("] — ")
                .Append(entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(" · ").Append(type);
            if (tag.Length > 0)
            {
                text.Append(" · ").Append(tag);
            }

            text.Append("\n\n");
            if (entry.Summary.Get(lang) is { Length: > 0 } summary)
            {
                text.Append(summary).Append("\n\n");
            }

            Section(text, english ? "Added" : "Eklenenler", entry.Added, lang);
            Section(text, english ? "Changed" : "Değişenler", entry.Changed, lang);
            Section(text, english ? "Fixed" : "Düzeltilenler", entry.Fixed, lang);
            Section(text, english ? "Removed" : "Kaldırılanlar", entry.Removed, lang);
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }

    public static string TypeLabel(ChangelogType type) => type switch
    {
        ChangelogType.Baseline => "BASELINE",
        ChangelogType.Major => "MAJOR",
        ChangelogType.Minor => "MINOR",
        _ => "PATCH",
    };

    private static void Section(StringBuilder text, string title, IReadOnlyList<LocalizedText> items, string language)
    {
        if (items.Count == 0)
        {
            return; // boş bölüm yazılmaz
        }

        text.Append("### ").Append(title).Append("\n\n");
        foreach (var item in items)
        {
            text.Append("- ").Append(item.Get(language)).Append('\n');
        }

        text.Append('\n');
    }
}
