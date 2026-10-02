using System.Text.RegularExpressions;

namespace Pult.Core.Maintenance;

public sealed record WingetRow(string Name, string Id, string Version, string Available, string Source);

/// Разбор таблицы `winget upgrade`: колонки — по позициям слов заголовка (имена бывают с пробелами и обрезаны «…»).
public static class WingetTable
{
    private static readonly Regex Word = new(@"\S+", RegexOptions.Compiled);

    public static IReadOnlyList<WingetRow> Parse(string output)
    {
        var rows = new List<WingetRow>();
        if (string.IsNullOrEmpty(output)) return rows;
        // Индикатор прогресса winget рисует поверх строки через \r — берём последний кусок.
        var lines = output.Split('\n').Select(l => { l = l.TrimEnd('\r'); var i = l.LastIndexOf('\r'); return i >= 0 ? l[(i + 1)..] : l; }).ToList();

        int sep = lines.FindIndex(l => l.Length >= 5 && l.All(c => c == '-'));
        if (sep < 1) return rows;
        var header = lines[sep - 1];
        var starts = Word.Matches(header).Select(m => m.Index).ToList();
        if (starts.Count < 4) return rows;

        for (int i = sep + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || IsSummary(line)) continue;
            string Col(int n)
            {
                int a = starts[n], b = n + 1 < starts.Count ? starts[n + 1] : line.Length;
                if (a >= line.Length) return "";
                return line.Substring(a, Math.Min(b, line.Length) - a).Trim();
            }
            var name = Col(0); var id = Col(1);
            if (name.Length == 0 || id.Length == 0 || id.Contains(' ')) continue;
            rows.Add(new WingetRow(name, id, Col(2), starts.Count > 3 ? Col(3) : "", starts.Count > 4 ? Col(4) : ""));
        }
        return rows;
    }

    private static bool IsSummary(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("Доступны обновления", StringComparison.OrdinalIgnoreCase)
            || t.Contains("upgrades available", StringComparison.OrdinalIgnoreCase)
            || t.Contains("upgrade available", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Несколько", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("The following packages", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Следующие пакеты", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("No installed package", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Установленные пакеты", StringComparison.OrdinalIgnoreCase)
            || t.Contains("--include-unknown", StringComparison.OrdinalIgnoreCase);
    }
}
