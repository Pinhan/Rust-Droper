using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RustDroper.Core.Orchestration;

/// <summary>İnternete (Twitch'e) gerçekten ulaşılabiliyor mu? Ağ bağdaştırıcısı "açık" görünse de yukarıda kopukluk olabilir; bu yüzden aktif yoklanır.</summary>
public interface IConnectivityProbe
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
}

public sealed record ConnectivityOptions
{
    /// <summary>Çevrimiçiyken yoklama aralığı (koptuğunu en geç bu kadar + zaman aşımı sonra anlarız; Windows ağ olayı varsa anında).</summary>
    public TimeSpan OnlineInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Çevrimdışıyken ya da bir yoklama başarısız olunca sonraki yoklamaya kadar süre (geri geldiğini en geç bu kadar sonra anlarız).</summary>
    public TimeSpan OfflineInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Çevrimdışı ilan etmek için art arda başarısız yoklama sayısı (tek bir kayıp pakette titreme olmasın).</summary>
    public int FailuresToGoOffline { get; init; } = 2;
}

/// <param name="Online"><c>true</c> = bağlantı geldi (ya da doğrulandı); <c>false</c> = koptu.</param>
/// <param name="Outage">Geldiyse kesintinin süresi (hiç kopmadan doğrulandıysa sıfır).</param>
public readonly record struct ConnectivityChange(bool Online, TimeSpan Outage);

/// <summary>
/// İnternet bağlantısı izleyicisi: Windows'un ağ olayı (<see cref="ISystemEvents.NetworkAvailabilityChanged"/>) ipucu verir, asıl karar
/// aktif yoklamadan (<see cref="IConnectivityProbe"/>) gelir — böylece modem/yukarı hat kopsa da bağdaştırıcı açık kalsa da fark edilir.
/// Yoklama yoksa yalnızca Windows olaylarına dayanır. Durum yalnız değişince (ve geri gelme doğrulanınca) <see cref="Changed"/> tetiklenir.
/// </summary>
public sealed class ConnectivityMonitor : IDisposable
{
    private readonly IConnectivityProbe? _probe;
    private readonly ISystemEvents? _events;
    private readonly TimeProvider _time;
    private readonly ConnectivityOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private bool _online = true;
    private int _failures;
    private bool _paused;
    private bool _forceNotify;
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _offlineSince;
    private TaskCompletionSource _nudge = NewNudge();
    private Task? _loop;
    private int _disposed;

    public ConnectivityMonitor(
        IConnectivityProbe? probe = null,
        ISystemEvents? events = null,
        TimeProvider? timeProvider = null,
        ConnectivityOptions? options = null,
        ILogger? logger = null)
    {
        _probe = probe;
        _events = events;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new ConnectivityOptions();
        _logger = logger ?? NullLogger.Instance;
        if (_events is not null)
        {
            _events.NetworkAvailabilityChanged += OnOsNetworkAvailability;
        }
    }

    /// <summary>Bilinen durum. Açılışta (ilk yoklamaya dek) çevrimiçi varsayılır.</summary>
    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return _online;
            }
        }
    }

    /// <summary>Durum değişince (koptu/geldi) ve Windows "ağ geldi" dediğinde doğrulanınca; herhangi bir iş parçacığında.</summary>
    public event Action<ConnectivityChange>? Changed;

    /// <summary>Yoklama döngüsünü başlatır (yoklama yoksa hiçbir şey yapmaz).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_probe is null || _loop is not null || _disposed != 0)
            {
                return;
            }

            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    /// <summary>Hemen yoklat (ör. motor ağ hatası aldı).</summary>
    public void CheckNow() => Nudge();

    /// <summary>Gerçek bir istek başarılı oldu: bağlantı vardır (yoklama yanlış "yok" demişse düzeltir).</summary>
    public void ReportReachable() => SetState(true);

    /// <summary>Uyku: yoklama ve olaylar durur (uykuya girerken ağın gidişi "kopma" sayılmasın).</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
        }
    }

    /// <summary>Uyanış: yoklama yeniden başlar ama <paramref name="quiet"/> süresince durum değiştirmez (ağ henüz gelmemiş olabilir).</summary>
    public void Resume(TimeSpan quiet)
    {
        lock (_gate)
        {
            _paused = false;
            _failures = 0;
            _forceNotify = false;
            _quietUntil = _time.GetUtcNow() + quiet;
        }

        Nudge();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_events is not null)
        {
            _events.NetworkAvailabilityChanged -= OnOsNetworkAvailability;
        }

        _cts.Cancel();
        _cts.Dispose();
    }

    private static TaskCompletionSource NewNudge() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Nudge()
    {
        lock (_gate)
        {
            _nudge.TrySetResult();
        }
    }

    private void OnOsNetworkAvailability(bool available)
    {
        lock (_gate)
        {
            if (_paused || _time.GetUtcNow() < _quietUntil)
            {
                return;
            }

            if (!available)
            {
                // Bağdaştırıcı "yok" dedi: tek bir başarısız yoklama yeter (başka bir bağdaştırıcıyla internet sürüyorsa yoklama başarılı olur → çevrimiçi kalırız).
                _failures = Math.Max(_failures, _options.FailuresToGoOffline - 1);
            }
            else
            {
                _forceNotify = true; // geri geldiğini doğrulayınca (zaten çevrimiçi görünsek bile) toparlama tetiklensin
            }
        }

        if (_probe is null)
        {
            SetState(available);
        }
        else
        {
            Nudge();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Task nudge;
                bool paused;
                lock (_gate)
                {
                    if (_nudge.Task.IsCompleted)
                    {
                        _nudge = NewNudge();
                    }

                    nudge = _nudge.Task;
                    paused = _paused;
                }

                if (!paused)
                {
                    bool reachable;
                    try
                    {
                        reachable = await _probe!.IsReachableAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _logger.LogDebug(ex, "Bağlantı yoklaması hata verdi (ulaşılamıyor sayıldı)");
                        reachable = false;
                    }

                    Apply(reachable);
                }

                TimeSpan wait;
                lock (_gate)
                {
                    wait = _online && _failures == 0 ? _options.OnlineInterval : _options.OfflineInterval;
                }

                await Task.WhenAny(Task.Delay(wait, _time, ct), nudge).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Apply(bool reachable)
    {
        bool goOffline = false;
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            if (_time.GetUtcNow() < _quietUntil)
            {
                _failures = reachable ? 0 : Math.Min(_failures + 1, _options.FailuresToGoOffline);
                return;
            }

            if (!reachable)
            {
                _failures++;
                goOffline = _failures >= _options.FailuresToGoOffline;
            }
        }

        if (reachable)
        {
            SetState(true);
        }
        else if (goOffline)
        {
            SetState(false);
        }
    }

    private void SetState(bool online)
    {
        ConnectivityChange? change = null;
        lock (_gate)
        {
            if (_paused || _time.GetUtcNow() < _quietUntil)
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (online)
            {
                _failures = 0;
                if (!_online)
                {
                    _online = true;
                    var outage = _offlineSince is { } since ? now - since : TimeSpan.Zero;
                    _offlineSince = null;
                    change = new ConnectivityChange(true, outage);
                }
                else if (_forceNotify)
                {
                    change = new ConnectivityChange(true, TimeSpan.Zero);
                }

                _forceNotify = false;
            }
            else if (_online)
            {
                _online = false;
                _offlineSince = now;
                _forceNotify = false;
                change = new ConnectivityChange(false, TimeSpan.Zero);
            }
        }

        if (change is { } value)
        {
            if (value.Online)
            {
                _logger.LogInformation("İnternet bağlantısı geldi (kesinti {Seconds:0} sn).", value.Outage.TotalSeconds);
            }
            else
            {
                _logger.LogWarning("İnternet bağlantısı koptu.");
            }

            try
            {
                Changed?.Invoke(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Bağlantı değişikliği dinleyicisi hata verdi");
            }
        }
    }
}
