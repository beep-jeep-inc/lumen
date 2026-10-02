using System.Globalization;
using System.Text.RegularExpressions;

namespace Pult.Core.Maintenance;

/// Что предложить после неудачного обновления winget.
public enum WingetFix { None, RetryAsAdmin, Reinstall }

/// Разбор причин неудачи winget (чистые функции). Winget при падении установщика отвечает общим кодом (0x8A150049 «установщик завершился с ошибкой»),
/// а настоящая причина — «Installer failed with exit code: 1603» в тексте; поэтому смотрим и код winget, и код установщика, и слова про права.
public static class WingetFailure
{
    /// «Технология установки отличается» (в системе стоит, например, переносная версия, а в каталоге — установщик).
    public const int InstallTechnologyDiffers = unchecked((int)0x8A15008E);
    /// APPINSTALLER_CLI_ERROR_COMMAND_REQUIRES_ADMIN (проверено «winget error 0x8A150019»).
    public const int CommandRequiresAdmin = unchecked((int)0x8A150019);

    // Коды установщика, при которых помогает повторить с правами администратора:
    // 1603 — фатальная ошибка MSI (самая частая при нехватке прав), 5 — отказано в доступе, 740 — нужна elevation,
    // 0x80070005 (E_ACCESSDENIED) — и как 2147942405 / -2147024891.
    private static readonly HashSet<long> AdminInstallerCodes = new() { 1603, 5, 740, 0x80070005, -2147024891 };

    private static readonly Regex InstallerCodeRx = new(@"(?:exit code|кодом? выхода|код завершения)\s*[:=]?\s*(0x[0-9a-f]+|-?\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HexRx = new(@"0x80070005", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Слова winget и Windows про права (английская и русская локали).
    private static readonly string[] AdminWords =
    {
        "requires administrator", "administrator privileges", "run as administrator", "as an administrator", "elevated", "elevation",
        "access is denied", "access denied", "права администратора", "от имени администратора", "отказано в доступе",
    };

    /// Код, который установщик назвал в тексте winget («Installer failed with exit code: 1603»); null — не назвал.
    public static long? InstallerExitCode(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var m = InstallerCodeRx.Match(output);
        if (!m.Success) return null;
        var s = m.Groups[1].Value;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// Признак нехватки прав: код установщика из списка, 0x80070005 где-то в тексте или коде, либо слова про права администратора.
    public static bool NeedsAdmin(WingetRun r)
    {
        if (r.Ok || r.Cancelled || r.TimedOut || r.StartError != null) return false;
        if (r.ExitCode == NoApplicableUpdateAsInt) return false;                 // «обновление не требуется» — не неудача
        if (r.ExitCode == unchecked((int)0x80070005) || r.ExitCode == CommandRequiresAdmin) return true;
        var output = r.Output ?? "";
        if (InstallerExitCode(output) is { } c && AdminInstallerCodes.Contains(c)) return true;
        if (HexRx.IsMatch(output)) return true;
        var low = output.ToLowerInvariant();
        return AdminWords.Any(low.Contains);
    }

    private static int NoApplicableUpdateAsInt => WingetRunner.NoApplicableUpdate;

    public static bool NeedsReinstall(WingetRun r) => !r.Ok && !r.Cancelled && !r.TimedOut && r.StartError == null && r.ExitCode == InstallTechnologyDiffers;

    /// Что предложить: переустановку (технология отличается) важнее повтора с правами.
    public static WingetFix Suggest(WingetRun r) => NeedsReinstall(r) ? WingetFix.Reinstall : NeedsAdmin(r) ? WingetFix.RetryAsAdmin : WingetFix.None;
}
