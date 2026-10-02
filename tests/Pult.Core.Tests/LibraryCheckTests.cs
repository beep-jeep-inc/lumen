using Pult.Core.Maintenance;
namespace Pult.Core.Tests;

public class LibraryCheckTests
{
    private static readonly (string, string?)[] Full =
    {
        ("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38.33135", "14.38.33135"),
        ("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.38.33135", "14.38.33135"),
    };
    private static bool AllFiles(string p) => true;
    private static bool NoFiles(string p) => false;

    [Fact]
    public void AllPresent_AllInstalled_NoEol()
    {
        var r = LibraryCheck.Evaluate(Full, new[] { "Microsoft.WindowsDesktop.App 8.0.22 [C:\\x]" }, AllFiles);
        Assert.Equal(3, r.Count);
        Assert.All(r, s => Assert.True(s.Installed));
        Assert.All(r, s => Assert.False(s.EndOfLife));
        Assert.Equal("14.38.33135", r.First(s => s.Key == "vcredist-x64").Version);
    }

    [Fact]
    public void MissingX86_OffersInstall()
    {
        var r = LibraryCheck.Evaluate(Full.Take(1), Array.Empty<string>(), AllFiles);
        var x86 = r.Single(s => s.Key == "vcredist-x86");
        Assert.False(x86.Installed);
        Assert.Equal("Microsoft.VCRedist.2015+.x86", x86.WingetId);
        Assert.True(r.Single(s => s.Key == "vcredist-x64").Installed);
    }

    [Fact]
    public void OldVcNames_2015to2019_Count()
    {
        var r = LibraryCheck.Evaluate(new (string, string?)[]
        {
            ("Microsoft Visual C++ 2015-2019 Redistributable (x64) - 14.29.30133", "14.29.30133"),
            ("Microsoft Visual C++ 2017 Redistributable (x86) - 14.16.27033", "14.16.27033"),
        }, Array.Empty<string>(), AllFiles);
        Assert.True(r.Single(s => s.Key == "vcredist-x64").Installed);
        Assert.True(r.Single(s => s.Key == "vcredist-x86").Installed);
    }

    [Fact]
    public void V14Naming_AndHighestDotnetVersionFromName()
    {
        var r = LibraryCheck.Evaluate(new (string, string?)[]
        {
            ("Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "14.51.36247.0"),
            ("Microsoft Visual C++ v14 Redistributable (x86) - 14.51.36247", "14.51.36247.0"),
            ("Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.51.36247", "14.51.36247"),
            ("Microsoft Windows Desktop Runtime - 6.0.3 (x64)", "48.15.37635"),
            ("Microsoft Windows Desktop Runtime - 6.0.36 (x64)", "48.144.23186"),
        }, Array.Empty<string>(), AllFiles);
        Assert.True(r.Single(s => s.Key == "vcredist-x64").Installed);
        Assert.True(r.Single(s => s.Key == "vcredist-x86").Installed);
        Assert.Equal("6.0.36", r.Single(s => s.Key == "dotnet-6").Version);
    }

    [Fact]
    public void NoDirectXFile_OffersDirectX()
    {
        var r = LibraryCheck.Evaluate(Full, Array.Empty<string>(), NoFiles);
        var dx = r.Single(s => s.Key == "directx");
        Assert.False(dx.Installed);
        Assert.Equal("Microsoft.DirectX", dx.WingetId);
    }

    [Fact]
    public void Dotnet7FromCli_IsEol_Dotnet6FromRegistry_IsEol()
    {
        var r = LibraryCheck.Evaluate(
            Full.Concat(new (string, string?)[] { ("Microsoft Windows Desktop Runtime - 6.0.36 (x86)", "6.0.36") }),
            new[] { "Microsoft.NETCore.App 7.0.20 [C:\\x]", "Microsoft.WindowsDesktop.App 7.0.20 [C:\\x]", "Microsoft.WindowsDesktop.App 8.0.22 [C:\\x]" }, AllFiles);
        var n7 = r.Single(s => s.Key == "dotnet-7");
        Assert.True(n7.EndOfLife); Assert.Equal("7.0.20", n7.Version);
        Assert.Contains("исправлений безопасности", n7.Explain);
        Assert.True(r.Single(s => s.Key == "dotnet-6").EndOfLife);
        Assert.DoesNotContain(r, s => s.Key == "dotnet-8");
    }

    [Fact]
    public void GameInputAndJava_ShownWithVersion()
    {
        var r = LibraryCheck.Evaluate(Full.Concat(new (string, string?)[]
        {
            ("Microsoft GameInput", "3.5.270.0"), ("Java 8 Update 481 (64-bit)", "8.0.4810.10"), ("Java Auto Updater", "2.8"),
        }), Array.Empty<string>(), AllFiles);
        Assert.Equal("3.5.270.0", r.Single(s => s.Key == "gameinput").Version);
        Assert.Equal("8.0.4810.10", r.Single(s => s.Key == "java").Version);
    }

    [Fact(Skip = "Живая проверка: библиотеки на этом ПК")]
    public void Live_ForThisPc()
    {
        var r = LibraryCheck.ForThisPc();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pult-live-libs.txt"), string.Join("\n", r));
        Assert.NotEmpty(r);
    }

    [Theory]
    [InlineData(0, 0, "Всё на месте, обновлений нет.")]
    [InlineData(2, 0, "Можно установить или обновить: 2.")]
    [InlineData(0, 1, "Обновлений нет. 1 версия больше не получает исправлений безопасности")]
    [InlineData(0, 2, "Обновлений нет. 2 версии больше не получают исправлений безопасности")]
    [InlineData(0, 5, "Обновлений нет. 5 версий больше не получают исправлений безопасности")]
    [InlineData(3, 2, "Можно установить или обновить: 3. 2 версии больше не получают исправлений безопасности")]
    public void Summary_AccountsForEndOfSupport(int actionable, int eol, string start) => Assert.StartsWith(start, LibraryCheck.Summary(actionable, eol));

    [Fact]
    public void Summary_EndOfSupport_NeverSaysEverythingIsFine() => Assert.DoesNotContain("Всё на месте", LibraryCheck.Summary(0, 2));

    [Fact]
    public void EndOfSupport_Explanation_SaysOnlyForOldPrograms_NoUninstallAdvice()
    {
        var r = LibraryCheck.Evaluate(new (string, string?)[] { ("Microsoft Windows Desktop Runtime - 6.0.36 (x64)", "6.0.36") }, Array.Empty<string>(), _ => true);
        var n6 = r.First(x => x.Key == "dotnet-6");
        Assert.Contains("старым программам", n6.Explain); Assert.Contains("не знаешь — не удаляй", n6.Explain);
    }
}
