using System.ComponentModel;
using System.Diagnostics;

namespace Pult.Core.Cleanup;

/// Kind — имя папки: node_modules, .venv, venv, bin, obj, __pycache__, .pytest_cache, dist, build, .next.
public sealed record DevJunkDir(string Path, string Kind, long Bytes);

/// Checked — отмечен по умолчанию: есть git, нет незакоммиченного и проект не трогали дольше ageDays. Без git или с изменениями — не отмечен.
public sealed record DevProject(string Path, DateTime LastTouched, bool IsGit, bool GitDirty, IReadOnlyList<DevJunkDir> Junk, bool Checked = false)
{
    public long JunkBytes => Junk.Sum(j => j.Bytes);
    /// Пометка для строки списка; null — проект чистый и старый.
    public string? Mark => !IsGit ? "без git — не отмечен" : GitDirty ? "есть незакоммиченные изменения — не отмечен" : null;
}

public static class DevJunk
{
    public const int MaxProjectDepth = 4, MaxSourceDepth = 24;

    /// Мусорные папки, которые можно удалять (имя папки = Kind). Остальные пути Clean не принимает.
    public static readonly IReadOnlyList<string> Kinds = new[] { "node_modules", ".venv", "venv", "bin", "obj", "__pycache__", ".pytest_cache", "dist", "build", ".next" };
    private static readonly HashSet<string> Skip = new(Kinds.Append(".git"), StringComparer.OrdinalIgnoreCase);

    private static bool IsLink(FileSystemInfo i) => (i.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool HasFile(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).Any(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool HasMarker(string dir) =>
        File.Exists(System.IO.Path.Combine(dir, "package.json")) || File.Exists(System.IO.Path.Combine(dir, "pyproject.toml")) || File.Exists(System.IO.Path.Combine(dir, "requirements.txt"))
        || Directory.Exists(System.IO.Path.Combine(dir, ".git")) || File.Exists(System.IO.Path.Combine(dir, ".git")) || HasFile(dir, "*.csproj") || HasFile(dir, "*.sln");

    public static IReadOnlyList<DevProject> ScanProjects(IEnumerable<string> roots, int ageDays, Func<string, (bool isGit, bool dirty)> git,
                                                         CancellationToken ct, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var found = new List<string>();
        foreach (var r in roots)
        {
            try
            {
                var full = SafeDelete.Norm(r);
                var di = new DirectoryInfo(full);
                if (di.Exists && !IsLink(di)) Find(full, 0, found, ct);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }
        var res = new List<DevProject>();
        foreach (var p in found.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var junk = new List<DevJunkDir>();
            var newest = DateTime.MinValue;
            try
            {
                Direct(p, junk);
                Source(p, true, 0, junk, ref newest, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (junk.Count == 0) continue;
            if (newest == DateTime.MinValue) try { newest = Directory.GetLastWriteTimeUtc(p); } catch (Exception) { newest = now; }
            var (isGit, dirty) = (false, false);
            try { (isGit, dirty) = git(p); } catch (Exception e) when (e is not OperationCanceledException) { }
            var old = now - newest >= TimeSpan.FromDays(ageDays);
            res.Add(new DevProject(p, newest, isGit, dirty, junk, old && isGit && !dirty));
        }
        return res.OrderByDescending(x => x.JunkBytes).ToList();
    }

    private static void Find(string dir, int depth, List<string> found, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (HasMarker(dir)) found.Add(dir);
        if (depth >= MaxProjectDepth) return;
        List<DirectoryInfo> subs;
        try { subs = new DirectoryInfo(dir).EnumerateDirectories().ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var s in subs)
        {
            try
            {
                if (IsLink(s) || Skip.Contains(s.Name) || s.Name.StartsWith('.')) continue;
                Find(s.FullName, depth + 1, found, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// Мусор прямо в папке проекта (с проверкой маркеров: bin/obj — только рядом с csproj, dist/build/.next — только рядом с package.json, venv — только с pyvenv.cfg).
    private static void Direct(string p, List<DevJunkDir> junk)
    {
        void Add(string name, Func<string, bool>? ok = null)
        {
            var d = System.IO.Path.Combine(p, name);
            var di = new DirectoryInfo(d);
            if (!di.Exists || IsLink(di) || (ok != null && !ok(d))) return;
            junk.Add(new DevJunkDir(d, name, SafeDelete.SizeOf(d)));
        }
        var csproj = HasFile(p, "*.csproj");
        var pkg = File.Exists(System.IO.Path.Combine(p, "package.json"));
        Add("node_modules");
        foreach (var v in new[] { ".venv", "venv" }) Add(v, d => File.Exists(System.IO.Path.Combine(d, "pyvenv.cfg")));
        if (csproj) { Add("bin"); Add("obj"); }
        if (pkg) { Add("dist"); Add("build"); Add(".next"); }
        Add(".pytest_cache");
    }

    /// Один обход исходников: новейшее время файла (без мусорных папок) и вложенные __pycache__/.pytest_cache. Мусор вложенных проектов — их собственный, здесь не собирается.
    private static void Source(string dir, bool collect, int depth, List<DevJunkDir> junk, ref DateTime newest, CancellationToken ct)
    {
        if (depth > MaxSourceDepth) return;
        ct.ThrowIfCancellationRequested();
        List<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var e in entries)
        {
            try
            {
                if (IsLink(e)) continue;
                if ((e.Attributes & FileAttributes.Directory) == 0) { if (e.LastWriteTimeUtc > newest) newest = e.LastWriteTimeUtc; continue; }
                if (e.Name is "__pycache__" or ".pytest_cache")
                {
                    if (collect && !junk.Any(j => j.Path.Equals(e.FullName, StringComparison.OrdinalIgnoreCase)))
                        junk.Add(new DevJunkDir(e.FullName, e.Name, SafeDelete.SizeOf(e.FullName)));
                    continue;
                }
                if (Skip.Contains(e.Name)) continue;
                Source(e.FullName, collect && !HasMarker(e.FullName), depth + 1, junk, ref newest, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// Удаление выбранного мусора. Принимаются только папки из списка Kinds внутри roots (без ссылок по пути от корня), не ссылки; занятые файлы пропускаются.
    public static CleanResult Clean(IEnumerable<DevJunkDir> dirs, IEnumerable<string> roots)
    {
        var r = CleanResult.Empty;
        var rootList = roots.ToList();
        foreach (var j in dirs)
        {
            try
            {
                var name = System.IO.Path.GetFileName(j.Path.TrimEnd('\\', '/'));
                if (!Kinds.Contains(j.Kind, StringComparer.OrdinalIgnoreCase) || !name.Equals(j.Kind, StringComparison.OrdinalIgnoreCase))
                { r = r.Plus(new CleanResult(0, 1, null)); continue; }
                if (!rootList.Any(root => SafeDelete.IsSafeUnder(root, j.Path))) { r = r.Plus(new CleanResult(0, 1, null)); continue; }   // вне папок проектов или путь идёт по ссылке
                var di = new DirectoryInfo(j.Path);
                if (!di.Exists || IsLink(di)) { if (di.Exists) r = r.Plus(new CleanResult(0, 1, null)); continue; }
                var size = SafeDelete.SizeOf(j.Path);
                var d = SafeDelete.DeleteDirectory(j.Path);
                r = r.Plus(d with { FreedBytes = d.Skipped == 0 ? size : d.FreedBytes });
            }
            catch (Exception) { r = r.Plus(new CleanResult(0, 1, null)); }
        }
        return r;
    }

    /// `git -C <path> status --porcelain`: не git или git не найден → (false,false); есть вывод → незакоммиченное.
    public static (bool isGit, bool dirty) GitStatus(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "-C " + AdminQuote(path) + " status --porcelain")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p == null) return (false, false);
            var outTask = p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch (Exception) { } return (false, false); }
            return p.ExitCode == 0 ? (true, outTask.Result.Trim().Length > 0) : (false, false);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { return (false, false); }
    }

    private static string AdminQuote(string p) => "\"" + p.TrimEnd('\\', '/') + "\"";

    /// Кеши пакетных менеджеров: npm, pip, NuGet («следующая сборка скачает пакеты заново»), yarn, pnpm — только те, что есть.
    public static IReadOnlyList<ICleanupCategory> CacheCategories(CleanupEnv env)
    {
        ICleanupCategory C(string id, string title, string dir, string @base, bool chk, string? note = null) =>
            new DirsCategory(id, CleanupGroups.Dev, title, env.Procs, () => new[] { new CleanTarget(@base, dir) }, null, chk, note);
        var l = env.LocalAppData;
        var nuget = System.IO.Path.Combine(env.UserProfile, ".nuget", "packages");
        return new[]
        {
            C("Dev.npm", "Кеш npm", System.IO.Path.Combine(l, "npm-cache", "_cacache"), System.IO.Path.Combine(l, "npm-cache"), true),
            C("Dev.pip", "Кеш pip", System.IO.Path.Combine(l, "pip", "cache"), System.IO.Path.Combine(l, "pip"), true),
            C("Dev.nuget", "Пакеты NuGet", nuget, nuget, false, "следующая сборка скачает пакеты заново"),
            C("Dev.yarn", "Кеш yarn", System.IO.Path.Combine(l, "Yarn", "Cache"), System.IO.Path.Combine(l, "Yarn"), true),
            C("Dev.pnpm", "Хранилище pnpm", System.IO.Path.Combine(l, "pnpm", "store"), System.IO.Path.Combine(l, "pnpm"), false, "проекты скачают пакеты заново"),
        };
    }
}
