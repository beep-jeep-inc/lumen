using Pult.App.Services;
using Pult.App.Views;
using Pult.Core.Admin;
using Pult.Core.Maintenance;

namespace Pult.App.ViewModels;

/// «Драйверы»: видеокарты и устройства с ошибками читаются при открытии страницы; поиск в Центре обновления — по кнопке; установка — только отмеченного.
public sealed class DriversSection : SectionVm
{
    private readonly WuaSearch _wua = new();
    private IReadOnlyList<GpuDriver> _gpus = Array.Empty<GpuDriver>();
    private IReadOnlyList<DeviceProblem> _problems = Array.Empty<DeviceProblem>();
    private bool _cheapDone;
    private readonly WuaUpgradeSearch _upgradeSearch = new();
    private RowVm? _upgradeRow;               // «Доступно обновление Windows» (не отмечаемая), null — нет или не искали
    private List<RowVm>? _wuaRows;           // null — поиск ещё не делали
    private readonly ActionVm _install;
    // Галочка «Создать точку восстановления» — по умолчанию включена при каждой установке, выбор не запоминается.
    private readonly RowVm _restoreRow = new() { Title = Strings.RestoreBeforeInstall, Detail = Strings.RestoreBeforeInstallHint, Checkable = true, CanCheck = true, IsChecked = true };

    protected override int? PendingCount => _wuaRows?.Count;
    protected override string TileManyFor(int n) => TileCaptions.Drivers(n);
    protected override string TileNone => "новых драйверов нет";
    public override string UncheckedText => Strings.TileHeavyDrivers;
    private int FirmwareCount => _wuaRows?.Count(r => r.Tag is DriverUpdate { IsFirmware: true }) ?? 0;
    public override string TileCaption => base.TileCaption + (FirmwareCount > 0 ? "\n" + TileCaptions.BiosCareful : "");
    /// Золото — только если есть обычные драйверы; одна прошивка BIOS «делом» не считается (её ставят осторожно и не всем).
    public override string TileState => PendingCount is null ? "Off" : PendingCount == 0 ? "Ok" : PendingCount > FirmwareCount ? "Warn" : "Info";

    public DriversSection(MaintenanceVm host) : base(host, "🧩", Strings.SecDrivers, Strings.SecDriversHint, Strings.CheckDrivers)
    {
        _install = AddAction(Strings.InstallChecked, InstallAsync, true, () => Selected().Count > 0);
        AddRebootActions("после установки драйверов");
        Actions.Add(UpdateHelperAction);
        Rebuild();
    }

    public async Task LoadCheapAsync()
    {
        var (gpus, problems) = await Task.Run(() => (Drivers.Gpus(), DeviceErrors.Current()));
        Apply(gpus, problems);
    }

    public void Apply(IReadOnlyList<GpuDriver> gpus, IReadOnlyList<DeviceProblem> problems)
    {
        _gpus = gpus; _problems = problems; _cheapDone = true; Rebuild();
    }

    private List<DriverUpdate> Selected() => (_wuaRows ?? new()).Where(r => r.IsChecked).Select(r => (DriverUpdate)r.Tag!).ToList();
    private void UpdateLabels() => _install.Text = Selected() is { Count: > 0 } s ? $"{Strings.InstallChecked} ({s.Count})" : Strings.InstallChecked;

    private void Rebuild()
    {
        Items.Clear();
        Items.Add(new HeaderVm(Strings.Gpus));
        if (!_cheapDone) Items.Add(new RowVm { Title = Strings.Reading });
        else if (_gpus.Count == 0) Items.Add(new RowVm { Title = Strings.NoGpu });
        foreach (var g in _gpus)
        {
            var row = new RowVm { Title = g.Name, Detail = $"версия {(g.Version.Length > 0 ? g.Version : "неизвестна")}" + (g.Date is { } d ? $" · от {d:dd.MM.yyyy}" : "") };
            if (g.Old) row.SetPill(Strings.OldDriver, "Warn"); else row.SetPill(Strings.DriverFine, "Ok");
            var url = g.VendorUrl;
            row.Buttons.Add(new ActionVm(g.VendorUrl.StartsWith("ms-settings") ? Strings.OpenWindowsUpdate : Strings.OpenVendorApp + g.VendorAppHint, RelayCommand.Sync(() => MaintenanceVm.OpenUrl(url))));
            Items.Add(row);
        }
        if (_cheapDone)
        {
            Items.Add(new HeaderVm(Strings.DeviceErrors, Strings.DeviceErrorsSub));
            if (_problems.Count == 0) { var ok = new RowVm { Title = Strings.NoDeviceErrors }; ok.SetPill("✓", "Ok"); Items.Add(ok); }
            foreach (var p in _problems)
            {
                var row = new RowVm { Title = p.Name, Detail = p.Explain };
                row.SetPill($"код {p.Code}", "Warn"); Items.Add(row);
            }
        }
        Items.Add(new HeaderVm(Strings.WuDrivers, Strings.WuDriversSub));
        if (_upgradeRow != null) Items.Add(_upgradeRow);
        var regular = (_wuaRows ?? new()).Where(r => r.Tag is not DriverUpdate { IsFirmware: true }).ToList();
        var firmware = (_wuaRows ?? new()).Where(r => r.Tag is DriverUpdate { IsFirmware: true }).ToList();
        if (_wuaRows == null) Items.Add(new RowVm { Title = Strings.WuNotChecked });
        else if (regular.Count == 0) { var ok = new RowVm { Title = Strings.WuNone }; ok.SetPill("✓", "Ok"); Items.Add(ok); }
        else foreach (var r in regular) Items.Add(r);
        // Прошивка BIOS — отдельным блоком с заметным предупреждением: обычным драйверам она не нужна, ставят её осторожно и не все.
        if (firmware.Count > 0)
        {
            Items.Add(new HeaderVm(Strings.FirmwareBlock, Strings.FirmwareBlockSub));
            Items.Add(new NoticeVm(Strings.FirmwareNoticeTitle, Strings.FirmwareNotice));
            foreach (var r in firmware) Items.Add(r);
        }
        if (_wuaRows is { Count: > 0 }) Items.Add(_restoreRow);
        UpdateLabels();
    }

    protected override Task CheckAsync() => RunBusyAsync(Strings.WuSearching, async (ct, progress) =>
    {
        UpdateHelperAction.Visible = false;
        var upgradeTask = SearchUpgradeAsync(ct);             // параллельно с драйверами; сбой не мешает драйверам
        var list = await _wua.SearchDriversAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (_wua.LastError != null) { SetResult(Strings.WuFailed + _wua.LastError, true); return; }
        _upgradeRow = await upgradeTask;
        _wuaRows = list.Select(d => MakeRow(d)).ToList();
        Rebuild();
        SetResult(list.Count == 0 ? Strings.WuNone : string.Format(Strings.WuFound, list.Count),
                  notes: list.Count > 0 && !App.Services.Admin.CanElevate ? Strings.AdminNoAccount : "");   // ставить драйверы может только администратор
    });

    private async Task<RowVm?> SearchUpgradeAsync(CancellationToken ct)
    {
        try
        {
            var found = await _upgradeSearch.SearchAsync(ct);
            if (_upgradeSearch.LastError != null) { Log.Error("Поиск обновления Windows: " + _upgradeSearch.LastError); return null; }
            return found.Count > 0 ? MakeUpgradeRow(found[0], WindowsVersion.Current()) : null;
        }
        catch (Exception e) { Log.Error("Поиск обновления Windows", e); return null; }
    }

    private static RowVm MakeUpgradeRow(WindowsUpgrade u, string? current)
    {
        var row = new RowVm { Title = string.Format(Strings.WinUpgradeTitle, u.Title), Detail = string.Format(Strings.WinUpgradeDetail, current ?? Strings.WinUpgradeUnknownCurrent) };
        row.Buttons.Add(new ActionVm(Strings.OpenWindowsUpdate, RelayCommand.Sync(() => MaintenanceVm.OpenUrl("ms-settings:windowsupdate"))));
        return row;
    }

    private RowVm MakeRow(DriverUpdate d)
    {
        var detail = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.DriverModel)) detail.Add(d.DriverModel!);
        if (d.CurrentVersion != null && d.DriverVersion != null) detail.Add($"{d.CurrentVersion} → {d.DriverVersion}");
        else if (d.DriverVersion != null) detail.Add($"новая версия {d.DriverVersion}");
        if (d.DriverDate is { } dt) detail.Add($"от {dt:dd.MM.yyyy}");
        if (RebootTag(d.RebootBehavior) is { } tag) detail.Add(tag);
        var text = string.Join(" · ", detail);
        if (d.IsFirmware) text = (text.Length > 0 ? text + "\n" : "") + Strings.FirmwareWarning;
        // Прошивка по умолчанию НЕ отмечена (обычные драйверы тоже не отмечаются сами — выбор за пользователем).
        var row = new RowVm { Title = d.Title, Tag = d, Checkable = true, CanCheck = true, Detail = text, Changed = UpdateLabels };
        if (d.IsFirmware) row.SetPill("BIOS", "Warn");
        return row;
    }

    private async Task InstallAsync()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var skipRestore = !_restoreRow.IsChecked;
        var firmware = sel.Any(s => s.IsFirmware);
        if (firmware && PowerLine.IsOnBattery())
        { SetResult(Strings.FirmwareOnBattery, true); return; }
        if (!App.Services.Admin.CanElevate) { SetResult(Strings.AdminNoAccount, true); return; }
        var ask = string.Format(skipRestore ? Strings.DriverConfirmNoRestore : Strings.DriverConfirm, sel.Count, MaintSummaryPlural(sel.Count), RebootAsk(sel));
        if (firmware) ask += "\n\n" + Strings.FirmwareConfirm;
        if (!ConfirmDialog.Ask(ask)) return;
        ClearResult(); OfferReboot(false);
        if (!await AdminSetup.EnsureInstalledAsync()) { SetResult(Strings.HelperNeededDrivers, true); return; }
        await RunBusyAsync(Strings.DriversInstalling, async (ct, progress) =>
        {
            // Точку восстановления и окно подтверждения делает сам помощник; повторный поиск — тоже его (он ставит только пересечение с найденным).
            var r = await App.Services.Admin.SendAsync(AdminAction.DriverInstall, AdminMaintenance.DriverArg(sel.Select(s => s.UpdateId), skipRestore), ct, Strings.DriversInstalling, progress);
            if (r.Ok)
            {
                // Помощник мог поставить не всё отмеченное (только пересечение с найденным им) — список берём заново, а не вычёркиваем отмеченное.
                var fresh = await SearchAgainAsync(ct);
                if (fresh != null) _wuaRows = fresh;
                else foreach (var row in (_wuaRows ?? new()).Where(x => x.IsChecked).ToList()) _wuaRows!.Remove(row);
                Rebuild();
                var reboot = r.Code == AdminCodes.RebootRequired;
                SetResult(string.IsNullOrWhiteSpace(r.Message) ? Strings.DriversDone : r.Message, false, reboot ? Strings.RebootNeeded : Strings.RebootNotNeededDone);
                OfferReboot(reboot);
            }
            else { var (t, err) = DescribeAdmin(r, ""); SetResult(t, err); }
        });
        _restoreRow.IsChecked = true;                       // выбор не запоминается: в следующий раз точка снова включена
    }

    /// Перед установкой: перезагрузка не нужна вовсе или может понадобиться для таких-то (по данным Центра обновления).
    private static string RebootAsk(IReadOnlyList<DriverUpdate> sel)
    {
        var need = sel.Where(s => s.MayNeedReboot).Select(s => s.Title).ToList();
        return need.Count == 0 ? Strings.RebootNotNeededAsk : string.Format(Strings.RebootMaybeAsk, Drivers.TitleList(need));
    }

    private static string? RebootTag(int? b) => b switch { 0 => Strings.RebootTagNever, 1 => Strings.RebootTagAlways, 2 => Strings.RebootTagMaybe, _ => null };

    private async Task<List<RowVm>?> SearchAgainAsync(CancellationToken ct)
    {
        try
        {
            var list = await _wua.SearchDriversAsync(ct);
            return _wua.LastError != null ? null : list.Select(d => MakeRow(d)).ToList();
        }
        catch (OperationCanceledException) { return null; }
    }

    private static string MaintSummaryPlural(int n) => RuText.Plural(n, "драйвер", "драйвера", "драйверов");

    // --- режим предпросмотра ---
    public void LoadDemo()
    {
        Apply(new[]
        {
            Drivers.MakeGpu("NVIDIA GeForce RTX 3060", "31.0.15.3623", new DateTime(2023, 8, 9), DateTime.Now),
            Drivers.MakeGpu("Intel(R) UHD Graphics 630", "31.0.101.5186", new DateTime(2026, 3, 2), DateTime.Now),
        }, new[] { new DeviceProblem("Неизвестное устройство", 28, DeviceErrors.Explain(28)) });
        _wuaRows = new[]
        {
            new DriverUpdate("11111111-2222-3333-4444-555555555555", "Intel - System - 2.3.20306.4", "Intel Management Engine Interface", "2.3.20306.4", new DateTime(2026, 7, 14), "2.3.19100.1", RebootBehavior: 0),
            new DriverUpdate("11111111-2222-3333-4444-666666666666", "Realtek Semiconductor Corp. - MEDIA - 6.0.9612.1", "Realtek High Definition Audio", "6.0.9612.1", new DateTime(2026, 5, 2), "6.0.9300.1", RebootBehavior: 1),
            new DriverUpdate("11111111-2222-3333-4444-777777777777", "MediaTek, Inc. - Net - 3.5.0.1024", "MediaTek Wi-Fi 6 MT7921", "3.5.0.1024", new DateTime(2026, 6, 20), "3.4.0.900", RebootBehavior: 2),
            new DriverUpdate("11111111-2222-3333-4444-888888888888", "Micro-Star International Co., Ltd. Firmware Driver Update (1.0.1.5)", "MS-17L4 SBIOS Ver.115", "1.0.1.5", new DateTime(2026, 6, 1), null, "Firmware", "uefi\\res_{a2f798b0-2a3f-40ad-a6aa-f58548b5a4ed}"),
        }.Select(MakeRow).ToList();
        _upgradeRow = MakeUpgradeRow(new WindowsUpgrade("Windows 11, version 26H2"), "25H2 (сборка 26200.9457)");
        _wuaRows[0].IsChecked = true; _wuaRows[1].IsChecked = true;
        Rebuild();
        SetResult("Драйверы: установлено 2, с ошибками 0; нужна перезагрузка", false, Strings.RebootNeeded);
        OfferReboot(true);
    }
}
