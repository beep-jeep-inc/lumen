using Pult.Admin;
namespace Pult.Core.Tests;

/// Тесты, меняющие переменные окружения ВСЕГО процесса (DOTNET_STARTUP_HOOKS, PSModulePath): пока они идут, другие тесты
/// (запуск powershell/cmd, winget) не должны выполняться параллельно — иначе они унаследуют подложенные значения.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection { public const string Name = "ProcessEnvironment"; }

[Collection(ProcessEnvironmentCollection.Name)]
public class ProcessEnvironmentTests
{
    [Fact]
    public void CleanEnvironment_OnlyMachineValues_NoUserInjection()
    {
        var oldPs = Environment.GetEnvironmentVariable("PSModulePath");
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", @"C:\evil.dll");
        Environment.SetEnvironmentVariable("PSModulePath", @"C:\Users\x\evil");
        try
        {
            var machine = new Dictionary<string, string> { ["Path"] = @"%SystemRoot%\system32;C:\Tools", ["USERNAME"] = "SYSTEM", ["OS"] = "Windows_NT" };
            var env = CleanEnvironment.Build(machine, @"C:\Windows\system32");
            Assert.False(env.ContainsKey("DOTNET_STARTUP_HOOKS"));
            Assert.False(env.ContainsKey("PSModulePath"));
            Assert.False(env.ContainsKey("USERNAME"));
            Assert.False(env.ContainsKey("USERPROFILE"));
            Assert.Equal(@"C:\Windows\system32;C:\Tools", env["PATH"]);
            Assert.Equal(@"C:\Windows\Temp", env["TEMP"]);
            Assert.Equal(@"C:\Windows\system32\cmd.exe", env["ComSpec"]);
            var app = CleanEnvironment.Build(machine, @"C:\Windows\system32", new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\me", ["NODE_OPTIONS"] = "x" });
            Assert.Equal(@"C:\Users\me", app["USERPROFILE"]);
            Assert.False(app.ContainsKey("NODE_OPTIONS"));
            var real = CleanEnvironment.ForSystem();
            Assert.False(real.ContainsKey("DOTNET_STARTUP_HOOKS"));
            Assert.DoesNotContain(@"C:\Users\x", real["PATH"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
            Environment.SetEnvironmentVariable("PSModulePath", oldPs);
        }
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public class WingetTempTests
{
    [Fact]
    public void ForWinget_TempIsOurFolder_ProfileKept_NoUserInjection()
    {
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", @"C:\evil.dll");
        try
        {
            var env = CleanEnvironment.ForWinget(@"C:\ProgramData\Pult\wtemp\abc");
            Assert.Equal(@"C:\ProgramData\Pult\wtemp\abc", env["TEMP"]);
            Assert.Equal(@"C:\ProgramData\Pult\wtemp\abc", env["TMP"]);
            Assert.False(string.IsNullOrEmpty(env["USERPROFILE"]));
            Assert.False(string.IsNullOrEmpty(env["APPDATA"]));
            Assert.False(string.IsNullOrEmpty(env["LOCALAPPDATA"]));
            Assert.False(env.ContainsKey("DOTNET_STARTUP_HOOKS"));
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null); }
    }

    [Fact]
    public void CleanOld_RemovesOldSubfolders_KeepsFresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "pult-wt-" + Guid.NewGuid().ToString("N"));
        var old = Path.Combine(root, "old"); var fresh = Path.Combine(root, "fresh");
        Directory.CreateDirectory(old); Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(old, "x.tmp"), "1");
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(old, now.AddDays(-2)); Directory.SetLastWriteTimeUtc(fresh, now.AddHours(-3));
        try
        {
            WingetTemp.CleanOld(root, now, TimeSpan.FromDays(1));
            Assert.False(Directory.Exists(old)); Assert.True(Directory.Exists(fresh));
        }
        finally { Directory.Delete(root, true); }
    }
}
