using Pult.Core.Admin;

namespace Pult.Admin;

/// Обслуживание в процессе помощника пользователя (с правами администратора): проверяет, спрашивает («Да/Нет» с авто-«Нет» через 5 минут)
/// и только после «Да» отдаёт задание SYSTEM-исполнителю. Сам COM/PowerShell не запускает.
public sealed class BrokeredMaintenance(IJobChannel channel, Func<string, ConfirmAnswer> confirm, Func<string, long>? estimate = null) : IMaintenanceExecutor
{
    public static BrokeredMaintenance ForThisPc() => new(SystemTaskChannel.ForThisPc(),
        text => NativeMethods.AskYesNo(text, "Lumen — помощник администратора", AdminMaintenance.ConfirmTimeout),
        id => { var p = SystemCleaner.Plan(id, Environment.SystemDirectory, AdminStore.ProgramDataDir); return p == null ? 0 : SystemCleaner.Estimate(p.Value.Roots, p.Value.MinAge, DateTime.UtcNow); });

    private static string NewNonce() => Guid.NewGuid().ToString("N");

    private static MaintenanceResult Declined(ConfirmAnswer a, string text) => a == ConfirmAnswer.Timeout
        ? new(false, "Нет ответа в окне помощника за 5 минут — считаю «Нет». " + text, AdminCodes.DeclinedTimeout)
        : new(false, text, AdminCodes.Declined);

    private static MaintenanceResult From(JobResult r) => new(r.Ok, r.Message, r.Code);

    public MaintenanceResult CreateRestorePoint() =>
        From(channel.Run(NewNonce(), AdminJobKind.RestorePoint, "", AdminMaintenance.RestoreStep));

    /// Только чтение, без окна: читает SYSTEM-исполнитель (у него свой HKCU — подложить COM/модули из профиля пользователя нельзя).
    public MaintenanceResult ReadDiskDetails() =>
        From(channel.Run(NewNonce(), AdminJobKind.DiskDetails, "", AdminMaintenance.DiskDetailsStep));

    public MaintenanceResult InstallDrivers(IReadOnlyList<string> updateIds, bool skipRestore)
    {
        var s = channel.Run(NewNonce(), AdminJobKind.DriverSearch, string.Join(",", updateIds), AdminMaintenance.SearchStep);
        if (!s.Ok) return From(s);
        var ids = s.Ids ?? Array.Empty<string>();
        if (ids.Length == 0) return From(s);                                   // «выбранных обновлений больше нет» — спрашивать нечего
        var titles = ids.Select((_, i) => s.Titles != null && i < s.Titles.Length ? s.Titles[i] : "").ToList();
        var answer = confirm(DriverInstaller.ConfirmText(titles, skipRestore));
        if (answer != ConfirmAnswer.Yes) return Declined(answer, DriverInstaller.DeclinedMessage);
        return From(channel.Run(NewNonce(), AdminJobKind.DriverInstall, AdminMaintenance.DriverArg(ids, skipRestore), AdminMaintenance.DriverInstallStep));
    }

    public const string RepairConfirmText = "Lumen просит проверить и починить системные файлы Windows (DISM /RestoreHealth, затем sfc /scannow).\n\nЭто займёт 10–45 минут, нужен интернет. Компьютером можно пользоваться. Перезагрузка сама не начнётся.\n\nНачать?";
    public const string RepairDeclinedMessage = "Ты отказался в окне помощника — починка Windows не запускалась";

    /// Как и очистка: окно «Да/Нет» самого помощника (авто-«Нет» через 5 минут), и только после «Да» — SYSTEM-задание без аргументов.
    public MaintenanceResult RepairSystem()
    {
        var answer = confirm(RepairConfirmText);
        if (answer != ConfirmAnswer.Yes) return Declined(answer, RepairDeclinedMessage);
        return From(channel.Run(NewNonce(), AdminJobKind.RepairSystem, "", AdminMaintenance.RepairStep));
    }

    public MaintenanceResult CleanSystem(string cleanId)
    {
        if (!AdminMaintenance.IsCleanId(cleanId)) return new(false, "неизвестный вид очистки");
        var answer = confirm(SystemCleaner.ConfirmText(cleanId, estimate?.Invoke(cleanId) ?? 0));
        if (answer != ConfirmAnswer.Yes) return Declined(answer, SystemCleaner.DeclinedMessage);
        return From(channel.Run(NewNonce(), AdminJobKind.CleanSystem, cleanId,
                                cleanId == AdminMaintenance.ComponentCleanup ? AdminMaintenance.ComponentStep : AdminMaintenance.CleanStep));
    }
}
