using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RustDroper.App.Infrastructure;
using RustDroper.App.ViewModels;
using RustDroper.App.Views;

namespace RustDroper.App;

public partial class MainWindow : Window
{
    private const double SlideDistance = 24;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private MainViewModel? _vm;
    private Color _accentColor = Motion.AccentFor(kick: false);
    private Stopwatch? _accentClock;
    private Color _accentFrom;
    private Color _accentTo;

    public MainWindow()
    {
        InitializeComponent();
        // Seçili platformun vurgusu: pencere kaynağındaki "PlatformAccentBrush" (ilerleme çubukları, bölüm ikonları, kart şeridi DynamicResource ile izler).
        SetAccent(_accentColor);
        SourceInitialized += (_, _) => UseDarkTitleBar();
        // "12 sn önce" gibi göreli metinler yalnızca pencere açıkken saniyede bir yenilenir.
        _tick.Tick += (_, _) => (DataContext as MainViewModel)?.Dashboard.Tick();
        Loaded += (_, _) => _tick.Start();
        Closed += (_, _) =>
        {
            _tick.Stop();
            StopAccentAnimation();
            Detach();
        };
        DataContextChanged += (_, e) =>
        {
            Detach();
            _vm = e.NewValue as MainViewModel;
            if (_vm is not null)
            {
                StopAccentAnimation();
                SetAccent(Motion.AccentFor(_vm.IsKickSelected));
                _vm.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm = null;
        }
    }

    // Ekran geçişi: veri zaten değişti; içerik yeni konumuna kayarak/solarak gelir, vurgu rengi mor⇄yeşil akar.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null || !IsLoaded)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsKickSelected):
                AnimateAccent(Motion.AccentFor(_vm.IsKickSelected));
                Motion.PlayScreenTransition(ScreenHost, ScreenShift, _vm.IsKickSelected ? SlideDistance : -SlideDistance);
                break;
            case nameof(MainViewModel.ShowSettings):
                Motion.PlayScreenTransition(ScreenHost, ScreenShift, _vm.ShowSettings ? SlideDistance : -SlideDistance);
                break;
        }
    }

    // Paylaşılan fırçayı değiştirmek yerine (şablonlar fırçayı dondurabilir) her karede yeni donmuş fırça konur; DynamicResource tüketicileri izler.
    // Geçiş bitince CompositionTarget.Rendering aboneliği kalkar → boştayken hiçbir şey çalışmaz.
    private void SetAccent(Color color)
    {
        _accentColor = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources["PlatformAccentBrush"] = brush;
    }

    private void AnimateAccent(Color to)
    {
        StopAccentAnimation();
        _accentFrom = _accentColor;
        _accentTo = to;
        _accentClock = Stopwatch.StartNew();
        CompositionTarget.Rendering += OnAccentFrame;
    }

    private void StopAccentAnimation()
    {
        CompositionTarget.Rendering -= OnAccentFrame;
        _accentClock = null;
    }

    private void OnAccentFrame(object? sender, EventArgs e)
    {
        if (_accentClock is null)
        {
            StopAccentAnimation();
            return;
        }

        var t = Math.Min(1.0, _accentClock.Elapsed.TotalMilliseconds / Motion.Medium.TimeSpan.TotalMilliseconds);
        var eased = 1 - Math.Pow(1 - t, 3); // CubicEase out
        SetAccent(Color.FromRgb(
            Lerp(_accentFrom.R, _accentTo.R, eased),
            Lerp(_accentFrom.G, _accentTo.G, eased),
            Lerp(_accentFrom.B, _accentTo.B, eased)));
        if (t >= 1)
        {
            StopAccentAnimation();
        }
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + ((b - a) * t));

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }
}
