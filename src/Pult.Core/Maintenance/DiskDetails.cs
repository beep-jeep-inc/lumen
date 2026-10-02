using System.Globalization;
using System.Text;

namespace Pult.Core.Maintenance;

/// Счётчики одного физического диска от помощника (MSFT_StorageReliabilityCounter). null — Windows не отдала это число или оно не прошло проверку.
public sealed record DiskCounters(int DeviceId, int? Wear, int? TemperatureC, long? ReadErrors, long? WriteErrors, long? PowerOnHours);

/// Протокол ТОЛЬКО ЧИТАЮЩЕГО действия помощника DiskDetails. Ни одного значения из запроса — вывод скрипта помощника это строки вида
/// «D|номер|износ|температура|ошибкиЧтения|ошибкиЗаписи|часы», где каждое поле — целое без знака или «-» (нет данных).
/// Пульт НЕ доверяет ответу (файл ответа читается из общей папки): всё разбирается и проверяется здесь, лишнее отбрасывается.
public static class DiskDetailsProtocol
{
    /// Код ответа с данными (в Message — проверенные строки). Любой другой код/неудача — Message это обычный текст для человека.
    public const string Code = "disk-details";
    public const int MaxDisks = 32, MaxLineLength = 96, MaxTotalLength = 8 * 1024;
    public const int MaxDeviceId = 9999, MaxTemperatureC = 150;
    public const long MaxErrors = 4_294_967_295, MaxHours = 1_000_000;           // 1 млн часов — больше 114 лет, уже явный мусор

    /// Разбор. Принимается только полностью правильная строка D|…; всё прочее (END, ERR, мусор, чужие строки) молча пропускается.
    /// Строка, где хоть одно поле не «-» и не число, отбрасывается целиком. Значение вне диапазона не отбрасывает строку, а превращается в «нет данных» (износ 0…100, температура 1…150, ошибки до 2^32-1, часы до 1 млн).
    /// Повтор номера диска — берётся первая строка. Слишком длинный вывод (> 8 КБ) — пустой список.
    public static IReadOnlyList<DiskCounters> Parse(string? text)
    {
        var list = new List<DiskCounters>();
        if (string.IsNullOrEmpty(text) || text.Length > MaxTotalLength) return list;
        foreach (var raw in text.Split('\n'))
        {
            if (list.Count >= MaxDisks) break;
            var line = raw.Trim();
            if (line.Length == 0 || line.Length > MaxLineLength || !line.StartsWith("D|", StringComparison.Ordinal)) continue;
            var f = line.Split('|');
            if (f.Length != 7) continue;
            if (!TryNumber(f[1], out var id) || id > MaxDeviceId) continue;
            if (list.Any(c => c.DeviceId == id)) continue;
            if (!f.Skip(2).All(x => x == "-" || TryNumber(x, out _))) continue;                 // поле не «-» и не число — это не наша строка
            var wear = Bounded(f[2], 0, 100); var temp = Bounded(f[3], 1, MaxTemperatureC);
            var re = Bounded(f[4], 0, MaxErrors); var we = Bounded(f[5], 0, MaxErrors); var hours = Bounded(f[6], 0, MaxHours);
            list.Add(new((int)id, wear is { } w ? (int)w : null, temp is { } t ? (int)t : null, re, we, hours));
        }
        return list;
    }

    /// Каноническая запись проверенных счётчиков (то, что помощник кладёт в Message ответа). Parse(Format(x)) == x.
    public static string Format(IEnumerable<DiskCounters> counters)
    {
        var sb = new StringBuilder();
        foreach (var c in counters.Take(MaxDisks))
            sb.Append("D|").Append(c.DeviceId.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(F(c.Wear)).Append('|').Append(F(c.TemperatureC)).Append('|').Append(F(c.ReadErrors)).Append('|').Append(F(c.WriteErrors)).Append('|').Append(F(c.PowerOnHours)).Append('\n');
        return sb.ToString();
    }

    private static string F(long? v) => v is { } x ? x.ToString(CultureInfo.InvariantCulture) : "-";

    /// Только ASCII-цифры, 1…10 знаков (никаких знаков, пробелов, экспонент).
    private static bool TryNumber(string s, out long v)
    {
        v = 0;
        if (s.Length is < 1 or > 10) return false;
        foreach (var ch in s) if (ch is < '0' or > '9') return false;
        v = long.Parse(s, CultureInfo.InvariantCulture);
        return true;
    }

    private static long? Bounded(string s, long min, long max) => TryNumber(s, out var v) && v >= min && v <= max ? v : null;

    /// Счётчики → в диски по номеру (DeviceId). Диски без счётчиков остаются как были; чужие номера игнорируются.
    public static IReadOnlyList<DiskInfo> Merge(IReadOnlyList<DiskInfo> disks, IReadOnlyList<DiskCounters> counters) =>
        disks.Select(d =>
        {
            var c = int.TryParse(d.DeviceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? counters.FirstOrDefault(x => x.DeviceId == id) : null;
            return c == null ? d : d with { Wear = c.Wear ?? d.Wear, TemperatureC = c.TemperatureC ?? d.TemperatureC, ReadErrors = c.ReadErrors ?? d.ReadErrors,
                                            WriteErrors = c.WriteErrors ?? d.WriteErrors, PowerOnHours = c.PowerOnHours ?? d.PowerOnHours };
        }).ToList();
}

/// Занятое место одного тома (буква диска) на физическом диске. Читается без прав администратора.
public sealed record VolumeUsage(string Letter, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

public static class DiskVolumes
{
    /// «C: занято 420 ГБ из 476 ГБ (свободно 56 ГБ)» — по строке на том. Тома без размера пропускаются.
    public static IReadOnlyList<string> Describe(IReadOnlyList<VolumeUsage>? volumes) =>
        (volumes ?? Array.Empty<VolumeUsage>()).Where(v => v.TotalBytes > 0)
            .Select(v => $"{v.Letter}: занято {SizeFormat.Human(v.UsedBytes)} из {SizeFormat.Human(v.TotalBytes)} (свободно {SizeFormat.Human(Math.Min(v.FreeBytes, v.TotalBytes))})").ToList();
}
