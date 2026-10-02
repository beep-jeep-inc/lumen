using Pult.Admin;
using Pult.Core.Admin;
using Pult.Core.Maintenance;
namespace Pult.Core.Tests;

/// Winget с правами администратора: аргумент и политика помощника, окно подтверждения, поиск winget, признаки неудачи (чистая логика, без запуска winget).
public class WingetAdminTests : IDisposable
{
    private static readonly string Nonce = new string('b', 32);
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-wa-" + Guid.NewGuid().ToString("N"));
    public WingetAdminTests() { Directory.CreateDirectory(_d); }
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    // ---- аргумент ----

    [Theory]
    [InlineData("upgrade|Microsoft.VCRedist.2015+.x64", WingetMode.Upgrade, "Microsoft.VCRedist.2015+.x64")]
    [InlineData("install|Python.Python.3.12", WingetMode.Install, "Python.Python.3.12")]
    [InlineData("upgrade|7zip.7zip", WingetMode.Upgrade, "7zip.7zip")]
    [InlineData("upgrade|a", WingetMode.Upgrade, "a")]
    [InlineData("upgrade|Valve.Steam_x-1", WingetMode.Upgrade, "Valve.Steam_x-1")]
    public void Arg_Valid(string arg, WingetMode mode, string id)
    {
        Assert.True(WingetArg.TryParse(arg, out var m, out var i, out _));
        Assert.Equal(mode, m); Assert.Equal(id, i);
        Assert.Equal(arg, WingetArg.Build(m, i));
    }

    [Fact]
    public void Arg_IdLength_128Ok_129Refused()
    {
        Assert.True(WingetArg.TryParse("upgrade|" + new string('a', 128), out _, out _, out _));
        Assert.False(WingetArg.TryParse("upgrade|" + new string('a', 129), out _, out _, out _));
        Assert.False(WingetArg.TryParse(new string('a', 5000), out _, out _, out _));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("upgrade")] [InlineData("upgrade|")] [InlineData("|x")] [InlineData("upgrade||x")]
    [InlineData("upgrade|a|b")] [InlineData("install|x|y")] [InlineData("Upgrade|x")] [InlineData("INSTALL|x")] [InlineData("remove|x")] [InlineData("uninstall|x")]
    [InlineData("upgrade|-x")] [InlineData("upgrade|--source")] [InlineData("upgrade|.x")] [InlineData("upgrade|_x")] [InlineData("upgrade|+x")]
    [InlineData("upgrade|a b")] [InlineData("upgrade| a")] [InlineData("upgrade|a ")] [InlineData("upgrade|a\n")] [InlineData("upgrade|a\r")] [InlineData("upgrade|a\0")]
    [InlineData("upgrade|a;calc")] [InlineData("upgrade|a&b")] [InlineData("upgrade|a\"b")] [InlineData("upgrade|a'b")] [InlineData("upgrade|a/b")] [InlineData("upgrade|a\\b")]
    [InlineData("upgrade|а.русский")] [InlineData("upgrade|ａ")] [InlineData("upgrade|a=b")] [InlineData("upgrade|%PATH%")] [InlineData("upgrade|a:b")] [InlineData("upgrade|a*")]
    [InlineData(" upgrade|x")] [InlineData("upgrade |x")] [InlineData("upgrade|x|")]
    public void Arg_Invalid_Refused(string? arg) => Assert.False(WingetArg.TryParse(arg, out _, out _, out var problem) || problem == "");

    [Fact]
    public void Arg_Invalid_GivesNonEmptyProblem()
    {
        Assert.False(WingetArg.TryParse("upgrade|a b", out _, out _, out var p));
        Assert.NotEqual("", p);
    }

    [Fact]
    public void CommandArgs_AreExactlyTheFixedOnes_IdIsOneElement()
    {
        Assert.Equal(new[] { "upgrade", "--id", "X.Y", "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" },
                     WingetArg.CommandArgs(WingetMode.Upgrade, "X.Y"));
        Assert.Equal(new[] { "install", "--id", "X.Y", "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" },
                     WingetArg.CommandArgs(WingetMode.Install, "X.Y"));
    }

    [Fact]
    public void ConfirmText_ShowsIdAndTheQuestion()
    {
        var up = WingetArg.ConfirmText(WingetMode.Upgrade, "Valve.Steam");
        Assert.Contains("Valve.Steam", up); Assert.Contains("Обновить", up); Assert.Contains("с правами администратора", up);
        var inst = WingetArg.ConfirmText(WingetMode.Install, "Valve.Steam");
        Assert.Contains("Установить", inst); Assert.DoesNotContain("Обновить", inst);
    }

    [Fact]
    public void ExitCode_RoundTrips_IncludingNegative()
    {
        Assert.Equal("winget=2316632137", WingetArg.ExitCode(unchecked((int)0x8A150049)));
        Assert.Equal(unchecked((int)0x8A150049), WingetArg.TryParseExitCode("winget=2316632137"));
        Assert.Equal(-1, WingetArg.TryParseExitCode(WingetArg.ExitCode(-1)));
        Assert.Null(WingetArg.TryParseExitCode("winget=abc")); Assert.Null(WingetArg.TryParseExitCode("winget=-5")); Assert.Null(WingetArg.TryParseExitCode("freed=1;skipped=0")); Assert.Null(WingetArg.TryParseExitCode(null));
    }

    // ---- политика ----

    private static Decision Decide(string arg) => AdminPolicy.Decide(new AdminRequest(Nonce, AdminAction.WingetUpgrade, arg));

    [Fact]
    public void Policy_WingetUpgrade_AllowsOnlyValidArg()
    {
        Assert.True(Decide("upgrade|Valve.Steam").Allowed);
        Assert.True(Decide("install|Valve.Steam").Allowed);
        foreach (var bad in new[] { "", "Valve.Steam", "upgrade|Valve.Steam|extra", "upgrade|-x", "upgrade|a b", "upgrade|a\n", "all|x", "upgrade|" + new string('a', 200) })
            Assert.False(Decide(bad).Allowed, bad);
    }

    [Fact]
    public void WingetUpgrade_IsNotSystemJob_AndIsLongForBusyLogic()
    {
        Assert.DoesNotContain(Enum.GetNames<AdminJobKind>(), n => n.Contains("Winget", StringComparison.OrdinalIgnoreCase));    // SYSTEM-исполнитель такого задания не знает
        Assert.Equal(AdminMaintenance.WingetWait, AdminMaintenance.WaitTimeout(AdminAction.WingetUpgrade, "upgrade|x"));
        Assert.True(AdminMaintenance.WingetWait >= AdminMaintenance.ConfirmTimeout + AdminMaintenance.WingetProcessTimeout);
        Assert.True(AdminMaintenance.WingetWait < TimeSpan.FromMinutes(80));
    }

    [Fact]
    public async Task ShortAction_FailsFast_WhileHelperHeldByWingetUpgrade()
    {
        Directory.CreateDirectory(Path.Combine(_d, "c")); Directory.CreateDirectory(Path.Combine(_d, "cr"));
        var client = new AdminClient(Path.Combine(_d, "c"), Path.Combine(_d, "cr"), runTask: () => true, isInstalled: () => true) { BusyWait = TimeSpan.FromMilliseconds(300) };
        var held = client.SendAsync(AdminAction.WingetUpgrade, "upgrade|Valve.Steam", TimeSpan.FromSeconds(3), default);      // ответа не будет
        await Task.Delay(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await client.SendAsync(AdminAction.StartupToggle, "HklmRun|off|X", TimeSpan.FromSeconds(10), default);
        Assert.False(r.Ok); Assert.Equal(AdminCodes.Busy, r.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
        await held;
    }

    // ---- исполнитель в контексте пользователя: окно подтверждения ----

    private sealed class Spy
    {
        public List<string> Asked = new(); public List<(string Exe, string[] Args)> Runs = new();
        public ProcessOutcome Outcome = new(0, "Успешно установлено", false);
        public string? Untrusted; public ConfirmAnswer Answer = ConfirmAnswer.Yes; public string? Exe = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe";
        public UserWinget Make() => new(t => { Asked.Add(t); return Answer; }, () => Exe, (e, a, t) => { Runs.Add((e, a.ToArray())); return Outcome; }, _ => Untrusted);
    }

    [Fact]
    public void UserWinget_No_NothingRuns_SeparateCode()
    {
        var s = new Spy { Answer = ConfirmAnswer.No };
        var r = s.Make().Run(WingetMode.Upgrade, "Valve.Steam");
        Assert.False(r.Ok); Assert.Equal(AdminCodes.WingetDeclined, r.Code); Assert.NotEqual(AdminCodes.Declined, r.Code);
        Assert.Empty(s.Runs); Assert.Contains("Valve.Steam", Assert.Single(s.Asked));
    }

    [Fact]
    public void UserWinget_UntrustedFolder_NoWindow_NoRun_CodeUntrusted()
    {
        var s = new Spy { Untrusted = @"C:\Program Files\WindowsApps: менять может S-1-5-21-1" };
        var r = s.Make().Run(WingetMode.Upgrade, "Valve.Steam");
        Assert.False(r.Ok); Assert.Equal(AdminCodes.WingetUntrusted, r.Code);
        Assert.Empty(s.Asked); Assert.Empty(s.Runs);
        Assert.Contains("WindowsApps", r.Message);
        var ok = new Spy(); ok.Make().Run(WingetMode.Upgrade, "Valve.Steam");
        Assert.Single(ok.Asked); Assert.Single(ok.Runs);                                  // trustProblem == null → окно показывается
    }

    [Fact]
    public void UserWinget_Timeout_IsNo_NothingRuns()
    {
        var s = new Spy { Answer = ConfirmAnswer.Timeout };
        var r = s.Make().Run(WingetMode.Install, "Valve.Steam");
        Assert.False(r.Ok); Assert.Equal(AdminCodes.DeclinedTimeout, r.Code); Assert.Empty(s.Runs);
    }

    [Fact]
    public void UserWinget_Yes_RunsFixedArgs_ByFullPath()
    {
        var s = new Spy();
        var r = s.Make().Run(WingetMode.Upgrade, "Valve.Steam");
        Assert.True(r.Ok);
        var run = Assert.Single(s.Runs);
        Assert.Equal(s.Exe, run.Exe); Assert.True(Path.IsPathFullyQualified(run.Exe));
        Assert.Equal(WingetArg.CommandArgs(WingetMode.Upgrade, "Valve.Steam"), run.Args);
        Assert.Contains("Обновить", Assert.Single(s.Asked));
    }

    [Fact]
    public void UserWinget_InvalidId_OrNoWinget_AsksNothing_RunsNothing()
    {
        var s = new Spy();
        Assert.False(s.Make().Run(WingetMode.Upgrade, "-x").Ok);
        Assert.False(s.Make().Run(WingetMode.Upgrade, "a b").Ok);
        Assert.Empty(s.Asked); Assert.Empty(s.Runs);
        s.Exe = null;
        var r = s.Make().Run(WingetMode.Upgrade, "Valve.Steam");
        Assert.False(r.Ok); Assert.Contains("winget не найден", r.Message);
        Assert.Empty(s.Asked); Assert.Empty(s.Runs);
    }

    [Fact]
    public void UserWinget_Results_ByExitCode()
    {
        var s = new Spy { Outcome = new(unchecked((int)0x8A150049), "Installer failed with exit code: 1603", false) };
        var r = s.Make().Run(WingetMode.Upgrade, "Valve.Steam");
        Assert.False(r.Ok); Assert.Equal("winget=2316632137", r.Code); Assert.Contains("0x8A150049", r.Message);
        s.Outcome = new(WingetRunner.NoApplicableUpdate, "", false);
        Assert.True(s.Make().Run(WingetMode.Upgrade, "Valve.Steam").Ok);
        s.Outcome = new(-1, "", true);
        var t = s.Make().Run(WingetMode.Upgrade, "Valve.Steam");
        Assert.False(t.Ok); Assert.Contains("ещё работает", t.Message);
    }

    // ---- RequestProcessor ----

    private sealed class FakeWinget : IWingetExecutor
    {
        public List<string> Calls = new();
        public MaintenanceResult Run(WingetMode mode, string id) { Calls.Add(mode + ":" + id); return new(true, "ok"); }
    }

    private (AdminResponse? Res, FakeWinget W, int MaintCalls) Process(string arg, AdminAction action = AdminAction.WingetUpgrade)
    {
        var user = Path.Combine(_d, "u" + Guid.NewGuid().ToString("N")[..6]); var res = Path.Combine(_d, "r" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(user); Directory.CreateDirectory(res);
        var req = Path.Combine(user, "admin.req");
        File.WriteAllText(req, AdminRequest.New(action, arg).ToJson());
        var w = new FakeWinget(); var maint = new CountingMaint();
        var r = new RequestProcessor(req, res, null, maint, null, w).ProcessOnce();
        return (r, w, maint.Calls);
    }

    private sealed class CountingMaint : IMaintenanceExecutor
    {
        public int Calls;
        public MaintenanceResult InstallDrivers(IReadOnlyList<string> ids, bool skipRestore) { Calls++; return new(true, ""); }
        public MaintenanceResult CreateRestorePoint() { Calls++; return new(true, ""); }
        public MaintenanceResult CleanSystem(string id) { Calls++; return new(true, ""); }
        public MaintenanceResult RepairSystem() { Calls++; return new(true, ""); }
    }

    [Fact]
    public void Processor_WingetUpgrade_ValidReachesWingetExecutor_NotMaintenance()
    {
        var (r, w, maint) = Process("install|Valve.Steam");
        Assert.True(r!.Ok); Assert.Equal(new[] { "Install:Valve.Steam" }, w.Calls); Assert.Equal(0, maint);
    }

    [Theory]
    [InlineData("")] [InlineData("Valve.Steam")] [InlineData("upgrade|a|b")] [InlineData("upgrade|-x")] [InlineData("upgrade|a b")] [InlineData("upgrade|x;calc")] [InlineData("remove|x")]
    public void Processor_WingetUpgrade_BadArg_NeverReachesAnyExecutor(string arg)
    {
        var (r, w, maint) = Process(arg);
        Assert.False(r!.Ok); Assert.Empty(w.Calls); Assert.Equal(0, maint);
    }

    [Fact]
    public void Processor_WithoutWingetExecutor_Fails_NotSilentOk()
    {
        var user = Path.Combine(_d, "uu"); var res = Path.Combine(_d, "rr"); Directory.CreateDirectory(user); Directory.CreateDirectory(res);
        var req = Path.Combine(user, "admin.req");
        File.WriteAllText(req, AdminRequest.New(AdminAction.WingetUpgrade, "upgrade|Valve.Steam").ToJson());
        var r = new RequestProcessor(req, res, null, new CountingMaint()).ProcessOnce();
        Assert.False(r!.Ok);
    }

    // ---- поиск winget: защищённая папка пакета, ссылка-псевдоним, никакого PATH ----

    private static string? Find(IEnumerable<string> dirs, Func<string, bool> exists, string arch = "x64") =>
        Winget.FindForHelper(@"C:\Program Files", arch, _ => dirs, exists);

    [Fact]
    public void FindForHelper_PicksNewestVersionOfRightArch_WithExistingExe()
    {
        var dirs = new[]
        {
            "Microsoft.DesktopAppInstaller_1.28.100.0_x64__8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_1.30.0.0_arm64__8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_2026.917.151.0_neutral_~_8wekyb3d8bbwe",
            "Microsoft.DesktopAppInstaller_1.29.380.0_neutral_split.language-ru_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_9.9_x64__evilpublisher",
        };
        var p = Find(dirs, f => f.StartsWith(@"C:\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe", p);
        Assert.Equal(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.30.0.0_arm64__8wekyb3d8bbwe\winget.exe",
                     Find(dirs, f => f.StartsWith(@"C:\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase), "arm64"));
    }

    [Fact]
    public void FindForHelper_SkipsNewestWithoutExe()
    {
        var dirs = new[] { "Microsoft.DesktopAppInstaller_1.28.100.0_x64__8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe" };
        var p = Find(dirs, f => f.Contains("1.28.100.0"));
        Assert.Contains("1.28.100.0", p);
    }

    [Fact]
    public void FindForHelper_OnlyAlias_IsNull_NeverPathLookup()
    {
        var alias = @"C:\Users\U\AppData\Local\Microsoft\WindowsApps\winget.exe";
        Assert.Null(Find(Array.Empty<string>(), f => f == alias));                          // псевдоним (ссылка) в профиле — не защита
        Assert.Null(Find(Array.Empty<string>(), f => f == @"C:\Tools\winget.exe"));         // PATH не смотрим
        Assert.NotNull(Find(new[] { "Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe" }, f => f.EndsWith("winget.exe")));   // пакет есть → путь пакета
    }

    [Fact]
    public void FindForHelper_IoFailure_IsNull()
    {
        Assert.Null(Winget.FindForHelper(@"C:\Program Files", "x64", _ => throw new UnauthorizedAccessException(), _ => true));
    }

    // ---- чистое окружение пользователя для winget ----

    [Fact]
    public void ForUserEnvironment_HasProfileFromKnownFolders_NotUserOverrides()
    {
        var old = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", @"C:\evil.dll");
        try
        {
            var env = CleanEnvironment.ForUser();
            Assert.False(env.ContainsKey("DOTNET_STARTUP_HOOKS"));
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), env["LOCALAPPDATA"]);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), env["USERPROFILE"]);
            Assert.Equal(Path.Combine(env["LOCALAPPDATA"], "Temp"), env["TEMP"]);
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", old); }
    }

    // ---- признаки неудачи (D1/D2) ----

    private static WingetRun Fail(int code, string output) => new(code, output, false, false);

    [Theory]
    [InlineData("Installer failed with exit code: 1603")]
    [InlineData("Installer failed with exit code: 5")]
    [InlineData("Installer failed with exit code: 740")]
    [InlineData("Installer failed with exit code: 0x80070005")]
    [InlineData("Installer failed with exit code: 2147942405")]
    [InlineData("Installer failed with exit code: -2147024891")]
    [InlineData("Сбой установщика с кодом выхода: 1603")]
    [InlineData("This command requires administrator privileges to run.")]
    [InlineData("Для этой команды требуются права администратора")]
    [InlineData("Запустите от имени администратора")]
    [InlineData("Access is denied. (0x80070005)")]
    [InlineData("Отказано в доступе")]
    public void NeedsAdmin_RecognizesRightsProblems(string output) => Assert.True(WingetFailure.NeedsAdmin(Fail(unchecked((int)0x8A150049), output)));

    [Fact]
    public void NeedsAdmin_ByWingetExitCode0x80070005_AndRealStrings()
    {
        Assert.True(WingetFailure.NeedsAdmin(Fail(unchecked((int)0x80070005), "")));
        Assert.True(WingetFailure.NeedsAdmin(Fail(unchecked((int)0x8A150049), "Found Steam [Valve.Steam]\nInstaller failed with exit code: 1603")));
    }

    [Theory]
    [InlineData("Installer failed with exit code: 1618")]       // другая установка идёт
    [InlineData("Installer failed with exit code: 3010")]
    [InlineData("Installer failed with exit code: 1")]
    [InlineData("No applicable upgrade found")]
    [InlineData("")]
    public void NeedsAdmin_OtherFailures_NotRecognized(string output) => Assert.False(WingetFailure.NeedsAdmin(Fail(unchecked((int)0x8A150049), output)));

    [Fact]
    public void NeedsAdmin_NotForSuccessTimeoutCancelNoUpdate()
    {
        Assert.False(WingetFailure.NeedsAdmin(new WingetRun(0, "access denied", false, false)));
        Assert.False(WingetFailure.NeedsAdmin(new WingetRun(-1, "access denied", true, false)));
        Assert.False(WingetFailure.NeedsAdmin(new WingetRun(-1, "access denied", false, true)));
        Assert.False(WingetFailure.NeedsAdmin(new WingetRun(-1, "", false, false, "не удалось запустить winget")));
        Assert.False(WingetFailure.NeedsAdmin(Fail(WingetRunner.NoApplicableUpdate, "Access is denied")));
    }

    [Fact]
    public void InstallerExitCode_Parsing()
    {
        Assert.Equal(1603, WingetFailure.InstallerExitCode("Installer failed with exit code: 1603"));
        Assert.Equal(0x80070005, WingetFailure.InstallerExitCode("exit code: 0x80070005"));
        Assert.Equal(1603, WingetFailure.InstallerExitCode("Сбой установщика с кодом выхода: 1603"));
        Assert.Null(WingetFailure.InstallerExitCode("ничего")); Assert.Null(WingetFailure.InstallerExitCode(null));
    }

    [Fact]
    public void CommandRequiresAdmin_0x8A150019_NeedsAdmin_SuggestsRetryAsAdmin()
    {
        var r = new WingetRun(unchecked((int)0x8A150019), "", false, false);
        Assert.True(WingetFailure.NeedsAdmin(r));
        Assert.Equal(WingetFix.RetryAsAdmin, WingetFailure.Suggest(r));
    }

    [Fact]
    public void Suggest_ReinstallBeatsAdmin_OtherwiseNone()
    {
        Assert.Equal(WingetFix.Reinstall, WingetFailure.Suggest(Fail(WingetFailure.InstallTechnologyDiffers, "access denied")));
        Assert.Equal(WingetFix.RetryAsAdmin, WingetFailure.Suggest(Fail(unchecked((int)0x8A150049), "exit code: 1603")));
        Assert.Equal(WingetFix.None, WingetFailure.Suggest(Fail(unchecked((int)0x8A150049), "exit code: 1618")));
        Assert.Equal(unchecked((int)0x8A15008E), WingetFailure.InstallTechnologyDiffers);
        Assert.False(WingetFailure.NeedsReinstall(new WingetRun(WingetFailure.InstallTechnologyDiffers, "", true, false)));
    }

    [Fact]
    public void UninstallCommand_Fixed()
    {
        var c = Winget.UninstallCommand(@"C:\w\winget.exe", "Valve.Steam", new Version(1, 29));
        Assert.Equal(new[] { "uninstall", "--id", "Valve.Steam", "--exact", "--silent", "--accept-source-agreements", "--disable-interactivity" }, c.Args);
        Assert.DoesNotContain("--disable-interactivity", Winget.UninstallCommand("w", "x", new Version(1, 2)).Args);
    }
}
