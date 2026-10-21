using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace RustDroper.App.Views;

/// <summary>Başlat uyarısı: karanlık temalı, modal; açılırken kısa solma + büyüme animasyonu. Kapatılırsa (Esc/Tamam) motor yine başlar.</summary>
public partial class StartNoticeWindow : Window
{
    public StartNoticeWindow()
    {
        InitializeComponent();
        NoticeView.OkClicked += Close;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                }
            }
        };
        Loaded += (_, _) =>
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(200));
            var ease = Motion.Ease;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            Pop.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = ease });
            Pop.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = ease });
        };
    }

    public bool DontShowAgain => NoticeView.DontShowAgainChecked;
}
