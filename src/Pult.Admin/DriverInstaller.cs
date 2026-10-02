using Pult.Core.Admin;
using Pult.Core.Maintenance;

namespace Pult.Admin;

public enum RestoreOutcome { Created, TooRecent, Disabled, Failed }

public interface IRestoreBackend { RestoreOutcome Create(out string detail); }

/// Точка восстановления. Защиту системы не включаем сами: выключена — понятный отказ.
public sealed class RestorePointService(IRestoreBackend backend)
{
    public const string DisabledMessage = "Защита системы выключена — точку восстановления создать нельзя";
    public const string RecentMessage = "Свежая точка восстановления уже есть (Windows создаёт не чаще раза в сутки) — продолжаю с ней";
    public const string CreatedMessage = "Точка восстановления создана";

    public MaintenanceResult Run()
    {
        RestoreOutcome o; string detail;
        try { o = backend.Create(out detail); } catch (Exception e) { o = RestoreOutcome.Failed; detail = e.Message; }
        return o switch
        {
            RestoreOutcome.Created => new(true, CreatedMessage),
            RestoreOutcome.TooRecent => new(true, RecentMessage, AdminCodes.RestoreRecent),
            RestoreOutcome.Disabled => new(false, DisabledMessage, AdminCodes.RestoreDisabled),
            _ => new(false, "Не удалось создать точку восстановления" + (detail.Length > 0 ? " (" + detail + ")" : "")),
        };
    }
}

public sealed class PsRestoreBackend(IScriptRunner ps) : IRestoreBackend
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    public RestoreOutcome Create(out string detail)
    {
        var r = ps.Run(MaintenanceScripts.RestorePoint, Timeout);
        if (r.TimedOut) { detail = "не уложилась в 3 минуты"; return RestoreOutcome.Failed; }
        return MaintenanceScripts.ParseRestore(r.Output, out detail);
    }
}

public sealed record DriverUpdateRow(string Id, string Title, string? DriverClass = null, string? HardwareId = null, int? RebootBehavior = null);
public sealed record DriverInstallReport(int Installed, int Failed, int Skipped, bool RebootRequired, string? Error, IReadOnlyList<string>? RebootTitles = null);

public interface IDriverInstallBackend { DriverInstallReport Install(IReadOnlyList<string> updateIds); }

public interface ISearchDiagnostics { string? LastError { get; } }

/// Поиск драйверов в Центре обновления для помощника (PowerShell-скрипт; Pult.Core.WuaSearch использует dynamic и в NativeAOT не работает).
public sealed class PsWuaSearch(IScriptRunner ps) : IWindowsUpdateSearch, ISearchDiagnostics
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    public string? LastError { get; private set; }

    public Task<IReadOnlyList<DriverUpdate>> SearchDriversAsync(CancellationToken ct)
    {
        LastError = null;
        var r = ps.Run(MaintenanceScripts.SearchDrivers, Timeout);
        if (r.TimedOut) { LastError = "Центр обновления не ответил за 3 минуты"; return Task.FromResult<IReadOnlyList<DriverUpdate>>(Array.Empty<DriverUpdate>()); }
        var (rows, err) = MaintenanceScripts.ParseSearch(r.Output);
        LastError = err;
        IReadOnlyList<DriverUpdate> list = rows.Select(x => new DriverUpdate(x.Id, x.Title, null, null, null, null, x.DriverClass, x.HardwareId, x.RebootBehavior)).ToList();
        return Task.FromResult(err == null ? list : Array.Empty<DriverUpdate>());
    }
}

public sealed class PsDriverBackend(IScriptRunner ps) : IDriverInstallBackend
{
    /// Скачивание и установка: 27 минут (вместе с 3 мин поиска и 3 мин точки восстановления укладывается в 35 мин ожидания пульта).
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(27);

    public DriverInstallReport Install(IReadOnlyList<string> updateIds)
    {
        var r = ps.Run(MaintenanceScripts.InstallDrivers(updateIds), Timeout);
        if (r.TimedOut) return new(0, 0, 0, false, "Lumen перестал ждать (27 минут): установка может ещё идти — проверь позже в Центре обновления Windows");
        return MaintenanceScripts.ParseInstall(r.Output);
    }
}

/// DriverInstall: искать заново в Центре обновления, ставить ТОЛЬКО пересечение запрошенных id с результатом поиска, перед установкой — точка восстановления.
public sealed class DriverInstaller(IWindowsUpdateSearch search, RestorePointService restore, IDriverInstallBackend backend, Func<string, bool> confirm)
{
    public const string SkippedRestoreMessage = "Точка восстановления не создавалась (ты так выбрал)";
    public const string DeclinedMessage = "Ты отказался в окне помощника — драйверы не установлены";

    public const string RestoreWillBe = "Перед установкой будет создана точка восстановления.",
                        RestoreWillNot = "Точка восстановления НЕ будет создана (ты так выбрал).",
                        NoAutoReboot = "Компьютер сам не перезагрузится: если драйверам нужна перезагрузка, Lumen покажет кнопку — перезагрузишь, когда удобно.";

    public static string ConfirmText(IReadOnlyList<string> titles, bool skipRestore = false)
    {
        var shown = titles.Take(12).Select(t => { t = t.Replace((char)13, (char)32).Replace((char)10, (char)32); return "• " + (t.Length > 110 ? t[..110] + "…" : t); });
        return $"Установить драйверы из Центра обновления Windows: {titles.Count} шт.?\n\n" + string.Join("\n", shown)
               + (titles.Count > 12 ? $"\n…и ещё {titles.Count - 12}" : "")
               + "\n\n" + (skipRestore ? RestoreWillNot : RestoreWillBe) + "\n" + NoAutoReboot;
    }

    /// Из запрошенных — только те, что есть в результате поиска; возвращаются id из результата поиска (не строки запроса).
    public static IReadOnlyList<string> Intersect(IEnumerable<string> requested, IEnumerable<DriverUpdate> found)
    {
        var want = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var f in found)
            if (want.Contains(f.UpdateId) && AdminMaintenance.IsValidUpdateId(f.UpdateId) && !list.Contains(f.UpdateId, StringComparer.OrdinalIgnoreCase))
                list.Add(f.UpdateId.ToLowerInvariant());
        return list;
    }

    public MaintenanceResult Run(IReadOnlyList<string> requested, bool skipRestore = false)
    {
        IReadOnlyList<DriverUpdate> found;
        try { found = search.SearchDriversAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { return new(false, "Не удалось найти обновления драйверов: " + e.Message); }
        if (search is ISearchDiagnostics { LastError: { } searchError })
            return new(false, "Не удалось найти обновления драйверов: " + searchError);

        var chosen = Intersect(requested, found);
        var ignored = requested.Count - chosen.Count;
        if (chosen.Count == 0) return new(true, "Выбранных обновлений драйверов больше нет в Центре обновления — ничего не установлено");

        var titles = chosen.Select(id => found.First(f => string.Equals(f.UpdateId, id, StringComparison.OrdinalIgnoreCase)).Title).ToList();
        if (!confirm(ConfirmText(titles, skipRestore))) return new(false, DeclinedMessage, AdminCodes.Declined);        // окно помощника: программа пользователя его не нажмёт (UIPI)

        var restoreNote = SkippedRestoreMessage;
        if (!skipRestore)                                                       // поиск — до точки: если ставить нечего, точку не создаём
        {
            var rp = restore.Run();
            if (!rp.Ok) return new(false, rp.Message + " — драйверы не устанавливались", rp.Code);
            restoreNote = rp.Message;
        }

        var rep = backend.Install(chosen);
        if (rep.Error != null) return new(false, "Установка драйверов: " + rep.Error);
        var text = $"Драйверы: установлено {rep.Installed}, с ошибками {rep.Failed}" + (rep.Skipped > 0 ? $", пропущено {rep.Skipped} (не принята лицензия — поставь их в Центре обновления Windows)" : "")
                   + (ignored > 0 ? $", не из результата поиска (проигнорировано) {ignored}" : "")
                   + (rep.RebootRequired ? "; нужна перезагрузка" + (rep.RebootTitles is { Count: > 0 } rt ? ": " + Drivers.TitleList(rt) : "") : "")
                   + ". " + restoreNote;                                        // что стало с точкой восстановления — в итоге, а не только в журнале
        var ok = rep.Installed > 0 || rep.Failed == 0;
        return new(ok, text, rep.RebootRequired ? AdminCodes.RebootRequired : "");
    }
}

/// Исполнитель для SYSTEM (`--system-job`): подтверждение уже получено помощником пользователя, поэтому confirm — «да».
public sealed class SystemMaintenance(Func<string, bool> confirm, IUserClassesRegistry? classes = null, IScriptRunner? runner = null, IRepairRunner? repair = null) : IMaintenanceExecutor
{
    private readonly IScriptRunner _ps = runner ?? new PowerShellHost();
    private readonly IUserClassesRegistry _classes = classes ?? new HkcuClasses();

    public static SystemMaintenance ForSystemWorker() => new(_ => true);

    private static MaintenanceResult ComRefusal(string key) => new(false, ComGuard.Message + " (" + key + ")", AdminCodes.ComRegistration);

    public MaintenanceResult CreateRestorePoint() =>
        ComGuard.Problem(_classes) is { } bad ? ComRefusal(bad) : new RestorePointService(new PsRestoreBackend(_ps)).Run();

    /// Поиск для окна подтверждения: id (из результата поиска, пересечение с запрошенными) и названия.
    public JobResult SearchDrivers(string nonce, IReadOnlyList<string> requested)
    {
        if (ComGuard.Problem(_classes) is { } bad) return new(nonce, false, ComRefusal(bad).Message, AdminCodes.ComRegistration);
        var search = new PsWuaSearch(_ps);
        IReadOnlyList<DriverUpdate> found;
        try { found = search.SearchDriversAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { return new(nonce, false, "Не удалось найти обновления драйверов: " + e.Message); }
        if (search.LastError is { } err) return new(nonce, false, "Не удалось найти обновления драйверов: " + err);
        var ids = DriverInstaller.Intersect(requested, found);
        var titles = ids.Select(id => found.First(f => string.Equals(f.UpdateId, id, StringComparison.OrdinalIgnoreCase)).Title).ToArray();
        return ids.Count == 0
            ? new(nonce, true, "Выбранных обновлений драйверов больше нет в Центре обновления — ничего не установлено", "", Array.Empty<string>(), Array.Empty<string>())
            : new(nonce, true, $"Найдено {ids.Count}", "", ids.ToArray(), titles);
    }

    public MaintenanceResult InstallDrivers(IReadOnlyList<string> updateIds, bool skipRestore) =>
        ComGuard.Problem(_classes) is { } bad ? ComRefusal(bad)
            : new DriverInstaller(new PsWuaSearch(_ps), new RestorePointService(new PsRestoreBackend(_ps)), new PsDriverBackend(_ps), confirm).Run(updateIds, skipRestore);

    /// Только чтение счётчиков дисков: сначала проверка пользовательских COM-регистраций (как у остальных действий с WMI/COM), затем константный скрипт.
    public MaintenanceResult ReadDiskDetails() =>
        ComGuard.Problem(_classes) is { } bad ? ComRefusal(bad) : DiskDetailsRun.Run(_ps);

    public MaintenanceResult RepairSystem() => SystemRepairRun.Run(repair ?? new SystemRepairRunner());

    public MaintenanceResult CleanSystem(string cleanId) =>
        SystemCleaner.Run(cleanId, Environment.SystemDirectory, AdminStore.ProgramDataDir, DateTime.UtcNow, new SystemDismRunner(), confirm);
}
