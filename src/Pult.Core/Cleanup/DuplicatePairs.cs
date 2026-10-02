namespace Pult.Core.Cleanup;

/// Пара папок, в которых лежат одинаковые файлы. A и B упорядочены (без учёта регистра); A == B — «копии внутри одной папки».
public sealed record FolderPair(string A, string B, int Groups, long Bytes)
{
    public bool SameFolder => string.Equals(A, B, StringComparison.OrdinalIgnoreCase);
}

/// Сводка дубликатов по парам папок и отметка копий «в этой папке». ГЛАВНЫЙ ИНВАРИАНТ: ни одна группа не может оказаться отмеченной целиком —
/// из каждой группы одинаковых файлов всегда остаётся хотя бы одна неотмеченная копия (при пересекающихся парах и при повторных нажатиях тоже).
public static class DuplicatePairs
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public static string FolderOf(string path) => Path.GetDirectoryName(path) ?? "";

    private static List<string> Folders(DuplicateGroup g) => g.Paths.Select(FolderOf).Distinct(Cmp).OrderBy(f => f, Cmp).ToList();

    /// Группа относится к паре (a, b): файлы лежат и в a, и в b (a == b — все файлы группы лежат в одной этой папке).
    public static bool InPair(DuplicateGroup g, string a, string b)
    {
        var f = Folders(g);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return f.Count == 1 && Cmp.Equals(f[0], a);
        return f.Contains(a, Cmp) && f.Contains(b, Cmp);
    }

    /// Пары папок по убыванию размера. Группа с копиями в трёх папках входит во все три пары.
    public static IReadOnlyList<FolderPair> Summarize(IEnumerable<DuplicateGroup> groups)
    {
        var map = new Dictionary<(string, string), (int Groups, long Bytes)>(new PairCmp());
        foreach (var g in groups)
        {
            var f = Folders(g);
            if (f.Count == 0) continue;
            void Add(string a, string b) { map.TryGetValue((a, b), out var v); map[(a, b)] = (v.Groups + 1, v.Bytes + g.Size); }
            if (f.Count == 1) Add(f[0], f[0]);
            else for (var i = 0; i < f.Count; i++) for (var j = i + 1; j < f.Count; j++) Add(f[i], f[j]);
        }
        return map.Select(kv => new FolderPair(kv.Key.Item1, kv.Key.Item2, kv.Value.Groups, kv.Value.Bytes))
                  .OrderByDescending(p => p.Bytes).ThenBy(p => p.A, Cmp).ThenBy(p => p.B, Cmp).ToList();
    }

    private sealed class PairCmp : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) => Cmp.Equals(x.Item1, y.Item1) && Cmp.Equals(x.Item2, y.Item2);
        public int GetHashCode((string, string) o) => HashCode.Combine(Cmp.GetHashCode(o.Item1), Cmp.GetHashCode(o.Item2));
    }

    /// Какие файлы пометить, когда человек выбрал «отметить копии в target» для пары (a, b). Копии остальных папок не трогаются.
    /// Файл пропускается, если после его отметки в группе не останется ни одной неотмеченной копии (учитываются уже стоящие отметки — marked).
    /// marked не меняется; результат — только НОВЫЕ пути. Повторный вызов с уже учтёнными отметками ничего нового не даёт.
    public static IReadOnlyList<string> CopiesToMark(IEnumerable<DuplicateGroup> groups, string a, string b, string target, IReadOnlyCollection<string> marked)
    {
        var res = new List<string>();
        if (!Cmp.Equals(target, a) && !Cmp.Equals(target, b)) return res;
        var local = new HashSet<string>(marked, Cmp);
        foreach (var g in groups)
        {
            if (!InPair(g, a, b)) continue;
            foreach (var c in g.Paths.Where(p => Cmp.Equals(FolderOf(p), target) && !local.Contains(p)).OrderBy(p => p, Cmp).ToList())
            {
                var othersLeft = g.Paths.Count(p => !local.Contains(p) && !Cmp.Equals(p, c));
                if (othersLeft < 1) continue;                                  // последнюю неотмеченную копию не трогаем
                local.Add(c); res.Add(c);
            }
        }
        return res;
    }

    /// Отметить файлы-кандидаты (например, серию), не затрагивая чужие отметки: кандидат добавляется, только если после этого в каждой его группе
    /// остаётся неотмеченная копия. marked не меняется; результат — только НОВЫЕ пути (уже стоящие отметки в него не входят).
    public static IReadOnlyList<string> AddKeepingOne(IEnumerable<DuplicateGroup> groups, IReadOnlyCollection<string> marked, IEnumerable<string> candidates)
    {
        var local = new HashSet<string>(marked, Cmp);
        var byPath = new Dictionary<string, List<DuplicateGroup>>(Cmp);
        foreach (var g in groups) foreach (var p in g.Paths) { if (!byPath.TryGetValue(p, out var l)) byPath[p] = l = new(); l.Add(g); }
        var added = new List<string>();
        foreach (var c in candidates)
        {
            if (local.Contains(c)) continue;
            if (byPath.TryGetValue(c, out var gs) && gs.Any(g => g.Paths.All(p => Cmp.Equals(p, c) || local.Contains(p)))) continue;   // это последняя неотмеченная копия
            local.Add(c); added.Add(c);
        }
        return added;
    }

    /// Первая группа, где отмечены ВСЕ копии (так быть не должно), иначе null.
    public static DuplicateGroup? FirstFullyMarked(IEnumerable<DuplicateGroup> groups, IReadOnlyCollection<string> marked)
    {
        var set = marked as HashSet<string> ?? new HashSet<string>(marked, Cmp);
        return groups.FirstOrDefault(g => g.Paths.Count > 0 && g.Paths.All(p => set.Contains(p)));
    }

    /// Убрать отметки, из-за которых в группе не осталось бы неотмеченной копии (страховка перед удалением): оставляется первая по алфавиту копия группы.
    public static IReadOnlyCollection<string> EnforceKeepOne(IEnumerable<DuplicateGroup> groups, IReadOnlyCollection<string> marked)
    {
        var set = new HashSet<string>(marked, Cmp);
        foreach (var g in groups)
            if (g.Paths.Count > 0 && g.Paths.All(set.Contains))
                set.Remove(g.Paths.OrderBy(p => p, Cmp).First());
        return set;
    }
}

/// Понятная запись пути: «Загрузки\m», «Рабочий стол\pult\src\Pult.App» — по самой длинной подходящей известной папке.
public static class FriendlyPath
{
    public static string Of(string path, IEnumerable<(string Root, string Name)> roots)
    {
        if (string.IsNullOrEmpty(path)) return path;
        (string Root, string Name)? best = null;
        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r.Root)) continue;
            var root = r.Root.TrimEnd('\\', '/');
            if (path.Length < root.Length || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length > root.Length && path[root.Length] is not ('\\' or '/')) continue;
            if (best == null || root.Length > best.Value.Root.TrimEnd('\\', '/').Length) best = r;
        }
        if (best == null) return path;
        var rest = path[best.Value.Root.TrimEnd('\\', '/').Length..].TrimStart('\\', '/');
        return rest.Length == 0 ? best.Value.Name : best.Value.Name + "\\" + rest;
    }

    /// Известные папки этого ПК с русскими названиями (профиль — последним, чтобы выигрывали более длинные).
    public static IReadOnlyList<(string Root, string Name)> ForThisPc()
    {
        var list = new List<(string, string)>
        {
            (KnownFolders.DownloadsDir(), "Загрузки"),
            (KnownFolders.PathOf(KnownFolders.Documents) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Документы"),
            (KnownFolders.PathOf(KnownFolders.Desktop) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Рабочий стол"),
            (KnownFolders.PathOf(KnownFolders.Pictures) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Изображения"),
            (KnownFolders.PathOf(KnownFolders.Videos) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Видео"),
            (KnownFolders.PathOf(KnownFolders.Music) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Музыка"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Профиль"),
        };
        return list;
    }
}
