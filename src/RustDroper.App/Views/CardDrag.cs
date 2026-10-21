using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RustDroper.App.ViewModels;

namespace RustDroper.App.Views;

/// <summary>Izgara geometrisi (saf): imleç konumundan hedef hücre, hücre dikdörtgenleri. <see cref="CardDrag"/> ve testler kullanır.</summary>
public static class GridMath
{
    public static int Rows(int count, int columns) => Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, columns)));

    /// <summary>Hücre dikdörtgeni (UniformGrid: tüm hücreler eşit; satır sayısı = ⌈adet / sütun⌉).</summary>
    public static Rect Cell(int index, Size panel, int columns, int count)
    {
        var width = panel.Width / columns;
        var height = panel.Height / Rows(count, columns);
        return new Rect((index % columns) * width, (index / columns) * height, width, height);
    }

    /// <summary>İmleç hangi hücrede (sınır dışı → en yakın geçerli hücre).</summary>
    public static int TargetIndex(Point point, Size panel, int columns, int count)
    {
        if (count <= 0 || panel.Width <= 0 || panel.Height <= 0)
        {
            return 0;
        }

        var column = Math.Clamp((int)(point.X / (panel.Width / columns)), 0, columns - 1);
        var row = Math.Clamp((int)(point.Y / (panel.Height / Rows(count, columns))), 0, Rows(count, columns) - 1);
        return Math.Clamp((row * columns) + column, 0, count - 1);
    }

    /// <summary>Sürüklenen öğe <paramref name="from"/>'dan <paramref name="target"/>'a taşınırsa, <paramref name="index"/>'teki DİĞER öğenin yeni hücresi.</summary>
    public static int SlotAfterMove(int index, int from, int target)
    {
        if (index == from)
        {
            return target;
        }

        if (from < target && index > from && index <= target)
        {
            return index - 1;
        }

        if (from > target && index >= target && index < from)
        {
            return index + 1;
        }

        return index;
    }
}

/// <summary>
/// Izgarada kart sürükle-bırak sıralama (ek paket yok): kart üzerindeki tutamaçtan (<see cref="IsHandleProperty"/>) tutulur, diğer kartlar yeni yerlerine
/// kısa animasyonla kayar, bırakınca <see cref="DashboardViewModel.MoveStreamerCard"/> çağrılır. Olay abonelikleri kalıcıdır ama yalnız sürükleme sırasında
/// iş yapılır (animasyon/zamanlayıcı yok); bırakınca/yakalama kaybolunca her şey sıfırlanır.
/// </summary>
public static class CardDrag
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(CardDrag), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty IsHandleProperty = DependencyProperty.RegisterAttached(
        "IsHandle", typeof(bool), typeof(CardDrag), new PropertyMetadata(false));

    private static readonly DependencyProperty SessionProperty = DependencyProperty.RegisterAttached(
        "Session", typeof(DragSession), typeof(CardDrag));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    public static bool GetIsHandle(DependencyObject d) => (bool)d.GetValue(IsHandleProperty);

    public static void SetIsHandle(DependencyObject d, bool value) => d.SetValue(IsHandleProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items)
        {
            return;
        }

        items.PreviewMouseLeftButtonDown -= OnDown;
        items.PreviewMouseMove -= OnMove;
        items.PreviewMouseLeftButtonUp -= OnUp;
        items.LostMouseCapture -= OnLostCapture;
        if ((bool)e.NewValue)
        {
            items.PreviewMouseLeftButtonDown += OnDown;
            items.PreviewMouseMove += OnMove;
            items.PreviewMouseLeftButtonUp += OnUp;
            items.LostMouseCapture += OnLostCapture;
        }
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var items = (ItemsControl)sender;
        var handle = FindHandle(e.OriginalSource as DependencyObject, items);
        if (handle is null || items.ContainerFromElement(handle) is not { } container)
        {
            return;
        }

        var index = items.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0 || items.Items[index] is not DropRow)
        {
            return;
        }

        var session = new DragSession(items, index, e.GetPosition(items));
        items.SetValue(SessionProperty, session);
        items.CaptureMouse();
        e.Handled = true;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var items = (ItemsControl)sender;
        if (items.GetValue(SessionProperty) is DragSession session && e.LeftButton == MouseButtonState.Pressed)
        {
            session.Move(e.GetPosition(items));
        }
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        var items = (ItemsControl)sender;
        if (items.GetValue(SessionProperty) is DragSession session)
        {
            items.SetValue(SessionProperty, null);
            items.ReleaseMouseCapture();
            session.End(commit: true);
            e.Handled = true;
        }
    }

    private static void OnLostCapture(object sender, MouseEventArgs e)
    {
        var items = (ItemsControl)sender;
        if (items.GetValue(SessionProperty) is DragSession session)
        {
            items.SetValue(SessionProperty, null);
            session.End(commit: false); // yakalama başka yere gitti (ör. pencere etkin değil): iptal
        }
    }

    private static DependencyObject? FindHandle(DependencyObject? start, ItemsControl root)
    {
        for (var current = start; current is not null && !ReferenceEquals(current, root); current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (GetIsHandle(current))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>Tek bir sürükleme oturumu. Herkese açık: test/önizleme sahte fare hareketiyle sürebilir.</summary>
    public sealed class DragSession
    {
        private const int Columns = 5;
        private readonly ItemsControl _items;
        private readonly FrameworkElement[] _containers;
        private readonly Point _start;
        private bool _dragging;

        public DragSession(ItemsControl items, int from, Point mouse)
        {
            _items = items;
            From = from;
            Target = from;
            _start = mouse;
            _containers = [.. Enumerable.Range(0, items.Items.Count).Select(i => items.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement ?? new Border())]; // üretilmemiş kapsayıcı: boş yer tutucu (çökme yok)
        }

        public int From { get; }

        public int Target { get; private set; }

        public bool IsDragging => _dragging;

        public void Move(Point mouse)
        {
            var dx = mouse.X - _start.X;
            var dy = mouse.Y - _start.Y;
            if (!_dragging)
            {
                if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                _dragging = true;
                var dragged = _containers[From];
                Panel.SetZIndex(dragged, 100);
                dragged.Opacity = 0.92;
            }

            Transform(_containers[From]).X = dx;
            Transform(_containers[From]).Y = dy;
            var panel = PanelSize();
            var target = GridMath.TargetIndex(mouse, panel, Columns, _containers.Length);
            if (target == Target)
            {
                return;
            }

            Target = target;
            for (var j = 0; j < _containers.Length; j++)
            {
                if (j == From)
                {
                    continue;
                }

                var slot = GridMath.SlotAfterMove(j, From, Target);
                var from = GridMath.Cell(j, panel, Columns, _containers.Length);
                var to = GridMath.Cell(slot, panel, Columns, _containers.Length);
                Animate(Transform(_containers[j]), to.X - from.X, to.Y - from.Y);
            }
        }

        public void End(bool commit)
        {
            foreach (var container in _containers)
            {
                if (container.RenderTransform is TranslateTransform t)
                {
                    t.BeginAnimation(TranslateTransform.XProperty, null);
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                    t.X = 0;
                    t.Y = 0;
                }

                Panel.SetZIndex(container, 0);
                container.Opacity = 1;
            }

            if (commit && _dragging && Target != From && _items.DataContext is DashboardViewModel dashboard && _items.Items[From] is DropRow row)
            {
                dashboard.MoveStreamerCard(row, Target);
            }

            _dragging = false;
        }

        private Size PanelSize()
        {
            var panel = FindPanel(_items);
            return panel is null ? _items.RenderSize : new Size(panel.ActualWidth, panel.ActualHeight);
        }

        private static Panel? FindPanel(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is UniformGrid grid)
                {
                    return grid;
                }

                if (FindPanel(child) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        private static TranslateTransform Transform(FrameworkElement element)
        {
            if (element.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }

            return transform;
        }

        private static void Animate(TranslateTransform transform, double x, double y)
        {
            var easing = Motion.Ease;
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, new Duration(TimeSpan.FromMilliseconds(150))) { EasingFunction = easing });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, new Duration(TimeSpan.FromMilliseconds(150))) { EasingFunction = easing });
        }
    }
}
