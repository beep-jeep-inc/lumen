using System.Text;
using Pult.Admin;
using Pult.Core.Admin;
using Pult.Core.Maintenance;
namespace Pult.Core.Tests;

/// «Починка Windows» (DISM + sfc): разбор вывода (английская и русская Windows), итог, политика помощника, исполнитель. Реальных DISM/sfc не запускаем.
public class RepairTests
{
    private static byte[] Utf16(string s, bool bom = false) => (bom ? Encoding.Unicode.GetPreamble() : Array.Empty<byte>()).Concat(Encoding.Unicode.GetBytes(s)).ToArray();

    // ---- вывод sfc: UTF-16 ----

    [Fact]
    public void DecodeConsole_Utf16_WithAndWithoutBom_English()
    {
        const string t = "Beginning system scan.\r\r\nWindows Resource Protection did not find any integrity violations.\r\n";
        Assert.Contains("did not find any integrity violations", SystemRepair.DecodeConsole(Utf16(t, bom: true)));
        Assert.Contains("did not find any integrity violations", SystemRepair.DecodeConsole(Utf16(t)));
    }

    [Fact]
    public void DecodeConsole_Utf16_Russian_WithoutBom()
    {
        const string t = "Защита ресурсов Windows не обнаружила нарушений целостности.\r\n";
        var s = SystemRepair.DecodeConsole(Utf16(t));
        Assert.Contains("не обнаружила нарушений целостности", s);
        Assert.False(s.Contains('\0'));
    }

    [Fact]
    public void DecodeConsole_Utf8_Empty_Null_StrayNuls()
    {
        Assert.Contains("успешно", SystemRepair.DecodeConsole(Encoding.UTF8.GetBytes("всё успешно")));
        Assert.Equal("", SystemRepair.DecodeConsole(null));
        Assert.Equal("", SystemRepair.DecodeConsole(Array.Empty<byte>()));
        Assert.Equal("abc", SystemRepair.DecodeConsole(Utf16("abc\0")));
    }

    // ---- итоговая фраза sfc: три исхода × две локали ----

    [Theory]
    [InlineData("Windows Resource Protection did not find any integrity violations.", SfcVerdict.Clean)]
    [InlineData("Защита ресурсов Windows не обнаружила нарушений целостности.", SfcVerdict.Clean)]
    [InlineData("Windows Resource Protection found corrupt files and successfully repaired them.\r\nFor online repairs, details are included in the CBS log file", SfcVerdict.Repaired)]
    [InlineData("Защита ресурсов Windows обнаружила поврежденные файлы и успешно их восстановила.", SfcVerdict.Repaired)]
    [InlineData("Защита ресурсов Windows обнаружила повреждённые файлы и успешно их восстановила.", SfcVerdict.Repaired)]
    [InlineData("Windows Resource Protection found corrupt files but was unable to fix some of them.", SfcVerdict.Unfixable)]
    [InlineData("Защита ресурсов Windows обнаружила поврежденные файлы, но не может восстановить некоторые из них.", SfcVerdict.Unfixable)]
    [InlineData("Windows Resource Protection could not perform the requested operation.", SfcVerdict.CouldNotRun)]
    [InlineData("Защита ресурсов Windows не может выполнить запрошенную операцию.", SfcVerdict.CouldNotRun)]
    [InlineData("There is a system repair pending which requires reboot to complete.  Restart Windows and run sfc again.", SfcVerdict.RebootPending)]
    public void ParseConsole_RecognizesEveryOutcome(string text, SfcVerdict expected) => Assert.Equal(expected, SystemRepair.ParseConsole(text));

    [Fact]
    public void ParseConsole_ProgressNoiseOnly_IsNull()
    {
        Assert.Null(SystemRepair.ParseConsole("Начато сканирование системы.  Этот процесс может занять некоторое время.\r\nVerification 45% complete."));
        Assert.Null(SystemRepair.ParseConsole(""));
        Assert.Null(SystemRepair.ParseConsole(null));
    }

    [Fact]
    public void ParseConsole_UnfixableWinsOverRepairedWords()
    {
        Assert.Equal(SfcVerdict.Unfixable, SystemRepair.ParseConsole("... found corrupt files but was unable to fix some of them. ... successfully repaired ..."));
    }

    // ---- запасной путь: CBS.log ----

    [Fact]
    public void ParseCbs_Markers()
    {
        Assert.Equal(SfcVerdict.Unfixable, SystemRepair.ParseCbs("[SR] Verifying 100 components\n[SR] Cannot repair member file [l:20]\"x.dll\" of Microsoft-Windows-Foo\n[SR] Repair complete"));
        Assert.Equal(SfcVerdict.Repaired, SystemRepair.ParseCbs("[SR] Repairing 2 components\n[SR] Repair complete"));
        Assert.Equal(SfcVerdict.Repaired, SystemRepair.ParseCbs("[SR] Repairing corrupted file x from store"));
        Assert.Equal(SfcVerdict.Clean, SystemRepair.ParseCbs("[SR] Repairing 0 components\n[SR] Beginning Verify and Repair transaction\n[SR] Verify complete"));
        Assert.Null(SystemRepair.ParseCbs("2026-10-02 12:00:00, Info CBS Session started"));
        Assert.Null(SystemRepair.ParseCbs(null));
    }

    [Fact]
    public void ParseSfc_ConsoleWinsOverCbs_CbsUsedWhenConsoleSilent_ElseUnknown()
    {
        Assert.Equal(SfcVerdict.Clean, SystemRepair.ParseSfc("Windows Resource Protection did not find any integrity violations.", "[SR] Cannot repair member file"));
        Assert.Equal(SfcVerdict.Repaired, SystemRepair.ParseSfc("мусор", "[SR] Repairing 1 components"));
        Assert.Equal(SfcVerdict.Unknown, SystemRepair.ParseSfc("мусор", "ничего"));
        Assert.Equal(SfcVerdict.Unknown, SystemRepair.ParseSfc(null, null));
    }

    // ---- общий итог ----

    [Fact]
    public void Summarize_Clean_Repaired_Unfixable()
    {
        var clean = SystemRepair.Summarize(0, false, SfcVerdict.Clean, 0, false);
        Assert.True(clean.Ok); Assert.Equal("", clean.Code); Assert.Contains("Повреждений нет", clean.Message);

        var fixedUp = SystemRepair.Summarize(0, false, SfcVerdict.Repaired, 0, false);
        Assert.True(fixedUp.Ok); Assert.Equal(AdminCodes.RebootRequired, fixedUp.Code); Assert.Contains("Перезагрузите", fixedUp.Message);

        var bad = SystemRepair.Summarize(0, false, SfcVerdict.Unfixable, 1, false);
        Assert.False(bad.Ok); Assert.Equal(SystemRepair.UnfixableCode, bad.Code); Assert.Contains("CBS.log", bad.Message);
        Assert.Equal(AdminCodes.RepairUnfixable, SystemRepair.UnfixableCode);
    }

    [Fact]
    public void Summarize_DismProblems_AreReportedButDoNotHideSfcResult()
    {
        var failed = SystemRepair.Summarize(unchecked((int)0x800f0906), false, SfcVerdict.Clean, 0, false);
        Assert.True(failed.Ok); Assert.Contains("DISM не смог", failed.Message); Assert.Contains("интернет", failed.Message);

        var timedOut = SystemRepair.Summarize(-1, true, SfcVerdict.Repaired, 0, false);
        Assert.Contains("DISM не уложился", timedOut.Message); Assert.Equal(AdminCodes.RebootRequired, timedOut.Code);

        var reboot = SystemRepair.Summarize(3010, false, SfcVerdict.Clean, 0, false);
        Assert.True(reboot.Ok); Assert.Equal(AdminCodes.RebootRequired, reboot.Code);
    }

    [Fact]
    public void Summarize_SfcTimeout_RebootPending_CouldNotRun_Unknown()
    {
        Assert.False(SystemRepair.Summarize(0, false, SfcVerdict.Unknown, -1, true).Ok);
        var pending = SystemRepair.Summarize(0, false, SfcVerdict.RebootPending, 1, false);
        Assert.False(pending.Ok); Assert.Equal(AdminCodes.RebootRequired, pending.Code);
        Assert.False(SystemRepair.Summarize(0, false, SfcVerdict.CouldNotRun, 2, false).Ok);
        Assert.True(SystemRepair.Summarize(0, false, SfcVerdict.Unknown, 0, false).Ok);              // итог не распознан, но код 0 — честно сказано в тексте
        Assert.Contains("не удалось", SystemRepair.Summarize(0, false, SfcVerdict.Unknown, 0, false).Message);
        Assert.False(SystemRepair.Summarize(0, false, SfcVerdict.Unknown, 1, false).Ok);
    }

    [Fact]
    public void AnotherRunning_SeesSfcDismOnly()
    {
        Assert.True(SystemRepair.AnotherRunning(new FakeProcs("sfc")));
        Assert.True(SystemRepair.AnotherRunning(new FakeProcs("Dism")));
        Assert.True(SystemRepair.AnotherRunning(new FakeProcs("DismHost")));
        Assert.False(SystemRepair.AnotherRunning(new FakeProcs("notepad", "chrome")));
    }

    [Fact]
    public void FixedCommands_AreExactlyTheDocumentedOnes()
    {
        Assert.Equal("/Online /Cleanup-Image /RestoreHealth", SystemRepair.DismArguments);
        Assert.Equal("/scannow", SystemRepair.SfcArguments);
        var sys = Environment.SystemDirectory;
        Assert.Equal(Path.Combine(sys, "Dism.exe"), SystemRepairRunner.DismPath);
        Assert.Equal(Path.Combine(sys, "sfc.exe"), SystemRepairRunner.SfcPath);
        Assert.True(Path.IsPathFullyQualified(SystemRepairRunner.DismPath));
        Assert.EndsWith(@"Logs\CBS\CBS.log", SystemRepairRunner.CbsLogPath);
    }

    // ---- политика помощника ----

    private static readonly string Nonce = new string('a', 32);
    private static Decision Decide(AdminAction a, string arg) => AdminPolicy.Decide(new AdminRequest(Nonce, a, arg));

    [Fact]
    public void Policy_RepairSystem_NoArgumentsAllowed_AnyArgumentRefused()
    {
        Assert.True(Decide(AdminAction.RepairSystem, "").Allowed);
        foreach (var arg in new[] { " ", "x", "/scannow", "--all", "Wer", "&calc", "\n", "0", "sfc.exe /verifyonly" })
            Assert.False(Decide(AdminAction.RepairSystem, arg).Allowed, arg);
    }

    [Fact]
    public void Job_RepairSystem_RoundTrips_AndGoesThroughPolicy()
    {
        var j = new AdminJob(Nonce, AdminJobKind.RepairSystem, "");
        Assert.Equal(j, AdminJob.Parse(j.ToJson()));
        Assert.Equal(AdminAction.RepairSystem, j.ToRequest().Action);
        Assert.False(AdminPolicy.Decide(new AdminJob(Nonce, AdminJobKind.RepairSystem, "x").ToRequest()).Allowed);
    }

    [Fact]
    public void Waits_AreLongEnough_ButUnderTaskLimits()
    {
        Assert.Equal(AdminMaintenance.RepairWait, AdminMaintenance.WaitTimeout(AdminAction.RepairSystem, ""));
        // подтверждение (5) + DISM (45) + sfc (30) укладываются в задание SYSTEM (90) и помощника (95)
        Assert.True(AdminMaintenance.RepairStep >= SystemRepair.DismTimeout + SystemRepair.SfcTimeout);
        Assert.True(AdminMaintenance.RepairStep < TimeSpan.FromMinutes(90));
        Assert.True(AdminMaintenance.RepairWait >= AdminMaintenance.ConfirmTimeout + AdminMaintenance.RepairStep);
        Assert.True(AdminMaintenance.RepairWait < TimeSpan.FromMinutes(95));
        Assert.Contains("PT90M", TaskXml.BuildSystem("C:\\x.exe"));        // лимиты заданий соответствуют ожиданиям
        Assert.Contains("PT95M", TaskXml.Build("PC\\U", "C:\\x.exe"));
    }

    // ---- исполнитель (SYSTEM) на подставных запускалках ----

    private sealed class FakeRepair(bool running, ProcessOutcome dism, SfcRun sfc) : IRepairRunner
    {
        public List<string> Calls = new();
        public bool AnotherRunning() { Calls.Add("check"); return running; }
        public ProcessOutcome Dism(TimeSpan t) { Calls.Add("dism " + t.TotalMinutes); return dism; }
        public SfcRun Sfc(TimeSpan t) { Calls.Add("sfc " + t.TotalMinutes); return sfc; }
    }

    [Fact]
    public void Run_DismThenSfc_ResultFromConsoleText()
    {
        var r = new FakeRepair(false, new(0, "", false), new(0, "Windows Resource Protection found corrupt files and successfully repaired them.", false, null));
        var res = SystemRepairRun.Run(r);
        Assert.Equal(new[] { "check", "dism 45", "sfc 30" }, r.Calls);
        Assert.True(res.Ok); Assert.Equal(AdminCodes.RebootRequired, res.Code);
    }

    [Fact]
    public void Run_ConsoleSilent_UsesCbsAppended_AndNeverStaleCbs()
    {
        var r = new FakeRepair(false, new(0, "", false), new(1, "", false, "[SR] Cannot repair member file x"));
        var res = SystemRepairRun.Run(r);
        Assert.False(res.Ok); Assert.Equal(AdminCodes.RepairUnfixable, res.Code);
    }

    [Fact]
    public void Run_AnotherRepairRunning_StartsNothing()
    {
        var r = new FakeRepair(true, new(0, "", false), new(0, "", false, null));
        var res = SystemRepairRun.Run(r);
        Assert.Equal(new[] { "check" }, r.Calls);
        Assert.False(res.Ok); Assert.Equal(AdminCodes.Busy, res.Code);
    }

    [Theory]
    [InlineData("Защите ресурсов Windows не удалось выполнить запрошенную операцию. Есть ожидающее восстановление, для завершения которого требуется перезагрузка.")]
    [InlineData("Защита ресурсов Windows обнаружила ожидающую операцию восстановления, для завершения которой требуется перезагрузка системы.")]
    [InlineData("Есть ожидающее восстановление системы, требуется перезагрузка. Перезагрузите и повторите sfc.")]
    public void Sfc_RussianRebootPending_BothForms(string text) => Assert.Equal(SfcVerdict.RebootPending, SystemRepair.ParseConsole(text));

    [Fact]
    public void Sfc_RussianRepaired_WithoutRebootPhrase_StaysRepaired() =>
        Assert.Equal(SfcVerdict.Repaired, SystemRepair.ParseConsole("Защита ресурсов Windows обнаружила поврежденные файлы и успешно их восстановила."));

    [Fact]
    public void Dism_Is45Minutes_AndNoteSaysSo()
    {
        Assert.Equal(TimeSpan.FromMinutes(45), SystemRepair.DismTimeout);
        var o = SystemRepair.Summarize(-1, true, SfcVerdict.Clean, 0, false);
        Assert.Contains("45 минут", o.Message);
    }

    [Fact]
    public void Run_SfcTimedOut_IsFailure()
    {
        var res = SystemRepairRun.Run(new FakeRepair(false, new(0, "", false), new(-1, "", true, null)));
        Assert.False(res.Ok); Assert.Contains("30 минут", res.Message);
    }

    [Fact]
    public void SystemJobExecutor_RepairSystem_RefusesArgument_AndRunsWithoutOne()
    {
        var r = new FakeRepair(false, new(0, "", false), new(0, "Windows Resource Protection did not find any integrity violations.", false, null));
        var exec = new SystemJobExecutor(new SystemMaintenance(_ => true, repair: r));
        Assert.False(exec.Execute(Nonce, AdminJobKind.RepairSystem, "x").Ok);
        Assert.Empty(r.Calls);
        Assert.True(exec.Execute(Nonce, AdminJobKind.RepairSystem, "").Ok);
        Assert.Contains("dism 45", r.Calls);
    }

    // ---- помощник пользователя: окно подтверждения и задание ----

    private sealed class FakeChannel : IJobChannel
    {
        public List<(AdminJobKind Kind, string Arg, TimeSpan T)> Calls = new();
        public JobResult Run(string nonce, AdminJobKind kind, string arg, TimeSpan timeout) { Calls.Add((kind, arg, timeout)); return new(nonce, true, "готово", AdminCodes.RebootRequired); }
    }

    [Fact]
    public void Brokered_RepairSystem_YesSendsFixedJob_NoOrTimeoutNever()
    {
        var ch = new FakeChannel(); var asked = new List<string>();
        var yes = new BrokeredMaintenance(ch, t => { asked.Add(t); return ConfirmAnswer.Yes; });
        var r = yes.RepairSystem();
        Assert.True(r.Ok);
        var call = Assert.Single(ch.Calls);
        Assert.Equal(AdminJobKind.RepairSystem, call.Kind); Assert.Equal("", call.Arg); Assert.Equal(AdminMaintenance.RepairStep, call.T);
        Assert.Contains("DISM", Assert.Single(asked)); Assert.Contains("sfc", asked[0]);

        var no = new FakeChannel();
        var rn = new BrokeredMaintenance(no, _ => ConfirmAnswer.No).RepairSystem();
        Assert.False(rn.Ok); Assert.Equal(AdminCodes.Declined, rn.Code); Assert.Empty(no.Calls);

        var to = new FakeChannel();
        var rt = new BrokeredMaintenance(to, _ => ConfirmAnswer.Timeout).RepairSystem();
        Assert.False(rt.Ok); Assert.Equal(AdminCodes.DeclinedTimeout, rt.Code); Assert.Empty(to.Calls);
    }

    private sealed class Exec : IMaintenanceExecutor
    {
        public List<string> Calls = new();
        public MaintenanceResult InstallDrivers(IReadOnlyList<string> ids, bool skipRestore) { Calls.Add("drivers"); return new(true, "ok"); }
        public MaintenanceResult CreateRestorePoint() { Calls.Add("rp"); return new(true, "ok"); }
        public MaintenanceResult CleanSystem(string id) { Calls.Add("clean"); return new(true, "ok"); }
        public MaintenanceResult RepairSystem() { Calls.Add("repair"); return new(true, "Повреждений нет.", ""); }
    }

    private static (AdminResponse? Res, Exec Exec) Process(AdminAction a, string arg)
    {
        var d = Path.Combine(Path.GetTempPath(), "pult-rep-" + Guid.NewGuid().ToString("N"));
        var user = Path.Combine(d, "u"); var res = Path.Combine(d, "r"); Directory.CreateDirectory(user); Directory.CreateDirectory(res);
        try
        {
            var req = Path.Combine(user, "admin.req");
            File.WriteAllText(req, AdminRequest.New(a, arg).ToJson());
            var ex = new Exec();
            return (new RequestProcessor(req, res, null, ex).ProcessOnce(), ex);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void RequestProcessor_RepairSystem_ReachesExecutorOnlyWithoutArguments()
    {
        var (ok, ex) = Process(AdminAction.RepairSystem, "");
        Assert.True(ok!.Ok); Assert.Equal(new[] { "repair" }, ex.Calls);
        var (bad, ex2) = Process(AdminAction.RepairSystem, "/scannow");
        Assert.False(bad!.Ok); Assert.Empty(ex2.Calls);
    }

    [Fact]
    public void DefaultExecutor_WithoutRepairSupport_Refuses()
    {
        IMaintenanceExecutor old = new OldExec();
        Assert.False(old.RepairSystem().Ok);
    }
    private sealed class OldExec : IMaintenanceExecutor
    {
        public MaintenanceResult InstallDrivers(IReadOnlyList<string> ids, bool skipRestore) => new(true, "");
        public MaintenanceResult CreateRestorePoint() => new(true, "");
        public MaintenanceResult CleanSystem(string id) => new(true, "");
    }
}
