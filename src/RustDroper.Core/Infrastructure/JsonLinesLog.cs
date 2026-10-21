using System.Text;
using System.Text.Json;

namespace RustDroper.Core.Infrastructure;

/// <summary>
/// Ekle-yalnız JSONL günlüğü (ör. claim geçmişi). Çökme sonucu yarım kalan son satır okumada atlanır, diğer kayıtlar etkilenmez.
/// </summary>
public sealed class JsonLinesLog<T>
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public JsonLinesLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public void Append(T record)
    {
        var line = JsonSerializer.Serialize(record, JsonDefaults.Compact) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    public IReadOnlyList<T> ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var records = new List<T>();
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<T>(line, JsonDefaults.Options) is { } record)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // Bozuk/yarım satır: atla.
                }
            }

            return records;
        }
    }
}
