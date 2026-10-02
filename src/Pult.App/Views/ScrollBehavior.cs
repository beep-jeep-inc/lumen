using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Pult.App.Views;

/// Присоединённое свойство: когда число (токен) меняется, ScrollViewer прокручивается к началу.
/// Раздел увеличивает токен после каждой проверки/поиска — страница после перестройки списка не остаётся на середине.
public static class ScrollBehavior
{
    public static readonly DependencyProperty ResetTokenProperty = DependencyProperty.RegisterAttached(
        "ResetToken", typeof(int), typeof(ScrollBehavior), new PropertyMetadata(0, OnChanged));

    public static int GetResetToken(DependencyObject o) => (int)o.GetValue(ResetTokenProperty);
    public static void SetResetToken(DependencyObject o, int v) => o.SetValue(ResetTokenProperty, v);

    /// Когда число меняется, страница прокручивается к строке, на которую указывает ScrollTarget раздела (DataContext ScrollViewer).
    public static readonly DependencyProperty TargetTokenProperty = DependencyProperty.RegisterAttached(
        "TargetToken", typeof(int), typeof(ScrollBehavior), new PropertyMetadata(0, OnTargetChanged));

    public static int GetTargetToken(DependencyObject o) => (int)o.GetValue(TargetTokenProperty);
    public static void SetTargetToken(DependencyObject o, int v) => o.SetValue(TargetTokenProperty, v);

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv || (int)e.NewValue == (int)e.OldValue) return;
        sv.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var target = (sv.DataContext as Pult.App.ViewModels.SectionVm)?.ScrollTarget;
            var el = target == null ? null : Find(sv, target);
            if (el == null) { sv.ScrollToTop(); return; }
            sv.UpdateLayout();
            var y = el.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
            sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + y - 8));
        }));
    }

    private static FrameworkElement? Find(DependencyObject root, object target)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is ContentPresenter cp && ReferenceEquals(cp.Content, target)) return cp;
            if (Find(c, target) is { } f) return f;
        }
        return null;
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv || (int)e.NewValue == (int)e.OldValue) return;
        // после того как список перестроен и размеры посчитаны
        sv.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => sv.ScrollToTop()));
    }
}
