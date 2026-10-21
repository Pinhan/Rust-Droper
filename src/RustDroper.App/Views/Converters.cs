using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RustDroper.App.ViewModels;

namespace RustDroper.App.Views;

/// <summary><c>true</c> → Visible (Invert ile tersi, yoksa Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Dolu (boş olmayan metin / null olmayan nesne) → Visible (Invert ile tersi).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var present = value switch
        {
            null => false,
            string s => s.Length > 0,
            _ => true,
        };
        return present != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><see cref="Severity"/> → tema fırçası.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Severity.Ok => "OkBrush",
            Severity.Info => "InfoBrush",
            Severity.Warn => "WarnBrush",
            Severity.Error => "ErrorBrush",
            _ => "MutedBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><see cref="Severity"/> parametre olarak verilen değere ("Info", "Ok"...) eşitse <c>true</c> (durum noktası nabzı için).</summary>
public sealed class SeverityIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Severity severity && parameter is string name && string.Equals(severity.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Kaynak anahtarı ("IconLogIn") → <see cref="Geometry"/> (Themes\Icons.xaml).</summary>
public sealed class GeometryKeyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryFindResource(key) is Geometry geometry ? geometry : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// <see cref="DropState"/> parametre olarak verilen adlardan ("Queued,Waiting") biri mi: <see cref="AsVisibility"/> ile Visible/Collapsed, yoksa bool.
/// </summary>
public sealed class DropStateConverter : IValueConverter
{
    public bool AsVisibility { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var match = value is DropState state && parameter is string names
            && names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(state.ToString(), StringComparer.Ordinal);
        return AsVisibility ? (match ? Visibility.Visible : Visibility.Collapsed) : match;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Yerel görsel dosya yolu → küçültülerek çözülmüş (240 px), dondurulmuş <see cref="System.Windows.Media.Imaging.BitmapImage"/>; dosya yok/bozuksa <c>null</c> (yer tutucu görünür).</summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } path || !System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // dosya tutamacı serbest kalır
            image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 240;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Sayı × parametre (ör. genişlik × 0,6 = yükseklik: görsel alanı oranı).</summary>
public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d && parameter is string p && double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) ? d * factor : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>PNG baytları → <see cref="System.Windows.Media.Imaging.BitmapImage"/> (QR görüntüsü; bellekten, dondurulmuş).</summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = new System.IO.MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
