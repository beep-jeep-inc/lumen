using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using Pult.Admin;
using Pult.Core.Admin;
namespace Pult.Core.Tests;

/// Исправления ревью безопасности batch C: права на папки, Id, чистое окружение.
public class AdminSecurityTests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-sec-" + Guid.NewGuid().ToString("N"));
    public AdminSecurityTests() => Directory.CreateDirectory(_d);
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    // ---------- права на папки ----------

    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AuthUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    private static FileSecurity AdminOnlyFile()
    {
        var s = new FileSecurity();
        s.SetOwner(PathTrust.Admins);
        s.AddAccessRule(new FileSystemAccessRule(PathTrust.Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(PathTrust.LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return s;
    }

    [Fact]
    public void Acl_AdminOnly_Accepted() => Assert.Null(PathTrust.CheckSecurity(AdminOnlyFile(), strict: true));

    [Theory]
    [InlineData(FileSystemRights.Write)] [InlineData(FileSystemRights.Modify)] [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)] [InlineData(FileSystemRights.ChangePermissions)] [InlineData(FileSystemRights.TakeOwnership)]
    public void Acl_UserWritable_Rejected(FileSystemRights rights)
    {
        var s = AdminOnlyFile();
        s.AddAccessRule(new FileSystemAccessRule(Users, rights, AccessControlType.Allow));
        Assert.NotNull(PathTrust.CheckSecurity(s, strict: true));
    }

    [Fact]
    public void Acl_CurrentUserOwnerOrAce_Rejected()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var owned = AdminOnlyFile(); owned.SetOwner(me);
        Assert.NotNull(PathTrust.CheckSecurity(owned, strict: true));
        var ace = AdminOnlyFile(); ace.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.Write, AccessControlType.Allow));
        Assert.NotNull(PathTrust.CheckSecurity(ace, strict: true));
    }

    [Fact]
    public void Acl_Ancestor_CreateSubfolderAllowed_DeleteNot_InheritOnlyIgnored()   // так устроен C:\
    {
        var s = new DirectorySecurity();
        s.SetOwner(PathTrust.TrustedInstaller);
        s.AddAccessRule(new FileSystemAccessRule(AuthUsers, FileSystemRights.AppendData, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(AuthUsers, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                                 PropagationFlags.InheritOnly, AccessControlType.Allow));
        Assert.Null(PathTrust.CheckSecurity(s, strict: false));
        Assert.NotNull(PathTrust.CheckSecurity(s, strict: true));                   // а в папке самой программы — нельзя
        s.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Delete, AccessControlType.Allow));
        Assert.NotNull(PathTrust.CheckSecurity(s, strict: false));
    }

    [Fact]
    public void PathTrust_RealFiles_TempRejected_System32Accepted()
    {
        var exe = Path.Combine(_d, "app.exe"); File.WriteAllText(exe, "MZ");
        var t = new PathTrust();
        Assert.NotNull(t.FileChainProblem(exe));
        var sysProblem = t.FileChainProblem(Path.Combine(Environment.SystemDirectory, "ping.exe")); Assert.True(sysProblem == null, sysProblem);
        Assert.NotNull(t.FileChainProblem(Path.Combine(Environment.SystemDirectory, "no-such-file.exe")));
        Assert.NotNull(t.FileChainProblem(@"\\server\share\app.exe"));
        Assert.NotNull(t.DirProblem(_d));
        Assert.Null(t.DirProblem(Environment.SystemDirectory));
    }

    [Fact]
    public void FileChainProblem_UserFolder_NotNull_System32_Null_NoNameChecks()
    {
        var exe = Path.Combine(_d, "winget.exe"); File.WriteAllText(exe, "MZ");
        var t = new PathTrust();
        Assert.NotNull(t.FileChainProblem(exe));                                   // файл в папке пользователя
        var sys = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.Null(t.FileChainProblem(sys));                                      // по имени не отказывает (имя — не её дело): только права
        Assert.NotNull(t.FileChainProblem(@"\\server\share\winget.exe"));
        Assert.NotNull(t.FileChainProblem(Path.Combine(_d, "нет.exe")));
        Assert.NotNull(t.FileChainProblem(@"winget.exe"));
    }

    [Theory]
    [InlineData("S-1-19-512-4096", true)]    // PPL Windows — на winget.exe
    [InlineData("S-1-19-1024-8192", true)]   // Protected WinTcb
    [InlineData("S-1-19-512-1536", false)]   // антивирусный уровень — не доверяем
    [InlineData("S-1-19-512-512", false)]
    [InlineData("S-1-5-21-1-2-3-1001", false)]
    [InlineData("S-1-15-3-1024", false)]
    public void IsWindowsTrustLabel_OnlyWindowsLevels(string sid, bool expected) =>
        Assert.Equal(expected, PathTrust.IsWindowsTrustLabel(new System.Security.Principal.SecurityIdentifier(sid)));

    [Fact]
    public void PackageProblem_UserFolder_NotNull_HugeFolder_Refused()
    {
        var exe = Path.Combine(_d, "winget.exe"); File.WriteAllText(exe, "MZ");
        var t = new PathTrust();
        Assert.NotNull(t.PackageProblem(exe));
        // System32: сам файл и папки защищены, но соседей тысячи — для «папки пакета» это не годится, честный отказ
        Assert.NotNull(t.PackageProblem(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
    }

    [LiveFact]   // только чтение: на этом ПК папка WindowsApps доступна на запись пользователю — ожидается причина
    public void Live_FileChainProblem_OfRealWinget()
    {
        var w = Pult.Core.Maintenance.Winget.FindForHelper();
        var why = w == null ? "winget не найден" : new PathTrust().PackageProblem(w);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pult-live-winget-trust.txt"), (w ?? "-") + "\n" + (why ?? "null (папки защищены)"));
    }
}
