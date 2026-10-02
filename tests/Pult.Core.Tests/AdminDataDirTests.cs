using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using Pult.Admin;
using Pult.Core.Admin;
namespace Pult.Core.Tests;

/// Fix round 2: папка данных помощника (создание с правами сразу, перенос негодной), проверка списка доступа, флажки zapret.
public class AdminDataDirTests : IDisposable
{
    private readonly string _d = Path.Combine(Path.GetTempPath(), "pult-dd-" + Guid.NewGuid().ToString("N"));
    private string Pult => Path.Combine(_d, "Pult");
    public AdminDataDirTests() => Directory.CreateDirectory(_d);
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// Без прав администратора владельца не сменить: новая папка — защищённый список доступа «я + Users чтение»;
    /// «негодная» — ссылка или папка с evil.txt внутри.
    private sealed class TestPolicy : IDataDirPolicy
    {
        public DirectorySecurity NewSecurity()
        {
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var s = new DirectorySecurity();
            s.SetAccessRuleProtection(true, false);
            s.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            s.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            return s;
        }
        public string? Problem(string dir) =>
            (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0 ? "ссылка"
            : Directory.EnumerateFiles(dir, "evil.txt", SearchOption.AllDirectories).Any() ? "чужие файлы" : null;
    }

    private string[] Aside() => Directory.GetFileSystemEntries(_d, "Pult.old-*");

    [Fact]
    public void DataDir_Absent_CreatedWithProtectedAclFromBirth_NoLeftovers()
    {
        Installer.SecureDataDir(Pult, new TestPolicy());
        Assert.True(Directory.Exists(Path.Combine(Pult, "res")));
        Assert.True(new DirectoryInfo(Pult).GetAccessControl().AreAccessRulesProtected);            // права заданы при создании
        Assert.Equal(new[] { Pult }, Directory.GetFileSystemEntries(_d));                           // ни Pult.<guid>, ни .old
    }

    [Fact]
    public void DataDir_PreexistingUnsafe_MovedAside_NotDeleted_NewCreated()
    {
        Directory.CreateDirectory(Pult);
        File.WriteAllText(Path.Combine(Pult, "evil.txt"), "x");
        File.WriteAllText(Path.Combine(Pult, "admin.json"), """{"Apps":[{"Id":"x","Path":"C:\\evil.exe","Sha256":"00"}]}""");
        Installer.SecureDataDir(Pult, new TestPolicy());
        var old = Assert.Single(Aside());
        Assert.True(File.Exists(Path.Combine(old, "evil.txt")));                                    // отодвинута целиком
        Assert.False(File.Exists(Path.Combine(Pult, "admin.json")));
        Assert.Equal(new[] { "res" }, Directory.GetFileSystemEntries(Pult).Select(Path.GetFileName));
        Assert.True(new DirectoryInfo(Pult).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void DataDir_PreexistingJunction_LinkMovedAside_TargetUntouched()
    {
        var target = Path.Combine(_d, "System32-stand-in");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "kernel.dll"), "keep");
        RequestProcessorTests.Junction(Pult, target);
        Installer.SecureDataDir(Pult, new TestPolicy());
        var old = Assert.Single(Aside());
        Assert.True((File.GetAttributes(old) & FileAttributes.ReparsePoint) != 0);                 // перенесена сама ссылка
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "kernel.dll")));
        Assert.False((File.GetAttributes(Pult) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void DataDir_PreexistingFile_MovedAside()
    {
        File.WriteAllText(Pult, "not a dir");
        Installer.SecureDataDir(Pult, new TestPolicy());
        Assert.Single(Aside());
        Assert.True(Directory.Exists(Pult));
    }

    [Fact]
    public void DataDir_PreexistingSafe_Kept()
    {
        Installer.SecureDataDir(Pult, new TestPolicy());
        File.WriteAllText(Path.Combine(Pult, "admin.json"), "{}");
        Directory.Delete(Path.Combine(Pult, "res"));
        Installer.SecureDataDir(Pult, new TestPolicy());
        Assert.Empty(Aside());
        Assert.True(File.Exists(Path.Combine(Pult, "admin.json")));
        Assert.True(Directory.Exists(Path.Combine(Pult, "res")));
    }

    // ---------- список доступа папки данных / admin.json ----------

    private static DirectorySecurity AdminOnlyDir()
    {
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var s = new DirectorySecurity();
        s.SetOwner(PathTrust.Admins);
        s.AddAccessRule(new FileSystemAccessRule(PathTrust.Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(PathTrust.LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return s;
    }

    [Fact]
    public void DataDirAcl_AdminOnly_Accepted_UsersOrMeWritable_Rejected()
    {
        var dir = new DirectoryInfo(_d);
        Assert.Null(SafeFile.ProtectedProblem(dir, AdminOnlyDir));
        foreach (var (sid, rights) in new[] { (Users, FileSystemRights.Write), (Me, FileSystemRights.Modify), (Me, FileSystemRights.ChangePermissions),
                                              (Users, FileSystemRights.CreateFiles), (Users, FileSystemRights.DeleteSubdirectoriesAndFiles) })
        {
            var s = AdminOnlyDir();
            s.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Assert.NotNull(SafeFile.ProtectedProblem(dir, () => s));
        }
    }

    [Fact]
    public void DataDirAcl_RealTempDirAndFile_Rejected()        // временная папка пользователя — не защищена
    {
        var f = Path.Combine(_d, "admin.json"); File.WriteAllText(f, "{}");
        Assert.NotNull(SafeFile.ProtectedDirProblem(_d));
        Assert.NotNull(SafeFile.ProtectedFileProblem(f));
        Assert.NotNull(new AdminDataDirPolicy().Problem(_d));
    }

    [Fact]
    public void ResponseDirUnsafe_AtWriteTime_NoResponse_NoExecution()
    {
        var res = Path.Combine(_d, "res"); Directory.CreateDirectory(res);
        var req = Path.Combine(_d, "admin.req");
        File.WriteAllText(req, AdminRequest.New(AdminAction.RestorePoint, "").ToJson());
        var exec = new RequestProcessorTests.Exec();
        var p = new RequestProcessor(req, res, SafeFile.ProtectedDirProblem, exec);
        Assert.Throws<InvalidOperationException>(() => p.ProcessOnce());
        Assert.Empty(exec.Calls);
        Assert.Empty(Directory.GetFiles(res));
    }

}
