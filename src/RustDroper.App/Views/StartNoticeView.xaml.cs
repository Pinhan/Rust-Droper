using System.Windows.Controls;

namespace RustDroper.App.Views;

/// <summary>Başlat uyarısı içeriği: uyarı metni + "Bir daha gösterme" + Tamam. Pencere (<see cref="StartNoticeWindow"/>) ve önizleme aynı içeriği kullanır.</summary>
public partial class StartNoticeView : UserControl
{
    public StartNoticeView()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => OkClicked?.Invoke();
    }

    public event Action? OkClicked;

    public bool DontShowAgainChecked
    {
        get => DontShowAgain.IsChecked == true;
        set => DontShowAgain.IsChecked = value;
    }
}
