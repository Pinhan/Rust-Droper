using System.Text.Json;
using System.Text.Json.Serialization;

namespace RustDroper.Core.Infrastructure;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Tek satırlık kayıtlar (JSONL) için girintisiz seçenekler.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

public sealed record StoreLoadResult<T>(T Value, FileSource Source, string? Warning);

/// <summary>
/// <c>{ "schemaVersion": n, "data": ... }</c> zarfıyla atomik JSON deposu. Dosya yok/bozuk/daha yeni sürümse çökmez,
/// varsayılan değeri ve nedenini (Warning) döndürür.
/// </summary>
public sealed class AtomicJsonStore<T>
    where T : class, new()
{
    private readonly string _path;
    private readonly int _schemaVersion;
    private readonly JsonSerializerOptions _options;
    private readonly Lock _gate = new();

    public AtomicJsonStore(string path, int schemaVersion, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _schemaVersion = schemaVersion;
        _options = options ?? JsonDefaults.Options;
    }

    public StoreLoadResult<T> Load()
    {
        lock (_gate)
        {
            T? value = null;
            var read = AtomicFile.ReadWithFallback(_path, bytes => TryParse(bytes, out value));
            return read.Content is null || value is null
                ? new StoreLoadResult<T>(new T(), FileSource.Missing, read.Warning)
                : new StoreLoadResult<T>(value, read.Source, read.Warning);
        }
    }

    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            var envelope = new Envelope { SchemaVersion = _schemaVersion, Data = value };
            AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(envelope, _options));
        }
    }

    private bool TryParse(byte[] bytes, out T? value)
    {
        value = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, _options);
            if (envelope?.Data is null || envelope.SchemaVersion > _schemaVersion)
            {
                return false;
            }

            value = envelope.Data;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class Envelope
    {
        public int SchemaVersion { get; set; }

        public T? Data { get; set; }
    }
}
