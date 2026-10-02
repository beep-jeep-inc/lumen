using Pult.Core.Maintenance;
using Pult.Core.Monitoring;
namespace Pult.Core.Tests;

/// Python/.NET в работе: кто пользуется пакетом перед обновлением (чистое сопоставление; процессы, папки и pyvenv.cfg подставные).
public class RuntimeUsageTests
{
    private const string Py312 = @"C:\Users\U\AppData\Local\Programs\Python\Python312";
    private const string Py313 = @"C:\Users\U\AppData\Local\Programs\Python\Python313";
    private static readonly string[] Dirs312 = { Py312 };
    private static string? NoFiles(string _) => null;

    private static RunningProc P(string name, string? exe, string cl) => new(name, exe, cl);

    [Theory]
    [InlineData("Python.Python.3.12", true)] [InlineData("python.python.3.9", true)] [InlineData("Microsoft.DotNet.Runtime.8", true)]
    [InlineData("Microsoft.DotNet.SDK.9", true)] [InlineData("Microsoft.WindowsDesktop.App.7", true)] [InlineData("Microsoft.AspNetCore.8", true)]
    [InlineData("Python.Launcher", false)] [InlineData("Valve.Steam", false)] [InlineData("Microsoft.VCRedist.2015+.x64", false)] [InlineData("", false)] [InlineData(null, false)]
    public void IsGuarded(string? id, bool expected) => Assert.Equal(expected, RuntimeUsage.IsGuarded(id));

    [Theory]
    [InlineData("Python.Python.3.12", "3.12")] [InlineData("Python.Python.3.9", "3.9")] [InlineData("Python.Python.3", "3")]
    [InlineData("Python.Launcher", null)] [InlineData("Python.Python.3.12.1", null)] [InlineData("Python.Python.x", null)] [InlineData("Valve.Steam", null)]
    public void PythonVersion(string id, string? expected) => Assert.Equal(expected, RuntimeUsage.PythonVersion(id));

    [Fact]
    public void ConventionalDirs_Python312()
    {
        var d = RuntimeUsage.ConventionalPythonDirs("3.12", @"C:\Users\U\AppData\Local", @"C:\Program Files", @"C:\Program Files (x86)");
        Assert.Contains(Py312, d);
        Assert.Contains(@"C:\Program Files\Python312", d);
        Assert.Contains(@"C:\Program Files (x86)\Python312-32", d);
    }

    [Fact]
    public void IsInside_Boundaries()
    {
        Assert.True(RuntimeUsage.IsInside(Py312 + @"\python.exe", Py312));
        Assert.True(RuntimeUsage.IsInside(Py312.ToUpperInvariant() + @"\Scripts\x.exe", Py312 + @"\"));
        Assert.True(RuntimeUsage.IsInside(Py312, Py312));
        Assert.False(RuntimeUsage.IsInside(Py312 + @"0\python.exe", Py312));            // Python3120 — не Python312
        Assert.False(RuntimeUsage.IsInside(Py313 + @"\python.exe", Py312));
        Assert.False(RuntimeUsage.IsInside(@"C:\x\python.exe", ""));
    }

    [Fact]
    public void Python_ProcessFromThisVersionFolder_Matches_OtherVersionNot()
    {
        var procs = new[]
        {
            P("python.exe", Py312 + @"\python.exe", @"""" + Py312 + @"\python.exe"" -m notes_bot.startup"),
            P("pythonw.exe", Py312 + @"\pythonw.exe", ""),
            P("python.exe", Py313 + @"\python.exe", "python -m other"),
            P("chrome.exe", @"C:\chrome.exe", ""),
        };
        var users = RuntimeUsage.Find("Python.Python.3.12", procs, Dirs312, NoFiles);
        Assert.Equal(new[] { "python -m notes_bot.startup", "pythonw" }, users);
    }

    [Fact]
    public void Python_Venv_ByPyvenvCfgHome()
    {
        var venvScripts = @"D:\proj\bot\.venv\Scripts";
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"D:\proj\bot\.venv\pyvenv.cfg"] = "home = " + Py312 + "\r\ninclude-system-site-packages = false\r\nversion = 3.12.7\r\n",
            [@"D:\proj\other\.venv\pyvenv.cfg"] = "home = " + Py313 + "\r\n",
        };
        string? Read(string p) => files.GetValueOrDefault(p);
        var procs = new[]
        {
            P("python.exe", venvScripts + @"\python.exe", @"D:\proj\bot\.venv\Scripts\python.exe -m notes_bot.startup"),
            P("python.exe", @"D:\proj\other\.venv\Scripts\python.exe", "python other.py"),
            P("python.exe", @"D:\proj\nocfg\.venv\Scripts\python.exe", "python x.py"),
        };
        var users = RuntimeUsage.Find("Python.Python.3.12", procs, Dirs312, Read);
        Assert.Equal(new[] { "python -m notes_bot.startup" }, users);
        Assert.Equal(new[] { "python other.py" }, RuntimeUsage.Find("Python.Python.3.13", procs, new[] { Py313 }, Read));
    }

    [Fact]
    public void Python_CfgNextToExe_AndReadFailureIsIgnored()
    {
        var procs = new[] { P("python.exe", @"D:\v\python.exe", "python a.py") };
        Assert.Single(RuntimeUsage.Find("Python.Python.3.12", procs, Dirs312, p => p == @"D:\v\pyvenv.cfg" ? "home=" + Py312 : null));
        Assert.Empty(RuntimeUsage.Find("Python.Python.3.12", procs, Dirs312, _ => throw new UnauthorizedAccessException()));
    }

    [Fact]
    public void Python_UnknownVersionFolder_CountsAnyPython_ButUnknownPathDoesNotMatchKnownFolder()
    {
        var procs = new[] { P("python.exe", @"C:\x\python.exe", "python a.py"), P("py.exe", @"C:\Windows\py.exe", ""), P("notpython.exe", null, "") };
        Assert.Single(RuntimeUsage.Find("Python.Python.3", procs, Array.Empty<string>(), NoFiles));      // «3» без минорной — папку не угадать
        Assert.Empty(RuntimeUsage.Find("Python.Python.3.12", new[] { P("python.exe", null, "python a.py") }, Dirs312, NoFiles));  // путь неизвестен — не гадаем
    }

    [Fact]
    public void Python_ProcessNames()
    {
        var procs = new[] { "python.exe", "python3.exe", "python3.12.exe", "pythonw.exe", "pythonw3.exe", "python3.12.exe.bak", "pythonista.exe", "python.exe.exe" }
            .Select(n => P(n, Py312 + @"\" + n, "")).ToList();
        var hit = RuntimeUsage.Find("Python.Python.3.12", procs, Dirs312, NoFiles);
        Assert.Equal(new[] { "python", "python3", "python3.12", "pythonw", "pythonw3" }, hit);
    }

    [Fact]
    public void DotNet_DotnetExe_Only()
    {
        var procs = new[]
        {
            P("dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", @"""C:\Program Files\dotnet\dotnet.exe"" MyBot.dll --x"),
            P("dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", @"dotnet MyBot.dll --x"),
            P("python.exe", Py312 + @"\python.exe", "python a.py"),
            P("Pult.exe", @"C:\Pult\Pult.exe", ""),
        };
        Assert.Equal(new[] { "dotnet MyBot.dll --x" }, RuntimeUsage.Find("Microsoft.DotNet.Runtime.8", procs, Array.Empty<string>(), NoFiles));
        Assert.Equal(new[] { "dotnet MyBot.dll --x" }, RuntimeUsage.Find("Microsoft.WindowsDesktop.App.7", procs, Array.Empty<string>(), NoFiles));
        Assert.Equal(new[] { "dotnet MyBot.dll --x" }, RuntimeUsage.Find("Microsoft.AspNetCore.8", procs, Array.Empty<string>(), NoFiles));
        Assert.Equal(new[] { "dotnet MyBot.dll --x" }, RuntimeUsage.Find("Microsoft.DotNet.SDK.9", procs, Array.Empty<string>(), NoFiles));
    }

    [Fact]
    public void NotGuardedPackage_NeverReportsAnyone()
    {
        var procs = new[] { P("dotnet.exe", null, ""), P("python.exe", Py312 + @"\python.exe", "") };
        Assert.Empty(RuntimeUsage.Find("Valve.Steam", procs, Dirs312, NoFiles));
        Assert.Empty(RuntimeUsage.Find("Python.Launcher", procs, Dirs312, NoFiles));
    }

    [Theory]
    [InlineData(@"""C:\Py\python.exe"" -m notes_bot.startup", "python.exe", "python -m notes_bot.startup")]
    [InlineData(@"C:\Py\python.exe -m http.server 8000", "python.exe", "python -m http.server 8000")]
    [InlineData(@"""C:\Program Files\dotnet\dotnet.exe"" bot.dll", "dotnet.exe", "dotnet bot.dll")]
    [InlineData(@"python", "python.exe", "python")]
    [InlineData(@"""C:\Py\python.exe""", "python.exe", "python")]
    [InlineData("", "pythonw.exe", "pythonw")]
    public void Display_ShortCommand(string cl, string name, string expected) => Assert.Equal(expected, RuntimeUsage.Display(P(name, null, cl)));

    [Fact]
    public void Display_LongCommand_IsCut()
    {
        var d = RuntimeUsage.Display(P("python.exe", null, "python " + new string('x', 200)));
        Assert.True(d.Length <= 71); Assert.EndsWith("…", d);
    }

    [Fact]
    public void Find_Deduplicates()
    {
        var procs = new[] { P("python.exe", Py312 + @"\python.exe", "python -m bot"), P("python.exe", Py312 + @"\python.exe", "python -m bot") };
        Assert.Single(RuntimeUsage.Find("Python.Python.3.12", procs, Dirs312, NoFiles));
    }

    [Fact]
    public void VenvHome_Parsing()
    {
        Assert.Equal(Py312, RuntimeUsage.VenvHome("home = " + Py312 + "\nversion = 3.12"));
        Assert.Equal(Py312, RuntimeUsage.VenvHome("version = 3.12\r\nHome=" + Py312 + "\r\n"));
        Assert.Null(RuntimeUsage.VenvHome("version = 3.12")); Assert.Null(RuntimeUsage.VenvHome("")); Assert.Null(RuntimeUsage.VenvHome(null));
    }

    private sealed class Q(params RunningProcInfo[] procs) : IProcessQuery
    {
        public bool IsRunning(string name) => false;
        public bool AnyCommandLine(string wqlNameFilter, Func<string, bool> match) => false;
        public IReadOnlyList<RunningProcInfo> Processes(string wqlWhere) => procs;
    }

    [Fact]
    public void FindUsers_UsesProcessQuery_NotForOtherPackages()
    {
        var q = new Q(new RunningProcInfo("dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", "dotnet bot.dll"));
        Assert.Equal(new[] { "dotnet bot.dll" }, RuntimeUsage.FindUsers("Microsoft.DotNet.Runtime.8", q));
        Assert.Empty(RuntimeUsage.FindUsers("Valve.Steam", q));
        Assert.Empty(RuntimeUsage.FindUsers("Microsoft.DotNet.Runtime.8", new Q()));
        Assert.Empty(RuntimeUsage.FindUsers("Python.Python.3.12", q));
    }
}
