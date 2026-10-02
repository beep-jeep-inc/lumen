using Pult.Core.Maintenance;

namespace Pult.Core.Cleanup;

/// Прогресс долгого просмотра файлов: «Папки диска: просмотрено файлов 12 345 · 3,2 ГБ · 0:41». Обновление не чаще 4 раз в секунду
/// (минимальный интервал minInterval), чтобы интерфейс не захлёбывался на миллионах файлов. Считает только числа; ни путей, ни имён.
public sealed class ScanCounter
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    private readonly IProgress<string> _progress; private readonly Func<DateTime> _now; private readonly TimeSpan _interval;
    private DateTime _started, _lastReport;
    private long _files, _bytes;
    private string _label;
    public int Reports { get; private set; }

    public ScanCounter(IProgress<string> progress, string label, Func<DateTime>? now = null, TimeSpan? minInterval = null)
    {
        _progress = progress; _label = label; _now = now ?? (() => DateTime.UtcNow); _interval = minInterval ?? DefaultInterval;
        _started = _now(); _lastReport = DateTime.MinValue;
    }

    public long Files => _files;
    public long Bytes => _bytes;

    /// Новый этап: счётчики и время начинаются заново.
    public void Phase(string label) { _label = label; _files = 0; _bytes = 0; _started = _now(); _lastReport = DateTime.MinValue; }

    public void File(long bytes)
    {
        _files++; _bytes += Math.Max(0, bytes);
        Emit(false, null);
    }

    /// «Сверяю одинаковые файлы: 5 из 40 · 0:12» (время — от начала этапа).
    public void Step(string what, int done, int total) => Emit(false, $"{what}: {done} из {total}");

    /// Вывести текущее состояние сразу (конец этапа).
    public void Flush() => Emit(true, null);

    private void Emit(bool force, string? custom)
    {
        var now = _now();
        if (!force && _lastReport != DateTime.MinValue && now - _lastReport < _interval) return;
        _lastReport = now; Reports++;
        var el = now - _started;
        _progress.Report(Format(custom ?? $"{_label}: просмотрено файлов {_files:N0}", custom == null ? _bytes : -1, el));
    }

    /// «text · 3,2 ГБ · 1:05» (размер — если bytes ≥ 0).
    public static string Format(string text, long bytes, TimeSpan elapsed) =>
        text + (bytes >= 0 ? " · " + SizeFormat.Human(bytes) : "") + " · " + Clock(elapsed);

    /// М:СС (часы — в минутах: 75:03).
    public static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
