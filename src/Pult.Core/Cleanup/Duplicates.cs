using System.Security.Cryptography;

namespace Pult.Core.Cleanup;

public sealed record DuplicateGroup(long Size, string Hash, IReadOnlyList<string> Paths)
{
    /// Сколько места займут лишние копии.
    public long Wasted => Size * (Paths.Count - 1);
}

/// Поиск дубликатов: размер → хеш первых 64 КБ → полный SHA256. Только чтение; ссылки не обходятся; занятые файлы пропускаются.
public static class Duplicates
{
    public const long OneMb = 1024 * 1024;
    private const int Head = 64 * 1024;

    public static IReadOnlyList<DuplicateGroup> Find(IEnumerable<string> roots, long minBytes = OneMb, CancellationToken ct = default, IProgress<string>? progress = null,
                                                     Func<FileSystemInfo, FileAttributes>? attrOf = null, ScanCounter? counter = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<string>>();
        long scanned = 0;
        foreach (var r in roots)
            foreach (var f in DiskMap.Files(r, ct, null, attrOf))             // файлы «только в облаке» сюда не попадают: хеш скачал бы их целиком
            {
                try
                {
                    if (counter != null) counter.File(f.Length); else if (++scanned % 500 == 0) progress?.Report($"Просматриваю файлы: {scanned}…");
                    if (f.Length < minBytes || f.Length == 0 || !seen.Add(f.FullName)) continue;
                    if (!bySize.TryGetValue(f.Length, out var l)) bySize[f.Length] = l = new();
                    l.Add(f.FullName);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }

        counter?.Flush();
        var res = new List<DuplicateGroup>();
        var candidates = bySize.Values.Count(l => l.Count > 1); var done = 0;
        foreach (var (size, paths) in bySize)
        {
            if (paths.Count < 2) continue;
            if (counter != null) { counter.Step("Сверяю одинаковые по размеру файлы", ++done, candidates); } else progress?.Report($"Сверяю одинаковые по размеру файлы: {++done} из {candidates}…");
            foreach (var g1 in paths.GroupBy(p => Hash(p, Head, ct)).Where(g => g.Key != null && g.Count() > 1))
                foreach (var g2 in g1.GroupBy(p => Hash(p, long.MaxValue, ct)).Where(g => g.Key != null && g.Count() > 1))
                    res.Add(new DuplicateGroup(size, g2.Key!, g2.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return res.OrderByDescending(g => g.Wasted).ToList();
    }

    /// SHA256 первых limit байт; null — файл недоступен.
    private static string? Hash(string path, long limit, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buf = new byte[81920]; long left = limit;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                var n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) break;
                sha.TransformBlock(buf, 0, n, null, 0); left -= n;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
