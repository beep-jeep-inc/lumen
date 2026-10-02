namespace Pult.Core.Maintenance;

public sealed record UpdateCommand(string FileName, IReadOnlyList<string> Args);

/// winget: где лежит и какими командами обновлять/ставить. Запуск — на стороне окна.
public static class Winget
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");

    /// Путь к winget.exe: сначала WindowsApps (туда кладёт App Installer), потом PATH. null — не найден.
    public static string? Find()
    {
        try
        {
            if (File.Exists(DefaultPath)) return DefaultPath;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), "winget.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex PackageDirRx =
        new(@"^Microsoft\.DesktopAppInstaller_(\d+(?:\.\d+){1,3})_(x64|x86|arm64)__8wekyb3d8bbwe\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// Поиск winget для ПОМОЩНИКА (запуск с повышенными правами): ничего из PATH, профиля пользователя и окружения запроса.
    /// Только папка самого пакета «Установщик приложений» в C:\Program Files\WindowsApps, берётся самая новая версия нужной разрядности.
    /// Что папка действительно защищена, проверяет вызывающий (PathTrust.FileChainProblem) — на некоторых ПК права WindowsApps ослаблены.
    /// Псевдоним в %LOCALAPPDATA% не используется: папка принадлежит пользователю, ссылку в ней можно подменить.
    /// Все обращения к диску передаются снаружи (в тестах — подставные). null — не нашли.
    public static string? FindForHelper(string programFiles, string arch, Func<string, IEnumerable<string>> listDirNames, Func<string, bool> fileExists)
    {
        try
        {
            var root = Path.Combine(programFiles, "WindowsApps");
            return listDirNames(root)
                .Select(n => (Name: n, M: PackageDirRx.Match(n)))
                .Where(x => x.M.Success && string.Equals(x.M.Groups[2].Value, arch, StringComparison.OrdinalIgnoreCase) && Version.TryParse(x.M.Groups[1].Value, out _))
                .OrderByDescending(x => Version.Parse(x.M.Groups[1].Value))
                .Select(x => Path.Combine(root, x.Name, "winget.exe"))
                .FirstOrDefault(fileExists);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// То же на настоящем диске.
    public static string? FindForHelper() => FindForHelper(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch { System.Runtime.InteropServices.Architecture.Arm64 => "arm64", System.Runtime.InteropServices.Architecture.X86 => "x86", _ => "x64" },
        root => Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(d => Path.GetFileName(d)!).ToList() : new List<string>(),
        File.Exists);

    /// «--disable-interactivity» появился в winget 1.4: на старом App Installer каждая команда с ним падает («argument not recognized»).
    public static readonly Version DisableInteractivityFrom = new(1, 4);
    /// Старше этой версии нет нужных команд (upgrade --id --exact, --accept-source-agreements): проще обновить App Installer.
    public static readonly Version MinUsable = new(1, 2);

    /// Версия неизвестна (не смогли узнать) — считаем winget свежим, как раньше.
    public static bool SupportsDisableInteractivity(Version? v) => v == null || v >= DisableInteractivityFrom;
    public static bool IsTooOld(Version? v) => v != null && v < MinUsable;

    private static readonly System.Text.RegularExpressions.Regex VersionRx = new(@"(\d+)\.(\d+)(?:\.(\d+))?", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// Вывод «winget --version»: «v1.29.380», «v1.9.25180-preview». null — не похоже на версию.
    public static Version? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = VersionRx.Match(output);
        if (!m.Success) return null;
        try { return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0); }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException) { return null; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Version> VersionCache = new(StringComparer.OrdinalIgnoreCase);

    /// Версия winget: спрашиваем один раз за сессию (удачный ответ запоминается). null — не узнали.
    public static async Task<Version?> VersionAsync(string wingetPath, CancellationToken ct)
    {
        if (VersionCache.TryGetValue(wingetPath, out var known)) return known;
        var run = await WingetRunner.RunAsync(wingetPath, new[] { "--version" }, TimeSpan.FromSeconds(20), ct);
        var v = run.Ok ? ParseVersion(run.Output) : null;
        if (v != null) VersionCache[wingetPath] = v;
        return v;
    }

    private static readonly string[] Tail =
        { "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements" };

    private static UpdateCommand Build(string wingetPath, string verb, string id, Version? v) =>
        new(wingetPath, new[] { verb, "--id", id }.Concat(Tail).Concat(SupportsDisableInteractivity(v) ? new[] { "--disable-interactivity" } : Array.Empty<string>()).ToList());

    public static UpdateCommand UpgradeCommand(string wingetPath, string id, Version? wingetVersion = null) => Build(wingetPath, "upgrade", id, wingetVersion);
    public static UpdateCommand InstallCommand(string wingetPath, string id, Version? wingetVersion = null) => Build(wingetPath, "install", id, wingetVersion);

    /// Удаление пакета (от пользователя, для «Переустановить»): тот же стиль аргументов, без --accept-package-agreements (у uninstall его нет).
    public static UpdateCommand UninstallCommand(string wingetPath, string id, Version? wingetVersion = null) =>
        new(wingetPath, new[] { "uninstall", "--id", id, "--exact", "--silent", "--accept-source-agreements" }
            .Concat(SupportsDisableInteractivity(wingetVersion) ? new[] { "--disable-interactivity" } : Array.Empty<string>()).ToList());

    /// Аргументы списка обновлений («winget upgrade»).
    public static IReadOnlyList<string> ListUpgradesArgs(Version? wingetVersion = null) =>
        SupportsDisableInteractivity(wingetVersion) ? new[] { "upgrade", "--accept-source-agreements", "--disable-interactivity" }
                                                     : new[] { "upgrade", "--accept-source-agreements" };
}
