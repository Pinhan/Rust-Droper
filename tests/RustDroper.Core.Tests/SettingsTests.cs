using RustDroper.Core.Configuration;

namespace RustDroper.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_AutoClaimIsOn_LanguageAuto_GameRust()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));

        Assert.True(settings.Current.AutoClaim);
        Assert.False(settings.Current.StartWithWindows);
        Assert.False(settings.Current.StartMinimized);
        Assert.Equal("auto", settings.Current.Language);
        Assert.Equal("rust", settings.Current.GameSlug);
        Assert.Null(settings.LoadWarning);
    }

    [Fact]
    public void Update_PersistsAndReloads()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        new SettingsService(path).Update(s => s with { AutoClaim = true, StartMinimized = true });

        var reloaded = new SettingsService(path);

        Assert.True(reloaded.Current.AutoClaim);
        Assert.True(reloaded.Current.StartMinimized);
        Assert.False(reloaded.Current.StartWithWindows);
    }

    [Fact]
    public void Update_RaisesChangedOnlyWhenSomethingChanged()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.File("settings.json"));
        var raised = 0;
        settings.Changed += _ => raised++;

        settings.Update(s => s with { AutoClaim = true }); // zaten açık (varsayılan)
        settings.Update(s => s with { AutoClaim = false });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsWithWarning()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ bu json degil");

        var settings = new SettingsService(path);

        Assert.True(settings.Current.AutoClaim);
        Assert.NotNull(settings.LoadWarning);
    }

    [Fact]
    public void Normalization_RepairsUnknownLanguageToAutoAndBlankSlug()
    {
        var normalized = new AppSettings { Language = "xx", GameSlug = "  " }.Normalized();

        Assert.Equal("auto", normalized.Language);
        Assert.Equal("rust", normalized.GameSlug);
    }

    [Theory]
    [InlineData("tr", "tr")]
    [InlineData(" TR ", "tr")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("auto", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    [InlineData("de", "auto")]
    public void LanguageSetting_IsNormalized(string? value, string expected)
    {
        Assert.Equal(expected, SupportedLanguages.Normalize(value));
        Assert.Equal(expected, new AppSettings { Language = value! }.Normalized().Language);
    }

    [Fact]
    public void OldFileWithTurkish_StaysTurkish_AndFileWithoutLanguageField_BecomesTurkish()
    {
        using var dir = new TempDir();
        var explicitTr = dir.File("a.json");
        File.WriteAllText(explicitTr, "{ \"schemaVersion\": 1, \"data\": { \"language\": \"tr\", \"autoClaim\": true, \"autoClaimDefaultMigrated\": true } }");
        var noField = dir.File("b.json");
        File.WriteAllText(noField, "{ \"schemaVersion\": 1, \"data\": { \"autoClaim\": true, \"autoClaimDefaultMigrated\": true } }");

        Assert.Equal("tr", new SettingsService(explicitTr).Current.Language);
        Assert.Equal("tr", new SettingsService(noField).Current.Language);
    }

    [Fact]
    public void LanguageChoice_PersistsAcrossReload()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        new SettingsService(path).Update(s => s with { Language = "en" });

        Assert.Equal("en", new SettingsService(path).Current.Language);
    }

    [Theory]
    [InlineData("Kick", "Kick")]
    [InlineData(" kick ", "Kick")]
    [InlineData("Twitch", "Twitch")]
    [InlineData("xx", "Twitch")]
    [InlineData(null, "Twitch")]
    public void LastPlatform_IsNormalized(string? value, string expected)
    {
        Assert.Equal(expected, (new AppSettings { LastPlatform = value! }).Normalized().LastPlatform);
    }

    [Fact]
    public void OldFileWithAutoClaimOff_IsSwitchedOnOnce_ThenUserChoiceIsKept()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"data\":{\"autoClaim\":false,\"gameSlug\":\"rust\"}}");

        var first = new SettingsService(path);
        Assert.True(first.Current.AutoClaim); // varsayılan açığa çevrildi (bir kez)
        Assert.True(first.Current.AutoClaimDefaultMigrated);

        first.Update(s => s with { AutoClaim = false }); // kullanıcı bilerek kapatır
        var second = new SettingsService(path);
        Assert.False(second.Current.AutoClaim); // artık korunur
    }

    [Fact]
    public void OldSettingsFileWithoutLastPlatform_LoadsAsTwitch()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"data\":{\"autoClaim\":true}}");

        var settings = new SettingsService(path);

        Assert.Equal("Twitch", settings.Current.LastPlatform);
        Assert.True(settings.Current.AutoClaim);
    }

    [Fact]
    public void LastPlatform_PersistsAcrossReload()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        new SettingsService(path).Update(s => s with { LastPlatform = "Kick" });

        Assert.Equal("Kick", new SettingsService(path).Current.LastPlatform);
    }
}
