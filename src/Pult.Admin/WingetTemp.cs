using Pult.Core.Admin;

namespace Pult.Admin;

/// Временные папки для повышенного winget: свои, под защитой помощника (C:\ProgramData\Pult\wtemp\<guid>), а не %LOCALAPPDATA%\Temp пользователя.
public static class WingetTemp
{
    public static string Root => Path.Combine(Installer.DataDir, "wtemp");

    /// Удалить подпапки старше maxAge (остатки прерванных запусков). now — подставное в тестах.
    public static void CleanOld(string root, DateTime nowUtc, TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var di = new DirectoryInfo(d);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { di.Delete(); continue; }   // ссылку убираем, внутрь не заходим
                    if (nowUtc - di.LastWriteTimeUtc > maxAge) di.Delete(true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Запуск winget со своей временной папкой. Папку не под защитой не используем — отказ без запуска.
    /// После завершения процесса папка удаляется; при таймауте процесс ещё работает — не трогаем (уберёт чистка через сутки).
    public static ProcessOutcome RunIn(string root, Func<string, ProcessOutcome> run)
    {
        CleanOld(root, DateTime.UtcNow, TimeSpan.FromDays(1));
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            if (SafeFile.ProtectedDirProblem(dir) is { } why)
            {
                try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return new ProcessOutcome(-1, "", false, "временная папка помощника не под защитой: " + why);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new ProcessOutcome(-1, "", false, "не удалось создать временную папку помощника: " + e.Message); }
        var o = run(dir);
        if (!o.TimedOut) { try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        return o;
    }
}
