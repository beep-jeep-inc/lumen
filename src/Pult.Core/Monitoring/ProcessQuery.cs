using System.Diagnostics;
using System.Management;

namespace Pult.Core.Monitoring;

public interface IProcessQuery
{
    bool IsRunning(string name);
    /// Есть ли у процесса с таким именем видимое главное окно. Фоновые процессы без окна (ускорение запуска Edge, Chrome «в фоне») — нет.
    /// По умолчанию — «запущен»: так ведут себя заглушки в тестах.
    bool HasVisibleWindow(string name) => IsRunning(name);
    /// Один снимок списка процессов на тик (вызывается вне потока окна); IsRunning смотрит в него.
    void Snapshot() { }
    /// wqlNameFilter — условие WQL по Name, например "Name='python.exe' OR Name='pythonw.exe'".
    bool AnyCommandLine(string wqlNameFilter, Func<string, bool> match);
    /// Один WMI-запрос: пары (имя процесса, командная строка). По умолчанию пусто (заглушки в тестах).
    IReadOnlyList<(string Name, string CommandLine)> CommandLines(string wqlNameFilter) => Array.Empty<(string, string)>();
    /// Один WMI-запрос: имя, полный путь exe и командная строка (путь/командная строка — null, если Windows их не показала). По умолчанию пусто (заглушки в тестах).
    IReadOnlyList<RunningProcInfo> Processes(string wqlWhere) => Array.Empty<RunningProcInfo>();
}

public sealed record RunningProcInfo(string Name, string? ExePath, string? CommandLine);

public sealed class SystemProcessQuery : IProcessQuery
{
    private volatile HashSet<string>? _snap;

    public void Snapshot()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = Process.GetProcesses();
        foreach (var p in all) { try { set.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
        _snap = set;
    }

    public bool IsRunning(string name)
    {
        if (_snap is { } snap) return snap.Contains(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
        var ps = Process.GetProcessesByName(name);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    public bool HasVisibleWindow(string name)
    {
        var ps = Process.GetProcessesByName(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
        try
        {
            foreach (var p in ps)
                try { if (p.MainWindowHandle != IntPtr.Zero) return true; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }     // процесс уже завершился / недоступен
            return false;
        }
        finally { foreach (var p in ps) p.Dispose(); }
    }

    public IReadOnlyList<(string Name, string CommandLine)> CommandLines(string wqlNameFilter)
    {
        var list = new List<(string, string)>();
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT Name, CommandLine FROM Win32_Process WHERE {wqlNameFilter}");
            using var col = s.Get();
            foreach (ManagementObject o in col)
                using (o) if (o["Name"] is string n && o["CommandLine"] is string cl) list.Add((n, cl));
        }
        catch (ManagementException) { }
        return list;
    }

    public IReadOnlyList<RunningProcInfo> Processes(string wqlWhere)
    {
        var list = new List<RunningProcInfo>();
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT Name, ExecutablePath, CommandLine FROM Win32_Process WHERE {wqlWhere}");
            using var col = s.Get();
            foreach (ManagementObject o in col)
                using (o) if (o["Name"] is string n) list.Add(new RunningProcInfo(n, o["ExecutablePath"] as string, o["CommandLine"] as string));
        }
        catch (ManagementException) { }
        return list;
    }

    public bool AnyCommandLine(string wqlNameFilter, Func<string, bool> match)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE {wqlNameFilter}");
            using var col = s.Get();
            foreach (ManagementObject o in col)
                using (o) if (o["CommandLine"] is string cl && match(cl)) return true;
        }
        catch (ManagementException) { }
        return false;
    }
}
