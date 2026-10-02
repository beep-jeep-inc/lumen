using Pult.Core.Admin;
using Pult.Core.Maintenance;

namespace Pult.Admin;

/// Что умеет «Починка Windows» на стороне SYSTEM-исполнителя (в тестах подставной).
public interface IRepairRunner
{
    /// Идёт ли уже sfc/DISM (наша прошлая починка или чужая) — второй запуск не нужен.
    bool AnotherRunning();
    ProcessOutcome Dism(TimeSpan timeout);
    /// Текст sfc (уже раскодированный из UTF-16), код выхода, таймаут; Cbs — то, что дописалось в CBS.log за время sfc (null — не вышло прочитать).
    SfcRun Sfc(TimeSpan timeout);
}

public sealed record SfcRun(int ExitCode, string Output, bool TimedOut, string? CbsAppended);

/// Фиксированные команды по полным путям из System32, чистое окружение, без аргументов от пользователя.
public sealed class SystemRepairRunner : IRepairRunner
{
    public static string DismPath => Path.Combine(Environment.SystemDirectory, "Dism.exe");
    public static string SfcPath => Path.Combine(Environment.SystemDirectory, "sfc.exe");
    public static string CbsLogPath => Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!, "Logs", "CBS", "CBS.log");
    private const long MaxCbsRead = 4 * 1024 * 1024;

    public bool AnotherRunning()
    {
        // Без WMI (помощник собирается NativeAOT): просто смотрим процессы по имени.
        return SystemRepair.BusyProcessNames.Any(n =>
        {
            var ps = System.Diagnostics.Process.GetProcessesByName(n);
            try { return ps.Length > 0; } finally { foreach (var p in ps) p.Dispose(); }
        });
    }

    public ProcessOutcome Dism(TimeSpan timeout) => SystemProcess.Run(DismPath, SystemRepair.DismArguments, timeout, CleanEnvironment.ForSystem());

    public SfcRun Sfc(TimeSpan timeout)
    {
        var before = CbsLength();
        var r = SystemProcess.RunRaw(SfcPath, SystemRepair.SfcArguments, timeout, CleanEnvironment.ForSystem());
        return new SfcRun(r.ExitCode, SystemRepair.DecodeConsole(r.Stdout), r.TimedOut, ReadCbsFrom(before));
    }

    private static long CbsLength() { try { return new FileInfo(CbsLogPath).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; } }

    /// Только то, что sfc дописал в CBS.log (в журнале копятся и прошлые прогоны — их итоги не должны попасть в этот).
    private static string? ReadCbsFrom(long offset)
    {
        try
        {
            using var fs = new FileStream(CbsLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = fs.Length < offset ? 0 : offset;                    // журнал мог смениться (CBS.log ротируется) — тогда читаем с начала
            if (fs.Length - start > MaxCbsRead) start = fs.Length - MaxCbsRead;
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

public static class SystemRepairRun
{
    public static MaintenanceResult Run(IRepairRunner runner)
    {
        if (runner.AnotherRunning())
            return new(false, "Проверка системных файлов уже идёт (запущен sfc или DISM) — дождитесь её окончания.", AdminCodes.Busy);
        var dism = runner.Dism(SystemRepair.DismTimeout);
        var sfc = runner.Sfc(SystemRepair.SfcTimeout);
        var verdict = sfc.TimedOut ? SfcVerdict.Unknown : SystemRepair.ParseSfc(sfc.Output, sfc.CbsAppended);
        var o = SystemRepair.Summarize(dism.ExitCode, dism.TimedOut, verdict, sfc.ExitCode, sfc.TimedOut);
        return new(o.Ok, o.Message, o.Code);
    }
}
