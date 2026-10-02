using Pult.Admin;
using Pult.Core.Admin;

namespace Pult.Core.Tests;

/// Правки по отчёту SECURITY_AUDIT (30.09.2026).
public class SecurityHardeningTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "pult-sh-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    // ---- #7: помощник рядом с пультом — только с совпавшим встроенным хешем ----
    [Fact]
    public void Gate_MatchingHash_ReturnsOpenHandle_AndBlocksWriters()
    {
        var f = Path.Combine(TempDir(), "PultAdmin.exe"); var data = new byte[] { 1, 2, 3, 4 }; File.WriteAllBytes(f, data);
        using var h = AdminBinaryGate.OpenVerified(f, Sha(data), out var why);
        Assert.NotNull(h); Assert.Null(why);
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(f, new byte[] { 9 }));   // пока открыт — подменить нельзя
    }

    [Fact]
    public void Gate_WrongHash_Refused()
    {
        var f = Path.Combine(TempDir(), "PultAdmin.exe"); File.WriteAllBytes(f, new byte[] { 1 });
        Assert.Null(AdminBinaryGate.OpenVerified(f, Sha(new byte[] { 2 }), out var why));
        Assert.NotNull(why);
    }

    [Theory] [InlineData(null)] [InlineData("")] [InlineData("zz")]
    public void Gate_DevBuild_NoEmbeddedHash_NeverOffers(string? expected)
    {
        var f = Path.Combine(TempDir(), "PultAdmin.exe"); File.WriteAllBytes(f, new byte[] { 1 });
        Assert.Null(AdminBinaryGate.OpenVerified(f, expected, out var why));
        Assert.Equal(AdminBinaryGate.DevBuildMessage, why);
    }

    [Fact]
    public void Gate_MissingFile_Refused() =>
        Assert.Null(AdminBinaryGate.OpenVerified(Path.Combine(TempDir(), "no.exe"), Sha(new byte[] { 1 }), out _));

    // ---- LOW ----
    [Fact]
    public void AdminLog_RotatesWhenOverCap()
    {
        var f = Path.Combine(TempDir(), "admin.log");
        File.WriteAllText(f, new string('x', 200));
        AdminLog.Append(f, "new", maxBytes: 100);
        Assert.True(File.Exists(f + ".1")); Assert.Contains("new", File.ReadAllText(f)); Assert.DoesNotContain("xxx", File.ReadAllText(f));
        AdminLog.Append(f, "again", maxBytes: 100);
        Assert.Contains("again", File.ReadAllText(f)); Assert.Contains("new", File.ReadAllText(f));
    }

    [Theory]
    [InlineData(@"C:\a\b.ico", true)] [InlineData(@"\\srv\share\a.ico", false)] [InlineData("//srv/share/a.ico", false)]
    [InlineData(@"\\?\C:\a.ico", false)] [InlineData(@"\\.\PhysicalDrive0", false)] [InlineData(@"a.ico", false)] [InlineData("", false)]
    public void LocalFilePath(string p, bool ok) => Assert.Equal(ok, PathSafety.IsLocalFilePath(p));

    [Fact]
    public void FileWithinSize()
    {
        var f = Path.Combine(TempDir(), "i.bin"); File.WriteAllBytes(f, new byte[100]);
        Assert.True(PathSafety.WithinSize(f, 100)); Assert.False(PathSafety.WithinSize(f, 99)); Assert.False(PathSafety.WithinSize(f + "x", 99));
    }

    [Theory] [InlineData("con.txt", true)] [InlineData("NUL.txt", true)] [InlineData("aux.txt", true)] [InlineData("prn.txt", true)] [InlineData("com1.txt", true)]
    [InlineData("LPT9.txt", true)] [InlineData("con.foo.txt", true)] [InlineData("con .txt", true)] [InlineData("console.txt", false)] [InlineData("list-general.txt", false)]
    public void ReservedDosNames(string n, bool reserved)
    {
        Assert.Equal(reserved, PathSafety.IsReservedDosName(n));
    }

    [Theory] [InlineData("Pult Admin", true)] [InlineData("helper - бот-помощник (автозапуск)", true)] [InlineData("a\" /S evil", false)] [InlineData("", false)] [InlineData("a\nb", false)]
    public void TaskName_Validated(string n, bool ok) => Assert.Equal(ok, SystemTools.IsValidTaskName(n));

    [Fact]
    public void SystemTools_FullSystem32Path() =>
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), SystemTools.Exe("schtasks.exe"));
}
