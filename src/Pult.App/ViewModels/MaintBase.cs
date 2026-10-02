using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Pult.App.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Pult.App.ViewModels;

/// Подзаголовок внутри карточки раздела («Видеокарты», «Система»…).
public sealed class HeaderVm(string text, string sub = "")
{
    public string Text { get; } = text;
    public string Sub { get; } = sub;
}

/// Кнопка карточки или строки. Text/Visible меняются по ходу работы («Удалить отмеченное (3,2 ГБ)»).
public sealed class ActionVm : Observable
{
    public ActionVm(string text, ICommand command, bool primary = false, bool visible = true) { _text = text; Command = command; _primary = primary; _visible = visible; }
    public ICommand Command { get; }
    private bool _primary; public bool Primary { get => _primary; set { if (Set(ref _primary, value)) Raise(nameof(Secondary)); } }
    public bool Secondary => !Primary;
    private string _text; public string Text { get => _text; set { if (Set(ref _text, value)) Raise(nameof(AutomationName)); } }
    /// Строка списка, на которой стоит кнопка (проставляется автоматически): диктору и автотестам — «Показать: Загрузки», а не просто «Показать».
    public RowVm? Owner { get; internal set; }
    public string AutomationName => Owner is { Title.Length: > 0 } o ? $"{_text}: {o.Title}" : _text;
    internal void OwnerChanged() => Raise(nameof(AutomationName));
    private bool _visible; public bool Visible { get => _visible; set => Set(ref _visible, value); }
}

/// Заметное предупреждение внутри списка (золотая плашка, не серый мелкий текст).
public sealed class NoticeVm(string title, string text)
{
    public string Title { get; } = title;
    public string Text { get; } = text;
}

/// Строка списка: галочка (необязательная), название, пояснение, плашка, размер, щит (нужен помощник), кнопки справа.
public sealed class RowVm : Observable
{
    private static Brush Frozen(string hex) { var b = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Dictionary<string, (Brush Bg, Brush Fg)> Pills = new()
    {
        ["Ok"] = (Frozen("#19382F"), Frozen("#5CC98B")), ["Warn"] = (Frozen("#302A27"), Frozen("#F2B84B")),
        ["Err"] = (Frozen("#372338"), Frozen("#F59AA5")), ["Off"] = (Frozen("#2C314C"), Frozen("#AAB2CE")),
    };

    public string Title { get; init; } = "";
    public double Indent { get; init; }
    public object? Tag { get; init; }
    public bool Checkable { get; init; }
    /// В списке с галочками строки без галочки сохраняют место под неё, чтобы названия стояли ровно.
    public bool Reserve { get; init; }
    public System.Windows.Visibility CheckVisibility => Checkable ? System.Windows.Visibility.Visible : Reserve ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Collapsed;
    /// Длинные кнопки (выход из неудачи) — строкой под текстом, а не справа.
    private bool _below; public bool ButtonsBelow { get => _below; set => Set(ref _below, value); }
    public bool Shield { get; init; }
    /// Значок строки (Segoe Fluent Icons), необязательный.
    public string Glyph { get; init; } = "";
    /// Строка с переключателем вместо кнопки «Включить/Выключить» (автозагрузка): тот же ActionVm, та же команда, подтверждения и помощник те же.
    public bool UseToggle { get; init; }
    private bool _toggleOn; public bool ToggleOn { get => _toggleOn; set => Set(ref _toggleOn, value); }
    private string _toggleText = ""; public string ToggleText { get => _toggleText; set => Set(ref _toggleText, value); }
    /// Пока команда переключателя выполняется: подпись «Меняю…», повторное нажатие закрыто.
    private bool _toggleBusy; public bool ToggleBusy { get => _toggleBusy; set => Set(ref _toggleBusy, value); }
    public string ShieldTip { get; init; } = "Очистит помощник администратора (окно «Да»)";
    /// «Есть что обновить» — считается в плитку и счётчик меню.
    public bool Actionable { get; init; }
    public ObservableCollection<ActionVm> Buttons { get; }
    public RowVm()
    {
        Buttons = new();
        Buttons.CollectionChanged += (_, e) => { if (e.NewItems != null) foreach (ActionVm a in e.NewItems) { a.Owner = this; a.OwnerChanged(); } };
    }
    /// Зовётся при смене галочки (раздел пересчитывает подпись главной кнопки).
    public Action? Changed { get; set; }

    private string _detail = ""; public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string _size = ""; public string Size { get => _size; set => Set(ref _size, value); }
    private string _pill = ""; public string Pill { get => _pill; private set => Set(ref _pill, value); }
    private string _kind = "Off";
    public Brush PillBackground => Pills[_kind].Bg;
    public Brush PillForeground => Pills[_kind].Fg;
    public void SetPill(string text, string kind)
    {
        _kind = Pills.ContainsKey(kind) ? kind : "Off"; Pill = text; Raise(nameof(PillBackground)); Raise(nameof(PillForeground));
    }

    private bool _enabled = true; public bool CanCheck { get => _enabled; set { if (Set(ref _enabled, value)) { Raise(nameof(Opacity)); if (!value) IsChecked = false; } } }
    public double Opacity => _enabled ? 1 : 0.55;
    private bool _checked;
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) Changed?.Invoke(); } }
}

/// Карточка раздела страницы «Обслуживание»: заголовок, кнопка «Проверить»/«Отмена», полоса прогресса, список, итог, кнопки действий.
public abstract class SectionVm : Observable, Pult.Core.Maintenance.IQuickStep
{
    protected readonly MaintenanceVm Host;
    private CancellationTokenSource? _cts;

    protected SectionVm(MaintenanceVm host, string icon, string title, string hint, string checkText = "Проверить")
    {
        Host = host; Icon = icon; Title = title; Hint = hint; CheckText = checkText;
        CheckCommand = new RelayCommand(async () => { await GuardAsync(CheckAsync); RequestScrollTop(); }, () => CanRun);
        CancelCommand = RelayCommand.Sync(() => _cts?.Cancel());
        UpdateHelperAction = new ActionVm(Strings.UpdateHelper, new RelayCommand(() => GuardAsync(UpdateHelperAsync)), false, false);
        Items.CollectionChanged += (_, _) => { Raise(nameof(HasItems)); Raise(nameof(ShowEmpty)); RaiseTile(); };
        host.QuickChanged += RaiseTile;
        Actions.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (ActionVm a in e.NewItems) a.PropertyChanged += (_, p) => { if (p.PropertyName == nameof(ActionVm.Visible)) Raise(nameof(ShowFooter)); };
            Raise(nameof(ShowFooter));
        };
    }

    /// Нижняя панель действий (итог, прогресс, кнопки) нужна, только если в ней что-то есть.
    public bool ShowFooter => _busy || _result.Length > 0 || _notes.Length > 0 || Actions.Any(a => a.Visible);

    // ---- Плитка «Обзора» и счётчик в меню: только из уже имеющихся данных раздела, новых сканов не запускают ----
    /// Сколько пунктов ждёт действия: null — раздел ещё не проверяли, 0 — всё в порядке.
    protected virtual int? PendingCount => null;
    /// Подпись под числом, когда есть что сделать / когда всё в порядке.
    protected virtual string TileMany => "ждёт действия";
    /// Подпись с правильным склонением по числу; по умолчанию — постоянный TileMany.
    protected virtual string TileManyFor(int n) => TileMany;
    protected virtual string TileNone => "всё в порядке";
    protected virtual string TileUnknown => "ещё не проверялось";
    /// Показывать ли счётчик в меню (у «Что занимает место» нет «дел» — только сведения).
    protected virtual bool ShowBadge => true;
    public virtual string TileValue => PendingCount switch { null => "—", 0 => "✓", int n => n.ToString() };
    public virtual string TileCaption => PendingCount switch { null => TileUnknown, 0 => TileNone, int n => TileManyFor(n) };
    /// Off — не проверяли, Ok — порядок, Warn — есть что сделать, Info — только сведения.
    public virtual string TileState => PendingCount switch { null => "Off", 0 => "Ok", _ => "Warn" };
    public string Badge => ShowBadge && PendingCount is int n and > 0 ? n.ToString() : "";
    protected void RaiseTile()
    {
        Raise(nameof(TileValue)); Raise(nameof(TileCaption)); Raise(nameof(TileState)); Raise(nameof(Badge));
        Raise(nameof(TileMode)); Raise(nameof(IsChecking)); Raise(nameof(ShowTileCheck)); Raise(nameof(ShowTileValue)); Raise(nameof(ShowTileStatus)); Raise(nameof(NeedsAction)); Raise(nameof(NeedsManual)); Raise(nameof(TileStatusText)); Raise(nameof(IsQueued));
    }

    // ---- Плитка до проверки и во время неё; «Проверить всё» (быстрая проверка) ----
    /// Подпись плитки, пока раздела не проверяли. Лёгким разделам — коротко; тяжёлым — сколько займёт и что проверка идёт отдельно.
    public virtual string UncheckedText => Strings.TileNotChecked;
    /// Идёт ли сейчас проверка этого раздела (по кнопке или в «Проверить всё»).
    public bool IsChecking => IsBusy || Host.QuickCurrent == this;
    public bool IsQueued => PendingCount is null && Host.QuickIsQueued(this);
    /// Ещё нет данных и ничего не идёт: плитка показывает «Ещё не проверяли» вместо числа.
    private bool TileUnchecked => PendingCount is null && !_needsManual && !IsChecking && !IsQueued;
    /// Checking | Queued | Manual | Unchecked | Value
    public string TileMode => IsChecking ? "Checking" : IsQueued ? "Queued" : _needsManual ? "Manual" : TileUnchecked ? "Unchecked" : "Value";
    public bool ShowTileValue => TileMode == "Value";
    public bool ShowTileStatus => TileMode != "Value";
    /// Ссылка «Проверить» на плитке: только там, где у раздела вообще есть проверка.
    public bool ShowTileCheck => HasCheck && TileMode == "Unchecked";
    public string TileStatusText => TileMode switch
    {
        "Checking" => Strings.TileChecking, "Queued" => Strings.TileQueued, "Manual" => Strings.TileManual, _ => UncheckedText,
    };

    private bool _needsManual;
    /// Тихая проверка не дала результата (сбой или потребовалось окно): плитка просит открыть раздел.
    public bool NeedsManual { get => _needsManual; private set { if (Set(ref _needsManual, value)) RaiseTile(); } }
    public bool NeedsAction => TileState == "Warn";
    private bool _wasCancelled;

    void Pult.Core.Maintenance.IQuickStep.Cancel() => _cts?.Cancel();
    async Task Pult.Core.Maintenance.IQuickStep.RunAsync()
    {
        NeedsManual = false; _wasCancelled = false;
        Pult.Core.Maintenance.QuietMode.Begin();
        bool hit;
        try { await QuickCoreAsync(); }
        finally { hit = Pult.Core.Maintenance.QuietMode.End(); }
        NeedsManual = !_wasCancelled && (hit || PendingCount is null);
        RaiseTile();
    }
    /// Тихая проверка раздела: та же логика, что у кнопки «Проверить». Переопределяют лёгкие разделы, у которых при открытии окна есть более простое чтение.
    protected virtual Task QuickCoreAsync() => GuardAsync(CheckAsync);

    /// Растёт после каждой проверки/поиска: страница раздела прокручивается к началу (одно правило для всех разделов — к началу списка).
    private int _scrollToken; public int ScrollToken { get => _scrollToken; private set => Set(ref _scrollToken, value); }
    protected void RequestScrollTop() => ScrollToken++;

    /// Прокрутить страницу к конкретной строке списка (например, к заголовку «Дубликаты» после поиска, чтобы результат был виден).
    public object? ScrollTarget { get; private set; }
    private int _scrollToToken; public int ScrollToToken { get => _scrollToToken; private set => Set(ref _scrollToToken, value); }
    protected void RequestScrollTo(object item) { ScrollTarget = item; ScrollToToken++; }

    public string Icon { get; }
    /// Значки дизайна (Segoe Fluent Icons): пункт меню / плитка и кнопка проверки. Назначаются в MaintenanceVm.
    public string Glyph { get; set; } = "";
    public string CheckGlyph { get; set; } = "";
    public string Eyebrow => "Обслуживание · " + Host.Today;
    public string Title { get; }
    public string Hint { get; }
    public string CheckText { get; }
    public ObservableCollection<object> Items { get; } = new();
    public ObservableCollection<ActionVm> Actions { get; } = new();
    public ICommand CheckCommand { get; }
    public ICommand CancelCommand { get; }
    /// «Обновить помощника» — показывается, когда помощнику не хватает задачи «Pult System» (старая версия).
    public ActionVm UpdateHelperAction { get; }
    public bool HasItems => Items.Count > 0;

    private bool _busy; public bool IsBusy { get => _busy; protected set { if (Set(ref _busy, value)) { RaiseTile(); Raise(nameof(ShowEmpty)); Raise(nameof(ShowCancel)); Raise(nameof(ShowFooter)); CommandManager.InvalidateRequerySuggested(); } } }
    /// «Отмена» есть не у каждой работы: когда помощник уже делает дело (починка Windows, winget с правами), остановить его отсюда нельзя — кнопки нет.
    private bool _canCancel = true;
    public bool ShowCancel => _busy && _canCancel;
    private string _progress = ""; public string Progress { get => _progress; protected set => Set(ref _progress, value); }
    private string _result = ""; public string Result { get => _result; protected set { if (Set(ref _result, value)) Raise(nameof(ShowFooter)); } }
    private bool _isError; public bool ResultIsError { get => _isError; protected set => Set(ref _isError, value); }
    private string _notes = ""; public string Notes { get => _notes; protected set { if (Set(ref _notes, value)) Raise(nameof(ShowFooter)); } }
    private string _empty = ""; public string EmptyText { get => _empty; protected set { if (Set(ref _empty, value)) Raise(nameof(ShowEmpty)); } }
    /// Описание раздела не прячется во время работы и после итога (нужно, когда в разделе нет списка — «Починка Windows»).
    protected bool PinEmptyText { get; set; }
    public bool ShowEmpty => !HasItems && (PinEmptyText || !IsBusy) && _empty.Length > 0;
    /// Кнопка «Проверить» скрыта у раздела, где проверка — отдельные кнопки.
    public virtual bool HasCheck => true;

    /// Раздел временно нельзя запускать по причине вне него (например, другой раздел держит winget).
    protected virtual bool Blocked => false;
    protected bool CanRun => !IsBusy && !Blocked && !Host.QuickRunning;

    protected abstract Task CheckAsync();
    /// Та же проверка, что по кнопке «Проверить» (режим --preview).
    public Task RunCheckAsync() => CheckAsync();

    protected void SetResult(string text, bool error = false, string notes = "") { Result = text; ResultIsError = error; Notes = notes; RaiseTile(); }
    protected void ClearResult() => SetResult("");

    /// Любое действие кнопки: исключение до RunBusyAsync (установка помощника, поиск winget и т.п.) — строка итога и баннер, а не молчание в логе.
    protected async Task GuardAsync(Func<Task> f)
    {
        Log.Action = "раздел «" + Title + "»";
        try { await f(); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Error("обслуживание: " + Title, e);
            SetResult(Strings.Failed + e.Message, true); Host.ShowBanner(Strings.Failed + e.Message);
        }
    }

    /// Долгая работа: кнопка «Отмена», строка прогресса, ни одного исключения наружу (ошибка — строка итога и баннер).
    protected async Task RunBusyAsync(string start, Func<CancellationToken, IProgress<string>, Task> work, bool cancellable = true)
    {
        if (IsBusy) return;
        _canCancel = cancellable; _wasCancelled = false; if (!Pult.Core.Maintenance.QuietMode.Active) NeedsManual = false;
        _cts = new CancellationTokenSource(); IsBusy = true; Progress = start; ClearResult();
        Log.Action = "раздел «" + Title + "»: " + start;
        try { await work(_cts.Token, new Progress<string>(t => Progress = t)); }
        catch (OperationCanceledException) { _wasCancelled = true; SetResult(Strings.Cancelled); }
        catch (Exception e)
        {
            Log.Error("обслуживание: " + Title, e);
            SetResult(Strings.Failed + e.Message, true); Host.ShowBanner(Strings.Failed + e.Message);
        }
        finally { IsBusy = false; Progress = ""; _cts?.Dispose(); _cts = null; }
    }

    protected ActionVm AddAction(string text, Func<Task> run, bool primary = false, Func<bool>? can = null, bool visible = true)
    {
        var a = new ActionVm(text, new RelayCommand(() => GuardAsync(run), () => CanRun && (can?.Invoke() ?? true)), primary, visible);
        Actions.Add(a); return a;
    }

    // ---- Перезагрузка по кнопке (драйверы, починка Windows): три кнопки; сам Пульт ничего не перезагружает ----
    private ActionVm? _rebootNow, _rebootLater, _rebootCancel;
    private string _rebootReason = "";

    /// Добавляет кнопки «Перезагрузить сейчас» / «Позже» / «Отменить перезагрузку» (скрыты, пока не нужны). reason — для сообщения Windows («после установки драйверов»).
    protected void AddRebootActions(string reason)
    {
        _rebootReason = reason;
        _rebootNow = AddAction(Strings.RebootNow, RebootAsync, true, null, false);
        _rebootLater = AddAction(Strings.RebootLater, RebootLaterAsync, false, null, false);
        _rebootCancel = AddAction(Strings.RebootCancel, CancelRebootAsync, false, null, false);
    }

    /// Показать (offer) или спрятать все три кнопки перезагрузки.
    protected void OfferReboot(bool offer)
    {
        if (_rebootNow == null) return;
        _rebootNow.Visible = offer; _rebootLater!.Visible = offer; _rebootCancel!.Visible = false;
    }

    /// Только по кнопке, с минутой на отмену; автоматически не перезагружаем никогда.
    private async Task RebootAsync()
    {
        if (!Views.ConfirmDialog.Ask(Strings.RebootAsk)) return;
        var ok = await Task.Run(() => RunShutdown("/r /t 60 /c \"Lumen: перезагрузка " + _rebootReason + "\""));
        if (ok) { _rebootNow!.Visible = false; _rebootLater!.Visible = false; _rebootCancel!.Visible = true; SetResult(Strings.RebootScheduled); }
        else SetResult(Strings.RebootFailed, true);
    }

    private Task RebootLaterAsync()
    {
        OfferReboot(false);
        SetResult(Strings.RebootPostponed);
        return Task.CompletedTask;
    }

    private async Task CancelRebootAsync()
    {
        var ok = await Task.Run(() => RunShutdown("/a"));
        if (ok) { _rebootCancel!.Visible = false; _rebootNow!.Visible = true; _rebootLater!.Visible = true; SetResult(Strings.RebootCancelled); }
        else SetResult(Strings.RebootCancelFailed, true);
    }

    private static bool RunShutdown(string args)
    {
        try
        {
            var exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return false;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch (Exception e) { Log.Error("shutdown", e); return false; }
    }

    /// Ответ помощника → (понятный текст, ошибка ли). Коды — из Core; текст помощника — только для неизвестных причин.
    protected (string Text, bool Error) DescribeAdmin(Pult.Core.Admin.AdminResponse r, string okText)
    {
        if (r.Ok) return (okText, false);
        switch (r.Code)
        {
            case Pult.Core.Admin.AdminCodes.Declined: return (Strings.AdminDeclined, true);
            case Pult.Core.Admin.AdminCodes.DeclinedTimeout: return (Strings.AdminDeclinedTimeout, true);
            case Pult.Core.Admin.AdminCodes.SystemTaskMissing: UpdateHelperAction.Visible = true; return (Strings.AdminTaskMissing, true);
            case Pult.Core.Admin.AdminCodes.HelperGone: UpdateHelperAction.Visible = true; return (string.IsNullOrWhiteSpace(r.Message) ? Strings.AdminRefused : r.Message, true);
            case Pult.Core.Admin.AdminCodes.RestoreDisabled: return (Strings.RestoreDisabled, true);
            case Pult.Core.Admin.AdminCodes.NotInstalled: return (Strings.AdminNotInstalled, true);
            case Pult.Core.Admin.AdminCodes.NoAdminAccount: return (Strings.AdminNoAccount, true);
            case Pult.Core.Admin.AdminCodes.OtherUser: UpdateHelperAction.Visible = true; return (Strings.AdminOtherUser, true);
            case Pult.Core.Admin.AdminCodes.Busy: return (Strings.AdminBusy, true);
            default: return (string.IsNullOrWhiteSpace(r.Message) ? Strings.AdminRefused : r.Message, true);
        }
    }

    private async Task UpdateHelperAsync()
    {
        var ok = await AdminSetup.UpdateHelperAsync();
        if (ok) { UpdateHelperAction.Visible = false; SetResult(Strings.HelperUpdated); }
        else SetResult(Strings.HelperNotUpdated, true);
    }
}
