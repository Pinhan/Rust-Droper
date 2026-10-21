using System.Windows;
using System.Windows.Threading;

namespace RustDroper.App.Infrastructure;

/// <summary>
/// Ana pencerenin ömrü: kapatılınca pencere YOK EDİLİR (tepsideyken düşük RAM), tepsiden/ikinci örnekten tekrar açılınca
/// yeniden oluşturulur. Görünüm modeli pencereden bağımsız yaşar.
/// </summary>
internal sealed class WindowController(Func<MainWindow> factory)
{
    private MainWindow? _window;

    public bool IsOpen => _window is { IsVisible: true };

    /// <summary>Pencere açıldı (<c>true</c>) ya da yok edildi (<c>false</c>).</summary>
    public event Action<bool>? OpenChanged;

    public void Show()
    {
        if (_window is null)
        {
            var window = factory();
            window.Closed += (_, _) => OnClosed(window);
            _window = window;
            window.Show();
            OpenChanged?.Invoke(true);
        }

        var current = _window;
        if (current.WindowState == WindowState.Minimized)
        {
            current.WindowState = WindowState.Normal;
        }

        // Öne alma kısıtını (foreground lock) aşmak için bilinen Topmost çevirmesi.
        current.Topmost = true;
        current.Topmost = false;
        current.Activate();
    }

    /// <summary>Açık ve önde ise kapatır (tepsiye küçül), değilse açar/öne getirir.</summary>
    public void Toggle()
    {
        if (_window is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            _window.Close();
        }
        else
        {
            Show();
        }
    }

    public void Close() => _window?.Close();

    private void OnClosed(MainWindow window)
    {
        if (!ReferenceEquals(_window, window))
        {
            return;
        }

        _window = null;
        OpenChanged?.Invoke(false);

        // Pencere ağaçları serbest kaldıktan sonra bellek kırp.
        Application.Current?.Dispatcher.BeginInvoke(MemoryTrimmer.Trim, DispatcherPriority.ApplicationIdle);
    }
}
