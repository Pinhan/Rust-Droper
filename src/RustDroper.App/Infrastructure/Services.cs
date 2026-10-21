using System.Diagnostics;
using System.Windows.Threading;

namespace RustDroper.App.Infrastructure;

/// <summary>Arka plan iş parçacıklarından arayüz iş parçacığına geçiş (test: anında çalıştıran sahte).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }
}

/// <summary>Dış bağlantıları varsayılan tarayıcıda açar. Yalnızca http(s) kabul edilir.</summary>
public interface IBrowserLauncher
{
    void Open(Uri url);
}

/// <param name="suppress"><c>true</c> ise hiçbir şey açılmaz (<c>--no-browser</c>: geliştirme/sınama).</param>
public sealed class ShellBrowserLauncher(bool suppress = false) : IBrowserLauncher
{
    public void Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (suppress || url.Scheme is not ("http" or "https"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Varsayılan tarayıcı yoksa sessizce vazgeç; kullanıcı bağlantıyı elle açabilir.
        }
    }
}
