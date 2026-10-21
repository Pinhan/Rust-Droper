namespace RustDroper.Core.Infrastructure;

/// <summary>
/// Kayan pencere hız sınırlayıcı: her <paramref name="window"/> içinde en fazla <paramref name="permits"/> çağrı.
/// Twitch GQL için varsayılan 5 istek/sn (referans: aşılırsa "her şey bozuluyor").
/// </summary>
public sealed class SlidingWindowRateLimiter
{
    private readonly int _permits;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Queue<long> _stamps = new();
    private readonly Lock _gate = new();

    public SlidingWindowRateLimiter(int permits, TimeSpan window, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _permits = permits;
        _window = window;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var now = _time.GetTimestamp();
                while (_stamps.Count > 0 && _time.GetElapsedTime(_stamps.Peek(), now) >= _window)
                {
                    _stamps.Dequeue();
                }

                if (_stamps.Count < _permits)
                {
                    _stamps.Enqueue(now);
                    return;
                }

                wait = _window - _time.GetElapsedTime(_stamps.Peek(), now);
            }

            await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
