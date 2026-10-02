using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Pult.Core.Admin;

namespace Pult.Admin;

public static partial class Installer
{
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pult");
    public static string InstalledExe => Path.Combine(InstallDir, "PultAdmin.exe");
    /// Устаревшая папка от прежних версий (zapret в составе Пульта): при установке и удалении убираем, если она есть.
    public static string LegacyZapretRoot => Path.Combine(InstallDir, "zapret");
    public static string DataDir => Path.GetDirectoryName(AdminStore.DefaultPath)!;
    public static string ResDir => Path.Combine(DataDir, "res");

    /// Имя exe самого пульта: в папке помощника ему не место (у установщиков по умолчанию {autopf}\Pult — та же папка).
    public const string PultExeName = "Pult.exe";

    /// null — можно ставить. Иначе причина: рядом с будущим PultAdmin.exe лежит Pult.exe (или установка идёт из папки с Pult.exe прямо в неё) —
    /// помощник и пульт должны жить в разных папках, иначе удаление одного снесёт другое.
    public static string? InstallPathProblem(string installDir, string selfExePath, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (fileExists(Path.Combine(installDir, PultExeName)))
            return "В папке помощника (" + installDir + ") лежит сам Pult.exe — ставить помощника в одну папку с Lumen нельзя. Поставь Lumen в другое место (например, %LOCALAPPDATA%\\Programs\\Pult) и повтори.";
        var selfDir = Path.GetDirectoryName(selfExePath);
        if (selfDir != null
            && string.Equals(Path.GetFullPath(selfDir).TrimEnd('\\'), Path.GetFullPath(installDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && fileExists(Path.Combine(selfDir, PultExeName)))
            return "Установка идёт из папки, где лежит Pult.exe, прямо в неё — так нельзя: помощник и Lumen должны быть в разных папках.";
        return null;
    }

    public static int Install(string userId, Action<string>? log = null)
    {
        if (InstallPathProblem(InstallDir, Environment.ProcessPath!) is { } wrong) { Console.Error.WriteLine(wrong); return AdminCodes.ExitWrongFolder; }
        Directory.CreateDirectory(InstallDir);
        if (new PathTrust().DirProblem(InstallDir) is { } bad) { Console.Error.WriteLine(bad); return 6; }
        var self = Environment.ProcessPath!;
        if (!string.Equals(self, InstalledExe, StringComparison.OrdinalIgnoreCase)) File.Copy(self, InstalledExe, overwrite: true);
        SecureDataDir();
        if (!File.Exists(AdminStore.DefaultPath)) new AdminStore().Save(AdminStore.DefaultPath);
        var xml = Path.Combine(InstallDir, "task.xml");                    // не %TEMP%: туда может писать любая программа пользователя
        File.WriteAllText(xml, TaskXml.Build(userId, InstalledExe), System.Text.Encoding.Unicode);
        try
        {
            var rc = RunQuiet(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Create /TN \"{TaskXml.TaskName}\" /XML \"{xml}\" /F");
            if (rc != 0) return rc;
        }
        finally { File.Delete(xml); }
        var sysRc = InstallSystemTask();
        RemoveLegacyZapret(LegacyZapretRoot, log);
        return sysRc;
    }

    /// Второе задание «Pult System»: SYSTEM, тот же PultAdmin.exe с фиксированным аргументом --system-job, без триггеров;
    /// права задания — только SYSTEM и Администраторы (SDDL в XML). Папка jobs — под той же защитой, что и res.
    public static int InstallSystemTask()
    {
        var jobs = JobStore.DefaultDir;
        JobStore.EnsureDir(jobs);
        if (SafeFile.ProtectedDirProblem(jobs) is { } bad) { Console.Error.WriteLine("папка заданий не под защитой: " + bad); return 6; }
        var xml = Path.Combine(InstallDir, "task-system.xml");
        File.WriteAllText(xml, TaskXml.BuildSystem(InstalledExe), System.Text.Encoding.Unicode);
        try { return RunQuiet(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Create /TN \"{TaskXml.SystemTaskName}\" /XML \"{xml}\" /F"); }
        finally { File.Delete(xml); }
    }

    public static int Uninstall(Action<string>? log = null)
    {
        var rc = RunQuiet(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Delete /TN \"{TaskXml.TaskName}\" /F");
        RunQuiet(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Delete /TN \"{TaskXml.SystemTaskName}\" /F");
        if (Directory.Exists(DataDir) && new AdminDataDirPolicy().Problem(DataDir) == null)  // чужую папку не удаляем рекурсивно
            try { Directory.Delete(DataDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        RemoveLegacyZapret(LegacyZapretRoot, log);
        RemoveOwnFiles(InstallDir, DeleteOnReboot);
        return rc;
    }

    /// Удаляет только то, что поставил помощник: PultAdmin.exe, служебные xml . Всё остальное в папке (например, Pult.exe) остаётся;
    /// сама папка убирается, только если после этого пуста. Занятые файлы (сам работающий exe) — через onLocked (при перезагрузке).
    public static void RemoveOwnFiles(string installDir, Action<string> onLocked)
    {
        if (!Directory.Exists(installDir)) return;
        var locked = new List<string>();
        foreach (var name in new[] { "PultAdmin.exe", "task.xml", "task-system.xml" })
        {
            var f = Path.Combine(installDir, name);
            if (!File.Exists(f)) continue;
            try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { locked.Add(f); onLocked(f); }
        }
        try { Directory.Delete(installDir, false); }                          // не рекурсивно: чужие файлы не трогаем; непустая — останется
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // остались только занятые нами файлы — папка тоже уйдёт при перезагрузке; если есть чужое (Pult.exe), её не трогаем
            if (Directory.EnumerateFileSystemEntries(installDir).All(x => locked.Contains(x, StringComparer.OrdinalIgnoreCase))) onLocked(installDir);
        }
    }

    /// Убрать устаревшую папку zapret от прежних версий. Если файлы заняты (запущен winws) — процесс не трогаем: папка остаётся, пишем в журнал.
    /// Службу WinDivert не трогаем. Ссылку (reparse point) убираем, не заходя внутрь.
    public static bool RemoveLegacyZapret(string root, Action<string>? log)
    {
        if (!Directory.Exists(root)) return true;
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) Directory.Delete(root, false);
            else Directory.Delete(root, true);
            log?.Invoke("устаревшая папка " + root + " удалена");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("устаревшая папка " + root + " занята и осталась на месте: " + e.Message);
            return false;
        }
    }

    private static FileAttributes? TryGetAttributes(string p)
    {
        try { return File.GetAttributes(p); } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static int RunQuiet(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
        if (p == null) return -1;
        if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (InvalidOperationException) { } return -1; }
        return p.ExitCode;
    }

    private static void DeleteOnReboot(string dir)
    {
        if (File.Exists(dir)) { MoveFileExW(dir, null, 4); return; }
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) MoveFileExW(f, null, 4);
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length)) MoveFileExW(d, null, 4);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        MoveFileExW(dir, null, 4);                                        // MOVEFILE_DELAY_UNTIL_REBOOT
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existing, string? newName, int flags);
}
