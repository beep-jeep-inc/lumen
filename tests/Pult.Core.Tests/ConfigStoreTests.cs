using Pult.Core.Config;
namespace Pult.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pult-test-" + Guid.NewGuid().ToString("N"), "Новая папка");
    private string File_ => Path.Combine(_dir, "config.json");
    public ConfigStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_dir)!, true); } catch { } }

    [Fact]
    public void RoundTrip_KeepsCyrillic_AndStartWithWindows()
    {
        var store = new ConfigStore(File_);
        var c = new PultConfig { StartWithWindows = true };
        c.DevRoots.Add(@"C:\Users\User\Desktop\Новая папка");
        c.NoUpdate.Add("Vendor.Программа");
        store.Save(c);

        Assert.Contains("Новая папка", File.ReadAllText(File_));   // не \u-экранирование
        var back = store.Load();
        Assert.Null(store.LoadWarning);
        Assert.True(back.StartWithWindows);
        Assert.Contains(@"C:\Users\User\Desktop\Новая папка", back.DevRoots);
        Assert.Contains("Vendor.Программа", back.NoUpdate);
    }

    [Fact]
    public void OldConfig_WithPrograms_KeepsConfigV1Copy_OnceOnly_AfterSave()
    {
        const string old = "{ \"SchemaVersion\": 1, \"Programs\": [{ \"Id\": \"zapret\" }], \"Profiles\": [{ \"Id\": \"p1\" }], \"NoUpdate\": [\"A.B\"] }";
        File.WriteAllText(File_, old);
        var v1 = Path.Combine(_dir, "config.v1.json");
        var store = new ConfigStore(File_);
        var c = store.Load();
        Assert.Equal(2, c.SchemaVersion);
        store.Save(c);
        Assert.True(File.Exists(v1)); Assert.Equal(old, File.ReadAllText(v1));
        Assert.DoesNotContain("Programs", File.ReadAllText(File_));            // новая версия карточки не пишет
        store.Save(store.Load()); store.Save(store.Load());                  // повторные загрузки не перезаписывают копию
        Assert.Equal(old, File.ReadAllText(v1));
    }

    [Fact]
    public void NewConfig_NoProgramsKeys_NoV1Copy()
    {
        var store = new ConfigStore(File_);
        store.Save(new PultConfig()); store.Load();
        Assert.False(File.Exists(Path.Combine(_dir, "config.v1.json")));
    }

    [Fact]
    public void NewConfig_StartWithWindows_OffByDefault() => Assert.False(new PultConfig().StartWithWindows);

    [Fact]
    public void OldConfig_WithProgramsProfilesTrayAutostart_Loads_StartWithWindowsOff_NotWrittenBack()
    {
        File.WriteAllText(File_, """
            {
              "SchemaVersion": 1, "Language": "ru", "TrayAutostart": true, "ZapretLastStrategy": "general.bat",
              "Programs": [{ "Id": "zapret", "Name": "zapret", "Kind": "Zapret", "Path": "C:\\z", "RunAsAdmin": true, "AdminNoPrompt": true }],
              "Profiles": [{ "Id": "p1", "Name": "Утро", "ProgramIds": ["zapret"] }],
              "Helper": { "Enabled": true, "Root": "C:\\helper" },
              "NoUpdate": ["Some.Package"], "DevRoots": ["D:\\code"], "DevAgeDays": 45
            }
            """);
        var store = new ConfigStore(File_);
        var c = store.Load();
        Assert.Null(store.LoadWarning);
        Assert.False(c.StartWithWindows);                         // старая галочка трея не включает автозапуск
        Assert.Equal(new[] { "Some.Package" }, c.NoUpdate);
        Assert.Equal(new[] { @"D:\code" }, c.DevRoots);
        Assert.Equal(45, c.DevAgeDays);

        store.Save(c);
        var text = File.ReadAllText(File_);
        Assert.DoesNotContain("Programs", text); Assert.DoesNotContain("Profiles", text); Assert.DoesNotContain("TrayAutostart", text);
        Assert.DoesNotContain("zapret", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("Helper", text);
    }

    [Fact]
    public void SecondSave_KeepsPreviousAsBak()
    {
        var store = new ConfigStore(File_);
        store.Save(new PultConfig { Language = "ru" });
        store.Save(new PultConfig { Language = "en" });
        Assert.True(File.Exists(File_ + ".bak"));
        Assert.Contains("\"ru\"", File.ReadAllText(File_ + ".bak"));
    }

    [Fact]
    public void CorruptedMain_FallsBackToBak_AndWarns()
    {
        var store = new ConfigStore(File_);
        store.Save(new PultConfig { Language = "ru" });
        store.Save(new PultConfig { Language = "en" });
        File.WriteAllText(File_, "{ \"Language\": \"en\", \"Progr");   // оборванная запись
        var c = store.Load();
        Assert.Equal("ru", c.Language);
        Assert.NotNull(store.LoadWarning);
        Assert.Single(Directory.GetFiles(_dir, "config.json.broken-*"));
    }

    [Fact]
    public void CorruptedMainAndNoBak_ReturnsEmpty_KeepsBrokenCopy()
    {
        File.WriteAllText(File_, "мусор");
        var store = new ConfigStore(File_);
        var c = store.Load();
        Assert.Empty(c.NoUpdate);
        Assert.NotNull(store.LoadWarning);
        Assert.Single(Directory.GetFiles(_dir, "config.json.broken-*"));
    }

    [Fact]
    public void MissingFile_ReturnsEmpty_NoWarning()
    {
        var store = new ConfigStore(File_);
        Assert.Empty(store.Load().NoUpdate);
        Assert.Null(store.LoadWarning);
    }
}
