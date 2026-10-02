using System.Threading;
using System.Windows;
using Pult.App.Services;
using Pult.App.Views;
using Pult.Core.Admin;

namespace Pult.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    /// Режим --preview: одно окно для разработчика, без замка одного экземпляра.
    public static bool Preview { get; private set; }
    internal static void UsePreviewServices(AppServices s) => Services = s;
    /// Предупреждение, которое главное окно покажет баннером при открытии (пульт лежит не там, запущен от администратора).
    public static string StartupBanner { get; private set; } = "";
    private static Mutex? _single;
    private static EventWaitHandle? _showSignal;
    private static MainWindow? _main;
    private static volatile bool _quitting;

    private static void HookGlobalErrors()
    {
        // Все три «последних рубежа» пишут в журнал полную запись: контекст (раздел/действие), тип, сообщение, InnerException и стек.
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Log.Error(a.IsTerminating ? "фатальная ошибка (AppDomain), Lumen закрывается" : "необработанная ошибка (AppDomain)", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString() ?? "неизвестно"));
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("необработанная ошибка фоновой задачи", a.Exception); a.SetObserved(); };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        HookGlobalErrors();
        DispatcherUnhandledException += (_, a) => { Log.Error("необработанная ошибка (интерфейс)", a.Exception); a.Handled = true; };
        // Аппаратная отрисовка WPF подгружает драйвер видеокарты (у Intel это ~150 МБ приватной памяти) ради пары полупрозрачных окон. Замер: 230 МБ → 75 МБ. PULT_HW=1 возвращает GPU.
        if (Environment.GetEnvironmentVariable("PULT_HW") != "1") System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (e.Args.Contains("--preview")) { Preview = true; PreviewMode.Run(e.Args); return; }
        // Запущен с повышенными правами (галочка «Запустить» в установщике, «от имени администратора») — всё настроилось бы не на того пользователя.
        // Перезапускаемся обычным токеном через проводник (один раз) и выходим.
        if (TryRelaunchUnelevated()) { Shutdown(); return; }
        if (StartupChecks.IsInHelperFolder(Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))) StartupBanner = Strings.WrongInstallFolder;
        _single = new Mutex(true, @"Local\Pult-single", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Pult-show");
        if (!first) { _showSignal.Set(); Shutdown(); return; }                 // уже запущен — просто показать окно
        try { Init(e); }
        catch (Exception ex)
        {
            Log.Error("запуск не удался", ex);
            try { MessageBox.Show($"Lumen не запустился: {ex.Message}. Подробности — %LOCALAPPDATA%\\Pult\\pult.log", Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            try { _single?.ReleaseMutex(); } catch { }
            _single?.Dispose(); _single = null;
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _quitting = true;
        try { _showSignal?.Set(); } catch { }                                  // отпустить поток ожидания сигнала
        base.OnExit(e);
    }

    /// true — запустили копию без повышения, эту закрываем. Не вышло или уже пробовали — остаёмся и предупреждаем баннером.
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetShellWindow();

    private static bool TryRelaunchUnelevated()
    {
        try
        {
            if (!AdminRights.IsProcessElevatedByUac()) return false;
            var stamp = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pult", "relaunch.stamp");
            DateTime? last = null;
            try { if (System.IO.File.Exists(stamp)) last = System.IO.File.GetLastWriteTimeUtc(stamp); } catch (System.IO.IOException) { }
            if (!StartupChecks.ShouldRelaunchUnelevated(true, last, DateTime.UtcNow)) { StartupBanner = Strings.RunningElevated; return false; }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stamp)!);
            System.IO.File.WriteAllText(stamp, DateTime.UtcNow.ToString("O"));
            // Без запущенной оболочки рабочего стола explorer.exe из повышенного процесса сам стал бы оболочкой с полными правами — не перезапускаемся.
            if (!StartupChecks.DesktopShellRunning(GetShellWindow())) { StartupBanner = Strings.RunningElevated; return false; }
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) { StartupBanner = Strings.RunningElevated; return false; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + exe + "\"") { UseShellExecute = false })?.Dispose();
            Log.Info("Lumen был запущен с повышением — перезапущен обычным токеном через проводник");
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("перезапуск без повышения не вышел", ex);
            StartupBanner = Strings.RunningElevated;
            return false;
        }
    }

    private void Init(StartupEventArgs e)
    {
        new Thread(() => { while (_showSignal!.WaitOne()) { if (_quitting) break; try { Dispatcher.BeginInvoke(ShowMain); } catch { } } }) { IsBackground = true }.Start();
        Services = AppServices.Create();
        if (Services.Store.LoadWarning is { } w) Log.Info(w);
        // Автозапуск строго по галочке (по умолчанию выключен): выключенная снимает и старый ключ Run «Pult --tray» прежних версий.
        try { if (Services.Config.StartWithWindows) Autostart.Ensure(); else Autostart.Set(false); } catch (Exception ex) { Log.Error("автозапуск", ex); }

        _main = new MainWindow();
        if (Autostart.IsMinimizedStart(e.Args) && Services.Store.LoadWarning == null) { _main.WindowState = WindowState.Minimized; _main.Show(); }
        else ShowMain();
        Log.Info("Lumen запущен");
    }

    public static void ShowMain()
    {
        if (_main == null) return;
        _main.Show(); if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }
}
