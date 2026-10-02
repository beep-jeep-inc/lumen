using Pult.Core.Admin;
using Pult.App.Services;
using Pult.App.Views;
using Pult.Core.Maintenance;
using Pult.Core.Monitoring;

namespace Pult.App.ViewModels;

public sealed record PkgJob(string Id, string Name, bool Install, WingetRow? Row, RowVm Row_);

/// Общее для «Системных библиотек» и «Программ»: один поиск в winget на оба списка, обновление по одной программе с прогрессом.
public abstract class WingetSection : SectionVm
{
    protected readonly ActionVm StoreAction;
    protected bool _jobsRunning, _pendingRebuild;

    protected WingetSection(MaintenanceVm host, string icon, string title, string hint) : base(host, icon, title, hint)
    {
        StoreAction = new ActionVm(Strings.InstallAppInstaller, RelayCommand.Sync(() => MaintenanceVm.OpenUrl(Strings.AppInstallerUrl)), false, false);
        Host.Winget.Done += OnScanDone;
    }

    private void OnScanDone()
    {
        StoreAction.Visible = Host.Winget.Missing;
        if (_jobsRunning) { _pendingRebuild = true; return; }            // список не трогаем посреди установки: перестроим по её окончании
        if (Host.Winget.Error != null) { if (Host.Winget.Missing || IsBusy) SetResult(Host.Winget.Error, true); return; }
        _failures.Clear();                                                // новая проверка — прежние неудачи неактуальны
        ApplyScan();
    }

    protected abstract void ApplyScan();
    /// Перестроить список по уже известным данным, не трогая строку итога.
    protected abstract void RebuildQuiet();

    protected override Task CheckAsync() => RunBusyAsync(Strings.WingetChecking, async (ct, progress) =>
    {
        if (Host.Winget.Busy) { SetResult(Strings.WingetBusy); return; }
        await Host.Winget.RunAsync(ct, progress);
        ct.ThrowIfCancellationRequested();
        if (Host.Winget.Error != null) SetResult(Host.Winget.Error, true);
    });

    /// Строка уже обновилась/установилась: убрать её из списка и пересчитать подписи (у каждого раздела свой список).
    protected abstract void JobSucceeded(PkgJob j);

    private readonly Dictionary<RowVm, List<ActionVm>> _fixButtons = new();
    /// Последняя неудача по Id пакета: список перестраивается (скрыли пункт, обновили библиотеки), а причина и кнопка с выходом должны остаться на строке.
    private readonly Dictionary<string, WingetRun> _failures = new(StringComparer.OrdinalIgnoreCase);
    private void Succeeded(PkgJob j) { _failures.Remove(j.Id); ClearFix(j.Row_); JobSucceeded(j); }
    /// Вызывается при (пере)построении строк: если по этому пакету была неудача — вернуть на строку причину и кнопку.
    protected void ReapplyFailure(PkgJob j) { if (_failures.TryGetValue(j.Id, out var run)) MarkFailed(j, run); }
    private bool _reinstalling;
    protected override bool Blocked => Host.Winget.Busy || _reinstalling;

    /// Выполнить отмеченное по одному. Закрытые ли программы — проверяем перед каждой; открытая пропускается с просьбой закрыть.
    /// Пакеты Python/.NET, которыми кто-то пользуется: одиночное обновление — с вопросом и списком программ, в пачке из нескольких — пропускаются с пометкой.
    protected async Task RunJobsAsync(IReadOnlyList<PkgJob> jobs, string verbing)
    {
        var exe = await Task.Run(Winget.Find);
        if (exe == null) { StoreAction.Visible = true; SetResult(Strings.WingetMissing, true); return; }
        var wingetVer = await Winget.VersionAsync(exe, CancellationToken.None);
        if (Winget.IsTooOld(wingetVer)) { StoreAction.Visible = true; SetResult(Strings.WingetOld, true); return; }
        await RunBusyAsync(verbing, async (ct, progress) =>
        {
            Host.Winget.BeginJobs(); _jobsRunning = true;
            // «Отмена» действует между программами: установщик посреди работы не убиваем (сломается программа)
            using var onCancel = ct.Register(() => progress.Report(Strings.CancelAfterCurrent));
            try
            {
                int ok = 0, running = 0, failed = 0, inUse = 0, done = 0; var notes = new List<string>(); bool cancelled = false;
                foreach (var j in jobs)
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    done++;
                    progress.Report(string.Format(j.Install ? Strings.Installing : Strings.Updating, j.Name, done, jobs.Count));
                    var (pre, preNote) = await PreflightAsync(j, single: jobs.Count == 1);
                    if (pre == Pre.Running || pre == Pre.Declined) { running++; notes.Add("• " + preNote); continue; }
                    if (pre == Pre.InUse) { inUse++; notes.Add("• " + preNote); continue; }
                    var cmd = j.Install ? Winget.InstallCommand(exe, j.Id, wingetVer) : Winget.UpgradeCommand(exe, j.Id, wingetVer);
                    var run = await WingetRunner.RunAsync(cmd.FileName, cmd.Args, TimeSpan.FromMinutes(30), CancellationToken.None, killOnTimeout: false);
                    if (run.Ok || run.ExitCode == WingetRunner.NoApplicableUpdate) { ok++; Succeeded(j); }
                    else { failed++; notes.Add($"• {j.Name} — {WingetRunner.DescribeFailure(run)}"); MarkFailed(j, run); }
                }
                var text = $"{Strings.Done}: {ok} {Strings.Of} {jobs.Count}" + (running > 0 ? $", {Strings.SkippedOpen} {running}" : "") + (inUse > 0 ? $", {Strings.SkippedInUseCount} {inUse}" : "")
                           + (failed > 0 ? $", {Strings.WithError} {failed}" : "") + (cancelled ? $" · {Strings.Cancelled.ToLowerInvariant()}" : "");
                SetResult(text, failed > 0 || running > 0 || inUse > 0, string.Join("\n", notes));
            }
            finally
            {
                _jobsRunning = false; Host.Winget.EndJobs();
                if (_pendingRebuild) { _pendingRebuild = false; RebuildQuiet(); }
            }
        });
    }

    // ---- Что предложить после неудачи: «Повторить с правами администратора» / «Переустановить» ----

    private void ClearFix(RowVm row)
    {
        if (!_fixButtons.Remove(row, out var list)) return;
        foreach (var b in list) row.Buttons.Remove(b);
        row.ButtonsBelow = false;
    }

    /// Строка упавшего обновления: красная плашка, причина под названием и (если причина узнаётся) кнопка с выходом.
    protected void MarkFailed(PkgJob j, WingetRun run)
    {
        var row = j.Row_;
        _failures[j.Id] = run;
        ClearFix(row);
        row.IsChecked = false;
        row.SetPill(Strings.JobFailedPill, "Err");
        var baseDetail = row.Detail.Split('\n')[0];
        row.Detail = baseDetail + "\n" + WingetRunner.DescribeFailure(run);
        var fix = WingetFailure.Suggest(run);
        var added = new List<ActionVm>();
        void Add(string text, Func<Task> go)
        {
            var b = new ActionVm(text, new RelayCommand(() => GuardAsync(go), () => CanRun));
            row.Buttons.Insert(0, b); added.Add(b);
        }
        if (fix == WingetFix.RetryAsAdmin) Add(Strings.WingetRetryAdmin, () => RetryAsAdminAsync(j));
        else if (fix == WingetFix.Reinstall) Add(Strings.WingetReinstall, () => ReinstallAsync(j));
        else if (j.Install) Add(Strings.WingetInstallAgain, () => RunJobsAsync(new[] { j }, Strings.Working));
        if (added.Count > 0) { _fixButtons[row] = added; row.ButtonsBelow = true; }
    }

    private enum Pre { Go, Running, InUse, Declined }

    /// Проверки перед запуском обновления (общие для пачки, одиночного запуска и «Повторить с правами администратора»):
    /// программа открыта; Python/.NET используются другими программами (в пачке — пропуск, одиночно — вопрос). Note — текст для итога, без маркера.
    private async Task<(Pre Kind, string Note)> PreflightAsync(PkgJob j, bool single)
    {
        if (j.Install) return (Pre.Go, "");
        if (j.Row != null)
        {
            var row = j.Row;
            if (await Task.Run(() => { var q = new SystemProcessQuery(); q.Snapshot(); return RunningApps.IsRunning(row, q); })) return (Pre.Running, $"{j.Name} — {Strings.CloseIt}");
        }
        if (RuntimeUsage.IsGuarded(j.Id))
        {
            var users = await Task.Run(() => RuntimeUsage.FindUsers(j.Id, new SystemProcessQuery()));
            if (users.Count > 0)
            {
                var list = string.Join(", ", users);
                if (!single) return (Pre.InUse, $"{j.Name} — {string.Format(Strings.SkippedInUse, list)}. {Strings.UpdateAloneHint}");
                if (!ConfirmDialog.Ask(string.Format(Strings.RuntimeInUseAsk, string.Join("\n", users.Select(u => "• " + u)))))
                    return (Pre.Declined, $"{j.Name} — {string.Format(Strings.SkippedInUse, list)}");
            }
        }
        return (Pre.Go, "");
    }

    /// «Повторить с правами администратора»: помощник сам спросит «Да/Нет» (с Id пакета) и запустит winget повышенно, от пользователя.
    private async Task RetryAsAdminAsync(PkgJob j)
    {
        if (!App.Services.Admin.CanElevate) { SetResult(Strings.AdminNoAccount, true); return; }
        if (!await AdminSetup.EnsureInstalledAsync()) { SetResult(Strings.AdminNotInstalled, true); return; }
        var (pre, preNote) = await PreflightAsync(j, single: true);
        if (pre != Pre.Go) { SetResult(preNote, true); return; }
        UpdateHelperAction.Visible = false;
        var mode = j.Install ? WingetMode.Install : WingetMode.Upgrade;
        await RunBusyAsync(Strings.WingetAdminRunning, async (_, progress) =>
        {
            Host.Winget.BeginJobs(); _jobsRunning = true;
            try
            {
                // Без токена отмены: помощник уже делает дело, остановить его отсюда нельзя — ждём до конца.
                var r = await App.Services.Admin.SendAsync(AdminAction.WingetUpgrade, WingetArg.Build(mode, j.Id), CancellationToken.None, Strings.WingetAdminRunning, progress);
                if (r.Ok) { Succeeded(j); SetResult(string.Format(Strings.WingetAdminDone, j.Name)); }
                else if (r.Code == AdminCodes.WingetDeclined) SetResult(Strings.WingetAdminDeclined, true);
                else if (WingetArg.TryParseExitCode(r.Code) is int code)
                {
                    // Строка перестраивается под НОВЫЙ код: повторная неудача с правами может потребовать «Переустановить» (0x8A15008E) и т.п.
                    MarkFailed(j, new WingetRun(code, r.Message, false, false));
                    SetResult($"«{j.Name}»: {r.Message}", true);
                }
                else { var (text, err) = DescribeAdmin(r, ""); SetResult($"«{j.Name}»: {text}", err); }
            }
            finally
            {
                _jobsRunning = false; Host.Winget.EndJobs();
                if (_pendingRebuild) { _pendingRebuild = false; RebuildQuiet(); }
            }
        }, cancellable: false);
    }

    /// «Переустановить» (технология установки отличается): вопрос с предупреждением, потом winget uninstall и winget install от пользователя.
    /// Если установка после удаления упала из-за прав — на строке появится «Повторить с правами администратора» (установка), иначе «Установить заново».
    private async Task ReinstallAsync(PkgJob j)
    {
        if (!ConfirmDialog.Ask(string.Format(Strings.ReinstallConfirm, j.Name))) return;
        var exe = await Task.Run(Winget.Find);
        if (exe == null) { StoreAction.Visible = true; SetResult(Strings.WingetMissing, true); return; }
        var ver = await Winget.VersionAsync(exe, CancellationToken.None);
        if (Winget.IsTooOld(ver)) { StoreAction.Visible = true; SetResult(Strings.WingetOld, true); return; }
        if (j.Row != null && await Task.Run(() => { var q = new SystemProcessQuery(); q.Snapshot(); return RunningApps.IsRunning(j.Row, q); }))
        { SetResult($"«{j.Name}» — {Strings.CloseIt}", true); return; }
        _reinstalling = true;
        try
        {
            var removed = false;
            await RunBusyAsync(string.Format(Strings.Reinstalling, j.Name), async (_, progress) =>
            {
                Host.Winget.BeginJobs(); _jobsRunning = true;
                try
                {
                    var un = Winget.UninstallCommand(exe, j.Id, ver);
                    var r = await WingetRunner.RunAsync(un.FileName, un.Args, TimeSpan.FromMinutes(30), CancellationToken.None, killOnTimeout: false);
                    if (r.Ok) removed = true;
                    else SetResult($"«{j.Name}»: {Strings.UninstallFailed}{WingetRunner.DescribeFailure(r)}", true);
                }
                finally { _jobsRunning = false; Host.Winget.EndJobs(); }
            }, cancellable: false);
            if (removed) await RunJobsAsync(new[] { j with { Install = true, Row = null } }, string.Format(Strings.Reinstalling, j.Name));
        }
        finally { _reinstalling = false; if (_pendingRebuild) { _pendingRebuild = false; RebuildQuiet(); } }
    }

    protected static string Delta(WingetRow r) => r.Available.Length > 0 ? $"{(r.Version.Length > 0 ? r.Version : "?")} → {r.Available}" : r.Version;
}

public sealed class LibrariesSection : WingetSection
{
    private IReadOnlyList<LibraryStatus> _status = Array.Empty<LibraryStatus>();
    private bool _loaded;
    private readonly ActionVm _apply;

    public LibrariesSection(MaintenanceVm host) : base(host, "📚", Strings.SecLibraries, Strings.SecLibrariesHint)
    {
        _apply = AddAction(Strings.InstallOrUpdate, ApplyAsync, true, () => Checked().Count > 0);
        Actions.Add(StoreAction); Actions.Add(UpdateHelperAction);
        Rebuild();
    }

    /// В строку у трея — только то, что реально нужно: обновления и нехватка Visual C++/DirectX; необязательное («не установлена» Java и т.п.) не считается.
    public int ActionableCount => Items.OfType<RowVm>().Count(r => r.Checkable && r.Actionable);
    /// Сколько стоящих версий без поддержки (.NET 6/7): по ним нет обновления, но и «всё хорошо» про них не скажешь.
    private int EolInstalledCount => _status.Count(s => s.Installed && s.EndOfLife);
    protected override int? PendingCount => _loaded ? ActionableCount : null;
    protected override string TileMany => "можно установить или обновить";
    protected override string TileNone => "всего хватает";
    /// «Проверить всё» только перечитывает локальный список (реестр, без winget и сети); поиск обновлений остаётся за кнопкой раздела.
    protected override Task QuickCoreAsync() => GuardAsync(LoadCheapAsync);
    private List<RowVm> Checked() => Items.OfType<RowVm>().Where(r => r.Checkable && r.IsChecked).ToList();
    private void UpdateLabels() => _apply.Text = Checked().Count is > 0 and var n ? $"{Strings.InstallOrUpdate} ({n})" : Strings.InstallOrUpdate;

    public async Task LoadCheapAsync()
    {
        _status = await Task.Run(LibraryCheck.ForThisPc);
        _loaded = true; Rebuild();
    }

    protected override void JobSucceeded(PkgJob j)
    {
        Items.Remove(j.Row_);
        if (j.Install && _status.FirstOrDefault(s => s.WingetId == j.Id) is { } st) _status = _status.Select(x => x == st ? st with { Installed = true } : x).ToList();
    }

    public void Apply(IReadOnlyList<LibraryStatus> status) { _status = status; _loaded = true; Rebuild(); }
    protected override void RebuildQuiet() { Rebuild(); }
    protected override void ApplyScan() { Rebuild(); SetResult(LibraryCheck.Summary(ActionableCount, EolInstalledCount), false); }

    private void Rebuild()
    {
        Items.Clear();
        var updates = (Host.Winget.Rows ?? Array.Empty<WingetRow>()).Where(r => Libraries.Classify(r) == UpdateGroup.Library)
                          .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_loaded) Items.Add(new RowVm { Title = Strings.Reading });
        // Сначала решаем, к чему относится каждая строка, — место под галочку оставляем, только если галочка есть хоть у одной (иначе слева пустое поле).
        var plan = _status.Select(s =>
        {
            WingetRow? up = s.WingetId != null && updates.TryGetValue(s.WingetId, out var u) ? u
                          : s.Key == "java" ? updates.Values.FirstOrDefault(r => r.Id.StartsWith("Oracle.JavaRuntimeEnvironment", StringComparison.OrdinalIgnoreCase) || r.Id.StartsWith("EclipseAdoptium.", StringComparison.OrdinalIgnoreCase))
                          : null;
            if (up != null) used.Add(up.Id);
            var wid = up?.Id ?? s.WingetId;
            return (S: s, Up: up, Wid: wid, Check: wid != null && (!s.Installed || up != null));
        }).ToList();
        var reserve = plan.Any(p => p.Check) || updates.Values.Any(r => !used.Contains(r.Id));
        foreach (var (s, up, wid, check) in plan)
        {
            var row = new RowVm
            {
                Title = s.Title, Detail = s.Explain, Changed = UpdateLabels, Reserve = reserve,
                Checkable = check,
                Actionable = up != null || (!s.Installed && (s.Key.StartsWith("vcredist") || s.Key == "directx")),
                Tag = wid,
            };
            row.CanCheck = true;
            if (!s.Installed) row.SetPill(Strings.NotInstalled, "Err");
            else if (up != null) row.SetPill(Delta(up), "Warn");
            else if (s.EndOfLife) row.SetPill(Strings.EndOfSupport, "Warn");
            else row.SetPill(s.Version != null ? "v " + s.Version : Strings.Installed, "Ok");
            Items.Add(row);
            if (wid != null) ReapplyFailure(new PkgJob(wid, s.Title, up == null, up, row));
        }
        foreach (var r in updates.Values.Where(r => !used.Contains(r.Id)))
        {
            var row = new RowVm { Title = r.Name, Detail = r.Id, Checkable = true, Actionable = true, Reserve = true, Tag = r.Id, Changed = UpdateLabels, CanCheck = true };
            row.SetPill(Delta(r), "Warn"); Items.Add(row);
            ReapplyFailure(new PkgJob(r.Id, r.Name, false, r, row));
        }
        UpdateLabels();
    }

    private async Task ApplyAsync()
    {
        var rows = Checked();
        if (rows.Count == 0) return;
        if (!ConfirmDialog.Ask(string.Format(Strings.LibrariesConfirm, rows.Count))) return;
        var updates = (Host.Winget.Rows ?? Array.Empty<WingetRow>()).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var jobs = rows.Select(r =>
        {
            var id = (string)r.Tag!;
            var row = updates.GetValueOrDefault(id);
            return new PkgJob(id, r.Title, row == null, row, r);
        }).ToList();
        await RunJobsAsync(jobs, Strings.Working);
        Rebuild();
    }

    public void LoadDemo()
    {
        Apply(LibraryCheck.Evaluate(new (string, string?)[]
        {
            ("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "14.38.33135"),
            ("Microsoft Windows Desktop Runtime - 7.0.20 (x64)", "7.0.20"), ("Microsoft GameInput", "10.1.22621.3036"),
        }, new[] { "Microsoft.WindowsDesktop.App 7.0.20 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]" }, _ => false));
        Host.Winget.Set(new[]
        {
            new WingetRow("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft.VCRedist.2015+.x64", "14.38.33135", "14.44.35211", "winget"),
            new WingetRow("Microsoft Windows Desktop Runtime - 8.0.22 (x64)", "Microsoft.DotNet.DesktopRuntime.8", "8.0.22", "8.0.31", "winget"),
        });
    }
}

public sealed class ProgramsSection : WingetSection
{
    private readonly ActionVm _all, _apply, _restore;
    private bool _scanned;

    public ProgramsSection(MaintenanceVm host) : base(host, "📦", Strings.SecPrograms, Strings.SecProgramsHint)
    {
        _all = AddAction(Strings.CheckAll, ToggleAllAsync, false, () => Items.OfType<RowVm>().Any(r => r.Checkable));
        _apply = AddAction(Strings.UpdateChecked, ApplyAsync, true, () => Checked().Count > 0);
        _restore = AddAction(Strings.RestoreHidden, RestoreAsync, false, null, false);
        Actions.Add(StoreAction); Actions.Add(UpdateHelperAction);
        EmptyText = Strings.ProgramsNotChecked;
    }

    /// Только то, что можно обновить сейчас: скрытые тобой и служебные строки не считаются.
    public int ActionableCount => Items.OfType<RowVm>().Count(r => r.Checkable);
    protected override int? PendingCount => _scanned || HasItems ? ActionableCount : null;
    protected override string TileManyFor(int n) => TileCaptions.Programs(n);
    protected override string TileNone => "всё свежее";
    public override string UncheckedText => Strings.TileHeavyPrograms;
    private int HiddenFound { get; set; }
    public override string TileCaption => base.TileCaption + (HiddenFound > 0 ? "\n" + TileCaptions.Hidden(HiddenFound) : "");
    private List<RowVm> Checked() => Items.OfType<RowVm>().Where(r => r.Checkable && r.IsChecked).ToList();
    private void UpdateLabels()
    {
        var shown = Items.OfType<RowVm>().Where(r => r.Checkable).ToList();
        _apply.Text = Checked().Count is > 0 and var n ? $"{Strings.UpdateChecked} ({n})" : Strings.UpdateChecked;
        _all.Text = shown.Count > 0 && shown.All(r => r.IsChecked) ? Strings.UncheckAll : Strings.CheckAll;
    }
    private bool _showHidden;

    protected override void RebuildQuiet() { Rebuild(); }
    protected override void JobSucceeded(PkgJob j) => Items.Remove(j.Row_);

    protected override void ApplyScan()
    {
        _scanned = true; Rebuild();
        SetScanResult();
    }

    /// Итог: что доступно всего и сколько из этого человек скрыл сам — иначе «Обновлений: 1» выглядит так, будто всё почти свежее.
    private void SetScanResult()
    {
        if (HiddenFound > 0) SetResult($"Доступно {ActionableCount + HiddenFound} · скрыто тобой {HiddenFound}" + (ActionableCount == 0 ? ". Видимых обновлений нет." : ". Ничего не обновляется без отметки."));
        else SetResult(ActionableCount == 0 ? Strings.ProgramsFine : string.Format(Strings.ProgramsFound, ActionableCount));
    }

    private void Rebuild()
    {
        var keepChecked = new HashSet<string>(Checked().Select(r => ((WingetRow)r.Tag!).Id), StringComparer.OrdinalIgnoreCase);
        Items.Clear();
        var hidden = new HashSet<string>(App.Services.Config.NoUpdate, StringComparer.OrdinalIgnoreCase);
        var rows = (Host.Winget.Rows ?? Array.Empty<WingetRow>()).Where(r => Libraries.Classify(r) == UpdateGroup.Program).ToList();
        foreach (var r in rows.Where(r => !hidden.Contains(r.Id)))
        {
            var row = new RowVm { Title = r.Name, Detail = r.Id, Tag = r, Checkable = true, CanCheck = true, Changed = UpdateLabels };
            row.SetPill(Delta(r), "Warn");
            var captured = row;
            row.Buttons.Add(new ActionVm(Strings.DontUpdate, RelayCommand.Sync(() => Hide(captured))));
            Items.Add(row);
            ReapplyFailure(new PkgJob(r.Id, r.Name, false, r, row));
            if (keepChecked.Contains(r.Id)) row.IsChecked = true;
        }
        var hiddenRows = rows.Where(r => hidden.Contains(r.Id)).ToList();
        HiddenFound = hiddenRows.Count;
        EmptyText = _scanned ? Strings.ProgramsFine : Strings.ProgramsNotChecked;
        if (_scanned && hiddenRows.Count > 0 && Items.Count == 0) Items.Add(new RowVm { Title = "Видимых обновлений нет — всё, что доступно, скрыто тобой." });
        if (hiddenRows.Count > 0) AddHiddenList(hiddenRows);
        _restore.Visible = hiddenRows.Count > 0; _restore.Text = $"{Strings.RestoreHidden} ({hiddenRows.Count})";
        UpdateLabels();
        RaiseTile();
        Raise(nameof(ShowEmpty));
    }

    /// Раскрывающийся список скрытых тобой: что именно скрыто, у каждой — «Вернуть».
    private void AddHiddenList(List<WingetRow> hiddenRows)
    {
        var head = new RowVm { Reserve = true, Title = $"Скрыто тобой: {hiddenRows.Count}", Detail = _showHidden ? "эти программы не попадают в обновление" : "нажми «Показать», чтобы увидеть, какие именно" };
        head.Buttons.Add(new ActionVm(_showHidden ? "Свернуть" : "Показать", RelayCommand.Sync(() => { _showHidden = !_showHidden; RebuildOrDefer(); })));
        Items.Add(head);
        if (!_showHidden) return;
        foreach (var r in hiddenRows)
        {
            var row = new RowVm { Title = r.Name, Detail = r.Id, Indent = 0, Reserve = true };
            row.SetPill(Delta(r), "Off");
            var id = r.Id;
            row.Buttons.Add(new ActionVm(Strings.Restore, RelayCommand.Sync(() => Unhide(id))));
            Items.Add(row);
        }
    }

    private void Hide(RowVm row)
    {
        var id = ((WingetRow)row.Tag!).Id;
        var cfg = App.Services.Config;
        if (!cfg.NoUpdate.Contains(id, StringComparer.OrdinalIgnoreCase)) cfg.NoUpdate.Add(id);
        App.Services.Save();
        if (_jobsRunning) { Items.Remove(row); _pendingRebuild = true; }          // посреди обновления список не пересобираем: строка исчезает сразу, остальное — по окончании
        else Rebuild();
        if (_scanned) SetScanResult();
    }

    /// Пересборка списка программ; пока идёт обновление — откладывается до его конца (иначе итог ляжет на строку-сироту).
    private void RebuildOrDefer() { if (_jobsRunning) _pendingRebuild = true; else Rebuild(); }

    private void Unhide(string id)
    {
        var cfg = App.Services.Config;
        cfg.NoUpdate.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)); App.Services.Save();
        RebuildOrDefer();
        if (_scanned) SetScanResult();
    }

    private Task RestoreAsync()
    {
        App.Services.Config.NoUpdate.Clear(); App.Services.Save();
        RebuildOrDefer();
        if (_scanned) SetScanResult();
        return Task.CompletedTask;
    }

    private Task ToggleAllAsync()
    {
        var rows = Items.OfType<RowVm>().Where(r => r.Checkable).ToList();
        var on = !rows.All(r => r.IsChecked);
        foreach (var r in rows) r.IsChecked = on;
        return Task.CompletedTask;
    }

    private async Task ApplyAsync()
    {
        var rows = Checked();
        if (rows.Count == 0) return;
        if (!ConfirmDialog.Ask(string.Format(Strings.ProgramsConfirm, rows.Count))) return;
        var jobs = rows.Select(r => { var w = (WingetRow)r.Tag!; return new PkgJob(w.Id, w.Name, false, w, r); }).ToList();
        await RunJobsAsync(jobs, Strings.Working);
        UpdateLabels();
        Raise(nameof(ShowEmpty));
    }

    public void LoadDemo()
    {
        // Скрытые владельцем программы (в памяти, режим --preview ничего не сохраняет)
        foreach (var id in new[] { "Git.Git", "7zip.7zip", "VideoLAN.VLC", "Zoom.Zoom" }) if (!App.Services.Config.NoUpdate.Contains(id)) App.Services.Config.NoUpdate.Add(id);
        _showHidden = true; _scanned = true;
        Host.Winget.Set(Host.Winget.Rows!.Concat(new[]
        {
            new WingetRow("Git", "Git.Git", "2.45.1", "2.51.0", "winget"), new WingetRow("7-Zip", "7zip.7zip", "23.01", "25.01", "winget"),
            new WingetRow("VLC media player", "VideoLAN.VLC", "3.0.20", "3.0.21", "winget"), new WingetRow("Zoom Workplace", "Zoom.Zoom", "6.2.0", "6.5.1", "winget"),
            new WingetRow("GitHub Desktop", "GitHub.GitHubDesktop", "3.6.1", "3.6.6", "winget"),
            new WingetRow("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.9.2", "8.9.8.1", "winget"),
            new WingetRow("Python Launcher", "Python.Launcher", "< 3.14.7", "3.14.7", "winget"),
            new WingetRow("Spotify", "Spotify.Spotify", "1.2.79.427", "1.3.1.234", "winget"),
            new WingetRow("Помощник по установке Windows 11", "Microsoft.WindowsInstallationAssistant", "1.4.19041.5003", "1.4.19041.6448", "winget"),
            new WingetRow("Steam", "Valve.Steam", "2.10.91.91", "2.10.91.99", "winget"),
            new WingetRow("Python 3.12", "Python.Python.3.12", "3.12.7", "3.12.10", "winget"),
        }).ToList());
        // Как после неудачных обновлений: Steam упал из-за прав (1603), у Notepad++ другая технология установки (переносная версия).
        void Fail(string id, int code, string output)
        {
            var row = Items.OfType<RowVm>().First(r => ((WingetRow)r.Tag!).Id == id); var w = (WingetRow)row.Tag!;
            MarkFailed(new PkgJob(w.Id, w.Name, false, w, row), new WingetRun(code, output, false, false));
        }
        Fail("Valve.Steam", unchecked((int)0x8A150049), "Installer failed with exit code: 1603");
        Fail("Notepad++.Notepad++", WingetFailure.InstallTechnologyDiffers, "The install technology of the installed version is different from the available one");
        SetResult("Обновлено: 1 из 4, пропущено (программа открыта) 1, пропущено (используется) 1, с ошибкой 2", true,
                  "• Telegram Desktop — закрой программу и нажми «Обновить» ещё раз\n• Python 3.12 — пропущено: используется (python -m notes_bot.startup, python -m http.server). Обнови его отдельно — Lumen спросит.\n• Steam — код winget 0x8A150049 — Installer failed with exit code: 1603\n• Notepad++ (64-bit x64) — код winget 0x8A15008E");
    }
}
