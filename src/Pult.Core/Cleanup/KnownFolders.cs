using System.Runtime.InteropServices;

namespace Pult.Core.Cleanup;

/// Личные папки Windows там, где они лежат на самом деле: «Загрузки», «Документы», «Рабочий стол» часто перенесены на другой диск
/// (свойства папки → Расположение). Путь «Профиль\Downloads» в таком случае пуст или не тот.
public static class KnownFolders
{
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    public static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    public static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    public static readonly Guid Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// Путь известной папки для текущего пользователя; null — Windows не ответила.
    public static string? PathOf(Guid id)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            return SHGetKnownFolderPath(id, 0, IntPtr.Zero, out p) == 0 ? Marshal.PtrToStringUni(p) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DownloadsDir() => PathOf(Downloads) is { Length: > 0 } d ? d : Path.Combine(Profile, "Downloads");

    /// Все личные папки: где бы ни лежали. Несуществующие не отбрасываются — это решает вызывающий.
    public static IReadOnlyList<string> Personal() => new[]
    {
        DownloadsDir(), OrFallback(Documents, Environment.SpecialFolder.MyDocuments), OrFallback(Desktop, Environment.SpecialFolder.DesktopDirectory),
        OrFallback(Pictures, Environment.SpecialFolder.MyPictures), OrFallback(Videos, Environment.SpecialFolder.MyVideos), OrFallback(Music, Environment.SpecialFolder.MyMusic),
    }.Where(p => !string.IsNullOrEmpty(p)).ToList();

    private static string OrFallback(Guid id, Environment.SpecialFolder f) => PathOf(id) is { Length: > 0 } p ? p : Environment.GetFolderPath(f);

    /// Где можно удалять в Корзину: профиль + известные папки (где бы ни лежали). Корни дисков отбрасываются — «D:\» целиком разрешать нельзя.
    public static IReadOnlyList<string> RecycleRoots(string profile, IEnumerable<string?> known)
    {
        var list = new List<string>();
        foreach (var raw in new[] { profile }.Concat(known))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = SafeDelete.Norm(raw); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (SafeDelete.IsDriveRoot(full) || list.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
            list.Add(full);
        }
        return list;
    }

    /// Известные папки, которые не лежат внутри профиля (перенесены на другой диск): их надо просматривать отдельно от профиля.
    public static IReadOnlyList<string> OutsideProfile(string profile, IEnumerable<string?> known) =>
        RecycleRoots(profile, known).Skip(1).Where(r => !SafeDelete.IsUnder(profile, r)).ToList();
}
