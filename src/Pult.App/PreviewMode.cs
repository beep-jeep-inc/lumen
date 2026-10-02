using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pult.App.Services;
using Pult.App.Views;

namespace Pult.App;

/// Служебный режим для разработчика: `Pult.exe --preview maintenance|maintenance-demo [--shot файл.png] [--size ШxВ] [--section overview|drivers|libraries|programs|cleanup|space]`.
/// maintenance — настоящие данные (только чтение и сканы, ничего не ставится и не удаляется), maintenance-demo — вымышленные.
/// Открывает одно окно без замка «один экземпляр»; ничего не сохраняет. С --shot рисует окно в PNG изнутри (не зависит от фокуса) и закрывается.
internal static class PreviewMode
{
    private static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    public static async void Run(string[] args)
    {
        try
        {
            var which = Arg(args, "--preview") ?? "maintenance";
            var shot = Arg(args, "--shot");
            App.UsePreviewServices(AppServices.Create());

            var mw = new MainWindow();
            var mvm = mw.Maintenance;
            // --state: unchecked (ничего не загружено), checking («Проверить всё» идёт: автозагрузка готова, диски проверяются), done (демо-данные и итог быстрой проверки),
            // quick-real (настоящая быстрая проверка, только чтение; итог пишется в файл рядом со снимком)
            var state = Arg(args, "--state") ?? "done";
            if (state != "unchecked" && state != "checking") await mvm.OpenedAsync();
            if (state == "quick-real") { await mvm.QuickAsync(); if (shot != null) File.WriteAllText(Path.ChangeExtension(shot, ".quick.txt"), mvm.QuickSummary); }
            else if (which == "maintenance") await LoadRealMaintenance(mvm);
            else if (state == "done") { mvm.LoadDemo(); await mvm.PreviewQuick(-1); }
            else if (state == "checking") { mvm.Startup.LoadDemo(); await mvm.PreviewQuick(1); }
            if (Arg(args, "--section") is { } sec) mvm.SelectByName(sec);
            Window w = mw;

            if (Arg(args, "--size") is { } sz && sz.Split('x') is [var sw, var sh] && double.TryParse(sw, out var wd) && double.TryParse(sh, out var ht))
            { w.Width = wd; w.Height = ht; w.SizeToContent = SizeToContent.Manual; }
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Show();
            if (args.Contains("--maximized")) w.WindowState = WindowState.Maximized;   // проверка окна вопроса над развёрнутым окном
            w.Activate();

            if (shot == null) return;                                           // просто смотрим глазами
            await Task.Delay(900);
            if (Arg(args, "--dialog-live") is { } live)
            {
                // Проверка настоящего окна вопроса: через 1,5 с смотрим, где фокус, нажимаем Enter и пишем ответ в файл рядом со снимком (ничего не выполняется)
                ConfirmDialog.ForceScrim = true;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                string report = "";
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var d = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x != w && x.Content is Border && x.WindowStyle == WindowStyle.None && x.IsActive);
                    report = "windows=" + Application.Current.Windows.Count + " scrim=" + Application.Current.Windows.OfType<Window>().Count(x => x.AllowsTransparency && x != d) + " dialog=" + (d != null) + " focus=" + (d != null ? (Keyboard.FocusedElement as Button)?.Content : "-") + " size=" + d?.ActualWidth + "x" + d?.ActualHeight + " pos=" + d?.Left + "," + d?.Top + " main=" + w.PointToScreen(new Point(0, 0)) + " " + ((FrameworkElement)w.Content).ActualWidth + "x" + ((FrameworkElement)w.Content).ActualHeight;
                    if (d != null) Render(d, Path.ChangeExtension(shot, ".dialog.png"));
                    if (Keyboard.FocusedElement is Button b) b.Command?.Execute(null);
                    d?.Dispatcher.BeginInvoke(() => { if (d.IsVisible) { var ev = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(d)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent }; (Keyboard.FocusedElement as UIElement)?.RaiseEvent(ev); } });
                };
                timer.Start();
                var ans = live == "info" ? "info" : ConfirmDialog.Ask("Переместить в Корзину: 2 файла (всего 600 МБ)? Их можно будет вернуть из Корзины.\n\nПереместятся только выбранные файлы.").ToString();
                File.WriteAllText(Path.ChangeExtension(shot, ".live.txt"), report + " answer=" + ans);
                Application.Current.Shutdown();
                return;
            }
            if (Arg(args, "--dialog") is { } dlg)
            {
                RenderWithDialog(w, shot, dlg == "trash" ? string.Format(Strings.TrashConfirm, "2 файла", "600 МБ") + "\n\nПереместятся только выбранные файлы." : dlg);
                Application.Current.Shutdown();
                return;
            }
            await RenderScrolled(w, shot);
            Application.Current.Shutdown();
        }
        catch (Exception e)
        {
            Log.Error("preview", e);
            try { File.WriteAllText(Path.ChangeExtension(Arg(args, "--shot") ?? "preview-error", ".err.txt"), e.ToString()); } catch { }
            Application.Current.Shutdown(2);
        }
    }

    /// Настоящие чтения и сканы без записи: библиотеки, видеокарты, устройства с ошибками, winget, очистка. Ничего не ставится и не удаляется.
    private static async Task LoadRealMaintenance(ViewModels.MaintenanceVm m)
    {
        await m.OpenedAsync();
        async Task Try(string name, Func<Task> f)
        {
            var t = f(); var done = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(120)));
            if (done != t) Log.Info("preview: " + name + " не уложился в 120 секунд");
        }
        await Try("winget", () => m.Programs.RunCheckAsync());
        await Try("очистка", () => m.Cleanup.RunCheckAsync());
        await Try("драйверы", () => m.Drivers.RunCheckAsync());
    }

    /// Страница «Обслуживание» длиннее окна: снимаем по экрану за раз (имя-1.png, имя-2.png…).
    private static async Task RenderScrolled(Window w, string path)
    {
        var sv = FindFirst<ScrollViewer>(((MainWindow)w).PageElement(), _ => true);
        if (sv == null) { Render(w, path); return; }
        w.UpdateLayout();
        for (int i = 0; i < 12; i++)
        {
            sv.ScrollToVerticalOffset(i * (sv.ViewportHeight - 40)); await Task.Delay(300); w.UpdateLayout();
            Render(w, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + $"-{i + 1}.png"));
            if (sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 1) break;
        }
    }

    private static T? FindFirst<T>(DependencyObject root, Func<T, bool> pred) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && pred(t)) return t;
            if (FindFirst(c, pred) is { } r) return r;
        }
        return null;
    }

    private static void Render(Window w, string path)
    {
        w.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(w);
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);           // ActualHeight окна включает заголовок, которого в кадре нет
        int pw = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), ph = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(w);
        Save(rtb, path);
    }


    /// Снимок «окно программы + затемнение + диалог по центру» (макет 06): всё рисуется изнутри, без реального окна диалога.
    private static void RenderWithDialog(Window w, string path, string text)
    {
        w.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(w);
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        int pw = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), ph = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var mainBmp = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        mainBmp.Render(w);
        var card = Views.ConfirmDialog.PreviewCard(text, Math.Min(480, root.ActualWidth - 40));
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); card.Arrange(new Rect(card.DesiredSize)); card.UpdateLayout();
        var cardBmp = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        cardBmp.Render(card);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double W = root.ActualWidth, H = root.ActualHeight;
            dc.DrawImage(mainBmp, new Rect(0, 0, W, H));
            dc.DrawRectangle((Brush)Application.Current.Resources["Scrim"], null, new Rect(0, 0, W, H));
            dc.DrawImage(cardBmp, new Rect((W - card.ActualWidth) / 2, (H - card.ActualHeight) / 2, card.ActualWidth, card.ActualHeight));
        }
        var outBmp = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        outBmp.Render(dv);
        Save(outBmp, path);
    }
    private static void Save(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path); enc.Save(fs);
    }
}
