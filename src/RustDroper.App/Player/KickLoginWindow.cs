using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RustDroper.Core.Localization;

namespace RustDroper.App.Player;

/// <summary>
/// Görünür Kick giriş penceresi (oynatıcı profiliyle aynı WebView2 profili). Girişi <b>yalnızca kullanıcı</b> yapar;
/// uygulama hiçbir alana yazmaz, hiçbir kimlik bilgisini okumaz/saklamaz. Pencere kapanınca oturum durumu yenilenir.
/// </summary>
internal sealed class KickLoginWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly string _url;

    public KickLoginWindow(string url)
    {
        _url = url;
        Title = Loc.T("kickLogin.title");
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16));

        var note = new TextBlock
        {
            Text = Loc.T("kickLogin.note"),
            Foreground = Brushes.Gainsboro,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 8, 12, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var done = new Button { Content = Loc.T("kickLogin.done"), Margin = new Thickness(8), Padding = new Thickness(14, 4, 14, 4) };
        done.Click += (_, _) => Close();
        var header = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0x1f, 0x1f, 0x23)) };
        DockPanel.SetDock(done, Dock.Right);
        header.Children.Add(done);
        header.Children.Add(note);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_web, 1);
        grid.Children.Add(header);
        grid.Children.Add(_web);
        Content = grid;
    }

    public async Task InitializeAsync(CoreWebView2Environment environment)
    {
        await _web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        _web.CoreWebView2.IsMuted = true;
        _web.CoreWebView2.NavigationCompleted += (_, _) => _web.CoreWebView2.IsMuted = true;
        _web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        _web.CoreWebView2.Navigate(_url);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _web.Dispose();
        }
        catch (InvalidOperationException)
        {
        }

        base.OnClosed(e);
    }
}
