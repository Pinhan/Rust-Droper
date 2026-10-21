using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RustDroper.Core.Orchestration;

/// <summary>
/// "Sistem uykuya geçmesin" isteği (Windows: <c>SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)</c>).
/// Yalnızca sistemin uykusunu engeller; ekranın kapanmasına izin verir. Sistem ayarlarını DEĞİŞTİRMEZ, süreç bitince kendiliğinden kalkar.
/// </summary>
public interface IKeepAwake
{
    void Hold();

    void Release();
}

/// <summary>
/// Hangi durumda bilgisayarın uyanık tutulacağına karar verir. Uyuyan bilgisayar akış izleyemez → drop ilerlemesi durur.
/// Tutma koşulu: ayar açık VE en az bir motor çalışıyor ve gerçekten iş yapıyor (keşif/izleme/claim bekleme/toparlanma).
/// "Boşta" (uygun kampanya yok) durumunda tutulmaz: kullanıcı uykuya izin verir, uyanınca motor kendini toparlar (M5.2).
/// </summary>
public sealed class KeepAwakeCoordinator
{
    private readonly IKeepAwake _awake;
    private readonly Func<bool> _enabled;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private bool _held;

    public KeepAwakeCoordinator(IKeepAwake awake, Func<bool> enabled, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(awake);
        ArgumentNullException.ThrowIfNull(enabled);
        _awake = awake;
        _enabled = enabled;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsHolding
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    public static bool NeedsAwake(DropperSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.IsRunning
            && snapshot.Engine.State is EngineState.Starting or EngineState.Discovering or EngineState.Watching
                or EngineState.ClaimPending or EngineState.Degraded;
    }

    /// <summary>Servis ya da ayar değişince çağrılır (herhangi bir iş parçacığından).</summary>
    public void Evaluate(params DropperSnapshot?[] snapshots)
    {
        var need = _enabled() && snapshots.Any(s => s is not null && NeedsAwake(s));
        lock (_gate)
        {
            if (need == _held)
            {
                return;
            }

            _held = need;
        }

        if (need)
        {
            _awake.Hold();
            _logger.LogInformation("Uyku engelleme AÇIK (motor çalışıyor).");
        }
        else
        {
            _awake.Release();
            _logger.LogInformation("Uyku engelleme kapalı.");
        }
    }

    /// <summary>Uygulama kapanırken: tutuluyorsa bırak.</summary>
    public void Shutdown()
    {
        bool wasHeld;
        lock (_gate)
        {
            wasHeld = _held;
            _held = false;
        }

        if (wasHeld)
        {
            _awake.Release();
        }
    }
}
