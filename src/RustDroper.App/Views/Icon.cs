using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RustDroper.App.Views;

/// <summary>
/// Küçük vektör ikon: <c>Data</c> 24×24 ızgarada bir <see cref="Geometry"/> (Themes\Icons.xaml); renk <c>Foreground</c>'dan gelir.
/// Çizgi tabanlıdır, <see cref="IsFilled"/> ile dolgulu (marka işaretleri) çizilir. Hiçbir zamanlayıcı/animasyon içermez.
/// </summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(Icon), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(2.0));

    static Icon() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
}
