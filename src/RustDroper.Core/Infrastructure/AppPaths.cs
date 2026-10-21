namespace RustDroper.Core.Infrastructure;

/// <summary>
/// Tüm kullanıcı verisi tek bir klasörde. Varsayılan konum <c>%LOCALAPPDATA%\RustDroper</c> (gizli): exe nereye kopyalanırsa
/// kopyalansın, exe yanında hiçbir veri/ayar dosyası oluşmaz ve giriş (DPAPI'li <c>secrets.dat</c>) korunur.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    /// <summary>Eski konumdan yapılan tek seferlik taşımanın sonucu (yalnız <see cref="ForPortable"/> bu açılışta taşıma yaptıysa).</summary>
    public LegacyMigrationResult? Migration { get; private init; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");

    public string StateFile => Path.Combine(DataDirectory, "state.json");

    public string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    public string HashesFile => Path.Combine(DataDirectory, "hashes.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Drop görsel/video önbelleği (en çok ~20 MB; silinirse yeniden iner).</summary>
    public string MediaDirectory => Path.Combine(DataDirectory, "media");

    public string WebView2Directory => Path.Combine(DataDirectory, "webview2");

    /// <summary><c>%LOCALAPPDATA%\RustDroper</c> (LocalAppData çözümlenemezse kullanıcı profili altında).</summary>
    public static string DefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(root, "RustDroper");
    }

    /// <summary>
    /// <c>RUSTDROPER_DATA</c> ortam değişkeni varsa orası (taşıma yapılmaz); yoksa <see cref="DefaultDirectory"/>.
    /// Varsayılan konumda ilk açılışta <c>&lt;exe&gt;\data</c> (eski portable düzen) varsa içeriği buraya TAŞINIR.
    /// </summary>
    public static AppPaths ForPortable()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("RUSTDROPER_DATA");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return new AppPaths(overrideDirectory);
        }

        var processPath = Environment.ProcessPath;
        if (Path.GetFileNameWithoutExtension(processPath)?.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Test süreci kazara gerçek kullanıcı verisine (giriş, ayarlar, günlük) dokunmasın.
            return new AppPaths(Path.Combine(Path.GetTempPath(), "rustdroper-testhost-data"));
        }

        var target = DefaultDirectory();
        var legacy = new List<string>();
        var isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        var exeDirectory = processPath is null || isDotnetHost
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
        legacy.Add(Path.Combine(exeDirectory, "data"));

        var migration = LegacyDataMigrator.MigrateIfNeeded(target, legacy);
        return new AppPaths(target) { Migration = migration };
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        LegacyDataMigrator.TryHide(DataDirectory);
    }
}
