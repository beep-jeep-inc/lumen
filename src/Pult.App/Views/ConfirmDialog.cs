using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pult.App.Views;

/// Вопросы и сообщения в теме «Чернила» (вместо белых окон Windows): карточка 480 DIP по центру окна программы, родительское окно затемнено без размытия.
/// Поведение прежнее: «Нет» — основная кнопка, фокус и Enter на ней; Esc и ✕ — отказ; «Да» — контурная и не выбрана по умолчанию.
public static class ConfirmDialog
{
    public static bool Ask(string text) => Show(text, "Да", "Нет", true);
    public static void Info(string text) => Show(text, "Понятно", null, false);

    /// Вопрос с полем ввода. null — «Отмена»; пустая строка — поле очищено.
    public static string? Prompt(string text, string? initial)
    {
        if (Pult.Core.Maintenance.QuietMode.Blocks()) return null;
        var box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 14, 0, 0) };
        var ok = Btn("Сохранить", "PrimaryButton"); ok.IsDefault = true;
        var no = Btn("Отмена", "GhostButton"); no.IsCancel = true; no.Margin = new Thickness(8, 0, 0, 0);
        var (w, _) = Build(Strings.AppName, text, ok, no, box);
        ok.Click += (_, _) => w.DialogResult = true;
        no.Click += (_, _) => w.DialogResult = false;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return ShowModal(w) == true ? box.Text.Trim() : null;
    }

    private static bool Show(string text, string ok, string? cancel, bool defaultNo)
    {
        if (Pult.Core.Maintenance.QuietMode.Blocks()) return false;          // «Проверить всё»: окон не показываем, раздел попросит проверку вручную
        var okBtn = Btn(ok, cancel == null || !defaultNo ? "PrimaryButton" : "GhostButton");
        okBtn.IsDefault = !defaultNo;
        if (cancel == null) okBtn.IsCancel = true;                // у сообщения без полосы заголовка Esc тоже закрывает («Понятно»)
        Button? noBtn = null;
        if (cancel != null) { noBtn = Btn(cancel, "PrimaryButton"); noBtn.IsDefault = true; noBtn.IsCancel = true; noBtn.Margin = new Thickness(8, 0, 0, 0); }
        var (title, body) = Split(text, cancel == null);
        var (w, _) = Build(title, body, okBtn, noBtn, null);
        okBtn.Click += (_, _) => w.DialogResult = true;
        if (noBtn != null) noBtn.Click += (_, _) => w.DialogResult = false;
        var focus = noBtn ?? okBtn;
        w.Loaded += (_, _) => focus.Focus();                       // фокус и Enter — на «Нет» (у сообщения — на «Понятно»)
        return ShowModal(w) == true;
    }

    private static Button Btn(string text, string style) => new()
    {
        Content = text, Style = (Style)Application.Current.Resources[style], MinWidth = 84,
    };

    /// Заголовок и тело из одного текста: короткий первый вопрос («Переместить в Корзину: 2 файла?») становится заголовком, остальное — телом.
    /// Текст не теряется и не повторяется; если короткого вопроса в начале нет — общий заголовок, а текст целиком в теле.
    internal static (string Title, string Body) Split(string text, bool info)
    {
        var t = (text ?? "").Trim();
        if (info) return (Strings.AppName, t);
        var nl = t.IndexOf('\n');
        var first = nl < 0 ? t : t[..nl];
        var q = first.IndexOf('?');
        if (q > 0 && q < 90) return (first[..(q + 1)].Trim(), (first[(q + 1)..] + (nl < 0 ? "" : t[nl..])).Trim());
        return ("Подтвердите действие", t);
    }

    /// Карточка диалога без окна: удобно и для показа, и для превью.
    internal static Border BuildCard(string title, string body, Button primaryFirst, Button? second, UIElement? extra, double width, double maxBodyHeight, out Button close)
    {
        var res = Application.Current.Resources;
        Brush B(string k) => (Brush)res[k];
        var heading = new TextBlock
        {
            Text = title, FontFamily = (FontFamily)res["SerifFont"], FontSize = 24, FontWeight = FontWeights.Bold, Foreground = B("Text"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 28, 0),
        };
        var paras = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var p in body.Replace("\r", "").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            paras.Children.Add(new TextBlock { Text = p.Trim(), FontSize = 13.5, LineHeight = 20, Foreground = B("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (extra != null) paras.Children.Add(extra);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = maxBodyHeight, Content = paras };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(primaryFirst);
        if (second != null) buttons.Children.Add(second);
        close = new Button
        {
            Content = "✕", Style = (Style)res["LinkButton"], Foreground = B("TextDim"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 28, MinHeight = 28, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, -8, -10, 0), IsTabStop = false,
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "Закрыть (отказ)");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(heading); Grid.SetRow(heading, 0);
        grid.Children.Add(close); Grid.SetRow(close, 0);
        grid.Children.Add(scroll); Grid.SetRow(scroll, 1);
        grid.Children.Add(buttons); Grid.SetRow(buttons, 2);
        return new Border
        {
            Width = width, Background = B("SurfaceRaised"), BorderBrush = B("CardBorderHover"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24), Child = grid, SnapsToDevicePixels = true,
        };
    }

    private const double Shadow = 24;
    /// Только для --preview --dialog-live: показать затемнение и в режиме превью.
    internal static bool ForceScrim;

    /// Только для --preview: карточка вопроса «Да/Нет» с тенью, без окна (для снимка поверх затемнённого окна программы).
    internal static FrameworkElement PreviewCard(string text, double width = 480)
    {
        var okBtn = Btn("Да", "GhostButton");
        var noBtn = Btn("Нет", "PrimaryButton"); noBtn.Margin = new Thickness(8, 0, 0, 0);
        var (title, body) = Split(text, false);
        var card = BuildCard(title, body, okBtn, noBtn, null, width, 520, out _);
        card.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.28, BlurRadius = Shadow, ShadowDepth = 8, Direction = 270 };
        return new Border { Padding = new Thickness(Shadow, Shadow - 8, Shadow, Shadow + 8), Child = card };
    }

    private static (Window W, Border Card) Build(string title, string body, Button first, Button? second, UIElement? extra)
    {
        var main = Application.Current?.MainWindow;
        var usable = main is { IsVisible: true } && main.WindowState != WindowState.Minimized;
        var maxW = usable ? main!.ActualWidth - 40 : 480;
        var width = Math.Max(300, Math.Min(480, maxW));
        var maxBody = usable ? Math.Max(120, main!.ActualHeight - 260) : 520;
        var card = BuildCard(title, body, first, second, extra, width, maxBody, out var close);
        card.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.28, BlurRadius = Shadow, ShadowDepth = 8, Direction = 270 };   // лёгкая тень — только у диалога
        var w = new Window
        {
            Title = Strings.AppName, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.Manual, UseLayoutRounding = true,
            FontFamily = (FontFamily)Application.Current!.Resources["UiFont"], Foreground = (Brush)Application.Current!.Resources["Text"],
            Content = new Border { Padding = new Thickness(Shadow, Shadow - 8, Shadow, Shadow + 8), Child = card },
        };
        close.Click += (_, _) => w.DialogResult = false;
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not (TextBox or Button) && e.ButtonState == MouseButtonState.Pressed) { try { w.DragMove(); } catch (InvalidOperationException) { } } };
        // Позиция: по центру окна программы (пока не отрисовано — по центру экрана)
        w.SourceInitialized += (_, _) => PlaceOverMain(w, usable ? main : null);
        return (w, card);
    }

    private static void PlaceOverMain(Window w, Window? main)
    {
        w.UpdateLayout();
        w.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = w.DesiredSize;
        // Не main.Left/Top: у развёрнутого окна они хранят прежнее (не развёрнутое) положение — диалог уехал бы в сторону
        Rect area = (main != null ? ClientRect(main) : null) ?? SystemParameters.WorkArea;
        w.Left = area.Left + (area.Width - size.Width) / 2;
        w.Top = area.Top + (area.Height - size.Height) / 2;
    }

    /// Клиентская область окна программы на экране (в DIP); null — не удалось.
    private static Rect? ClientRect(Window main)
    {
        try
        {
            var src = PresentationSource.FromVisual(main);
            if (src?.CompositionTarget == null || main.Content is not FrameworkElement content) return null;
            var topLeft = src.CompositionTarget.TransformFromDevice.Transform(main.PointToScreen(new Point(0, 0)));
            return new Rect(topLeft.X, topLeft.Y, content.ActualWidth, content.ActualHeight);
        }
        catch (InvalidOperationException) { return null; }
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;

    /// Затемнение окна программы (Scrim 62%, без размытия): прозрачное для мыши окно без рамки поверх клиентской области. Не удалось — просто без затемнения.
    private static Window? ShowScrim()
    {
        try
        {
            var main = Application.Current?.MainWindow;
            if (main is not { IsVisible: true } || main.WindowState == WindowState.Minimized || (App.Preview && !ForceScrim)) return null;
            var src = PresentationSource.FromVisual(main);
            if (src?.CompositionTarget == null) return null;
            var topLeft = src.CompositionTarget.TransformFromDevice.Transform(main.PointToScreen(new Point(0, 0)));
            var s = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Owner = main,
                Background = (Brush)Application.Current!.Resources["Scrim"], Left = topLeft.X, Top = topLeft.Y,
                Width = ((FrameworkElement)main.Content).ActualWidth, Height = ((FrameworkElement)main.Content).ActualHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            s.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(s).Handle;
                SetWindowLong(h, GwlExStyle, GetWindowLong(h, GwlExStyle) | WsExTransparent | WsExNoActivate | WsExToolWindow);   // клики проходят сквозь, фокус не берёт
            };
            s.Show();
            return s;
        }
        catch (Exception e) { Services.Log.Error("затемнение диалога", e); return null; }
    }

    private static bool? ShowModal(Window w)
    {
        var scrim = ShowScrim();
        try { return w.ShowDialog(); }
        finally { try { scrim?.Close(); } catch { } }
    }
}
