using Pult.Core.Admin;
namespace Pult.Core.Tests;

/// Подставная проверка прав на папки (реальную — PathTrust — проверяют PathTrustTests).
internal sealed class FakeTrust(string? problem = null) : IPathTrust
{
    public string? ExeProblem(string exePath) => problem;
    public string? FileChainProblem(string path) => problem;
    public string? DirProblem(string dir) => problem;
}

public class AdminPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pult-a-" + Guid.NewGuid().ToString("N"));
    private readonly FileHasher _h = new();
    private static readonly IPathTrust Ok = new FakeTrust();
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string G1 = "11111111-2222-3333-4444-555555555555";

    public AdminPolicyTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private Decision Decide(AdminAction a, string arg) => AdminPolicy.Decide(new AdminRequest(Nonce, a, arg));

    // ---------- что разрешено: только обслуживание ----------

    [Fact]
    public void Maintenance_ValidRequests_Allowed()
    {
        Assert.True(Decide(AdminAction.DriverInstall, G1).Allowed);
        Assert.True(Decide(AdminAction.DriverInstall, "norp:" + G1).Allowed);
        Assert.True(Decide(AdminAction.RestorePoint, "").Allowed);
        Assert.True(Decide(AdminAction.CleanSystem, AdminMaintenance.Wer).Allowed);
    }

    [Fact]
    public void Maintenance_BadArguments_Denied()
    {
        Assert.False(Decide(AdminAction.DriverInstall, "").Allowed);
        Assert.False(Decide(AdminAction.DriverInstall, "calc.exe").Allowed);
        Assert.False(Decide(AdminAction.RestorePoint, "лишнее").Allowed);
        Assert.False(Decide(AdminAction.CleanSystem, @"C:\Windows").Allowed);
    }

    // ---------- бывшие zapret и «запуск программы» больше не существуют ----------

    [Theory]
    [InlineData("ZapretStart", "general (ALT).bat")]
    [InlineData("ZapretStop", "")]
    [InlineData("Run", "obs")]
    public void FormerZapretAndRunActions_RejectedAtParse(string action, string arg)
    {
        var json = $$"""{"Nonce":"{{Nonce}}","Action":"{{action}}","Arg":{{System.Text.Json.JsonSerializer.Serialize(arg)}}}""";
        Assert.Null(AdminRequest.Parse(json));
        Assert.DoesNotContain(action, Enum.GetNames<AdminAction>());
    }

    [Theory]
    [InlineData(7)] [InlineData(99)] [InlineData(-1)]
    public void UnknownActionNumber_DeniedByPolicy(int number)
    {
        var d = Decide((AdminAction)number, "obs");
        Assert.False(d.Allowed);
        Assert.Contains("неизвестное действие", d.Reason);
    }

    [Theory]
    [InlineData("RestorePoint, CleanSystem")] [InlineData("3")] [InlineData("RestorePoint,StartupToggle")] [InlineData("1, 2")]
    public void CombinedOrNumericAction_NeverThrows_OnlyKnownSetOrNull(string action)
    {
        var json = $$"""{"Nonce":"{{Nonce}}","Action":"{{action}}","Arg":""}""";
        var r = AdminRequest.Parse(json);
        if (r != null) Assert.Contains(r.Action.ToString(), Enum.GetNames<AdminAction>());
    }

    [Fact]
    public void AdminActions_AreExactlyTheKnownOnes() =>
        Assert.Equal(new[] { "CleanSystem", "DiskDetails", "DriverInstall", "RepairSystem", "RestorePoint", "StartupToggle", "WingetUpgrade" }, Enum.GetNames<AdminAction>().OrderBy(x => x, StringComparer.Ordinal));

    // ---------- протокол ----------

    [Fact]
    public void Protocol_RoundTrip_AndGarbage()
    {
        var r = new AdminRequest(Nonce, AdminAction.CleanSystem, "Wer");
        Assert.Equal(r, AdminRequest.Parse(r.ToJson()));
        Assert.Null(AdminRequest.Parse("не json"));
        Assert.Null(AdminRequest.Parse("""{"Nonce":"1","Action":"FormatDisk","Arg":""}"""));
    }

    [Theory]
    [InlineData("1")] [InlineData(@"..\..\Windows\System32\x")] [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0")] [InlineData("0123456789abcdef0123456789abcde\n")]
    public void Protocol_BadNonce_Rejected(string nonce) =>
        Assert.Null(AdminRequest.Parse($$"""{"Nonce":{{System.Text.Json.JsonSerializer.Serialize(nonce)}},"Action":"RestorePoint","Arg":""}"""));

    [Fact]
    public void Protocol_EscapesHtmlAndCmdChars()          // ответ не может стать рабочей строкой .cmd
    {
        var json = new AdminResponse(Nonce, false, "a & b <c>").ToJson();
        Assert.DoesNotContain("&", json);
        Assert.DoesNotContain("<", json);
    }

    [Fact]
    public void Response_KeepsMachineCode_OldResponseWithoutCodeIsEmpty()
    {
        var back = AdminResponse.Parse(new AdminResponse("n", false, "off", AdminCodes.RestoreDisabled).ToJson())!;
        Assert.Equal(AdminCodes.RestoreDisabled, back.Code);
        Assert.Equal("", AdminResponse.Parse("{\"Nonce\":\"n\",\"Ok\":true,\"Message\":\"ok\"}")!.Code);
    }

    // ---------- admin.json: старые поля игнорируются ----------

    [Fact]
    public void Store_OldAdminJson_WithZapretAndApps_ReadsFine_FieldsDroppedOnSave()
    {
        var p = Path.Combine(_root, "admin.json");
        File.WriteAllText(p, """
            {
              "ZapretUserDir": "C:\\Users\\x\\zapret", "ZapretProtectedDir": "C:\\Program Files\\Pult\\zapret\\1.10.2", "ZapretVersion": "1.10.2",
              "ZapretHashes": { "general (alt).bat": "ab" },
              "ZapretPrevious": [ { "Version": "1.9", "ProtectedDir": "C:\\old", "Hashes": {} } ],
              "Apps": [ { "Id": "obs", "Path": "C:\\Program Files\\obs\\obs64.exe", "Sha256": "cd", "Arguments": "--x" } ]
            }
            """);
        var store = AdminStore.Load(p);
        Assert.NotNull(store);
        store.Save(p);
        var text = File.ReadAllText(p);
        Assert.DoesNotContain("Zapret", text); Assert.DoesNotContain("Apps", text); Assert.DoesNotContain("obs", text);
        Assert.NotNull(AdminStore.Load(p));
    }

    [Fact]
    public void Store_MissingOrBroken_ReturnsEmpty()
    {
        Assert.NotNull(AdminStore.Load(Path.Combine(_root, "нет.json")));
        var p = Path.Combine(_root, "broken.json"); File.WriteAllText(p, "{ мусор");
        Assert.NotNull(AdminStore.Load(p));
        var big = Path.Combine(_root, "big.json"); File.WriteAllText(big, new string(' ', (int)AdminStore.MaxBytes + 1));
        Assert.NotNull(AdminStore.Load(big));
    }

    [Fact]
    public void SystemWorkerPath_UsesEmptyStore_ForAnyAction()
    {
        // SystemWorker перепроверяет задание политикой с пустым хранилищем — для обслуживания этого достаточно
        Assert.True(AdminPolicy.Decide(new AdminRequest(Nonce, AdminAction.RestorePoint, "")).Allowed);
    }
}
