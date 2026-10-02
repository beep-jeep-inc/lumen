using Pult.Core.Cleanup;
using Pult.Core.Maintenance;
using Pult.Core.Monitoring;
namespace Pult.Core.Tests;

/// Волна A1, I8–I11: старый winget, файлы только в облаке, перенесённые личные папки, фоновые процессы браузеров.
public class MaintenanceWaveTests
{
    // ---- I8: версия winget ----
    [Theory]
    [InlineData("v1.29.380", 1, 29, 380)]
    [InlineData("v1.4.10173\r\n", 1, 4, 10173)]
    [InlineData("v1.9.25180-preview", 1, 9, 25180)]
    [InlineData("1.3", 1, 3, 0)]
    public void Winget_ParseVersion(string text, int a, int b, int c) => Assert.Equal(new Version(a, b, c), Winget.ParseVersion(text));

    [Theory]
    [InlineData("")]
    [InlineData("ошибка")]
    [InlineData(null)]
    public void Winget_ParseVersion_Garbage_Null(string? text) => Assert.Null(Winget.ParseVersion(text));

    private const string Exe = @"C:\w\winget.exe";

    [Theory]
    [InlineData("1.3.2691", false)]
    [InlineData("1.4.10173", true)]
    [InlineData("1.29.380", true)]
    public void Winget_UpgradeArgs_DisableInteractivityOnlyFrom14(string ver, bool has)
    {
        var args = Winget.UpgradeCommand(Exe, "Mozilla.Firefox", Winget.ParseVersion(ver)).Args;
        Assert.Equal(has, args.Contains("--disable-interactivity"));
        Assert.Contains("--exact", args); Assert.Contains("--silent", args); Assert.Contains("--accept-source-agreements", args);
        Assert.Equal(new[] { "upgrade", "--id", "Mozilla.Firefox" }, args.Take(3));
    }

    [Theory]
    [InlineData("1.3.2691", false)]
    [InlineData("1.4.10173", true)]
    [InlineData("1.29.380", true)]
    public void Winget_InstallAndListArgs_FollowVersion(string ver, bool has)
    {
        var v = Winget.ParseVersion(ver);
        Assert.Equal(has, Winget.InstallCommand(Exe, "x.y", v).Args.Contains("--disable-interactivity"));
        Assert.Equal(has, Winget.ListUpgradesArgs(v).Contains("--disable-interactivity"));
        Assert.Contains("--accept-source-agreements", Winget.ListUpgradesArgs(v));
    }

    [Fact]
    public void Winget_UnknownVersion_KeepsOldBehaviour()
    {
        Assert.Contains("--disable-interactivity", Winget.UpgradeCommand(Exe, "a.b").Args);
        Assert.Contains("--disable-interactivity", Winget.ListUpgradesArgs());
    }

    [Theory]
    [InlineData("1.1.12986", true)]
    [InlineData("1.2.10271", false)]
    [InlineData("1.3.2691", false)]
    [InlineData("1.29.380", false)]
    public void Winget_TooOld_Below12(string ver, bool old) => Assert.Equal(old, Winget.IsTooOld(Winget.ParseVersion(ver)));

    [Fact]
    public void Winget_TooOld_UnknownIsNotOld() => Assert.False(Winget.IsTooOld(null));

    // ---- I9: только в облаке ----
    [Theory]
    [InlineData(0x400000, true)]
    [InlineData(0x40000, true)]
    [InlineData(0x1000, true)]
    [InlineData(0x400000 | 0x20, true)]
    [InlineData(0x20, false)]
    [InlineData(0x1, false)]
    [InlineData(0x10, false)]
    public void Cloud_Attributes(int attrs, bool cloud) => Assert.Equal(cloud, CloudFiles.IsCloudOnly((FileAttributes)attrs));

    private static Func<FileSystemInfo, FileAttributes> CloudByName(string marker) =>
        i => i.Name.Contains(marker) ? i.Attributes | CloudFiles.RecallOnDataAccess : i.Attributes;

    [Fact]
    public void Files_SkipsCloudOnly()
    {
        using var t = new TempTree();
        t.File_("r\\local.bin", 100); t.File_("r\\x.cloud.bin", 5000);
        var names = DiskMap.Files(t.P("r"), default, null, CloudByName(".cloud.")).Select(f => f.Name).ToList();
        Assert.Equal(new[] { "local.bin" }, names);
    }

    [Fact]
    public void TopFiles_AndTopFolders_DoNotCountCloudOnly()
    {
        using var t = new TempTree();
        t.File_("r\\d\\local.bin", 100); t.File_("r\\d\\big.cloud.bin", 50_000);
        var attr = CloudByName(".cloud.");
        Assert.Empty(DiskMap.TopFiles(t.P("r"), 10, 1000, default, null, attr));                    // облачный «крупный» файл в список не попадает
        Assert.Equal(100, DiskMap.TopFolders(t.P("r"), 1, 10, default, null, attr).Single().Bytes);  // и места не занимает
        Assert.Equal(50_100, DiskMap.TopFolders(t.P("r"), 1, 10, default).Single().Bytes);           // без подмены атрибутов считается всё
    }

    [Fact]
    public void Duplicates_CloudOnlyCopy_NotHashedNotReported()
    {
        using var t = new TempTree();
        var data = new byte[1_200_000]; new Random(9).NextBytes(data);
        Directory.CreateDirectory(t.P("x"));
        File.WriteAllBytes(t.P("x", "a.bin"), data); File.WriteAllBytes(t.P("x", "a.cloud.bin"), data);
        Assert.Empty(Duplicates.Find(new[] { t.Root }, 1024 * 1024, default, null, CloudByName(".cloud.")));
        Assert.Single(Duplicates.Find(new[] { t.Root }, 1024 * 1024));                                 // настоящая пара без подмены находится
    }

    [Fact]
    public void Duplicates_CloudOnlyFile_IsNeverOpened()
    {
        using var t = new TempTree();
        var data = new byte[1_200_000]; new Random(10).NextBytes(data);
        Directory.CreateDirectory(t.P("x"));
        File.WriteAllBytes(t.P("x", "a.bin"), data); File.WriteAllBytes(t.P("x", "b.cloud.bin"), data); File.WriteAllBytes(t.P("x", "c.cloud.bin"), data);
        // оба облачных файла «открыты эксклюзивно»: любая попытка прочитать дала бы отказ и была бы видна как пропущенный файл, а группа a+b не сложилась бы
        using var l1 = new FileStream(t.P("x", "b.cloud.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var l2 = new FileStream(t.P("x", "c.cloud.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(Duplicates.Find(new[] { t.Root }, 1024 * 1024, default, null, CloudByName(".cloud.")));
    }

    [Fact]
    public void OldDownloads_CloudOnlyInstaller_Skipped()
    {
        using var t = new TempTree();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        t.File_("dl\\setup.exe", 50, old); t.File_("dl\\cloud.exe", 50, old);
        File.SetCreationTimeUtc(t.P("dl", "setup.exe"), old); File.SetCreationTimeUtc(t.P("dl", "cloud.exe"), old);
        var r = DiskMap.OldDownloads(t.P("dl"), 30, new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), CloudByName("cloud"));
        Assert.Equal(new[] { "setup.exe" }, r.Select(f => Path.GetFileName(f.Path)));
    }

    private sealed class FakeEnv : IRecycleEnv
    {
        public List<string> MovedCalls = new(); public Func<string, FileAttributes> Attrs = _ => FileAttributes.Normal; public bool RemoveOnMove = true;
        public DriveType TypeOf(string r) => DriveType.Fixed;
        public long? BinMaxBytes(string r) => 1L << 40;
        public FileAttributes AttributesOf(string path) => Attrs(path);
        public void MoveToBin(IReadOnlyList<string> paths)                          // «Корзина» — просто удалить временный файл; настоящую Корзину владельца не трогаем
        {
            MovedCalls.AddRange(paths);
            if (RemoveOnMove) foreach (var p in paths) { if (File.Exists(p)) File.Delete(p); else if (Directory.Exists(p)) Directory.Delete(p, true); }
        }
    }

    [Fact]
    public void ToRecycleBin_CloudOnlyFile_SkippedWithReason_NeverSent()
    {
        using var t = new TempTree();
        var cloud = t.File_("p\\doc.cloud.txt", 10); var local = t.File_("p\\doc.txt", 10);
        var env = new FakeEnv { Attrs = p => p.Contains(".cloud.") ? FileAttributes.Normal | CloudFiles.RecallOnOpen : FileAttributes.Normal };
        var r = SafeDelete.ToRecycleBin(new[] { cloud, local }, env, new[] { t.P("p") });
        Assert.Equal(new[] { local }, r.Moved);
        Assert.Equal(new[] { local }, env.MovedCalls);
        var skip = Assert.Single(r.Skipped);
        Assert.Equal(cloud, skip.Path); Assert.Equal("файл только в облаке", skip.Reason);
        Assert.True(File.Exists(cloud));
    }

    [Fact]
    public void ToRecycleBin_FakeBin_LocalFileMoved_ReportedByFact()
    {
        using var t = new TempTree();
        var f = t.File_("keep\\tobin.txt", 10);
        var env = new FakeEnv();
        var r = SafeDelete.ToRecycleBin(new[] { f }, env);
        Assert.True(r.AllOk); Assert.Single(r.Moved);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void ToRecycleBin_MoverDidNothing_ReportedAsNotMoved()
    {
        using var t = new TempTree();
        var f = t.File_("keep\\stays.txt", 10);
        var r = SafeDelete.ToRecycleBin(new[] { f }, new FakeEnv { RemoveOnMove = false });
        Assert.False(r.AllOk); Assert.Empty(r.Moved);
        Assert.True(File.Exists(f));
    }

    // ---- I10: перенесённые личные папки ----
    [Fact]
    public void KnownFolders_RecycleRoots_ProfilePlusKnown_Deduped_NoDriveRoots()
    {
        var roots = KnownFolders.RecycleRoots(@"C:\Users\x", new string?[] { @"D:\Documents", @"d:\documents\", @"C:\Users\x\Desktop", @"E:\", "", null, @"C:\Users\x" });
        Assert.Equal(new[] { @"C:\Users\x", @"D:\Documents", @"C:\Users\x\Desktop" }, roots);
    }

    [Fact]
    public void KnownFolders_OutsideProfile_OnlyRedirected()
    {
        var outside = KnownFolders.OutsideProfile(@"C:\Users\x", new string?[] { @"D:\Downloads", @"C:\Users\x\Documents", @"D:\Docs\Sub" });
        Assert.Equal(new[] { @"D:\Downloads", @"D:\Docs\Sub" }, outside);
    }

    [Fact]
    public void KnownFolders_ThisPc_ResolvedAndAbsolute()
    {
        Assert.True(Path.IsPathFullyQualified(KnownFolders.DownloadsDir()));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), KnownFolders.PathOf(KnownFolders.Documents), ignoreCase: true);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), KnownFolders.PathOf(KnownFolders.Desktop), ignoreCase: true);
        Assert.NotEmpty(KnownFolders.Personal());
        Assert.Equal("374de290-123f-4565-9164-39c4925e467b", KnownFolders.Downloads.ToString());
    }

    [Fact]
    public void ToRecycleBin_RedirectedKnownFolder_Allowed_AppDataStillForbidden_OtherFoldersRefused()
    {
        using var t = new TempTree();
        var profile = t.Dir("profile"); var redirected = t.Dir("D\\Documents"); t.Dir("profile\\AppData");
        var inDocs = t.File_("D\\Documents\\dup.txt", 10);
        var inApp = t.File_("profile\\AppData\\cache.bin", 10);
        var stranger = t.File_("D\\Other\\x.txt", 10);
        var env = new FakeEnv();
        var roots = KnownFolders.RecycleRoots(profile, new[] { redirected });
        var r = SafeDelete.ToRecycleBin(new[] { inDocs, inApp, stranger }, env, roots, new[] { Path.Combine(profile, "AppData") });
        Assert.Equal(new[] { inDocs }, r.Moved);
        Assert.Contains(r.Skipped, s => s.Path == inApp && s.Reason.Contains("системная"));
        Assert.Contains(r.Skipped, s => s.Path == stranger && s.Reason.Contains("вне разрешённых"));
        Assert.True(File.Exists(inApp)); Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void ToRecycleBin_WithoutRedirectedRoot_SameFileRefused()                         // до правки такой файл был «вне разрешённых папок»
    {
        using var t = new TempTree();
        var profile = t.Dir("profile"); var inDocs = t.File_("D\\Documents\\dup.txt", 10);
        var r = SafeDelete.ToRecycleBin(new[] { inDocs }, new FakeEnv(), new[] { profile });
        Assert.Empty(r.Moved);
        Assert.Contains(r.Skipped, s => s.Reason.Contains("вне разрешённых"));
    }

    // ---- I11: фоновые процессы браузеров ----
    private sealed class Procs(string[] running, string[] withWindow) : IProcessQuery
    {
        public bool IsRunning(string name) => running.Contains(name, StringComparer.OrdinalIgnoreCase);
        public bool HasVisibleWindow(string name) => withWindow.Contains(name, StringComparer.OrdinalIgnoreCase);
        public bool AnyCommandLine(string wqlNameFilter, Func<string, bool> match) => false;
    }

    private static CleanupEnv Env(TempTree t, IProcessQuery procs) =>
        new(t.Dir("temp"), t.Dir("local"), t.Dir("roaming"), t.Dir("profile"), null, t.Dir("win"), t.Dir("pd"), t.Root, procs, () => DateTime.UtcNow, new FakeBin(), null);

    private static async Task<CleanupItem> Scan(CleanupEnv e, string id) => await UserCategories.All(e).First(c => c.Id == id).ScanAsync(default);

    [Theory]
    [InlineData("Browser.Edge", "msedge")]
    [InlineData("Browser.Chrome", "chrome")]
    [InlineData("Browser.Brave", "brave")]
    [InlineData("Browser.Yandex", "browser")]
    [InlineData("Browser.OperaGX", "opera")]
    public async Task Browser_RunningInBackgroundWithoutWindow_NotBlocked(string id, string proc)
    {
        using var t = new TempTree();
        var item = await Scan(Env(t, new Procs(new[] { proc }, Array.Empty<string>())), id);
        Assert.Null(item.BlockedBy);
    }

    [Theory]
    [InlineData("Browser.Edge", "msedge", "Edge")]
    [InlineData("Browser.Chrome", "chrome", "Chrome")]
    [InlineData("Browser.OperaGX", "opera", "Opera GX")]
    public async Task Browser_WithVisibleWindow_Blocked(string id, string proc, string title)
    {
        using var t = new TempTree();
        var item = await Scan(Env(t, new Procs(new[] { proc }, new[] { proc })), id);
        Assert.Equal(title, item.BlockedBy);
    }

    [Theory]
    [InlineData("Discord", "discord")]
    [InlineData("Telegram", "Telegram")]
    public async Task DiscordAndTelegram_TrayWithoutWindow_StillBlocked(string id, string proc)
    {
        using var t = new TempTree();
        var item = await Scan(Env(t, new Procs(new[] { proc }, Array.Empty<string>())), id);
        Assert.NotNull(item.BlockedBy);
    }

    [Fact]
    public async Task Steam_Running_StillBlocked()
    {
        using var t = new TempTree();
        var env = Env(t, new Procs(new[] { "steam", "steamwebhelper" }, Array.Empty<string>())) with { SteamPath = t.Dir("steam") };
        var cat = UserCategories.All(env).FirstOrDefault(c => c.Id == "Steam");
        if (cat == null) return;                                                               // категория Steam зависит от установленного клиента
        Assert.NotNull((await cat.ScanAsync(default)).BlockedBy);
    }

    [Fact]
    public async Task Browser_BackgroundProcess_CleanRunsAndSkipsLockedFilesSilently()
    {
        using var t = new TempTree();
        t.File_("local\\Microsoft\\Edge\\User Data\\Default\\Cache\\free", 100);
        var busy = t.File_("local\\Microsoft\\Edge\\User Data\\Default\\Cache\\busy", 100);
        var env = Env(t, new Procs(new[] { "msedge" }, Array.Empty<string>()));
        using var hold = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);      // «занят фоновым Edge»
        var r = await UserCategories.All(env).First(c => c.Id == "Browser.Edge").CleanAsync(default);
        Assert.Null(r.Error);
        Assert.Equal(100, r.FreedBytes);
        Assert.Equal(1, r.Skipped);
    }
}
