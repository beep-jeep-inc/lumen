using Pult.Core.Startup;
namespace Pult.Core.Tests;

/// B5: пометка «файл не найден» и «важные» записи автозагрузки.
public class StartupHealthTests
{
    private static StartupEntry E(string name, string? exe, string desc = "", string pub = "", string cmd = "", StartupSource src = StartupSource.HkcuRun, bool self = false) =>
        new(src, name, cmd.Length > 0 ? cmd : exe ?? "", exe, true, self, desc, pub);

    // ---- файл не найден ----

    [Fact]
    public void Missing_FullLocalExePath_NotExisting_IsMarked()
    {
        Assert.True(StartupHealth.IsMissing(E("A", @"C:\Apps\a.exe"), _ => false));
        Assert.False(StartupHealth.IsMissing(E("A", @"C:\Apps\a.exe"), _ => true));
        Assert.True(StartupHealth.IsMissing(E("L", @"D:\x\y.LNK"), _ => false));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("rundll32")] [InlineData(@"\\server\share\a.exe")] [InlineData(@"%ProgramFiles%\a.exe")]
    [InlineData(@"C:\Program")] [InlineData(@"C:\Apps\readme.txt")] [InlineData(@"C:\Apps\noext")] [InlineData(@"relative\a.exe")] [InlineData("C:")] [InlineData("a\0b.exe")]
    public void Missing_UncertainCases_AreNeverMarked(string? exe) => Assert.False(StartupHealth.IsMissing(E("A", exe), _ => false));

    [Fact]
    public void Missing_ExistsThrows_NotMarked() =>
        Assert.False(StartupHealth.IsMissing(E("A", @"C:\a.exe"), _ => throw new UnauthorizedAccessException()));

    [Fact]
    public void Missing_NeverTouchesTheFileSystemItself_OnlyAsksTheFunction()
    {
        var asked = new List<string>();
        StartupHealth.IsMissing(E("A", @"C:\Apps\a.exe"), p => { asked.Add(p); return true; });
        Assert.Equal(new[] { @"C:\Apps\a.exe" }, asked);
        asked.Clear();
        StartupHealth.IsMissing(E("A", @"\\srv\a.exe"), p => { asked.Add(p); return true; });
        Assert.Empty(asked);                                                           // сетевой путь не запрашиваем: можно повесить проверку
    }

    private sealed class Fake : IStartupAccess
    {
        public IReadOnlyList<StartupRaw> ReadEntries(StartupSource s) => s == StartupSource.HkcuRun
            ? new[] { new StartupRaw("Gone", @"""C:\nowhere\gone.exe"" -x"), new StartupRaw("Here", @"""C:\here\here.exe"""), new StartupRaw("Pult", @"""C:\own\Pult.exe""") }
            : Array.Empty<StartupRaw>();
        public byte[]? ReadApproved(StartupSource s, string name) => null;
        public void WriteApproved(StartupSource s, string name, byte[] flag) { }
    }

    [Fact]
    public void Scan_MarksMissing_NotSelf_NothingWhenNoFunction()
    {
        var list = StartupScanner.Scan(new Fake(), null, null, @"C:\own\Pult.exe", p => p.Contains("here.exe"));
        Assert.True(list.Single(e => e.Name == "Gone").Missing);
        Assert.False(list.Single(e => e.Name == "Here").Missing);
        Assert.False(list.Single(e => e.Name == "Pult").Missing);                        // Пульт сам — не помечаем (его файл «не найден» только в тесте)
        Assert.All(StartupScanner.Scan(new Fake()), e => Assert.False(e.Missing));        // без функции проверки пометок нет
    }

    // ---- важные ----

    [Theory]
    [InlineData("SecurityHealth", @"C:\Windows\system32\SecurityHealthSystray.exe", "Windows Security notification icon", "Microsoft Corporation", "защита Windows")]
    [InlineData("Windows Defender", null, "", "", "защита Windows")]
    [InlineData("RtkAudUService", @"C:\x\RtkAudUService64.exe", "Realtek HD Audio Universal Service", "Realtek Semiconductor", "драйвер звука")]
    [InlineData("Nahimic", null, "", "", "драйвер звука")]
    [InlineData("NvBackend", @"C:\x\NvBackend.exe", "NVIDIA Update Backend", "NVIDIA Corporation", "драйвер видеокарты")]
    [InlineData("RadeonSoftware", null, "AMD Software", "Advanced Micro Devices", "драйвер видеокарты")]
    [InlineData("SynTPEnh", @"C:\x\SynTPEnh.exe", "Synaptics TouchPad Enhancements", "Synaptics Incorporated", "драйвер тачпада")]
    [InlineData("ETDCtrl", null, "ETD Control Center", "ELAN Microelectronics", "драйвер тачпада")]
    [InlineData("LenovoVantage", null, "Lenovo Vantage", "Lenovo", "программа производителя")]
    [InlineData("HP Hotkey", null, "", "HP Inc.", "программа производителя")]
    [InlineData("LGHUB", @"C:\x\lghub.exe", "G HUB", "Logitech", "драйвер мыши или клавиатуры")]
    public void Important_KnownKinds(string name, string? exe, string desc, string pub, string kind)
    {
        var i = StartupHealth.Important(E(name, exe, desc, pub));
        Assert.NotNull(i); Assert.Equal(kind, i!.Kind); Assert.False(string.IsNullOrWhiteSpace(i.Warning));
    }

    [Fact]
    public void Important_DriverStoreProgram_IsAlwaysImportant() =>
        Assert.Equal("драйвер устройства", StartupHealth.Important(E("SomeSvc", @"C:\Windows\System32\DriverStore\FileRepository\foo.inf_amd64_1\foo.exe"))!.Kind);

    [Theory]
    [InlineData("Discord", @"C:\Users\u\AppData\Local\Discord\Update.exe", "Discord", "Discord Inc.")]
    [InlineData("Steam", @"C:\Program Files (x86)\Steam\steam.exe", "Steam Client Bootstrapper", "Valve Corporation")]
    [InlineData("Telegram", @"C:\Telegram.exe", "Telegram Desktop", "Telegram FZ-LLC")]
    [InlineData("AdobeAAMUpdater", @"C:\Adobe\Updater.exe", "Adobe Updater Startup Utility", "Adobe Inc.")]
    [InlineData("MSIExec", @"C:\x\a.exe", "", "")]                                      // «MSI» внутри слова — не производитель
    [InlineData("Spotify", @"C:\x\Spotify.exe", "Spotify", "Spotify AB")]
    public void Important_OrdinaryPrograms_AreNot(string name, string exe, string desc, string pub) => Assert.Null(StartupHealth.Important(E(name, exe, desc, pub)));

    [Fact]
    public void Important_SelfIsNeverImportant() => Assert.Null(StartupHealth.Important(E("Pult", @"C:\own\Pult.exe", self: true)));

    [Fact]
    public void ConfirmText_NamesTheEntry_AndWarns()
    {
        var e = E("SecurityHealth", null);
        var t = StartupHealth.ConfirmText(e, StartupHealth.Important(e)!);
        Assert.Contains("«SecurityHealth»", t); Assert.Contains("защиты Windows", t); Assert.Contains("Выключить всё равно?", t);
    }
}
