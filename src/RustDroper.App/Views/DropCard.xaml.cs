using System.Windows.Controls;

namespace RustDroper.App.Views;

/// <summary>
/// Drop kartı (DataContext = DropRow). Yalnızca görünüm: görsel JPG/PNG'dir; fareyle üzerine gelince kısa bir yakınlaştırma animasyonu çalışır.
/// Not: kartta MP4 oynatma YOK — MediaElement ölçümde kart başına ~100 MB özel bellek ve ~%20 CPU tuttu (docs/05 §7).
/// </summary>
public partial class DropCard : UserControl
{
    public DropCard() => InitializeComponent();
}
