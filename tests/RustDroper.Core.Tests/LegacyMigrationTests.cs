using RustDroper.Core.Infrastructure;
using Xunit;

namespace RustDroper.Core.Tests;

public sealed class LegacyMigrationTests
{
    private static void Seed(string dir, string secrets = "gizli-v1")
    {
        Directory.CreateDirectory(Path.Combine(dir, "webview2", "Default"));
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "secrets.dat"), secrets);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "state.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "webview2", "Default", "Cookies"), "c");
        File.WriteAllText(Path.Combine(dir, "logs", "a.log"), "l");
    }

    [Fact]
    public void Legacy_data_is_moved_not_copied()
    {
        using var temp = new TempDir();
        var legacy = temp.File("exe\\data");
        var target = temp.File("local\\RustDroper");
        Seed(legacy);

        var result = LegacyDataMigrator.MigrateIfNeeded(target, [legacy]);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Moved); // secrets, settings, state, webview2, logs
        Assert.Empty(result.Failed);
        Assert.Equal("gizli-v1", File.ReadAllText(Path.Combine(target, "secrets.dat")));
        Assert.True(File.Exists(Path.Combine(target, "webview2", "Default", "Cookies")));
        Assert.True(File.Exists(Path.Combine(target, "logs", "a.log")));
        Assert.False(Directory.Exists(legacy)); // boşalan eski klasör silinir: exe yanında iz kalmaz
    }

    [Fact]
    public void Existing_target_login_wins_and_legacy_is_untouched()
    {
        using var temp = new TempDir();
        var legacy = temp.File("exe\\data");
        var target = temp.File("local\\RustDroper");
        Seed(legacy, "eski");
        Seed(target, "yeni");

        var result = LegacyDataMigrator.MigrateIfNeeded(target, [legacy]);

        Assert.Null(result);
        Assert.Equal("yeni", File.ReadAllText(Path.Combine(target, "secrets.dat")));
        Assert.Equal("eski", File.ReadAllText(Path.Combine(legacy, "secrets.dat")));
    }

    [Fact]
    public void Partial_target_without_login_keeps_its_own_files_and_takes_the_rest()
    {
        using var temp = new TempDir();
        var legacy = temp.File("exe\\data");
        var target = temp.File("local\\RustDroper");
        Seed(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "{\"yeni\":true}");

        var result = LegacyDataMigrator.MigrateIfNeeded(target, [legacy]);

        Assert.NotNull(result);
        Assert.Contains("settings.json", result!.Skipped);
        Assert.Equal("{\"yeni\":true}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("gizli-v1", File.ReadAllText(Path.Combine(target, "secrets.dat")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json"))); // çakışan eski öğe yerinde kalır
    }

    [Fact]
    public void Legacy_without_login_is_ignored()
    {
        using var temp = new TempDir();
        var legacy = temp.File("exe\\data");
        var target = temp.File("local\\RustDroper");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "logs", "a.log"), "l");

        Assert.Null(LegacyDataMigrator.MigrateIfNeeded(target, [legacy]));
        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(legacy, "logs", "a.log")));
    }

    [Fact]
    public void Missing_legacy_or_same_directory_is_a_noop()
    {
        using var temp = new TempDir();
        var target = temp.File("local\\RustDroper");
        Seed(target);

        Assert.Null(LegacyDataMigrator.MigrateIfNeeded(temp.File("yok"), [temp.File("yok\\data")]));
        Assert.Null(LegacyDataMigrator.MigrateIfNeeded(target, [target]));
        Assert.True(File.Exists(Path.Combine(target, "secrets.dat")));
    }

    [Fact]
    public void Locked_file_is_reported_and_stays_while_others_move()
    {
        using var temp = new TempDir();
        var legacy = temp.File("exe\\data");
        var target = temp.File("local\\RustDroper");
        Seed(legacy);
        using var lockHandle = new FileStream(Path.Combine(legacy, "state.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = LegacyDataMigrator.MigrateIfNeeded(target, [legacy]);

        Assert.NotNull(result);
        Assert.Contains("state.json", result!.Failed);
        Assert.True(File.Exists(Path.Combine(target, "secrets.dat")));
        Assert.True(File.Exists(Path.Combine(legacy, "state.json")));
    }

    [Fact]
    public void Corrupt_secrets_file_moves_and_reads_as_logged_out()
    {
        using var temp = new TempDir();
        var legacy = temp.File("exe\\data");
        var target = temp.File("local\\RustDroper");
        Seed(legacy, "bu-gecerli-bir-DPAPI-blogu-degil");

        LegacyDataMigrator.MigrateIfNeeded(target, [legacy]);
        var store = new DpapiSecretStore(Path.Combine(target, "secrets.dat"));

        Assert.Null(store.Get("twitch.accessToken")); // çözülemeyen dosya "giriş yok" sayılır, çökmez
    }

    [Fact]
    public void Test_process_never_resolves_to_real_user_data()
    {
        var saved = Environment.GetEnvironmentVariable("RUSTDROPER_DATA");
        Environment.SetEnvironmentVariable("RUSTDROPER_DATA", null);
        try
        {
            var paths = AppPaths.ForPortable();
            Assert.NotEqual(AppPaths.DefaultDirectory(), paths.DataDirectory, StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), paths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RUSTDROPER_DATA", saved);
        }
    }

    [Fact]
    public void Default_directory_is_under_local_app_data_and_hidden_on_create()
    {
        var dir = AppPaths.DefaultDirectory();
        Assert.EndsWith("RustDroper", dir);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dir, StringComparison.OrdinalIgnoreCase);

        using var temp = new TempDir();
        var paths = new AppPaths(temp.File("d"));
        paths.EnsureCreated();
        Assert.True((File.GetAttributes(paths.DataDirectory) & FileAttributes.Hidden) != 0);
    }
}
