using System.Diagnostics;
using Pult.Core.Admin;
using Pult.Core.Cleanup;
using Pult.Core.Monitoring;

namespace Pult.Core.Tests;

public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pult-cl-" + Guid.NewGuid().ToString("N")[..8]);
    public TempTree() { Directory.CreateDirectory(Root); }
    public string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
    public string File_(string rel, int size = 10, DateTime? written = null)
    {
        var p = P(rel.Split('\\')); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
        if (written != null) File.SetLastWriteTimeUtc(p, written.Value);
        return p;
    }
    public string Dir(string rel) { var p = P(rel.Split('\\')); Directory.CreateDirectory(p); return p; }
    public static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!; p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))   // не идти по junction при зачистке
                if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) try { Directory.Delete(d); } catch { }
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(Root, true);
        }
        catch { }
    }
}

public sealed class FakeProcs(params string[] running) : IProcessQuery
{
    public bool IsRunning(string name) => running.Contains(name.Replace(".exe", ""), StringComparer.OrdinalIgnoreCase);
    public bool AnyCommandLine(string wqlNameFilter, Func<string, bool> match) => false;
}

public sealed class FakeBin : IRecycleBin
{
    public long Bytes = 5000, Items = 3; public bool Emptied;
    public (long, long) Query() => (Emptied ? 0 : Bytes, Emptied ? 0 : Items);
    public bool Empty() { Emptied = true; return true; }
}

public class SafeDeleteTests
{
    [Fact]
    public void DeleteContents_JunctionInside_LinkRemovedTargetIntact()
    {
        using var t = new TempTree();
        var outside = t.File_("outside\\keep.txt", 100); var outDir = Path.GetDirectoryName(outside)!;
        t.File_("root\\a.bin", 50);
        TempTree.Junction(t.P("root", "lnk"), outDir);
        var r = SafeDelete.DeleteContents(t.P("root"));
        Assert.Equal(50, r.FreedBytes);
        Assert.True(File.Exists(outside));                         // цель цела
        Assert.Empty(Directory.GetFileSystemEntries(t.P("root"))); // сама ссылка удалена
    }

    [Fact]
    public void DeleteContents_RootIsJunction_Refused()
    {
        using var t = new TempTree();
        var outside = t.File_("outside\\keep.txt", 100);
        TempTree.Junction(t.P("lnk"), Path.GetDirectoryName(outside)!);
        var r = SafeDelete.DeleteContents(t.P("lnk"));
        Assert.NotNull(r.Error);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void DeleteDirectory_OnJunction_RemovesLinkOnly()
    {
        using var t = new TempTree();
        var outside = t.File_("outside\\keep.txt", 100);
        TempTree.Junction(t.P("lnk"), Path.GetDirectoryName(outside)!);
        SafeDelete.DeleteDirectory(t.P("lnk"));
        Assert.False(Directory.Exists(t.P("lnk")));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Measure_DoesNotFollowJunction()
    {
        using var t = new TempTree();
        t.File_("root\\a.bin", 70); t.File_("other\\big.bin", 1000);
        TempTree.Junction(t.P("root", "lnk"), t.P("other"));
        var m = SafeDelete.Measure(t.P("root"));
        Assert.Equal((70L, 1), (m.Bytes, m.Files));
    }

    [Fact]
    public void DeleteContents_ReadOnlyFile_Deleted_LockedFile_SkippedSilently()
    {
        using var t = new TempTree();
        var ro = t.File_("root\\ro.txt", 10); File.SetAttributes(ro, FileAttributes.ReadOnly);
        var locked = t.File_("root\\sub\\locked.txt", 20); t.File_("root\\free.txt", 30);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = SafeDelete.DeleteContents(t.P("root"));
            Assert.Equal(40, r.FreedBytes);
            Assert.Equal(1, r.Skipped);
            Assert.Null(r.Error);
        }
        Assert.False(File.Exists(ro));
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void DeleteContents_Filter_OnlyOldFiles()
    {
        using var t = new TempTree();
        var now = DateTime.UtcNow;
        t.File_("root\\old.tmp", 10, now.AddDays(-3)); t.File_("root\\new.tmp", 20, now);
        t.File_("root\\d\\old2.tmp", 5, now.AddDays(-2));
        var r = SafeDelete.DeleteContents(t.P("root"), i => now - i.LastWriteTimeUtc >= TimeSpan.FromDays(1));
        Assert.Equal(15, r.FreedBytes);
        Assert.True(File.Exists(t.P("root", "new.tmp")));
        Assert.False(File.Exists(t.P("root", "old.tmp")));
    }

    [Fact]
    public void DeleteContents_MissingRoot_NoError() => Assert.Equal(CleanResult.Empty, SafeDelete.DeleteContents(Path.Combine(Path.GetTempPath(), "pult-none-" + Guid.NewGuid())));

    [Fact]
    public void IsSafeUnder_RejectsOutsideAndLinks()
    {
        using var t = new TempTree();
        t.Dir("base\\real"); t.Dir("elsewhere");
        TempTree.Junction(t.P("base", "lnk"), t.P("elsewhere"));
        Assert.True(SafeDelete.IsSafeUnder(t.P("base"), t.P("base", "real", "x")));
        Assert.False(SafeDelete.IsSafeUnder(t.P("base"), t.P("base", "lnk", "x")));
        Assert.False(SafeDelete.IsSafeUnder(t.P("base"), t.P("elsewhere")));
        Assert.False(SafeDelete.IsSafeUnder(t.P("base"), t.P("base", "..", "elsewhere")));
    }

    private sealed class FakeRecycleEnv(DriveType type = DriveType.Fixed, long? max = null) : IRecycleEnv
    {
        public DriveType TypeOf(string r) => type;
        public long? BinMaxBytes(string r) => max;
        public void MoveToBin(IReadOnlyList<string> paths) { }                   // настоящую Корзину владельца в тестах не трогаем никогда
    }

    [Fact]
    public void ToRecycleBin_RefusesRelativeAndDriveRoot_MissingIgnored()
    {
        var r = SafeDelete.ToRecycleBin(new[] { @"C:\", "relative.txt" }, new FakeRecycleEnv());
        Assert.Equal(2, r.Skipped.Count); Assert.False(r.AllOk); Assert.Empty(r.Moved);
        var none = SafeDelete.ToRecycleBin(new[] { Path.Combine(Path.GetTempPath(), "pult-none-" + Guid.NewGuid()) }, new FakeRecycleEnv());
        Assert.True(none.AllOk); Assert.Empty(none.Moved);
    }

    [Theory]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Unknown)]
    public void ToRecycleBin_NonFixedDrive_SkippedNotTouched(DriveType type)
    {
        using var t = new TempTree(); var f = t.File_("a.txt");
        var r = SafeDelete.ToRecycleBin(new[] { f }, new FakeRecycleEnv(type));
        Assert.True(File.Exists(f)); Assert.Empty(r.Moved);
        Assert.Equal((f, SafeDelete.NotLocal), Assert.Single(r.Skipped));
    }

    [Fact]
    public void ToRecycleBin_BinDisabled_Skipped()
    {
        using var t = new TempTree(); var f = t.File_("a.txt");
        var r = SafeDelete.ToRecycleBin(new[] { f }, new FakeRecycleEnv(max: -1));
        Assert.True(File.Exists(f)); Assert.Contains("выключена", Assert.Single(r.Skipped).Reason);
    }

    [Fact]
    public void ToRecycleBin_TooBigForBin_Skipped_SumAlsoChecked()
    {
        using var t = new TempTree();
        var big = t.File_("big.bin", 5000); var s1 = t.File_("s1.bin", 600); var s2 = t.File_("s2.bin", 600);
        var r = SafeDelete.ToRecycleBin(new[] { big }, new FakeRecycleEnv(max: 1000));
        Assert.True(File.Exists(big)); Assert.Contains("не поместится", Assert.Single(r.Skipped).Reason);
        var both = SafeDelete.ToRecycleBin(new[] { s1, s2 }, new FakeRecycleEnv(max: 1000));      // по отдельности влезают, вместе — нет
        Assert.True(File.Exists(s1) && File.Exists(s2)); Assert.Equal(2, both.Skipped.Count);
    }

    [Fact]
    public void ToRecycleBin_OutsideAllowedRoots_AndLinks_Skipped()
    {
        using var t = new TempTree();
        var inside = t.File_("ok\\a.txt"); var outside = t.File_("other\\b.txt");
        TempTree.Junction(t.P("ok", "lnk"), t.P("other"));
        var r = SafeDelete.ToRecycleBin(new[] { outside, t.P("ok", "lnk") }, new FakeRecycleEnv(max: 1 << 30), within: new[] { t.P("ok") });
        Assert.True(File.Exists(outside)); Assert.Empty(r.Moved); Assert.Equal(2, r.Skipped.Count);
        Assert.True(File.Exists(inside));
    }

    [Fact]
    public void Norm_KeepsDriveRoot_TrimsOthers()
    {
        Assert.Equal(@"C:\", SafeDelete.Norm(@"C:\"));
        Assert.Equal(@"C:\x", SafeDelete.Norm(@"C:\x\"));
        Assert.Equal(@"C:\x", SafeDelete.Norm(@"C:\x"));
    }

    [Fact]
    public void IsSafeUnder_DriveRootAsBase_AndNormalBase()
    {
        Assert.True(SafeDelete.IsSafeUnder(@"C:\", @"C:\Windows"));
        Assert.False(SafeDelete.IsSafeUnder(@"C:\", @"C:\"));
        Assert.False(SafeDelete.IsSafeUnder(@"C:\", @"D:\x"));
        using var t = new TempTree(); var f = t.File_("a\\b.txt");
        Assert.True(SafeDelete.IsSafeUnder(t.Root, f));
        Assert.True(SafeDelete.IsSafeUnder(t.Root + "\\", f));
        Assert.False(SafeDelete.IsSafeUnder(t.P("a"), t.Root));
    }

    [Fact]
    public void DeleteContents_DriveRoot_Refused()
    {
        // Настоящий корень диска не передаём: защита срабатывает по тексту пути до обращения к диску, поэтому годится несуществующая буква.
        var letter = "ZYXWVUTSRQPONMLKJIHGFED".First(c => !Directory.Exists(c + @":\"));
        var root = letter + @":\";
        Assert.True(SafeDelete.IsDriveRoot(root)); Assert.True(SafeDelete.IsDriveRoot(letter + @":\\"));
        Assert.False(SafeDelete.IsDriveRoot(root + "Windows"));
        var r = SafeDelete.DeleteContents(root);
        Assert.Equal(0, r.FreedBytes); Assert.Equal(1, r.Skipped); Assert.Contains("корень диска", r.Error);
        Assert.Equal(1, SafeDelete.DeleteDirectory(root).Skipped);
    }

    [Fact]
    public void ToRecycleBin_ExcludedRoot_Skipped_OthersContinue()
    {
        using var t = new TempTree();
        var app = t.File_("prof\\AppData\\db.sqlite"); var doc = t.File_("prof\\big.bin");
        var r = SafeDelete.ToRecycleBin(new[] { app }, new FakeRecycleEnv(max: 1 << 30), within: new[] { t.P("prof") }, exclude: new[] { t.P("prof", "AppData") });
        Assert.True(File.Exists(app)); Assert.Empty(r.Moved);
        Assert.Contains("системная папка", Assert.Single(r.Skipped).Reason);
        Assert.True(SafeDelete.IsUnder(t.P("prof", "AppData"), app));
        Assert.False(SafeDelete.IsUnder(t.P("prof", "AppData"), doc));
        Assert.False(SafeDelete.IsUnder(t.P("prof", "App"), app));
    }

    [Fact]
    public void ToRecycleBin_RealEnv_LocalDriveAndBinInfo()
    {
        var env = new ShellRecycleEnv();
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(DriveType.Fixed, env.TypeOf(root));
        Assert.True(env.BinMaxBytes(root) is null or -1 or > 0);
    }
}

public class UserCategoriesTests
{
    private static CleanupEnv Env(TempTree t, IProcessQuery? procs = null, Func<string, CancellationToken, Task<AdminResponse>>? admin = null, string? steam = null) =>
        new(t.Dir("temp"), t.Dir("local"), t.Dir("roaming"), t.Dir("profile"), steam, t.Dir("win"), t.Dir("pd"), t.Root,
            procs ?? new FakeProcs(), () => DateTime.UtcNow, new FakeBin(), admin);

    private static ICleanupCategory Cat(CleanupEnv e, string id) => UserCategories.All(e).First(c => c.Id == id);

    [Fact]
    public async Task Browser_FakeProfile_OnlyCachesRemoved_PrivateDataUntouched()
    {
        using var t = new TempTree();
        var env = Env(t); var ud = t.P("local", "Google", "Chrome", "User Data");
        string[] keep = { "Default\\Cookies", "Default\\Network\\Cookies", "Default\\Login Data", "Default\\History", "Default\\Bookmarks", "Default\\Web Data",
                          "Default\\Preferences", "Local State", "Profile 2\\Login Data", "Profile 2\\Extensions\\x\\a.js", "Default\\Local Storage\\leveldb\\1.ldb" };
        string[] go = { "Default\\Cache\\f_1", "Default\\Code Cache\\js\\a", "Default\\GPUCache\\data_0", "Default\\Service Worker\\CacheStorage\\x\\y", "Profile 2\\Cache\\f", "ShaderCache\\s", "GrShaderCache\\g" };
        foreach (var k in keep) File.WriteAllText(t.File_("local\\Google\\Chrome\\User Data\\" + k), "x");
        foreach (var g in go) t.File_("local\\Google\\Chrome\\User Data\\" + g, 100);
        t.File_("local\\Google\\Chrome\\User Data\\Default\\Service Worker\\Database\\keep.ldb");   // не CacheStorage
        var c = Cat(env, "Browser.Chrome");
        var item = await c.ScanAsync(default);
        Assert.Equal(700, item.Bytes); Assert.Equal(7, item.Files); Assert.Null(item.BlockedBy); Assert.True(item.Checked);
        var r = await c.CleanAsync(default);
        Assert.Equal(700, r.FreedBytes);
        foreach (var k in keep) Assert.True(File.Exists(Path.Combine(ud, k)), k);
        Assert.True(File.Exists(Path.Combine(ud, "Default", "Service Worker", "Database", "keep.ldb")));
        foreach (var g in go) Assert.False(File.Exists(Path.Combine(ud, g)), g);
    }

    [Fact]
    public async Task Browser_ProfileIsJunction_NotFollowed()
    {
        using var t = new TempTree();
        var secret = t.File_("elsewhere\\Cache\\precious", 100);
        var ud = t.Dir("local\\Google\\Chrome\\User Data");
        TempTree.Junction(Path.Combine(ud, "Default"), t.P("elsewhere"));
        var c = Cat(Env(t), "Browser.Chrome");
        Assert.Equal(0, (await c.ScanAsync(default)).Bytes);
        await c.CleanAsync(default);
        Assert.True(File.Exists(secret));
    }

    [Fact]
    public async Task Browser_CacheDirIsJunction_TargetIntact()
    {
        using var t = new TempTree();
        var secret = t.File_("elsewhere\\precious", 100);
        t.Dir("local\\Google\\Chrome\\User Data\\Default");
        TempTree.Junction(t.P("local", "Google", "Chrome", "User Data", "Default", "Cache"), t.P("elsewhere"));
        var c = Cat(Env(t), "Browser.Chrome");
        await c.CleanAsync(default);
        Assert.True(File.Exists(secret));
    }

    [Fact]
    public async Task Browser_Running_Blocked_NotCleaned_Unchecked()
    {
        using var t = new TempTree();
        var f = t.File_("local\\Microsoft\\Edge\\User Data\\Default\\Cache\\a", 100);
        var c = Cat(Env(t, new FakeProcs("msedge")), "Browser.Edge");
        var item = await c.ScanAsync(default);
        Assert.Equal("Edge", item.BlockedBy); Assert.False(item.Checked); Assert.Equal(100, item.Bytes);
        var r = await c.CleanAsync(default);
        Assert.Equal("закрой Edge", r.Error); Assert.Equal(0, r.FreedBytes);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public async Task Telegram_OnlyUserDataCacheAndMediaCache()
    {
        using var t = new TempTree();
        var tg = "roaming\\Telegram Desktop\\tdata\\";
        var keep = new[] { "D877F783D5D3EF8C\\map", "key_datas", "user_data\\settings", "user_data\\emoji\\e", "settingss", "user_data\\cachex\\z" };
        foreach (var k in keep) t.File_(tg + k);
        t.File_(tg + "user_data\\cache\\0\\a", 100); t.File_(tg + "user_data\\media_cache\\b", 200);
        var c = Cat(Env(t), "Telegram");
        var r = await c.CleanAsync(default);
        Assert.Equal(300, r.FreedBytes);
        foreach (var k in keep) Assert.True(File.Exists(t.P(("roaming\\Telegram Desktop\\tdata\\" + k).Split('\\'))), k);
    }

    [Fact]
    public async Task Telegram_Running_Blocked()
    {
        using var t = new TempTree();
        t.File_("roaming\\Telegram Desktop\\tdata\\user_data\\cache\\a", 100);
        var item = await Cat(Env(t, new FakeProcs("Telegram")), "Telegram").ScanAsync(default);
        Assert.Equal("Telegram", item.BlockedBy);
    }

    [Fact]
    public async Task UserTemp_OnlyOlderThanDay()
    {
        using var t = new TempTree();
        var env = Env(t);
        t.File_("temp\\old.tmp", 10, DateTime.UtcNow.AddDays(-2)); t.File_("temp\\new.tmp", 20);
        var r = await Cat(env, "UserTemp").CleanAsync(default);
        Assert.Equal(10, r.FreedBytes);
        Assert.True(File.Exists(t.P("temp", "new.tmp")));
    }

    [Fact]
    public async Task Thumbnails_OnlyThumbcacheDb()
    {
        using var t = new TempTree();
        var ex = "local\\Microsoft\\Windows\\Explorer\\";
        t.File_(ex + "thumbcache_256.db", 100); t.File_(ex + "thumbcache_idx.db", 50); t.File_(ex + "iconcache_256.db", 70); t.File_(ex + "keep.txt", 5);
        var c = Cat(Env(t), "Thumbnails");
        Assert.Equal(150, (await c.ScanAsync(default)).Bytes);
        var r = await c.CleanAsync(default);
        Assert.Equal(150, r.FreedBytes);
        Assert.True(File.Exists(t.P("local", "Microsoft", "Windows", "Explorer", "iconcache_256.db")));
    }

    [Fact]
    public async Task RecycleBin_ScanAndEmpty()
    {
        using var t = new TempTree();
        var env = Env(t); var c = Cat(env, "RecycleBin");
        var i = await c.ScanAsync(default);
        Assert.Equal((5000L, 3), (i.Bytes, i.Files)); Assert.False(i.Checked);
        Assert.Equal(5000, (await c.CleanAsync(default)).FreedBytes);
        Assert.True(((FakeBin)env.Bin).Emptied);
    }

    [Fact]
    public async Task AdminCategories_DelegateToHelper_WithFixedIds()
    {
        using var t = new TempTree();
        var sent = new List<string>();
        var env = Env(t, admin: (id, _) => { sent.Add(id); return Task.FromResult(new AdminResponse("n", true, "ok", AdminMaintenance.CleanCode(1234, 2))); });
        foreach (var id in AdminMaintenance.CleanIds)
        {
            var c = Cat(env, id);
            Assert.True(c.NeedsAdmin);
            var r = await c.CleanAsync(default);
            Assert.Equal((1234L, 2, (string?)null), (r.FreedBytes, r.Skipped, r.Error));
        }
        Assert.Equal(AdminMaintenance.CleanIds, sent);
    }

    [Fact]
    public async Task AdminCategory_NoHelper_Declined_OrThrows_NeverThrows()
    {
        using var t = new TempTree();
        Assert.NotNull((await Cat(Env(t), "Wer").CleanAsync(default)).Error);
        var declined = Env(t, admin: (_, _) => Task.FromResult(new AdminResponse("n", false, "Ты отказался", AdminCodes.Declined)));
        Assert.Equal("Ты отказался", (await Cat(declined, "WuDownload").CleanAsync(default)).Error);
        var boom = Env(t, admin: (_, _) => throw new InvalidOperationException("x"));
        Assert.NotNull((await Cat(boom, "WindowsTemp").CleanAsync(default)).Error);
    }

    [Fact]
    public async Task AdminCategory_ScanReadableRoot_ShowsSize_ComponentCleanupUnknown()
    {
        using var t = new TempTree();
        t.File_("win\\Temp\\old.tmp", 100, DateTime.UtcNow.AddDays(-3)); t.File_("win\\Temp\\new.tmp", 50);
        var env = Env(t);
        var wt = await Cat(env, "WindowsTemp").ScanAsync(default);
        Assert.Equal(100, wt.Bytes); Assert.True(wt.NeedsAdmin); Assert.False(wt.Checked);
        Assert.Equal(-1, (await Cat(env, "ComponentCleanup").ScanAsync(default)).Bytes);
        Assert.False((await Cat(env, "WuDownload").ScanAsync(default)).Visible);   // папки нет — не показывается
    }

    [Fact]
    public async Task WindowsOld_SizeOnly_CleanRefuses()
    {
        using var t = new TempTree();
        t.File_("Windows.old\\a", 300);
        var c = Cat(Env(t), "WindowsOld");
        Assert.Equal(300, (await c.ScanAsync(default)).Bytes);
        var r = await c.CleanAsync(default);
        Assert.NotNull(r.Error); Assert.True(File.Exists(t.P("Windows.old", "a")));
    }

    [Fact]
    public async Task Steam_OnlyShadercacheHttpcacheLogs()
    {
        using var t = new TempTree();
        var s = t.Dir("steam");
        t.File_("steam\\steamapps\\shadercache\\1\\a", 100); t.File_("steam\\appcache\\httpcache\\b", 50); t.File_("steam\\logs\\c", 10);
        t.File_("steam\\steamapps\\common\\Game\\game.exe", 999); t.File_("steam\\config\\config.vdf", 5);
        var c = Cat(Env(t, steam: s), "Steam");
        var r = await c.CleanAsync(default);
        Assert.Equal(160, r.FreedBytes);
        Assert.True(File.Exists(t.P("steam", "steamapps", "common", "Game", "game.exe"))); Assert.True(File.Exists(t.P("steam", "config", "config.vdf")));
    }

    [Fact]
    public async Task ScanAll_HidesMissingAndEmpty()
    {
        using var t = new TempTree();
        t.File_("local\\Google\\Chrome\\User Data\\Default\\Cache\\a", 100);
        var items = await UserCategories.ScanAllAsync(UserCategories.All(Env(t)), default);
        Assert.Contains(items, i => i.Id == "Browser.Chrome");
        Assert.DoesNotContain(items, i => i.Id == "Browser.Edge" || i.Id == "Discord" || i.Id == "Steam");
    }
}
