using System.IO;
using Pult.App.Services;
using Pult.Core.Admin;
using Pult.Core.Maintenance;
using Pult.Core.Startup;

namespace Pult.App.ViewModels;

/// «Автозагрузка»: список читается по кнопке (и один раз при открытии — это только чтение реестра). Переключатель пишет только флаг
/// Explorer\StartupApproved — запись не удаляется. Свои источники (HKCU, своя папка) Пульт переключает сам, машинные — через помощника.
public sealed class StartupSection : SectionVm
{
    private sealed class Item(StartupEntry e) { public StartupEntry E { get; } = e; public bool On { get; set; } = e.Enabled; }
    private readonly IStartupAccess _access;
    private List<Item>? _items;

    protected override int? PendingCount => _items?.Count(i => i.On && !i.E.IsSelf);
    protected override string TileManyFor(int n) => TileCaptions.Startup(n);
    protected override string TileNone => "ничего не запускается";
    protected override string TileUnknown => "список не смотрели";
    protected override bool ShowBadge => false;
    public override string TileState => PendingCount is null ? "Off" : "Info";

    public StartupSection(MaintenanceVm host, IStartupAccess? access = null) : base(host, "🚀", Strings.SecStartup, Strings.SecStartupHint)
    {
        _access = access ?? RegistryStartupAccess.ForThisPc();
        Actions.Add(UpdateHelperAction);
        EmptyText = Strings.StartupNotRead;
    }

    private IReadOnlyList<StartupEntry> ScanNow() =>
        StartupScanner.Scan(_access, StartupFileInfo.Describe, Environment.ExpandEnvironmentVariables, Environment.ProcessPath, File.Exists);

    /// При открытии окна: только чтение реестра и свойств файлов, без запросов к помощнику.
    public async Task LoadCheapAsync()
    {
        try { Apply(await Task.Run(ScanNow)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log.Error("автозагрузка", e); }
    }

    protected override Task CheckAsync() => RunBusyAsync(Strings.StartupReading, async (ct, progress) => Apply(await Task.Run(ScanNow, ct)));

    public void Apply(IReadOnlyList<StartupEntry> entries)
    {
        _items = entries.Select(e => new Item(e)).ToList();
        Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        var items = _items ?? new();
        foreach (var src in StartupSources.All)
        {
            var group = items.Where(i => i.E.Source == src).ToList();
            if (group.Count == 0) continue;
            Items.Add(new HeaderVm(StartupSources.Title(src), StartupSources.IsMachineWide(src) ? "меняет помощник администратора" : ""));
            foreach (var it in group) Items.Add(MakeRow(it));
        }
        EmptyText = items.Count == 0 ? Strings.StartupEmpty : "";
        Summarize();
    }

    private void Summarize()
    {
        var items = _items ?? new();
        var on = items.Count(i => i.On && !i.E.IsSelf);
        var total = items.Count(i => !i.E.IsSelf);
        SetResult(items.Count == 0 ? "" : $"Запускается вместе с Windows: {on} из {total}.", false, items.Count == 0 ? "" : Strings.StartupNotes);
    }

    private RowVm MakeRow(Item it)
    {
        var e = it.E;
        var machine = StartupSources.IsMachineWide(e.Source);
        var lines = new List<string>();
        var who = string.Join(" · ", new[] { e.Description, e.Publisher }.Where(x => x.Length > 0).Distinct());
        if (who.Length > 0) lines.Add(who);
        var path = e.ExePath ?? e.Command;
        if (path.Length > 0) lines.Add(StartupCommand.Shorten(path));
        if (e.Missing) lines.Add(Strings.StartupMissingNote);
        if (StartupHealth.Important(e) is { } imp) lines.Add(string.Format(Strings.StartupImportantLine, imp.Kind));
        var row = new RowVm
        {
            Title = e.Name, Detail = e.IsSelf ? Strings.StartupSelfDetail : string.Join("\n", lines),
            Shield = machine && !e.IsSelf, ShieldTip = Strings.StartupShieldTip, UseToggle = !e.IsSelf,
        };
        if (e.IsSelf) { row.SetPill(Strings.StartupSelf, "Off"); return row; }
        return Bind(row, it);
    }

    private RowVm Bind(RowVm row, Item it)
    {
        ActionVm? btn = null;
        btn = new ActionVm(it.On ? Strings.StartupDoOff : Strings.StartupDoOn, new RelayCommand(() => GuardAsync(() => ToggleAsync(it, row, btn!)), () => CanRun));
        row.Buttons.Add(btn);
        Paint(row, it, btn);
        return row;
    }

    private static void Paint(RowVm row, Item it, ActionVm btn)
    {
        // Состояние показывает переключатель и подпись «Включено/Выключено» рядом с ним; бейдж остаётся только для «Файл не найден»
        row.ToggleOn = it.On; row.ToggleText = it.On ? Strings.StartupOn : Strings.StartupOff;
        if (it.On && it.E.Missing) row.SetPill(Strings.StartupMissingPill, "Warn"); else row.SetPill("", "Off");
        btn.Text = it.On ? Strings.StartupDoOff : Strings.StartupDoOn;
    }

    private async Task ToggleAsync(Item it, RowVm row, ActionVm btn)
    {
        var e = it.E; var target = !it.On;
        // Выключение «важной» записи (часть Windows или драйвера) — только после вопроса: что может пропасть. Включение вопросов не требует.
        if (!target && StartupHealth.Important(e) is { } imp && !Views.ConfirmDialog.Ask(StartupHealth.ConfirmText(e, imp))) return;
        row.ToggleBusy = true;                                           // подпись «Меняю…» под переключателем; положение меняет только успешная команда
        try { await ToggleCoreAsync(it, row, btn, e, target); }
        finally { row.ToggleBusy = false; }
    }

    private async Task ToggleCoreAsync(Item it, RowVm row, ActionVm btn, StartupEntry e, bool target)
    {
        if (StartupSources.IsMachineWide(e.Source))
        {
            if (!await AdminSetup.EnsureInstalledAsync()) { SetResult(Strings.StartupHelperNeeded, true); return; }
            UpdateHelperAction.Visible = false;
            await RunBusyAsync(Strings.StartupConfirmInHelper, async (ct, progress) =>
            {
                var r = await App.Services.Admin.SendAsync(AdminAction.StartupToggle, StartupArg.Build(e.Source, target, e.Name), ct, Strings.StartupConfirmInHelper, progress);
                if (r.Ok) { it.On = target; Paint(row, it, btn); Summarize(); RaiseTile(); }
                else { var (text, err) = DescribeAdmin(r, ""); SetResult($"«{e.Name}»: {text}", err); }
            });
            return;
        }
        var res = await Task.Run(() => StartupToggler.Toggle(_access, e.Source, e.Name, target, DateTime.UtcNow, StartupSources.UserSources));
        if (res.Ok) { it.On = target; Paint(row, it, btn); Summarize(); RaiseTile(); }
        else SetResult($"«{e.Name}»: {res.Message}", true);
    }

    public void LoadDemo()
    {
        Apply(new[]
        {
            new StartupEntry(StartupSource.HkcuRun, "Discord", @"""C:\Users\User\AppData\Local\Discord\Update.exe"" --processStart Discord.exe", @"C:\Users\User\AppData\Local\Discord\Update.exe", true, false, "Discord", "Discord Inc."),
            new StartupEntry(StartupSource.HkcuRun, "Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", @"C:\Program Files (x86)\Steam\steam.exe", true, false, "Steam Client Bootstrapper", "Valve Corporation"),
            new StartupEntry(StartupSource.HkcuRun, "Telegram", @"""C:\Users\User\AppData\Roaming\Telegram Desktop\Telegram.exe"" -autostart", @"C:\Users\User\AppData\Roaming\Telegram Desktop\Telegram.exe", false, false, "Telegram Desktop", "Telegram FZ-LLC"),
            new StartupEntry(StartupSource.HkcuRun, "Pult", @"""C:\Program Files\Pult\Pult.exe"" --minimized", @"C:\Program Files\Pult\Pult.exe", true, true, "Lumen запуска", ""),
            new StartupEntry(StartupSource.HklmRun, "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe", @"C:\Windows\system32\SecurityHealthSystray.exe", true, false, "Windows Security notification icon", "Microsoft Corporation"),
            new StartupEntry(StartupSource.HklmRun, "RtkAudUService", @"""C:\Windows\System32\DriverStore\FileRepository\realtekservice.inf_amd64_a1b2c3\RtkAudUService64.exe"" -background", @"C:\Windows\System32\DriverStore\FileRepository\realtekservice.inf_amd64_a1b2c3\RtkAudUService64.exe", true, false, "Realtek HD Audio Universal Service", "Realtek Semiconductor"),
            new StartupEntry(StartupSource.HklmRun32, "AdobeAAMUpdater", @"""C:\Program Files (x86)\Common Files\Adobe\OOBE\PDApp\UWA\UpdaterStartupUtility.exe""", @"C:\Program Files (x86)\Common Files\Adobe\OOBE\PDApp\UWA\UpdaterStartupUtility.exe", false, false, "Adobe Updater Startup Utility", "Adobe Inc."),
            new StartupEntry(StartupSource.HkcuRun, "TgWsProxy", @"""C:\Users\User\Desktop\Новая папка\zapret\TgWsProxy.exe""", @"C:\Users\User\Desktop\Новая папка\zapret\TgWsProxy.exe", true, false, "", "", true),
            new StartupEntry(StartupSource.UserFolder, "OneNote.lnk", @"C:\Users\User\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\OneNote.lnk", null, true, false, "", ""),
        });
    }
}

/// «Здоровье дисков»: по кнопке — состояние, износ, температура и ошибки из Windows (CIM). Без помощника; сбой — строка итога, не падение.
public sealed class DisksSection : SectionVm
{
    private List<(DiskInfo Disk, DiskVerdict Verdict)>? _disks;

    protected override int? PendingCount => _disks == null ? null : DiskHealth.NeedAttention(_disks.Select(d => d.Verdict));
    protected override string TileManyFor(int n) => TileCaptions.Disks(n);
    protected override string TileNone => "все диски исправны";
    protected override string TileUnknown => "проверка по кнопке";

    public DisksSection(MaintenanceVm host) : base(host, "💽", Strings.SecDisks, Strings.SecDisksHint)
    {
        EmptyText = Strings.DisksNotRead;
        AddAction(Strings.DisksDetails, DetailsAsync, false, () => _disks is { Count: > 0 });
    }

    /// Только чтение через помощника: счётчики приходят числами, Пульт проверяет их ещё раз (DiskDetailsProtocol) и дописывает к уже показанным дискам.
    private Task DetailsAsync() => RunBusyAsync(Strings.DisksDetailsAsk, async (ct, progress) =>
    {
        var installed = await Task.Run(() => App.Services.Admin.IsInstalled);
        if (!installed) { SetResult(Strings.DisksNoHelper); return; }
        var r = await App.Services.Admin.SendAsync(Pult.Core.Admin.AdminAction.DiskDetails, "", ct);
        if (!r.Ok || r.Code != DiskDetailsProtocol.Code) { var (t, err) = DescribeAdmin(r, Strings.DisksDetailsOk); SetResult(t, err); return; }
        var counters = DiskDetailsProtocol.Parse(r.Message);
        if (counters.Count == 0 || _disks == null) { SetResult(Strings.DisksDetailsEmpty, true); return; }
        Apply(new DiskReadResult(DiskDetailsProtocol.Merge(_disks.Select(d => d.Disk).ToList(), counters), true, null));
        SetResult(Result.Length > 0 ? Result + " " + Strings.DisksDetailsOk : Strings.DisksDetailsOk, ResultIsError);
    });

    protected override Task CheckAsync() => RunBusyAsync(Strings.DisksReading, async (ct, progress) =>
    {
        var r = await DiskReader.ReadAsync(TimeSpan.FromSeconds(45), ct);
        Apply(r);
    });

    public void Apply(DiskReadResult r)
    {
        Items.Clear();
        if (r.Error != null) { _disks = null; EmptyText = ""; SetResult(r.Error, true); RaiseTile(); return; }
        _disks = r.Disks.Select(d => (d, DiskHealth.Assess(d))).ToList();
        foreach (var (d, v) in _disks) Items.Add(MakeRow(d, v));
        EmptyText = _disks.Count == 0 ? Strings.DisksNone : "";
        var bad = DiskHealth.NeedAttention(_disks.Select(x => x.Verdict));
        var res = _disks.Count == 0 ? "" : bad == 0 ? "Все диски исправны." : $"Требуют внимания: {bad} из {_disks.Count}.";
        SetResult(res, bad > 0, r.CountersAvailable || _disks.Count == 0 ? "" : Strings.DisksNoCounters);
        RaiseTile();
    }

    private static RowVm MakeRow(DiskInfo d, DiskVerdict v)
    {
        var lines = new List<string>();
        if (v.State != DiskState.Healthy) lines.Add(v.Summary);
        var facts = new List<string> { d.MediaType };
        if (d.SizeBytes > 0) facts.Add(SizeFormat.Human(d.SizeBytes));
        if (d.Wear is { } w) facts.Add($"износ {w}%");
        if (d.TemperatureC is { } t) facts.Add($"{t} °C");
        if (d.PowerOnHours is { } h) facts.Add($"работал {h:N0} ч");
        if (d.ReadErrors is { } e) facts.Add($"ошибок чтения: {e}");
        if (d.WriteErrors is { } we) facts.Add($"ошибок записи: {we}");
        lines.Add(string.Join(" · ", facts));
        lines.AddRange(DiskVolumes.Describe(d.Volumes));
        var row = new RowVm { Title = d.Name, Detail = string.Join("\n", lines) };
        switch (v.State)
        {
            case DiskState.Healthy: row.SetPill("Исправен", "Ok"); break;
            case DiskState.Warning: row.SetPill("Внимание", "Warn"); break;
            case DiskState.Unhealthy: row.SetPill("Неисправность", "Err"); break;
            default: row.SetPill("Нет данных", "Off"); break;
        }
        return row;
    }

    public void LoadDemo() => Apply(new DiskReadResult(new[]
    {
        new DiskInfo("Samsung SSD 980 PRO 1TB", "SSD", 1_000_204_886_016, 0, new[] { 2 }, 6, 38, 0, 1240, "0",
                     new[] { new VolumeUsage("C", 930_000_000_000, 412_000_000_000) }, 0),
        new DiskInfo("WDC WD20EZRZ-00Z5HB0", "HDD", 2_000_398_934_016, 0, new[] { 2 }, null, 41, 0, 29_800, "1",
                     new[] { new VolumeUsage("D", 1_800_000_000_000, 260_000_000_000), new VolumeUsage("E", 60_000_000_000, 41_000_000_000) }, 0),
        new DiskInfo("KINGSTON SA400S37480G", "SSD", 480_103_981_056, 0, new[] { 2 }, 87, 44, 0, 18_300, "2",
                     new[] { new VolumeUsage("F", 447_000_000_000, 14_000_000_000) }, 3),
    }, true, null));
}
