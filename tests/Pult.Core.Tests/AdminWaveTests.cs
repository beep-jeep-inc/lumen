using Pult.Admin;
using Pult.Core.Admin;
namespace Pult.Core.Tests;

/// Волна A1 (аудит допущений): обычная учётная запись, место установки, чужой пользователь Windows, запреты помощника.
public class AdminWaveTests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-aw-" + Guid.NewGuid().ToString("N"));
    public AdminWaveTests() { Directory.CreateDirectory(_d); }
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    // ---- C1: обычная учётная запись ----
    [Theory]
    [InlineData(true, "S-1-5-21-1-2-3-1001", "S-1-5-32-544", "S-1-1-0")]      // группа есть (в том числе deny-only при UAC)
    [InlineData(true, "s-1-5-32-544")]
    [InlineData(false, "S-1-5-21-1-2-3-1001", "S-1-5-32-545", "S-1-1-0")]     // только «Пользователи»
    [InlineData(false)]
    public void Rights_AdminGroupInToken_DecidesCanElevate(bool expected, params string[] sids) =>
        Assert.Equal(expected, AdminRights.ContainsAdministrators(sids));

    [Fact]
    public async Task Send_StandardAccount_RefusesAtOnce_WithoutRunningTask_EvenIfNotInstalled()
    {
        var ran = false;
        var client = new AdminClient(_d, _d, runTask: () => ran = true, isInstalled: () => false, canElevate: () => false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromSeconds(30), default);
        Assert.False(r.Ok);
        Assert.Equal(AdminCodes.NoAdminAccount, r.Code);
        Assert.Equal(AdminRights.NeedAdminText, r.Message);
        Assert.False(ran);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(File.Exists(Path.Combine(_d, "admin.req")));                 // запрос даже не писался
    }

    [Theory]
    [InlineData(AdminAction.DriverInstall, "norp:11111111-2222-3333-4444-555555555555")]
    [InlineData(AdminAction.CleanSystem, "Wer")]
    [InlineData(AdminAction.RestorePoint, "")]
    public async Task Send_StandardAccount_MaintenanceActionsRefused(AdminAction a, string arg)
    {
        var client = new AdminClient(_d, _d, runTask: () => true, isInstalled: () => true, canElevate: () => false);
        Assert.Equal(AdminCodes.NoAdminAccount, (await client.SendAsync(a, arg, default)).Code);
    }

    // ---- C2: где живёт пульт ----
    [Fact]
    public void Install_PultExeNextToHelper_Refused()
    {
        var dir = @"C:\Program Files\Pult";
        var exists = (string p) => string.Equals(p, dir + @"\Pult.exe", StringComparison.OrdinalIgnoreCase);
        var why = Installer.InstallPathProblem(dir, @"D:\src\PultAdmin.exe", exists);
        Assert.NotNull(why);
        Assert.Contains("Pult.exe", why);
    }

    [Fact]
    public void Install_FromFolderWithPultExeIntoItself_Refused()
    {
        var dir = @"C:\Program Files\Pult";
        var why = Installer.InstallPathProblem(dir, dir + @"\PultAdmin.exe", p => p.EndsWith("Pult.exe", StringComparison.OrdinalIgnoreCase) && p.StartsWith(dir));
        Assert.NotNull(why);
    }

    [Fact]
    public void Install_NoPultExeThere_Allowed() =>
        Assert.Null(Installer.InstallPathProblem(@"C:\Program Files\Pult", @"C:\Users\x\AppData\Local\Programs\Pult\PultAdmin.exe", _ => false));

    [Fact]
    public void Install_RefusalCode_IsDistinct() => Assert.Equal(11, AdminCodes.ExitWrongFolder);

    [Fact]
    public void Uninstall_RemovesOnlyHelperFiles_NeverPultExe_NorTheFolderWithIt()
    {
        var dir = Path.Combine(_d, "Pult"); Directory.CreateDirectory(dir);
        foreach (var f in new[] { "PultAdmin.exe", "task.xml", "Pult.exe", "config.json" }) File.WriteAllText(Path.Combine(dir, f), f);
        var locked = new List<string>();
        Installer.RemoveOwnFiles(dir, locked.Add);
        Assert.False(File.Exists(Path.Combine(dir, "PultAdmin.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "task.xml")));
        Assert.True(File.Exists(Path.Combine(dir, "Pult.exe")));                  // пульт цел
        Assert.True(File.Exists(Path.Combine(dir, "config.json")));               // и всё чужое тоже
        Assert.Empty(locked);
    }

    [Fact]
    public void Uninstall_OnlyHelperFiles_FolderRemoved()
    {
        var dir = Path.Combine(_d, "Pult2"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "PultAdmin.exe"), "x");
        Installer.RemoveOwnFiles(dir, _ => { });
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Uninstall_LockedExe_GoesToRebootList_ForeignFilesNotListed()
    {
        var dir = Path.Combine(_d, "Pult3"); Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "PultAdmin.exe");
        using var hold = new FileStream(exe, FileMode.Create, FileAccess.ReadWrite, FileShare.None);   // «работающий» exe
        File.WriteAllText(Path.Combine(dir, "Pult.exe"), "x");
        var locked = new List<string>();
        Installer.RemoveOwnFiles(dir, locked.Add);
        Assert.Equal(new[] { exe }, locked);                                      // папку с Pult.exe при перезагрузке не удаляем
        Assert.True(File.Exists(Path.Combine(dir, "Pult.exe")));
    }

    // ---- Устаревшая папка zapret от прежних версий ----
    [Fact]
    public void LegacyZapret_Folder_Removed()
    {
        var root = Path.Combine(_d, "Pult5", "zapret"); Directory.CreateDirectory(Path.Combine(root, "v1")); File.WriteAllText(Path.Combine(root, "v1", "a.bat"), "x");
        var log = new List<string>();
        Assert.True(Installer.RemoveLegacyZapret(root, log.Add));
        Assert.False(Directory.Exists(root));
        Assert.Single(log);
    }

    [Fact]
    public void LegacyZapret_Absent_NothingToDo_NoLog()
    {
        var log = new List<string>();
        Assert.True(Installer.RemoveLegacyZapret(Path.Combine(_d, "нет-такой"), log.Add));
        Assert.Empty(log);
    }

    [Fact]
    public void LegacyZapret_FileInUse_FolderStays_LoggedNotKilled()
    {
        var root = Path.Combine(_d, "Pult6", "zapret"); Directory.CreateDirectory(Path.Combine(root, "bin"));
        var busy = Path.Combine(root, "bin", "winws.exe");
        using var hold = new FileStream(busy, FileMode.Create, FileAccess.ReadWrite, FileShare.None);   // «запущенный winws»
        var log = new List<string>();
        Assert.False(Installer.RemoveLegacyZapret(root, log.Add));
        Assert.True(Directory.Exists(root));
        Assert.Contains("осталась на месте", Assert.Single(log));
    }

    [Fact]
    public void LegacyZapret_IsJunction_OnlyLinkRemoved_TargetSurvives()
    {
        var dir = Path.Combine(_d, "Pult4"); Directory.CreateDirectory(dir);
        var real = Path.Combine(_d, "precious"); Directory.CreateDirectory(real); File.WriteAllText(Path.Combine(real, "keep.txt"), "x");
        var link = Path.Combine(dir, "zapret");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using (var p = System.Diagnostics.Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        Assert.True(Installer.RemoveLegacyZapret(link, null));
        Assert.True(File.Exists(Path.Combine(real, "keep.txt")));
        Assert.False(Directory.Exists(link));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Pult\Pult.exe", true)]
    [InlineData(@"c:\program files\pult\Pult.exe", true)]
    [InlineData(@"C:\Program Files\Pult\App\Pult.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Pult\Pult.exe", false)]
    [InlineData(@"C:\Program Files\Pult2\Pult.exe", false)]
    [InlineData("", false)]
    public void StartupChecks_PultInHelperFolder(string exe, bool expected) =>
        Assert.Equal(expected, StartupChecks.IsInHelperFolder(exe, @"C:\Program Files"));

    // ---- I6: несколько пользователей Windows ----
    private const string Sid = "S-1-5-21-1756151443-3689284912-1088699740-1001";
    [Theory]
    [InlineData("<Principal><UserId>S-1-5-21-1756151443-3689284912-1088699740-1001</UserId></Principal>", true)]
    [InlineData("<UserId>DESKTOP\\User</UserId>", true)]
    [InlineData("<UserId>desktop\\user</UserId>", true)]
    [InlineData("<UserId>S-1-5-21-1756151443-3689284912-1088699740-1002</UserId>", false)]   // брат
    [InlineData("<UserId>DESKTOP\\brother</UserId>", false)]
    [InlineData("<UserId>S-1-5-21-1756151443-3689284912-1088699740-10011</UserId>", false)]  // префикс — не то же самое
    [InlineData("", true)]                                                                   // не смогли прочитать — не блокируем
    [InlineData("<Task/>", true)]
    public void TaskOwner_Matches(string xml, bool mine) => Assert.Equal(mine, TaskOwner.BelongsTo(xml, Sid, "DESKTOP\\User"));

    [Fact]
    public void TaskOwner_Null_IsUnknown_NotBlocked() => Assert.True(TaskOwner.BelongsTo(null, Sid, "DESKTOP\\User"));

    [Fact]
    public async Task Send_TaskOfAnotherUser_RefusesAtOnce_WithoutRunningTask()
    {
        var ran = false;
        var client = new AdminClient(_d, _d, runTask: () => ran = true, isInstalled: () => true, ownsTask: () => false);
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromSeconds(30), default);
        Assert.Equal(AdminCodes.OtherUser, r.Code);
        Assert.Contains("другого пользователя Windows", r.Message);
        Assert.False(ran);
        Assert.False(File.Exists(Path.Combine(_d, "admin.req")));
        Assert.True(client.TaskBelongsToOtherUser);
    }

    [Fact]
    public void TaskOfAnotherUser_NotInstalled_IsNotOtherUser() =>
        Assert.False(new AdminClient(_d, _d, () => true, () => false, ownsTask: () => false).TaskBelongsToOtherUser);

    // ---- I7: пульт запущен с повышением ----
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Relaunch_NotElevated_Never() => Assert.False(StartupChecks.ShouldRelaunchUnelevated(false, null, Now));

    [Fact]
    public void Relaunch_Elevated_FirstTime_Yes() => Assert.True(StartupChecks.ShouldRelaunchUnelevated(true, null, Now));

    [Fact]
    public void Relaunch_Elevated_TriedSecondsAgo_NoLoop() => Assert.False(StartupChecks.ShouldRelaunchUnelevated(true, Now.AddSeconds(-5), Now));

    [Fact]
    public void Relaunch_Elevated_TriedLongAgo_AgainAllowed() => Assert.True(StartupChecks.ShouldRelaunchUnelevated(true, Now.AddMinutes(-10), Now));

    [Fact]
    public void Relaunch_StampFromFuture_Yes_NotStuckForever() => Assert.True(StartupChecks.ShouldRelaunchUnelevated(true, Now.AddDays(3), Now));

    // ---- Fix round 1: оболочка рабочего стола ----
    [Fact]
    public void Relaunch_OnlyWhenDesktopShellRuns()
    {
        Assert.False(StartupChecks.DesktopShellRunning(IntPtr.Zero));
        Assert.True(StartupChecks.DesktopShellRunning(new IntPtr(0x1234)));
    }
}
