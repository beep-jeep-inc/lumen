using System.Runtime.InteropServices;

namespace Pult.Core.Cleanup;

/// Безопасное удаление. Правила: ссылки (junction/symlink) никогда не обходятся — удаляется сама ссылка, цель цела;
/// всё остаётся внутри корня; read-only снимается; занятые файлы пропускаются; исключений наружу нет.
public static class SafeDelete
{
    public const int MaxDepth = 64;

    private static bool IsLink(FileSystemInfo i) => (i.Attributes & FileAttributes.ReparsePoint) != 0;
    private static bool IsDir(FileSystemInfo i) => (i.Attributes & FileAttributes.Directory) != 0;
    private static bool Exists(string p) => Directory.Exists(p) || File.Exists(p);

    /// Полный путь без хвостового разделителя; корень диска ("C:\", "\\srv\share\") остаётся корнем ("C:" без слэша — это текущая папка диска).
    public static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    /// Корень диска ("C:\", "\\srv\share\") — его ни очищать, ни удалять нельзя. Только по тексту пути, файловую систему не трогает.
    public static bool IsDriveRoot(string path)
    {
        try { var full = Norm(path); return string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
    }

    private static string Dir(string d) => d.EndsWith('\\') ? d : d + "\\";

    /// path лежит строго внутри baseRoot и ни один компонент пути от baseRoot до path не ссылка.
    public static bool IsSafeUnder(string baseRoot, string path)
    {
        try
        {
            var b = Norm(baseRoot); var p = Norm(path);
            var bd = Dir(b);
            if (p.Length <= bd.Length || !p.StartsWith(bd, StringComparison.OrdinalIgnoreCase)) return false;
            var cur = b;
            if (Exists(cur) && IsLink(new DirectoryInfo(cur))) return false;
            foreach (var part in p[bd.Length..].Split('\\'))
            {
                cur = Path.Combine(cur, part);
                if (!Exists(cur)) return true;                       // дальше ничего нет — ссылок там быть не может
                if (IsLink(new DirectoryInfo(cur))) return false;
            }
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// Путь path — сам root или лежит внутри него (только по тексту, без проверки ссылок).
    public static bool IsUnder(string root, string path)
    {
        try
        {
            var r = Norm(root); var p = Norm(path);
            return string.Equals(r, p, StringComparison.OrdinalIgnoreCase) || p.StartsWith(Dir(r), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
    }

    /// Сколько байт и файлов внутри root (корень-ссылка — 0). filter — как в DeleteContents: считаются только подходящие файлы.
    public static (long Bytes, int Files, bool Unreadable) Measure(string root, Func<FileSystemInfo, bool>? filter = null, Func<FileSystemInfo, FileAttributes>? attrOf = null)
    {
        long bytes = 0; int files = 0; var unreadable = false;
        void Walk(string dir, int depth)
        {
            if (depth > MaxDepth) return;
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unreadable = true; return; }
            foreach (var e in entries)
            {
                try
                {
                    if (IsLink(e)) continue;
                    if (IsDir(e)) Walk(e.FullName, depth + 1);
                    else if ((filter == null || filter(e)) && !CloudFiles.IsCloudOnly(e, attrOf)) { bytes += ((FileInfo)e).Length; files++; }   // «только в облаке» не занимает место
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable = true; }
            }
        }
        try
        {
            var full = Norm(root);
            var di = new DirectoryInfo(full);
            if (di.Exists && !IsLink(di)) Walk(full, 0);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { unreadable = true; }
        return (bytes, files, unreadable);
    }

    public static long SizeOf(string root) => Measure(root).Bytes;

    /// Удалить содержимое root (сам root остаётся). filter — какие файлы/ссылки удалять (null — все); пустая папка убирается, если filter её пропускает.
    public static CleanResult DeleteContents(string root, Func<FileSystemInfo, bool>? filter = null)
    {
        try
        {
            string full;
            try { full = Norm(root); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { return new(0, 0, "плохой путь"); }
            if (IsDriveRoot(full)) return new(0, 1, "корень диска не очищаю");   // до любого обращения к диску
            var di = new DirectoryInfo(full);
            if (!di.Exists) return CleanResult.Empty;
            if (IsLink(di)) return new(0, 1, $"{full}: это ссылка — не трогаю");
            long freed = 0; int skipped = 0;
            Walk(full, full, filter, 0, ref freed, ref skipped);
            return new(freed, skipped, null);
        }
        catch (Exception) { return new(0, 1, "ошибка очистки"); }
    }

    /// Удалить папку целиком. Если dir — ссылка, удаляется только она сама.
    public static CleanResult DeleteDirectory(string dir)
    {
        try
        {
            var full = Norm(dir);
            if (IsDriveRoot(full)) return new(0, 1, "корень диска не удаляю");   // до любого обращения к диску
            var di = new DirectoryInfo(full);
            if (!di.Exists) return CleanResult.Empty;
            if (IsLink(di)) return TryRemoveDir(full) ? CleanResult.Empty : new(0, 1, null);
            var inner = DeleteContents(full);
            if (inner.Error != null) return inner;
            return TryRemoveDir(full) ? inner : inner with { Skipped = inner.Skipped + 1 };
        }
        catch (Exception) { return new(0, 1, "ошибка очистки"); }
    }

    private static bool Walk(string root, string dir, Func<FileSystemInfo, bool>? filter, int depth, ref long freed, ref int skipped)
    {
        List<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; return false; }
        var left = 0;
        foreach (var e in entries)
        {
            try
            {
                var full = e.FullName;
                if (!full.StartsWith(Dir(root), StringComparison.OrdinalIgnoreCase)) { skipped++; left++; continue; }   // страховка: только внутри корня
                if (IsLink(e))                                                   // ссылку не открываем: убираем только её саму
                {
                    if (filter != null && !filter(e)) { left++; continue; }
                    if (!(IsDir(e) ? TryRemoveDir(full) : TryDeleteFile(full))) { skipped++; left++; }
                }
                else if (IsDir(e))
                {
                    if (depth + 1 > MaxDepth) { skipped++; left++; continue; }
                    var keep = filter != null && !filter(e);                     // до очистки: удаление содержимого обновляет время папки
                    var empty = Walk(root, full, filter, depth + 1, ref freed, ref skipped);
                    if (empty && !keep) { if (!TryRemoveDir(full)) { skipped++; left++; } }
                    else left++;
                }
                else
                {
                    if (filter != null && !filter(e)) { left++; continue; }
                    var len = ((FileInfo)e).Length;
                    if (TryDeleteFile(full)) freed += len; else { skipped++; left++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; left++; }
        }
        return left == 0;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            File.Delete(path);                                                   // для symlink-файла удаляет саму ссылку
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool TryRemoveDir(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            Directory.Delete(path, false);                                       // не рекурсивно: junction/symlink снимается как ссылка
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// Личные файлы — только в Корзину (можно вернуть), никогда не насовсем. Перед отправкой каждый путь проверяется:
    /// только локальные фиксированные диски, Корзина на диске включена и вместит всё выбранное; иначе путь пропускается с причиной.
    /// within — если задан, путь обязан лежать внутри одной из этих папок (без ссылок по пути).
    public static RecycleResult ToRecycleBin(IEnumerable<string> paths, IRecycleEnv? env = null, IEnumerable<string>? within = null, IEnumerable<string>? exclude = null)
    {
        env ??= new ShellRecycleEnv();
        var skipped = new List<(string, string)>(); var eligible = new List<string>();
        var perDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var roots = within?.ToList();
        var excl = exclude?.ToList();
        foreach (var p in paths)
        {
            try
            {
                if (!Path.IsPathFullyQualified(p)) { skipped.Add((p, "путь не полный")); continue; }
                var f = Norm(p);
                var drive = Path.GetPathRoot(f) ?? "";
                if (f.Length <= 3) { skipped.Add((p, "корень диска не трогаю")); continue; }
                if (!Exists(f)) continue;                                        // уже нет — делать нечего
                if (excl != null && excl.Any(x => IsUnder(x, f))) { skipped.Add((p, "системная папка программ — не трогаю")); continue; }
                if (roots != null && !roots.Any(r => IsSafeUnder(r, f))) { skipped.Add((p, "вне разрешённых папок")); continue; }
                if (IsLink(new DirectoryInfo(f))) { skipped.Add((p, "это ссылка — удали вручную")); continue; }
                if (CloudFiles.IsCloudOnly(env.AttributesOf(f))) { skipped.Add((p, CloudFiles.Reason)); continue; }   // в Корзину уйдёт из облака насовсем и ничего не освободит
                if (env.TypeOf(drive) != DriveType.Fixed) { skipped.Add((p, NotLocal)); continue; }
                var max = env.BinMaxBytes(drive);
                if (max is < 0) { skipped.Add((p, "Корзина на этом диске выключена — удали вручную")); continue; }
                var size = Directory.Exists(f) ? Measure(f).Bytes : new FileInfo(f).Length;
                if (max is { } m && size > m) { skipped.Add((p, "не поместится в Корзину — удали вручную")); continue; }
                perDrive[drive] = perDrive.GetValueOrDefault(drive) + size;
                eligible.Add(f);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            { skipped.Add((p, "нет доступа")); }
        }
        foreach (var (drive, total) in perDrive)                                // всё выбранное на диске вместе тоже должно поместиться
            if (env.BinMaxBytes(drive) is { } m && m >= 0 && total > m)
            {
                foreach (var f in eligible.Where(x => string.Equals(Path.GetPathRoot(x), drive, StringComparison.OrdinalIgnoreCase)).ToList())
                { skipped.Add((f, "всё выбранное не поместится в Корзину — выбери меньше")); eligible.Remove(f); }
            }
        if (eligible.Count == 0) return new RecycleResult(Array.Empty<string>(), skipped);

        try { env.MoveToBin(eligible); }                                          // настоящая Корзина — через Shell; в тестах подставляется
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException) { }
        var moved = new List<string>();
        foreach (var f in eligible)                                              // итог — по факту: нет больше на месте = ушло; отказ/отмена = пропущено
            if (!Exists(f)) moved.Add(f); else skipped.Add((f, "не удалось переместить в Корзину (отмена или файл занят)"));
        return new RecycleResult(moved, skipped);
    }

    public const string NotLocal = "не на локальном диске — удали вручную";

    /// Настоящая отправка в Корзину (SHFileOperation с FOF_ALLOWUNDO). Вызывается только через IRecycleEnv.MoveToBin.
    internal static void ShellMoveToBin(IReadOnlyList<string> eligible)
    {
        var op = new ShFileOp
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', eligible) + "\0\0",
            // FOF_WANTNUKEWARNING частично отменяет FOF_NOCONFIRMATION: если Windows всё же соберётся удалять насовсем, она спросит.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        SHFileOperationW(ref op);
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr hwnd; public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags; [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings; [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOp op);
}

/// Корзина через Shell: размер — SHQueryRecycleBin, очистка — SHEmptyRecycleBin без окон и звука.
public sealed class ShellRecycleBin : IRecycleBin
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Info { public uint cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref Info info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);

    public (long Bytes, long Items) Query()
    {
        try
        {
            var i = new Info { cbSize = (uint)Marshal.SizeOf<Info>() };
            return SHQueryRecycleBinW(null, ref i) == 0 ? (i.i64Size, i.i64NumItems) : (0, 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return (0, 0); }
    }

    public bool Empty()
    {
        try { return SHEmptyRecycleBinW(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) is 0 or unchecked((int)0x8000000A); }   // NOCONFIRMATION|NOPROGRESSUI|NOSOUND; пустая корзина — тоже ок
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}

/// Итог отправки в Корзину: что ушло и что пропущено (путь и причина). Успех — только когда пропущенных нет.
public sealed record RecycleResult(IReadOnlyList<string> Moved, IReadOnlyList<(string Path, string Reason)> Skipped)
{
    public bool AllOk => Skipped.Count == 0;
}

/// Сведения о диске и его Корзине. Подставляется в тестах.
public interface IRecycleEnv
{
    DriveType TypeOf(string driveRoot);
    /// Максимальный размер Корзины диска в байтах; -1 — Корзина выключена («удалять сразу»); null — неизвестно.
    long? BinMaxBytes(string driveRoot);
    /// Отправить в Корзину (Shell). Итог SafeDelete определяет по факту — что больше не лежит на месте. Тесты подставляют свой вариант: настоящий кладёт файлы владельца в Корзину.
    void MoveToBin(IReadOnlyList<string> paths) => SafeDelete.ShellMoveToBin(paths);
    /// Атрибуты файла или папки (0 — не прочитать). Подставляется в тестах, чтобы проверить «только в облаке» без OneDrive.
    FileAttributes AttributesOf(string path)
    {
        try { return File.GetAttributes(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }
}

public sealed class ShellRecycleEnv : IRecycleEnv
{
    public DriveType TypeOf(string driveRoot)
    {
        try { return new DriveInfo(driveRoot).DriveType; } catch (Exception e) when (e is ArgumentException or IOException) { return DriveType.Unknown; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, System.Text.StringBuilder name, int len);

    public long? BinMaxBytes(string driveRoot)
    {
        try
        {
            var sb = new System.Text.StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPointW(driveRoot, sb, sb.Capacity)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(sb.ToString(), @"\{[0-9a-fA-F-]+\}");
            if (!m.Success) return null;
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + m.Value);
            if (k?.GetValue("NukeOnDelete") is int nuke && nuke != 0) return -1;
            if (k?.GetValue("MaxCapacity") is int mb && mb > 0) return mb * 1024L * 1024L;
            var total = new DriveInfo(driveRoot).TotalSize;
            return total / 10;                                                   // по умолчанию Windows даёт Корзине около 10% диска
        }
        catch (Exception e) when (e is IOException or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
