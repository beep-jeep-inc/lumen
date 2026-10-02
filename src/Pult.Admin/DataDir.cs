using System.Security.AccessControl;
using System.Security.Principal;
using Pult.Core.Admin;

namespace Pult.Admin;

/// Как создавать и проверять папку данных помощника (в тестах — подставная: без прав администратора владельца не сменить).
public interface IDataDirPolicy
{
    /// Права новой папки — задаются в момент создания, чтобы никто не успел открыть её на запись.
    DirectorySecurity NewSecurity();
    /// null — папка и всё внутри под защитой; иначе причина.
    string? Problem(string dir);
}

public sealed class AdminDataDirPolicy : IDataDirPolicy
{
    public DirectorySecurity NewSecurity()
    {
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var sec = new DirectorySecurity();
        sec.SetOwner(PathTrust.Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(PathTrust.Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(PathTrust.LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// Сама папка и каждый файл/папка внутри: не ссылка, владелец — система, писать может только система.
    public string? Problem(string dir)
    {
        if (SafeFile.ProtectedDirProblem(dir) is { } why) return why;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
            {
                var p = e is DirectoryInfo d
                    ? SafeFile.ProtectedProblem(d, () => d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access))
                    : SafeFile.ProtectedProblem(e, () => ((FileInfo)e).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
                if (p != null) return p;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }
}

public static partial class Installer
{
    public static void SecureDataDir() => SecureDataDir(DataDir, new AdminDataDirPolicy());

    /// C:\ProgramData\Pult: любая программа может создать её заранее (и держать открытой с правом менять права).
    /// Поэтому: годную папку оставляем; негодную (ссылка, файл, чужой владелец, запись для не-администраторов, чужие файлы
    /// внутри) отодвигаем в Pult.old-&lt;время&gt; без обхода содержимого; новую создаём под случайным именем сразу с защитными
    /// правами и переименовываем на место. Имя успели занять — повторяем; не вышло — отказ.
    public static void SecureDataDir(string dir, IDataDirPolicy policy)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (TryGetAttributes(dir) is { } attrs)
            {
                bool realDir = (attrs & FileAttributes.Directory) != 0 && (attrs & FileAttributes.ReparsePoint) == 0;
                if (realDir && policy.Problem(dir) == null)
                {
                    var res = Path.Combine(dir, "res");
                    if (!Directory.Exists(res)) Directory.CreateDirectory(res);                     // права наследует от защищённой папки
                    if (policy.Problem(dir) is { } w) throw new InvalidOperationException("папка данных помощника не под защитой: " + w);
                    return;
                }
                var aside = $"{dir}.old-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
                if ((attrs & FileAttributes.Directory) != 0) Directory.Move(dir, aside); else File.Move(dir, aside);   // ссылку переносит саму; ошибка — отказ
            }
            var fresh = $"{dir}.{Guid.NewGuid():N}";
            policy.NewSecurity().CreateDirectory(fresh);
            Directory.CreateDirectory(Path.Combine(fresh, "res"));
            try { Directory.Move(fresh, dir); }
            catch (IOException)                                                                        // «Pult» снова кто-то создал
            {
                try { Directory.Delete(fresh, true); } catch (IOException) { }
                continue;
            }
            if (policy.Problem(dir) is { } why) throw new InvalidOperationException("не удалось защитить папку данных помощника: " + why);
            return;
        }
        throw new InvalidOperationException("не удалось создать папку данных помощника: имя всё время занимают — повтори установку");
    }
}
