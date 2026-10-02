using System.Diagnostics;
using Pult.Admin;
using Pult.Core.Admin;
namespace Pult.Core.Tests;

public class RequestProcessorTests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-rp-" + Guid.NewGuid().ToString("N"));
    private string User => Path.Combine(_d, "user");
    private string Res => Path.Combine(_d, "res");
    private string Req => Path.Combine(User, "admin.req");
    public RequestProcessorTests() { Directory.CreateDirectory(User); Directory.CreateDirectory(Res); }
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    /// Запоминает, какие действия дошли до исполнителя обслуживания.
    internal sealed class Exec : IMaintenanceExecutor
    {
        public List<string> Calls = new();
        public MaintenanceResult InstallDrivers(IReadOnlyList<string> updateIds, bool skipRestore) { Calls.Add("drivers"); return new(true, "ok"); }
        public MaintenanceResult CreateRestorePoint() { Calls.Add("rp"); return new(true, "ok"); }
        public MaintenanceResult CleanSystem(string cleanId) { Calls.Add("clean " + cleanId); return new(true, "ok"); }
    }

    private RequestProcessor Proc(string req, Exec exec) =>
        new(req, Res, null, exec);

    [Fact]
    public void Allowed_Executes_WritesResponseToResDir_AndLeavesUserFolderAlone()
    {
        var req = AdminRequest.New(AdminAction.RestorePoint, "");
        File.WriteAllText(Req, req.ToJson());
        var exec = new Exec();
        var res = Proc(Req, exec).ProcessOnce()!;
        Assert.True(res.Ok);
        Assert.Equal(req.Nonce, res.Nonce);
        Assert.Equal(new[] { "rp" }, exec.Calls);
        Assert.True(File.Exists(Req));                                              // помощник в профиле ничего не удаляет
        Assert.Equal(new[] { "admin.req" }, Directory.GetFiles(User).Select(Path.GetFileName));   // и ничего не пишет
        Assert.Equal(req.Nonce, AdminResponse.Parse(File.ReadAllText(Path.Combine(Res, req.Nonce + ".json")))!.Nonce);
    }

    [Fact]
    public void Denied_DoesNotExecute_ExplainsWhy()
    {
        var req = AdminRequest.New(AdminAction.CleanSystem, "неизвестный-вид");
        File.WriteAllText(Req, req.ToJson());
        var exec = new Exec();
        var res = Proc(Req, exec).ProcessOnce()!;
        Assert.False(res.Ok);
        Assert.Empty(exec.Calls);
        Assert.Contains("неизвестный вид очистки", res.Message);
    }

    [Fact]
    public void NoOrGarbageRequest_NoExecution()
    {
        File.WriteAllText(Req, "мусор");
        var exec = new Exec();
        Assert.Null(Proc(Req, exec).ProcessOnce());
        Assert.Null(Proc(Path.Combine(User, "нет.req"), exec).ProcessOnce());
        Assert.Empty(exec.Calls);
        Assert.Empty(Directory.GetFiles(Res));
    }

    [Fact]
    public void BadNonce_NoExecution_NoResponseFile()
    {
        File.WriteAllText(Req, """{"Nonce":"..\\..\\evil","Action":"RestorePoint","Arg":""}""");
        var exec = new Exec();
        Assert.Null(Proc(Req, exec).ProcessOnce());
        Assert.Empty(exec.Calls);
        Assert.Empty(Directory.GetFiles(Res));
    }

    [Fact]
    public void RequestOver16KB_Ignored()
    {
        var req = AdminRequest.New(AdminAction.RestorePoint, new string('a', 17 * 1024));
        File.WriteAllText(Req, req.ToJson());
        var exec = new Exec();
        Assert.Null(Proc(Req, exec).ProcessOnce());
        Assert.Empty(exec.Calls);
    }

    [Fact]
    public void RequestDirIsJunction_Ignored_NothingWritten()       // C3: %LOCALAPPDATA%\Pult подменён ссылкой
    {
        var real = Path.Combine(_d, "elsewhere");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "admin.req"), AdminRequest.New(AdminAction.RestorePoint, "").ToJson());
        var link = Path.Combine(_d, "link");
        Junction(link, real);
        var exec = new Exec();
        Assert.Null(Proc(Path.Combine(link, "admin.req"), exec).ProcessOnce());
        Assert.Empty(exec.Calls);
        Assert.True(File.Exists(Path.Combine(real, "admin.req")));
        Assert.Empty(Directory.GetFiles(Res));
    }

    [Fact]
    public void OldResponses_Deleted_FreshKept()
    {
        var old = Path.Combine(Res, "0123456789abcdef0123456789abcdef.json");
        var fresh = Path.Combine(Res, "fedcba9876543210fedcba9876543210.json");
        File.WriteAllText(old, "{}"); File.WriteAllText(fresh, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-11));
        Proc(Req, new Exec()).ProcessOnce();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    internal static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "не удалось создать junction");
    }
}
