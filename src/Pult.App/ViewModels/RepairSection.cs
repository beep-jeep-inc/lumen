using Pult.App.Services;
using Pult.App.Views;
using Pult.Core.Admin;
using Pult.Core.Maintenance;
using Pult.Core.Monitoring;

namespace Pult.App.ViewModels;

/// «Починка Windows»: одна кнопка — DISM /RestoreHealth, затем sfc /scannow через помощника (SYSTEM-задание, как очистка хранилища компонентов).
/// Только по кнопке и после вопроса. Остановить из Пульта нельзя (помощник уже делает дело) — кнопки «Отмена» нет, и это сказано в тексте.
public sealed class RepairSection : SectionVm
{
    private enum Last { None, Clean, Reboot, Problem }
    private Last _last = Last.None;
    private readonly ActionVm _start;

    protected override int? PendingCount => _last switch { Last.None => null, Last.Clean => 0, _ => 1 };
    protected override string TileMany => _last == Last.Reboot ? Strings.RepairTileReboot : Strings.RepairTileMany;
    protected override string TileNone => Strings.RepairTileNone;
    protected override string TileUnknown => Strings.RepairTileUnknown;
    protected override bool ShowBadge => false;
    public override bool HasCheck => false;
    public override string UncheckedText => Strings.TileRepairNotRun;

    public RepairSection(MaintenanceVm host) : base(host, "🛠", Strings.SecRepair, Strings.SecRepairHint)
    {
        _start = AddAction(Strings.RepairStart, StartAsync, true);
        AddRebootActions("после починки Windows");
        Actions.Add(UpdateHelperAction);
        EmptyText = Strings.RepairNotRun; PinEmptyText = true;     // описание (сколько займёт, нужен интернет, остановить нельзя) остаётся над итогом
    }

    /// Кнопки «Проверить» у раздела нет, и сама проверка ничего не запускает.
    protected override Task CheckAsync() => Task.CompletedTask;

    private async Task StartAsync()
    {
        if (!App.Services.Admin.CanElevate) { SetResult(Strings.AdminNoAccount, true); return; }
        // Идёт ли уже sfc/DISM (в том числе запущенный раньше помощником, если Пульт перезапускали): второй запуск не нужен.
        if (await Task.Run(() => { var q = new SystemProcessQuery(); q.Snapshot(); return SystemRepair.AnotherRunning(q); }))
        { SetResult(Strings.RepairAlreadyRunning, true); return; }
        if (!ConfirmDialog.Ask(Strings.RepairConfirm)) return;
        if (!await AdminSetup.EnsureInstalledAsync()) { SetResult(Strings.AdminNotInstalled, true); return; }
        UpdateHelperAction.Visible = false; OfferReboot(false); _start.Primary = true;
        await RunBusyAsync(Strings.RepairRunning, async (_, progress) =>
        {
            // Без токена отмены: ожидание — до конца (помощник всё равно доделает), так второй запуск до его окончания невозможен.
            var r = await App.Services.Admin.SendAsync(AdminAction.RepairSystem, "", CancellationToken.None, Strings.RepairRunning, progress);
            Apply(r);
        }, cancellable: false);
    }

    private void Apply(AdminResponse r)
    {
        // «Нет» в окне помощника, нет ответа, помощник занят/не установлен: починка не запускалась — это не проблема Windows, плитку не трогаем.
        if (r.Code is AdminCodes.Declined or AdminCodes.DeclinedTimeout or AdminCodes.Busy or AdminCodes.NotInstalled or AdminCodes.NoAdminAccount or AdminCodes.HelperGone)
        {
            var (t, e) = DescribeAdmin(r, "");
            SetResult(t, e); OfferReboot(false); _start.Primary = true; RaiseTile();
            return;
        }
        var reboot = r.Code == AdminCodes.RebootRequired;
        if (r.Ok)
        {
            _last = reboot ? Last.Reboot : Last.Clean;
            SetResult(string.IsNullOrWhiteSpace(r.Message) ? Strings.RepairTileNone : r.Message, false, reboot ? Strings.RepairRebootNote : "");
        }
        else if (r.Code == AdminCodes.RepairUnfixable)
        {
            _last = Last.Problem; SetResult(r.Message, true, Strings.RepairLogNote);
        }
        else if (reboot)
        {
            _last = Last.Reboot; SetResult(r.Message, true);
        }
        else
        {
            var (text, err) = DescribeAdmin(r, "");
            _last = Last.Problem; SetResult(text, err);
        }
        OfferReboot(reboot); _start.Primary = !reboot;      // когда предложена перезагрузка, главная кнопка — она
        RaiseTile();
    }

    public void LoadDemo()
    {
        _last = Last.Reboot;
        SetResult("Повреждения найдены и исправлены. Перезагрузите ПК, чтобы изменения вступили в силу.", false);
        OfferReboot(true); _start.Primary = false;
        RaiseTile();
    }
}
