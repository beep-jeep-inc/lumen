using System.Text.RegularExpressions;

namespace Pult.Core.Maintenance;

public enum WingetMode { Upgrade, Install }

/// Аргумент действия помощника WingetUpgrade: «upgrade|Id» или «install|Id». Id — как у winget (буквы, цифры, «.», «_», «+», «-»), без пробелов,
/// без начального «-» (чтобы Id не превратился в ключ командной строки). Любые лишние части и символы — отказ.
public static class WingetArg
{
    public const int MaxLength = 8 + 128;
    private static readonly Regex IdRx = new(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}\z", RegexOptions.CultureInvariant);

    public static bool IsValidId(string? id) => id != null && IdRx.IsMatch(id);

    public static string Build(WingetMode mode, string id) => (mode == WingetMode.Install ? "install|" : "upgrade|") + id;

    /// Режим — только точная строка «upgrade» или «install» (строчными); частей ровно две.
    public static bool TryParse(string? arg, out WingetMode mode, out string id, out string problem)
    {
        mode = WingetMode.Upgrade; id = ""; problem = "";
        if (string.IsNullOrEmpty(arg)) { problem = "не указан пакет winget"; return false; }
        if (arg.Length > MaxLength) { problem = "слишком длинный аргумент"; return false; }
        var parts = arg.Split('|');
        if (parts.Length != 2) { problem = "аргумент должен быть вида «upgrade|Id» или «install|Id»"; return false; }
        if (parts[0] == "upgrade") mode = WingetMode.Upgrade;
        else if (parts[0] == "install") mode = WingetMode.Install;
        else { problem = "неизвестный режим winget (нужен upgrade или install)"; return false; }
        if (!IsValidId(parts[1])) { problem = "недопустимый Id пакета winget"; return false; }
        id = parts[1];
        return true;
    }

    /// Фиксированные аргументы winget для помощника (повышенно). Id уже проверен; в строку они не склеиваются — идут отдельными элементами.
    public static IReadOnlyList<string> CommandArgs(WingetMode mode, string id) => new[]
    {
        mode == WingetMode.Install ? "install" : "upgrade", "--id", id, "--exact", "--silent",
        "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
    };

    /// Текст окна подтверждения помощника: Id пакета и слова «Обновить/Установить с правами администратора?».
    public static string ConfirmText(WingetMode mode, string id) =>
        (mode == WingetMode.Install ? "Установить" : "Обновить") + " пакет winget с правами администратора?\n\n" + id +
        "\n\nLumen просит помощника запустить winget с повышенными правами. Если ты этого не запрашивал(а) — нажми «Нет».";

    public const string DeclinedMessage = "Ты отказался в окне помощника — ничего не выполнено";

    public static string ExitCode(int code) => AdminCodesPrefix + (uint)code;
    private const string AdminCodesPrefix = Admin.AdminCodes.WingetExitPrefix;

    /// «winget=2316632137» → код выхода winget; null — это не такой код.
    public static int? TryParseExitCode(string? code)
    {
        if (code == null || !code.StartsWith(AdminCodesPrefix, StringComparison.Ordinal)) return null;
        return uint.TryParse(code.AsSpan(AdminCodesPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var u) ? unchecked((int)u) : null;
    }
}
