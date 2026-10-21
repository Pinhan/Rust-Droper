using RustDroper.Core.Changelog;

namespace RustDroper.Core.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("0.1.0", true)]
    [InlineData("10.20.30", true)]
    [InlineData("1.0", false)]
    [InlineData("1.0.0.0", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("1.0.0-beta", false)]
    [InlineData("01.0.0", false)]
    [InlineData("1.-1.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParse_IsStrict(string? text, bool expected) => Assert.Equal(expected, SemVer.TryParse(text, out _));

    [Theory]
    [InlineData("1.0.0", "2.0.0", ChangelogType.Major)]
    [InlineData("1.4.7", "2.0.0", ChangelogType.Major)]
    [InlineData("1.0.0", "1.1.0", ChangelogType.Minor)]
    [InlineData("1.3.9", "1.4.0", ChangelogType.Minor)]
    [InlineData("1.0.0", "1.0.1", ChangelogType.Patch)]
    [InlineData("1.2.3", "1.2.9", ChangelogType.Patch)]
    public void BumpFrom_ClassifiesTheIncrease(string previous, string next, ChangelogType expected)
    {
        Assert.True(SemVer.TryParse(previous, out var a));
        Assert.True(SemVer.TryParse(next, out var b));
        Assert.Equal(expected, b.BumpFrom(a));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")] // artış yok
    [InlineData("1.1.0", "1.0.5")] // geri gitme
    [InlineData("1.0.0", "2.1.0")] // MAJOR artınca MINOR sıfırlanmalı
    [InlineData("1.0.0", "2.0.1")] // MAJOR artınca PATCH sıfırlanmalı
    [InlineData("1.0.0", "1.1.1")] // MINOR artınca PATCH sıfırlanmalı
    public void BumpFrom_RejectsInvalidIncreases(string previous, string next)
    {
        Assert.True(SemVer.TryParse(previous, out var a));
        Assert.True(SemVer.TryParse(next, out var b));
        Assert.Null(b.BumpFrom(a));
    }

    [Fact]
    public void CompareTo_OrdersNumerically()
    {
        Assert.True(SemVer.TryParse("1.10.0", out var ten));
        Assert.True(SemVer.TryParse("1.9.0", out var nine));
        Assert.True(ten.CompareTo(nine) > 0);
    }
}

public class ChangelogParserTests
{
    private const string Valid = """
        { "releases": [
          { "version": "1.1.0", "date": "2026-11-01", "type": "minor",
            "tag": { "tr": "Yeni", "en": "New" }, "summary": { "tr": "Özet", "en": "Summary" },
            "added": [ { "tr": "A", "en": "a" } ], "changed": [], "fixed": [ { "tr": "F", "en": "f" } ], "removed": [] },
          { "version": "1.0.0", "date": "2026-10-20", "type": "baseline",
            "tag": { "tr": "İlk", "en": "First" }, "summary": { "tr": "s", "en": "s" }, "added": [ { "tr": "x", "en": "x" } ] }
        ] }
        """;

    [Fact]
    public void ParsesEntriesInFileOrder()
    {
        var result = ChangelogParser.Parse(Valid);

        Assert.Empty(result.Warnings);
        Assert.Equal(["1.1.0", "1.0.0"], result.Entries.Select(e => e.Version));
        Assert.Equal(ChangelogType.Minor, result.Entries[0].Type);
        Assert.Equal(new DateOnly(2026, 11, 1), result.Entries[0].Date);
        Assert.Equal("Yeni", result.Entries[0].Tag.Tr);
        Assert.Single(result.Entries[0].Added);
        Assert.Empty(result.Entries[0].Changed);
        Assert.Empty(result.Entries[1].Fixed); // alan hiç yoksa boş
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ bu json degil")]
    [InlineData("{}")]
    [InlineData("{ \"releases\": 5 }")]
    [InlineData("[ 1, 2, \"x\" ]")]
    public void BrokenInput_NeverThrows_AndYieldsNoEntries(string? json)
    {
        var result = ChangelogParser.Parse(json);

        Assert.Empty(result.Entries);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void InvalidEntries_AreSkipped_ValidOnesKept()
    {
        const string json = """
            { "releases": [
              { "version": "x.y", "date": "2026-10-20", "type": "minor" },
              { "version": "1.2.0", "date": "20.10.2026", "type": "minor" },
              { "version": "1.2.0", "date": "2026-10-20", "type": "huge" },
              "metin",
              { "version": "1.1.0", "date": "2026-10-20", "type": "MINOR" }
            ] }
            """;

        var result = ChangelogParser.Parse(json);

        Assert.Equal(["1.1.0"], result.Entries.Select(e => e.Version));
        Assert.Equal(4, result.Warnings.Count);
    }

    [Fact]
    public void MissingLanguage_FallsBackToTheOther_AndEmptyItemsAreSkipped()
    {
        const string json = """
            { "releases": [ { "version": "1.0.1", "date": "2026-10-21", "type": "patch",
              "fixed": [ { "tr": "Sadece Türkçe" }, { "en": "English only" }, { "tr": "", "en": "" }, "düz metin", { } ] } ] }
            """;

        var entry = ChangelogParser.Parse(json).Entries.Single();

        Assert.Equal(2, entry.Fixed.Count);
        Assert.Equal("Sadece Türkçe", entry.Fixed[0].Get("en")); // İngilizce yok → Türkçe
        Assert.Equal("English only", entry.Fixed[1].Get("tr")); // Türkçe yok → İngilizce
        Assert.False(entry.Fixed[0].IsComplete);
    }

    [Fact]
    public void ToleratesCommentsAndTrailingCommas()
    {
        const string json = "{ \"releases\": [ { \"version\": \"1.0.0\", \"date\": \"2026-10-20\", \"type\": \"baseline\", /* not */ }, ], }";

        Assert.Single(ChangelogParser.Parse(json).Entries);
    }

    [Fact]
    public void RootArray_IsAccepted()
    {
        const string json = "[ { \"version\": \"1.0.0\", \"date\": \"2026-10-20\", \"type\": \"baseline\" } ]";

        Assert.Single(ChangelogParser.Parse(json).Entries);
    }
}

public class ChangelogMarkdownTests
{
    private static IReadOnlyList<ChangelogEntry> Entries() => ChangelogParser.Parse("""
        { "releases": [
          { "version": "1.1.0", "date": "2026-11-01", "type": "minor", "tag": { "tr": "Yeni", "en": "New" }, "summary": { "tr": "Özet", "en": "Summary" },
            "added": [ { "tr": "Ekle", "en": "Add" } ], "removed": [ { "tr": "Çıkar", "en": "Drop" } ] },
          { "version": "1.0.0", "date": "2026-10-20", "type": "baseline", "tag": { "tr": "İlk", "en": "First" }, "summary": { "tr": "s", "en": "s" },
            "added": [ { "tr": "x", "en": "x" } ] }
        ] }
        """).Entries;

    [Fact]
    public void English_OmitsEmptySections_AndUsesLfOnly()
    {
        var text = ChangelogMarkdown.Render(Entries(), "en");

        Assert.StartsWith("# Changelog\n", text, StringComparison.Ordinal);
        Assert.Contains("## [1.1.0] — 2026-11-01 · MINOR · New\n", text, StringComparison.Ordinal);
        Assert.Contains("## [1.0.0] — 2026-10-20 · BASELINE · First\n", text, StringComparison.Ordinal);
        Assert.Contains("### Added\n\n- Add\n", text, StringComparison.Ordinal);
        Assert.Contains("### Removed\n\n- Drop\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("### Changed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("### Fixed", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("- x\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Turkish_UsesTurkishHeadings()
    {
        var text = ChangelogMarkdown.Render(Entries(), "tr");

        Assert.StartsWith("# Değişiklik Günlüğü\n", text, StringComparison.Ordinal);
        Assert.Contains("### Eklenenler\n\n- Ekle\n", text, StringComparison.Ordinal);
        Assert.Contains("### Kaldırılanlar\n\n- Çıkar\n", text, StringComparison.Ordinal);
        Assert.Contains("· İlk\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("### Added", text, StringComparison.Ordinal);
    }
}
