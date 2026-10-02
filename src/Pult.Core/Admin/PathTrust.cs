using System.Security.AccessControl;
using System.Security.Principal;

namespace Pult.Core.Admin;

/// Можно ли помощнику администратора доверять этому месту (winget с повышенными правами, папки самого помощника): файл и папки до корня диска
/// должна мочь менять только система (Администраторы, SYSTEM, TrustedInstaller).
public interface IPathTrust
{
    /// Права на файл и цепочку папок до корня диска (для winget). null — защищено; иначе понятная причина.
    string? FileChainProblem(string path);
    /// Для папок под защитой помощника: сама папка и путь до корня.
    string? DirProblem(string dir);
}

public sealed class PathTrust : IPathTrust
{
    public static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    // Права, которые дают изменить сам файл или добавить/заменить что-то в папке с программой (подкладка DLL).
    private const int WriteData = 0x2, AppendData = 0x4, DeleteChild = 0x40, Delete = 0x10000,
                      WriteDac = 0x40000, WriteOwner = 0x80000, GenericAll = 0x10000000, GenericWrite = 0x40000000;
    private const int StrictMask = WriteData | AppendData | DeleteChild | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;
    // Для папок выше: добавить новую папку рядом безопасно (так устроен C:\), опасно — удалить/переименовать путь или сменить права.
    private const int AncestorMask = DeleteChild | Delete | WriteDac | WriteOwner | GenericAll;

    public static bool IsTrustedOwner(SecurityIdentifier? sid) =>
        sid != null && (sid == Admins || sid == LocalSystem || sid == TrustedInstaller);

    private static bool MayWrite(SecurityIdentifier sid) => IsTrustedOwner(sid) || sid == CreatorOwner || IsWindowsTrustLabel(sid);

    /// Метка доверия защищённых процессов S-1-19-&lt;тип&gt;-&lt;уровень&gt; (стоит на файлах пакетов Store, например
    /// S-1-19-512-4096 на winget.exe): её несут только защищённые процессы самой Windows (уровень Windows=4096,
    /// WinTcb=8192) — обычная программа её не получит. Антивирусные и магазинные уровни (ниже 4096) не доверяем.
    public static bool IsWindowsTrustLabel(SecurityIdentifier sid)
    {
        var p = sid.Value.Split('-');
        return p.Length == 5 && p[2] == "19" && (p[3] == "512" || p[3] == "1024")
            && int.TryParse(p[4], out var level) && level >= 4096;
    }

    /// Чистая проверка владельца и списка доступа. strict — для самого файла и его папки; иначе — для папок выше.
    public static string? CheckSecurity(FileSystemSecurity sec, bool strict)
    {
        if (!IsTrustedOwner(sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) return "владелец — не администратор";
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;   // к самому объекту не относится
            if (r.IdentityReference is not SecurityIdentifier sid || MayWrite(sid)) continue;
            if (((int)r.FileSystemRights & (strict ? StrictMask : AncestorMask)) != 0) return $"менять может {sid.Value}";
        }
        return null;
    }

    /// Права на сам файл и на цепочку папок до корня диска, без проверок по имени программы.
    /// null — файл и все папки менять могут только Администраторы/SYSTEM/TrustedInstaller; иначе причина.
    public string? FileChainProblem(string path)
    {
        if (!IsLocalFullPath(path)) return "нужен полный путь на локальном диске";
        try
        {
            var fi = new FileInfo(Path.GetFullPath(path));
            if (!fi.Exists) return "файл программы не найден";
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) return "файл — ссылка на другой файл";
            if (CheckSecurity(fi.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), strict: true) is { } why)
                return "файл: " + why;
            return ChainProblem(fi.Directory!);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return "не удалось проверить права на файл: " + e.Message; }
    }

    /// Для программы из своей небольшой папки пакета (winget.exe в WindowsApps): FileChainProblem + все .dll/.exe
    /// этой папки и её подпапок — повышенный процесс загрузит их вместе с файлом (WindowsPackageManager.dll и др.),
    /// так что подменить нельзя должно быть и их. Для System32 не годится — там тысячи файлов.
    public string? PackageProblem(string path)
    {
        if (FileChainProblem(path) is { } why) return why;
        try
        {
            var dir = new FileInfo(Path.GetFullPath(path)).Directory!;
            int n = 0;
            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var ext = f.Extension.ToLowerInvariant();
                if (ext != ".dll" && ext != ".exe") continue;
                if (++n > 2000) return "в папке программы слишком много файлов — проверить не могу";
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0) return $"{f.FullName} — ссылка на другой файл";
                if (CheckSecurity(f.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), strict: true) is { } fwhy)
                    return $"{f.FullName}: {fwhy}";
            }
            foreach (var d in dir.EnumerateDirectories("*", SearchOption.AllDirectories))
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return $"{d.FullName} — ссылка на другую папку";
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return "не удалось проверить права на файлы программы: " + e.Message; }
    }

    public string? DirProblem(string dir)
    {
        if (!IsLocalFullPath(dir)) return "нужен полный путь на локальном диске";
        try { return ChainProblem(new DirectoryInfo(Path.GetFullPath(dir))); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException)
        { return "не удалось проверить права на папку: " + e.Message; }
    }

    private static string? ChainProblem(DirectoryInfo first)
    {
        bool strict = true;
        for (var d = first; d != null; d = d.Parent, strict = false)
        {
            if (!d.Exists) return $"нет папки {d.FullName}";
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return $"{d.FullName} — ссылка на другую папку";
            if (CheckSecurity(d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), strict) is { } why)
                return $"{d.FullName}: {why}";
        }
        return null;
    }

    private static bool IsLocalFullPath(string p) =>
        p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\' && Path.IsPathFullyQualified(p);
}
