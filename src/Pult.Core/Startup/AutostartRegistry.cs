using Microsoft.Win32;

namespace Pult.Core.Startup;

/// Автозапуск самого Пульта в HKCU: значение Run «Pult» плюс флаг Explorer\StartupApproved\Run.
/// Windows не запускает запись Run, если флаг «выключено» (03…) — его ставит Диспетчер задач. Поэтому:
/// включить = записать Run И убрать флаг; галочка показывает правду: Run есть И не выключен в StartupApproved.
/// Корневые пути параметризованы — тесты работают в HKCU\Software\PultTest\…, а не в настоящем реестре.
public sealed class AutostartRegistry(string runKey, string approvedKey, string name = "Pult")
{
    public static AutostartRegistry ForThisUser() => new(
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");

    /// Запись есть, совпадает с командой и Windows не считает её выключенной.
    public bool IsOn(string command) => IsRegistered(command) && !IsDisabledByWindows();

    public bool IsRegistered(string command)
    {
        using var k = Registry.CurrentUser.OpenSubKey(runKey);
        return k?.GetValue(name) is string v && v == command;
    }

    /// Флаг выключения (Диспетчер задач, «Параметры → Автозагрузка») есть и первый байт нечётный.
    public bool IsDisabledByWindows()
    {
        using var k = Registry.CurrentUser.OpenSubKey(approvedKey);
        return k?.GetValue(name) is byte[] b && !StartupApproval.IsEnabled(b);
    }

    /// on: пишет Run и удаляет флаг (иначе Windows молча не запустит). off: удаляет Run и флаг — мусор не оставляем.
    public void Set(bool on, string command)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(runKey))
        {
            if (on) k.SetValue(name, command); else k.DeleteValue(name, throwOnMissingValue: false);
        }
        using var a = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true);
        a?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// При старте Пульта: галочка включена, а Run пропал или другой путь (переустановка) — переписать Run. Флаг не трогаем:
    /// если человек выключил запись в Диспетчере задач, это его выбор.
    public void EnsureRegistered(string command)
    {
        if (IsRegistered(command)) return;
        using var k = Registry.CurrentUser.CreateSubKey(runKey);
        k.SetValue(name, command);
    }
}
