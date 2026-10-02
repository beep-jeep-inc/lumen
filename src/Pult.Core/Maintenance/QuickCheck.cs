namespace Pult.Core.Maintenance;

/// Состав и порядок «Проверить всё» на Обзоре. Только лёгкие чтения без сети и без нагрузки (секунды):
/// автозагрузка (реестр), здоровье дисков (обычная проверка, без «Подробно» через помощника), системные библиотеки (реестр, без winget).
/// Тяжёлые и редкие — Очистка, Программы (winget), Драйверы (Центр обновления), Что занимает место, дубликаты, Починка Windows — сюда не входят никогда:
/// они запускаются отдельно, каждая своей кнопкой.
public static class QuickCheckPlan
{
    public const string Startup = "startup", Disks = "disks", Libraries = "libraries";
    /// От быстрых к медленным.
    public static readonly IReadOnlyList<string> Order = new[] { Startup, Disks, Libraries };
    public static bool IsQuick(string key) => Order.Contains(key);
}

/// Окна, которые не должны всплывать во время быстрой проверки (подтверждение, установка помощника, UAC).
/// Пока режим включён, такие места не спрашивают, а сообщают о себе через Blocks(): раздел помечается «нужна ручная проверка».
public static class QuietMode
{
    private static int _active;
    private static bool _hit;
    public static bool Active => Volatile.Read(ref _active) > 0;
    public static void Begin() { Interlocked.Increment(ref _active); _hit = false; }
    /// Возвращает true, если за время режима что-то хотело показать окно.
    public static bool End() { Interlocked.Decrement(ref _active); var h = _hit; _hit = false; return h; }
    /// Вызывать перед показом окна: true — окно показывать нельзя (отказ без вопроса).
    public static bool Blocks() { if (!Active) return false; _hit = true; return true; }
}

/// Один шаг быстрой проверки (раздел окна). Тестируется без WPF.
public interface IQuickStep
{
    string Title { get; }
    /// Тихая проверка: не показывает окон, завершается, когда раздел закончил.
    Task RunAsync();
    /// Прервать текущую работу раздела.
    void Cancel();
    /// Признак «есть что сделать» — тот же, что золотая точка плитки.
    bool NeedsAction { get; }
    /// Проверка не удалась или потребовала окна: нужна ручная проверка в самом разделе.
    bool NeedsManual { get; }
}

public sealed record QuickSummary(DateTime Time, int Total, int Done, int NeedAction, int Manual, bool Cancelled)
{
    public string Text
    {
        get
        {
            if (Cancelled) return $"Быстрая проверка остановлена · проверено {Done} из {Total}";
            var head = $"Быстрая проверка в {Time:HH:mm} · ";
            var body = NeedAction == 0 ? "всё в порядке" : $"есть что сделать в {NeedAction} {Sections(NeedAction)}";
            if (Manual > 0) body += $" · не удалось проверить: {Manual}";
            return head + body;
        }
    }

    /// «в 1 разделе», «в 2 разделах», «в 5 разделах», «в 21 разделе».
    public static string Sections(int n)
    {
        var m100 = n % 100; var m10 = n % 10;
        return m10 == 1 && m100 != 11 ? "разделе" : "разделах";
    }
}

/// Очередь быстрой проверки: шаги идут по одному, чтобы не нагружать ПК. Отмена прерывает текущий шаг и не запускает остальные.
public sealed class QuickCheckRunner(IReadOnlyList<IQuickStep> steps, Func<DateTime>? now = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);
    private bool _cancelled;
    private readonly HashSet<IQuickStep> _pending = new();

    public IReadOnlyList<IQuickStep> Steps => steps;
    public bool Running { get; private set; }
    public IQuickStep? Current { get; private set; }
    /// Номер текущего шага (с 1) и сколько их всего.
    public int Index { get; private set; }
    public int Total => steps.Count;
    public QuickSummary? Summary { get; private set; }
    public event Action? Changed;

    public bool IsQueued(IQuickStep s) => Running && _pending.Contains(s);
    public string ProgressText => Current == null ? "" : $"Проверяю: {Current.Title} · {Index} из {Total}";

    public void Cancel()
    {
        if (!Running) return;
        _cancelled = true;
        Current?.Cancel();
    }

    public async Task RunAsync()
    {
        if (Running) return;
        Running = true; _cancelled = false; Summary = null; Index = 0;
        _pending.Clear(); foreach (var s in steps) _pending.Add(s);
        Changed?.Invoke();
        var ran = new List<IQuickStep>(); var failed = 0;
        try
        {
            foreach (var step in steps)
            {
                if (_cancelled) break;
                Index++; Current = step; _pending.Remove(step); Changed?.Invoke();
                var ok = true;
                try { await step.RunAsync(); }
                catch (OperationCanceledException) { }
                catch (Exception) { ok = false; failed++; }          // сбой шага не роняет очередь: раздел помечен «проверьте вручную»
                Current = null;
                if (_cancelled) break;                                // прерванный на полпути шаг проверенным не считается
                if (ok) ran.Add(step);
                Changed?.Invoke();
            }
        }
        finally
        {
            Current = null; Running = false; _pending.Clear();
            Summary = new QuickSummary(_now(), steps.Count, ran.Count, ran.Count(s => s.NeedsAction), failed + ran.Count(s => s.NeedsManual), _cancelled);
            Changed?.Invoke();
        }
    }
}
