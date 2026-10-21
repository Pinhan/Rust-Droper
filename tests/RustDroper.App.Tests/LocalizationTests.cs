using System.Text.RegularExpressions;
using RustDroper.App.ViewModels;
using RustDroper.Core.Configuration;
using RustDroper.Core.Localization;

namespace RustDroper.App.Tests;

/// <summary>
/// Küresel dili değiştiren testler başka testlerle paralel çalışmasın diye bu koleksiyonda toplanır (xUnit, paralelleştirmeyi kapalı koleksiyonları
/// diğerleri bittikten sonra, tek başına çalıştırır). Başka testler dili yalnız <see cref="Loc.Use"/> (AsyncLocal) ile seçer.
/// </summary>
[CollectionDefinition("Loc global", DisableParallelization = true)]
public sealed class LocGlobalCollection;

/// <summary>Çeviri tablosunun bütünlüğü: her anahtar iki dilde dolu, yer tutucu sayıları eşit, İngilizce metinde Türkçe kalıntı yok, kaynaktaki anahtarlar var.</summary>
public class LocalizationTableTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[:,][^}]*)?\}", RegexOptions.Compiled);

    private static string[] PlaceholdersOf(string text) => [.. Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().Order()];

    [Fact]
    public void EveryKey_HasBothLanguages_AndMatchingPlaceholders()
    {
        var all = Loc.All;
        Assert.NotEmpty(all);
        foreach (var entry in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Tr), $"Türkçe boş: {entry.Key}");
            Assert.False(string.IsNullOrWhiteSpace(entry.En), $"İngilizce boş: {entry.Key}");
            Assert.True(
                PlaceholdersOf(entry.Tr).SequenceEqual(PlaceholdersOf(entry.En)),
                $"Yer tutucular farklı: {entry.Key}  tr=[{string.Join(',', PlaceholdersOf(entry.Tr))}] en=[{string.Join(',', PlaceholdersOf(entry.En))}]");
        }
    }

    [Fact]
    public void EnglishText_ContainsNoTurkishLetters()
    {
        // Yalnız dil adı etiketi ("Türkçe") ve iki dilli etiket ("Dil / Language") Türkçe harf taşıyabilir.
        var allowed = new HashSet<string> { "settings.language.tr", "settings.language", "settings.language.auto" };
        var turkish = new Regex("[çğıöşüÇĞİÖŞÜ]");
        foreach (var entry in Loc.All.Where(e => !allowed.Contains(e.Key)))
        {
            Assert.False(turkish.IsMatch(entry.En), $"İngilizce metinde Türkçe harf var: {entry.Key} → {entry.En}");
        }
    }

    [Fact]
    public void KeysUsedInSourceCode_AllExist()
    {
        var root = FindRepoRoot();
        var used = new List<(string File, string Key)>();
        var codeKey = new Regex(@"Loc\.(T|Plural)\(\s*""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);
        var xamlKey = new Regex(@"\{loc:Loc\s+([A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in codeKey.Matches(text))
            {
                if (m.Groups[1].Value == "Plural")
                {
                    used.Add((file, m.Groups[2].Value + ".one"));
                    used.Add((file, m.Groups[2].Value + ".other"));
                }
                else
                {
                    used.Add((file, m.Groups[2].Value));
                }
            }

            foreach (Match m in xamlKey.Matches(text))
            {
                used.Add((file, m.Groups[1].Value));
            }
        }

        Assert.NotEmpty(used);
        var missing = used.Where(u => !Loc.Has(u.Key)).Select(u => $"{Path.GetFileName(u.File)}: {u.Key}").Distinct().ToList();
        Assert.True(missing.Count == 0, "Tabloda olmayan anahtarlar: " + string.Join("; ", missing));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RustDroper.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}

public class LocalizationBasicsTests
{
    [Fact]
    public void Use_OverridesLanguageOnlyForTheCurrentFlow()
    {
        Assert.Equal("tr", Loc.Language); // varsayılan: Türkçe (testler bunu varsayar)
        using (Loc.Use("en"))
        {
            Assert.Equal("en", Loc.Language);
            Assert.Equal("SETTINGS", Loc.T("settings.title"));
        }

        Assert.Equal("tr", Loc.Language);
        Assert.Equal("AYARLAR", Loc.T("settings.title"));
    }

    [Fact]
    public async Task Use_FlowsThroughAsyncCode()
    {
        using var scope = Loc.Use("en");
        var inner = await Task.Run(async () =>
        {
            await Task.Yield();
            return Loc.T("settings.title");
        });
        Assert.Equal("SETTINGS", inner);

    }

    [Theory]
    [InlineData("auto", "tr", "tr")]
    [InlineData("auto", "TR", "tr")]
    [InlineData("auto", "en", "en")]
    [InlineData("auto", "de", "en")]
    [InlineData("auto", null, "en")]
    [InlineData("tr", "en", "tr")]
    [InlineData("en", "tr", "en")]
    [InlineData(null, "tr", "tr")]
    [InlineData("garbage", "fr", "en")]
    public void Resolve_FollowsWindowsLanguageOnlyForAuto(string? setting, string? windows, string expected)
    {
        Assert.Equal(expected, Loc.Resolve(setting, windows));
    }

    [Fact]
    public void MissingKey_ReturnsTheKey()
    {
        Assert.Equal("no.such.key", Loc.T("no.such.key"));
    }

    [Fact]
    public void Plural_PicksOneOnlyForExactlyOne()
    {
        using var _ = Loc.Use("en");
        Assert.Equal("1 campaign found.", Loc.Plural("health.campaigns.found", 1, 1));
        Assert.Equal("3 campaigns found.", Loc.Plural("health.campaigns.found", 3, 3));
        Assert.Equal("0 campaigns found.", Loc.Plural("health.campaigns.found", 0, 0));
    }
}

public class FmtLanguageTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    [Fact]
    public void Remaining_Turkish_IsUnchanged()
    {
        Assert.Equal("4 gün 3 sa", Fmt.Remaining(TimeSpan.FromHours(99)));
        Assert.Equal("5 sa 20 dk", Fmt.Remaining(new TimeSpan(5, 20, 0)));
        Assert.Equal("12 dk", Fmt.Remaining(TimeSpan.FromMinutes(12)));
        Assert.Equal("3 gün", Fmt.Remaining(TimeSpan.FromDays(3)));
    }

    [Fact]
    public void Remaining_English_UsesDaysHoursMinutes()
    {
        using var _ = Loc.Use("en");
        Assert.Equal("4 days 3 h", Fmt.Remaining(TimeSpan.FromHours(99)));
        Assert.Equal("1 day 2 h", Fmt.Remaining(TimeSpan.FromHours(26)));
        Assert.Equal("1 day", Fmt.Remaining(TimeSpan.FromDays(1)));
        Assert.Equal("5 h 20 min", Fmt.Remaining(new TimeSpan(5, 20, 0)));
        Assert.Equal("2 h", Fmt.Remaining(TimeSpan.FromHours(2)));
        Assert.Equal("12 min", Fmt.Remaining(TimeSpan.FromMinutes(12)));
        Assert.Equal("1 min", Fmt.Remaining(TimeSpan.FromSeconds(5)));
        Assert.Equal("1 min", Fmt.Remaining(TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void Minutes_AndAgo_English()
    {
        using var _ = Loc.Use("en");
        Assert.Equal("45 min", Fmt.Minutes(45));
        Assert.Equal("2 h", Fmt.Minutes(120));
        Assert.Equal("1 h 5 min", Fmt.Minutes(65));
        Assert.Equal("just now", Fmt.Ago(TimeSpan.FromSeconds(2)));
        Assert.Equal("30 s ago", Fmt.Ago(TimeSpan.FromSeconds(30)));
        Assert.Equal("7 min ago", Fmt.Ago(TimeSpan.FromMinutes(7)));
        Assert.Equal("3 h ago", Fmt.Ago(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void DateFormats_FollowTheLanguage()
    {
        var start = DateTimeOffset.Parse("2026-10-23T21:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-02T02:59:00Z");
        var now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

        Assert.Equal("23 Eki 21:00 → 2 Kas 02:59", Fmt.DateRange(start, end, now, Zone));
        Assert.Equal("bugün 21:00", Fmt.When(start, start.AddHours(-2), Zone));
        Assert.Equal("yarın 21:00", Fmt.When(start, start.AddDays(-1), Zone));
        Assert.Equal("23 Eki 21:00", Fmt.When(start, now, Zone));

        using var _ = Loc.Use("en");
        Assert.Equal("Oct 23, 21:00 → Nov 2, 02:59", Fmt.DateRange(start, end, now, Zone));
        Assert.Equal("today 21:00", Fmt.When(start, start.AddHours(-2), Zone));
        Assert.Equal("tomorrow 21:00", Fmt.When(start, start.AddDays(-1), Zone));
        Assert.Equal("Oct 23, 21:00", Fmt.When(start, now, Zone));
        Assert.Equal("Oct 23, 21:00 →", Fmt.DateRange(start, null, now, Zone));
        Assert.Equal("Oct 23, 2027 21:00", Fmt.DateRange(start.AddYears(1), null, now, Zone).Replace(" →", string.Empty));
    }

    [Fact]
    public void EngineBadge_English()
    {
        using var _ = Loc.Use("en");
        Assert.Equal("Watching", Fmt.EngineBadge(RustDroper.Core.Orchestration.EngineState.Watching, running: true).Text);
        Assert.Equal("Stopped", Fmt.EngineBadge(RustDroper.Core.Orchestration.EngineState.Watching, running: false).Text);
        Assert.Equal("Waiting to claim", Fmt.EngineBadge(RustDroper.Core.Orchestration.EngineState.ClaimPending, running: true).Text);
    }
}

public class AboutLanguageTests
{
    [Fact]
    public void About_TextsFollowTheLanguage()
    {
        var about = new AboutViewModel();
        Assert.Contains("Geliştirici", about.Developer, StringComparison.Ordinal); // Hakkında sayfasında geliştirici kalır
        Assert.Equal($"Rust Droper v{AboutViewModel.Version}", about.FooterText);

        using var _ = Loc.Use("en");
        Assert.StartsWith("Developer:", about.Developer, StringComparison.Ordinal);
        Assert.StartsWith("Contact:", about.Contact, StringComparison.Ordinal);
        Assert.StartsWith("Version ", about.VersionText, StringComparison.Ordinal);
        Assert.Equal($"Rust Droper v{AboutViewModel.Version}", about.FooterText); // alt satırda yalnız sürüm (geliştirici yok)
        Assert.DoesNotContain("Pinhan", about.FooterText, StringComparison.Ordinal);
        Assert.Contains("MIT License", about.Copyright, StringComparison.Ordinal);
        Assert.Contains("independent project", about.Disclaimer, StringComparison.Ordinal);
        Assert.Equal(3, about.ThirdParty.Count);
    }
}

/// <summary>Küresel dili gerçekten değiştiren testler (koleksiyon paralel çalışmaz); her test sonunda Türkçeye döner.</summary>
[Collection("Loc global")]
public class LanguageSwitchTests : IDisposable
{
    public LanguageSwitchTests()
    {
        Loc.Configure("tr");
    }

    public void Dispose() => Loc.Configure("tr");

    [Fact]
    public void SettingsViewModel_ChangingLanguage_SavesAndSwitchesInstantly()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        var vm = new SettingsViewModel(settings, new FakeAutostart());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("auto", vm.Language);
        vm.Language = "en";

        Assert.Equal("en", settings.Current.Language);
        Assert.Equal("en", Loc.Language);
        Assert.Contains(string.Empty, raised); // tüm özellikler yenilensin
        Assert.Equal("Automatic (Windows)", vm.LanguageOptions[0].Label);
        Assert.True(vm.LanguageOptions[2].IsSelected);
        Assert.False(vm.LanguageOptions[1].IsSelected);

        vm.Language = "tr";
        Assert.Equal("tr", Loc.Language);
        Assert.Equal("Otomatik (Windows)", vm.LanguageOptions[0].Label);
    }

    [Fact]
    public void SettingsViewModel_IgnoresBlankLanguageValue()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        settings.Update(s => s with { Language = "en" });
        var vm = new SettingsViewModel(settings, new FakeAutostart());

        vm.Language = string.Empty;

        Assert.Equal("en", settings.Current.Language);
    }

    [Fact]
    public void Configure_RaisesLanguageChangedOnlyOnRealChange()
    {
        var count = 0;
        void Handler() => count++;
        Loc.LanguageChanged += Handler;
        try
        {
            Loc.Configure("tr"); // zaten Türkçe
            Assert.Equal(0, count);
            Loc.Configure("en");
            Assert.Equal(1, count);
            Loc.Configure("auto", windowsLanguage: "de"); // → en: değişmedi
            Assert.Equal(1, count);
            Loc.Configure("auto", windowsLanguage: "tr");
            Assert.Equal(2, count);
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
        }
    }

    [Fact]
    public void AutoFollowsWindowsLanguage()
    {
        Assert.Equal("en", Loc.Configure("auto", windowsLanguage: "en"));
        Assert.Equal("tr", Loc.Configure("auto", windowsLanguage: "tr"));
        Assert.Equal("en", Loc.Configure("en", windowsLanguage: "tr"));
    }

    [Fact]
    public async Task XamlMarkupExtension_UpdatesLiveWhenLanguageChanges()
    {
        using var sta = RustDroper.App.Tests.Player.StaDispatcher.Start();
        await sta.InvokeAsync(() =>
        {
            const string xaml = "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:loc=\"clr-namespace:RustDroper.App.Localization;assembly=RustDroper\">"
                + "<TextBlock x:Name=\"A\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Text=\"{loc:Loc settings.title}\" />"
                + "<Button xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Name=\"B\" Content=\"{loc:Loc settings.language.auto}\" ToolTip=\"{loc:Loc health.recheck.tip}\" /></StackPanel>";
            var root = (System.Windows.Controls.StackPanel)System.Windows.Markup.XamlReader.Parse(xaml);
            var text = (System.Windows.Controls.TextBlock)root.Children[0];
            var button = (System.Windows.Controls.Button)root.Children[1];
            Assert.Equal("AYARLAR", text.Text);
            Assert.Equal("Otomatik (Windows)", button.Content);

            Loc.Configure("en");

            Assert.Equal("SETTINGS", text.Text);
            Assert.Equal("Automatic (Windows)", button.Content);
            Assert.Equal("Re-read the inventory and campaigns, then check again", button.ToolTip);
        });
    }

    [Fact]
    public void About_RefreshesWhenLanguageChanges()
    {
        var about = new AboutViewModel();
        var raised = new List<string?>();
        about.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Loc.Configure("en");

        Assert.Contains(string.Empty, raised);
        Assert.StartsWith("Developer:", about.Developer, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreferencedViewModel_IsNotKeptAliveByTheLanguageEvent()
    {
        static WeakReference Create()
        {
            var vm = new AboutViewModel();
            return new WeakReference(vm);
        }

        var weak = Create();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Loc.Configure("en"); // ölü abonelik temizlenir, görünüm modeli bırakılabilir
        GC.Collect();

        Assert.False(weak.IsAlive);
    }
}
