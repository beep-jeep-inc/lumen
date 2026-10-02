using System.Security.Principal;
using Pult.Core.Admin;

namespace Pult.Admin;

/// Что умеет SYSTEM-исполнитель (реально — SystemMaintenance; в тестах подставной).
public interface IJobExecutor { JobResult Execute(string nonce, AdminJobKind kind, string arg); }

public sealed class SystemJobExecutor(SystemMaintenance m) : IJobExecutor
{
    public JobResult Execute(string nonce, AdminJobKind kind, string arg)
    {
        // SYSTEM не доверяет ничему, кроме заново разобранного Arg (включая «norp:»).
        var parsed = AdminMaintenance.TryParseDriverIds(arg, out var ids, out var skipRestore, out var problem);
        switch (kind)
        {
            case AdminJobKind.DriverSearch:
                if (!parsed) return new(nonce, false, problem);
                return m.SearchDrivers(nonce, ids);
            case AdminJobKind.DriverInstall:
                if (!parsed) return new(nonce, false, problem);
                return From(nonce, m.InstallDrivers(ids, skipRestore));
            case AdminJobKind.RestorePoint: return From(nonce, m.CreateRestorePoint());
            case AdminJobKind.CleanSystem: return From(nonce, m.CleanSystem(arg));
            case AdminJobKind.DiskDetails: return arg.Length == 0 ? From(nonce, m.ReadDiskDetails()) : new(nonce, false, "у этого действия не бывает аргументов");
            case AdminJobKind.RepairSystem: return arg.Length == 0 ? From(nonce, m.RepairSystem()) : new(nonce, false, "у этого действия не бывает аргументов");
        }
        return new(nonce, false, "неизвестное задание");
    }

    private static JobResult From(string nonce, MaintenanceResult r) => new(nonce, r.Ok, r.Message, r.Code);
}

/// `PultAdmin.exe --system-job` от задания «Pult System» (SYSTEM): взять старейшее задание, заново проверить политикой,
/// выполнить, записать .result, удалить задание. Не SYSTEM — ничего не делает.
public static class SystemWorker
{
    public const int ExitNotSystem = 11;
    public const int MaxJobsPerRun = 5;
    public static readonly TimeSpan MaxLoopAge = TimeSpan.FromMinutes(5);

    public static bool CurrentIsSystem()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem;
    }

    public static int Run(Func<bool> isSystem, JobStore store, IJobExecutor exec, Action<string> log, Func<DateTime>? utcNow = null)
    {
        if (!isSystem()) { log("ОТКАЗ: --system-job запущен не от SYSTEM"); return ExitNotSystem; }
        store.DeleteStale(DateTime.UtcNow);
        var now = utcNow ?? (() => DateTime.UtcNow);
        var started = now();
        for (var i = 0; i < MaxJobsPerRun; i++)                                      // остаток не должен заслонить задание, ради которого запущен /Run
        {
            if (i > 0 && now() - started > MaxLoopAge) break;              // лимит задачи — 75 мин: новое долгое задание поздно начинать
            var taken = store.TakeOldest(log);
            if (taken == null) break;
            var (job, path) = taken.Value;
            try
            {
                var d = AdminPolicy.Decide(job.ToRequest());   // файлу не доверяем
                JobResult res;
                if (!d.Allowed) res = new(job.Nonce, false, d.Reason, d.Code);
                else
                {
                    try { res = exec.Execute(job.Nonce, job.Kind, job.Arg) with { Nonce = job.Nonce }; }
                    catch (Exception e) { res = new(job.Nonce, false, "ошибка: " + e.Message); }
                }
                store.WriteResult(res);
                log($"{(res.Ok ? "OK" : "ОТКАЗ")} задание {job.Kind}: {res.Message}");
            }
            finally { try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        }
        return 0;
    }
}

public interface IJobChannel
{
    /// Отдать задание SYSTEM-исполнителю и дождаться результата (или причины, почему его нет).
    JobResult Run(string nonce, AdminJobKind kind, string arg, TimeSpan timeout);
}

/// schtasks /Run /TN "Pult System" (полный путь System32) + ожидание .result.
public sealed class SystemTaskChannel(JobStore store, Func<bool> runTask, Func<bool> taskExists) : IJobChannel
{
    public static SystemTaskChannel ForThisPc() => new(new JobStore(JobStore.DefaultDir, new SystemJobTrust()),
        () => RunQuiet(SystemTools.Exe("schtasks.exe"), $"/Run /TN \"{TaskXml.SystemTaskName}\"") == 0,
        () => RunQuiet(SystemTools.Exe("schtasks.exe"), $"/Query /TN \"{TaskXml.SystemTaskName}\"") == 0);

    public JobResult Run(string nonce, AdminJobKind kind, string arg, TimeSpan timeout)
    {
        if (!taskExists())
            return new(nonce, false, "SYSTEM-исполнитель («Pult System») не установлен — переустанови помощника администратора", AdminCodes.SystemTaskMissing);
        store.Submit(new AdminJob(nonce, kind, arg));
        if (!runTask()) { store.Cancel(nonce); return new(nonce, false, "не удалось запустить задание «Pult System»"); }
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (store.TryReadResult(nonce) is { } r) return r;
            Thread.Sleep(200);
        }
        store.Cancel(nonce);
        return new(nonce, false, "SYSTEM-исполнитель не ответил вовремя — операция может ещё идти, проверь позже");
    }

    private static int RunQuiet(string exe, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
        if (p == null) return -1;
        if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (InvalidOperationException) { } return -1; }
        return p.ExitCode;
    }
}
