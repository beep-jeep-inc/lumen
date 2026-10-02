using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pult.Core.Admin;

namespace Pult.Admin;

public readonly record struct CleanResult(long FreedBytes, int Deleted, int Skipped, string? Problem = null);

/// Очистка системных папок с правами администратора. Пути строит сам помощник (от системных папок, не от пользователя).
/// Windows\Temp обычные программы могут заполнять чем угодно, в том числе junction/symlink на System32 — поэтому обход устроен так:
///  * каждая папка открывается дескриптором БЕЗ права удаления/переименования для других (FILE_SHARE_DELETE не даём) и проверяется
///    по дескриптору: не reparse point, настоящий путь совпадает с ожидаемым. Пока дескриптор открыт, папку нельзя подменить ссылкой;
///    дескрипторы родителей держатся открытыми всю глубину обхода;
///  * ссылки (junction/symlink) внутрь не заходят никогда: удаляется сама ссылка (DeleteFile/RemoveDirectory не идут по ссылке), цель не тронута;
///  * занятый файл / нет прав — пропуск (считаем), исключений наружу нет.
public static partial class SystemCleaner
{
    public static readonly TimeSpan TempMinAge = TimeSpan.FromDays(1);
    public const int MaxDepth = 64;

    /// Удалить содержимое папки root (саму root — не трогаем). minAge — только то, что старше (по LastWriteTime), null — всё.
    public static CleanResult CleanContents(string root, TimeSpan? minAge, DateTime utcNow)
    {
        try
        {
            string full;
            try { full = Path.GetFullPath(root).TrimEnd('\\'); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { return new(0, 0, 0, "плохой путь"); }
            if (!Directory.Exists(full) && !File.Exists(full)) return new(0, 0, 0);                   // папки нет — не ошибка
            using var lk = DirLock.Open(full);
            if (lk == null) return new(0, 0, 1, $"{full}: ссылка или занята — не трогаю");
            var st = new State(minAge, utcNow);
            Walk(full, st, 0);
            return new(st.Freed, st.Deleted, st.Skipped);
        }
        catch (Exception e) { return new(0, 0, 1, e.Message); }                                        // ничего наружу
    }

    public static CleanResult CleanRoots(IEnumerable<string> roots, TimeSpan? minAge, DateTime utcNow)
    {
        long freed = 0; int del = 0, skip = 0; string? problem = null;
        foreach (var r in roots)
        {
            var c = CleanContents(r, minAge, utcNow);
            freed += c.FreedBytes; del += c.Deleted; skip += c.Skipped; problem ??= c.Problem;
        }
        return new(freed, del, skip, problem);
    }

    /// Фиксированные папки для WindowsTemp / WuDownload / Wer. null — для других id.
    public static (string[] Roots, TimeSpan? MinAge)? Plan(string id, string systemDir, string programData)
    {
        var sysRoot = Path.GetDirectoryName(systemDir.TrimEnd('\\'))!;
        return id switch
        {
            AdminMaintenance.WindowsTemp => (new[] { Path.Combine(sysRoot, "Temp") }, TempMinAge),
            AdminMaintenance.WuDownload => (new[] { Path.Combine(sysRoot, "SoftwareDistribution", "Download") }, null),
            AdminMaintenance.Wer => (new[] { Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                                              Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue") }, null),
            _ => null,
        };
    }

    public const string DeclinedMessage = "Ты отказался в окне помощника — очистка не выполнена";
    public const string ComponentWarning = "Очистка хранилища компонентов Windows (DISM /StartComponentCleanup) необратима: "
        + "после неё установленные обновления Windows нельзя будет удалить. Может идти до часа. Выполнить?";

    public static string ConfirmText(string id, long estimatedBytes) => id switch
    {
        AdminMaintenance.WindowsTemp => $"Удалить старые (больше суток) временные файлы Windows (C:\\Windows\\Temp)? Примерно {FormatBytes(estimatedBytes)}. Удаление безвозвратное.",
        AdminMaintenance.WuDownload => $"Удалить скачанные файлы обновлений Windows (SoftwareDistribution\\Download)? Примерно {FormatBytes(estimatedBytes)}. Удаление безвозвратное.",
        AdminMaintenance.Wer => $"Удалить отчёты об ошибках Windows (ProgramData\\Microsoft\\Windows\\WER)? Примерно {FormatBytes(estimatedBytes)}. Удаление безвозвратное.",
        _ => ComponentWarning,
    };

    /// Сколько байт удалится (только чтение, по ссылкам не ходит). Для текста подтверждения.
    public static long Estimate(IEnumerable<string> roots, TimeSpan? minAge, DateTime utcNow)
    {
        long total = 0;
        void Sum(string dir, int depth)
        {
            if (depth > MaxDepth) return;
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    try
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((e.Attributes & FileAttributes.Directory) != 0) Sum(e.FullName, depth + 1);
                        else if (minAge == null || utcNow - e.LastWriteTimeUtc >= minAge.Value) total += ((FileInfo)e).Length;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
        foreach (var r in roots) { try { var f = Path.GetFullPath(r); if ((new DirectoryInfo(f).Attributes & FileAttributes.ReparsePoint) == 0) Sum(f, 0); } catch (Exception) { } }
        return total;
    }

    public static MaintenanceResult Run(string id, string systemDir, string programData, DateTime utcNow, IDismRunner dism, Func<string, bool> confirm)
    {
        if (id == AdminMaintenance.ComponentCleanup)
            return confirm(ConfirmText(id, 0)) ? ComponentCleanup.Run(dism) : new(false, DeclinedMessage, AdminCodes.Declined);
        if (Plan(id, systemDir, programData) is not { } plan) return new(false, "неизвестный вид очистки");
        if (!confirm(ConfirmText(id, Estimate(plan.Roots, plan.MinAge, utcNow)))) return new(false, DeclinedMessage, AdminCodes.Declined);
        var r = CleanRoots(plan.Roots, plan.MinAge, utcNow);
        var text = $"Освобождено {FormatBytes(r.FreedBytes)}" + (r.Skipped > 0 ? $", пропущено {r.Skipped} (заняты или нет доступа)" : "");
        if (r.Problem != null && r.Deleted == 0 && r.Skipped > 0) text += ": " + r.Problem;
        return new(true, text, AdminMaintenance.CleanCode(r.FreedBytes, r.Skipped));
    }

    public static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} ГБ" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} МБ" : b >= 1L << 10 ? $"{b >> 10} КБ" : $"{b} Б";

    private sealed class State(TimeSpan? minAge, DateTime utcNow)
    {
        public long Freed; public int Deleted, Skipped;
        public bool IsOld(DateTime lastWriteUtc) => minAge == null || utcNow - lastWriteUtc >= minAge.Value;
    }

    /// Вызывается, пока dir открыта (DirLock) вызывающим. true — в папке после прохода ничего не осталось.
    private static bool Walk(string dir, State st, int depth)
    {
        List<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { st.Skipped++; return false; }

        var left = 0;
        foreach (var e in entries)
        {
            try
            {
                var attr = e.Attributes;
                var isLink = (attr & FileAttributes.ReparsePoint) != 0;
                var isDir = (attr & FileAttributes.Directory) != 0;
                var written = e.LastWriteTimeUtc;

                if (isLink)                                                     // ссылку не открываем и не обходим: убираем только её саму
                {
                    if (!st.IsOld(written)) { left++; continue; }
                    if (isDir ? RemoveDir(e.FullName) : DeleteOne(e.FullName)) st.Deleted++; else { st.Skipped++; left++; }
                }
                else if (isDir)
                {
                    bool empty;
                    if (depth + 1 > MaxDepth) { st.Skipped++; left++; continue; }
                    using (var sub = DirLock.Open(e.FullName))                  // проверка по дескриптору; до конца обхода папку не подменить
                    {
                        if (sub == null) { st.Skipped++; left++; continue; }
                        empty = Walk(e.FullName, st, depth + 1);
                    }
                    if (empty && st.IsOld(written))                             // возраст — до очистки: удаление содержимого обновляет время папки
                    {
                        if (RemoveDir(e.FullName)) st.Deleted++; else { st.Skipped++; left++; }
                    }
                    else left++;
                }
                else
                {
                    if (!st.IsOld(written)) { left++; continue; }
                    var len = ((FileInfo)e).Length;
                    if (DeleteOne(e.FullName)) { st.Freed += len; st.Deleted++; } else { st.Skipped++; left++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { st.Skipped++; left++; }
        }
        return left == 0;
    }

    private static string Ext(string p) => p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p : @"\\?\" + p;

    private static bool DeleteOne(string path) => DeleteFileW(Ext(path));
    private static bool RemoveDir(string path) => RemoveDirectoryW(Ext(path));

    /// Открытая папка: не ссылка, путь по дескриптору совпадает с ожидаемым, другим нельзя её удалить/переименовать.
    private sealed class DirLock : IDisposable
    {
        private readonly SafeFileHandle _h;
        private DirLock(SafeFileHandle h) { _h = h; }
        public void Dispose() => _h.Dispose();

        public static DirLock? Open(string fullPath)
        {
            const uint listDirectory = 0x1, readAttributes = 0x80, synchronize = 0x100000;
            const uint shareRead = 1, shareWrite = 2, openExisting = 3, backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
            var h = CreateFileW(Ext(fullPath), listDirectory | readAttributes | synchronize, shareRead | shareWrite, 0, openExisting,
                                backupSemantics | openReparsePoint, 0);
            if (h.IsInvalid) { h.Dispose(); return null; }
            try
            {
                if (!SafeFile.GetFileInformationByHandle(h, out var info)) { h.Dispose(); return null; }
                var a = (FileAttributes)info.Attributes;
                if ((a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.Directory) == 0) { h.Dispose(); return null; }
                if (!string.Equals(SafeFile.FinalPath(h), fullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { h.Dispose(); return null; }   // ссылка где-то в пути
                return new DirLock(h);
            }
            catch { h.Dispose(); throw; }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteFileW(string name);

    [LibraryImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveDirectoryW(string name);
}
