using System.Windows.Controls;

namespace RustDroper.App.Views;

/// <summary>Claim edilmiş ödül kartı (DataContext = ClaimedItem): görsel, ad, kaynak, claim tarihi. Yalnızca görünüm; hover'da kısa yakınlaştırma.</summary>
public partial class ClaimedCard : UserControl
{
    public ClaimedCard() => InitializeComponent();
}
