using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using static RustDroper.App.Infrastructure.NativeMethods;

namespace RustDroper.App.Infrastructure;

internal enum BalloonLevel
{
    Info,
    Warning,
}

internal sealed record TrayMenuItem(int Id, string Text, bool Enabled = true, bool IsSeparator = false)
{
    public static TrayMenuItem Separator { get; } = new(0, string.Empty, true, true);
}

/// <summary>
/// Sistem tepsisi simgesi (Shell_NotifyIcon). Ek bağımlılık yok: mesaj-yalnız pencere (HwndSource) + yerel açılır menü.
/// Sol tık = <see cref="LeftClicked"/>, sağ tık = <see cref="MenuProvider"/>'dan gelen menü, seçim = <see cref="MenuCommand"/>.
/// Explorer yeniden başlarsa ("TaskbarCreated") simge yeniden eklenir. UI iş parçacığında oluşturulmalıdır.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int MaxTooltip = 127;

    private readonly HwndSource _source;
    private readonly Icon _icon;
    private readonly uint _taskbarCreated;
    private long _lastLeftClick;
    private string _tooltip;
    private bool _added;
    private bool _disposed;

    public TrayIcon(Icon icon, string tooltip)
    {
        ArgumentNullException.ThrowIfNull(icon);
        _icon = icon;
        _tooltip = Truncate(tooltip);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _source = new HwndSource(new HwndSourceParameters("RustDroperTray")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);
        Add();
    }

    public event Action? LeftClicked;

    public event Action<int>? MenuCommand;

    /// <summary>Kullanıcı balona tıkladı.</summary>
    public event Action? BalloonClicked;

    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    /// <summary>Mesaj penceresinin tutamağı (tanılama/canlı doğrulama için).</summary>
    public IntPtr WindowHandle => _source.Handle;

    /// <summary>Simge kabuğa (Shell_NotifyIcon NIM_ADD) eklendi mi? Eklenemezse uygulama tepsisiz çalışır, günlüğe yazılır.</summary>
    public bool IsAdded => _added;

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            var text = Truncate(value);
            if (_disposed || text == _tooltip)
            {
                return;
            }

            _tooltip = text;
            Notify(NIM_MODIFY);
        }
    }

    /// <summary>
    /// Tepsi balonu (Windows 10/11'de bildirim olarak görünür). Pencere kapalıyken de çalışır; simge eklenemediyse <c>false</c> döner.
    /// Başlık ≤63, metin ≤255 karakter (Win32 sınırı); fazlası kırpılır.
    /// </summary>
    public bool ShowBalloon(string title, string text, BalloonLevel level)
    {
        if (_disposed || !_added)
        {
            return false;
        }

        var data = CreateData();
        data.uFlags |= NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = level == BalloonLevel.Warning ? NIIF_WARNING : NIIF_INFO;
        return Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
        _icon.Dispose();
    }

    private static string Truncate(string text, int max = MaxTooltip) => text.Length <= max ? text : text[..max];

    private void Add() => _added = Notify(NIM_ADD);

    private bool Notify(int message)
    {
        var data = CreateData();
        return Shell_NotifyIconW(message, ref data);
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = IconId,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = (uint)WM_APP_TRAY,
        hIcon = _icon.Handle,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            handled = true;
            OnTrayMessage((int)(lParam.ToInt64() & 0xFFFF));
        }
        else if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated)
        {
            Add();
        }

        return IntPtr.Zero;
    }

    private void OnTrayMessage(int mouseMessage)
    {
        switch (mouseMessage)
        {
            case WM_LBUTTONUP:
                // Çift tık = iki WM_LBUTTONUP: ikincisini yut ki pencere aç-kapa yapmasın.
                var now = Environment.TickCount64;
                if (now - _lastLeftClick > GetDoubleClickTime())
                {
                    _lastLeftClick = now;
                    LeftClicked?.Invoke();
                }

                break;
            case WM_RBUTTONUP:
                ShowMenu();
                break;
            case NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke();
                break;
        }
    }

    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke();
        if (items is null || items.Count == 0)
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                }
                else
                {
                    AppendMenuW(menu, MF_STRING | (item.Enabled ? 0u : MF_GRAYED), (UIntPtr)(uint)item.Id, item.Text);
                }
            }

            GetCursorPos(out var point);

            // Menü, arka plana tıklayınca kapansın diye pencere ön plana alınmalı (bilinen Win32 tepsi davranışı).
            SetForegroundWindow(_source.Handle);
            var command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD, point.X, point.Y, _source.Handle, IntPtr.Zero);
            PostMessageW(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (command > 0)
            {
                MenuCommand?.Invoke(command);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
