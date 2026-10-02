using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pult.Core.Monitoring;

namespace Pult.Core.Maintenance;

/// Запущенный процесс (имя файла, полный путь, командная строка). Путь и командная строка могут быть неизвестны (чужой процесс без прав).
public sealed record RunningProc(string Name, string? ExePath, string CommandLine);

/// Кто пользуется Python или .NET прямо сейчас: обновление пакета (Python.Python.*, Microsoft.DotNet.* и т.п.) на 2–3 минуты ломает такие программы.
/// Здесь — чистая логика сопоставления (процессы, папки Python, чтение pyvenv.cfg передаются снаружи), плюс тонкая обёртка над реальной системой.
public static class RuntimeUsage
{
    private static readonly string[] GuardedPrefixes = { "Python.Python.", "Microsoft.DotNet.", "Microsoft.WindowsDesktop.", "Microsoft.AspNetCore." };

    /// Пакеты, обновление которых проверяется на «используется».
    public static bool IsGuarded(string? id) => id != null && GuardedPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsPython(string? id) => id != null && id.StartsWith("Python.Python.", StringComparison.OrdinalIgnoreCase);

    /// «Python.Python.3.12» → «3.12»; «Python.Python.3» → «3»; не Python → null.
    public static string? PythonVersion(string? id)
    {
        if (!IsPython(id)) return null;
        var v = id!["Python.Python.".Length..];
        return Regex.IsMatch(v, @"^[0-9]+(\.[0-9]+)?\z", RegexOptions.CultureInvariant) ? v : null;
    }

    /// Обычные папки установки этой версии: «3.12» → Python312 (+ «-32», «-arm64») в профиле и в Program Files.
    public static IReadOnlyList<string> ConventionalPythonDirs(string version, string localAppData, string programFiles, string programFilesX86)
    {
        var tag = "Python" + version.Replace(".", "");
        var names = new[] { tag, tag + "-32", tag + "-arm64" };
        return names.Select(n => Path.Combine(localAppData, "Programs", "Python", n))
                    .Concat(names.Select(n => Path.Combine(programFiles, n)))
                    .Concat(names.Select(n => Path.Combine(programFilesX86, n))).ToList();
    }

    private static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');

    /// Лежит ли path внутри папки dir (сама папка тоже считается). Без учёта регистра.
    public static bool IsInside(string path, string dir)
    {
        var p = Norm(path); var d = Norm(dir);
        if (d.Length == 0) return false;
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex PythonExe = new(@"^pythonw?([0-9]+(\.[0-9]+)?)?\.exe\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HomeRx = new(@"^[ \t]*home[ \t]*=[ \t]*(.+?)[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// Значение «home =» из pyvenv.cfg (папка базового Python, из которого создали виртуальное окружение); null — нет.
    public static string? VenvHome(string? cfgText)
    {
        if (string.IsNullOrEmpty(cfgText)) return null;
        var m = HomeRx.Match(cfgText.Replace("\r", ""));
        return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
    }

    /// Процесс Python относится к одной из папок установки: сам лежит в ней или это venv, созданный из неё (pyvenv.cfg рядом с Scripts\\ или в корне окружения).
    public static bool UsesPython(RunningProc p, IReadOnlyList<string> installDirs, Func<string, string?> readFile)
    {
        if (!PythonExe.IsMatch(p.Name)) return false;
        if (installDirs.Count == 0) return true;                              // версию к папке не привязать — осторожно считаем любым Python
        if (string.IsNullOrEmpty(p.ExePath)) return false;
        if (installDirs.Any(d => IsInside(p.ExePath!, d))) return true;
        var dir = Path.GetDirectoryName(p.ExePath);
        for (var i = 0; i < 2 && !string.IsNullOrEmpty(dir); i++, dir = Path.GetDirectoryName(dir))
        {
            var home = VenvHome(SafeRead(readFile, Path.Combine(dir, "pyvenv.cfg")));
            if (home != null) return installDirs.Any(d => IsInside(home, d));
        }
        return false;
    }

    private static string? SafeRead(Func<string, string?> read, string path)
    {
        try { return read(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Имя для списка: «python -m notes_bot.startup» (без пути к exe и расширения); если командной строки нет — просто имя процесса. Длинное — с «…».
    public static string Display(RunningProc p)
    {
        var name = Path.GetFileNameWithoutExtension(p.Name);
        var cl = (p.CommandLine ?? "").Trim();
        if (cl.Length == 0) return name;
        string rest;
        if (cl[0] == '"') { var end = cl.IndexOf('"', 1); rest = end < 0 ? "" : cl[(end + 1)..]; }
        else { var sp = cl.IndexOf(' '); rest = sp < 0 ? "" : cl[sp..]; }
        var text = (name + " " + rest.Trim()).Trim();
        return text.Length > 70 ? text[..70] + "…" : text;
    }

    /// Кто пользуется пакетом: для Python — процессы python*.exe из папки этой версии или venv от неё; для .NET — dotnet.exe.
    /// Для не охраняемых пакетов — пусто. Результат без повторов.
    public static IReadOnlyList<string> Find(string id, IReadOnlyList<RunningProc> procs, IReadOnlyList<string> pythonDirs, Func<string, string?> readFile)
    {
        if (!IsGuarded(id)) return Array.Empty<string>();
        IEnumerable<RunningProc> hit = IsPython(id)
            ? procs.Where(p => UsesPython(p, pythonDirs, readFile))
            : procs.Where(p => p.Name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase));
        return hit.Select(Display).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- настоящая система ----

    private const string WqlFilter = "Name LIKE 'python%' OR Name = 'dotnet.exe'";

    /// Папки установки Python этой версии: реестр (PythonCore\<версия>\InstallPath в HKCU/HKLM, обе разрядности) и обычные места.
    public static IReadOnlyList<string> PythonInstallDirs(string version)
    {
        var dirs = new List<string>();
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
            foreach (var suffix in new[] { "", "-32", "-arm64" })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var k = baseKey.OpenSubKey(@"SOFTWARE\Python\PythonCore\" + version + suffix + @"\InstallPath");
                    if (k?.GetValue("") is string s && s.Length > 0) dirs.Add(s);
                    if (k?.GetValue("ExecutablePath") is string e && e.Length > 0 && Path.GetDirectoryName(e) is { } ed) dirs.Add(ed);
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        dirs.AddRange(ConventionalPythonDirs(version, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                             Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)));
        return dirs.Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// Только чтение: запрос процессов (WMI) и реестра, ничего не запускается и не меняется. Вызывать не на потоке окна.
    public static IReadOnlyList<string> FindUsers(string id, IProcessQuery q)
    {
        if (!IsGuarded(id)) return Array.Empty<string>();
        var procs = q.Processes(WqlFilter).Select(x => new RunningProc(x.Name, x.ExePath, x.CommandLine ?? "")).ToList();
        if (procs.Count == 0) return Array.Empty<string>();
        var dirs = PythonVersion(id) is { } v && v.Contains('.') ? PythonInstallDirs(v) : Array.Empty<string>();
        return Find(id, procs, dirs, path => File.Exists(path) ? File.ReadAllText(path) : null);
    }
}
