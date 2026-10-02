using Pult.Admin;
using Pult.Core.Admin;
namespace Pult.Core.Tests;

// Fix round 1 (ревью M3): заголовок скриптов, CLSID вместо ProgID, пользовательский COM в HKCU, подтверждения в окне помощника.
public partial class AdminMaintenanceTests
{
    private static string[] AllScripts() => new[]
    {
        MaintenanceScripts.SearchDrivers, MaintenanceScripts.InstallDrivers(new[] { G1 }), MaintenanceScripts.RestorePoint,
    };

    [Fact]
    public void Scripts_StartWithSystemOnlyModulePath_AndNoProgIds()
    {
        foreach (var sc in AllScripts())
        {
            var lines = sc.Split('\n');
            Assert.Equal("$env:PSModulePath = \"$PSHOME\\Modules\"", lines[0]);
            Assert.Equal("$PSModuleAutoLoadingPreference = 'None'", lines[1]);
            Assert.All(lines.Skip(2).TakeWhile(l => l.StartsWith("Import-Module")), l => Assert.StartsWith("Import-Module \"$PSHOME\\Modules\\", l));
            Assert.DoesNotContain("-ComObject", sc);
            Assert.DoesNotContain("Microsoft.Update.Session", sc);
            Assert.DoesNotContain("Microsoft.Update.UpdateColl", sc);
            Assert.DoesNotContain("wmiclass", sc);
            Assert.DoesNotContain("Get-WmiObject", sc);
        }
        Assert.Contains("GetTypeFromCLSID([guid]'4CB43D7F-7EEE-4906-8698-60DA1C38F2FE')", MaintenanceScripts.SearchDrivers);
        Assert.Contains("CimCmdlets.psd1", MaintenanceScripts.RestorePoint);
        // CreationTime из WMI — строка DMTF («20260915102153.000000-000»): [datetime] на ней падает (-2146233087, живая проверка 30.09)
        Assert.DoesNotContain("[datetime]$pt", MaintenanceScripts.RestorePoint);
        Assert.Contains("ManagementDateTimeConverter", MaintenanceScripts.RestorePoint);
    }

    [Fact]
    public void UserPsModulePath_InEnvironment_DoesNotReachScript()
    {
        // Пользователь подложил PSModulePath (как в HKCU\Environment): скрипт всё равно видит только $PSHOME\Modules.
        var userMods = Make("usermods");
        var env = PowerShellHost.Env();
        env["PSModulePath"] = userMods + ";" + env["PSModulePath"];
        var script = MaintenanceScripts.Header("Microsoft.PowerShell.Utility")
                     + "Write-Output $env:PSModulePath\nWrite-Output ($PSHOME + '\\Modules')\nWrite-Output $PSModuleAutoLoadingPreference";
        var r = SystemProcess.Run(PowerShellHost.ExePath, PowerShellHost.Arguments(script), TimeSpan.FromSeconds(60), env);
        var lines = r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal(lines[1], lines[0]);
        Assert.DoesNotContain(userMods, lines[0]);
        Assert.Equal("None", lines[2]);
    }

    [LiveFact]                                                  // настоящий онлайн-поиск в Центре обновления (до 3 минут, нужна сеть): только PULT_LIVE=1
    public async Task Live_RealSearchScript_RunsWithHeader_NoAdminNeeded()
    {
        var s = new PsWuaSearch(new PowerShellHost());
        await s.SearchDriversAsync(default);
        Assert.Null(s.LastError);                               // заголовок + CLSID работают (поиск не требует прав)
    }

    private sealed class FakeClasses(params string[] keys) : IUserClassesRegistry
    {
        public bool KeyExists(string p) => keys.Contains(p, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Microsoft.Update.Session")]
    [InlineData("CLSID\\{4CB43D7F-7EEE-4906-8698-60DA1C38F2FE}")]
    [InlineData("CLSID\\{13639463-00DB-4646-803D-528026140D88}")]
    [InlineData("Wow6432Node\\CLSID\\{4590F811-1D3A-11D0-891F-00AA004B2E24}")]
    [InlineData("CLSID\\{CF4CC405-E2C5-4DDD-B3CE-5E7582D8C9FA}")]
    public void ComGuard_UserRegistration_RefusesBothActions_ScriptNeverRuns(string key)
    {
        var runner = new FakeRunner("");
        var m = new SystemMaintenance(_ => true, new FakeClasses(key), runner);
        var a = m.InstallDrivers(new[] { G1 }, false);
        var b = m.CreateRestorePoint();
        Assert.False(a.Ok); Assert.False(b.Ok);
        Assert.Equal(AdminCodes.ComRegistration, a.Code); Assert.Equal(AdminCodes.ComRegistration, b.Code);
        Assert.Contains("регистрация COM", a.Message);
        Assert.Null(runner.LastScript);
    }

    [Fact]
    public void ComGuard_Clean_AllowsRestorePoint()
    {
        var runner = new FakeRunner("CREATED\n");
        Assert.True(new SystemMaintenance(_ => true, new FakeClasses(), runner).CreateRestorePoint().Ok);
        Assert.NotNull(runner.LastScript);
    }

    [Fact]
    public void Install_Declined_NothingHappens_TextListsTitlesCountAndRestorePoint()
    {
        var log = new List<string>(); string? shown = null;
        var be = new FakeBackend(new(1, 0, 0, false, null), log);
        var inst = new DriverInstaller(new FakeSearch(G1, G2), new RestorePointService(new FakeRestore(RestoreOutcome.Created, log)), be, t => { shown = t; return false; });
        var r = inst.Run(new[] { G1, G2 });
        Assert.False(r.Ok); Assert.Equal(AdminCodes.Declined, r.Code);
        Assert.Empty(log); Assert.Null(be.Got);
        Assert.Contains("2 шт.", shown); Assert.Contains("• t", shown); Assert.Contains("точка восстановления", shown);
    }

    [Fact]
    public void Install_NothingToInstall_NoQuestion()
    {
        var asked = false;
        var inst = new DriverInstaller(new FakeSearch(G2), new RestorePointService(new FakeRestore(RestoreOutcome.Created, new List<string>())),
                                       new FakeBackend(new(1, 0, 0, false, null), new List<string>()), _ => { asked = true; return true; });
        Assert.True(inst.Run(new[] { G1 }).Ok);
        Assert.False(asked);
    }

    [Fact]
    public void Cleaner_Declined_DeletesNothing_TextHasEstimate()
    {
        var sys = Make("winD"); var pd = Make("pdD");
        var sysDir = Path.Combine(sys, "System32"); Directory.CreateDirectory(sysDir);
        var f = File_(Path.Combine(pd, "Microsoft", "Windows", "WER", "ReportQueue", "q.wer"), 4096);
        string? shown = null;
        var r = SystemCleaner.Run("Wer", sysDir, pd, Now, new FakeDism(new(0, "", false)), t => { shown = t; return false; });
        Assert.False(r.Ok); Assert.Equal(AdminCodes.Declined, r.Code);
        Assert.True(File.Exists(f));
        Assert.Contains("4 КБ", shown);
        Assert.Contains("безвозвратное", shown);
    }

    private sealed class CountingDism : IDismRunner
    {
        public int Runs;
        public ProcessOutcome StartComponentCleanup(TimeSpan t) { Runs++; return new(0, "", false); }
    }

    [Fact]
    public void ComponentCleanup_Declined_DismNotRun_WarnsIrreversible()
    {
        var dism = new CountingDism(); string? shown = null;
        var r = SystemCleaner.Run("ComponentCleanup", @"C:\Windows\System32", @"C:\ProgramData", Now, dism, t => { shown = t; return false; });
        Assert.False(r.Ok); Assert.Equal(AdminCodes.Declined, r.Code);
        Assert.Equal(0, dism.Runs);
        Assert.Contains("необратима", shown);
        Assert.True(SystemCleaner.Run("ComponentCleanup", @"C:\Windows\System32", @"C:\ProgramData", Now, dism, _ => true).Ok);
        Assert.Equal(1, dism.Runs);
    }

    [Fact]
    public void Estimate_CountsOnlyOldFiles_SkipsLinks()
    {
        var outside = Make("estOut"); File_(Path.Combine(outside, "big.dat"), 9999);
        var root = Make("est");
        File_(Path.Combine(root, "old.tmp"), 100, Now - TimeSpan.FromDays(3));
        File_(Path.Combine(root, "new.tmp"), 50, Now - TimeSpan.FromHours(1));
        Junction(Path.Combine(root, "lnk"), outside);
        Assert.Equal(100, SystemCleaner.Estimate(new[] { root }, SystemCleaner.TempMinAge, Now));
    }

    [Fact]
    public void SystemProcess_IgnoresStderr_InParsedOutput()
    {
        var r = SystemProcess.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c echo R^|1^|0^|0^|0 1>&2 & echo OUT", TimeSpan.FromSeconds(20), CleanEnvironment.ForSystem());
        Assert.Contains("OUT", r.Output);
        Assert.DoesNotContain("R|1", r.Output);
    }
}
