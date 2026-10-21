using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RustDroper.App.ViewModels;

namespace RustDroper.App.Views;

/// <summary>
/// iOS tarzı Twitch ⇄ Kick geçişi. Seçim <see cref="MainViewModel.IsKickSelected"/>'tan gelir; gösterge ve etiket renkleri
/// yalnız seçim değişince kısa bir animasyonla kayar/solar (sürekli çalışan bir şey yok).
/// </summary>
public partial class PlatformToggle : UserControl
{
    private static readonly Color Muted = Color.FromRgb(0x9A, 0xA0, 0xA8);
    private static readonly Color Bright = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private const double SegmentWidth = 134;

    private MainViewModel? _vm;

    public PlatformToggle()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnViewModelChanged;
            }

            _vm = e.NewValue as MainViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnViewModelChanged;
                Apply(_vm.IsKickSelected, animate: false);
            }
        };
        Unloaded += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnViewModelChanged;
                _vm = null;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsKickSelected) && _vm is not null)
        {
            Apply(_vm.IsKickSelected, animate: IsLoaded);
        }
    }

    private void Apply(bool kick, bool animate)
    {
        Slide(ThumbShift, TranslateTransform.XProperty, kick ? SegmentWidth : 0, animate);
        Fade(TwitchLabel, kick ? Muted : Bright, animate);
        Fade(KickLabel, kick ? Bright : Muted, animate);
        TwitchGlyph.Opacity = kick ? 0.6 : 1;
        KickGlyph.Opacity = kick ? 1 : 0.6;
    }

    private static void Slide(Animatable target, DependencyProperty property, double to, bool animate)
    {
        if (!animate)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        var from = (double)target.GetValue(property);
        target.SetValue(property, to);
        target.BeginAnimation(property, new DoubleAnimation(from, to, Motion.Medium) { EasingFunction = Motion.Ease, FillBehavior = FillBehavior.Stop });
    }

    private static void Fade(SolidColorBrush brush, Color to, bool animate)
    {
        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }

        var from = brush.Color;
        brush.Color = to;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, to, Motion.Medium) { FillBehavior = FillBehavior.Stop });
    }
}
