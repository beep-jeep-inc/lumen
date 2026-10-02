using Pult.Core.Cleanup;

namespace Pult.Core.Tests;

/// Живые проверки на этом ПК (только чтение, ничего не удаляется): PULT_LIVE=1, результат — в %TEMP%\pult-live-cleanup.txt.
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute() { if (Environment.GetEnvironmentVariable("PULT_LIVE") != "1") Skip = "Живая проверка этого ПК: PULT_LIVE=1"; }
}

public class DiskMapTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TopFolders_KnownSizes_SortedWithDepth()
    {
        using var t = new TempTree();
        t.File_("r\\big\\a", 1000); t.File_("r\\big\\deep\\b", 500); t.File_("r\\small\\c", 10); t.File_("r\\loose", 7);
        var top = DiskMap.TopFolders(t.P("r"), 2, 10, default);
        Assert.Equal(new[] { "big", "deep", "small" }, top.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(new long[] { 1500, 500, 10 }, top.Select(f => f.Bytes));
        Assert.Equal(new[] { 1, 2, 1 }, top.Select(f => f.Depth));
        Assert.Single(DiskMap.TopFolders(t.P("r"), 1, 1, default));
        Assert.DoesNotContain(DiskMap.TopFolders(t.P("r"), 1, 10, default), f => f.Depth > 1);
    }

    [Fact]
    public void TopFolders_JunctionNotCountedTwice()
    {
        using var t = new TempTree();
        t.File_("r\\real\\a", 1000);
        TempTree.Junction(t.P("r", "lnk"), t.P("r", "real"));
        var top = DiskMap.TopFolders(t.P("r"), 1, 10, default);
        Assert.Equal(new[] { "real" }, top.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(1000, top[0].Bytes);
        Assert.Single(DiskMap.TopFiles(t.P("r"), 10, 0));
    }

    [Fact]
    public void TopFiles_SkipDir_ExcludesThatSubtree()
    {
        using var t = new TempTree();
        t.File_("prof\\AppData\\Local\\huge.vhdx", 900); t.File_("prof\\Videos\\v.mp4", 500);
        var skip = t.P("prof", "AppData");
        var f = DiskMap.TopFiles(t.P("prof"), 10, 0, default, d => string.Equals(d, skip, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "v.mp4" }, f.Select(x => Path.GetFileName(x.Path)));
    }

    [Fact]
    public void TopFiles_MinBytes_Top()
    {
        using var t = new TempTree();
        t.File_("a", 100); t.File_("d\\b", 300); t.File_("d\\c", 200);
        var f = DiskMap.TopFiles(t.Root, 2, 150);
        Assert.Equal(new long[] { 300, 200 }, f.Select(x => x.Bytes));
    }

    [Fact]
    public void TopFolders_Cancelled_Throws()
    {
        using var t = new TempTree(); t.File_("r\\a\\b", 1);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DiskMap.TopFolders(t.P("r"), 1, 5, cts.Token));
    }

    [Fact]
    public void TopFolders_MissingRoot_Empty() => Assert.Empty(DiskMap.TopFolders(Path.Combine(Path.GetTempPath(), "pult-none-" + Guid.NewGuid()), 2, 5, default));

    [Fact]
    public void OldDownloads_ByDateAndExtension()
    {
        using var t = new TempTree();
        var old = Now.AddDays(-60); var fresh = Now.AddDays(-3);
        foreach (var n in new[] { "a.exe", "b.MSI", "c.zip", "d.rar", "e.7z", "f.iso" }) { var p = t.File_("dl\\" + n, 10, old); File.SetCreationTimeUtc(p, old); }
        var f1 = t.File_("dl\\fresh.exe", 10, fresh); File.SetCreationTimeUtc(f1, fresh);
        var f2 = t.File_("dl\\newdl.zip", 10, old); File.SetCreationTimeUtc(f2, fresh);          // дата файла старая, но скачан недавно
        var f3 = t.File_("dl\\doc.pdf", 10, old); File.SetCreationTimeUtc(f3, old);              // не установщик
        var f4 = t.File_("dl\\sub\\inner.exe", 10, old); File.SetCreationTimeUtc(f4, old);       // подпапки не смотрим
        var r = DiskMap.OldDownloads(t.P("dl"), 30, Now);
        Assert.Equal(new[] { "a.exe", "b.MSI", "c.zip", "d.rar", "e.7z", "f.iso" }, r.Select(x => Path.GetFileName(x.Path)).OrderBy(x => x));
    }

    [Fact]
    public void Duplicates_SameContent_Grouped_SameSizeDifferentContent_Not()
    {
        using var t = new TempTree();
        var a = new byte[2 * 1024 * 1024]; new Random(1).NextBytes(a);
        var b = (byte[])a.Clone(); b[^1] ^= 0xFF;                      // тот же размер и начало, другой хвост
        var other = new byte[3 * 1024 * 1024]; new Random(2).NextBytes(other);
        Directory.CreateDirectory(t.P("x")); Directory.CreateDirectory(t.P("y"));
        File.WriteAllBytes(t.P("x", "a1.bin"), a); File.WriteAllBytes(t.P("y", "a2.bin"), a); File.WriteAllBytes(t.P("y", "a3.bin"), a);
        File.WriteAllBytes(t.P("x", "b.bin"), b); File.WriteAllBytes(t.P("x", "o.bin"), other);
        File.WriteAllBytes(t.P("x", "tiny1"), new byte[100]); File.WriteAllBytes(t.P("y", "tiny2"), new byte[100]);   // меньше порога
        var g = Assert.Single(Duplicates.Find(new[] { t.P("x"), t.P("y"), t.P("x") }, 1024 * 1024));          // корень дважды — без двойных путей
        Assert.Equal(3, g.Paths.Count); Assert.Equal(a.Length, g.Size); Assert.Equal(2L * a.Length, g.Wasted);
        Assert.DoesNotContain(g.Paths, p => p.EndsWith("b.bin"));
        Assert.Equal(64, g.Hash.Length);
    }

    [Fact]
    public void Duplicates_JunctionNotCountedAsCopy()
    {
        using var t = new TempTree();
        var a = new byte[1200000]; new Random(3).NextBytes(a);
        Directory.CreateDirectory(t.P("x")); File.WriteAllBytes(t.P("x", "a.bin"), a);
        TempTree.Junction(t.P("lnk"), t.P("x"));
        Assert.Empty(Duplicates.Find(new[] { t.Root }, 1024 * 1024));
    }

    [Fact]
    public void Duplicates_LockedFile_SkippedSilently()
    {
        using var t = new TempTree();
        var a = new byte[1200000]; new Random(4).NextBytes(a);
        Directory.CreateDirectory(t.P("x"));
        File.WriteAllBytes(t.P("x", "a1"), a); File.WriteAllBytes(t.P("x", "a2"), a);
        using var fs = new FileStream(t.P("x", "a2"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(Duplicates.Find(new[] { t.Root }));                       // a2 недоступен — группы нет, исключения нет
    }

    /// Живая: кладёт файл в НАСТОЯЩУЮ Корзину владельца (PULT_LIVE=1). Логика проверяется без Корзины в MaintenanceWaveTests (подставной IRecycleEnv.MoveToBin).
    [LiveFact]
    public void Live_ToRecycleBin_RealFile_GoesToBin_NotDeletedForever()
    {
        using var t = new TempTree();
        var f = t.File_("keep\\tobin.txt", 10);
        var r = SafeDelete.ToRecycleBin(new[] { f }); Assert.True(r.AllOk); Assert.Single(r.Moved);
        Assert.False(File.Exists(f));
    }

    [LiveFact]
    public async Task Live_ScanThisPc_ReadOnly()
    {
        var sb = new System.Text.StringBuilder();
        var env = CleanupEnv.ForThisPc();
        var cats = UserCategories.All(env).Concat(DevJunk.CacheCategories(env)).ToList();
        foreach (var c in cats)
        {
            var i = await c.ScanAsync(default);
            sb.AppendLine($"{c.Group} | {i.Id} | {i.Title} | bytes={i.Bytes} files={i.Files} admin={i.NeedsAdmin} blocked={i.BlockedBy} checked={i.Checked} visible={i.Visible}");
        }
        var cfg = new Pult.Core.Config.PultConfig();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var projs = DevJunk.ScanProjects(cfg.DevRoots, cfg.DevAgeDays, DevJunk.GitStatus, default);
        sb.AppendLine($"DEV projects with junk: {projs.Count}, checked by default: {projs.Count(p => p.Checked)}, {sw.Elapsed}");
        foreach (var p in projs) sb.AppendLine($"  {p.Path} junk={p.JunkBytes} kinds={string.Join(',', p.Junk.Select(j => j.Kind).Distinct())} git={p.IsGit} dirty={p.GitDirty} last={p.LastTouched:yyyy-MM-dd} checked={p.Checked}");
        sw.Restart();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        sb.AppendLine($"TOP 10 folders of {home} (depth 1):");
        foreach (var f in DiskMap.TopFolders(home, 1, 10, default)) sb.AppendLine($"  {f.Bytes / 1048576} MB  {f.Path}");
        sb.AppendLine($"  ({sw.Elapsed})");
        var dl = Path.Combine(home, "Downloads");
        sw.Restart();
        var dups = Duplicates.Find(new[] { dl });
        sb.AppendLine($"DUPLICATE groups in Downloads: {dups.Count}, wasted MB: {dups.Sum(d => d.Wasted) / 1048576} ({sw.Elapsed})");
        sb.AppendLine($"OLD downloads: {DiskMap.OldDownloads(dl).Count}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pult-live-cleanup.txt"), sb.ToString());
    }
}
