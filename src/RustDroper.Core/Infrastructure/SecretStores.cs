using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustDroper.Core.Infrastructure;

public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);
}

/// <summary>
/// Günlüklerde/hata mesajlarında sırları gizler. Birincil savunma sırları hiç loglamamaktır;
/// bu, ikinci kat savunmadır: bilinen değerler birebir, ayrıca token benzeri desenler maskelenir.
/// </summary>
public static partial class SecretMasker
{
    private const string Mask = "***";
    private static readonly Lock Gate = new();
    private static string[] s_known = [];

    public static void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 6)
        {
            return;
        }

        lock (Gate)
        {
            if (!s_known.Contains(secret, StringComparer.Ordinal))
            {
                s_known = [.. s_known, secret];
            }
        }
    }

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string[] known;
        lock (Gate)
        {
            known = s_known;
        }

        foreach (var secret in known)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return TokenPattern().Replace(text, "$1$2" + Mask);
    }

    // oauth:xxxx · Bearer xxxx · "access_token":"xxxx" · auth-token=xxxx · Client-Integrity: xxxx
    [GeneratedRegex(
        """(?i)\b(oauth|bearer|access_token|refresh_token|auth-token|authorization|client-integrity|device_code)(["'\s:=]+)[A-Za-z0-9_\-\.=+/]{12,}""",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

/// <summary>Bellekte tutulan sır deposu (test/geliştirme).</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value) => _values[key] = value;

    public void Remove(string key) => _values.Remove(key);
}

/// <summary>
/// <c>secrets.dat</c>: tüm sırlar tek JSON sözlüğü, <b>DPAPI (CurrentUser)</b> ile şifreli, atomik yazılır.
/// Dosya başka Windows kullanıcısına/makineye kopyalanırsa çözülemez → "yok" sayılır, kullanıcı yeniden giriş yapar.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RustDroper.secrets.v1");

    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _cache;

    public DpapiSecretStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            var value = EnsureLoaded().GetValueOrDefault(key);
            SecretMasker.Register(value);
            return value;
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            SecretMasker.Register(value);
            var values = EnsureLoaded();
            values[key] = value;
            Persist(values);
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            var values = EnsureLoaded();
            if (values.Remove(key))
            {
                Persist(values);
            }
        }
    }

    private Dictionary<string, string> EnsureLoaded()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        Dictionary<string, string>? loaded = null;
        AtomicFile.ReadWithFallback(_path, bytes => TryDecrypt(bytes, out loaded));
        _cache = loaded ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return _cache;
    }

    private void Persist(Dictionary<string, string> values)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(values);
        AtomicFile.WriteAllBytes(_path, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
    }

    private static bool TryDecrypt(byte[] bytes, out Dictionary<string, string>? values)
    {
        values = null;
        try
        {
            var json = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values is not null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return false;
        }
    }
}
