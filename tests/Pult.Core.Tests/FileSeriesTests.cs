using Pult.Core.Cleanup;
namespace Pult.Core.Tests;

/// B4: серии крупных файлов одной строкой и пояснения к системным папкам.
public class FileSeriesTests
{
    private const string D = @"C:\Users\u\Videos";
    private static FileSize F(string name, long b = 2_000_000_000, string dir = D) => new(Path.Combine(dir, name), b);

    [Theory]
    [InlineData("media-001.zip", "media-", 1, ".zip")]
    [InlineData("media-0015.zip", "media-", 15, ".zip")]
    [InlineData("big.part1.rar", "big.part", 1, ".rar")]
    [InlineData("big.part02.rar", "big.part", 2, ".rar")]
    [InlineData("backup.7z.003", "backup.7z.", 3, "")]
    [InlineData("data.z01", "data.z", 1, "")]
    [InlineData("file2023-001.zip", "file2023-", 1, ".zip")]
    public void Parse_NumberedNames(string name, string pre, int n, string suf) => Assert.Equal((pre, n, suf), FileSeries.Parse(name));

    [Theory]
    [InlineData("movie.mkv")] [InlineData("2023.zip")] [InlineData("12345.iso")] [InlineData("README")] [InlineData("a-12345.zip")] [InlineData("")]
    public void Parse_NotNumbered(string name) => Assert.Null(FileSeries.Parse(name));

    [Fact]
    public void Split_FifteenArchives_BecomeOneSeries_OthersStaySingles()
    {
        var files = Enumerable.Range(1, 15).Select(i => F($"media-abc-{i:000}.zip")).Append(F("Win11.iso", 6_000_000_000)).Append(F("clip.mkv", 4_000_000_000)).ToList();
        var (series, singles) = FileSeries.Split(files);
        var s = Assert.Single(series);
        Assert.Equal(15, s.Count); Assert.Equal(30_000_000_000, s.Bytes); Assert.Equal("media-abc-….zip", s.Title); Assert.Equal(D, s.Folder);
        Assert.Equal(2, singles.Count);
        Assert.Equal(15 + 2, series.Sum(x => x.Count) + singles.Count);               // ни один файл не потерян и не удвоен
    }

    [Fact]
    public void Split_TwoFilesOnly_NotASeries() => Assert.Empty(FileSeries.Split(new[] { F("a-001.zip"), F("a-002.zip") }).Series);

    [Fact]
    public void Split_SameNumberRepeated_NotASeries() =>
        Assert.Empty(FileSeries.Split(new[] { F("a-001.zip"), F("a-001.zip", dir: D + "2"), F("a-001.zip", dir: D + "3") }).Series);      // три папки — это не серия

    [Fact]
    public void Split_DifferentExtensionsOrFolders_SeparateGroups()
    {
        var files = new[] { F("a-1.zip"), F("a-2.zip"), F("a-3.zip"), F("a-1.rar"), F("a-2.rar"), F("a-3.rar"), F("b-1.zip", dir: @"D:\x"), F("b-2.zip", dir: @"D:\x") };
        var (series, singles) = FileSeries.Split(files);
        Assert.Equal(2, series.Count); Assert.Equal(2, singles.Count);
    }

    [Fact]
    public void Split_RarParts_SortedByNumber()
    {
        var (series, _) = FileSeries.Split(new[] { F("x.part10.rar"), F("x.part2.rar"), F("x.part1.rar") });
        var s = Assert.Single(series);
        Assert.Equal(new[] { "x.part1.rar", "x.part2.rar", "x.part10.rar" }, s.Files.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal("x.part….rar", s.Title);
    }

    [Fact]
    public void Split_CaseInsensitive_AndEmpty()
    {
        var (series, _) = FileSeries.Split(new[] { F("A-1.ZIP"), F("a-2.zip"), F("a-3.zip") });
        Assert.Single(series);
        var (s0, f0) = FileSeries.Split(Array.Empty<FileSize>());
        Assert.Empty(s0); Assert.Empty(f0);
    }

    [Theory]
    [InlineData(@"C:\Windows\WinSxS", "Хранилище компонентов")]
    [InlineData(@"C:\Windows\WinSxS\", "Хранилище компонентов")]
    [InlineData(@"c:\program files\windowsapps", "Microsoft Store")]
    [InlineData(@"C:\Program Files\WindowsApps.tmp", "Microsoft Store")]
    [InlineData(@"C:\Windows\Installer", "установщиков")]
    [InlineData(@"C:\Windows\SoftwareDistribution", "обновления Windows")]
    [InlineData(@"C:\hiberfil.sys", "гибернации")]
    [InlineData(@"C:\pagefile.sys", "подкачки")]
    public void SystemFolders_AreExplained(string path, string contains) => Assert.Contains(contains, SystemFolderNotes.Explain(path));

    [Theory]
    [InlineData(@"C:\Users\u\Installer")] [InlineData(@"D:\Games\WinSxS")] [InlineData(@"C:\Users\u\Downloads")] [InlineData("")] [InlineData(@"C:\")]
    public void OtherFolders_NotExplained(string path) => Assert.Null(SystemFolderNotes.Explain(path));

    [Fact]
    public void Explanations_NeverSuggestManualDeleting()
    {
        foreach (var p in new[] { @"C:\Windows\WinSxS", @"C:\Program Files\WindowsApps", @"C:\Program Files\WindowsApps.tmp", @"C:\Windows\Installer", @"C:\Windows\SoftwareDistribution", @"C:\hiberfil.sys", @"C:\pagefile.sys" })
        {
            var t = SystemFolderNotes.Explain(p)!;
            Assert.Matches("не (трогай|удаляй)|не удаляй вручную|вручную не", t);
        }
    }
}
