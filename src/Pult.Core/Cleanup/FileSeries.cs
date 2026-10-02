using System.Globalization;
using System.Text.RegularExpressions;

namespace Pult.Core.Cleanup;

/// Серия файлов с общим именем и номером в одной папке: media-001.zip … media-015.zip, big.part1.rar, backup.7z.001.
/// Title — «media-….zip» (общая часть имени с многоточием на месте номера).
public sealed record FileSeriesGroup(string Folder, string Title, IReadOnlyList<FileSize> Files)
{
    public long Bytes => Files.Sum(f => f.Bytes);
    public int Count => Files.Count;
}

public static class FileSeries
{
    public const int MinCount = 3;

    // «всё до номера» (кончается не цифрой) + номер до 4 цифр + необязательное расширение до 5 знаков: media-001.zip, a.part1.rar, backup.7z.001
    private static readonly Regex Rx = new(@"^(?<pre>.*[^\d])(?<n>[0-9]{1,4})(?<suf>\.[A-Za-z0-9]{1,5})?\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// null — имя не похоже на номерное.
    public static (string Prefix, int Number, string Suffix)? Parse(string fileName)
    {
        var m = Rx.Match(fileName);
        if (!m.Success) return null;
        return (m.Groups["pre"].Value, int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture), m.Groups["suf"].Value);
    }

    /// Серии (≥ 3 файла с разными номерами в одной папке и общим началом/расширением) — отдельно, остальные файлы — как были. Порядок результата: по убыванию размера.
    public static (IReadOnlyList<FileSeriesGroup> Series, IReadOnlyList<FileSize> Singles) Split(IEnumerable<FileSize> files, int minCount = MinCount)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var buckets = new Dictionary<(string Folder, string Pre, string Suf), List<(FileSize F, int N)>>(new KeyCmp());
        var singles = new List<FileSize>();
        foreach (var f in files)
        {
            var name = Path.GetFileName(f.Path); var folder = Path.GetDirectoryName(f.Path) ?? "";
            if (Parse(name) is { } p)
            {
                var key = (folder, p.Prefix, p.Suffix);
                if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new();
                l.Add((f, p.Number));
            }
            else singles.Add(f);
        }
        var series = new List<FileSeriesGroup>();
        foreach (var (key, list) in buckets)
        {
            if (list.Count >= minCount && list.Select(x => x.N).Distinct().Count() >= minCount)
                series.Add(new FileSeriesGroup(key.Folder, key.Pre + "…" + key.Suf, list.OrderBy(x => x.N).ThenBy(x => x.F.Path, cmp).Select(x => x.F).ToList()));
            else singles.AddRange(list.Select(x => x.F));
        }
        return (series.OrderByDescending(s => s.Bytes).ToList(), singles.OrderByDescending(f => f.Bytes).ToList());
    }

    private sealed class KeyCmp : IEqualityComparer<(string, string, string)>
    {
        private static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
        public bool Equals((string, string, string) x, (string, string, string) y) => C.Equals(x.Item1, y.Item1) && C.Equals(x.Item2, y.Item2) && C.Equals(x.Item3, y.Item3);
        public int GetHashCode((string, string, string) o) => HashCode.Combine(C.GetHashCode(o.Item1), C.GetHashCode(o.Item2), C.GetHashCode(o.Item3));
    }
}

/// Пояснения к известным большим системным папкам и файлам: что это и можно ли трогать. Только текст — Пульт их не удаляет.
public static class SystemFolderNotes
{
    private sealed record Note(string Parent, string Text);

    // Parent — имя родительской папки (пусто — родитель не важен). Имя без учёта регистра.
    private static readonly Dictionary<string, Note> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WinSxS"] = new("Windows", "хранилище компонентов Windows: нужно для обновлений и восстановления. Вручную не трогай — безопасно сжимается только пунктом «Хранилище компонентов» в разделе «Очистка»."),
        ["WindowsApps"] = new("Program Files", "установленные приложения из Microsoft Store. Не трогай и не удаляй: приложения сломаются. Удалять их можно только через «Приложения» Windows."),
        ["WindowsApps.tmp"] = new("Program Files", "похоже на остаток обновления приложений Microsoft Store. Не трогай: Windows сама разберётся; если не знаешь — не удаляй."),
        ["Installer"] = new("Windows", "кеш установщиков программ: без него программы нельзя будет удалить или починить. Вручную не удаляй."),
        ["SoftwareDistribution"] = new("Windows", "скачанные обновления Windows. Безопасно освобождается пунктом про загрузки обновлений в разделе «Очистка»; вручную не трогай."),
        ["hiberfil.sys"] = new("", "файл гибернации и быстрого запуска. Размер задаёт Windows; не удаляй вручную."),
        ["pagefile.sys"] = new("", "файл подкачки (виртуальная память). Нужен Windows — не удаляй."),
        ["swapfile.sys"] = new("", "служебный файл подкачки для приложений Windows. Нужен системе — не удаляй."),
    };

    /// Пояснение для пути (папки или файла); null — путь не из списка известных.
    public static string? Explain(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var t = path.TrimEnd('\\', '/');
        var leaf = Path.GetFileName(t);
        if (leaf.Length == 0 || !Notes.TryGetValue(leaf, out var n)) return null;
        if (n.Parent.Length > 0)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(t)?.TrimEnd('\\', '/') ?? "");
            if (!string.Equals(parent, n.Parent, StringComparison.OrdinalIgnoreCase)) return null;       // «Installer» в папке пользователя — это не системная
        }
        return char.ToUpperInvariant(n.Text[0]) + n.Text[1..];
    }
}
