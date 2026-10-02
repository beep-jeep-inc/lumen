using Microsoft.Win32;
using Pult.Admin;
using Pult.Core.Admin;
using Pult.Core.Maintenance;
using Pult.Core.Startup;
namespace Pult.Core.Tests;

/// Подставной доступ к автозагрузке: записи и флаги в памяти; запоминает, что писали.
internal sealed class FakeStartup : IStartupAccess
{
    public Dictionary<StartupSource, List<StartupRaw>> Entries = new();
    public Dictionary<(StartupSource, string), byte[]> Approved = new();
    public List<(StartupSource, string, byte[])> Writes = new();
    public bool FailRead;
    public IReadOnlyList<StartupRaw> ReadEntries(StartupSource s) =>
        FailRead ? throw new UnauthorizedAccessException("нет доступа") : Entries.TryGetValue(s, out var l) ? l : new List<StartupRaw>();
    public byte[]? ReadApproved(StartupSource s, string name) => Approved.TryGetValue((s, name.ToLowerInvariant()), out var b) ? b : null;
    public void WriteApproved(StartupSource s, string name, byte[] flag) { Writes.Add((s, name, flag)); Approved[(s, name.ToLowerInvariant())] = flag; }
    public FakeStartup With(StartupSource s, string name, string cmd) { if (!Entries.TryGetValue(s, out var l)) Entries[s] = l = new(); l.Add(new(name, cmd)); return this; }
}

public class StartupTests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-st-" + Guid.NewGuid().ToString("N"));
    private string Res => Path.Combine(_d, "res");
    private string Req => Path.Combine(_d, "user", "admin.req");
    public StartupTests() { Directory.CreateDirectory(Path.Combine(_d, "user")); Directory.CreateDirectory(Res); }
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    // ---- флаг StartupApproved ----

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void IsEnabled_FromFirstByte(byte[]? data, bool on) => Assert.Equal(on, StartupApproval.IsEnabled(data));

    [Fact]
    public void Flag_On_Is12Bytes_02_AndZeros()
    {
        var f = StartupApproval.Flag(true, DateTime.UtcNow);
        Assert.Equal(12, f.Length);
        Assert.Equal(new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, f);
    }

    [Fact]
    public void Flag_Off_Is12Bytes_03_AndFileTime()
    {
        var t = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var f = StartupApproval.Flag(false, t);
        Assert.Equal(12, f.Length);
        Assert.Equal(3, f[0]); Assert.Equal(0, f[1]); Assert.Equal(0, f[2]); Assert.Equal(0, f[3]);
        Assert.Equal(t.ToFileTimeUtc(), BitConverter.ToInt64(f, 4));
        Assert.False(StartupApproval.IsEnabled(f));
    }

    // ---- разбор команды ----

    [Theory]
    [InlineData(@"""C:\Program Files\Foo\foo.exe"" --silent", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Tools\bar.exe /min", @"C:\Tools\bar.exe")]
    [InlineData(@"C:\Program Files\Baz\baz.EXE -x", @"C:\Program Files\Baz\baz.EXE")]
    [InlineData(@"notepad", "notepad")]
    [InlineData(@"notepad.bat arg", @"notepad.bat")]
    [InlineData("", null)]
    [InlineData(@"""unclosed", null)]
    public void ExePath_Parses(string cmd, string? expected) => Assert.Equal(expected, StartupCommand.ExePath(cmd));

    [Fact]
    public void ExePath_ExpandsVariablesFirst() =>
        Assert.Equal(@"C:\Windows\system32\x.exe", StartupCommand.ExePath(@"%windir%\system32\x.exe", s => s.Replace("%windir%", @"C:\Windows")));

    [Fact]
    public void Shorten_KeepsShortPaths_CutsLongOnes()
    {
        Assert.Equal(@"C:\a\b.exe", StartupCommand.Shorten(@"C:\a\b.exe"));
        var s = StartupCommand.Shorten(@"C:\Users\User\AppData\Local\Programs\SomeVeryLongProgramName\Application\bin\prog.exe", 40);
        Assert.True(s.Length <= 42);
        Assert.StartsWith(@"C:\…\", s);
        Assert.EndsWith(@"prog.exe", s);
    }

    // ---- список ----

    [Fact]
    public void Scan_ReadsEntriesAndState_MissingFlagMeansOn()
    {
        var fake = new FakeStartup().With(StartupSource.HkcuRun, "Discord", @"""C:\d\Discord.exe"" -x").With(StartupSource.HkcuRun, "Steam", @"""C:\s\steam.exe""")
                                    .With(StartupSource.HklmRun, "SecurityHealth", @"C:\w\sh.exe");
        fake.Approved[(StartupSource.HkcuRun, "steam")] = StartupApproval.Flag(false, DateTime.UtcNow);
        var list = StartupScanner.Scan(fake, p => ("Описание " + Path.GetFileName(p), "Издатель"), null, @"C:\own\Pult.exe");
        Assert.Equal(3, list.Count);
        Assert.True(list.Single(e => e.Name == "Discord").Enabled);
        Assert.False(list.Single(e => e.Name == "Steam").Enabled);
        Assert.Equal("Описание Discord.exe", list.Single(e => e.Name == "Discord").Description);
        Assert.Equal(StartupSource.HklmRun, list.Single(e => e.Name == "SecurityHealth").Source);
    }

    [Fact]
    public void Scan_MarksPultItself_ByNameOrPath()
    {
        var fake = new FakeStartup().With(StartupSource.HkcuRun, "Pult", @"""C:\x\Pult.exe"" --minimized")
                                    .With(StartupSource.HkcuRun, "Other", @"""C:\own\Pult.exe""")
                                    .With(StartupSource.HklmRun, "Pult", @"""C:\y\z.exe""");        // HKLM «Pult» — чужая запись, не наш ключ
        var list = StartupScanner.Scan(fake, null, null, @"C:\own\Pult.exe");
        Assert.True(list.Single(e => e.Source == StartupSource.HkcuRun && e.Name == "Pult").IsSelf);
        Assert.True(list.Single(e => e.Name == "Other").IsSelf);
        Assert.False(list.Single(e => e.Source == StartupSource.HklmRun).IsSelf);
    }

    [Fact]
    public void Scan_UnreadableSource_IsSkipped_NoCrash()
    {
        var list = StartupScanner.Scan(new FakeStartup { FailRead = true });
        Assert.Empty(list);
    }

    [Fact]
    public void Scan_FolderItems_HaveNoCommandParsing()
    {
        var fake = new FakeStartup().With(StartupSource.UserFolder, "OneNote.lnk", @"C:\s\OneNote.lnk");
        var e = Assert.Single(StartupScanner.Scan(fake));
        Assert.Null(e.ExePath);
        Assert.True(e.Enabled);
    }

    // ---- переключение (общая логика Пульта и помощника) ----

    [Fact]
    public void Toggle_Off_WritesOnlyTheFlag_AndKeepsTheEntry()
    {
        var fake = new FakeStartup().With(StartupSource.HkcuRun, "Discord", "cmd");
        var r = StartupToggler.Toggle(fake, StartupSource.HkcuRun, "discord", false, DateTime.UtcNow, StartupSources.UserSources);
        Assert.True(r.Ok);
        var w = Assert.Single(fake.Writes);
        Assert.Equal("Discord", w.Item2);                                         // имя берётся из списка, а не из запроса
        Assert.Equal(3, w.Item3[0]);
        Assert.Single(fake.Entries[StartupSource.HkcuRun]);                      // запись на месте
        var on = StartupToggler.Toggle(fake, StartupSource.HkcuRun, "Discord", true, DateTime.UtcNow, StartupSources.UserSources);
        Assert.True(on.Ok);
        Assert.Equal(2, fake.Writes[1].Item3[0]);
    }

    [Fact]
    public void Toggle_MissingEntry_Refused_NothingWritten()
    {
        var fake = new FakeStartup().With(StartupSource.HklmRun, "A", "x");
        var r = StartupToggler.Toggle(fake, StartupSource.HklmRun, "B", false, DateTime.UtcNow, StartupSources.HelperSources);
        Assert.False(r.Ok);
        Assert.Equal(StartupToggler.MissingCode, r.Code);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Toggle_SourceNotAllowed_Refused()
    {
        var fake = new FakeStartup().With(StartupSource.HkcuRun, "A", "x").With(StartupSource.HklmRun, "A", "x");
        Assert.False(StartupToggler.Toggle(fake, StartupSource.HkcuRun, "A", false, DateTime.UtcNow, StartupSources.HelperSources).Ok);     // помощник не трогает HKCU
        Assert.False(StartupToggler.Toggle(fake, StartupSource.HklmRun, "A", false, DateTime.UtcNow, StartupSources.UserSources).Ok);       // Пульт сам не пишет в HKLM
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Toggle_Pult_Refused()
    {
        var fake = new FakeStartup().With(StartupSource.HkcuRun, "Pult", "x");
        var r = StartupToggler.Toggle(fake, StartupSource.HkcuRun, "Pult", false, DateTime.UtcNow, StartupSources.UserSources);
        Assert.False(r.Ok);
        Assert.Contains("Lumen", r.Message);
        Assert.Empty(fake.Writes);
    }

    // ---- аргументы помощника ----

    [Theory]
    [InlineData("HklmRun|off|Realtek")]
    [InlineData("HklmRun32|on|Adobe Updater")]
    [InlineData("CommonFolder|off|My App.lnk")]
    [InlineData("HklmRun|off|имя|с|палками")]
    public void Arg_Valid_Parses(string arg)
    {
        Assert.True(StartupArg.TryParseForHelper(arg, out _, out _, out _, out _));
    }

    [Fact]
    public void Arg_Roundtrip()
    {
        Assert.True(StartupArg.TryParseForHelper(StartupArg.Build(StartupSource.HklmRun32, true, "A|B"), out var s, out var on, out var n, out _));
        Assert.Equal(StartupSource.HklmRun32, s); Assert.True(on); Assert.Equal("A|B", n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HklmRun")]
    [InlineData("HklmRun|off")]
    [InlineData("HklmRun|off|")]                                   // пустое имя
    [InlineData("HkcuRun|off|X")]                                  // пользовательский источник — не для помощника
    [InlineData("UserFolder|off|X.lnk")]
    [InlineData("hklmrun|off|X")]                                  // регистр
    [InlineData("1|off|X")]                                        // число вместо имени источника
    [InlineData("0|off|X")]
    [InlineData("HklmRun|ON|X")]
    [InlineData("HklmRun|toggle|X")]
    [InlineData("HklmRun|1|X")]
    [InlineData("HklmRun|off|X\nY")]                               // управляющие символы
    [InlineData("HklmRun|off|X\0Y")]
    [InlineData("HklmRun|off|X\tY")]
    [InlineData("HklmRun|off| X")]                                 // пробел по краю
    [InlineData("CommonFolder|off|..")]
    [InlineData("CommonFolder|off|a\\b.lnk")]                      // путь вместо имени файла
    [InlineData("CommonFolder|off|C:\\Windows\\x.exe")]
    [InlineData("CommonFolder|off|../x.lnk")]
    public void Arg_Invalid_Rejected(string arg)
    {
        Assert.False(StartupArg.TryParseForHelper(arg, out _, out _, out _, out _));
    }

    [Fact]
    public void Arg_NameTooLong_Rejected()
    {
        Assert.True(StartupArg.TryParseForHelper("HklmRun|off|" + new string('a', 260), out _, out _, out _, out _));
        Assert.False(StartupArg.TryParseForHelper("HklmRun|off|" + new string('a', 261), out _, out _, out _, out _));
        Assert.False(StartupArg.TryParseForHelper("HklmRun|off|" + new string('a', 5000), out _, out _, out _, out _));
        Assert.False(StartupArg.TryParseForHelper(null, out _, out _, out _, out _));
    }

    [Fact]
    public void SourceIds_AreExactNames()
    {
        foreach (var s in StartupSources.All) { Assert.True(StartupSources.TryParse(StartupSources.Id(s), out var back)); Assert.Equal(s, back); }
        Assert.False(StartupSources.TryParse("2", out _)); Assert.False(StartupSources.TryParse(null, out _));
        Assert.False(StartupSources.TryParse("hklmrun", out _));
    }

    // ---- политика помощника ----

    private static Decision Decide(string arg) => AdminPolicy.Decide(AdminRequest.New(AdminAction.StartupToggle, arg));

    [Fact]
    public void Policy_StartupToggle_AllowsOnlyValidArgs()
    {
        Assert.True(Decide("HklmRun|off|Realtek").Allowed);
        Assert.True(Decide("CommonFolder|on|App.lnk").Allowed);
        Assert.False(Decide("").Allowed);
        Assert.False(Decide("HkcuRun|off|X").Allowed);
        Assert.False(Decide("HklmRun|off|C:\\evil.exe\n").Allowed);
        Assert.False(Decide("HklmRun|delete|X").Allowed);
        Assert.True(Decide(@"HklmRun|off|X"" & calc").Allowed);   // кавычки допустимы в имени значения реестра: имя всё равно сверят со списком, а исполнять помощник ничего не будет
    }

    [Fact]
    public void Protocol_StartupToggle_ParsesFromJson_AndUnknownNumbersStillDenied()
    {
        var r = AdminRequest.New(AdminAction.StartupToggle, "HklmRun|off|X");
        Assert.Equal(AdminAction.StartupToggle, AdminRequest.Parse(r.ToJson())!.Action);
        Assert.True(Decide("HklmRun|off|X").Allowed);
        Assert.False(AdminPolicy.Decide(new AdminRequest("0123456789abcdef0123456789abcdef", (AdminAction)4, "HklmRun|off|X")).Allowed);
    }

    // ---- помощник: RequestProcessor ----

    private readonly List<string> _asked = new();
    private AdminResponse Process(AdminRequest req, FakeStartup fake, RequestProcessorTests.Exec? exec = null, ConfirmAnswer answer = ConfirmAnswer.Yes, bool noConfirm = false)
    {
        File.WriteAllText(Req, req.ToJson());
        Func<string, ConfirmAnswer>? confirm = noConfirm ? null : t => { _asked.Add(t); return answer; };
        return new RequestProcessor(Req, Res, null, exec ?? new RequestProcessorTests.Exec(), fake, null, confirm).ProcessOnce()!;
    }

    [Fact]
    public void Helper_Toggle_No_NothingWritten_Declined()
    {
        var fake = new FakeStartup().With(StartupSource.HklmRun, "Realtek", @"C:.exe");
        var res = Process(AdminRequest.New(AdminAction.StartupToggle, "HklmRun|off|Realtek"), fake, answer: ConfirmAnswer.No);
        Assert.False(res.Ok); Assert.Equal(AdminCodes.Declined, res.Code); Assert.Empty(fake.Writes);
        Assert.Contains("Realtek", Assert.Single(_asked));
    }

    [Fact]
    public void Helper_Toggle_Timeout_DeclinedTimeout_NothingWritten()
    {
        var fake = new FakeStartup().With(StartupSource.HklmRun, "Realtek", @"C:.exe");
        var res = Process(AdminRequest.New(AdminAction.StartupToggle, "HklmRun|on|Realtek"), fake, answer: ConfirmAnswer.Timeout);
        Assert.False(res.Ok); Assert.Equal(AdminCodes.DeclinedTimeout, res.Code); Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Helper_Toggle_MissingOrBadArg_NoWindow()
    {
        var fake = new FakeStartup().With(StartupSource.HklmRun, "A", "x");
        Assert.False(Process(AdminRequest.New(AdminAction.StartupToggle, "HklmRun|off|Nope"), fake).Ok);
        Assert.False(Process(AdminRequest.New(AdminAction.StartupToggle, "HkcuRun|off|A"), fake).Ok);
        Assert.Empty(_asked); Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Helper_Toggle_WithoutConfirmDelegate_Refused_NothingWritten()
    {
        var fake = new FakeStartup().With(StartupSource.HklmRun, "A", "x");
        var res = Process(AdminRequest.New(AdminAction.StartupToggle, "HklmRun|off|A"), fake, noConfirm: true);
        Assert.False(res.Ok); Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Toggler_NoConfirm_UserSources_WritesAsBefore()
    {
        var fake = new FakeStartup().With(StartupSource.HkcuRun, "discord", "x");
        Assert.True(StartupToggler.Toggle(fake, StartupSource.HkcuRun, "discord", false, DateTime.UtcNow, StartupSources.UserSources).Ok);
        Assert.Single(fake.Writes);
    }

    [Fact]
    public void ConfirmText_StripsBidiAndNewlines_ShortensLongCommand()
    {
        var raw = new StartupRaw("Evil\u202Eexe.\ntxt\u2028x", @"C:\Program Files\" + new string('a', 400) + ".exe");
        var text = StartupArg.ConfirmText(StartupSource.HklmRun, false, raw);
        Assert.False(text.Contains('\u202E')); Assert.False(text.Contains('\u2028'));   // \u043F\u043E\u0440\u044F\u0434\u043A\u043E\u0432\u043E\u0435 \u0441\u0440\u0430\u0432\u043D\u0435\u043D\u0438\u0435: \u043A\u0443\u043B\u044C\u0442\u0443\u0440\u043D\u043E\u0435 \u00AB\u043D\u0435 \u0432\u0438\u0434\u0438\u0442\u00BB \u0442\u0430\u043A\u0438\u0435 \u0441\u0438\u043C\u0432\u043E\u043B\u044B
        Assert.Contains("«Evilexe.txtx»", text);
        Assert.StartsWith("Выключить", text);
        var cmdLine = text.Split('\n').Single(l => l.StartsWith("Команда: "));
        Assert.True(cmdLine.Length <= "Команда: ".Length + 200);
        Assert.StartsWith("Включить", StartupArg.ConfirmText(StartupSource.HklmRun, true, raw));
        Assert.Contains("нажми «Нет»", text);
    }

    [Fact]
    public void Helper_Toggle_WritesFlagOnly_ForExistingEntry()
    {
        var fake = new FakeStartup().With(StartupSource.HklmRun, "Realtek", @"C:\r\r.exe");
        var exec = new RequestProcessorTests.Exec();
        var res = Process(AdminRequest.New(AdminAction.StartupToggle, "HklmRun|off|realtek"), fake, exec);
        Assert.True(res.Ok);
        var w = Assert.Single(fake.Writes);
        Assert.Equal((StartupSource.HklmRun, "Realtek"), (w.Item1, w.Item2));
        Assert.Equal(12, w.Item3.Length);
        Assert.Empty(exec.Calls);                                                // обслуживание не затронуто
        Assert.Single(fake.Entries[StartupSource.HklmRun]);                     // запись не удалена и не изменена
    }

    [Fact]
    public void Helper_Toggle_MissingEntry_Refused_WithCode()
    {
        var fake = new FakeStartup().With(StartupSource.HklmRun, "A", "x");
        var res = Process(AdminRequest.New(AdminAction.StartupToggle, "HklmRun|off|Nope"), fake);
        Assert.False(res.Ok);
        Assert.Equal(StartupToggler.MissingCode, res.Code);
        Assert.Empty(fake.Writes);
    }

    [Theory]
    [InlineData("HkcuRun|off|A")]
    [InlineData("HklmRun|off|")]
    [InlineData("HklmRun|off|A\nB")]
    [InlineData("zzz")]
    [InlineData("")]
    public void Helper_Toggle_BadArgs_RefusedBeforeAnyAccess(string arg)
    {
        var fake = new FakeStartup().With(StartupSource.HklmRun, "A", "x").With(StartupSource.HkcuRun, "A", "x");
        var res = Process(AdminRequest.New(AdminAction.StartupToggle, arg), fake);
        Assert.False(res.Ok);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Helper_Toggle_CommonFolder_ChecksFileNameInFolderListing()
    {
        var fake = new FakeStartup().With(StartupSource.CommonFolder, "App.lnk", @"C:\ProgramData\x\App.lnk");
        Assert.True(Process(AdminRequest.New(AdminAction.StartupToggle, "CommonFolder|off|App.lnk"), fake).Ok);
        var fake2 = new FakeStartup().With(StartupSource.CommonFolder, "App.lnk", @"C:\ProgramData\x\App.lnk");
        Assert.False(Process(AdminRequest.New(AdminAction.StartupToggle, @"CommonFolder|off|..\App.lnk"), fake2).Ok);
        Assert.Empty(fake2.Writes);
    }

    // ---- короткое действие против долгого (AdminClient) ----

    [Fact]
    public void StartupToggle_IsShort_UsesDefaultWait() =>
        Assert.Equal(AdminMaintenance.StartupWait, AdminMaintenance.WaitTimeout(AdminAction.StartupToggle, "HklmRun|off|X"));

    [Fact]
    public async Task ShortAction_FailsFast_WhileHelperHeldByLongMaintenance()
    {
        Directory.CreateDirectory(Path.Combine(_d, "c")); Directory.CreateDirectory(Path.Combine(_d, "cr"));
        var client = new AdminClient(Path.Combine(_d, "c"), Path.Combine(_d, "cr"), runTask: () => true, isInstalled: () => true) { BusyWait = TimeSpan.FromMilliseconds(300) };
        var long1 = client.SendAsync(AdminAction.CleanSystem, "Wer", TimeSpan.FromSeconds(3), default);        // держит помощника: ответа не будет
        await Task.Delay(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await client.SendAsync(AdminAction.StartupToggle, "HklmRun|off|X", TimeSpan.FromSeconds(10), default);
        Assert.False(r.Ok);
        Assert.Equal(AdminCodes.Busy, r.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
        await long1;
    }

    // ---- настоящий реестр: только временные ключи HKCU\Software\PultTest ----

    [Fact]
    public void RealRegistry_TempKeys_ReadAndToggle_OnlyApprovedValueChanges()
    {
        var root = @"Software\PultTest\" + Guid.NewGuid().ToString("N");
        var folder = Path.Combine(_d, "startup"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "desktop.ini"), "x"); File.WriteAllText(Path.Combine(folder, "Tool.lnk"), "x");
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(root + @"\Run"))
            {
                k.SetValue("Alpha", @"""C:\a\alpha.exe"" -x"); k.SetValue("Beta", @"C:\b\beta.exe", RegistryValueKind.ExpandString);
            }
            var access = new RegistryStartupAccess(s => new StartupLocation(RegistryHive.CurrentUser,
                s == StartupSource.HkcuRun ? root + @"\Run" : null, root + @"\Approved\" + s, s == StartupSource.UserFolder ? folder : null));
            var names = access.ReadEntries(StartupSource.HkcuRun).Select(e => e.Name).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "Alpha", "Beta" }, names);
            Assert.Equal(new[] { "Tool.lnk" }, access.ReadEntries(StartupSource.UserFolder).Select(e => e.Name));     // desktop.ini пропущен
            Assert.Null(access.ReadApproved(StartupSource.HkcuRun, "Alpha"));

            Assert.True(StartupToggler.Toggle(access, StartupSource.HkcuRun, "Alpha", false, DateTime.UtcNow, StartupSources.UserSources).Ok);
            var raw = access.ReadApproved(StartupSource.HkcuRun, "Alpha")!;
            Assert.Equal(12, raw.Length); Assert.Equal(3, raw[0]);
            Assert.Equal(2, access.ReadEntries(StartupSource.HkcuRun).Count);                                           // записи на месте
            Assert.False(StartupScanner.Scan(access).Single(e => e.Source == StartupSource.HkcuRun && e.Name == "Alpha").Enabled);
            Assert.True(StartupScanner.Scan(access).Single(e => e.Source == StartupSource.HkcuRun && e.Name == "Beta").Enabled);

            Assert.True(StartupToggler.Toggle(access, StartupSource.HkcuRun, "Alpha", true, DateTime.UtcNow, StartupSources.UserSources).Ok);
            Assert.Equal(2, access.ReadApproved(StartupSource.HkcuRun, "Alpha")![0]);
            Assert.True(StartupToggler.Toggle(access, StartupSource.UserFolder, "Tool.lnk", false, DateTime.UtcNow, StartupSources.UserSources).Ok);
            Assert.False(StartupApproval.IsEnabled(access.ReadApproved(StartupSource.UserFolder, "Tool.lnk")));
            using var run = Registry.CurrentUser.OpenSubKey(root + @"\Run")!;
            Assert.Equal(@"""C:\a\alpha.exe"" -x", run.GetValue("Alpha"));                                              // сама запись не менялась
        }
        finally { try { Registry.CurrentUser.DeleteSubKeyTree(root, false); Registry.CurrentUser.DeleteSubKeyTree(@"Software\PultTest", false); } catch { } }
    }

    // ---- здоровье дисков ----

    private static DiskInfo Disk(string type = "SSD", int health = 0, int[]? op = null, int? wear = 5, int? temp = 35, long? err = 0, long? hours = 100) =>
        new("Тестовый диск", type, 500_000_000_000, health, op ?? new[] { 2 }, wear, temp, err, hours);

    [Fact]
    public void Disk_Healthy()
    {
        var v = DiskHealth.Assess(Disk());
        Assert.Equal(DiskState.Healthy, v.State); Assert.Equal("Исправен", v.Summary);
    }

    [Theory]
    [InlineData(79, DiskState.Healthy)]
    [InlineData(80, DiskState.Warning)]
    [InlineData(100, DiskState.Warning)]
    public void Disk_WearThreshold(int wear, DiskState expected) => Assert.Equal(expected, DiskHealth.Assess(Disk(wear: wear)).State);

    [Fact]
    public void Disk_Wear_Text()
    {
        var v = DiskHealth.Assess(Disk(wear: 87));
        Assert.Equal(DiskState.Warning, v.State); Assert.Equal("Внимание: износ 87%", v.Summary);
    }

    [Fact]
    public void Disk_HealthWarning_IsWarning() => Assert.Equal(DiskState.Warning, DiskHealth.Assess(Disk(health: 1)).State);

    [Fact]
    public void Disk_Unhealthy_SaysSaveData()
    {
        var v = DiskHealth.Assess(Disk(health: 2));
        Assert.Equal(DiskState.Unhealthy, v.State);
        Assert.Equal("Диск сообщает о неисправности — сохраните важные данные", v.Summary);
    }

    [Fact]
    public void Disk_UncorrectedReadErrors_Warn()
    {
        var v = DiskHealth.Assess(Disk(err: 3));
        Assert.Equal(DiskState.Warning, v.State); Assert.Contains("ошибки чтения: 3", v.Summary);
    }

    [Fact]
    public void Disk_PredictiveFailure_IsUnhealthy_EvenIfHealthSaysOk() =>
        Assert.Equal(DiskState.Unhealthy, DiskHealth.Assess(Disk(health: 0, op: new[] { 5 })).State);

    [Fact]
    public void Disk_Degraded_IsWarning() => Assert.Equal(DiskState.Warning, DiskHealth.Assess(Disk(op: new[] { 3 })).State);

    [Theory]
    [InlineData("SSD", 69, DiskState.Healthy)]
    [InlineData("SSD", 70, DiskState.Warning)]
    [InlineData("HDD", 54, DiskState.Healthy)]
    [InlineData("HDD", 55, DiskState.Warning)]
    public void Disk_Temperature(string type, int t, DiskState expected) => Assert.Equal(expected, DiskHealth.Assess(Disk(type: type, temp: t)).State);

    [Fact]
    public void Disk_NoCounters_StillHealthy_ByStatus()
    {
        var v = DiskHealth.Assess(Disk(wear: null, temp: null, err: null, hours: null));
        Assert.Equal(DiskState.Healthy, v.State);
    }

    [Fact]
    public void Disk_NoData_WhenHealthUnknownAndNoCounters()
    {
        var v = DiskHealth.Assess(Disk(health: 5, wear: null, temp: null, err: null, hours: null, op: new int[0]));
        Assert.Equal(DiskState.Unknown, v.State); Assert.Equal(DiskHealth.NoDataText, v.Summary);
    }

    [Fact]
    public void Disk_UnknownHealth_ButCountersFine_IsHealthy() => Assert.Equal(DiskState.Healthy, DiskHealth.Assess(Disk(health: 5)).State);

    [Fact]
    public void Disk_SeveralReasons_AllListed()
    {
        var v = DiskHealth.Assess(Disk(wear: 90, err: 2, temp: 75));
        Assert.Equal(DiskState.Warning, v.State);
        Assert.Contains("износ 90%", v.Summary); Assert.Contains("ошибки чтения: 2", v.Summary); Assert.Contains("75 °C", v.Summary);
    }

    [Theory]
    [InlineData(3, 0, "HDD")]
    [InlineData(4, 0, "SSD")]
    [InlineData(0, 17, "SSD")]
    [InlineData(0, 0, "накопитель")]
    public void MediaType_Names(int media, int bus, string name) => Assert.Equal(name, DiskHealth.MediaTypeName(media, bus));

    [Fact]
    public void TrustedDiskNumbers_OnlyUniqueNonSpacesDisks()
    {
        var t = DiskHealth.TrustedDiskNumbers(new[] { ("0", 17), ("1", 11), ("2", 16), ("3", 15), ("4", 7), ("4", 7) });   // 17 NVMe, 11 SATA, 16 Spaces, 15 файловый виртуальный, 7 USB дважды
        Assert.Equal(new[] { "0", "1" }, t.OrderBy(x => x).ToArray());
        Assert.Empty(DiskHealth.TrustedDiskNumbers(Array.Empty<(string, int)>()));
    }

    [Fact]
    public void NeedAttention_CountsWarningAndUnhealthy()
    {
        var v = new[] { DiskHealth.Assess(Disk()), DiskHealth.Assess(Disk(wear: 90)), DiskHealth.Assess(Disk(health: 2)), DiskHealth.Assess(Disk(health: 5, wear: null, temp: null, err: null)) };
        Assert.Equal(2, DiskHealth.NeedAttention(v));
    }

    [Fact]
    public async Task DiskReader_Timeout_ReturnsErrorNotException()
    {
        var r = await DiskReader.ReadAsync(TimeSpan.FromMilliseconds(1), default);        // слишком короткое время: либо успел, либо вежливый отказ
        Assert.NotNull(r.Disks);
    }
}

/// Живая проверка на этом ПК, только чтение (ничего не переключается). Запускается, если задана переменная PULT_LIVE=1.
public class StartupDiskLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Live_ReadRealStartupAndDisks_ReadOnly()
    {
        if (Environment.GetEnvironmentVariable("PULT_LIVE") != "1") return;
        var list = StartupScanner.Scan(RegistryStartupAccess.ForThisPc(), StartupFileInfo.Describe, Environment.ExpandEnvironmentVariables, Environment.ProcessPath);
        output.WriteLine($"Автозагрузка: {list.Count} записей, включено {list.Count(e => e.Enabled)}");
        foreach (var e in list) output.WriteLine($"  [{e.Source}] {e.Name} | {(e.Enabled ? "вкл" : "выкл")} | {StartupCommand.Shorten(e.ExePath ?? e.Command, 50)} | {e.Description} | {e.Publisher}");
        var r = await DiskReader.ReadAsync(TimeSpan.FromSeconds(45), default);
        output.WriteLine($"Диски: ошибка={r.Error ?? "нет"}, счётчики={r.CountersAvailable}, дисков={r.Disks.Count}");
        foreach (var d in r.Disks)
            output.WriteLine($"  {d.Name} | {d.MediaType} | {d.SizeBytes / 1_000_000_000} ГБ | health={d.HealthStatus} op=[{string.Join(",", d.OperationalStatus)}] wear={d.Wear} t={d.TemperatureC} err={d.ReadErrors} h={d.PowerOnHours} => {DiskHealth.Assess(d).Summary}");
    }
}

public class AutostartRegistryTests
{
    private const string Cmd = @"""C:\x\Pult.exe"" --minimized";

    private static void WithKeys(Action<AutostartRegistry, string, string> body)
    {
        var root = @"Software\PultTest\" + Guid.NewGuid().ToString("N");
        try { body(new AutostartRegistry(root + @"\Run", root + @"\Approved"), root + @"\Run", root + @"\Approved"); }
        finally { try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, false); Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\PultTest", false); } catch { } }
    }

    private static void PutFlag(string key, byte first)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
        var b = new byte[12]; b[0] = first; k.SetValue("Pult", b, Microsoft.Win32.RegistryValueKind.Binary);
    }

    [Fact]
    public void Set_On_ClearsDisabledFlag_AndShowsOn() => WithKeys((a, run, ap) =>
    {
        PutFlag(ap, 0x03);
        Assert.False(a.IsOn(Cmd));
        a.Set(true, Cmd);
        Assert.True(a.IsOn(Cmd));
        Assert.False(a.IsDisabledByWindows());
    });

    [Fact]
    public void RunPresent_ButDisabledInTaskManager_ShowsOff() => WithKeys((a, run, ap) =>
    {
        a.Set(true, Cmd);
        PutFlag(ap, 0x03);
        Assert.True(a.IsRegistered(Cmd));
        Assert.False(a.IsOn(Cmd));
    });

    [Fact]
    public void Set_Off_RemovesRunAndFlag() => WithKeys((a, run, ap) =>
    {
        a.Set(true, Cmd); PutFlag(ap, 0x03);
        a.Set(false, Cmd);
        Assert.False(a.IsRegistered(Cmd));
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ap);
        Assert.Null(k?.GetValue("Pult"));
    });

    [Fact]
    public void EnsureRegistered_WritesMissingRun_ButKeepsUserDisabledFlag() => WithKeys((a, run, ap) =>
    {
        PutFlag(ap, 0x03);
        a.EnsureRegistered(Cmd);
        Assert.True(a.IsRegistered(Cmd));
        Assert.True(a.IsDisabledByWindows());          // выбор человека не отменяем
    });

    [Fact]
    public void OtherCommand_IsNotOn() => WithKeys((a, run, ap) =>
    {
        a.Set(true, @"""D:\old\Pult.exe"" --minimized");
        Assert.False(a.IsOn(Cmd));
    });

    [Fact]
    public void EnabledFlag_02_CountsAsOn() => WithKeys((a, run, ap) =>
    {
        a.Set(true, Cmd); PutFlag(ap, 0x02);
        Assert.True(a.IsOn(Cmd));
    });
}
