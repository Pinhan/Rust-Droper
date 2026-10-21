using System.Security.Cryptography;
using System.Text;

namespace RustDroper.App.Infrastructure;

/// <summary>
/// Tek örnek (named mutex). İkinci başlatma var olan örneğe "pencereyi göster" sinyali yollar ve çıkar.
/// Anahtar veri klasöründen türetilir: farklı portable kopyalar (farklı <c>data\</c>) birbirini engellemez.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _mutexName;
    private readonly string _showName;
    private readonly ManualResetEventSlim _stop = new(false);
    private Mutex? _mutex;
    private EventWaitHandle? _show;
    private Thread? _listener;

    public SingleInstanceGuard(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _mutexName = $@"Local\RustDroper.{key}";
        _showName = $@"Local\RustDroper.{key}.show";
    }

    public static string KeyFor(string dataDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).TrimEnd('\\').ToLowerInvariant()));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>Bu süreç ilk örnekse <c>true</c> (ve sinyal dinleyicisi hazırlanır).</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, _showName);
        return true;
    }

    /// <summary>Çalışan örneğe "pencereyi göster" der; örnek bulunamazsa <c>false</c>.</summary>
    public bool SignalExisting()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(_showName);
            return handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>İkinci örneklerden gelen sinyalleri arka plan iş parçacığında dinler; <paramref name="onSignal"/> o iş parçacığında çağrılır.</summary>
    public void StartListening(Action onSignal)
    {
        ArgumentNullException.ThrowIfNull(onSignal);
        if (_show is null || _listener is not null)
        {
            return;
        }

        var show = _show;
        _listener = new Thread(() =>
        {
            WaitHandle[] handles = [show, _stop.WaitHandle];
            while (WaitHandle.WaitAny(handles) == 0)
            {
                onSignal();
            }
        })
        {
            IsBackground = true,
            Name = "RustDroper.SingleInstance",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _listener?.Join(TimeSpan.FromSeconds(1));
        _show?.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Başka iş parçacığı edindi: tutamak kapanınca zaten bırakılır.
            }

            _mutex.Dispose();
        }

        _stop.Dispose();
    }
}
