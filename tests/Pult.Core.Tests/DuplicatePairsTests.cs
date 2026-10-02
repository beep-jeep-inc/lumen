using Pult.Core.Cleanup;
namespace Pult.Core.Tests;

/// B3: сводка по парам папок, отметка копий и инвариант «ни одна группа не отмечена целиком».
public class DuplicatePairsTests
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private const string A = @"C:\Users\u\Downloads\t5.dra", B = @"C:\Users\u\Downloads\m", C = @"C:\Users\u\Documents";

    private static DuplicateGroup G(long size, params string[] paths) => new(size, Guid.NewGuid().ToString("N"), paths);

    private static void AssertInvariant(IEnumerable<DuplicateGroup> groups, IEnumerable<string> marked)
    {
        var set = new HashSet<string>(marked, Cmp);
        foreach (var g in groups) Assert.True(g.Paths.Any(p => !set.Contains(p)), "группа отмечена целиком: " + string.Join(" | ", g.Paths));
    }

    [Fact]
    public void Summarize_CountsPairs_AndSizes()
    {
        var groups = new[]
        {
            G(100, A + @"\1.bin", B + @"\1.bin"), G(200, A + @"\2.bin", B + @"\2.bin"), G(50, B + @"\x.bin", C + @"\x.bin"),
            G(10, B + @"\a.bin", B + @"\b.bin"),
        };
        var p = DuplicatePairs.Summarize(groups);
        var ab = p.Single(x => x.A == A.Replace("t5.dra", "t5.dra") && x.B == B || x.B == A && x.A == B);
        Assert.Equal((2, 300L), (ab.Groups, ab.Bytes));
        Assert.Contains(p, x => x.SameFolder && Cmp.Equals(x.A, B) && x.Groups == 1 && x.Bytes == 10);
        Assert.Equal(3, p.Count);
        Assert.True(p.Zip(p.Skip(1)).All(z => z.First.Bytes >= z.Second.Bytes));       // по убыванию
    }

    [Fact]
    public void Summarize_ThreeFolders_GroupGoesIntoEveryPair()
    {
        var p = DuplicatePairs.Summarize(new[] { G(5, A + @"\f", B + @"\f", C + @"\f") });
        Assert.Equal(3, p.Count); Assert.All(p, x => { Assert.Equal(1, x.Groups); Assert.Equal(5, x.Bytes); });
    }

    [Fact]
    public void Summarize_PairKeyIsOrderAndCaseInsensitive()
    {
        var p = DuplicatePairs.Summarize(new[] { G(1, A + @"\f", B + @"\f"), G(1, B.ToUpperInvariant() + @"\g", A.ToLowerInvariant() + @"\g") });
        var one = Assert.Single(p); Assert.Equal(2, one.Groups);
    }

    [Fact]
    public void CopiesToMark_MarksOnlyTargetFolder_KeepsOtherFolder()
    {
        var groups = new[] { G(1, A + @"\1", B + @"\1"), G(1, A + @"\2", B + @"\2"), G(1, B + @"\o1", C + @"\o1") };
        var m = DuplicatePairs.CopiesToMark(groups, A, B, B, new HashSet<string>());
        Assert.Equal(new[] { B + @"\1", B + @"\2" }, m.OrderBy(x => x, Cmp));            // группа с папкой C к этой паре не относится
        AssertInvariant(groups, m);
    }

    [Fact]
    public void CopiesToMark_TwoCopiesInTarget_BothMarked_OtherFolderKept()
    {
        var g = G(1, A + @"\x", B + @"\x1", B + @"\x2");
        var m = DuplicatePairs.CopiesToMark(new[] { g }, A, B, B, new HashSet<string>());
        Assert.Equal(2, m.Count); AssertInvariant(new[] { g }, m);
    }

    [Fact]
    public void CopiesToMark_SameFolderPair_LeavesOneCopy()
    {
        var g = G(1, B + @"\a", B + @"\b", B + @"\c");
        var m = DuplicatePairs.CopiesToMark(new[] { g }, B, B, B, new HashSet<string>());
        Assert.Equal(2, m.Count); AssertInvariant(new[] { g }, m);
    }

    [Fact]
    public void CopiesToMark_RepeatedPress_IsIdempotent()
    {
        var groups = new[] { G(1, A + @"\1", B + @"\1"), G(1, B + @"\a", B + @"\b") };
        var marked = new HashSet<string>(Cmp);
        var first = DuplicatePairs.CopiesToMark(groups, A, B, B, marked); foreach (var p in first) marked.Add(p);
        var second = DuplicatePairs.CopiesToMark(groups, A, B, B, marked);
        Assert.NotEmpty(first); Assert.Empty(second);
        AssertInvariant(groups, marked);
    }

    [Fact]
    public void CopiesToMark_OverlappingPairs_NeverMarksWholeGroup()
    {
        // Один и тот же файл лежит в A, B и C: помечаем копии в B, потом в C, потом в A — последняя отметка не должна пройти.
        var g = G(1, A + @"\f", B + @"\f", C + @"\f");
        var marked = new HashSet<string>(Cmp);
        foreach (var (a, b, t) in new[] { (A, B, B), (B, C, C), (A, C, A), (A, B, A), (A, B, B), (A, C, C) })
        {
            foreach (var p in DuplicatePairs.CopiesToMark(new[] { g }, a, b, t, marked)) marked.Add(p);
            AssertInvariant(new[] { g }, marked);
        }
        Assert.Equal(2, marked.Count);
    }

    [Fact]
    public void CopiesToMark_RespectsExistingMarksFromOtherPlaces()
    {
        var g = G(1, A + @"\f", B + @"\f");
        var marked = new HashSet<string>(Cmp) { A + @"\f" };                              // человек уже отметил копию в A вручную
        Assert.Empty(DuplicatePairs.CopiesToMark(new[] { g }, A, B, B, marked));          // вторую (последнюю) отметить нельзя
    }

    [Fact]
    public void CopiesToMark_WrongTarget_Nothing() =>
        Assert.Empty(DuplicatePairs.CopiesToMark(new[] { G(1, A + @"\f", B + @"\f") }, A, B, C, new HashSet<string>()));

    [Fact]
    public void Randomised_AnySequenceOfPresses_KeepsInvariant()
    {
        var rnd = new Random(12345);
        var folders = new[] { A, B, C, @"D:\x", @"D:\y" };
        for (var round = 0; round < 200; round++)
        {
            var groups = Enumerable.Range(0, rnd.Next(1, 12)).Select(i =>
            {
                var n = rnd.Next(2, 5);
                var paths = Enumerable.Range(0, n).Select(k => folders[rnd.Next(folders.Length)] + @"\f" + i + "_" + k).Distinct().ToArray();
                return paths.Length < 2 ? G(1, folders[0] + @"\s" + i, folders[1] + @"\s" + i) : G(rnd.Next(1, 1000), paths);
            }).ToList();
            var marked = new HashSet<string>(Cmp);
            for (var press = 0; press < 12; press++)
            {
                var pairs = DuplicatePairs.Summarize(groups);
                var pr = pairs[rnd.Next(pairs.Count)];
                var target = rnd.Next(2) == 0 ? pr.A : pr.B;
                foreach (var p in DuplicatePairs.CopiesToMark(groups, pr.A, pr.B, target, marked)) marked.Add(p);
                AssertInvariant(groups, marked);
                if (rnd.Next(4) == 0 && groups.Count > 0)                                  // иногда человек сам отмечает файл рядом с кнопками — защита ловит
                {
                    var g = groups[rnd.Next(groups.Count)]; var p = g.Paths[rnd.Next(g.Paths.Count)];
                    var trial = new HashSet<string>(marked, Cmp) { p };
                    if (DuplicatePairs.FirstFullyMarked(groups, trial) == null) marked.Add(p);
                }
                AssertInvariant(groups, marked);
            }
        }
    }

    [Fact]
    public void AddKeepingOne_SeriesDoesNotUnmarkOrMarkLastCopy()
    {
        var x = B + @"\media-001.zip"; var y = A + @"\media-001.zip";        // x вне серии, y в серии
        var groups = new[] { G(1, x, y) };
        var marked = new HashSet<string>(Cmp) { x };
        var added = DuplicatePairs.AddKeepingOne(groups, marked, new[] { y });
        Assert.Empty(added);                                                  // y — последняя неотмеченная копия
        Assert.Contains(x, marked);
        Assert.Null(DuplicatePairs.FirstFullyMarked(groups, marked));
        // без чужих отметок серия отмечается целиком, если у каждого файла есть копия вне серии
        var g2 = new[] { G(1, x, y), G(1, B + @"\media-002.zip", A + @"\media-002.zip") };
        var add2 = DuplicatePairs.AddKeepingOne(g2, new HashSet<string>(Cmp), new[] { y, A + @"\media-002.zip" });
        Assert.Equal(2, add2.Count);
        // серия, где обе копии группы внутри неё: добавляется только одна
        var inner = new[] { G(1, A + @"\s-1.zip", A + @"\s-2.zip") };
        Assert.Single(DuplicatePairs.AddKeepingOne(inner, new HashSet<string>(Cmp), new[] { A + @"\s-1.zip", A + @"\s-2.zip" }));
    }

    [Fact]
    public void Randomised_WithSeriesAndManualMarks_KeepsInvariant()
    {
        var rnd = new Random(777);
        var folders = new[] { A, B, C };
        for (var round = 0; round < 200; round++)
        {
            var groups = Enumerable.Range(0, rnd.Next(1, 10)).Select(i =>
            {
                var paths = Enumerable.Range(0, rnd.Next(2, 4)).Select(k => folders[rnd.Next(folders.Length)] + @"\f" + i + "_" + k).Distinct().ToArray();
                return paths.Length < 2 ? G(1, A + @"\s" + i, B + @"\s" + i) : G(1, paths);
            }).ToList();
            var all = groups.SelectMany(g => g.Paths).ToList();
            var marked = new HashSet<string>(Cmp);
            for (var step = 0; step < 15; step++)
            {
                switch (rnd.Next(4))
                {
                    case 0:                                                    // серия вкл: случайный набор файлов
                        foreach (var p in DuplicatePairs.AddKeepingOne(groups, marked, all.Where(_ => rnd.Next(2) == 0).ToList())) marked.Add(p);
                        break;
                    case 1:                                                    // серия выкл
                        foreach (var p in all.Where(_ => rnd.Next(3) == 0).ToList()) marked.Remove(p);
                        break;
                    case 2:
                        var pairs = DuplicatePairs.Summarize(groups); var pr = pairs[rnd.Next(pairs.Count)];
                        foreach (var p in DuplicatePairs.CopiesToMark(groups, pr.A, pr.B, rnd.Next(2) == 0 ? pr.A : pr.B, marked)) marked.Add(p);
                        break;
                    default:
                        var one = all[rnd.Next(all.Count)];
                        if (DuplicatePairs.FirstFullyMarked(groups, new HashSet<string>(marked, Cmp) { one }) == null) marked.Add(one);
                        break;
                }
                AssertInvariant(groups, marked);
            }
        }
    }

    [Fact]
    public void FirstFullyMarked_FindsViolation_AndEnforceKeepOneRepairsIt()
    {
        var g1 = G(1, A + @"\f", B + @"\f"); var g2 = G(1, A + @"\g", B + @"\g");
        var marked = new HashSet<string>(Cmp) { A + @"\f", B + @"\f", A + @"\g" };
        Assert.Same(g1, DuplicatePairs.FirstFullyMarked(new[] { g1, g2 }, marked));
        var fixedSet = DuplicatePairs.EnforceKeepOne(new[] { g1, g2 }, marked);
        Assert.Null(DuplicatePairs.FirstFullyMarked(new[] { g1, g2 }, fixedSet));
        Assert.Equal(2, fixedSet.Count);
        Assert.Contains(A + @"\g", fixedSet);                                             // чужие (неполные) отметки не тронуты
    }

    [Fact]
    public void FriendlyPath_UsesLongestKnownRoot()
    {
        var roots = new[] { (@"C:\Users\u", "Профиль"), (@"C:\Users\u\Desktop", "Рабочий стол"), (@"C:\Users\u\Downloads", "Загрузки") };
        Assert.Equal(@"Рабочий стол\pult\src\Pult.App", FriendlyPath.Of(@"C:\Users\u\Desktop\pult\src\Pult.App", roots));
        Assert.Equal(@"Загрузки\m", FriendlyPath.Of(@"c:\users\u\downloads\m", roots));
        Assert.Equal("Загрузки", FriendlyPath.Of(@"C:\Users\u\Downloads\", roots));
        Assert.Equal(@"Профиль\x", FriendlyPath.Of(@"C:\Users\u\x", roots));
        Assert.Equal(@"C:\Users\uu\x", FriendlyPath.Of(@"C:\Users\uu\x", roots));         // «u» не префикс «uu»
        Assert.Equal(@"D:\data", FriendlyPath.Of(@"D:\data", roots));
    }
}

public class ScanCounterTests
{
    private sealed class Clock { public DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); }
    private sealed class Sink : IProgress<string> { public List<string> L = new(); public void Report(string v) => L.Add(v); }

    [Fact]
    public void Throttled_AtMostFourPerSecond()
    {
        var c = new Clock(); var s = new Sink(); var sc = new Pult.Core.Cleanup.ScanCounter(s, "Папки", () => c.T);
        for (var i = 0; i < 10_000; i++) { sc.File(1000); if (i % 100 == 0) c.T += TimeSpan.FromMilliseconds(10); }   // 100 шагов по 10 мс = 1 секунда
        Assert.InRange(s.L.Count, 1, 5);
        Assert.Equal(10_000, sc.Files);
    }

    [Fact]
    public void Format_FilesGigabytesAndClock()
    {
        var c = new Clock(); var s = new Sink(); var sc = new Pult.Core.Cleanup.ScanCounter(s, "Папки диска", () => c.T);
        sc.File(2L * 1024 * 1024 * 1024);
        c.T += TimeSpan.FromSeconds(65); sc.File(1024L * 1024 * 1024);
        Assert.Contains("Папки диска: просмотрено файлов 2", s.L[^1]);
        Assert.Contains("ГБ", s.L[^1]); Assert.EndsWith("· 1:05", s.L[^1]);
    }

    [Fact]
    public void Clock_MinutesSeconds() => Assert.Equal("75:03", Pult.Core.Cleanup.ScanCounter.Clock(TimeSpan.FromSeconds(75 * 60 + 3)));

    [Fact]
    public void Step_ShowsDoneOfTotal_AndPhaseResetsCounters()
    {
        var c = new Clock(); var s = new Sink(); var sc = new Pult.Core.Cleanup.ScanCounter(s, "x", () => c.T);
        sc.Step("Сверяю", 3, 40);
        Assert.StartsWith("Сверяю: 3 из 40", s.L[^1]);
        sc.File(5); sc.Phase("y"); Assert.Equal(0, sc.Files);
    }

    [Fact]
    public void Flush_ReportsEvenInsideInterval()
    {
        var c = new Clock(); var s = new Sink(); var sc = new Pult.Core.Cleanup.ScanCounter(s, "x", () => c.T);
        sc.File(1); sc.File(1); sc.Flush();
        Assert.Equal(2, s.L.Count);
    }
}
