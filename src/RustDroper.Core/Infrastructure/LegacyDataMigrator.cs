namespace RustDroper.Core.Infrastructure;

/// <param name="Source">Taşınan eski veri klasörü.</param>
/// <param name="Moved">Taşınan öğe sayısı.</param>
/// <param name="Skipped">Hedefte zaten var olduğu için dokunulmayan öğeler.</param>
/// <param name="Failed">Taşınamayan öğeler (kilitli dosya vb.); eski yerinde kalır.</param>
public sealed record LegacyMigrationResult(string Source, int Moved, IReadOnlyList<string> Skipped, IReadOnlyList<string> Failed);

/// <summary>
/// Eski konumdaki (<c>&lt;exe&gt;\data</c>) veriyi yeni konuma <b>taşır</b> (kopyalamaz: Twitch yenileme belirteci tek kullanımlık
/// olabileceğinden iki yerde canlı kopya bırakılmaz). Yalnız eski klasörde <c>secrets.dat</c> (giriş) varsa ve yeni konumda yoksa çalışır;
/// aksi halde eski klasöre dokunulmaz. Hata fırlatmaz, öğe bazında en iyi çabayla ilerler.
/// </summary>
public static class LegacyDataMigrator
{
    private const string Marker = "secrets.dat";

    public static LegacyMigrationResult? MigrateIfNeeded(string target, IEnumerable<string> legacyDirectories)
    {
        try
        {
            if (File.Exists(Path.Combine(target, Marker)))
            {
                return null; // yeni konum zaten kurulu → eski veriye dokunma (çakışma yok)
            }

            foreach (var legacy in legacyDirectories)
            {
                var source = Path.GetFullPath(legacy);
                if (string.Equals(source, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(Path.Combine(source, Marker)))
                {
                    continue;
                }

                return MoveAll(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // taşıma isteğe bağlı: başarısızlık uygulamayı durdurmaz; kullanıcı yeniden giriş yapar
        }

        return null;
    }

    private static LegacyMigrationResult MoveAll(string source, string target)
    {
        Directory.CreateDirectory(target);
        var moved = 0;
        var skipped = new List<string>();
        var failed = new List<string>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(target, name);
            try
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    skipped.Add(name);
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    MoveDirectory(entry, destination);
                }
                else
                {
                    File.Move(entry, destination);
                }

                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(name);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(source).Any())
            {
                Directory.Delete(source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new LegacyMigrationResult(source, moved, skipped, failed);
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException) when (!Directory.Exists(destination))
        {
            // farklı sürücü: kopyala + sil
            CopyDirectory(source, destination);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    /// <summary>Klasörü gizli yapar (en iyi çaba).</summary>
    public static void TryHide(string directory)
    {
        try
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(directory, attributes | FileAttributes.Hidden);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
