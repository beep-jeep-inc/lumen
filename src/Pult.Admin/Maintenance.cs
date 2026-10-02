using System.Diagnostics;
using System.Text;
using Pult.Core.Admin;

namespace Pult.Admin;

/// Итог действия обслуживания: Code — машинно-читаемый (AdminCodes.* или «freed=…;skipped=…»).
public sealed record MaintenanceResult(bool Ok, string Message, string Code = "");

public interface IMaintenanceExecutor
{
    MaintenanceResult InstallDrivers(IReadOnlyList<string> updateIds, bool skipRestore);
    MaintenanceResult CreateRestorePoint();
    MaintenanceResult CleanSystem(string cleanId);
    /// Починка Windows (DISM + sfc). По умолчанию «не поддерживается»: исполнитель обязан переопределить её сам.
    MaintenanceResult RepairSystem() => new(false, "починка Windows недоступна");
    /// Здоровье дисков (только чтение). По умолчанию «недоступно»; читает только SYSTEM-исполнитель.
    MaintenanceResult ReadDiskDetails() => new(false, "чтение дисков недоступно");
}

public sealed record ProcessOutcome(int ExitCode, string Output, bool TimedOut, string Error = "");

/// Запуск встроенной программы Windows по полному пути в чистом окружении (ничего из окружения пользователя).
public static class SystemProcess
{
    private const int MaxOutput = 256 * 1024;

    /// Как Run, но stdout отдаётся байтами (не больше 1 МБ). Таймаут — остановка процесса вместе с дочерними.
    public static RawOutcome RunRaw(string exe, string args, TimeSpan timeout, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        CleanEnvironment.Apply(psi, env);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить " + Path.GetFileName(exe));
        var ms = new MemoryStream();
        const int cap = 1024 * 1024;
        var outTask = Task.Run(() =>
        {
            var buf = new byte[8192]; var s = p.StandardOutput.BaseStream; int n;
            try { while ((n = s.Read(buf, 0, buf.Length)) > 0) lock (ms) if (ms.Length < cap) ms.Write(buf, 0, (int)Math.Min(n, cap - ms.Length)); }
            catch (IOException) { }
        });
        var errTask = Task.Run(() => { try { p.StandardError.ReadToEnd(); } catch (IOException) { } });
        try { p.StandardInput.Close(); } catch (IOException) { }
        var timedOut = !p.WaitForExit(timeout);
        if (timedOut) { try { p.Kill(entireProcessTree: true); p.WaitForExit(10000); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        else p.WaitForExit();
        outTask.Wait(10000); errTask.Wait(5000);
        lock (ms) return new RawOutcome(timedOut ? -1 : p.ExitCode, ms.ToArray(), timedOut);
    }

    /// argList — аргументы отдельными элементами (без склейки в строку); тогда args игнорируется. killOnTimeout=false: по таймауту процесс НЕ убиваем (установщик посреди работы), только перестаём ждать.
    public static ProcessOutcome Run(string exe, string args, TimeSpan timeout, IReadOnlyDictionary<string, string> env, string? stdinText = null,
                                     IReadOnlyList<string>? argList = null, bool killOnTimeout = true)
    {
        var psi = new ProcessStartInfo(exe, argList == null ? args : "")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (argList != null) foreach (var a in argList) psi.ArgumentList.Add(a);
        CleanEnvironment.Apply(psi, env);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить " + Path.GetFileName(exe));
        var sb = new StringBuilder();
        void Add(string? line) { if (line == null) return; lock (sb) if (sb.Length < MaxOutput) sb.AppendLine(line); }
        p.OutputDataReceived += (_, e) => Add(e.Data);
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) if (err.Length < 8192) err.AppendLine(e.Data); };                  // stderr не смешиваем с разбираемым stdout (там CLIXML-прогресс PowerShell)
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try { if (stdinText != null) p.StandardInput.Write(stdinText); p.StandardInput.Close(); } catch (IOException) { }
        if (!p.WaitForExit(timeout))
        {
            if (killOnTimeout) { try { p.Kill(entireProcessTree: true); p.WaitForExit(10000); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            lock (sb) lock (err) return new ProcessOutcome(-1, sb.ToString(), true, err.ToString());
        }
        p.WaitForExit();                                                        // дочитать вывод
        lock (sb) lock (err) return new ProcessOutcome(p.ExitCode, sb.ToString(), false, err.ToString());
    }
}

/// Вывод программы как есть (байты): sfc пишет в UTF-16, текстовое чтение его бы испортило.
public sealed record RawOutcome(int ExitCode, byte[] Stdout, bool TimedOut);

public interface IDismRunner { ProcessOutcome StartComponentCleanup(TimeSpan timeout); }

public sealed class SystemDismRunner : IDismRunner
{
    public ProcessOutcome StartComponentCleanup(TimeSpan timeout) =>
        SystemProcess.Run(ComponentCleanup.DismPath, ComponentCleanup.Arguments, timeout, CleanEnvironment.ForSystem());
}

/// DISM из System32 (полный путь), фиксированные аргументы, таймаут 60 минут.
public static class ComponentCleanup
{
    public static string DismPath => Path.Combine(Environment.SystemDirectory, "Dism.exe");
    public const string Arguments = "/Online /Cleanup-Image /StartComponentCleanup";
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(60);

    public static MaintenanceResult Run(IDismRunner dism)
    {
        var r = dism.StartComponentCleanup(Timeout);
        if (r.TimedOut) return new(false, "Очистка хранилища компонентов не уложилась в 60 минут и остановлена");
        return r.ExitCode switch
        {
            0 => new(true, "Очистка хранилища компонентов Windows завершена"),
            3010 => new(true, "Очистка хранилища компонентов завершена, нужна перезагрузка", AdminCodes.RebootRequired),
            _ => new(false, $"Очистка хранилища компонентов не удалась (код {r.ExitCode})"),
        };
    }
}
