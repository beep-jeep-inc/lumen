namespace Pult.Core.Admin;

/// Проверки при старте пульта: где он лежит и не запущен ли с повышенными правами. Чистая логика — системные значения передаются снаружи.
public static class StartupChecks
{
    /// Папка помощника (C:\Program Files\Pult). Самому пульту там быть не должно: удаление/обновление помощника затронуло бы его.
    public static bool IsInHelperFolder(string? pultExePath, string programFilesDir)
    {
        if (string.IsNullOrWhiteSpace(pultExePath) || string.IsNullOrWhiteSpace(programFilesDir)) return false;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(pultExePath));
            if (dir == null) return false;
            var helper = Path.GetFullPath(Path.Combine(programFilesDir, "Pult")).TrimEnd('\\');
            dir = dir.TrimEnd('\\');
            return string.Equals(dir, helper, StringComparison.OrdinalIgnoreCase)
                   || dir.StartsWith(helper + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// Оболочка рабочего стола (Проводник) запущена: GetShellWindow() вернул не ноль. Иначе explorer.exe стал бы новой оболочкой с правами администратора.
    public static bool DesktopShellRunning(IntPtr shellWindow) => shellWindow != IntPtr.Zero;

    /// Окно, в течение которого повторный перезапуск без повышения не делаем (защита от цикла: не вышло с первого раза — работаем как есть).
    public static readonly TimeSpan RelaunchWindow = TimeSpan.FromSeconds(60);

    /// Перезапускаться без повышения: процесс повышен и за последнюю минуту мы этого ещё не пробовали.
    public static bool ShouldRelaunchUnelevated(bool elevated, DateTime? lastAttemptUtc, DateTime nowUtc) =>
        elevated && (lastAttemptUtc == null || nowUtc - lastAttemptUtc.Value > RelaunchWindow || nowUtc < lastAttemptUtc.Value);
}
