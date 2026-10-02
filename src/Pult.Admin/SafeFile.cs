using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Pult.Core.Admin;

namespace Pult.Admin;

/// Чтение файлов из папок пользователя и проверка своих папок — без перехода по ссылкам (junction/symlink/hardlink).
public static partial class SafeFile
{
    /// Прочитать обычный файл не больше maxBytes. null — если это ссылка, путь уходит через ссылку в другое место,
    /// у файла несколько имён (hardlink), он больше лимита или его нет.
    public static byte[]? ReadRegular(string path, long maxBytes)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { return null; }
        using var h = CreateFile(full, GenericRead, FileShareRead, 0, OpenExisting, FlagOpenReparsePoint | SqosPresent | SecurityIdentification, 0);
        if (h.IsInvalid) return null;
        if (!GetFileInformationByHandle(h, out var info)) return null;
        if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
        if (info.NumberOfLinks != 1) return null;
        long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (size > maxBytes) return null;
        if (!string.Equals(FinalPath(h), full, StringComparison.OrdinalIgnoreCase)) return null;   // папка в пути — ссылка
        using var fs = new FileStream(h, FileAccess.Read);
        var buf = new byte[size];
        fs.ReadExactly(buf);
        return fs.ReadByte() == -1 ? buf : null;                                                      // файл вырос во время чтения
    }

    /// Папка помощника (C:\ProgramData\Pult и её res) годится, только если это не ссылка, владелец — администраторы/SYSTEM
    /// и по списку доступа менять её может только система (обычным пользователям — только чтение).
    public static string? ProtectedDirProblem(string dir)
    {
        var d = new DirectoryInfo(dir);
        if (!d.Exists) return $"нет папки {dir}";
        return ProtectedProblem(d, () => d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
    }

    /// То же для admin.json; файла нет — не проблема (будет пустой набор).
    public static string? ProtectedFileProblem(string file)
    {
        var f = new FileInfo(file);
        if (!f.Exists) return null;
        return ProtectedProblem(f, () => f.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
    }

    /// Не ссылка, владелец — администраторы/SYSTEM/TrustedInstaller, и никто другой не может писать, создавать, удалять, менять права.
    public static string? ProtectedProblem(FileSystemInfo i, Func<FileSystemSecurity> acl)
    {
        try
        {
            if ((i.Attributes & FileAttributes.ReparsePoint) != 0) return $"{i.FullName} — ссылка";
            return PathTrust.CheckSecurity(acl(), strict: true) is { } why ? $"{i.FullName}: {why}" : null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException) { return $"{i.FullName}: {e.Message}"; }
    }

    internal static unsafe string? FinalPath(SafeFileHandle h)
    {
        var buf = new char[1024];
        uint n;
        fixed (char* p = buf) n = GetFinalPathNameByHandle(h, p, (uint)buf.Length, 0);
        if (n == 0 || n >= buf.Length) return null;
        var s = new string(buf, 0, (int)n);
        return s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? null : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    private const uint GenericRead = 0x80000000, FileShareRead = 1, OpenExisting = 3, FlagOpenReparsePoint = 0x00200000,
                       SqosPresent = 0x00100000, SecurityIdentification = 0x00010000;   // путь ведёт в канал — свой токен ему не отдаём

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint sa, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle h, char* buf, uint len, uint flags);
}
