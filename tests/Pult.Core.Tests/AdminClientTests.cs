using Pult.Core.Admin;
namespace Pult.Core.Tests;

public class AdminClientTests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-ac-" + Guid.NewGuid().ToString("N"));
    private string Res => Path.Combine(_d, "res");
    public AdminClientTests() { Directory.CreateDirectory(_d); Directory.CreateDirectory(Res); }
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    [Fact]
    public async Task Send_ReadsResponseFromResDir_MatchingNonce_AndDeletesRequestItself()
    {
        var client = new AdminClient(_d, Res, runTask: () =>
        {
            var req = AdminRequest.Parse(File.ReadAllText(Path.Combine(_d, "admin.req")))!;
            _ = Task.Run(async () =>
            {
                var file = Path.Combine(Res, req.Nonce + ".json");
                File.WriteAllText(file, new AdminResponse("старый", true, "чужой").ToJson());
                await Task.Delay(300);
                File.WriteAllText(file, new AdminResponse(req.Nonce, true, "ok").ToJson());
            });
            return true;
        }, isInstalled: () => true);
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromSeconds(5), default);
        Assert.True(r.Ok);
        Assert.Equal("ok", r.Message);
        Assert.False(File.Exists(Path.Combine(_d, "admin.req")));                 // запрос убирает пульт, не помощник
    }

    [Fact]
    public async Task Send_IgnoresResponseInUserDir()
    {
        var client = new AdminClient(_d, Res, runTask: () =>
        {
            var req = AdminRequest.Parse(File.ReadAllText(Path.Combine(_d, "admin.req")))!;
            File.WriteAllText(Path.Combine(_d, "admin.res"), new AdminResponse(req.Nonce, true, "подделка").ToJson());
            File.WriteAllText(Path.Combine(_d, req.Nonce + ".json"), new AdminResponse(req.Nonce, true, "подделка").ToJson());
            return true;
        }, isInstalled: () => true);
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromMilliseconds(600), default);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Send_Timeout_ReturnsFailure_NotHang()
    {
        var client = new AdminClient(_d, Res, runTask: () => true, isInstalled: () => true);
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromMilliseconds(600), default);
        Assert.False(r.Ok);
        Assert.Contains("не ответил", r.Message);
        Assert.False(File.Exists(Path.Combine(_d, "admin.req")));
    }

    [Fact]
    public async Task Send_NotInstalled_ExplainsWithoutRunning()
    {
        bool ran = false;
        var client = new AdminClient(_d, Res, runTask: () => ran = true, isInstalled: () => false);
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromSeconds(1), default);
        Assert.False(r.Ok);
        Assert.False(ran);
        Assert.Contains("не установлен", r.Message);
    }

    [Fact]
    public async Task Send_HelperGone_NoResponse_ReturnsHelperGone_Quickly()
    {
        var client = new AdminClient(_d, Res, runTask: () => true, isInstalled: () => true, helperAlive: () => false)
        { HelperGrace = TimeSpan.FromMilliseconds(200), AliveCheckEvery = TimeSpan.FromMilliseconds(150) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await client.SendAsync(AdminAction.StartupToggle, "HklmRun|off|X", TimeSpan.FromMinutes(10), default);
        Assert.False(r.Ok); Assert.Equal(AdminCodes.HelperGone, r.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Send_HelperGone_WithDefaultTimings_ByFifteenSeconds()
    {
        var client = new AdminClient(_d, Res, runTask: () => true, isInstalled: () => true, helperAlive: () => false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await client.SendAsync(AdminAction.RepairSystem, "", TimeSpan.FromMinutes(10), default);
        Assert.Equal(AdminCodes.HelperGone, r.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(16), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task Send_HelperAlive_ResponseLater_IsNormalResponse()
    {
        var client = new AdminClient(_d, Res, runTask: () =>
        {
            var req = AdminRequest.Parse(File.ReadAllText(Path.Combine(_d, "admin.req")))!;
            _ = Task.Run(async () => { await Task.Delay(1200); File.WriteAllText(Path.Combine(Res, req.Nonce + ".json"), new AdminResponse(req.Nonce, true, "ok").ToJson()); });
            return true;
        }, isInstalled: () => true, helperAlive: () => true) { HelperGrace = TimeSpan.FromMilliseconds(100), AliveCheckEvery = TimeSpan.FromMilliseconds(100) };
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromSeconds(10), default);
        Assert.True(r.Ok); Assert.Equal("ok", r.Message);
    }

    [Fact]
    public async Task Send_HelperGone_ButResponseExists_ReturnsResponse()
    {
        var client = new AdminClient(_d, Res, runTask: () =>
        {
            var req = AdminRequest.Parse(File.ReadAllText(Path.Combine(_d, "admin.req")))!;
            _ = Task.Run(async () => { await Task.Delay(100); File.WriteAllText(Path.Combine(Res, req.Nonce + ".json"), new AdminResponse(req.Nonce, true, "записал и вышел").ToJson()); });
            return true;
        }, isInstalled: () => true, helperAlive: () => false) { HelperGrace = TimeSpan.FromMilliseconds(100), AliveCheckEvery = TimeSpan.FromMilliseconds(200) };
        var r = await client.SendAsync(AdminAction.RestorePoint, "", TimeSpan.FromSeconds(10), default);
        Assert.True(r.Ok); Assert.NotEqual(AdminCodes.HelperGone, r.Code);
    }

    [Theory]
    [InlineData(@"C:\zapret\","\"C:\\zapret\"")]
    [InlineData(@"C:\Новая папка\zapret \", "\"C:\\Новая папка\\zapret\"")]
    [InlineData(@"D:\a b\c", "\"D:\\a b\\c\"")]
    public void QuotePath_NoTrailingBackslashBeforeClosingQuote(string path, string expected) =>
        Assert.Equal(expected, AdminClient.QuotePath(path));
}
