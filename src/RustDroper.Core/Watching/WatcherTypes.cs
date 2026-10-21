using RustDroper.Core.Domain;

namespace RustDroper.Core.Watching;

public enum WatchEnd
{
    /// <summary>Çağıran iptal etti (normal durdurma).</summary>
    Cancelled,

    /// <summary>Yayın kapandı ya da bulunamadı.</summary>
    Offline,

    /// <summary>Tekrarlayan hatalar nedeniyle izleyici vazgeçti; neden <see cref="WatchResult.Reason"/>'da.</summary>
    Failed,
}

public sealed record WatchResult(WatchEnd End, string? Reason);

/// <summary>Bir kanalı "izleyen" bileşen (kademe: HLS, gizli oynatıcı...). Bkz. docs/02 §5.</summary>
public interface IWatcher
{
    WatcherStats Stats { get; }

    /// <summary>Kanalı izler; iptal edilene, yayın kapanana ya da kalıcı hata olana kadar döner.</summary>
    Task<WatchResult> RunAsync(LiveChannel channel, CancellationToken cancellationToken);
}

/// <summary>İzleyicinin canlı sayaçları (iş parçacığı güvenli); tanılama ve "trafik gerçekten akıyor mu" göstergesi.</summary>
public sealed class WatcherStats
{
    private readonly Lock _gate = new();
    private long _segments;
    private long _bytes;
    private DateTimeOffset? _lastSegmentAt;
    private string? _variant;

    public long SegmentsFetched
    {
        get { lock (_gate) { return _segments; } }
    }

    public long BytesFetched
    {
        get { lock (_gate) { return _bytes; } }
    }

    public DateTimeOffset? LastSegmentAt
    {
        get { lock (_gate) { return _lastSegmentAt; } }
    }

    public string? Variant
    {
        get { lock (_gate) { return _variant; } }
    }

    internal void RecordSegment(long bytes, DateTimeOffset at)
    {
        lock (_gate)
        {
            _segments++;
            _bytes += bytes;
            _lastSegmentAt = at;
        }
    }

    /// <summary>Tarayıcı tabanlı izleyicide "video oynuyor" gözlemi (<see cref="LastSegmentAt"/> son canlılık işaretidir).</summary>
    internal void RecordPlaying(DateTimeOffset at)
    {
        lock (_gate)
        {
            _lastSegmentAt = at;
        }
    }

    internal void SetVariant(string name)
    {
        lock (_gate)
        {
            _variant = name;
        }
    }
}
