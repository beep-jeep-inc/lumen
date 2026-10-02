using System.IO;
using Pult.Core.Startup;

namespace Pult.App.Services;

/// «Запускать вместе с Windows»: ключ HKCU Run «Pult» с аргументом --minimized (окно свёрнуто на панель задач).
/// Логика реестра и флага StartupApproved — Pult.Core.Startup.AutostartRegistry (там же тесты).
/// Set(false) заодно снимает и старый ключ прежних версий (там был «--tray»).
public static class Autostart
{
    public const string MinimizedArg = "--minimized", LegacyTrayArg = "--tray";
    private static readonly AutostartRegistry Reg = AutostartRegistry.ForThisUser();
    private static string Command => $"\"{Environment.ProcessPath}\" {MinimizedArg}";
    /// Правда для галочки: запись есть и Windows её не выключила.
    public static bool IsOn => Reg.IsOn(Command);
    public static bool IsOnSafe { get { try { return IsOn; } catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; } } }
    public static void Set(bool on) => Reg.Set(on, Command);
    /// Запись должна быть, если галочка включена в настройках; флаг «выключено» от Диспетчера задач не трогаем.
    public static void Ensure() => Reg.EnsureRegistered(Command);
    /// Запуск свёрнутым: новым ключом (--minimized) или старым (--tray, у прежних версий).
    public static bool IsMinimizedStart(IEnumerable<string> args) => args.Any(a => a is MinimizedArg or LegacyTrayArg);
}
