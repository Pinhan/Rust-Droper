using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RustDroper.App.Infrastructure;

/// <summary>Anlık bellek/kaynak ölçümü. <see cref="PrivateBytes"/> = ayrılmış (commit) özel bellek; çalışma kümesi kırpılsa da düşmez.</summary>
public sealed record MemoryReport(long PrivateBytes, long WorkingSet, long ManagedHeap, long ManagedCommitted, int Handles, int Threads)
{
    private const double MB = 1024 * 1024;

    public static MemoryReport Capture()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new MemoryReport(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            gc.TotalCommittedBytes,
            process.HandleCount,
            process.Threads.Count);
    }

    public override string ToString() =>
        $"özel={PrivateBytes / MB:0.0} MB, çalışma kümesi={WorkingSet / MB:0.0} MB, yönetilen yığın={ManagedHeap / MB:0.0} MB (ayrılan {ManagedCommitted / MB:0.0} MB), tutamaç={Handles}, iş parçacığı={Threads}";
}

internal static class MemoryTrimmer
{
    /// <summary>Pencere yok edildikten sonra yönetilen yığını toplar ve çalışma kümesini kırpar (tepsideyken düşük RAM).</summary>
    public static void Trim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        NativeMethods.EmptyWorkingSet(process.Handle);
    }
}

/// <summary>
/// Uzun süre tepside çalışırken belleği sınırlı tutar ve eğilimi günlüğe yazar: her <paramref name="interval"/>'de (pencere kapalıysa)
/// kırpma yapar, ardından <c>bellek:</c> satırı yazar. Sızıntı/şişme olursa günlükten (örn. gece boyu) izlenebilir.
/// </summary>
internal sealed class MemoryMonitor : IDisposable
{
    private readonly Func<bool> _isWindowOpen;
    private readonly ILogger _logger;
    private readonly Timer _timer;

    public MemoryMonitor(Func<bool> isWindowOpen, TimeSpan interval, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(isWindowOpen);
        _isWindowOpen = isWindowOpen;
        _logger = logger ?? NullLogger.Instance;
        var first = interval < TimeSpan.FromMinutes(1) ? interval : TimeSpan.FromMinutes(1); // ilk satır açılıştan 1 dk sonra
        _timer = new Timer(_ => Tick(), null, first, interval);
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>Tek bir periyot: pencere kapalıysa kırp, sonra ölç ve günlüğe yaz. Hata fırlatmaz.</summary>
    internal void Tick()
    {
        try
        {
            var open = _isWindowOpen();
            if (!open)
            {
                MemoryTrimmer.Trim();
            }

            _logger.LogInformation("bellek ({Mode}): {Report}", open ? "pencere açık" : "tepside", MemoryReport.Capture());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Bellek ölçümü başarısız");
        }
    }
}
