using RustDroper.Core.Localization;

namespace RustDroper.App.Localization;

internal static partial class AppStrings
{
    private static readonly LocEntry[] ReleaseNotes =
    [
        L("main.releaseNotes.tip", "Güncelleme notları", "Release notes"),
        L("relnotes.title", "GÜNCELLEME NOTLARI", "RELEASE NOTES"),
        L("relnotes.back", "Pano", "Dashboard"),
        L("relnotes.empty", "Güncelleme notu bulunamadı.", "No release notes found."),
        L("relnotes.summary", "Özet", "Summary"),

        // Semantic Versioning açıklama kartı
        L("relnotes.semver.title", "Semantic Versioning", "Semantic Versioning"),
        L("relnotes.semver.intro",
            "Sürüm numarası MAJOR.MINOR.PATCH biçimindedir. Projede yapılan her değişiklik, aşağıdaki kurala göre yeni bir sürüm olarak yayınlanır ve burada listelenir.",
            "Version numbers use the MAJOR.MINOR.PATCH format. Every change to the project ships as a new version following the rule below and is listed here."),
        L("relnotes.major.pattern", "X.0.0", "X.0.0"),
        L("relnotes.major.desc", "Mimari değişiklik, büyük refaktör, geriye uyumsuz değişiklik", "Architecture changes, big refactors, backwards-incompatible changes"),
        L("relnotes.minor.pattern", "1.X.0", "1.X.0"),
        L("relnotes.minor.desc", "Yeni özellik, sayfa, modül, kullanıcı fonksiyonu", "New features, pages, modules, user functions"),
        L("relnotes.patch.pattern", "1.0.X", "1.0.X"),
        L("relnotes.patch.desc", "Hata düzeltme, CSS, performans, güvenlik, metin ve küçük UX düzeltmeleri", "Bug fixes, CSS, performance, security, text and small UX fixes"),

        // sürüm türü rozetleri ve bölüm başlıkları
        L("relnotes.type.baseline", "BASELINE", "BASELINE"),
        L("relnotes.type.major", "MAJOR", "MAJOR"),
        L("relnotes.type.minor", "MINOR", "MINOR"),
        L("relnotes.type.patch", "PATCH", "PATCH"),
        L("relnotes.section.added", "Eklenenler", "Added"),
        L("relnotes.section.changed", "Değişenler", "Changed"),
        L("relnotes.section.fixed", "Düzeltilenler", "Fixed"),
        L("relnotes.section.removed", "Kaldırılanlar", "Removed"),
    ];
}
