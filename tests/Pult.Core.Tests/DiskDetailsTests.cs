using System.Text.RegularExpressions;
using Pult.Admin;
using Pult.Core.Admin;
using Pult.Core.Maintenance;
namespace Pult.Core.Tests;

/// B1: DiskDetails — только читающее действие помощника без аргументов; ответ — проверенные числа.
public class DiskDetailsTests : IDisposable
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-dd-" + Guid.NewGuid().ToString("N"));
    public DiskDetailsTests() { Directory.CreateDirectory(Path.Combine(_d, "u")); Directory.CreateDirectory(Path.Combine(_d, "r")); }
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    // ---- политика ----

    [Fact]
    public void Policy_NoArgument_Allowed() => Assert.True(AdminPolicy.Decide(new AdminRequest(Nonce, AdminAction.DiskDetails, "")).Allowed);

    [Theory]
    [InlineData(" ")] [InlineData("0")] [InlineData("x")] [InlineData("C:\\")] [InlineData("Get-Disk")] [InlineData("norp:")] [InlineData("\0")] [InlineData("\n")]
    [InlineData("HklmRun|off|X")] [InlineData("PhysicalDisk0")]
    public void Policy_AnyArgument_Refused(string arg)
    {
        var d = AdminPolicy.Decide(new AdminRequest(Nonce, AdminAction.DiskDetails, arg));
        Assert.False(d.Allowed);
        Assert.Contains("не бывает аргументов", d.Reason);
    }

    [Fact]
    public void Policy_VeryLongArgument_Refused() =>
        Assert.False(AdminPolicy.Decide(new AdminRequest(Nonce, AdminAction.DiskDetails, new string('a', 100_000))).Allowed);

    [Fact]
    public void Action_IsShort_NotMaintenance_DefaultsToItsOwnWait() =>
        Assert.Equal(AdminMaintenance.DiskDetailsWait, AdminMaintenance.WaitTimeout(AdminAction.DiskDetails, ""));

    [Fact]
    public async Task ShortAction_FailsFast_WhileHelperHeldByLongMaintenance()
    {
        var client = new AdminClient(Path.Combine(_d, "c"), Path.Combine(_d, "cr"), runTask: () => true, isInstalled: () => true) { BusyWait = TimeSpan.FromMilliseconds(300) };
        var long1 = client.SendAsync(AdminAction.CleanSystem, "Wer", TimeSpan.FromSeconds(3), default);
        await Task.Delay(300);
        var r = await client.SendAsync(AdminAction.DiskDetails, "", TimeSpan.FromSeconds(10), default);
        Assert.False(r.Ok); Assert.Equal(AdminCodes.Busy, r.Code);
        await long1;
    }

    // ---- процессор помощника ----

    private sealed class Maint(MaintenanceResult? res, bool throws = false) : IMaintenanceExecutor
    {
        public int Calls;
        public MaintenanceResult InstallDrivers(IReadOnlyList<string> ids, bool skipRestore) => throw new InvalidOperationException("не должно вызываться");
        public MaintenanceResult CreateRestorePoint() => throw new InvalidOperationException("не должно вызываться");
        public MaintenanceResult CleanSystem(string id) => throw new InvalidOperationException("не должно вызываться");
        public MaintenanceResult ReadDiskDetails() { Calls++; if (throws) throw new InvalidOperationException("сбой"); return res!; }
    }

    private AdminResponse? Process(AdminAction a, string arg, IMaintenanceExecutor? m)
    {
        var req = Path.Combine(_d, "u", "admin.req");
        File.WriteAllText(req, AdminRequest.New(a, arg).ToJson());
        return new RequestProcessor(req, Path.Combine(_d, "r"), null, m).ProcessOnce();
    }

    [Fact]
    public void Processor_ReadsOnlyWithoutArgument()
    {
        var rd = new Maint(new(true, "D|0|1|2|3|4|5\n", DiskDetailsProtocol.Code));
        var ok = Process(AdminAction.DiskDetails, "", rd)!;
        Assert.True(ok.Ok); Assert.Equal(DiskDetailsProtocol.Code, ok.Code); Assert.Equal(1, rd.Calls);
        var bad = Process(AdminAction.DiskDetails, "0", rd)!;
        Assert.False(bad.Ok); Assert.Equal(1, rd.Calls);                                // с аргументом до чтения не доходит
    }

    [Fact]
    public void Processor_WithoutExecutor_FailsClean() => Assert.False(Process(AdminAction.DiskDetails, "", null)!.Ok);

    [Fact]
    public void Processor_ExecutorThrows_BecomesFailureResponse() => Assert.False(Process(AdminAction.DiskDetails, "", new Maint(null, throws: true))!.Ok);

    // ---- У1: читает SYSTEM-работник, а не процесс пользователя ----

    private sealed class Channel : IJobChannel
    {
        public List<(AdminJobKind Kind, string Arg, TimeSpan T)> Calls = new();
        public JobResult Run(string nonce, AdminJobKind kind, string arg, TimeSpan timeout) { Calls.Add((kind, arg, timeout)); return new(nonce, true, "D|0|1|2|3|4|5\n", DiskDetailsProtocol.Code); }
    }

    [Fact]
    public void Brokered_DiskDetails_GoesToSystemChannel_EmptyArg_NoConfirm()
    {
        var ch = new Channel(); var asked = 0;
        var r = new BrokeredMaintenance(ch, _ => { asked++; return ConfirmAnswer.No; }).ReadDiskDetails();
        var c = Assert.Single(ch.Calls);
        Assert.Equal(AdminJobKind.DiskDetails, c.Kind); Assert.Equal("", c.Arg); Assert.Equal(AdminMaintenance.DiskDetailsStep, c.T);
        Assert.True(r.Ok); Assert.Equal(DiskDetailsProtocol.Code, r.Code); Assert.Equal(0, asked);
    }

    [Fact]
    public void Job_DiskDetails_ToRequest_IsDiskDetails_ArgRefusedByPolicy()
    {
        Assert.Equal(AdminAction.DiskDetails, new AdminJob(Nonce, AdminJobKind.DiskDetails, "").ToRequest().Action);
        Assert.True(AdminPolicy.Decide(new AdminJob(Nonce, AdminJobKind.DiskDetails, "").ToRequest()).Allowed);
        Assert.False(AdminPolicy.Decide(new AdminJob(Nonce, AdminJobKind.DiskDetails, "x").ToRequest()).Allowed);
    }

    private sealed class Classes(params string[] keys) : IUserClassesRegistry
    {
        public bool KeyExists(string p) => keys.Contains(p, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"CLSID\{8BC3F05E-D86B-11D0-A075-00C04FB68820}")]
    [InlineData(@"Wow6432Node\CLSID\{4590F811-1D3A-11D0-891F-00AA004B2E24}")]
    public void SystemWorker_UserComRegistration_Refuses_ScriptNeverRuns(string key)
    {
        var rn = new Runner(new(0, "D|0|1|2|3|4|5\nEND\n", false));
        var exec = new SystemJobExecutor(new SystemMaintenance(_ => true, new Classes(key), rn));
        var r = exec.Execute(Nonce, AdminJobKind.DiskDetails, "");
        Assert.False(r.Ok); Assert.Equal(AdminCodes.ComRegistration, r.Code); Assert.Equal(0, rn.Calls);
    }

    [Fact]
    public void SystemWorker_Clean_RunsConstantScriptExactlyOnce_AndRefusesArgument()
    {
        var rn = new Runner(new(0, "D|0|12|35|0|1|4200\nEND\n", false));
        var exec = new SystemJobExecutor(new SystemMaintenance(_ => true, new Classes(), rn));
        var r = exec.Execute(Nonce, AdminJobKind.DiskDetails, "");
        Assert.True(r.Ok); Assert.Equal(DiskDetailsProtocol.Code, r.Code); Assert.Equal(1, rn.Calls);
        Assert.Same(DiskDetailsScript.Text, rn.Script);
        Assert.False(exec.Execute(Nonce, AdminJobKind.DiskDetails, "x").Ok); Assert.Equal(1, rn.Calls);
    }

    [Fact]
    public void Budgets_ProcessLessThanStep_StepLessThanWait() =>
        Assert.True(AdminMaintenance.DiskDetailsProcessTimeout < AdminMaintenance.DiskDetailsStep && AdminMaintenance.DiskDetailsStep < AdminMaintenance.DiskDetailsWait);

    // ---- запуск скрипта (фейковый PowerShell) ----

    private sealed class Runner(ProcessOutcome o) : IScriptRunner
    {
        public string? Script; public TimeSpan Timeout; public int Calls;
        public ProcessOutcome Run(string script, TimeSpan timeout) { Calls++; Script = script; Timeout = timeout; return o; }
    }

    [Fact]
    public void Run_UsesFixedScript_AndFixedTimeout_AndReturnsOnlyValidatedNumbers()
    {
        var rn = new Runner(new(0, "мусор\nD|0|12|35|0|1|4200\nD|1|-|-|-|-|-\nD|2|1|2|3|4|5;calc.exe\nERR|123\nEND\n", false));
        var r = DiskDetailsRun.Run(rn);
        Assert.True(r.Ok); Assert.Equal(DiskDetailsProtocol.Code, r.Code);
        Assert.Equal("D|0|12|35|0|1|4200\nD|1|-|-|-|-|-\n", r.Message);               // строка с «;calc.exe» не прошла
        Assert.Same(DiskDetailsScript.Text, rn.Script);
        Assert.Equal(AdminMaintenance.DiskDetailsProcessTimeout, rn.Timeout);
    }

    [Fact]
    public void Run_TimeoutOrNoData_IsFailureWithPlainText()
    {
        Assert.False(DiskDetailsRun.Run(new Runner(new(-1, "D|0|1|1|1|1|1", true))).Ok);
        var none = DiskDetailsRun.Run(new Runner(new(0, "ERR|-2147217405\n", false)));
        Assert.False(none.Ok); Assert.DoesNotContain("2147217405", none.Message);        // текст ошибки Windows наружу не идёт
    }

    [Fact]
    public void Script_IsReadOnlyConstant_NoWritingOrExecutingCommands()
    {
        var s = DiskDetailsScript.Text;
        Assert.Contains("Get-CimInstance", s); Assert.Contains("MSFT_StorageReliabilityCounter", s);
        // прямой перебор класса Windows отдаёт пустым (проверено 02.10 на Samsung NVMe) — только через связь с MSFT_PhysicalDisk
        Assert.Contains("Get-CimAssociatedInstance", s); Assert.Contains("MSFT_PhysicalDisk", s);
        Assert.DoesNotMatch(new Regex(@"\b(Set|Remove|New|Invoke|Start|Stop|Add|Clear|Restart|Enable|Disable|Format|Initialize|Repair|Reset|Out|Export|Register|Unregister|Install|Update)-", RegexOptions.IgnoreCase), s);
        Assert.DoesNotContain("iex", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{", s);                                                // нет подстановок значений
        Assert.DoesNotContain("$args", s); Assert.DoesNotContain("$input", s);
    }

    // ---- разбор на стороне Пульта ----

    [Fact]
    public void Parse_ValidLines()
    {
        var l = DiskDetailsProtocol.Parse("D|0|6|38|0|2|1240\r\nD|1|-|41|-|-|29800\n");
        Assert.Equal(2, l.Count);
        Assert.Equal(new DiskCounters(0, 6, 38, 0, 2, 1240), l[0]);
        Assert.Equal(new DiskCounters(1, null, 41, null, null, 29800), l[1]);
    }

    [Theory]
    [InlineData("D|0|101|38|0|0|1", null, 38)]                   // износ > 100 — нет данных
    [InlineData("D|0|50|0|0|0|1", 50, null)]                      // температура 0 — нет данных
    [InlineData("D|0|50|151|0|0|1", 50, null)]
    [InlineData("D|0|50|150|0|0|1", 50, 150)]
    public void Parse_OutOfRange_BecomesNoData(string line, int? wear, int? temp)
    {
        var c = Assert.Single(DiskDetailsProtocol.Parse(line));
        Assert.Equal(wear, c.Wear); Assert.Equal(temp, c.TemperatureC);
    }

    [Fact]
    public void Parse_ErrorsAndHoursLimits()
    {
        var c = Assert.Single(DiskDetailsProtocol.Parse("D|3|1|1|4294967295|4294967296|1000001"));
        Assert.Equal(4294967295, c.ReadErrors); Assert.Null(c.WriteErrors); Assert.Null(c.PowerOnHours);
        var d = Assert.Single(DiskDetailsProtocol.Parse("D|3|1|1|0|0|1000000"));
        Assert.Equal(1_000_000, d.PowerOnHours);
    }

    [Theory]
    [InlineData("D|0|1|2|3|4")] [InlineData("D|0|1|2|3|4|5|6")] [InlineData("D|-1|1|2|3|4|5")] [InlineData("D|x|1|2|3|4|5")] [InlineData("D||1|2|3|4|5")]
    [InlineData("D|0|+1|2|3|4|5")] [InlineData("D|0|1e1|2|3|4|5")] [InlineData("D|0|0x10|2|3|4|5")] [InlineData("D|0| 1|2|3|4|5")] [InlineData("D|0|1.5|2|3|4|5")]
    [InlineData("D|10000|1|2|3|4|5")] [InlineData("D|12345678901|1|2|3|4|5")] [InlineData("D|٣|1|2|3|4|5")] [InlineData("d|0|1|2|3|4|5")] [InlineData(" X D|0|1|2|3|4|5")]
    [InlineData("D|0|1|2|3|4|5;calc")] [InlineData("D|0|1|2|3|4|5\0")] [InlineData("END")] [InlineData("ERR|5")] [InlineData("")]
    public void Parse_BadLines_Dropped(string line) => Assert.Empty(DiskDetailsProtocol.Parse(line));

    [Fact]
    public void Parse_Limits_Duplicates_LongOutput()
    {
        var many = string.Concat(Enumerable.Range(0, 100).Select(i => $"D|{i}|1|1|1|1|1\n"));
        Assert.True(many.Length <= DiskDetailsProtocol.MaxTotalLength);
        Assert.Equal(DiskDetailsProtocol.MaxDisks, DiskDetailsProtocol.Parse(many).Count);
        var dup = DiskDetailsProtocol.Parse("D|0|1|10|0|0|1\nD|0|99|99|9|9|9\n");
        Assert.Equal(1, Assert.Single(dup).Wear);                                       // первая строка
        Assert.Empty(DiskDetailsProtocol.Parse(new string('x', 9000)));
        Assert.Empty(DiskDetailsProtocol.Parse("D|0|1|1|1|1|1\n" + new string(' ', DiskDetailsProtocol.MaxTotalLength)));
        Assert.Empty(DiskDetailsProtocol.Parse("D|0|" + new string('1', 200) + "|1|1|1|1"));
        Assert.Empty(DiskDetailsProtocol.Parse(null));
    }

    [Fact]
    public void Format_RoundTrips_AndNeverCarriesAnythingElse()
    {
        var src = new[] { new DiskCounters(0, 6, 38, 0, 2, 1240), new DiskCounters(7, null, null, null, null, null) };
        var text = DiskDetailsProtocol.Format(src);
        Assert.Matches(new Regex(@"^(D\|[0-9]+(\|([0-9]+|-)){5}\n)+\z"), text);
        Assert.Equal(src, DiskDetailsProtocol.Parse(text));
    }

    [Fact]
    public void Merge_ByDeviceId_KeepsOldValuesAndIgnoresForeign()
    {
        var disks = new[]
        {
            new DiskInfo("A", "SSD", 1, 0, new[] { 2 }, null, null, null, null, "0"),
            new DiskInfo("B", "HDD", 1, 0, new[] { 2 }, 5, 40, 0, 100, "1"),
            new DiskInfo("C", "SSD", 1, 0, new[] { 2 }, null, null, null, null, ""),
        };
        var m = DiskDetailsProtocol.Merge(disks, new[] { new DiskCounters(0, 9, 33, 0, 1, 500), new DiskCounters(1, null, null, null, null, null), new DiskCounters(9, 1, 1, 1, 1, 1) });
        Assert.Equal((9, 33, 500L, 1L), (m[0].Wear, m[0].TemperatureC, m[0].PowerOnHours, m[0].WriteErrors));
        Assert.Equal((5, 40, 100L), (m[1].Wear, m[1].TemperatureC, m[1].PowerOnHours));   // пустые счётчики не затирают
        Assert.Null(m[2].Wear);
    }

    [Fact]
    public void Volumes_Described_UsedAndFree()
    {
        var l = DiskVolumes.Describe(new[] { new VolumeUsage("C", 1_000_000_000_000, 400_000_000_000), new VolumeUsage("D", 0, 0) });
        var line = Assert.Single(l);
        Assert.StartsWith("C: занято ", line); Assert.Contains("свободно", line);
        Assert.Empty(DiskVolumes.Describe(null));
    }
}
