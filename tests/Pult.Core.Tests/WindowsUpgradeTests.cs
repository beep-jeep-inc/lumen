using Pult.Core.Maintenance;
using Xunit;

namespace Pult.Core.Tests;

public class WindowsUpgradeTests
{
    private static (string, IReadOnlyList<string>) U(string t, params string[] c) => (t, c);

    [Fact]
    public void Filter_KeepsUpgradeDropsDriverAndNoCategories()
    {
        var r = WuaUpgradeSearch.FilterUpgrades(new[]
        {
            U("Windows 11, version 26H2", "Upgrades"),
            U("MSI Firmware Driver Update (1.0.1.5)", "Drivers"),
            U("Без категорий"),
        });
        Assert.Single(r);
        Assert.Equal("Windows 11, version 26H2", r[0].Title);
    }

    [Fact]
    public void Filter_IsCaseInsensitive() =>
        Assert.Single(WuaUpgradeSearch.FilterUpgrades(new[] { U("X", "Drivers", "upGRADES") }));

    [Theory]
    [InlineData("25H2", "26200", 9457, "25H2 (сборка 26200.9457)")]
    [InlineData("25H2", "26200", null, "25H2 (сборка 26200)")]
    [InlineData(null, "26200", 9457, "сборка 26200.9457")]
    [InlineData("25H2", null, 5, "25H2")]
    [InlineData("", " ", null, null)]
    public void WindowsVersion_Format(string? v, string? b, int? ubr, string? expected) =>
        Assert.Equal(expected, WindowsVersion.Format(v, b, ubr));
}
