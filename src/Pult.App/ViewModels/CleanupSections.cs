using System.IO;
using Pult.App.Services;
using Pult.App.Views;
using Pult.Core.Admin;
using Pult.Core.Cleanup;
using Pult.Core.Maintenance;

namespace Pult.App.ViewModels;

internal sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }

/// «Очистка»: Система, Браузеры и программы, Разработка. Сканирование — по кнопке, удаляется только отмеченное после вопроса.
public sealed class CleanupSection : SectionVm
{
    private sealed record Entry(ICleanupCategory? Cat, CleanupItem? Item, DevProject? Project);
    private List<ICleanupCategory> _cats = new();
    private CleanupEnv? _env;
    private readonly ActionVm _clean;

    protected override int? PendingCount => HasItems ? Items.OfType<RowVm>().Count(r => r.Checkable) : null;
    protected override string TileNone => "чисто";
    protected override string TileUnknown => "Проверить";
    public override string UncheckedText => Strings.TileHeavyCleanup;

    /// Сколько можно освободить: сумма уже посчитанных пунктов, которые Пульт вообще может очистить (без неизвестных размеров).
    private long Freeable() => Items.OfType<RowVm>().Where(r => r.Checkable && r.Tag is Entry)
        .Sum(r => { var e = (Entry)r.Tag!; var b = e.Item?.Bytes ?? e.Project!.JunkBytes; return Math.Max(0, b); });
    private const long GoldFrom = 1L << 30;
    public override string TileValue => PendingCount is null ? "—" : Freeable() > 0 ? TileCaptions.Approx(Freeable()) : "✓";
    public override string TileCaption => PendingCount is null ? TileUnknown : Freeable() > 0 ? "можно освободить" : TileNone;
    /// Золото — только если есть что делать (от 1 ГБ); мелочь — спокойным цветом.
    public override string TileState => PendingCount is null ? "Off" : Freeable() >= GoldFrom ? "Warn" : Freeable() > 0 ? "Info" : "Ok";

    public CleanupSection(MaintenanceVm host) : base(host, "🧹", Strings.SecCleanup, Strings.SecCleanupHint, Strings.Scan)
    {
        _clean = AddAction(Strings.DeleteChecked, CleanAsync, true, () => Checked().Count > 0);
        Actions.Add(UpdateHelperAction);
        EmptyText = Strings.CleanNotScanned;
    }

    private List<RowVm> Checked() => Items.OfType<RowVm>().Where(r => r.Checkable && r.IsChecked && r.Tag is Entry).ToList();

    private void Recalc()
    {
        long sum = 0; int unknown = 0; var rows = Checked();
        foreach (var r in rows)
        {
            var e = (Entry)r.Tag!;
            var b = e.Item?.Bytes ?? e.Project!.JunkBytes;
            if (b < 0) unknown++; else sum += b;
        }
        _clean.Text = rows.Count == 0 ? Strings.DeleteChecked : $"{Strings.DeleteChecked} ({SizeFormat.Human(sum)}{(unknown > 0 ? " + " + Strings.NoSize : "")})";
    }

    private CleanupEnv MakeEnv() => CleanupEnv.ForThisPc() with
    {
        Admin = async (id, ct) =>
        {
            var r = await App.Services.Admin.SendAsync(AdminAction.CleanSystem, id, ct, Strings.CleanAdminProgress, _adminProgress);
            _lastAdmin[id] = r; return r;
        },
    };
    private readonly Dictionary<string, AdminResponse> _lastAdmin = new();
    private IProgress<string>? _adminProgress;

    protected override Task CheckAsync() => RunBusyAsync(Strings.CleanScanning, async (ct, progress) =>
    {
        UpdateHelperAction.Visible = false;
        _env = await Task.Run(MakeEnv);                                   // снимок процессов и путь Steam — не на потоке окна
        _cats = UserCategories.All(_env).Concat(DevJunk.CacheCategories(_env)).ToList();
        var items = await UserCategories.ScanAllAsync(_cats, ct, new SyncProgress<string>(t => progress.Report(Strings.Looking + t)));
        progress.Report(Strings.FindingProjects);
        var cfg = App.Services.Config;
        var projects = await Task.Run(() => DevJunk.ScanProjects(cfg.DevRoots, cfg.DevAgeDays, DevJunk.GitStatus, ct), ct);
        Build(items, projects.Where(p => p.JunkBytes > 0).OrderByDescending(p => p.JunkBytes).ToList());
        SetResult(Items.OfType<RowVm>().Any(r => r.Checkable) ? Strings.CleanScanned : Strings.CleanNothing);
    });

    private void Build(IReadOnlyList<CleanupItem> items, IReadOnlyList<DevProject> projects)
    {
        Items.Clear();
        foreach (var group in new[] { CleanupGroups.System, CleanupGroups.Apps, CleanupGroups.Dev })
        {
            var inGroup = items.Where(i => i.Group == group).ToList();
            var projs = group == CleanupGroups.Dev ? projects : Array.Empty<DevProject>();
            if (inGroup.Count == 0 && projs.Count == 0) continue;
            Items.Add(new HeaderVm(group));
            foreach (var i in inGroup) Items.Add(ItemRow(i));
            if (projs.Count > 0)
            {
                Items.Add(new HeaderVm(Strings.ProjectsHeader, Strings.ProjectsSub));
                foreach (var p in projs) Items.Add(ProjectRow(p));
            }
        }
        Recalc();
        Raise(nameof(ShowEmpty));
    }

    private RowVm ItemRow(CleanupItem i)
    {
        var canCheck = !i.Blocked && i.Id != "WindowsOld" && !(i.NeedsAdmin && !App.Services.Admin.CanElevate);
        var detail = new List<string>();
        if (i.Blocked) detail.Add($"{Strings.Close} {i.BlockedBy} — {Strings.WontTouchOpen}");
        else if (!string.IsNullOrEmpty(i.Note) && i.Bytes >= 0) detail.Add(i.Note!);
        var noAdmin = i.NeedsAdmin && !App.Services.Admin.CanElevate;               // обычная учётная запись: помощник не заработает
        if (i.NeedsAdmin) detail.Add(noAdmin ? Strings.AdminNoAccount : Strings.NeedsHelper);
        var row = new RowVm
        {
            Title = i.Title, Tag = new Entry(_cats.FirstOrDefault(c => c.Id == i.Id), i, null), Checkable = canCheck, Reserve = true, Shield = i.NeedsAdmin,
            Size = SizeFormat.Human(i.Bytes), Detail = string.Join(" · ", detail), Changed = Recalc,
        };
        row.CanCheck = canCheck; row.IsChecked = canCheck && i.Checked;
        if (i.Blocked) row.SetPill(Strings.Open, "Warn");
        if (i.Id == "WindowsOld") row.Buttons.Add(new ActionVm(Strings.OpenWindowsCleanup, RelayCommand.Sync(() => MaintenanceVm.OpenUrl(WindowsOldHint))));
        return row;
    }

    private const string WindowsOldHint = "ms-settings:storagepolicies";

    private RowVm ProjectRow(DevProject p)
    {
        var kinds = string.Join(", ", p.Junk.Select(j => j.Kind).Distinct());
        var detail = kinds + (p.Mark != null ? " · " + p.Mark : "");
        var row = new RowVm { Title = ProjectTitle(p.Path), Tag = new Entry(null, null, p), Checkable = true, Size = SizeFormat.Human(p.JunkBytes), Detail = detail, Changed = Recalc };
        row.CanCheck = true; row.IsChecked = p.Checked;
        if (p.Mark != null) row.SetPill(p.GitDirty ? Strings.Uncommitted : Strings.NoGit, "Warn");
        var path = p.Path;
        row.Buttons.Add(new ActionVm(Strings.ShowInExplorer, RelayCommand.Sync(() => MaintenanceVm.ShowInExplorer(path))));
        return row;
    }

    /// Путь проекта от понятного корня: «Рабочий стол\pult\src\Pult.App» — чтобы одинаковые «src» разных проектов не путались.
    private static IReadOnlyList<(string Root, string Name)>? _friendlyRoots;
    private static string ProjectTitle(string path) => FriendlyPath.Of(path.TrimEnd('\\', '/'), _friendlyRoots ??= FriendlyPath.ForThisPc());

    private async Task CleanAsync()
    {
        var rows = Checked();
        if (rows.Count == 0) return;
        long sum = 0; int unknown = 0;
        foreach (var r in rows) { var e = (Entry)r.Tag!; var b = e.Item?.Bytes ?? e.Project!.JunkBytes; if (b < 0) unknown++; else sum += b; }
        var asksHelper = rows.Any(r => ((Entry)r.Tag!).Item?.NeedsAdmin == true);
        var text = string.Format(Strings.CleanConfirm, SizeFormat.Human(sum) + (unknown > 0 ? " + " + Strings.NoSize : ""))
                   + (asksHelper ? "\n\n" + Strings.CleanConfirmHelper : "");
        if (rows.Any(r => ((Entry)r.Tag!).Item?.Id == "RecycleBin")) text += "\n\n" + Strings.CleanConfirmBin;
        var risky = rows.Select(r => ((Entry)r.Tag!).Project).Where(p => p is { Mark: not null }).Select(p => ProjectTitle(p!.Path)).ToList();
        if (risky.Count > 0) text += "\n\n" + string.Format(Strings.CleanConfirmProjects, string.Join(", ", risky));
        if (!ConfirmDialog.Ask(text)) return;
        if (asksHelper && !await AdminSetup.EnsureInstalledAsync())
        {
            foreach (var r in rows.Where(r => ((Entry)r.Tag!).Item?.NeedsAdmin == true)) r.IsChecked = false;
            rows = Checked();
            SetResult(Strings.HelperNeededClean, true);
            if (rows.Count == 0) return;
        }
        await RunBusyAsync(Strings.Cleaning, async (ct, progress) =>
        {
            _adminProgress = progress;
            var env = _env;
            if (env != null) await Task.Run(() => env.Procs.Snapshot());   // программы, закрытые после скана, больше не мешают
            CleanResult total = CleanResult.Empty; var notes = new List<string>(); int n = 0; _lastAdmin.Clear(); bool cancelled = false;
            var roots = App.Services.Config.DevRoots.ToList();
            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var e = (Entry)row.Tag!; n++;
                progress.Report(string.Format(Strings.CleaningOne, e.Item?.Title ?? row.Title, n, rows.Count));
                CleanResult r;
                try
                {
                    if (e.Project != null) r = await Task.Run(() => DevJunk.Clean(e.Project.Junk, roots), ct);
                    else if (e.Cat != null)
                    {
                        r = await e.Cat.CleanAsync(ct);
                        // причина отказа помощника — по коду ответа, а не по его тексту
                        if (e.Cat.NeedsAdmin && r.Error != null && _lastAdmin.TryGetValue(e.Cat.Id, out var ar)) r = r with { Error = DescribeAdmin(ar, "").Text };
                    }
                    else r = new CleanResult(0, 0, Strings.CategoryNotFound);
                }
                catch (OperationCanceledException) { cancelled = true; break; }   // уже освобождённое не теряем: итог покажет
                total = total.Plus(r with { Error = null });
                if (r.Error != null) notes.Add($"• {row.Title} — {r.Error}");
                else if (r.Skipped == 0) Items.Remove(row);
                else { row.Detail = string.Format(Strings.SkippedBusyFiles, r.Skipped); row.IsChecked = false; }
            }
            var human = SizeFormat.Human(total.FreedBytes);
            var done = (cancelled ? string.Format(Strings.CancelledFreed, human) : $"{Strings.Freed} {human}") + (total.Skipped > 0 ? $", {Strings.SkippedFiles} {total.Skipped} — {Strings.Busy}" : "");
            SetResult(done, notes.Count > 0, string.Join("\n", notes));
            Recalc(); Raise(nameof(ShowEmpty));
        });
        _adminProgress = null;
    }

    public void LoadDemo()
    {
        var items = new[]
        {
            new CleanupItem("Temp", CleanupGroups.System, "Временные файлы пользователя", 1_420_000_000, 5210, false, null, true),
            new CleanupItem("RecycleBin", CleanupGroups.System, "Корзина", 310_000_000, 42, false, null, false),
            new CleanupItem("WindowsTemp", CleanupGroups.System, "Временные файлы Windows", 120_000_000, 311, true, null, true),
            new CleanupItem("ComponentCleanup", CleanupGroups.System, "Хранилище компонентов (DISM)", -1, 0, true, null, false, "размер узнаю при очистке"),
            new CleanupItem("WindowsOld", CleanupGroups.System, "Прежняя установка Windows (Windows.old)", 11_800_000_000, 90000, false, null, false, "удаляет сама Windows"),
            new CleanupItem("Chrome", CleanupGroups.Apps, "Кеш Chrome", 880_000_000, 2100, false, "Chrome", false),
            new CleanupItem("Edge", CleanupGroups.Apps, "Кеш Edge", 410_000_000, 900, false, null, true),
            new CleanupItem("Telegram", CleanupGroups.Apps, "Кеш Telegram (не чаты)", 2_300_000_000, 120, false, null, true),
            new CleanupItem("Dev.npm", CleanupGroups.Dev, "Кеш npm", 640_000_000, 9000, false, null, true),
            new CleanupItem("Dev.nuget", CleanupGroups.Dev, "Пакеты NuGet", 1_900_000_000, 12000, false, null, false, "следующая сборка скачает пакеты заново"),
        };
        _cats = new();
        var projects = new[]
        {
            new DevProject(@"C:\Users\User\Desktop\site", DateTime.Now.AddDays(-80), true, false, new[] { new DevJunkDir(@"C:\x\node_modules", "node_modules", 720_000_000) }, true),
            new DevProject(@"C:\Users\User\Desktop\shop", DateTime.Now.AddDays(-3), true, true, new[] { new DevJunkDir(@"C:\y\node_modules", "node_modules", 510_000_000), new DevJunkDir(@"C:\y\dist", "dist", 20_000_000) }, false),
            new DevProject(@"C:\Users\User\Desktop\app\src\App", DateTime.Now.AddDays(-1), false, false, new[] { new DevJunkDir(@"C:\z\bin", "bin", 140_000_000), new DevJunkDir(@"C:\z\obj", "obj", 60_000_000) }, false),
        };
        Build(items, projects);
        SetResult("Освобождено 3,4 ГБ, пропущено файлов 12 — заняты", false, "");
        Notes = "• Кеш Chrome — закрой Chrome\n• Хранилище компонентов (DISM) — Ты отказался в окне помощника";
    }
}

internal sealed record SpaceFile(string Path, long Bytes);

/// «Что занимает место»: карта папок диска C:, крупные файлы, старые установщики и дубликаты. Личные файлы — только в Корзину и только отмеченные.
public sealed class SpaceSection : SectionVm
{
    private sealed record Folder(string Path, long Bytes, IReadOnlyList<(string Path, long Bytes)> Children);
    private List<Folder>? _folders;
    private List<SpaceFile>? _big, _old;
    private List<DuplicateGroup>? _dups;
    private readonly ActionVm _trash, _dupBtn;
    public const long BigFileBytes = 300L * 1024 * 1024;

    protected override int? PendingCount => HasItems ? Items.OfType<RowVm>().Count(r => r.Size.Length > 0) : null;
    protected override string TileNone => "ничего крупного";
    protected override string TileUnknown => "сканирование не запускали";
    public override string UncheckedText => Strings.TileHeavySpace;
    protected override bool ShowBadge => false;
    public override string TileState => PendingCount is null ? "Off" : "Info";
    /// «Больше всего: Users 118 ГБ» — самая большая из просмотренных папок диска.
    private Folder? Biggest => _folders?.OrderByDescending(f => f.Bytes).FirstOrDefault();
    public override string TileValue => PendingCount is null ? "—" : Biggest is { } b ? SizeFormat.Human(b.Bytes) : base.TileValue;
    public override string TileCaption => PendingCount is null ? TileUnknown : Biggest is { } b ? TileCaptions.Biggest(Path.GetFileName(b.Path.TrimEnd('\\')) is { Length: > 0 } n ? n : b.Path) : base.TileCaption;

    public SpaceSection(MaintenanceVm host) : base(host, "🗺", Strings.SecSpace, Strings.SecSpaceHint, Strings.Scan)
    {
        _dupBtn = AddAction(Strings.FindDuplicates, DuplicatesAsync);
        _trash = AddAction(Strings.ToRecycle, TrashAsync, true, () => Checked().Count > 0);
        EmptyText = Strings.SpaceNotScanned;
    }

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// AppData (профили браузеров, WSL/Docker-диски, почта) в «крупных файлах» не ищем и в Корзину не отправляем.
    private static string AppDataDir => Path.Combine(Profile, "AppData");
    /// Отметки хранятся по ПУТИ (один и тот же файл может стоять в «крупных», «установщиках» и «дубликатах»): считаем и переносим его один раз.
    private readonly HashSet<string> _marks = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncing;
    private bool? _showDupDetail;
    private IReadOnlyList<(string Root, string Name)> _roots = Array.Empty<(string, string)>();
    private string Friendly(string path) => FriendlyPath.Of(path, _roots.Count > 0 ? _roots : (_roots = FriendlyPath.ForThisPc()));

    /// Размеры всех файлов, которые сейчас показаны в разделе (по пути).
    private Dictionary<string, long> KnownSizes()
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in (_big ?? new()).Concat(_old ?? new())) d[f.Path] = f.Bytes;
        if (_dups != null) foreach (var g in _dups) foreach (var p in g.Paths) d[p] = g.Size;
        return d;
    }

    private List<SpaceFile> Checked()
    {
        var sizes = KnownSizes();
        return _marks.Where(sizes.ContainsKey).Select(p => new SpaceFile(p, sizes[p])).ToList();
    }

    private void SetRowsChecked(string path, bool on, RowVm? except = null)
    {
        var was = _syncing; _syncing = true;
        try { foreach (var r in Items.OfType<RowVm>()) if (!ReferenceEquals(r, except) && r.Checkable && r.Tag is SpaceFile o && string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase)) r.IsChecked = on; }
        finally { _syncing = was; }
    }

    /// Человек поставил или снял галочку. Если от этой отметки в группе дубликатов не осталось бы ни одной копии — отметка не принимается.
    private void OnRowChanged(RowVm row)
    {
        if (_syncing) return;
        if (row.Tag is FileSeriesGroup ser) { OnSeriesChanged(row, ser); return; }
        if (row.Tag is not SpaceFile f) return;
        if (row.IsChecked)
        {
            var trial = new HashSet<string>(_marks, StringComparer.OrdinalIgnoreCase) { f.Path };
            if (_dups != null && DuplicatePairs.FirstFullyMarked(_dups, trial) is { } g)
            {
                var msg = string.Format(Strings.DupAllChecked, Path.GetFileName(g.Paths[0]));
                void Revert() { var was = _syncing; _syncing = true; try { row.IsChecked = false; } finally { _syncing = was; } SetResult(msg, true); }
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp != null) disp.BeginInvoke(Revert); else Revert();         // галочку возвращаем после того, как WPF закончит обработку нажатия
                return;
            }
            _marks.Add(f.Path);
        }
        else _marks.Remove(f.Path);
        SetRowsChecked(f.Path, row.IsChecked, row);
        RefreshSeriesRows();
        Recalc();
    }

    /// Галочка на серии — отметить/снять все её файлы. Если из-за этого в группе дубликатов не осталось бы ни одной копии, лишние отметки снимаются.
    private void OnSeriesChanged(RowVm row, FileSeriesGroup ser)
    {
        var on = row.IsChecked;
        // Чужие (уже стоящие) отметки не трогаем: файлы серии добавляются по одному, пока в их группе остаётся неотмеченная копия.
        if (on) _marks.UnionWith(_dups != null ? DuplicatePairs.AddKeepingOne(_dups, _marks, ser.Files.Select(f => f.Path)) : ser.Files.Select(f => f.Path));
        else foreach (var f in ser.Files) _marks.Remove(f.Path);
        var skipped = on ? ser.Files.Count(f => !_marks.Contains(f.Path)) : 0;
        foreach (var f in ser.Files) SetRowsChecked(f.Path, _marks.Contains(f.Path));
        RefreshSeriesRows();
        Recalc();
        if (skipped > 0) SetResult(string.Format(Strings.SeriesKeepOne, skipped), true);
    }

    /// Галочка серии = отмечены все её файлы (после ручных отметок отдельных файлов).
    private void RefreshSeriesRows()
    {
        var was = _syncing; _syncing = true;
        try { foreach (var r in Items.OfType<RowVm>()) if (r.Tag is FileSeriesGroup g) r.IsChecked = g.Files.All(f => _marks.Contains(f.Path)); }
        finally { _syncing = was; }
    }

    private void Recalc()
    {
        var rows = Checked(); long sum = rows.Sum(r => r.Bytes);
        _trash.Text = rows.Count == 0 ? Strings.ToRecycle : $"{Strings.ToRecycle} ({rows.Count}, {SizeFormat.Human(sum)})";
    }

    protected override Task CheckAsync() => RunBusyAsync(Strings.SpaceScanning, async (ct, progress) =>
    {
        _marks.Clear(); _openSeries.Clear();                           // после нового сканирования старые отметки не действуют (файлов может уже не быть на экране)
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        _folders = await Task.Run(() => ScanFolders(drive, ct, progress), ct);
        var counter = new ScanCounter(progress, "Крупные файлы");
        progress.Report(Strings.BigFilesScanning);
        // Профиль и личные папки, перенесённые на другой диск (Загрузки, Документы…): там тоже бывают крупные файлы.
        var bigRoots = new[] { Profile }.Concat(KnownFolders.OutsideProfile(Profile, KnownFolders.Personal())).ToList();
        _big = (await Task.Run(() => bigRoots.SelectMany(r => DiskMap.TopFiles(r, 60, BigFileBytes, ct, d => string.Equals(d, AppDataDir, StringComparison.OrdinalIgnoreCase), null, counter)).OrderByDescending(f => f.Bytes).Take(60).ToList(), ct))
            .Select(f => new SpaceFile(f.Path, f.Bytes)).ToList();
        counter.Flush();
        var downloads = KnownFolders.DownloadsDir();
        _old = (await Task.Run(() => DiskMap.OldDownloads(downloads, 30), ct)).Select(f => new SpaceFile(f.Path, f.Bytes)).ToList();
        Rebuild();
        SetResult(Strings.SpaceScanned);
    });

    /// Верхние папки диска и по две самые большие внутри каждой. Корень диска обходим сами.
    private static List<Folder> ScanFolders(string drive, CancellationToken ct, IProgress<string> progress)
    {
        var counter = new ScanCounter(progress, "Папки диска");
        var res = new List<Folder>();
        IEnumerable<DirectoryInfo> tops;
        try { tops = new DirectoryInfo(drive).EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return res; }
        foreach (var d in tops)
        {
            ct.ThrowIfCancellationRequested();
            var kids = DiskMap.TopFolders(d.FullName, 1, int.MaxValue, ct, null, null, counter);
            long direct = 0;
            try { foreach (var f in d.EnumerateFiles()) { try { direct += f.Length; counter.File(f.Length); } catch (IOException) { } } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var total = kids.Sum(k => k.Bytes) + direct;
            if (total > 0) res.Add(new Folder(d.FullName, total, kids.Take(2).Select(k => (k.Path, k.Bytes)).ToList()));
        }
        counter.Flush();
        return res.OrderByDescending(f => f.Bytes).Take(10).ToList();
    }

    private async Task DuplicatesAsync()
    {
        await RunBusyAsync(Strings.DupScanning, async (ct, progress) =>
        {
            var roots = KnownFolders.Personal().Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var counter = new ScanCounter(new SyncProgress<string>(progress.Report), "Дубликаты");
            var found = await Task.Run(() => Duplicates.Find(roots, Duplicates.OneMb, ct, null, null, counter), ct);
            _dups = found.ToList();
            _showDupDetail = null; _dupShown = DupDetailStep;
            _marks.Clear(); _openSeries.Clear();
            Rebuild();
            SetResult(found.Count == 0 ? Strings.DupNone : string.Format(Strings.DupFound, found.Count, SizeFormat.Human(found.Sum(g => g.Wasted))));
        });
        if (_dupHeader != null && _dups != null) RequestScrollTo(_dupHeader);          // результат поиска — внизу раздела: показываем его, а не начало
        else RequestScrollTop();
    }

    private HeaderVm? _dupHeader;

    private void Rebuild()
    {
        Items.Clear(); _dupHeader = null;
        if (_folders != null)
        {
            Items.Add(new HeaderVm(Strings.BiggestFolders, Strings.BiggestFoldersSub));
            foreach (var f in _folders)
            {
                Items.Add(FolderRow(f.Path, f.Bytes, 0));
                foreach (var c in f.Children) Items.Add(FolderRow(c.Path, c.Bytes, 22));
            }
        }
        if (_big != null)
        {
            Items.Add(new HeaderVm(Strings.BigFiles, Strings.BigFilesSub));
            if (_big.Count == 0) Items.Add(new RowVm { Title = Strings.NothingFound });
            var (series, singles) = FileSeries.Split(_big.Select(b => new FileSize(b.Path, b.Bytes)));
            // Серии (media-001.zip … media-015.zip) — одной строкой с галочкой на всю серию; всего строк не больше 15.
            var entries = series.Select(g => (Bytes: g.Bytes, Series: (FileSeriesGroup?)g, File: (FileSize?)null))
                                .Concat(singles.Select(f => (Bytes: f.Bytes, Series: (FileSeriesGroup?)null, File: (FileSize?)f)))
                                .OrderByDescending(e => e.Bytes).Take(MaxBigRows).ToList();
            foreach (var e in entries)
            {
                if (e.Series is { } g) { AddSeriesRows(g); continue; }
                Items.Add(FileRow(new SpaceFile(e.File!.Path, e.File.Bytes), 0));
            }
        }
        if (_old != null)
        {
            Items.Add(new HeaderVm(Strings.OldInstallers, Strings.OldInstallersSub));
            if (_old.Count == 0) Items.Add(new RowVm { Title = Strings.NothingFound });
            foreach (var f in _old) Items.Add(FileRow(f, 0));
        }
        if (_dups != null)
        {
            Items.Add(_dupHeader = new HeaderVm(Strings.Duplicates, Strings.DuplicatesSub));
            if (_dups.Count == 0) Items.Add(new RowVm { Title = Strings.NothingFound });
            else
            {
                // Сначала сводка по парам папок (можно отметить все копии в одной из двух папок одним нажатием), ниже — подробный список групп.
                var pairs = DuplicatePairs.Summarize(_dups);
                foreach (var pr in pairs.Take(MaxPairRows)) Items.Add(PairRow(pr));
                if (pairs.Count > MaxPairRows) Items.Add(new RowVm { Title = string.Format(Strings.DupMorePairs, pairs.Count - MaxPairRows) });
                var open = _showDupDetail ?? (_dups.Count <= DupDetailAutoOpen);
                var shown = Math.Min(_dups.Count, _dupShown);
                var toggle = new RowVm { Title = open ? Strings.DupDetailTitle : string.Format(Strings.DupDetailClosed, _dups.Count),
                                         Detail = open && _dups.Count > shown ? string.Format(Strings.DupDetailCap, shown, _dups.Count) : "" };
                toggle.Buttons.Add(new ActionVm(open ? Strings.Collapse : Strings.Show, RelayCommand.Sync(() => { _showDupDetail = !open; Rebuild(); })));
                Items.Add(toggle);
                if (open)
                    foreach (var g in _dups.Take(shown))
                    {
                        var head = new RowVm { Title = $"{g.Paths.Count} {RuText.Plural(g.Paths.Count, "одинаковый файл", "одинаковых файла", "одинаковых файлов")} по {SizeFormat.Human(g.Size)}", Detail = $"лишние копии займут {SizeFormat.Human(g.Wasted)}" };
                        head.SetPill(SizeFormat.Human(g.Wasted), "Warn");
                        Items.Add(head);
                        foreach (var p in g.Paths) Items.Add(FileRow(new SpaceFile(p, g.Size), 22, g));
                    }
                if (open && _dups.Count > shown)
                {
                    var more = new RowVm { Title = string.Format(Strings.DupMoreGroups, Math.Min(DupDetailStep, _dups.Count - shown), _dups.Count - shown) };
                    more.Buttons.Add(new ActionVm(Strings.Show, RelayCommand.Sync(() => { _dupShown += DupDetailStep; Rebuild(); })));
                    Items.Add(more);
                }
            }
        }
        Recalc(); Raise(nameof(ShowEmpty));
    }

    private RowVm FolderRow(string path, long bytes, double indent)
    {
        var row = new RowVm { Title = indent > 0 ? Path.GetFileName(path) : path, Indent = indent, Size = SizeFormat.Human(bytes), Detail = SystemFolderNotes.Explain(path) ?? "" };
        row.Buttons.Add(new ActionVm(Strings.ShowInExplorer, RelayCommand.Sync(() => MaintenanceVm.ShowInExplorer(path))));
        return row;
    }

    public const int MaxBigRows = 15;
    private readonly HashSet<string> _openSeries = new(StringComparer.OrdinalIgnoreCase);

    /// Строка серии («media-….zip × 15 · 30 ГБ»): галочка на всю серию, «Показать»/«Свернуть» список файлов серии.
    private void AddSeriesRows(FileSeriesGroup g)
    {
        var key = g.Folder + "|" + g.Title;
        var open = _openSeries.Contains(key);
        var row = new RowVm { Title = $"{g.Title} × {g.Count}", Detail = $"{Friendly(g.Folder)} · серия из {g.Count} файлов", Size = SizeFormat.Human(g.Bytes), Checkable = true, Tag = g };
        row.CanCheck = true;
        row.IsChecked = g.Files.All(f => _marks.Contains(f.Path));
        row.Changed = () => OnRowChanged(row);
        row.Buttons.Add(new ActionVm(open ? Strings.Collapse : Strings.SeriesFiles, RelayCommand.Sync(() => { if (!_openSeries.Remove(key)) _openSeries.Add(key); Rebuild(); })));
        Items.Add(row);
        if (open) foreach (var f in g.Files) Items.Add(FileRow(new SpaceFile(f.Path, f.Bytes), 22));
    }

    public const int MaxPairRows = 12, DupDetailAutoOpen = 50, DupDetailStep = 50;
    private int _dupShown = DupDetailStep;                       // сколько групп показано в подробном списке (по 50, остальное — «Показать ещё»)

    private static string Leaf(string folder) => Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder;

    /// Строка пары папок: «Загрузки\t5.dra ↔ Загрузки\m» — «312 одинаковых файлов, 6,1 ГБ» — кнопки «Отметить копии в …».
    private RowVm PairRow(FolderPair p)
    {
        var count = RuText.Count(p.Groups, "одинаковый файл", "одинаковых файла", "одинаковых файлов");
        var row = new RowVm
        {
            Title = p.SameFolder ? $"{Friendly(p.A)} (копии внутри одной папки)" : $"{Friendly(p.A)} ↔ {Friendly(p.B)}",
            Detail = $"{count}, {SizeFormat.Human(p.Bytes)}. {(p.SameFolder ? Strings.DupSameHint : Strings.DupPairHint)}", ButtonsBelow = true,
        };
        if (p.SameFolder) row.Buttons.Add(new ActionVm(Strings.DupMarkExtra, RelayCommand.Sync(() => MarkCopies(p, p.A))));
        else
        {
            row.Buttons.Add(new ActionVm(string.Format(Strings.DupMarkIn, Leaf(Friendly(p.B))), RelayCommand.Sync(() => MarkCopies(p, p.B))));
            row.Buttons.Add(new ActionVm(string.Format(Strings.DupMarkIn, Leaf(Friendly(p.A))), RelayCommand.Sync(() => MarkCopies(p, p.A))));
        }
        return row;
    }

    /// Отметить копии в одной папке пары. Из каждой группы остаётся хотя бы одна копия — это проверяет DuplicatePairs.CopiesToMark (повторное нажатие ничего не добавит).
    private void MarkCopies(FolderPair p, string target)
    {
        if (_dups == null) return;
        var add = DuplicatePairs.CopiesToMark(_dups, p.A, p.B, target, _marks);
        foreach (var path in add) { _marks.Add(path); SetRowsChecked(path, true); }
        RefreshSeriesRows();
        Recalc();
        var sizes = KnownSizes();
        SetResult(add.Count == 0 ? Strings.DupNothingToMark
                                 : string.Format(Strings.DupMarked, RuText.Count(add.Count, "копия", "копии", "копий"), Friendly(target), SizeFormat.Human(add.Sum(x => sizes.GetValueOrDefault(x)))));
    }

    private RowVm FileRow(SpaceFile f, double indent, DuplicateGroup? group = null)
    {
        RowVm row = null!;
        row = new RowVm { Title = Path.GetFileName(f.Path), Detail = Path.GetDirectoryName(f.Path) ?? "", Indent = indent, Size = SizeFormat.Human(f.Bytes), Checkable = true, Tag = f };
        row.CanCheck = true;
        if (_marks.Contains(f.Path)) row.IsChecked = true;
        row.Changed = () => OnRowChanged(row);
        var p = f.Path;
        row.Buttons.Add(new ActionVm(Strings.ShowInExplorer, RelayCommand.Sync(() => MaintenanceVm.ShowInExplorer(p))));
        return row;
    }

    private async Task TrashAsync()
    {
        var files = Checked();
        if (files.Count == 0) return;
        // Страховка: все копии одного файла отмечать нельзя — из группы дубликатов должна остаться хотя бы одна (обычно до сюда не доходит: отметка не принимается раньше).
        if (_dups != null && DuplicatePairs.FirstFullyMarked(_dups, files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)) is { } full)
        { ConfirmDialog.Info(string.Format(Strings.DupAllChecked, Path.GetFileName(full.Paths[0]))); return; }
        var sum = files.Sum(f => f.Bytes);
        if (!ConfirmDialog.Ask(string.Format(Strings.TrashConfirm, files.Count, SizeFormat.Human(sum)))) return;
        await RunBusyAsync(Strings.Moving, async (ct, progress) =>
        {
            var paths = files.Select(f => f.Path).ToList();
            var res = await Task.Run(() => SafeDelete.ToRecycleBin(paths, null, KnownFolders.RecycleRoots(Profile, KnownFolders.Personal()), new[] { AppDataDir }), ct);
            var moved = new HashSet<string>(res.Moved, StringComparer.OrdinalIgnoreCase);
            _big?.RemoveAll(f => moved.Contains(f.Path)); _old?.RemoveAll(f => moved.Contains(f.Path));
            if (_dups != null)
            {
                foreach (var g in _dups.ToList())
                {
                    var left = g.Paths.Where(p => !moved.Contains(p)).ToList();
                    _dups.Remove(g);
                    if (left.Count > 1) _dups.Add(g with { Paths = left });
                }
            }
            _marks.ExceptWith(moved);
            Rebuild();
            var movedBytes = files.Where(f => moved.Contains(f.Path)).Sum(f => f.Bytes);
            SetResult(string.Format(Strings.MovedToBin, res.Moved.Count, SizeFormat.Human(movedBytes)) + (res.Skipped.Count > 0 ? $", {Strings.NotMoved} {res.Skipped.Count}" : ""),
                      res.Skipped.Count > 0, string.Join("\n", res.Skipped.Take(8).Select(s => $"• {Path.GetFileName(s.Path)} — {s.Reason}")));
        });
    }

    public void LoadDemo()
    {
        _folders = new List<Folder>
        {
            new(@"C:\Users", 118_000_000_000, new[] { (@"C:\Users\User", 117_000_000_000L), (@"C:\Users\Public", 600_000_000L) }),
            new(@"C:\Windows", 27_400_000_000, new[] { (@"C:\Windows\WinSxS", 9_100_000_000L), (@"C:\Windows\System32", 6_800_000_000L) }),
            new(@"C:\Program Files", 19_200_000_000, new[] { (@"C:\Program Files\WindowsApps", 11_000_000_000L), (@"C:\Program Files\WindowsApps.tmp", 8_300_000_000L) }),
        };
        _big = new() { new(@"C:\Users\User\Videos\obs\2026-08-12 21-05-44.mkv", 4_300_000_000), new(@"C:\Users\User\Downloads\Win11_25H2_Russian_x64.iso", 6_100_000_000) };
        for (var i = 1; i <= 15; i++) _big.Add(new(Path.Combine(KnownFolders.DownloadsDir(), $"media-8f3a2c-{i:000}.zip"), 2_000_000_000));
        _old = new() { new(@"C:\Users\User\Downloads\node-v20.11-x64.msi", 27_000_000), new(@"C:\Users\User\Downloads\setup-old.exe", 88_000_000) };
        var dl = KnownFolders.DownloadsDir();
        _dups = new();
        for (var i = 0; i < 42; i++) _dups.Add(new DuplicateGroup(20_000_000 + i * 1000, "a" + i, new[] { Path.Combine(dl, "t5.dra", $"part{i}.bin"), Path.Combine(dl, "m", $"part{i}.bin") }));
        for (var i = 0; i < 12; i++) _dups.Add(new DuplicateGroup(8_000_000 + i * 1000, "b" + i, new[] { Path.Combine(dl, $"scan{i}.pdf"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"scan{i}.pdf") }));
        for (var i = 0; i < 6; i++) _dups.Add(new DuplicateGroup(3_000_000 + i * 1000, "c" + i, new[] { Path.Combine(dl, "m", $"x{i}.zip"), Path.Combine(dl, "m", $"x{i} (1).zip") }));
        Rebuild();
        if (Items.OfType<RowVm>().FirstOrDefault(r => r.Checkable) is { } first) first.IsChecked = true;
        SetResult(Strings.SpaceScanned);
    }
}
