using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RustDroper.App.Views;

/// <summary>
/// Hafif hareket yardımcıları. Hepsi kısa ömürlüdür (<c>FillBehavior.Stop</c>: bitince animasyon saati kalmaz);
/// sürekli çalışan animasyon yoktur ve pencere kapanınca (yok edilince) hiçbiri kalmaz.
/// </summary>
public static class Motion
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(220));
    public static readonly Duration Medium = new(TimeSpan.FromMilliseconds(260));

    public static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.RegisterAttached(
        "AnimatedValue", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnAnimatedValueChanged));

    public static double GetAnimatedValue(DependencyObject d) => (double)d.GetValue(AnimatedValueProperty);

    public static void SetAnimatedValue(DependencyObject d, double value) => d.SetValue(AnimatedValueProperty, value);

    public static IEasingFunction Ease { get; } = CreateEase();

    /// <summary>Platform vurgu renkleri (Twitch mor, Kick yeşil).</summary>
    public static Color AccentFor(bool kick) => kick ? Color.FromRgb(0x53, 0xFC, 0x18) : Color.FromRgb(0x91, 0x46, 0xFF);

    /// <summary>Hedefe kayma + solma ile ekran geçişi; veri bu çağrıdan ÖNCE değişmiş olmalıdır.</summary>
    public static void PlayScreenTransition(UIElement host, TranslateTransform shift, double fromX)
    {
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, Fast) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
        host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Fast) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
    }

    private static CubicEase CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze(); // paylaşılan örnek: dondurulmuş (iş parçacığı sahipliği yok)
        return ease;
    }

    private static void OnAnimatedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase bar)
        {
            return;
        }

        var to = (double)e.NewValue;
        var from = bar.Value;
        bar.Value = to; // taban değer hedeftir; animasyon bitince (Stop) buna döner
        if (!bar.IsLoaded || Math.Abs(from - to) < 0.05)
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null);
            return;
        }

        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(600)))
        {
            EasingFunction = Ease,
            FillBehavior = FillBehavior.Stop,
        });
    }
}
