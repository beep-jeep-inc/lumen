using Pult.Core.Cleanup;
using Pult.Core.Config;

namespace Pult.Core.Tests;

public class DevJunkTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-90), Fresh = Now.AddDays(-2);

    private static IReadOnlyList<DevProject> Scan(TempTree t, Func<string, (bool, bool)>? git = null, int age = 30) =>
        DevJunk.ScanProjects(new[] { t.Root }, age, git ?? (_ => (true, false)), default, Now);

    [Fact]
    public void OldCleanGitProject_Checked_WithNodeModules()
    {
        using var t = new TempTree();
        t.File_("p1\\package.json", 5, Old); t.File_("p1\\src\\a.js", 5, Old); t.File_("p1\\node_modules\\x\\y.js", 1000, Fresh);
        var r = Scan(t);
        var p = Assert.Single(r);
        Assert.True(p.Checked); Assert.True(p.IsGit); Assert.False(p.GitDirty); Assert.Null(p.Mark);
        Assert.Equal(Old, p.LastTouched);      // мусорная папка не считается «трогали»
        var j = Assert.Single(p.Junk); Assert.Equal("node_modules", j.Kind); Assert.Equal(1000, j.Bytes);
    }

    [Fact]
    public void FreshProject_NotChecked()
    {
        using var t = new TempTree();
        t.File_("p1\\package.json", 5, Old); t.File_("p1\\src\\a.js", 5, Fresh); t.File_("p1\\node_modules\\x.js", 10);
        Assert.False(Assert.Single(Scan(t)).Checked);
    }

    [Fact]
    public void GitDirty_NotChecked_AndMarked()
    {
        using var t = new TempTree();
        t.File_("p1\\package.json", 5, Old); t.File_("p1\\node_modules\\x.js", 10);
        var p = Assert.Single(Scan(t, _ => (true, true)));
        Assert.False(p.Checked); Assert.True(p.GitDirty); Assert.NotNull(p.Mark);
    }

    [Fact]
    public void NoGit_NotChecked_AndMarked()
    {
        using var t = new TempTree();
        t.File_("p1\\package.json", 5, Old); t.File_("p1\\node_modules\\x.js", 10);
        var p = Assert.Single(Scan(t, _ => (false, false)));
        Assert.False(p.Checked); Assert.False(p.IsGit); Assert.Contains("git", p.Mark);
    }

    [Fact]
    public void BinObj_OnlyNextToCsproj()
    {
        using var t = new TempTree();
        t.File_("a\\package.json", 5, Old); t.File_("a\\bin\\x.dll", 10); t.File_("a\\obj\\y", 10);                         // package.json, но не csproj
        t.File_("b\\b.csproj", 5, Old); t.File_("b\\bin\\x.dll", 10); t.File_("b\\obj\\y", 20);
        var r = Scan(t);
        Assert.DoesNotContain(r, p => p.Path.EndsWith("\\a"));                  // у «a» нет мусора — в список не попадает
        var b = Assert.Single(r);
        Assert.Equal(new[] { "bin", "obj" }, b.Junk.Select(j => j.Kind).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void DistBuild_OnlyNextToPackageJson()
    {
        using var t = new TempTree();
        t.File_("a\\b.csproj", 5, Old); t.File_("a\\dist\\x", 10); t.File_("a\\build\\y", 10);
        t.File_("n\\package.json", 5, Old); t.File_("n\\dist\\x", 10); t.File_("n\\.next\\z", 10);
        var r = Scan(t);
        Assert.DoesNotContain(r, p => p.Path.EndsWith("\\a"));
        Assert.Equal(new[] { ".next", "dist" }, Assert.Single(r).Junk.Select(j => j.Kind).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Venv_OnlyWithPyvenvCfg()
    {
        using var t = new TempTree();
        t.File_("p\\requirements.txt", 5, Old); t.File_(".venv\\x".Insert(0, "p\\"), 10);                                    // .venv без pyvenv.cfg
        t.File_("q\\requirements.txt", 5, Old); t.File_("q\\.venv\\pyvenv.cfg", 10); t.File_("q\\venv\\lib\\a", 10);       // .venv настоящий, venv — нет
        var r = Scan(t);
        Assert.DoesNotContain(r, p => p.Path.EndsWith("\\p"));
        Assert.Equal(".venv", Assert.Single(Assert.Single(r).Junk).Kind);
    }

    [Fact]
    public void Pycache_AndPytestCache_Found()
    {
        using var t = new TempTree();
        t.File_("p\\pyproject.toml", 5, Old); t.File_("p\\pkg\\mod.py", 5, Old); t.File_("p\\pkg\\__pycache__\\mod.pyc", 40); t.File_("p\\.pytest_cache\\v", 10);
        var j = Assert.Single(Scan(t)).Junk;
        Assert.Equal(new[] { ".pytest_cache", "__pycache__" }, j.Select(x => x.Kind).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void DoesNotEnterNodeModules_ForProjectSearch()
    {
        using var t = new TempTree();
        t.File_("p\\package.json", 5, Old); t.File_("p\\node_modules\\dep\\package.json", 5); t.File_("p\\node_modules\\dep\\node_modules\\z", 100);
        var r = Scan(t);
        Assert.Single(r);                                                         // вложенный dep — не отдельный проект
        Assert.Single(r[0].Junk);
    }

    [Fact]
    public void Junction_NotWalked_NotCountedAsJunk()
    {
        using var t = new TempTree();
        t.File_("outside\\package.json", 5, Old); t.File_("outside\\node_modules\\x", 100);
        t.File_("root\\p\\package.json", 5, Old);
        TempTree.Junction(t.P("root", "lnk"), t.P("outside"));                    // ссылка на чужой проект
        TempTree.Junction(t.P("root", "p", "node_modules"), t.P("outside", "node_modules"));   // node_modules — ссылка
        var r = DevJunk.ScanProjects(new[] { t.P("root") }, 30, _ => (true, false), default, Now);
        Assert.Empty(r);
        Assert.True(File.Exists(t.P("outside", "node_modules", "x")));
    }

    [Fact]
    public void Depth_Max4()
    {
        using var t = new TempTree();
        t.File_("a\\b\\c\\d\\package.json", 5, Old); t.File_("a\\b\\c\\d\\node_modules\\x", 10);   // глубина 4 — находится
        t.File_("a\\b\\c\\d\\e\\package.json", 5, Old); t.File_("a\\b\\c\\d\\e\\node_modules\\x", 10);   // 5 — нет
        var r = Scan(t);
        Assert.Single(r); Assert.EndsWith("\\d", r[0].Path);
    }

    [Fact]
    public void NestedProject_JunkNotDoubleCounted()
    {
        using var t = new TempTree();
        t.File_("m\\package.json", 5, Old); t.File_("m\\sub\\package.json", 5, Old); t.File_("m\\sub\\__pycache__\\a.pyc", 10);
        var r = Scan(t);
        Assert.Equal(1, r.SelectMany(p => p.Junk).Count(j => j.Kind == "__pycache__"));
    }

    [Fact]
    public void Clean_RemovesOnlyListedKinds_AndNeverFollowsLinks()
    {
        using var t = new TempTree();
        var keep = t.File_("outside\\keep", 10);
        t.File_("p\\src\\a.js", 5); t.File_("p\\node_modules\\x", 100);
        TempTree.Junction(t.P("p", "node_modules", "lnk"), t.P("outside"));
        var r = DevJunk.Clean(new[] { new DevJunkDir(t.P("p", "node_modules"), "node_modules", 100),
                                      new DevJunkDir(t.P("p", "src"), "node_modules", 5),          // имя папки не совпадает с Kind — отказ
                                      new DevJunkDir(t.P("p"), "build", 1) }, new[] { t.Root });    // не та папка — отказ
        Assert.True(File.Exists(keep));
        Assert.False(Directory.Exists(t.P("p", "node_modules")));
        Assert.True(File.Exists(t.P("p", "src", "a.js")));
        Assert.Equal(100, r.FreedBytes); Assert.Equal(2, r.Skipped);
    }

    [Fact]
    public void Clean_OutsideRoots_OrThroughJunction_NotDeleted()
    {
        using var t = new TempTree();
        var victim = t.File_("elsewhere\\node_modules\\x", 50);
        t.File_("dev\\p\\package.json", 1);
        TempTree.Junction(t.P("dev", "p2"), t.P("elsewhere"));                                   // проект-ссылка: путь к node_modules идёт через junction
        var viaLink = new DevJunkDir(t.P("dev", "p2", "node_modules"), "node_modules", 50);
        var outside = new DevJunkDir(t.P("elsewhere", "node_modules"), "node_modules", 50);
        var r = DevJunk.Clean(new[] { viaLink, outside }, new[] { t.P("dev") });
        Assert.True(File.Exists(victim));
        Assert.Equal(0, r.FreedBytes); Assert.Equal(2, r.Skipped);
    }

    [Fact]
    public void Git_NotInstalledOrNotRepo_ReturnsFalse()
    {
        using var t = new TempTree();
        Assert.Equal((false, false), DevJunk.GitStatus(t.Root));
    }

    [Fact]
    public async Task CacheCategories_Npm_Pip_NuGetNote()
    {
        using var t = new TempTree();
        var env = new CleanupEnv(t.Dir("temp"), t.Dir("local"), t.Dir("roaming"), t.Dir("profile"), null, t.Dir("win"), t.Dir("pd"), t.Root, new FakeProcs(), () => Now, new FakeBin(), null);
        t.File_("local\\npm-cache\\_cacache\\a", 100); t.File_("local\\npm-cache\\_logs\\keep", 5); t.File_("profile\\.nuget\\packages\\x\\y.dll", 50);
        var cats = DevJunk.CacheCategories(env);
        var npm = cats.First(c => c.Id == "Dev.npm"); var nuget = cats.First(c => c.Id == "Dev.nuget");
        Assert.Equal(100, (await npm.ScanAsync(default)).Bytes);
        var n = await nuget.ScanAsync(default);
        Assert.False(n.Checked); Assert.Contains("заново", n.Note);
        Assert.Equal(100, (await npm.CleanAsync(default)).FreedBytes);
        Assert.True(File.Exists(t.P("local", "npm-cache", "_logs", "keep")));
    }

    [Fact]
    public void Config_DevRoots_DefaultDesktop_AndRoundtrip()
    {
        var c = new PultConfig();
        Assert.Equal(30, c.DevAgeDays);
        Assert.Equal(new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) }, c.DevRoots);
        var json = System.Text.Json.JsonSerializer.Serialize(new PultConfig { DevRoots = new() { @"D:\code" }, DevAgeDays = 10 });
        var back = System.Text.Json.JsonSerializer.Deserialize<PultConfig>(json)!;
        Assert.Equal(new[] { @"D:\code" }, back.DevRoots); Assert.Equal(10, back.DevAgeDays);
    }
}
