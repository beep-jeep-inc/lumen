using Pult.Core.Config;
using Pult.Core.Maintenance;
namespace Pult.Core.Tests;

public class WingetTests
{
    private const string Ru =
        "Имя                                              ИД                                     Версия               Доступно            Источник\r\n" +
        "---------------------------------------------------------------------------------------------------------------------------------------------\r\n" +
        "GitHub Desktop                                   GitHub.GitHubDesktop                   3.6.1                3.6.6               winget\r\n" +
        "Microsoft Windows Desktop Runtime - 8.0.22 (x64) Microsoft.DotNet.DesktopRuntime.8      8.0.22               8.0.31              winget\r\n" +
        "Notepad++ (64-bit x64)                           Notepad++.Notepad++                    8.9.2                8.9.8.1             winget\r\n" +
        "Python Launcher                                  Python.Launcher                        < 3.14.7             3.14.7              winget\r\n" +
        "Spotify                                          Spotify.Spotify                        1.2.79.427.g80eb4a07 1.3.1.234.g59d6bf59 winget\r\n" +
        "Помощник по установке Windows 11                 Microsoft.WindowsInstallationAssistant 1.4.19041.5003       1.4.19041.6448      winget\r\n" +
        "Доступны обновления: 32.\r\n" +
        "Несколько (1) пакетов содержат номера версий, которые невозможно определить. Используйте параметр --include-unknown для просмотра.\r\n";

    private const string En =
        "Name            Id                Version Available Source\n" +
        "-------------------------------------------------------------\n" +
        "Git             Git.Git           2.55.0  2.55.1    winget\n" +
        "Long Progr…     Some.Long.Program 1.0     2.0       winget\n" +
        "2 upgrades available.\n";

    [Fact]
    public void Parse_Russian_RowsAndSummariesSkipped()
    {
        var rows = WingetTable.Parse(Ru);
        Assert.Equal(6, rows.Count);
        Assert.Equal(new WingetRow("GitHub Desktop", "GitHub.GitHubDesktop", "3.6.1", "3.6.6", "winget"), rows[0]);
        Assert.Equal("Microsoft Windows Desktop Runtime - 8.0.22 (x64)", rows[1].Name);
        Assert.Equal("Microsoft.DotNet.DesktopRuntime.8", rows[1].Id);
        Assert.Equal("Notepad++ (64-bit x64)", rows[2].Name);
        Assert.Equal("< 3.14.7", rows[3].Version);
        Assert.Equal("1.2.79.427.g80eb4a07", rows[4].Version);
        Assert.Equal("1.3.1.234.g59d6bf59", rows[4].Available);
        Assert.Equal("Помощник по установке Windows 11", rows[5].Name);
        Assert.All(rows, r => Assert.Equal("winget", r.Source));
    }

    [Fact]
    public void Parse_English_AndTruncatedName()
    {
        var rows = WingetTable.Parse(En);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Git.Git", rows[0].Id);
        Assert.Equal("Long Progr…", rows[1].Name);
        Assert.Equal("Some.Long.Program", rows[1].Id);
    }

    [Fact]
    public void Parse_SpinnerPrefixAndGarbage_Safe()
    {
        Assert.Empty(WingetTable.Parse(""));
        Assert.Empty(WingetTable.Parse("Не найдено обновлений."));
        var rows = WingetTable.Parse("   -   \\   |   \r" + Ru);
        Assert.Equal(6, rows.Count);
    }

    [Theory]
    [InlineData("Microsoft.VCRedist.2015+.x64", UpdateGroup.Library)]
    [InlineData("Microsoft.DotNet.DesktopRuntime.8", UpdateGroup.Library)]
    [InlineData("Microsoft.DotNet.AspNetCore.8", UpdateGroup.Library)]
    [InlineData("Microsoft.GameInput", UpdateGroup.Library)]
    [InlineData("Oracle.JavaRuntimeEnvironment", UpdateGroup.Library)]
    [InlineData("EclipseAdoptium.Temurin.17.JRE", UpdateGroup.Library)]
    [InlineData("Microsoft.DotNet.SDK.6", UpdateGroup.Program)]
    [InlineData("Notepad++.Notepad++", UpdateGroup.Program)]
    public void Classify(string id, UpdateGroup expected) =>
        Assert.Equal(expected, Libraries.Classify(new WingetRow("x", id, "1", "2", "winget")));

    [Fact]
    public void Commands_HaveExpectedArgs()
    {
        var u = Winget.UpgradeCommand(@"C:\w\winget.exe", "Git.Git");
        Assert.Equal(@"C:\w\winget.exe", u.FileName);
        Assert.Equal("upgrade --id Git.Git --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", string.Join(' ', u.Args));
        Assert.Equal("install", Winget.InstallCommand("w", "A.B").Args[0]);
    }

    [Fact]
    public void NoUpdate_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pult-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"));
            var c = new PultConfig(); c.NoUpdate.Add("Git.Git");
            store.Save(c);
            Assert.Equal(new[] { "Git.Git" }, store.Load().NoUpdate);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact(Skip = "Живая проверка: winget upgrade на этом ПК")]
    public void Live_WingetUpgrade_Parses()
    {
        var w = Winget.Find(); Assert.NotNull(w);
        var psi = new System.Diagnostics.ProcessStartInfo(w!, "upgrade") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        var rows = WingetTable.Parse(text);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pult-live-winget.txt"), $"{rows.Count}\n" + string.Join("\n", rows));
        Assert.True(rows.Count > 0);
    }
}
