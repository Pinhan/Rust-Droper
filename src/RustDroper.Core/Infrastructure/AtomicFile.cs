using System.Globalization;

namespace RustDroper.Core.Infrastructure;

public enum FileSource
{
    /// <summary>Dosya yok (ya da okunabilir hiçbir kopya yok).</summary>
    Missing,
    Primary,

    /// <summary>Ana dosya bozuktu; son sağlam yedekten (.bak) kurtarıldı.</summary>
    Backup,
}

public readonly record struct FileReadResult(byte[]? Content, FileSource Source, string? Warning);

/// <summary>
/// Yarım yazma/bozulmaya dayanıklı dosya yazma-okuma: önce <c>.tmp</c>'ye yaz + diske flush, sonra atomik değiştir,
/// eski sürüm <c>.bak</c> olarak kalır (referans projelerde <c>settings.json</c> boşalması sorunu için).
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var temp = fullPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(fullPath))
        {
            File.Replace(temp, fullPath, fullPath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, fullPath);
        }
    }

    /// <summary>
    /// Ana dosyayı, geçerli değilse yedeği okur. Bozuk dosya silinmez, <c>.corrupt-*</c> olarak kenara alınır;
    /// yedekten kurtarılırsa yedek ana dosya olarak geri konur (sonraki yazma bozuk dosyayı yedeğin üstüne yazmasın diye).
    /// </summary>
    public static FileReadResult ReadWithFallback(string path, Func<byte[], bool> isValid)
    {
        ArgumentNullException.ThrowIfNull(isValid);
        var fullPath = Path.GetFullPath(path);
        var backup = fullPath + ".bak";
        string? warning = null;

        if (File.Exists(fullPath))
        {
            var bytes = File.ReadAllBytes(fullPath);
            if (isValid(bytes))
            {
                return new FileReadResult(bytes, FileSource.Primary, null);
            }

            warning = $"{Path.GetFileName(fullPath)} geçersiz/bozuk; kenara alındı.";
            Quarantine(fullPath);
        }

        if (File.Exists(backup))
        {
            var bytes = File.ReadAllBytes(backup);
            if (isValid(bytes))
            {
                File.Copy(backup, fullPath, overwrite: true);
                return new FileReadResult(bytes, FileSource.Backup, warning ?? $"{Path.GetFileName(fullPath)} bulunamadı; yedekten kurtarıldı.");
            }

            warning = $"{Path.GetFileName(fullPath)} ve yedeği geçersiz; kenara alındı.";
            Quarantine(backup);
        }

        return new FileReadResult(null, FileSource.Missing, warning);
    }

    private static void Quarantine(string file)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        File.Move(file, $"{file}.corrupt-{stamp}", overwrite: true);
    }
}
