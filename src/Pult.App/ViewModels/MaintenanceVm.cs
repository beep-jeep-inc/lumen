using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Pult.App.Services;
using Pult.Core.Maintenance;

namespace Pult.App.ViewModels;

/// Результат проверки winget, общий для разделов «Системные библиотеки» и «Программы»: одна проверка — оба списка.
public sealed class WingetScan
{
    public bool Running { get; private set; }
    private int _jobs;
    /// Идёт поиск ИЛИ установка/обновление из любого раздела: второй winget и перестройка списков посреди работы не нужны.
    public bool Busy => Running || _jobs > 0;
    public void BeginJobs() { _jobs++; CommandManager.InvalidateRequerySuggested(); }
    public void EndJobs() { _jobs = Math.Max(0, _jobs - 1); CommandManager.InvalidateRequerySuggested(); }
    public IReadOnlyList<WingetRow>? Rows { get; private set; }
    /// Почему нет списка (winget не найден, не ответил…); null — проверка прошла.
    public string? Error { get; private set; }
    public bool Missing { get; private set; }
    public event Action? Done;

    public async Task RunAsync(CancellationToken ct, IProgress<string>? progress)
    {
        if (Running) return;
        Running = true; CommandManager.InvalidateRequerySuggested(); progress?.Report(Strings.WingetChecking);
        try
        {
            var exe = await Task.Run(Winget.Find);
            Missing = exe == null;
            if (exe == null) { Rows = null; Error = Strings.WingetMissing; return; }
            var ver = await Winget.VersionAsync(exe, ct);
            if (Winget.IsTooOld(ver)) { Missing = true; Rows = null; Error = Strings.WingetOld; return; }     // Missing — признак «показать кнопку App Installer»
            var run = await WingetRunner.ListUpgradesAsync(exe, ct, ver);
            var rows = WingetTable.Parse(run.Output);
            // «Нет обновлений»: winget печатает фразу без таблицы — это не ошибка
            if (rows.Count == 0 && !run.Ok && run.ExitCode != WingetRunner.NoApplicableUpdate && !run.Output.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
                && !run.Output.Contains("Не найдено", StringComparison.OrdinalIgnoreCase) && !run.Output.Contains("Установленные пакеты", StringComparison.OrdinalIgnoreCase))
            { Rows = null; Error = Strings.WingetFailed + WingetRunner.DescribeFailure(run); return; }
            Rows = rows; Error = null;
        }
        finally { Running = false; CommandManager.InvalidateRequerySuggested(); Done?.Invoke(); }
    }

    public void Set(IReadOnlyList<WingetRow> rows) { Rows = rows; Error = null; Missing = false; Done?.Invoke(); }
}

/// Пункт бокового меню: «Обзор» (Section = null) или один из разделов.
public sealed class NavItemVm : Observable
{
    public NavItemVm(string title, string icon, SectionVm? section, string glyph = "")
    {
        Title = title; Icon = icon; Section = section; Glyph = glyph;
        if (section != null) section.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SectionVm.Badge)) Raise(nameof(Badge)); };
    }
    public string Title { get; }
    public string Icon { get; }
    public string Glyph { get; }
    public SectionVm? Section { get; }
    public string Badge => Section?.Badge ?? "";
    private bool _selected; public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// Команда с параметром (выбор раздела из меню и плиток).
public sealed class ParamCommand(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => run(p);
}

/// Окно «Обслуживание»: меню слева выбирает видимый раздел (или «Обзор» — плитки по всем разделам).
/// Работа разделов прежняя: при открытии — только дешёвое чтение, поиск в winget и Центре обновления и сканы — только по кнопке. При открытии — только дешёвое чтение (библиотеки, видеокарты, устройства с ошибками);
/// поиск в winget и Центре обновления, сканы очистки и проверка дисков — только по кнопке. Автозагрузка при открытии только читает реестр.
public sealed class MaintenanceVm : Observable
{
    public WingetScan Winget { get; } = new();
    public IReadOnlyList<SectionVm> Sections { get; }
    public DriversSection Drivers { get; }
    public LibrariesSection Libraries { get; }
    public ProgramsSection Programs { get; }
    public CleanupSection Cleanup { get; }
    public SpaceSection Space { get; }
    public StartupSection Startup { get; }
    public DisksSection Disks { get; }
    public RepairSection Repair { get; }
    public ICommand CloseBannerCommand { get; }

    // ---- «Проверить всё»: только лёгкие чтения (QuickCheckPlan), по одному, без окон ----
    private Pult.Core.Maintenance.QuickCheckRunner _quick = null!;
    public event Action? QuickChanged;
    public ICommand QuickCheckCommand { get; }
    public ICommand QuickStopCommand { get; }
    public bool QuickRunning => _quick.Running;
    internal Task QuickAsync() => _quick.RunAsync();
    public bool QuickIdle => !_quick.Running;
    private static SectionVm? Owner(Pult.Core.Maintenance.IQuickStep? st) => st is PreviewStep p ? p.Section : st as SectionVm;
    public SectionVm? QuickCurrent => Owner(_quick.Current);
    public bool QuickIsQueued(SectionVm s) => _quick.Steps.Any(st => Owner(st) == s && _quick.IsQueued(st));

    /// Режим --preview: та же очередь, но шаги ничего не читают; шаг freezeAt «идёт» бесконечно (снимок «во время проверки»), остальные завершаются сразу.
    internal Task PreviewQuick(int freezeAt)
    {
        _quick.Changed -= OnQuickChanged;
        SetQuick(new Pult.Core.Maintenance.QuickCheckRunner(Pult.Core.Maintenance.QuickCheckPlan.Order.Select((k, i) => (Pult.Core.Maintenance.IQuickStep)new PreviewStep(ByKey(k), i == freezeAt)).ToList()));
        var t = _quick.RunAsync(); OnQuickChanged(); return freezeAt >= 0 ? Task.CompletedTask : t;
    }
    private sealed class PreviewStep(SectionVm section, bool freeze) : Pult.Core.Maintenance.IQuickStep
    {
        public SectionVm Section { get; } = section;
        public string Title => Section.Title;
        public Task RunAsync() => freeze ? Task.Delay(Timeout.Infinite) : Task.CompletedTask;
        public void Cancel() { }
        public bool NeedsAction => Section.NeedsAction;
        public bool NeedsManual => Section.NeedsManual;
    }
    public string QuickProgress => _quick.ProgressText;
    public string QuickSummary => _quick.Summary?.Text ?? "";
    public bool ShowQuickSummary => !_quick.Running && _quick.Summary != null;
    public bool ShowQuickHint => !_quick.Running && _quick.Summary == null;
    /// Итог без отметок о неудаче — зелёный, «есть что сделать» — золотой, остановлено — нейтральный.
    public string QuickSummaryState => _quick.Summary is not { } r ? "None" : r.Cancelled ? "Off" : r.NeedAction > 0 ? "Warn" : r.Manual > 0 ? "Off" : "Ok";
    private void SetQuick(Pult.Core.Maintenance.QuickCheckRunner runner)
    {
        _quick = runner; runner.Changed += OnQuickChanged;
    }
    private void OnQuickChanged()
    {
        foreach (var n in new[] { nameof(QuickRunning), nameof(QuickIdle), nameof(QuickProgress), nameof(QuickSummary), nameof(ShowQuickSummary), nameof(ShowQuickHint), nameof(QuickSummaryState) }) Raise(n);
        QuickChanged?.Invoke();
        CommandManager.InvalidateRequerySuggested();
    }
    private SectionVm ByKey(string key) => key switch
    {
        Pult.Core.Maintenance.QuickCheckPlan.Startup => Startup, Pult.Core.Maintenance.QuickCheckPlan.Disks => Disks, Pult.Core.Maintenance.QuickCheckPlan.Libraries => Libraries,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "раздел не входит в быструю проверку"),
    };
    private bool CanQuick() => !_quick.Running && !AnyBusy && !Winget.Busy;
    /// Идёт ли работа хотя бы в одном разделе (закрытие окна посреди неё обрывает, например, «Переустановить»).
    public bool AnyBusy => Sections.Any(s => s.IsBusy);
    /// Название первого занятого раздела; null — все свободны.
    public string? BusyTitle => Sections.FirstOrDefault(s => s.IsBusy)?.Title;
    public IReadOnlyList<NavItemVm> Nav { get; }
    public ICommand SelectCommand { get; }

    private SectionVm? _current;
    /// Выбранный раздел; null — «Обзор».
    public SectionVm? Current => _current;
    /// Что показывает правая часть: раздел или (для «Обзора») сама модель окна.
    public object CurrentView => (object?)_current ?? this;

    public void Select(SectionVm? section)
    {
        _current = section;
        foreach (var n in Nav) n.IsSelected = n.Section == section;
        Log.Section = section?.Title ?? "Обзор";
        Raise(nameof(Current)); Raise(nameof(CurrentView));
    }

    /// Для режима --preview: overview | drivers | libraries | programs | cleanup | space | startup | disks | repair.
    public void SelectByName(string name)
    {
        SectionVm? s = name.ToLowerInvariant() switch
        {
            "overview" => null, "drivers" => Drivers, "libraries" => Libraries, "programs" => Programs, "cleanup" => Cleanup, "space" => Space, "startup" => Startup, "disks" => Disks, "repair" => Repair,
            _ => Sections[0],
        };
        Select(s);
    }

    public string Greeting => DateTime.Now.Hour switch { >= 5 and < 12 => "Доброе утро", >= 12 and < 18 => "Добрый день", >= 18 => "Добрый вечер", _ => "Доброй ночи" };
    public string Eyebrow => "Обслуживание · " + Today;
    public string Today => DateTime.Now.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    private string _banner = ""; public string Banner { get => _banner; private set => Set(ref _banner, value); }

    /// Галочка «Запускать вместе с Windows»: сразу сохраняет настройки и пишет или снимает ключ автозапуска.
    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            Set(ref _startWithWindows, value);
            var svc = App.Services;
            svc.Config.StartWithWindows = value;
            svc.Save();
            try { Autostart.Set(value); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { Log.Error("автозапуск", e); ShowBanner("Не удалось изменить автозапуск: " + e.Message); }
        }
    }

    public MaintenanceVm()
    {
        Drivers = new DriversSection(this); Libraries = new LibrariesSection(this); Programs = new ProgramsSection(this);
        Cleanup = new CleanupSection(this); Space = new SpaceSection(this);
        Startup = new StartupSection(this); Disks = new DisksSection(this); Repair = new RepairSection(this);
        Sections = new SectionVm[] { Drivers, Libraries, Programs, Cleanup, Space, Startup, Disks, Repair };
        CloseBannerCommand = RelayCommand.Sync(() => Banner = "");
        SetQuick(new Pult.Core.Maintenance.QuickCheckRunner(Pult.Core.Maintenance.QuickCheckPlan.Order.Select(k => (Pult.Core.Maintenance.IQuickStep)ByKey(k)).ToList()));
        QuickCheckCommand = new RelayCommand(() => { Banner = ""; return _quick.RunAsync(); }, CanQuick);
        QuickStopCommand = RelayCommand.Sync(() => _quick.Cancel(), () => _quick.Running);
        // Значки Segoe Fluent Icons (Segoe MDL2 Assets на Windows 10) — коды из спецификации дизайна
        Drivers.Glyph = ""; Drivers.CheckGlyph = ""; Libraries.Glyph = ""; Programs.Glyph = ""; Cleanup.Glyph = ""; Space.Glyph = ""; Space.CheckGlyph = "";
        Startup.Glyph = ""; Disks.Glyph = ""; Repair.Glyph = "";
        var nav = new List<NavItemVm> { new("Обзор", "🏠", null, "") };
        nav.AddRange(Sections.Select(x => new NavItemVm(x.Title, x.Icon, x, x.Glyph)));
        Nav = nav; nav[0].IsSelected = true;
        SelectCommand = new ParamCommand(p => Select(p as SectionVm));
        _startWithWindows = App.Services.Config.StartWithWindows && Autostart.IsOnSafe;
        App.Services.SaveFailed += ShowBanner;
    }

    public void ShowBanner(string text) => Banner = text;

    private Task? _opened;
    /// Первое открытие окна: дешёвое чтение, ничего тяжёлого. Повторный вызов ждёт то же чтение.
    public Task OpenedAsync() => _opened ??= Task.WhenAll(Drivers.LoadCheapAsync(), Libraries.LoadCheapAsync(), Startup.LoadCheapAsync());

    /// Режим --preview: вымышленные данные для всех разделов.
    public void LoadDemo()
    {
        Drivers.LoadDemo(); Libraries.LoadDemo(); Programs.LoadDemo(); Cleanup.LoadDemo(); Space.LoadDemo(); Startup.LoadDemo(); Disks.LoadDemo(); Repair.LoadDemo();
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Log.Error("открыть " + url, e); }
    }

    public static void ShowInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false }); }
        catch (Exception e) { Log.Error("проводник", e); }
    }
}
