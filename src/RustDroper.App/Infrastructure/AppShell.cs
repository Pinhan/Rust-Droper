using System.Drawing;
using System.Windows;
using RustDroper.App.ViewModels;
using RustDroper.Core.Localization;
using RustDroper.Core.Orchestration;

namespace RustDroper.App.Infrastructure;

/// <summary>Tepsi simgesi + pencere denetleyicisi + görünüm modeli arasındaki yapıştırıcı (iş mantığı içermez).</summary>
internal sealed class AppShell : IDisposable, INotifier
{
    private const int CmdToggleWindow = 1;
    private const int CmdStartPause = 2;
    private const int CmdExit = 9;

    private readonly MainViewModel _viewModel;
    private readonly WindowController _windows;
    private readonly TrayIcon _tray;
    private UserNotification? _lastNotification;

    public AppShell(MainViewModel viewModel, WindowController windows)
    {
        _viewModel = viewModel;
        _windows = windows;
        _tray = new TrayIcon(LoadTrayIcon(), viewModel.TrayTooltip) { MenuProvider = BuildMenu };
        _tray.LeftClicked += _windows.Toggle;
        _tray.MenuCommand += OnMenuCommand;
        _tray.BalloonClicked += () =>
        {
            if (_lastNotification is { } clicked)
            {
                NotificationClicked?.Invoke(clicked);
            }
        };
        _windows.OpenChanged += open => _viewModel.IsWindowVisible = open;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TrayTooltip))
            {
                _tray.Tooltip = _viewModel.TrayTooltip;
            }
        };
    }

    /// <summary>Tepsi menüsünden "Çıkış" seçildi.</summary>
    public event Action? ExitRequested;

    /// <summary>Kullanıcı bir bildirim balonuna tıkladı (bağlantısı varsa açılır, yoksa pencere).</summary>
    public event Action<UserNotification>? NotificationClicked;

    public bool TrayAdded => _tray.IsAdded;

    /// <summary>Tepsi balonu gösterir. Pencere kapalıyken de çalışır; simge yoksa sessizce vazgeçer.</summary>
    public void Show(UserNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _lastNotification = notification;
        var level = notification.Kind is NotificationKind.ClaimFailed or NotificationKind.ClaimReady or NotificationKind.LoginRequired or NotificationKind.ConnectionLost
            ? BalloonLevel.Warning
            : BalloonLevel.Info;
        _tray.ShowBalloon(notification.Title, notification.Text, level);
    }

    public void ShowWindow() => _windows.Show();

    public void Dispose()
    {
        _windows.Close();
        _tray.Dispose();
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new TrayMenuItem(CmdToggleWindow, Loc.T(_windows.IsOpen ? "tray.hide" : "tray.show")),
        new TrayMenuItem(CmdStartPause, _viewModel.StartPauseLabel, _viewModel.CanStartPause),
        TrayMenuItem.Separator,
        new TrayMenuItem(CmdExit, Loc.T("tray.exit")),
    ];

    private void OnMenuCommand(int command)
    {
        switch (command)
        {
            case CmdToggleWindow:
                _windows.Toggle();
                break;
            case CmdStartPause:
                _viewModel.StartPauseCommand.Execute(null);
                break;
            case CmdExit:
                ExitRequested?.Invoke();
                break;
        }
    }

    private static Icon LoadTrayIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info.Stream;
        return new Icon(stream, SystemInformation16());
    }

    // Tepsi simgesi boyutu: 16 px @100% DPI; yüksek DPI'da Windows ölçekler, daha keskin kare için 32 px çerçeveyi de içerir.
    private static System.Drawing.Size SystemInformation16() => new(16, 16);
}
