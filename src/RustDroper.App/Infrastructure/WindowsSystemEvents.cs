using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.Infrastructure;

/// <summary>
/// Windows güç (uyku/uyanış) ve ağ olaylarını <see cref="ISystemEvents"/>'e çevirir.
/// <see cref="SystemEvents"/> kendi gizli pencere iş parçacığından bildirir; dinleyicilerin hatası burada yutulur (bildirim iş parçacığı çökmesin).
/// </summary>
public sealed class WindowsSystemEvents : ISystemEvents, IDisposable
{
    private readonly ILogger _logger;
    private bool _disposed;

    public WindowsSystemEvents(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public event Action? Suspending;

    public event Action? Resumed;

    public event Action<bool>? NetworkAvailabilityChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Raise(Suspending);
                break;
            case PowerModes.Resume:
                Raise(Resumed);
                break;
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        try
        {
            NetworkAvailabilityChanged?.Invoke(e.IsAvailable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ağ olayı dinleyicisi hata verdi");
        }
    }

    private void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Güç olayı dinleyicisi hata verdi");
        }
    }
}
