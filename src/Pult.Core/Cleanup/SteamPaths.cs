using Microsoft.Win32;

namespace Pult.Core.Cleanup;

/// Где установлен Steam (для строки «Кэш Steam» в очистке).
public static class SteamPaths
{
    public static string? SteamPath() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string p && Directory.Exists(p)
            ? Path.GetFullPath(p.Replace('/', '\\')) : null;
}
