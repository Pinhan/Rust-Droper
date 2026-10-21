using System.Windows;
using RustDroper.App.ViewModels;
using RustDroper.App.Views;

namespace RustDroper.App.Infrastructure;

/// <summary>Başlat uyarısı penceresini (modal) ana pencerenin üstünde gösterir; arayüz iş parçacığında çağrılır.</summary>
internal sealed class WpfStartNoticePrompt : IStartNoticePrompt
{
    public Task<StartNoticeAnswer> ShowAsync()
    {
        var window = new StartNoticeWindow();
        if (Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible) is { } owner)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return Task.FromResult(new StartNoticeAnswer(window.DontShowAgain));
    }
}
