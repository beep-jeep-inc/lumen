using Pult.Core.Maintenance;
namespace Pult.Core.Tests;

public class DriversTests
{
    private static readonly DateTime Now = new(2026, 9, 30);

    [Theory]
    [InlineData(28, "драйвер не установлен")]
    [InlineData(43, "устройство сообщило о сбое")]
    [InlineData(22, "устройство отключено")]
    [InlineData(10, "устройство не запускается")]
    public void Explain_KnownCodes(int code, string text) => Assert.Equal(text, DeviceErrors.Explain(code));

    [Fact]
    public void Explain_AllListedCodesHaveText_UnknownFallsBack()
    {
        foreach (var c in new[] { 1, 3, 10, 12, 14, 18, 19, 21, 22, 24, 28, 31, 32, 33, 34, 35, 37, 38, 39, 40, 41, 42, 43, 44, 45, 47, 48, 49, 50, 51, 52 })
            Assert.DoesNotContain("код", DeviceErrors.Explain(c));
        Assert.Contains("999", DeviceErrors.Explain(999));
    }

    [Fact]
    public void Build_FiltersNotConnected45AndZero()
    {
        var r = DeviceErrors.Build(new (string?, int)[] { ("Камера", 45), ("Звук", 28), ("Ok", 0), (null, 43) });
        Assert.Equal(2, r.Count);
        Assert.Equal("Звук", r[0].Name); Assert.Equal(28, r[0].Code);
        Assert.Equal("Неизвестное устройство", r[1].Name);
    }

    [Fact]
    public void Gpu_OldByDate()
    {
        Assert.True(Drivers.MakeGpu("NVIDIA GeForce RTX 4070", "32.0", Now.AddDays(-366), Now).Old);
        Assert.False(Drivers.MakeGpu("NVIDIA GeForce RTX 4070", "32.0", Now.AddDays(-364), Now).Old);
        Assert.False(Drivers.MakeGpu("NVIDIA GeForce RTX 4070", "32.0", null, Now).Old);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", "NVIDIA App", "nvidia.com")]
    [InlineData("Intel(R) UHD Graphics 770", "Intel Driver & Support Assistant", "intel.com")]
    [InlineData("AMD Radeon RX 7800 XT", "AMD Adrenalin", "amd.com")]
    public void Gpu_VendorHint(string name, string hint, string host)
    {
        var g = Drivers.MakeGpu(name, "1", Now, Now);
        Assert.Equal(hint, g.VendorAppHint);
        Assert.Contains(host, g.VendorUrl);
    }

    [Fact]
    public void Wua_VersionFromTitle()
    {
        Assert.Equal("2.3.20306.4", WuaSearch.VersionFromTitle("Intel System Driver Update (2.3.20306.4)"));
        Assert.Equal("13.6489.2509.910", WuaSearch.VersionFromTitle("Realtek AudioProcessingObject Driver Update (13.6489.2509.910)"));
        Assert.Null(WuaSearch.VersionFromTitle("Some driver"));
    }

    [Fact]
    public void FriendlyError_NoNetwork_AndStoppedService_PlainRussian()
    {
        foreach (var hr in new uint[] { 0x80072EE7, 0x80072EFD, 0x8024402C, 0x8024401C })
            Assert.Contains("нет связи", WuaSearch.FriendlyError(new System.Runtime.InteropServices.COMException("raw", unchecked((int)hr))));
        Assert.Contains("остановлена", WuaSearch.FriendlyError(new System.Runtime.InteropServices.COMException("raw", unchecked((int)0x8024001E))));
        Assert.Equal("boom", WuaSearch.FriendlyError(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Wua_CancelledToken_ReturnsEmptyQuickly()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        IWindowsUpdateSearch s = new WuaSearch();
        var r = await s.SearchDriversAsync(cts.Token);
        Assert.Empty(r);
    }

    [Fact(Skip = "Живая проверка: WMI и Центр обновления на этом ПК")]
    public async Task Live_All()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("DEVICES"); foreach (var d in DeviceErrors.Current()) sb.AppendLine(d.ToString());
        sb.AppendLine("GPUS"); foreach (var g in Drivers.Gpus()) sb.AppendLine(g.ToString());
        var w = new WuaSearch();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await w.SearchDriversAsync(CancellationToken.None);
        sb.AppendLine($"WUA {res.Count} in {sw.Elapsed} err={w.LastError}");
        foreach (var u in res) sb.AppendLine(u.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pult-live-drivers.txt"), sb.ToString());
    }
}
