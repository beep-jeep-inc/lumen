namespace Pult.Core.Cleanup;

public sealed record FolderSize(string Path, long Bytes, int Depth);
public sealed record FileSize(string Path, long Bytes);
public sealed record OldFile(string Path, long Bytes, DateTime LastUtc);

/// «Что занимает место»: только чтение. Ссылки (junction/symlink) не обходятся и не считаются; папки без доступа пропускаются.
public static class DiskMap
{
    public static readonly string[] InstallerExts = { ".exe", ".msi", ".zip", ".rar", ".7z", ".iso" };
    private static bool IsLink(FileSystemInfo i) => (i.Attributes & FileAttributes.ReparsePoint) != 0;

    /// Самые большие папки до глубины depth (1 = прямые подпапки root), по убыванию размера.
    public static IReadOnlyList<FolderSize> TopFolders(string root, int depth, int top, CancellationToken ct, IProgress<string>? progress = null, Func<FileSystemInfo, FileAttributes>? attrOf = null, ScanCounter? counter = null)
    {
        var all = new List<FolderSize>();
        long Size(string dir, int d)
        {
            ct.ThrowIfCancellationRequested();
            if (d == 1 && d <= depth) progress?.Report(dir);
            long total = 0;
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
            foreach (var e in entries)
            {
                try
                {
                    if (IsLink(e)) continue;
                    if ((e.Attributes & FileAttributes.Directory) != 0) total += Size(e.FullName, d + 1);
                    else if (!CloudFiles.IsCloudOnly(e, attrOf)) { var len = ((FileInfo)e).Length; total += len; counter?.File(len); }     // «только в облаке» места на диске не занимает
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (d >= 1 && d <= depth) all.Add(new FolderSize(dir, total, d));
            return total;
        }
        try
        {
            var full = SafeDelete.Norm(root);
            var di = new DirectoryInfo(full);
            if (di.Exists && !IsLink(di)) Size(full, 0);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        return all.OrderByDescending(f => f.Bytes).Take(top).ToList();
    }

    /// Самые большие файлы (не меньше minBytes) внутри root.
    public static IReadOnlyList<FileSize> TopFiles(string root, int top, long minBytes, CancellationToken ct = default, Func<string, bool>? skipDir = null, Func<FileSystemInfo, FileAttributes>? attrOf = null, ScanCounter? counter = null)
    {
        var list = new List<FileSize>();
        foreach (var f in Files(root, ct, skipDir, attrOf)) { counter?.File(f.Length); if (f.Length >= minBytes) list.Add(new FileSize(f.FullName, f.Length)); }
        return list.OrderByDescending(f => f.Bytes).Take(top).ToList();
    }

    /// Все обычные файлы под root без ссылок (и без заходов в них). Файлы «только в облаке» (OneDrive) пропускаются: читать их нельзя, они не занимают место.
    public static IEnumerable<FileInfo> Files(string root, CancellationToken ct = default, Func<string, bool>? skipDir = null, Func<FileSystemInfo, FileAttributes>? attrOf = null)
    {
        var stack = new Stack<string>();
        try
        {
            var full = SafeDelete.Norm(root);
            var di = new DirectoryInfo(full);
            if (!di.Exists || IsLink(di)) yield break;
            stack.Push(full);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { yield break; }
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                bool link, isDir;
                try { link = IsLink(e); isDir = (e.Attributes & FileAttributes.Directory) != 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (link) continue;
                if (isDir) { if (skipDir == null || !skipDir(e.FullName)) stack.Push(e.FullName); }
                else if (e is FileInfo fi && !CloudFiles.IsCloudOnly(fi, attrOf)) yield return fi;
            }
        }
    }

    /// Старые установщики и архивы в «Загрузках» (только сама папка, не подпапки): .exe .msi .zip .rar .7z .iso старше ageDays.
    public static IReadOnlyList<OldFile> OldDownloads(string downloads, int ageDays = 30, DateTime? nowUtc = null, Func<FileSystemInfo, FileAttributes>? attrOf = null)
    {
        var now = nowUtc ?? DateTime.UtcNow; var res = new List<OldFile>();
        try
        {
            var di = new DirectoryInfo(SafeDelete.Norm(downloads));
            if (!di.Exists || IsLink(di)) return res;
            foreach (var f in di.EnumerateFiles())
            {
                try
                {
                    if (IsLink(f) || !InstallerExts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) || CloudFiles.IsCloudOnly(f, attrOf)) continue;
                    var last = f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc;   // скачан недавно = свежий, даже если дата файла старая
                    if (now - last >= TimeSpan.FromDays(ageDays)) res.Add(new OldFile(f.FullName, f.Length, last));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        return res.OrderByDescending(f => f.Bytes).ToList();
    }
}
