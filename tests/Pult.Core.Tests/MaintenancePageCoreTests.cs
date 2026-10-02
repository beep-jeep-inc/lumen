using Pult.Core.Cleanup;
using Pult.Core.Maintenance;
using Pult.Core.Monitoring;
namespace Pult.Core.Tests;

public class MaintenancePageCoreTests
{
    [Fact]
    public void CleanOutput_drops_spinner_and_progress_but_keeps_table_for_parser()
    {
        var raw = "\r-\r\\\r|\r/\r   \rИмя       ИД                  Версия Доступно Источник\r\n" +
                  "--------------------------------------------------\r\n" +
                  "  ██████▒▒▒▒  40%\r\n" +
                  "Notepad++ Notepad++.Notepad++ 8.9.2  8.9.8.1  winget\r\n" +
                  "Доступны обновления: 1.\r\n";
        var rows = WingetTable.Parse(WingetRunner.CleanOutput(raw));
        Assert.Single(rows);
        Assert.Equal("Notepad++.Notepad++", rows[0].Id);
    }

    [Theory]
    [InlineData(1, "драйвер")]
    [InlineData(3, "драйвера")]
    [InlineData(5, "драйверов")]
    [InlineData(11, "драйверов")]
    [InlineData(21, "драйвер")]
    [InlineData(24, "драйвера")]
    public void Plural_russian(int n, string word) => Assert.Equal(word, RuText.Plural(n, "драйвер", "драйвера", "драйверов"));

    [Fact]
    public void Size_unknown_and_units()
    {
        Assert.Equal("размер узнаю при очистке", SizeFormat.Human(-1));
        Assert.Equal("1,5 ГБ", SizeFormat.Human((long)(1.5 * (1L << 30))));
        Assert.Equal("230 МБ", SizeFormat.Human(230L << 20));
        Assert.Equal("0 Б", SizeFormat.Human(0));
    }

    [Fact]
    public void Running_candidates_come_from_id_and_name()
    {
        var c = RunningApps.Candidates(new WingetRow("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "1", "2", "winget"));
        Assert.Contains("Notepad++", c);
        var g = RunningApps.Candidates(new WingetRow("GitHub Desktop", "GitHub.GitHubDesktop", "1", "2", "winget"));
        Assert.Contains("GitHubDesktop", g);
        Assert.Contains("GitHub", g);
    }

    private sealed class Procs(params string[] names) : IProcessQuery
    {
        public bool IsRunning(string n) => names.Contains(n, StringComparer.OrdinalIgnoreCase);
        public bool AnyCommandLine(string f, Func<string, bool> m) => false;
    }

    [Fact]
    public void Running_app_detected_by_process_name()
    {
        var row = new WingetRow("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "1", "2", "winget");
        Assert.True(RunningApps.IsRunning(row, new Procs("notepad++")));
        Assert.False(RunningApps.IsRunning(row, new Procs("chrome")));
    }

    [Fact]
    public void Describe_failure_has_hex_code()
    {
        var t = WingetRunner.DescribeFailure(new WingetRun(unchecked((int)0x8A150011), "Found X\nInstaller failed with exit code: 1603\n", false, false));
        Assert.Contains("0x8A150011", t); Assert.Contains("1603", t);
        Assert.Contains("не требуется", WingetRunner.DescribeFailure(new WingetRun(WingetRunner.NoApplicableUpdate, "", false, false)));
    }

    [Fact]
    public void Duplicates_reports_progress_and_finds_copies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pult-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var data = new byte[1_200_000]; new Random(1).NextBytes(data);
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), data); File.WriteAllBytes(Path.Combine(dir, "b.bin"), data);
            var lines = new List<string>();
            var res = Duplicates.Find(new[] { dir }, Duplicates.OneMb, default, new SyncProgress(lines));
            Assert.Single(res);
            Assert.Contains(lines, l => l.Contains("Сверяю"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class SyncProgress(List<string> l) : IProgress<string> { public void Report(string v) => l.Add(v); }
}
