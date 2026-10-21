using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RustDroper.Core.Media;

/// <summary>
/// Drop görsel/videolarının disk önbelleği. Güvenlik: yalnız HTTPS ve izin verilen alan adları (yönlendirme sonrası da), tek dosya boyut sınırı,
/// içerik türü + dosya imzası denetimi (JPEG/PNG/WEBP/MP4). Toplam boyut sınırı aşılınca en eski dosyalar silinir. Hata → <c>null</c> (arayüz yer tutucu çizer).
/// </summary>
public sealed class DropMediaCache
{
    public const long DefaultMaxTotalBytes = 20L * 1024 * 1024;
    public const long DefaultMaxFileBytes = 4L * 1024 * 1024;

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly HashSet<string> _hosts;
    private readonly long _maxTotal;
    private readonly long _maxFile;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _purgeGate = new(1, 1);

    public DropMediaCache(string directory, HttpClient http, IEnumerable<string> allowedHosts, long maxTotalBytes = DefaultMaxTotalBytes, long maxFileBytes = DefaultMaxFileBytes, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _directory = directory;
        _http = http;
        _hosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        _maxTotal = maxTotalBytes;
        _maxFile = maxFileBytes;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Directory => _directory;

    /// <summary>Adres önbellekteyse yerel yolu (ağ yok), değilse <c>null</c>. Kullanılan dosya "yeni" sayılır (silinme sırasında korunur).</summary>
    public string? TryGet(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!IsAllowed(url) || !System.IO.Directory.Exists(_directory))
        {
            return null;
        }

        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, Name(url) + ".*"))
            {
                if (!file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1))
                    {
                        File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // en son kullanılan en yeni kalsın (silme sırası)
                    }

                    return file;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Medya önbelleği okunamadı");
        }

        return null;
    }

    /// <summary>Önbellekten verir; yoksa indirir. Reddedilen/başarısız → <c>null</c> (istisna atmaz; iptal hariç).</summary>
    public async Task<string?> EnsureAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!IsAllowed(url))
        {
            return null;
        }

        if (TryGet(url) is { } cached)
        {
            return cached;
        }

        var key = Name(url);
        var task = _inflight.GetOrAdd(key, _ => RunAsync(url, key));
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> RunAsync(Uri url, string key)
    {
        try
        {
            await Task.Yield(); // GetOrAdd bitmeden kayıt silinmesin
            return await DownloadAsync(url, key).ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public bool IsAllowed(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && _hosts.Contains(url.Host);

    private async Task<string?> DownloadAsync(Uri url, string key)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !IsAllowed(response.RequestMessage?.RequestUri ?? url))
            {
                return null;
            }

            var extension = ExtensionFor(response.Content.Headers.ContentType?.MediaType);
            if (extension is null || response.Content.Headers.ContentLength > _maxFile)
            {
                return null;
            }

            var target = Path.Combine(_directory, key + extension);
            var temp = Path.Combine(_directory, key + ".part");
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[16 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > _maxFile)
                        {
                            return null;
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }
                }

                if (!HasValidSignature(temp, extension))
                {
                    return null;
                }

                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }

            await PurgeAsync().ConfigureAwait(false);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Medya indirilemedi: {Host}", url.Host);
            return null;
        }
    }

    /// <summary>Toplam boyut sınırını aşan önbelleği en eski dosyalardan başlayarak küçültür.</summary>
    public async Task PurgeAsync()
    {
        await _purgeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return;
            }

            var files = new DirectoryInfo(_directory).EnumerateFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= _maxTotal)
                {
                    break;
                }

                total -= file.Length;
                TryDelete(file.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Medya önbelleği temizlenemedi");
        }
        finally
        {
            _purgeGate.Release();
        }
    }

    private static string Name(Uri url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..24].ToLowerInvariant();

    private static string? ExtensionFor(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        _ => null,
    };

    private static bool HasValidSignature(string path, string extension)
    {
        Span<byte> head = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        if (stream.Read(head) < 12)
        {
            return false;
        }

        return extension switch
        {
            ".jpg" => head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
            ".png" => head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47,
            ".webp" => head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P',
            ".mp4" => head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p',
            _ => false,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
