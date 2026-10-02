using System.Diagnostics;
using System.Xml.Linq;
using Pult.Admin;
using Pult.Core.Admin;
namespace Pult.Core.Tests;

// Fix round 2: COM/PowerShell только от SYSTEM («Pult System»), задания через C:\ProgramData\Pult\jobs, авто-«Нет» в окне.
public partial class AdminMaintenanceTests
{
    private sealed class FakeJobTrust(Func<string, string?>? file = null, string? dir = null) : IJobTrust
    {
        public string? DirProblem(string d) => dir;
        public string? FileProblem(string f) => file?.Invoke(f);
    }

    private JobStore Jobs(string name, IJobTrust? trust = null) { var p = Path.Combine(_d, name); Directory.CreateDirectory(p); return new JobStore(p, trust ?? new FakeJobTrust()); }
    private static string Nonce() => Guid.NewGuid().ToString("N");

    // ---- задание «Pult System» ----

    [Fact]
    public void SystemTaskXml_SystemPrincipal_FixedArgument_NoTriggers_RestrictedAcl()
    {
        var x = XDocument.Parse(TaskXml.BuildSystem(@"C:\Program Files\Pult\PultAdmin.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("S-1-5-18", x.Descendants(ns + "UserId").Single().Value);
        Assert.Empty(x.Descendants(ns + "LogonType"));   // schtasks /XML отвергает ServiceAccount («out of range», проверено 30.09); для SYSTEM достаточно SID
        Assert.Equal("HighestAvailable", x.Descendants(ns + "RunLevel").Single().Value);
        Assert.Empty(x.Descendants(ns + "Triggers"));
        Assert.Equal("--system-job", x.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("\"C:\\Program Files\\Pult\\PultAdmin.exe\"", x.Descendants(ns + "Command").Single().Value);
        Assert.Equal("true", x.Descendants(ns + "Hidden").Single().Value);
        Assert.Equal("Queue", x.Descendants(ns + "MultipleInstancesPolicy").Single().Value);
        var limit = System.Xml.XmlConvert.ToTimeSpan(x.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal(TimeSpan.FromMinutes(90), limit);
        Assert.True(limit > AdminMaintenance.ComponentStep && limit > AdminMaintenance.DriverInstallStep + AdminMaintenance.SearchStep);
        var sddl = x.Descendants(ns + "SecurityDescriptor").Single().Value;
        Assert.Equal("D:P(A;;FA;;;SY)(A;;FA;;;BA)", sddl);                           // запускать/менять — только SYSTEM и Администраторы
        Assert.Equal("Pult System", TaskXml.SystemTaskName);
    }

    [Fact]
    public void Budgets_AppWaitExceedsHelperSteps()
    {
        Assert.True(AdminMaintenance.DriverInstallWait > AdminMaintenance.SearchStep + AdminMaintenance.ConfirmTimeout + AdminMaintenance.DriverInstallStep);
        Assert.True(AdminMaintenance.ComponentCleanupWait > AdminMaintenance.ConfirmTimeout + AdminMaintenance.ComponentStep);
        Assert.True(AdminMaintenance.CleanWait > AdminMaintenance.ConfirmTimeout + AdminMaintenance.CleanStep);
        Assert.True(AdminMaintenance.RestorePointWait > AdminMaintenance.RestoreStep);
        Assert.True(AdminMaintenance.ComponentStep > ComponentCleanup.Timeout);
    }

    // ---- файлы заданий ----

    [Fact]
    public void JobStore_ValidJob_Read_OldestFirst()
    {
        var s = Jobs("j1");
        var a = Nonce(); var b = Nonce();
        s.Submit(new AdminJob(a, AdminJobKind.RestorePoint, ""));
        File.SetCreationTimeUtc(Path.Combine(s.Dir, a + ".job"), DateTime.UtcNow.AddMinutes(-5));
        s.Submit(new AdminJob(b, AdminJobKind.CleanSystem, "Wer"));
        var t = s.TakeOldest()!.Value;
        Assert.Equal(a, t.Job.Nonce); Assert.Equal(AdminJobKind.RestorePoint, t.Job.Kind);
    }

    [Fact]
    public void JobStore_BadOwnerOrAcl_Ignored_AndRemoved()
    {
        var s = Jobs("j2", new FakeJobTrust(f => "владелец — не система"));
        var n = Nonce();
        File.WriteAllText(Path.Combine(s.Dir, n + ".job"), new AdminJob(n, AdminJobKind.RestorePoint, "").ToJson());
        Assert.Null(s.TakeOldest());
        Assert.False(File.Exists(Path.Combine(s.Dir, n + ".job")));
    }

    [Fact]
    public void JobStore_DirNotProtected_NothingTaken_SubmitRefused()
    {
        var s = Jobs("j3", new FakeJobTrust(dir: "пользователь может писать"));
        var n = Nonce();
        File.WriteAllText(Path.Combine(s.Dir, n + ".job"), new AdminJob(n, AdminJobKind.RestorePoint, "").ToJson());
        Assert.Null(s.TakeOldest());
        Assert.Throws<InvalidOperationException>(() => s.Submit(new AdminJob(Nonce(), AdminJobKind.RestorePoint, "")));
    }

    [Fact]
    public void JobStore_Oversize_Hardlink_UnknownKind_NameMismatch_Directory_Ignored()
    {
        var s = Jobs("j4");
        var big = Nonce();
        File.WriteAllText(Path.Combine(s.Dir, big + ".job"), new AdminJob(big, AdminJobKind.CleanSystem, "Wer").ToJson() + new string(' ', 9000));
        var unk = Nonce();
        File.WriteAllText(Path.Combine(s.Dir, unk + ".job"), "{\"Nonce\":\"" + unk + "\",\"Kind\":\"FormatDisk\",\"Arg\":\"\"}");
        var mism = Nonce();
        File.WriteAllText(Path.Combine(s.Dir, mism + ".job"), new AdminJob(Nonce(), AdminJobKind.RestorePoint, "").ToJson());
        Directory.CreateDirectory(Path.Combine(s.Dir, Nonce() + ".job"));
        var hard = Nonce();
        var src = Path.Combine(_d, "hardsrc.txt");
        File.WriteAllText(src, new AdminJob(hard, AdminJobKind.RestorePoint, "").ToJson());
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /H \"{Path.Combine(s.Dir, hard + ".job")}\" \"{src}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        File.WriteAllText(Path.Combine(s.Dir, "note.txt"), "x");
        Assert.Null(s.TakeOldest());
        Assert.True(File.Exists(src));                                                     // цель hardlink цела
    }

    [Fact]
    public void JobStore_Result_OnlyOwnNonce_NonceMismatchIgnored()
    {
        var s = Jobs("j5");
        var n = Nonce();
        s.WriteResult(new JobResult(n, true, "ok", "", new[] { G1 }, new[] { "t" }));
        Assert.Null(s.TryReadResult(Nonce()));
        var r = s.TryReadResult(n)!;
        Assert.Equal(new[] { G1 }, r.Ids); Assert.Equal("t", r.Titles![0]);
        Assert.Null(s.TryReadResult(n));                                                   // прочитанный удалён
        var other = Nonce();
        File.WriteAllText(Path.Combine(s.Dir, n + ".result"), new JobResult(other, true, "подмена").ToJson());
        Assert.Null(s.TryReadResult(n));
    }

    // ---- SYSTEM-исполнитель ----

    private sealed class FakeExec : IJobExecutor
    {
        public List<string> Calls = new();
        public Func<AdminJobKind, string, JobResult>? Reply;
        public JobResult Execute(string nonce, AdminJobKind kind, string arg)
        {
            Calls.Add(kind + ":" + arg);
            return Reply?.Invoke(kind, arg) ?? new(nonce, true, "ok");
        }
    }

    [Fact]
    public void Worker_NotSystem_DoesNothing_JobStays()
    {
        var s = Jobs("w1"); var n = Nonce();
        s.Submit(new AdminJob(n, AdminJobKind.RestorePoint, ""));
        var ex = new FakeExec(); var logs = new List<string>();
        var rc = SystemWorker.Run(() => false, s, ex, logs.Add);
        Assert.Equal(SystemWorker.ExitNotSystem, rc);
        Assert.Empty(ex.Calls);
        Assert.True(File.Exists(Path.Combine(s.Dir, n + ".job")));
        Assert.Contains(logs, l => l.Contains("не от SYSTEM"));
    }

    [Fact]
    public void Worker_RevalidatesJob_BadArgsNeverExecuted()
    {
        var s = Jobs("w2");
        foreach (var j in new[]
        {
            new AdminJob(Nonce(), AdminJobKind.CleanSystem, @"C:\Windows\System32"),
            new AdminJob(Nonce(), AdminJobKind.DriverInstall, "x'; calc; '"),
            new AdminJob(Nonce(), AdminJobKind.DriverSearch, ""),
            new AdminJob(Nonce(), AdminJobKind.RestorePoint, "arg"),
        })
        {
            s.Submit(j);
            var ex = new FakeExec();
            Assert.Equal(0, SystemWorker.Run(() => true, s, ex, _ => { }));
            Assert.Empty(ex.Calls);
            var r = s.TryReadResult(j.Nonce)!;
            Assert.False(r.Ok);
            Assert.False(File.Exists(Path.Combine(s.Dir, j.Nonce + ".job")));
        }
    }

    [Fact]
    public void Worker_ExecutorThrows_ResultIsFailure_JobRemoved()
    {
        var s = Jobs("w3"); var n = Nonce();
        s.Submit(new AdminJob(n, AdminJobKind.RestorePoint, ""));
        var ex = new FakeExec { Reply = (_, _) => throw new InvalidOperationException("boom") };
        SystemWorker.Run(() => true, s, ex, _ => { });
        var r = s.TryReadResult(n)!;
        Assert.False(r.Ok); Assert.Contains("boom", r.Message);
    }

    [Fact]
    public void JobStore_StaleJob_NotReturned_AndDeleted()
    {
        var s = Jobs("st1"); var n = Nonce();
        s.Submit(new AdminJob(n, AdminJobKind.RestorePoint, ""));
        File.SetCreationTimeUtc(Path.Combine(s.Dir, n + ".job"), DateTime.UtcNow.AddMinutes(-20));
        Assert.Null(s.TakeOldest());
        Assert.False(File.Exists(Path.Combine(s.Dir, n + ".job")));
    }

    [Fact]
    public void Worker_StaleAndFresh_OnlyFreshRuns()
    {
        var s = Jobs("st2"); var old = Nonce(); var fresh = Nonce();
        s.Submit(new AdminJob(old, AdminJobKind.CleanSystem, "Wer"));
        File.SetCreationTimeUtc(Path.Combine(s.Dir, old + ".job"), DateTime.UtcNow.AddMinutes(-20));
        s.Submit(new AdminJob(fresh, AdminJobKind.RestorePoint, ""));
        var ex = new FakeExec();
        SystemWorker.Run(() => true, s, ex, _ => { });
        Assert.Equal(new[] { "RestorePoint:" }, ex.Calls);
        Assert.Null(s.TryReadResult(old));
        Assert.NotNull(s.TryReadResult(fresh));
        Assert.Empty(Directory.GetFiles(s.Dir, "*.job"));
    }

    [Fact]
    public void Worker_ClaimsJob_BeforeExecute_AndLeftoverRunIsNeverExecuted()
    {
        var s = Jobs("st3"); var n = Nonce();
        s.Submit(new AdminJob(n, AdminJobKind.RestorePoint, ""));
        var ex = new FakeExec();
        ex.Reply = (_, _) =>
        {
            Assert.False(File.Exists(Path.Combine(s.Dir, n + ".job")));                    // уже забрано
            Assert.True(File.Exists(Path.Combine(s.Dir, n + ".run")));
            return new(n, true, "ok");
        };
        SystemWorker.Run(() => true, s, ex, _ => { });
        Assert.Single(ex.Calls);
        Assert.False(File.Exists(Path.Combine(s.Dir, n + ".run")));                        // удалено после выполнения

        var left = Nonce();                                                                // остаток от убитого исполнителя
        File.WriteAllText(Path.Combine(s.Dir, left + ".run"), new AdminJob(left, AdminJobKind.RestorePoint, "").ToJson());
        var ex2 = new FakeExec();
        SystemWorker.Run(() => true, s, ex2, _ => { });
        Assert.Empty(ex2.Calls);
    }

    // ---- полный путь: помощник -> задание -> SYSTEM -> результат -> ответ ----

    private (BrokeredMaintenance m, FakeExec ex, List<string> asked, JobStore store) Flow(Func<string, ConfirmAnswer> confirm, string name)
    {
        var store = Jobs(name); var ex = new FakeExec(); var asked = new List<string>();
        ex.Reply = (kind, arg) => kind switch
        {
            AdminJobKind.DriverSearch => new("", true, "найдено", "", new[] { G1 }, new[] { "Intel Test Driver\nsecond line" }),
            AdminJobKind.DriverInstall => new("", true, "Драйверы: установлено 1", AdminCodes.RebootRequired),
            _ => new("", true, "готово", AdminMaintenance.CleanCode(5, 0)),
        };
        var ch = new SystemTaskChannel(store, runTask: () => { SystemWorker.Run(() => true, store, ex, _ => { }); return true; }, taskExists: () => true);
        var m = new BrokeredMaintenance(ch, t => { asked.Add(t); return confirm(t); }, id => 2048);
        return (m, ex, asked, store);
    }

    [Fact]
    public void Flow_Drivers_SearchThenConfirmThenInstall_OnlySearchIdsGoToInstall()
    {
        var (m, ex, asked, _) = Flow(_ => ConfirmAnswer.Yes, "f1");
        var r = m.InstallDrivers(new[] { G1, G2 }, false);
        Assert.True(r.Ok); Assert.Equal(AdminCodes.RebootRequired, r.Code);
        Assert.Equal(new[] { $"DriverSearch:{G1},{G2}", $"DriverInstall:{G1}" }, ex.Calls);          // в установку ушёл только результат поиска
        var q = Assert.Single(asked);
        Assert.Contains("1 шт.", q); Assert.Contains("Intel Test Driver second line", q);            // переводы строк в названии убраны
        Assert.DoesNotContain("Intel Test Driver\n", q);
        Assert.Contains("точка восстановления", q);
    }

    [Fact]
    public void Flow_Drivers_SkipRestore_FlagSurvivesJobAndConfirmSaysSo()
    {
        var (m, ex, asked, _) = Flow(_ => ConfirmAnswer.Yes, "f1n");
        Assert.True(m.InstallDrivers(new[] { G1 }, true).Ok);
        Assert.Equal(new[] { $"DriverSearch:{G1}", $"DriverInstall:norp:{G1}" }, ex.Calls);
        Assert.Contains("НЕ будет создана", Assert.Single(asked));
        var j = new AdminJob(Nonce(), AdminJobKind.DriverInstall, "norp:" + G1);
        Assert.Equal(j, AdminJob.Parse(j.ToJson()));
    }

    private sealed class ScriptLog : IScriptRunner
    {
        public List<string> Scripts = new();
        public ProcessOutcome Run(string script, TimeSpan t) { Scripts.Add(script); return new(0, $"U|{G1}|0|QQ==\nEND\nR|1|0|0|0\nCREATED\n", false); }
    }
    private sealed class CleanClasses : IUserClassesRegistry { public bool KeyExists(string p) => false; }

    [Fact]
    public void SystemExecutor_ReparsesArg_PrefixSkipsRestore_BadPrefixRefused()
    {
        var log = new ScriptLog();
        var exec = new SystemJobExecutor(new SystemMaintenance(_ => true, new CleanClasses(), log));
        Assert.True(exec.Execute(Nonce(), AdminJobKind.DriverInstall, "norp:" + G1).Ok);
        Assert.DoesNotContain(MaintenanceScripts.RestorePoint, log.Scripts);                 // точка не создавалась
        Assert.NotEmpty(log.Scripts);

        log.Scripts.Clear();
        Assert.True(exec.Execute(Nonce(), AdminJobKind.DriverInstall, G1).Ok);
        Assert.Contains(MaintenanceScripts.RestorePoint, log.Scripts);                       // без префикса — создавалась

        log.Scripts.Clear();
        foreach (var bad in new[] { "norp:norp:" + G1, "NORP:" + G1, "norp:" })
        {
            var r = exec.Execute(Nonce(), AdminJobKind.DriverInstall, bad);
            Assert.False(r.Ok);
        }
        Assert.Empty(log.Scripts);
    }

    [Fact]
    public void Worker_LoopStopsTakingJobs_AfterFiveMinutes()
    {
        var s = Jobs("w5m"); var t = DateTime.UtcNow;
        var a = Nonce(); var b = Nonce();
        s.Submit(new AdminJob(a, AdminJobKind.RestorePoint, ""));
        var ex = new FakeExec();
        ex.Reply = (_, _) => { s.Submit(new AdminJob(b, AdminJobKind.RestorePoint, "")); t = t.AddMinutes(70); return new(a, true, "ok"); };   // за время работы прошло 70 минут
        SystemWorker.Run(() => true, s, ex, _ => { }, () => t);
        Assert.Single(ex.Calls);
        Assert.True(File.Exists(Path.Combine(s.Dir, b + ".job")));                             // новое задание осталось для свежего запуска
    }

    [Fact]
    public void Worker_LoopContinues_WithinFiveMinutes()
    {
        var s = Jobs("w1m"); var t = DateTime.UtcNow;
        s.Submit(new AdminJob(Nonce(), AdminJobKind.RestorePoint, "")); s.Submit(new AdminJob(Nonce(), AdminJobKind.RestorePoint, ""));
        var ex = new FakeExec { Reply = (_, _) => { t = t.AddMinutes(1); return new("", true, "ok"); } };
        SystemWorker.Run(() => true, s, ex, _ => { }, () => t);
        Assert.Equal(2, ex.Calls.Count);
    }

    [Fact]
    public void Flow_Drivers_No_OnlySearchRan()
    {
        var (m, ex, _, _) = Flow(_ => ConfirmAnswer.No, "f2");
        var r = m.InstallDrivers(new[] { G1 }, false);
        Assert.False(r.Ok); Assert.Equal(AdminCodes.Declined, r.Code);
        Assert.Equal(new[] { $"DriverSearch:{G1}" }, ex.Calls);
    }

    [Fact]
    public void Flow_Drivers_ConfirmTimeout_DeclinedTimeout_NoInstall()
    {
        var (m, ex, _, _) = Flow(_ => ConfirmAnswer.Timeout, "f3");
        var r = m.InstallDrivers(new[] { G1 }, false);
        Assert.False(r.Ok); Assert.Equal(AdminCodes.DeclinedTimeout, r.Code);
        Assert.Contains("5 минут", r.Message);
        Assert.Single(ex.Calls);
    }

    [Fact]
    public void Flow_Clean_ConfirmTimeout_OrNo_NoJobAtAll()
    {
        foreach (var a in new[] { ConfirmAnswer.Timeout, ConfirmAnswer.No })
        {
            var (m, ex, asked, store) = Flow(_ => a, "f4" + a);
            var r = m.CleanSystem("ComponentCleanup");
            Assert.False(r.Ok);
            Assert.Equal(a == ConfirmAnswer.Timeout ? AdminCodes.DeclinedTimeout : AdminCodes.Declined, r.Code);
            Assert.Empty(ex.Calls);
            Assert.Empty(Directory.GetFiles(store.Dir));
            Assert.Contains("необратима", asked[0]);
        }
    }

    [Fact]
    public void Flow_Clean_Yes_RunsAsJob_WithEstimateInText()
    {
        var (m, ex, asked, _) = Flow(_ => ConfirmAnswer.Yes, "f5");
        var r = m.CleanSystem("Wer");
        Assert.True(r.Ok);
        Assert.Equal(new[] { "CleanSystem:Wer" }, ex.Calls);
        Assert.Contains("2 КБ", asked[0]);
        Assert.True(AdminMaintenance.TryParseCleanCode(r.Code, out var b, out _)); Assert.Equal(5, b);
        Assert.False(m.CleanSystem("Bogus").Ok);
        Assert.Single(ex.Calls);
    }

    [Fact]
    public void Flow_RestorePoint_NoQuestion_JustJob()
    {
        var (m, ex, asked, _) = Flow(_ => ConfirmAnswer.No, "f6");
        Assert.True(m.CreateRestorePoint().Ok);
        Assert.Equal(new[] { "RestorePoint:" }, ex.Calls); Assert.Empty(asked);
    }

    [Fact]
    public void Flow_EndToEnd_ThroughRequestProcessor()
    {
        var (m, ex, _, _) = Flow(_ => ConfirmAnswer.Yes, "f7");
        var user = Path.Combine(_d, "f7u"); var res = Path.Combine(_d, "f7r");
        Directory.CreateDirectory(user); Directory.CreateDirectory(res);
        var rq = Path.Combine(user, "admin.req");
        File.WriteAllText(rq, AdminRequest.New(AdminAction.DriverInstall, G1).ToJson());
        var resp = new RequestProcessor(rq, res, null, m).ProcessOnce()!;
        Assert.True(resp.Ok); Assert.Equal(AdminCodes.RebootRequired, resp.Code);
        Assert.Equal(2, ex.Calls.Count);
    }

    [Fact]
    public void Channel_TaskMissing_ClearCode_NoJobFile()
    {
        var store = Jobs("c1");
        var ch = new SystemTaskChannel(store, () => true, () => false);
        var r = ch.Run(Nonce(), AdminJobKind.RestorePoint, "", TimeSpan.FromSeconds(1));
        Assert.False(r.Ok); Assert.Equal(AdminCodes.SystemTaskMissing, r.Code);
        Assert.Empty(Directory.GetFiles(store.Dir));
    }

    [Fact]
    public void Channel_NoAnswer_TimesOut_JobCancelled()
    {
        var store = Jobs("c2");
        var ch = new SystemTaskChannel(store, () => true, () => true);
        var r = ch.Run(Nonce(), AdminJobKind.RestorePoint, "", TimeSpan.FromMilliseconds(600));
        Assert.False(r.Ok); Assert.Contains("может ещё идти", r.Message);
        Assert.Empty(Directory.GetFiles(store.Dir, "*.job"));
    }

    [Fact]
    public void Channel_RunTaskFails_JobCancelled()
    {
        var store = Jobs("c3");
        var ch = new SystemTaskChannel(store, () => false, () => true);
        Assert.False(ch.Run(Nonce(), AdminJobKind.RestorePoint, "", TimeSpan.FromSeconds(1)).Ok);
        Assert.Empty(Directory.GetFiles(store.Dir));
    }

    [Fact]
    public void Json_RoundTrip_JobAndResult()
    {
        var j = new AdminJob(Nonce(), AdminJobKind.DriverInstall, G1);
        Assert.Equal(j, AdminJob.Parse(j.ToJson()));
        var r = new JobResult(Nonce(), true, "m", "c", new[] { "a" }, new[] { "b" });
        var p = JobResult.Parse(r.ToJson())!;
        Assert.Equal(r.Nonce, p.Nonce); Assert.Equal(new[] { "a" }, p.Ids);
        Assert.Null(AdminJob.Parse("{\"Nonce\":\"zz\",\"Kind\":\"RestorePoint\",\"Arg\":\"\"}"));
    }

    [Fact]
    public void WindowsIdentityCheck_IsNotSystem_ForTestProcess() => Assert.False(SystemWorker.CurrentIsSystem());
}
