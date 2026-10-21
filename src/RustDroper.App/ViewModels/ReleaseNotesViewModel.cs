using System.Reflection;
using System.Text;
using RustDroper.Core.Changelog;
using RustDroper.Core.Localization;

namespace RustDroper.App.ViewModels;

/// <summary>Güncelleme notları bölümü türü (renk/ikon buna bağlıdır).</summary>
public enum ReleaseSectionKind
{
    Added,
    Changed,
    Fixed,
    Removed,
}

/// <summary>Bir sürüm kartındaki bölüm: başlık, renk (önem derecesi), ikon anahtarı ve madde metinleri (geçerli dilde).</summary>
public sealed record ReleaseSectionView(ReleaseSectionKind Kind, string Title, Severity Severity, string IconKey, IReadOnlyList<string> Items);

/// <summary>Bir sürüm kartının sayfada gösterilen tüm metinleri (geçerli dilde üretilmiş). Boş bölümler <see cref="Sections"/>'a konmaz.</summary>
public sealed record ReleaseCardView(
    string Version,
    string VersionText,
    string TypeText,
    Severity TypeSeverity,
    string TagText,
    string DateText,
    string FromText,
    string SummaryText,
    IReadOnlyList<ReleaseSectionView> Sections,
    bool IsLast);

/// <summary>
/// "Güncelleme notları" sayfası: gömülü <c>changelog.json</c>'dan sürüm kartları (yeni → eski). Veri YALNIZ sayfa açıkken yüklenir
/// (<see cref="Load"/>) ve kapanınca bırakılır (<see cref="Unload"/>): sayfa kapalıyken bellek/CPU maliyeti yoktur. Dil değişince (açıkken)
/// metinler yeniden üretilir.
/// </summary>
public sealed class ReleaseNotesViewModel : ViewModelBase
{
    private readonly Func<string?> _loader;
    private IReadOnlyList<ChangelogEntry>? _entries;
    private IReadOnlyList<ReleaseCardView> _releases = [];

    public ReleaseNotesViewModel(Func<string?>? loader = null)
    {
        _loader = loader ?? ReadEmbedded;
        ObserveLanguage();
    }

    /// <summary>Sürüm kartları (yeni → eski); sayfa kapalıyken boş.</summary>
    public IReadOnlyList<ReleaseCardView> Releases
    {
        get => _releases;
        private set
        {
            if (SetProperty(ref _releases, value))
            {
                OnPropertyChanged(nameof(HasReleases));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool HasReleases => _releases.Count > 0;

    /// <summary>Yüklendi ama gösterilecek geçerli giriş yok ("bulunamadı" iletisi).</summary>
    public bool IsEmpty => _entries is not null && _releases.Count == 0;

    public bool IsLoaded => _entries is not null;

    /// <summary>Verileri okur ve kartları geçerli dilde kurar (bozuk dosya/giriş uygulamayı çökertmez: boş sayfa).</summary>
    public void Load()
    {
        try
        {
            _entries = ChangelogParser.Parse(_loader()).Entries;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _entries = [];
        }

        Rebuild();
    }

    /// <summary>Sayfa kapandı: veri ve kartlar bırakılır.</summary>
    public void Unload()
    {
        _entries = null;
        Releases = [];
        OnPropertyChanged(nameof(IsEmpty));
    }

    protected override void OnLanguageChanged()
    {
        if (_entries is not null)
        {
            Rebuild();
        }

        base.OnLanguageChanged();
    }

    private void Rebuild()
    {
        var entries = _entries ?? [];
        var language = Loc.Language;
        var cards = new List<ReleaseCardView>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var previous = entry.Type == ChangelogType.Baseline || i + 1 >= entries.Count ? null : entries[i + 1].Version;
            cards.Add(new ReleaseCardView(
                entry.Version,
                "v" + entry.Version,
                Loc.T(TypeKey(entry.Type)),
                TypeSeverity(entry.Type),
                entry.Tag.Get(language),
                entry.Date.ToString(Loc.T("fmt.date.dayYear"), Loc.Culture),
                previous is null ? string.Empty : $"{previous} → {entry.Version}",
                entry.Summary.Get(language),
                Sections(entry, language),
                i == entries.Count - 1));
        }

        Releases = cards;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static IReadOnlyList<ReleaseSectionView> Sections(ChangelogEntry entry, string language)
    {
        var sections = new List<ReleaseSectionView>(4);
        Add(ReleaseSectionKind.Added, "relnotes.section.added", Severity.Ok, "IconPlusCircle", entry.Added);
        Add(ReleaseSectionKind.Changed, "relnotes.section.changed", Severity.Info, "IconEdit", entry.Changed);
        Add(ReleaseSectionKind.Fixed, "relnotes.section.fixed", Severity.Warn, "IconCheck", entry.Fixed);
        Add(ReleaseSectionKind.Removed, "relnotes.section.removed", Severity.Error, "IconMinusCircle", entry.Removed);
        return sections;

        void Add(ReleaseSectionKind kind, string titleKey, Severity severity, string icon, IReadOnlyList<LocalizedText> items)
        {
            if (items.Count > 0)
            {
                sections.Add(new ReleaseSectionView(kind, Loc.T(titleKey), severity, icon, [.. items.Select(item => item.Get(language))]));
            }
        }
    }

    private static string TypeKey(ChangelogType type) => type switch
    {
        ChangelogType.Baseline => "relnotes.type.baseline",
        ChangelogType.Major => "relnotes.type.major",
        ChangelogType.Minor => "relnotes.type.minor",
        _ => "relnotes.type.patch",
    };

    private static Severity TypeSeverity(ChangelogType type) => type switch
    {
        ChangelogType.Baseline => Severity.Info,
        ChangelogType.Major => Severity.Error,
        ChangelogType.Minor => Severity.Ok,
        _ => Severity.Warn,
    };

    /// <summary>Gömülü <c>changelog.json</c> (derlemeye <c>LogicalName=changelog.json</c> ile eklenir).</summary>
    internal static string? ReadEmbedded()
    {
        using var stream = typeof(ReleaseNotesViewModel).Assembly.GetManifestResourceStream("changelog.json");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
