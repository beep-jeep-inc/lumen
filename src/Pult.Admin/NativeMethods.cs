using System.Runtime.InteropServices;
using Pult.Core.Admin;

namespace Pult.Admin;

internal static partial class NativeMethods
{
    public const uint LoadLibrarySearchApplicationDir = 0x200, LoadLibrarySearchSystem32 = 0x800;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDefaultDllDirectories(uint flags);

    private const uint MbYesNo = 0x4, MbIconWarning = 0x30, MbDefButton2 = 0x100, MbSetForeground = 0x10000, MbTopMost = 0x40000;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxTimeoutW(nint hWnd, string text, string caption, uint type, ushort languageId, uint milliseconds);

    /// «Да/Нет» с авто-«Нет» по таймеру (MessageBoxTimeoutW из user32 возвращает 32000, когда время вышло). По умолчанию — «Нет».
    public static ConfirmAnswer AskYesNo(string text, string caption, TimeSpan timeout)
    {
        var r = MessageBoxTimeoutW(0, text, caption, MbYesNo | MbIconWarning | MbDefButton2 | MbSetForeground | MbTopMost, 0, (uint)timeout.TotalMilliseconds);
        return r == 6 ? ConfirmAnswer.Yes : r == 32000 ? ConfirmAnswer.Timeout : ConfirmAnswer.No;
    }

    /// Окно «Да/Нет» самого помощника (уже с правами администратора); по умолчанию — «Нет».
    public static bool AskYesNo(string text, string caption) =>
        MessageBoxW(0, text, caption, MbYesNo | MbIconWarning | MbDefButton2 | MbSetForeground | MbTopMost) == 6;
}
