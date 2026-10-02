using System.Diagnostics;
using Pult.Admin;
using Pult.Core.Admin;
using Pult.Core.Maintenance;
namespace Pult.Core.Tests;

public partial class AdminMaintenanceTests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-mnt-" + Guid.NewGuid().ToString("N"));
    public AdminMaintenanceTests() { Directory.CreateDirectory(_d); }
    public void Dispose()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_d, "*", SearchOption.AllDirectories).ToList())   // junction убираем сами, чтобы не задеть цель
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) { try { Directory.Delete(dir); } catch { } }
            Directory.Delete(_d, true);
        }
        catch { }
    }

    private const string G1 = "0a1b2c3d-4e5f-6789-abcd-ef0123456789", G2 = "11111111-2222-3333-4444-555555555555";
    private static readonly IPathTrust Trust = new FakeTrust();
    private static Decision Decide(AdminAction a, string arg) => AdminPolicy.Decide(AdminRequest.New(a, arg));

    // ---- политика ----

    [Theory]
    [InlineData(G1)]
    [InlineData(G1 + "_12")]
    [InlineData(G1 + "," + G2 + "_3")]
    [InlineData("0A1B2C3D-4E5F-6789-ABCD-EF0123456789")]
    public void DriverInstall_ValidIds_Allowed(string arg) => Assert.True(Decide(AdminAction.DriverInstall, arg).Allowed);

    [Fact]
    public void DriverInstall_Exactly50Allowed_51Denied()
    {
        string Ids(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"{i:x8}-2222-3333-4444-555555555555"));
        Assert.True(Decide(AdminAction.DriverInstall, Ids(50)).Allowed);
        Assert.False(Decide(AdminAction.DriverInstall, Ids(51)).Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(G1 + "\n")]                                   // «$» в .NET пропустил бы перевод строки
    [InlineData(G1 + "_")]
    [InlineData(G1 + "_1x")]
    [InlineData(G1 + "_١٢")]                                  // арабо-индийские цифры: \d их принимает
    [InlineData(G1 + "_1234567890")]
    [InlineData(" " + G1)]
    [InlineData(G1 + " ," + G2)]
    [InlineData(G1 + ",")]
    [InlineData("," + G1)]
    [InlineData(G1 + ",," + G2)]
    [InlineData(G1 + "," + G1)]                               // повтор
    [InlineData(G1 + ";calc")]
    [InlineData(G1 + "&calc")]
    [InlineData(G1 + "'; calc; '")]                           // попытка выйти из кавычек скрипта
    [InlineData(@"..\..\x")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("{" + G1 + "}")]
    [InlineData("ffffffff-ffff-ffff-ffff-fffffffffffg")]
    public void DriverInstall_BadArg_Denied(string arg) => Assert.False(Decide(AdminAction.DriverInstall, arg).Allowed);

    [Fact]
    public void RestorePoint_NoArgs_Allowed_WithArgs_Denied()
    {
        Assert.True(Decide(AdminAction.RestorePoint, "").Allowed);
        Assert.False(Decide(AdminAction.RestorePoint, "x").Allowed);
        Assert.False(Decide(AdminAction.RestorePoint, " ").Allowed);
    }

    [Theory]
    [InlineData("WindowsTemp")] [InlineData("WuDownload")] [InlineData("Wer")] [InlineData("ComponentCleanup")]
    public void CleanSystem_FixedIds_Allowed(string id) => Assert.True(Decide(AdminAction.CleanSystem, id).Allowed);

    [Theory]
    [InlineData("")] [InlineData("windowstemp")] [InlineData("WindowsTemp ")] [InlineData("WindowsTemp,Wer")] [InlineData("Windows.old")]
    [InlineData(@"C:\Windows")] [InlineData(@"..\Temp")] [InlineData("Temp")] [InlineData("WindowsTemp\n")]
    public void CleanSystem_UnknownOrExtra_Denied(string arg) => Assert.False(Decide(AdminAction.CleanSystem, arg).Allowed);

    [Fact]
    public void UnknownActionNumber_Denied() => Assert.False(Decide((AdminAction)99, "").Allowed);

    [Fact]
    public void Request_UnknownActionName_NotParsed() =>
        Assert.Null(AdminRequest.Parse("""{"Nonce":"0123456789abcdef0123456789abcdef","Action":"FormatDisk","Arg":""}"""));

    [Fact]
    public void WaitTimeouts_PerAction()
    {
        Assert.Equal(TimeSpan.FromMinutes(50), AdminMaintenance.WaitTimeout(AdminAction.DriverInstall, G1));
        Assert.Equal(TimeSpan.FromMinutes(72), AdminMaintenance.WaitTimeout(AdminAction.CleanSystem, "ComponentCleanup"));
        Assert.True(AdminMaintenance.WaitTimeout(AdminAction.CleanSystem, "Wer") < TimeSpan.FromMinutes(72));
        Assert.Equal(TimeSpan.FromMinutes(10), AdminMaintenance.WaitTimeout(AdminAction.RestorePoint, ""));
        Assert.Equal(AdminMaintenance.DefaultWait, AdminMaintenance.WaitTimeout((AdminAction)99, ""));
    }

    [Fact]
    public void TaskTimeLimit_CoversLongestAction()
    {
        var x = System.Xml.Linq.XDocument.Parse(TaskXml.Build(@"PC\U", @"C:\P\PultAdmin.exe"));
        var limit = System.Xml.XmlConvert.ToTimeSpan(x.Descendants().Single(e => e.Name.LocalName == "ExecutionTimeLimit").Value);
        Assert.True(limit >= AdminMaintenance.ComponentCleanupWait);          // помощник пользователя живёт всё ожидание: окно + DISM
    }

    [Fact]
    public void CleanCode_RoundTrip()
    {
        Assert.True(AdminMaintenance.TryParseCleanCode(AdminMaintenance.CleanCode(123456789012, 7), out var b, out var s));
        Assert.Equal((123456789012, 7), (b, s));
        Assert.False(AdminMaintenance.TryParseCleanCode("freed=x;skipped=1", out _, out _));
    }

    // ---- клиент: прогресс ----

    [Fact]
    public async Task Client_ReportsProgressText_WhileWaiting()
    {
        var root = Path.Combine(_d, "cl"); var user = Path.Combine(root, "u"); var res = Path.Combine(root, "r");
        Directory.CreateDirectory(user); Directory.CreateDirectory(res);
        var client = new AdminClient(user, res, runTask: () => true, isInstalled: () => true);
        var seen = new List<string>();
        var prog = new Progress<string>(seen.Add);
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromMilliseconds(1300), default, "Создаю точку…", prog);
        await Task.Delay(100);
        Assert.False(r.Ok);
        Assert.Contains(seen, t => t.StartsWith("Создаю точку… (0:0"));
    }

    // ---- пересечение id и установка драйверов (фейки) ----

    private sealed class FakeSearch(params string[] ids) : IWindowsUpdateSearch
    {
        public Task<IReadOnlyList<DriverUpdate>> SearchDriversAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DriverUpdate>>(ids.Select(i => new DriverUpdate(i, "t", null, null, null, null)).ToList());
    }
    private sealed class FakeBackend(DriverInstallReport rep, List<string> log) : IDriverInstallBackend
    {
        public List<string>? Got;
        public DriverInstallReport Install(IReadOnlyList<string> ids) { Got = ids.ToList(); log.Add("install"); return rep; }
    }
    private sealed class FakeRestore(RestoreOutcome o, List<string> log, string detail = "") : IRestoreBackend
    {
        public RestoreOutcome Create(out string d) { d = detail; log.Add("restore"); return o; }
    }

    [Fact]
    public void Intersect_OnlyIdsFoundBySearch_ReturnsSearchIds()
    {
        var found = new DriverUpdate[] { new(G1, "a", null, null, null, null), new(G2 + "_5", "b", null, null, null, null) };
        var got = DriverInstaller.Intersect(new[] { G1, G2, G2 + "_5", "99999999-9999-9999-9999-999999999999" }, found);
        Assert.Equal(new[] { G1, G2 + "_5" }, got);                          // G2 без ревизии — другое обновление, в поиске его нет
    }

    [Fact]
    public void Intersect_IgnoresMalformedIdsFromSearch()
    {
        var got = DriverInstaller.Intersect(new[] { "evil'id" }, new[] { new DriverUpdate("evil'id", "x", null, null, null, null) });
        Assert.Empty(got);
    }

    private static (DriverInstaller inst, FakeBackend be, List<string> log) Make(string[] found, RestoreOutcome ro, DriverInstallReport? rep = null, string detail = "")
    {
        var log = new List<string>();
        var be = new FakeBackend(rep ?? new(2, 0, 0, false, null), log);
        return (new DriverInstaller(new FakeSearch(found), new RestorePointService(new FakeRestore(ro, log, detail)), be, _ => true), be, log);
    }

    [Fact]
    public void Install_OnlyIntersection_RestoreBeforeInstall()
    {
        var (inst, be, log) = Make(new[] { G1, G2 }, RestoreOutcome.Created, new(2, 0, 0, true, null));
        var r = inst.Run(new[] { G1, "99999999-9999-9999-9999-999999999999" });
        Assert.Equal(new[] { G1 }, be.Got);
        Assert.Equal(new[] { "restore", "install" }, log);
        Assert.True(r.Ok);
        Assert.Equal(AdminCodes.RebootRequired, r.Code);
        Assert.Contains("установлено 2", r.Message);
        Assert.Contains("(проигнорировано) 1", r.Message);
        Assert.EndsWith(RestorePointService.CreatedMessage, r.Message);             // итог говорит, что стало с точкой
    }

    [Fact]
    public void Install_ResultMentionsRestorePointOutcome()
    {
        var (recent, _, _) = Make(new[] { G1 }, RestoreOutcome.TooRecent);
        Assert.Contains(RestorePointService.RecentMessage, recent.Run(new[] { G1 }).Message);
        var (skip, _, _) = Make(new[] { G1 }, RestoreOutcome.Created);
        Assert.Contains(DriverInstaller.SkippedRestoreMessage, skip.Run(new[] { G1 }, skipRestore: true).Message);
    }

    [Fact]
    public void Install_SkipRestore_NoRestoreCall_InstallStillRuns()
    {
        var (inst, be, log) = Make(new[] { G1 }, RestoreOutcome.Created);
        Assert.True(inst.Run(new[] { G1 }, skipRestore: true).Ok);
        Assert.Equal(new[] { "install" }, log); Assert.Equal(new[] { G1 }, be.Got);
        var (inst2, _, log2) = Make(new[] { G1 }, RestoreOutcome.Created);
        Assert.True(inst2.Run(new[] { G1 }, skipRestore: false).Ok);
        Assert.Equal(new[] { "restore", "install" }, log2);
    }

    [Fact]
    public void ConfirmText_StatesWhetherRestorePointWillBeMade_AndNoAutoReboot()
    {
        var with = DriverInstaller.ConfirmText(new[] { "a" }, false);
        var without = DriverInstaller.ConfirmText(new[] { "a" }, true);
        Assert.Contains("Перед установкой будет создана точка восстановления.", with);
        Assert.DoesNotContain("НЕ будет создана", with);
        Assert.Contains("Точка восстановления НЕ будет создана (ты так выбрал).", without);
        Assert.DoesNotContain("будет создана точка", without);
        foreach (var t in new[] { with, without })
        {
            Assert.Contains("Компьютер сам не перезагрузится", t);
            Assert.DoesNotContain("Может потребоваться перезагрузка", t);
        }
    }

    [Fact]
    public void Install_SkipRestore_ConfirmWindowSaysSo()
    {
        string? shown = null; var log = new List<string>();
        var inst = new DriverInstaller(new FakeSearch(G1), new RestorePointService(new FakeRestore(RestoreOutcome.Created, log)),
                                       new FakeBackend(new(1, 0, 0, false, null), log), t => { shown = t; return false; });
        inst.Run(new[] { G1 }, skipRestore: true);
        Assert.Contains("НЕ будет создана", shown); Assert.Empty(log);
    }

    [Theory]
    [InlineData("norp:0a1b2c3d-4e5f-6789-abcd-ef0123456789", true, 1)]
    [InlineData("norp:0a1b2c3d-4e5f-6789-abcd-ef0123456789,11111111-2222-3333-4444-555555555555", true, 2)]
    [InlineData("0a1b2c3d-4e5f-6789-abcd-ef0123456789", false, 1)]
    public void ParseDriverIds_Prefix_Accepted(string arg, bool skip, int count)
    {
        Assert.True(AdminMaintenance.TryParseDriverIds(arg, out var ids, out var s, out _));
        Assert.Equal(skip, s); Assert.Equal(count, ids.Count);
    }

    [Theory]
    [InlineData("norp:")]
    [InlineData("norp:norp:0a1b2c3d-4e5f-6789-abcd-ef0123456789")]
    [InlineData("NORP:0a1b2c3d-4e5f-6789-abcd-ef0123456789")]
    [InlineData("0a1b2c3d-4e5f-6789-abcd-ef0123456789,norp:11111111-2222-3333-4444-555555555555")]
    [InlineData("x:0a1b2c3d-4e5f-6789-abcd-ef0123456789")]
    [InlineData("norp: 0a1b2c3d-4e5f-6789-abcd-ef0123456789")]
    [InlineData("norp:0a1b2c3d-4e5f-6789-abcd-ef0123456789,0a1b2c3d-4e5f-6789-abcd-ef0123456789")]
    public void ParseDriverIds_BadPrefix_Refused(string arg)
    {
        Assert.False(AdminMaintenance.TryParseDriverIds(arg, out var ids, out var s, out var problem));
        Assert.Empty(ids); Assert.False(s); Assert.NotEmpty(problem);
    }

    [Fact]
    public void ParseDriverIds_LengthLimit_CountsPrefix()
    {
        var ok = string.Join(",", Enumerable.Range(0, 50).Select(i => $"0a1b2c3d-4e5f-6789-abcd-{i:D12}"));
        Assert.True(AdminMaintenance.TryParseDriverIds("norp:" + ok, out _, out var s, out _)); Assert.True(s);
        Assert.False(AdminMaintenance.TryParseDriverIds("norp:" + ok + ",0a1b2c3d-4e5f-6789-abcd-ef0123456789", out _, out _, out _));
        Assert.False(AdminMaintenance.TryParseDriverIds("norp:" + new string('a', AdminMaintenance.MaxDriverIds * 48 + 1), out _, out _, out _));
    }

    [Fact]
    public void Processor_NoRestorePrefix_ReachesExecutor()
    {
        var m = new FakeMaint();
        Assert.True(Handle(AdminRequest.New(AdminAction.DriverInstall, AdminMaintenance.DriverArg(new[] { G1 }, true)), m).Ok);
        Assert.False(Handle(AdminRequest.New(AdminAction.DriverInstall, "norp:norp:" + G1), m).Ok);
        Assert.Equal(new[] { $"drivers {G1} norp" }, m.Calls);
    }

    [Fact]
    public void Install_NothingInSearch_NoRestoreNoInstall()
    {
        var (inst, be, log) = Make(new[] { G2 }, RestoreOutcome.Created);
        var r = inst.Run(new[] { G1 });
        Assert.True(r.Ok);
        Assert.Empty(log); Assert.Null(be.Got);
    }

    [Fact]
    public void Install_RestoreRecent_Continues()
    {
        var (inst, be, _) = Make(new[] { G1 }, RestoreOutcome.TooRecent);
        Assert.True(inst.Run(new[] { G1 }).Ok);
        Assert.NotNull(be.Got);
    }

    [Fact]
    public void Install_RestoreDisabled_NoInstall_ClearMessage()
    {
        var (inst, be, _) = Make(new[] { G1 }, RestoreOutcome.Disabled);
        var r = inst.Run(new[] { G1 });
        Assert.False(r.Ok);
        Assert.Equal(AdminCodes.RestoreDisabled, r.Code);
        Assert.Contains("Защита системы выключена", r.Message);
        Assert.Null(be.Got);
    }

    [Fact]
    public void Install_AllFailed_NotOk()
    {
        var (inst, _, _) = Make(new[] { G1 }, RestoreOutcome.Created, new(0, 1, 0, false, null));
        Assert.False(inst.Run(new[] { G1 }).Ok);
    }

    [Fact]
    public void Install_SearchError_NoRestoreNoInstall()
    {
        var log = new List<string>();
        var be = new FakeBackend(new(1, 0, 0, false, null), log);
        var failing = new PsWuaSearch(new FakeRunner("ERR|-2145124318\n"));
        var r = new DriverInstaller(failing, new RestorePointService(new FakeRestore(RestoreOutcome.Created, log)), be, _ => true).Run(new[] { G1 });
        Assert.False(r.Ok);
        Assert.Empty(log);
    }

    // ---- точка восстановления: сообщения ----

    [Theory]
    [InlineData(RestoreOutcome.Disabled, false, "Защита системы выключена — точку восстановления создать нельзя", AdminCodes.RestoreDisabled)]
    [InlineData(RestoreOutcome.TooRecent, true, "Свежая точка восстановления уже есть", AdminCodes.RestoreRecent)]
    [InlineData(RestoreOutcome.Created, true, "Точка восстановления создана", "")]
    [InlineData(RestoreOutcome.Failed, false, "Не удалось создать точку восстановления", "")]
    public void RestorePoint_Messages(RestoreOutcome o, bool ok, string text, string code)
    {
        var r = new RestorePointService(new FakeRestore(o, new List<string>())).Run();
        Assert.Equal(ok, r.Ok); Assert.Contains(text, r.Message); Assert.Equal(code, r.Code);
    }

    [Fact]
    public void RestorePoint_BackendThrows_IsFailureNotException()
    {
        var r = new RestorePointService(new ThrowingRestore()).Run();
        Assert.False(r.Ok);
    }
    private sealed class ThrowingRestore : IRestoreBackend { public RestoreOutcome Create(out string d) => throw new InvalidOperationException("boom"); }

    // ---- скрипты: разбор вывода и подстановка ----

    private sealed class FakeRunner(string output, bool timedOut = false) : IScriptRunner
    {
        public string? LastScript;
        public ProcessOutcome Run(string script, TimeSpan t) { LastScript = script; return new(0, output, timedOut); }
    }

    [Fact]
    public async Task PsWuaSearch_ParsesRows_IdFormatMatchesCore()
    {
        var title = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Драйвер Тест"));
        var fake = new FakeRunner($"U|{G1.ToUpperInvariant()}|0|{title}\r\nU|{G2}|7|{title}\r\njunk\r\nEND\r\n");
        var list = await new PsWuaSearch(fake).SearchDriversAsync(default);
        Assert.Equal(new[] { G1, G2 + "_7" }, list.Select(u => u.UpdateId));     // ревизия 0 опускается — как в WuaSearch
        Assert.Equal("Драйвер Тест", list[0].Title);
        Assert.True(list.All(u => AdminMaintenance.IsValidUpdateId(u.UpdateId)));
    }

    private static string B(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));
    private const string G3 = "22222222-2222-3333-4444-555555555555";

    [Fact]
    public async Task PsWuaSearch_ParsesClassAndHardwareId_AndOldFormatStillWorks()
    {
        var fake = new FakeRunner($"U|{G1}|0|{B("BIOS")}|{B("Firmware")}|{B("uefi\\res_{abc}")}\nU|{G2}|1|{B("Audio")}|{B("OtherHardware")}|\nU|{G3}|0|{B("Old")}\nEND\n");
        var list = await new PsWuaSearch(fake).SearchDriversAsync(default);
        Assert.Equal(3, list.Count);
        Assert.Equal("Firmware", list[0].DriverClass);
        Assert.Equal("uefi\\res_{abc}", list[0].HardwareId);
        Assert.True(list[0].IsFirmware);
        Assert.Equal("OtherHardware", list[1].DriverClass);
        Assert.Null(list[1].HardwareId);
        Assert.False(list[1].IsFirmware);
        Assert.Null(list[2].DriverClass);
        Assert.False(list[2].IsFirmware);
    }

    [Fact]
    public void DriverUpdate_IsFirmware()
    {
        Assert.True(new DriverUpdate("a", "t", null, null, null, null, "firmware", null).IsFirmware);
        Assert.True(new DriverUpdate("a", "t", null, null, null, null, "OtherHardware", "UEFI\\RES_{x}").IsFirmware);
        Assert.False(new DriverUpdate("a", "t", null, null, null, null, "OtherHardware", "PCI\\VEN_8086").IsFirmware);
        Assert.False(new DriverUpdate("a", "t", null, null, null, null).IsFirmware);
    }

    [Fact]
    public async Task PsWuaSearch_NoEnd_OrErr_OrTimeout_IsEmptyWithError()
    {
        foreach (var f in new[] { new FakeRunner($"U|{G1}|0|QQ==\n"), new FakeRunner("ERR|-2145124318\n"), new FakeRunner("", true) })
        {
            var s = new PsWuaSearch(f);
            Assert.Empty(await s.SearchDriversAsync(default));
            Assert.NotNull(s.LastError);
        }
    }

    [Fact]
    public void ParseSearch_RebootBehavior_AndOldFormats()
    {
        var t = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("T"));
        var (rows, err) = MaintenanceScripts.ParseSearch($"U|{G1}|0|{t}|{t}|{t}|2\nU|{G1}|1|{t}|{t}|{t}|\nU|{G1}|2|{t}|{t}|{t}|9\nU|{G1}|3|{t}|{t}|{t}\nU|{G1}|4|{t}\nEND");
        Assert.Null(err);
        Assert.Equal(new int?[] { 2, null, null, null, null }, rows.Select(r => r.RebootBehavior).ToArray());
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void ParseInstall_And_ParseRestore()
    {
        Assert.Equal(new DriverInstallReport(3, 1, 2, true, null), MaintenanceScripts.ParseInstall("x\r\nR|3|1|2|1\r\n"));
        Assert.Null(MaintenanceScripts.ParseInstall("R|3|1|2|1").RebootTitles);
        var rt = MaintenanceScripts.ParseInstall("R|2|0|0|1|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Драйвер А")) + "," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("B")));
        Assert.True(rt.RebootRequired); Assert.Equal(new[] { "Драйвер А", "B" }, rt.RebootTitles);
        Assert.Empty(MaintenanceScripts.ParseInstall("R|2|0|0|0|").RebootTitles!);
        Assert.Equal("a, b, c и ещё 1", Drivers.TitleList(new[] { "a", "b", "c", "d" }));
        Assert.NotNull(MaintenanceScripts.ParseInstall("ERR|-2145124318").Error);
        Assert.NotNull(MaintenanceScripts.ParseInstall("").Error);
        Assert.Equal(RestoreOutcome.Created, MaintenanceScripts.ParseRestore("CREATED\r\n", out _));
        Assert.Equal(RestoreOutcome.TooRecent, MaintenanceScripts.ParseRestore("RECENT", out _));
        Assert.Equal(RestoreOutcome.Disabled, MaintenanceScripts.ParseRestore("DISABLED", out _));
        Assert.Equal(RestoreOutcome.Failed, MaintenanceScripts.ParseRestore("FAIL|1058", out var d)); Assert.Equal("1058", d);
        Assert.Equal(RestoreOutcome.Failed, MaintenanceScripts.ParseRestore("мусор", out _));
    }

    [Fact]
    public void InstallScript_OnlyValidatedIds_RejectsInjection()
    {
        var s = MaintenanceScripts.InstallDrivers(new[] { G1, G2 + "_4" });
        Assert.Contains($"'{G1}','{G2}_4'", s);
        // у драйверов WUA CanRequestUserInput почти всегда True — пропуск по нему отсекал всё (живая проверка 30.09); пропуск только без лицензии
        Assert.DoesNotContain("InstallationBehavior.CanRequestUserInput -or", s);
        Assert.Contains("if (-not $u.EulaAccepted) { $skipped++ }", s);
        Assert.Contains("AllowSourcePrompts = $false", s);
        Assert.Throws<ArgumentException>(() => MaintenanceScripts.InstallDrivers(new[] { "x'); calc; ('" }));
        Assert.Throws<ArgumentException>(() => MaintenanceScripts.InstallDrivers(new[] { G1 + "\n" }));
    }

    [Fact]
    public void PowerShell_FullSystemPath_CleanEnv_NoUserModulePath()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), PowerShellHost.ExePath);
        var env = PowerShellHost.Env();
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules"), env["PSModulePath"]);
        Assert.DoesNotContain("USERPROFILE", env.Keys);
        var args = PowerShellHost.Arguments(MaintenanceScripts.SearchDrivers);
        Assert.StartsWith("-NoProfile -NonInteractive", args);
        Assert.Contains("-EncodedCommand", args);
    }

    [Fact]
    public void Dism_FullPath_FixedArguments_Outcomes()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "Dism.exe"), ComponentCleanup.DismPath);
        Assert.Equal("/Online /Cleanup-Image /StartComponentCleanup", ComponentCleanup.Arguments);
        Assert.Equal(TimeSpan.FromMinutes(60), ComponentCleanup.Timeout);
        Assert.True(ComponentCleanup.Run(new FakeDism(new(0, "", false))).Ok);
        var reboot = ComponentCleanup.Run(new FakeDism(new(3010, "", false)));
        Assert.True(reboot.Ok); Assert.Equal(AdminCodes.RebootRequired, reboot.Code);
        Assert.False(ComponentCleanup.Run(new FakeDism(new(5, "", false))).Ok);
        Assert.False(ComponentCleanup.Run(new FakeDism(new(-1, "", true))).Ok);
    }
    private sealed class FakeDism(ProcessOutcome o) : IDismRunner { public ProcessOutcome StartComponentCleanup(TimeSpan t) => o; }

    // ---- RequestProcessor: обслуживание ----

    private sealed class FakeMaint : IMaintenanceExecutor
    {
        public List<string> Calls = new();
        public MaintenanceResult InstallDrivers(IReadOnlyList<string> ids, bool skipRestore) { Calls.Add("drivers " + string.Join(",", ids) + (skipRestore ? " norp" : "")); return new(true, "ok-d", AdminCodes.RebootRequired); }
        public MaintenanceResult CreateRestorePoint() { Calls.Add("rp"); return new(false, "off", AdminCodes.RestoreDisabled); }
        public MaintenanceResult CleanSystem(string id) { Calls.Add("clean " + id); return new(true, "done", AdminMaintenance.CleanCode(10, 1)); }
    }

    private AdminResponse Handle(AdminRequest req, FakeMaint m)
    {
        var user = Path.Combine(_d, "pu"); var res = Path.Combine(_d, "pr");
        Directory.CreateDirectory(user); Directory.CreateDirectory(res);
        var rq = Path.Combine(user, "admin.req");
        File.WriteAllText(rq, req.ToJson());
        return new RequestProcessor(rq, res, null, m).ProcessOnce()!;
    }

    [Fact]
    public void Processor_ValidRequests_ReachExecutor_WithCodes()
    {
        var m = new FakeMaint();
        var a = Handle(AdminRequest.New(AdminAction.DriverInstall, G1.ToUpperInvariant() + "," + G2), m);
        Assert.True(a.Ok); Assert.Equal(AdminCodes.RebootRequired, a.Code);
        var b = Handle(AdminRequest.New(AdminAction.RestorePoint, ""), m);
        Assert.False(b.Ok); Assert.Equal(AdminCodes.RestoreDisabled, b.Code);
        var c = Handle(AdminRequest.New(AdminAction.CleanSystem, "Wer"), m);
        Assert.True(c.Ok);
        Assert.Equal(new[] { $"drivers {G1},{G2}", "rp", "clean Wer" }, m.Calls);          // id приведены к нижнему регистру
    }

    [Fact]
    public void Processor_BadRequests_NeverReachExecutor()
    {
        var m = new FakeMaint();
        foreach (var r in new[]
        {
            AdminRequest.New(AdminAction.DriverInstall, "x"), AdminRequest.New(AdminAction.DriverInstall, G1 + ";calc"),
            AdminRequest.New(AdminAction.RestorePoint, "a"), AdminRequest.New(AdminAction.CleanSystem, @"C:\Windows\System32"),
            AdminRequest.New(AdminAction.CleanSystem, "Temp"),
        })
            Assert.False(Handle(r, m).Ok);
        Assert.Empty(m.Calls);
    }

    [Fact]
    public void Processor_WithoutMaintenanceExecutor_Refuses()
    {
        var user = Path.Combine(_d, "qu"); var res = Path.Combine(_d, "qr");
        Directory.CreateDirectory(user); Directory.CreateDirectory(res);
        var rq = Path.Combine(user, "admin.req");
        File.WriteAllText(rq, AdminRequest.New(AdminAction.CleanSystem, "Wer").ToJson());
        Assert.False(new RequestProcessor(rq, res).ProcessOnce()!.Ok);
    }

    // ---- очистка ----

    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private string Make(string name) { var p = Path.Combine(_d, name); Directory.CreateDirectory(p); return p; }
    private static string File_(string path, int bytes, DateTime? written = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (written != null) File.SetLastWriteTimeUtc(path, written.Value);
        return path;
    }

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!; p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void Clean_CountsBytes_RemovesFilesAndEmptyFolders_KeepsRoot()
    {
        var root = Make("c1");
        File_(Path.Combine(root, "a.tmp"), 1000);
        File_(Path.Combine(root, "sub", "deep", "b.tmp"), 2500);
        File_(Path.Combine(root, "sub", "c.tmp"), 500);
        Directory.CreateDirectory(Path.Combine(root, "emptydir"));
        var r = SystemCleaner.CleanContents(root, null, Now);
        Assert.Equal(4000, r.FreedBytes);
        Assert.Equal(0, r.Skipped);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void Clean_MissingRoot_NoError() => Assert.Equal(new CleanResult(0, 0, 0), SystemCleaner.CleanContents(Path.Combine(_d, "нет-такой"), null, Now));

    [Fact]
    public void Clean_JunctionPointingOutside_TargetUntouched()
    {
        var outside = Make("outside");
        var keep = File_(Path.Combine(outside, "precious.dat"), 777);
        File_(Path.Combine(outside, "nested", "x.dat"), 10);
        var root = Make("c2");
        File_(Path.Combine(root, "own.tmp"), 100);
        Junction(Path.Combine(root, "link"), outside);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Junction(Path.Combine(root, "sub", "link2"), outside);                          // junction глубже
        var r = SystemCleaner.CleanContents(root, null, Now);
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(Path.Combine(outside, "nested", "x.dat")));
        Assert.Equal(100, r.FreedBytes);                                               // чужие байты не посчитаны
        Assert.False(Directory.Exists(Path.Combine(root, "link")));                    // сама ссылка убрана
    }

    [Fact]
    public void Clean_RootIsJunction_Refused_TargetUntouched()
    {
        var outside = Make("outside2");
        var keep = File_(Path.Combine(outside, "precious.dat"), 50);
        var link = Path.Combine(_d, "rootlink");
        Junction(link, outside);
        var r = SystemCleaner.CleanContents(link, null, Now);
        Assert.True(File.Exists(keep));
        Assert.Equal(0, r.FreedBytes);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void Clean_RootPathThroughJunction_Refused()
    {
        var real = Make("real");
        var keep = File_(Path.Combine(real, "inner", "k.dat"), 50);
        var link = Path.Combine(_d, "viaLink");
        Junction(link, real);
        var r = SystemCleaner.CleanContents(Path.Combine(link, "inner"), null, Now);   // ссылка — в середине пути
        Assert.True(File.Exists(keep));
        Assert.Equal(0, r.FreedBytes);
    }

    [Fact]
    public void Clean_LockedFile_Skipped_OthersDeleted()
    {
        var root = Make("c3");
        var locked = File_(Path.Combine(root, "busy.tmp"), 300);
        File_(Path.Combine(root, "free.tmp"), 200);
        File_(Path.Combine(root, "d", "busy2.tmp"), 40);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(Path.Combine(root, "d", "busy2.tmp"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = SystemCleaner.CleanContents(root, null, Now);
            Assert.Equal(200, r.FreedBytes);
            Assert.Equal(2, r.Skipped);
            Assert.True(File.Exists(locked));
            Assert.True(Directory.Exists(Path.Combine(root, "d")));                     // папка с занятым файлом осталась
        }
    }

    [Fact]
    public void Clean_MinAge_OnlyOlderThanOneDay()
    {
        var root = Make("c4");
        var old = File_(Path.Combine(root, "old.tmp"), 100, Now - TimeSpan.FromDays(3));
        var fresh = File_(Path.Combine(root, "fresh.tmp"), 100, Now - TimeSpan.FromHours(2));
        File_(Path.Combine(root, "mixed", "old.tmp"), 10, Now - TimeSpan.FromDays(2));
        File_(Path.Combine(root, "mixed", "fresh.tmp"), 10, Now - TimeSpan.FromHours(1));
        File_(Path.Combine(root, "olddir", "o.tmp"), 5, Now - TimeSpan.FromDays(9));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "olddir"), Now - TimeSpan.FromDays(9));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "mixed"), Now - TimeSpan.FromDays(9));
        var r = SystemCleaner.CleanContents(root, SystemCleaner.TempMinAge, Now);
        Assert.False(File.Exists(old)); Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(Path.Combine(root, "mixed", "old.tmp"))); Assert.True(File.Exists(Path.Combine(root, "mixed", "fresh.tmp")));
        Assert.False(Directory.Exists(Path.Combine(root, "olddir")));                   // старая папка опустела и сама старая
        Assert.Equal(115, r.FreedBytes);
    }

    [Fact]
    public void Clean_FreshEmptyFolder_Kept_WithMinAge()
    {
        var root = Make("c5");
        Directory.CreateDirectory(Path.Combine(root, "newdir"));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "newdir"), Now - TimeSpan.FromMinutes(5));
        SystemCleaner.CleanContents(root, SystemCleaner.TempMinAge, Now);
        Assert.True(Directory.Exists(Path.Combine(root, "newdir")));
    }

    [Fact]
    public void Clean_FileSymlinkOrHardlink_OnlyNameRemoved()
    {
        var outside = Make("outside3");
        var keep = File_(Path.Combine(outside, "k.dat"), 64);
        var root = Make("c6");
        var hard = Path.Combine(root, "hard.dat");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /H \"{hard}\" \"{keep}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        SystemCleaner.CleanContents(root, null, Now);
        Assert.False(File.Exists(hard));
        Assert.True(File.Exists(keep));                                                 // второе имя файла не пострадало
    }

    [Fact]
    public void Plan_FixedPaths_BuiltFromSystemFolders()
    {
        var p = SystemCleaner.Plan("WindowsTemp", @"C:\Windows\System32", @"C:\ProgramData")!.Value;
        Assert.Equal(new[] { @"C:\Windows\Temp" }, p.Roots); Assert.Equal(TimeSpan.FromDays(1), p.MinAge);
        var w = SystemCleaner.Plan("WuDownload", @"C:\Windows\System32", @"C:\ProgramData")!.Value;
        Assert.Equal(new[] { @"C:\Windows\SoftwareDistribution\Download" }, w.Roots); Assert.Null(w.MinAge);
        var e = SystemCleaner.Plan("Wer", @"C:\Windows\System32", @"C:\ProgramData")!.Value;
        Assert.Equal(new[] { @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue" }, e.Roots);
        Assert.Null(SystemCleaner.Plan("ComponentCleanup", @"C:\Windows\System32", @"C:\ProgramData"));
        Assert.Null(SystemCleaner.Plan(@"C:\Users", @"C:\Windows\System32", @"C:\ProgramData"));
    }

    [Fact]
    public void Run_ReportsBytesAndSkipped_InCode_AndUnknownRefused()
    {
        var sys = Make("win"); var pd = Make("pd");
        var sysDir = Path.Combine(sys, "System32"); Directory.CreateDirectory(sysDir);
        File_(Path.Combine(pd, "Microsoft", "Windows", "WER", "ReportArchive", "r1", "Report.wer"), 2048);
        File_(Path.Combine(pd, "Microsoft", "Windows", "WER", "ReportQueue", "q.wer"), 1024);
        var r = SystemCleaner.Run("Wer", sysDir, pd, Now, new FakeDism(new(0, "", false)), _ => true);
        Assert.True(r.Ok);
        Assert.True(AdminMaintenance.TryParseCleanCode(r.Code, out var bytes, out var skipped));
        Assert.Equal((3072, 0), (bytes, skipped));
        Assert.Contains("Освобождено 3 КБ", r.Message);
        Assert.False(SystemCleaner.Run("Bogus", sysDir, pd, Now, new FakeDism(new(0, "", false)), _ => true).Ok);
    }

    [Fact]
    public void MayNeedReboot_UnknownCountsAsMaybe()
    {
        DriverUpdate D(int? b) => new("11111111-2222-3333-4444-555555555555", "t", null, null, null, null, RebootBehavior: b);
        Assert.False(D(0).MayNeedReboot);
        Assert.True(D(1).MayNeedReboot);
        Assert.True(D(2).MayNeedReboot);
        Assert.True(D(null).MayNeedReboot);   // не знаем — не обещаем «перезагрузка не понадобится»
    }
}
