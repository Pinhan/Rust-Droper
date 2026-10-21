using System.Reflection;
using System.Text.RegularExpressions;
using RustDroper.App.ViewModels;
using RustDroper.Core.Changelog;
using RustDroper.Core.Localization;

namespace RustDroper.App.Tests;

/// <summary>
/// Sürüm kuralları KOD OLARAK (docs/08-surum-ve-degisiklik-kaydi.md): gömülü changelog.json geçerli, sürümler SemVer ve yeni→eski sıralı, iki dil dolu,
/// artışın türü <c>type</c> alanıyla uyumlu, en üst sürüm == derleme sürümü == Directory.Build.props, CHANGELOG.md / CHANGELOG.tr.md JSON ile senkron.
/// Bir değişiklik yapıp changelog.json'u (ve <c>build\changelog.ps1</c> ile markdown'ları) güncellemezseniz bu testler kırılır.
/// </summary>
public class ChangelogRulesTests
{
    private static readonly string Json = ReleaseNotesViewModel.ReadEmbedded() ?? throw new InvalidOperationException("Gömülü changelog.json yok.");

    private static IReadOnlyList<ChangelogEntry> Entries => ChangelogParser.Parse(Json).Entries;

    [Fact]
    public void EmbeddedJson_IsValid_WithoutSkippedEntries()
    {
        var result = ChangelogParser.Parse(Json);

        Assert.Empty(result.Warnings);
        Assert.NotEmpty(result.Entries);
    }

    [Fact]
    public void Versions_AreSemVer_NewestFirst_WithoutDuplicates()
    {
        var versions = Entries.Select(e => SemVer.TryParse(e.Version, out var v) ? v : throw new InvalidOperationException("Geçersiz sürüm: " + e.Version)).ToList();

        Assert.Equal(versions.Count, versions.Distinct().Count());
        for (var i = 0; i + 1 < versions.Count; i++)
        {
            Assert.True(versions[i].CompareTo(versions[i + 1]) > 0, $"Sıralama yeni→eski değil: {versions[i]} sonra {versions[i + 1]}");
        }
    }

    [Fact]
    public void EveryEntry_HasBothLanguagesFilled()
    {
        foreach (var entry in Entries)
        {
            Assert.True(entry.Tag.IsComplete, $"{entry.Version}: etiket iki dilde dolu olmalı");
            Assert.True(entry.Summary.IsComplete, $"{entry.Version}: özet iki dilde dolu olmalı");
            Assert.True(entry.HasItems, $"{entry.Version}: en az bir madde olmalı");
            foreach (var item in entry.Added.Concat(entry.Changed).Concat(entry.Fixed).Concat(entry.Removed))
            {
                Assert.True(item.IsComplete, $"{entry.Version}: madde iki dilde dolu olmalı → {item.Tr} / {item.En}");
            }
        }
    }

    [Fact]
    public void BumpTypeMatchesTheVersionIncrease_AndOnlyTheOldestIsBaseline()
    {
        var entries = Entries;
        Assert.Equal(ChangelogType.Baseline, entries[^1].Type);
        Assert.Equal(1, entries.Count(e => e.Type == ChangelogType.Baseline));
        for (var i = 0; i + 1 < entries.Count; i++)
        {
            Assert.True(SemVer.TryParse(entries[i].Version, out var newer));
            Assert.True(SemVer.TryParse(entries[i + 1].Version, out var older));
            var bump = newer.BumpFrom(older);
            Assert.True(bump is not null, $"{older} → {newer} geçerli bir SemVer artışı değil (MAJOR artınca MINOR/PATCH, MINOR artınca PATCH sıfırlanır)");
            Assert.Equal(bump, entries[i].Type);
        }
    }

    [Fact]
    public void FirstReleaseIsTheBaseline_1_0_0()
    {
        Assert.Equal("1.0.0", Entries[^1].Version);
        Assert.Equal(ChangelogType.Baseline, Entries[^1].Type);
    }

    [Fact]
    public void TopVersion_EqualsTheBuildVersion_AndDirectoryBuildProps()
    {
        var top = Entries[0].Version;
        var info = typeof(ReleaseNotesViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var built = info.Contains('+') ? info[..info.IndexOf('+', StringComparison.Ordinal)] : info;
        Assert.Equal(top, built);

        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Equal(top, Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value);
    }

    [Fact]
    public void ChangelogMarkdownFiles_AreInSyncWithTheJson()
    {
        var root = RepoRoot();
        foreach (var (file, language) in new[] { (ChangelogMarkdown.EnglishFile, "en"), (ChangelogMarkdown.TurkishFile, "tr") })
        {
            var actual = File.ReadAllText(Path.Combine(root, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.True(
                actual == ChangelogMarkdown.Render(Entries, language),
                $"{file} changelog.json ile senkron değil: `powershell -File build\\changelog.ps1` çalıştırın.");
        }
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RustDroper.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}

public class ReleaseNotesViewModelTests
{
    private const string Json = """
        { "releases": [
          { "version": "1.1.0", "date": "2026-11-03", "type": "minor", "tag": { "tr": "Yeni sayfa", "en": "New page" }, "summary": { "tr": "Özet TR", "en": "Summary EN" },
            "added": [ { "tr": "Eklendi", "en": "Added item" } ], "changed": [], "fixed": [ { "tr": "Düzeldi", "en": "Fixed item" } ], "removed": [] },
          { "version": "1.0.0", "date": "2026-10-20", "type": "baseline", "tag": { "tr": "İlk sürüm", "en": "Initial release" }, "summary": { "tr": "İlk", "en": "First" },
            "added": [ { "tr": "A", "en": "a" } ] }
        ] }
        """;

    [Fact]
    public void NothingIsLoaded_UntilTheFirstLoad_AndUnloadReleasesTheCards()
    {
        var calls = 0;
        var vm = new ReleaseNotesViewModel(() =>
        {
            calls++;
            return Json;
        });

        Assert.Equal(0, calls);
        Assert.False(vm.IsLoaded);
        Assert.Empty(vm.Releases);

        vm.Load();
        Assert.Equal(1, calls);
        Assert.True(vm.IsLoaded);
        Assert.Equal(2, vm.Releases.Count);

        vm.Unload();
        Assert.False(vm.IsLoaded);
        Assert.Empty(vm.Releases);
    }

    [Fact]
    public void Cards_AreNewestFirst_EmptySectionsHidden_AndTurkishByDefault()
    {
        var vm = new ReleaseNotesViewModel(() => Json);
        vm.Load();

        var top = vm.Releases[0];
        Assert.Equal("v1.1.0", top.VersionText);
        Assert.Equal("MINOR", top.TypeText);
        Assert.Equal(Severity.Ok, top.TypeSeverity);
        Assert.Equal("Yeni sayfa", top.TagText);
        Assert.Equal("3 Kas 2026", top.DateText);
        Assert.Equal("1.0.0 → 1.1.0", top.FromText);
        Assert.Equal("Özet TR", top.SummaryText);
        Assert.Equal(["Eklenenler", "Düzeltilenler"], top.Sections.Select(s => s.Title)); // Değişenler/Kaldırılanlar boş → yok
        Assert.Equal(["Eklendi"], top.Sections[0].Items);
        Assert.Equal(Severity.Ok, top.Sections[0].Severity);
        Assert.Equal(Severity.Warn, top.Sections[1].Severity);
        Assert.False(top.IsLast);

        var baseline = vm.Releases[1];
        Assert.Equal("BASELINE", baseline.TypeText);
        Assert.Equal(string.Empty, baseline.FromText); // baseline için "önceki → bu" yok
        Assert.True(baseline.IsLast);
    }

    [Fact]
    public void English_UsesEnglishTextsAndDateFormat()
    {
        using var _ = Loc.Use("en");
        var vm = new ReleaseNotesViewModel(() => Json);
        vm.Load();

        var top = vm.Releases[0];
        Assert.Equal("Nov 3, 2026", top.DateText);
        Assert.Equal("New page", top.TagText);
        Assert.Equal("Summary EN", top.SummaryText);
        Assert.Equal(["Added", "Fixed"], top.Sections.Select(s => s.Title));
        Assert.Equal(["Added item"], top.Sections[0].Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ bozuk")]
    [InlineData("{ \"releases\": [] }")]
    public void BrokenOrEmptyData_ShowsTheEmptyState_WithoutThrowing(string? json)
    {
        var vm = new ReleaseNotesViewModel(() => json);

        vm.Load();

        Assert.True(vm.IsLoaded);
        Assert.Empty(vm.Releases);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasReleases);
    }

    [Fact]
    public void LoaderIoFailure_IsSwallowed()
    {
        var vm = new ReleaseNotesViewModel(() => throw new IOException("kaynak okunamadı"));

        vm.Load();

        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void EmbeddedData_LoadsRealReleases()
    {
        var vm = new ReleaseNotesViewModel();

        vm.Load();

        Assert.True(vm.HasReleases);
        Assert.Equal("v1.0.0", vm.Releases[^1].VersionText);
        Assert.NotEmpty(vm.Releases[^1].Sections);
        Assert.All(vm.Releases.SelectMany(r => r.Sections), s => Assert.NotEmpty(s.Items));
    }
}

/// <summary>Sayfa açma/kapama ve dil değişimi (küresel dil → paralel çalışmaz).</summary>
[Collection("Loc global")]
public class ReleaseNotesNavigationTests : IDisposable
{
    private readonly TempDir _dir = new();

    public ReleaseNotesNavigationTests() => Loc.Configure("tr");

    public void Dispose()
    {
        Loc.Configure("tr");
        _dir.Dispose();
    }

    private MainViewModel NewViewModel(out RustDroper.Core.Orchestration.DropperService service)
    {
        var settings = new RustDroper.Core.Configuration.SettingsService(_dir.File("settings.json"));
        service = new RustDroper.Core.Orchestration.DropperService(new StubAccount(), new StubFactory(new StubPlatform()), settings);
        return new MainViewModel(settings, new FakeAutostart(), new RecordingBrowser(), service, new ImmediateDispatcher());
    }

    [Fact]
    public void Toggle_OpensLoadsAndClosesUnloads_AndIsMutuallyExclusiveWithOtherPages()
    {
        var vm = NewViewModel(out var service);
        using var _ = service;
        Assert.False(vm.ReleaseNotes.IsLoaded);
        Assert.Equal("Güncelleme notları", vm.ReleaseNotesButtonLabel);

        vm.ToggleReleaseNotesCommand.Execute(null);
        Assert.True(vm.ShowReleaseNotes);
        Assert.True(vm.ReleaseNotes.IsLoaded);
        Assert.True(vm.ReleaseNotes.HasReleases);
        Assert.Equal("← Pano", vm.ReleaseNotesButtonLabel);

        vm.ToggleAboutCommand.Execute(null);
        Assert.False(vm.ShowReleaseNotes);
        Assert.True(vm.ShowAbout);
        Assert.False(vm.ReleaseNotes.IsLoaded); // kapanınca bırakılır

        vm.ToggleReleaseNotesCommand.Execute(null);
        Assert.True(vm.ShowReleaseNotes);
        Assert.False(vm.ShowAbout);
        vm.ToggleHealthCommand.Execute(null);
        Assert.False(vm.ShowReleaseNotes);
        Assert.True(vm.ShowHealth);
        vm.ToggleReleaseNotesCommand.Execute(null);
        Assert.False(vm.ShowHealth);
        vm.ToggleSettingsCommand.Execute(null);
        Assert.False(vm.ShowReleaseNotes);
        Assert.True(vm.ShowSettings);

        vm.ToggleReleaseNotesCommand.Execute(null);
        vm.SelectPlatformCommand.Execute("Twitch"); // platform ekranı değişince sayfa kapanır
        Assert.False(vm.ShowReleaseNotes);
        Assert.False(vm.ReleaseNotes.IsLoaded);

        vm.ToggleReleaseNotesCommand.Execute(null);
        vm.ToggleReleaseNotesCommand.Execute(null); // ikinci basış kapatır ("Pano")
        Assert.False(vm.ShowReleaseNotes);
    }

    [Fact]
    public void LanguageChange_RefreshesAnOpenPage_Instantly()
    {
        var vm = NewViewModel(out var service);
        using var _ = service;
        vm.ToggleReleaseNotesCommand.Execute(null);
        var turkishSection = vm.ReleaseNotes.Releases[^1].Sections[0].Title;
        var turkishTag = vm.ReleaseNotes.Releases[^1].TagText;
        Assert.Equal("Eklenenler", turkishSection);
        Assert.Equal("İlk sürüm", turkishTag);
        Assert.Matches(@"^\d{1,2} \p{L}{3} 2026$", vm.ReleaseNotes.Releases[^1].DateText);

        vm.Settings.Language = "en";

        Assert.Equal("Added", vm.ReleaseNotes.Releases[^1].Sections[0].Title);
        Assert.Equal("Initial release", vm.ReleaseNotes.Releases[^1].TagText);
        Assert.Equal("Oct 20, 2026", vm.ReleaseNotes.Releases[^1].DateText);
        Assert.Equal("← Dashboard", vm.ReleaseNotesButtonLabel);
    }
}
