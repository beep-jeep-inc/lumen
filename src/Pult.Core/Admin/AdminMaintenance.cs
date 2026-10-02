using System.Text.RegularExpressions;

namespace Pult.Core.Admin;

/// Фиксированные наборы и проверки для действий обслуживания помощника (DriverInstall, RestorePoint, CleanSystem).
/// Пользователь (и любая программа, пишущая admin.req) может передать только id из этих наборов — не пути и не команды.
public static class AdminMaintenance
{
    public const int MaxDriverIds = 50;

    public const string WindowsTemp = "WindowsTemp", WuDownload = "WuDownload", Wer = "Wer", ComponentCleanup = "ComponentCleanup";
    public static readonly IReadOnlyList<string> CleanIds = new[] { WindowsTemp, WuDownload, Wer, ComponentCleanup };

    // WUA: UpdateID (GUID) и через «_» RevisionNumber; ревизия 0 в id опускается (см. WuaSearch). Только ASCII-цифры ([0-9], не \d), \z — не «$».
    private static readonly Regex IdRx = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(_[0-9]{1,9})?\z",
                                             RegexOptions.CultureInvariant);

    public static bool IsValidUpdateId(string? id) => id != null && IdRx.IsMatch(id);
    public static bool IsCleanId(string? id) => id != null && CleanIds.Contains(id, StringComparer.Ordinal);

    /// Необязательное начало аргумента DriverInstall: «norp:id1,id2» — без точки восстановления (ровно так, строчными, один раз).
    public const string NoRestorePrefix = "norp:";

    public static string DriverArg(IEnumerable<string> ids, bool skipRestore) => (skipRestore ? NoRestorePrefix : "") + string.Join(",", ids);

    /// «[norp:]id1,id2,…» → список в нижнем регистре и признак «без точки восстановления». false — пусто, больше 50, пробелы/пустые элементы, плохой формат, повтор, повторный/чужой префикс.
    public static bool TryParseDriverIds(string? arg, out IReadOnlyList<string> ids, out bool skipRestore, out string problem)
    {
        ids = Array.Empty<string>(); skipRestore = false; problem = "";
        if (string.IsNullOrEmpty(arg)) { problem = "не указаны обновления драйверов"; return false; }
        if (arg.Length > NoRestorePrefix.Length + MaxDriverIds * 48) { problem = "слишком длинный список обновлений"; return false; }
        if (arg.StartsWith(NoRestorePrefix, StringComparison.Ordinal)) { skipRestore = true; arg = arg[NoRestorePrefix.Length..]; }
        if (arg.Length == 0) { problem = "не указаны обновления драйверов"; skipRestore = false; return false; }
        if (arg.Length > MaxDriverIds * 48) { problem = "слишком длинный список обновлений"; skipRestore = false; return false; }
        var parts = arg.Split(',');
        if (parts.Length > MaxDriverIds) { problem = $"не больше {MaxDriverIds} обновлений за раз"; skipRestore = false; return false; }
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (!IsValidUpdateId(p)) { problem = "недопустимый идентификатор обновления"; skipRestore = false; return false; }
            list.Add(p.ToLowerInvariant());
        }
        if (list.Distinct(StringComparer.Ordinal).Count() != list.Count) { problem = "повторяющиеся идентификаторы обновлений"; skipRestore = false; return false; }
        ids = list;
        return true;
    }

    // Бюджеты: поиск (до 5) + окно подтверждения (до 5, потом авто-«Нет») + шаг SYSTEM-исполнителя (драйверы до 35, DISM до 62) + запас.
    public static readonly TimeSpan DriverInstallWait = TimeSpan.FromMinutes(50), ComponentCleanupWait = TimeSpan.FromMinutes(72),
                                    RestorePointWait = TimeSpan.FromMinutes(10), CleanWait = TimeSpan.FromMinutes(20),
                                    ConfirmTimeout = TimeSpan.FromMinutes(5), SearchStep = TimeSpan.FromMinutes(5),
                                    DriverInstallStep = TimeSpan.FromMinutes(35), RestoreStep = TimeSpan.FromMinutes(5),
                                    CleanStep = TimeSpan.FromMinutes(10), ComponentStep = TimeSpan.FromMinutes(62),
                                    DefaultWait = TimeSpan.FromSeconds(30), StartupWait = ConfirmTimeout + TimeSpan.FromSeconds(30);

    // Починка Windows: окно подтверждения (до 5) + DISM (до 45) + sfc (до 30) + запас; ограничения заданий — 90 (SYSTEM) и 95 минут (помощник). На медленных дисках RestoreHealth идёт 30–45 минут.
    public static readonly TimeSpan RepairStep = TimeSpan.FromMinutes(79), RepairWait = TimeSpan.FromMinutes(91);
    // Winget с правами администратора: окно подтверждения (до 5) + winget (до 30) + запас.
    // Здоровье дисков: запуск помощника + PowerShell и запрос CIM (до 40 с) + запас.
    public static readonly TimeSpan DiskDetailsProcessTimeout = TimeSpan.FromSeconds(40), DiskDetailsStep = TimeSpan.FromSeconds(50), DiskDetailsWait = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan WingetProcessTimeout = TimeSpan.FromMinutes(30), WingetWait = TimeSpan.FromMinutes(40);

    /// Сколько пульту ждать ответ помощника на это действие.
    public static TimeSpan WaitTimeout(AdminAction a, string arg) => a switch
    {
        AdminAction.DriverInstall => DriverInstallWait,
        AdminAction.RestorePoint => RestorePointWait,
        AdminAction.CleanSystem => arg == ComponentCleanup ? ComponentCleanupWait : CleanWait,
        AdminAction.StartupToggle => StartupWait,
        AdminAction.RepairSystem => RepairWait,
        AdminAction.WingetUpgrade => WingetWait,
        AdminAction.DiskDetails => DiskDetailsWait,
        _ => DefaultWait,
    };

    /// Итог CleanSystem в Code ответа: «freed=<байт>;skipped=<файлов>».
    public static string CleanCode(long freedBytes, int skipped) => $"freed={freedBytes};skipped={skipped}";

    public static bool TryParseCleanCode(string? code, out long freedBytes, out int skipped)
    {
        freedBytes = 0; skipped = 0;
        var m = code == null ? null : Regex.Match(code, @"^freed=([0-9]{1,18});skipped=([0-9]{1,9})\z", RegexOptions.CultureInvariant);
        if (m is not { Success: true }) return false;
        freedBytes = long.Parse(m.Groups[1].Value); skipped = int.Parse(m.Groups[2].Value);
        return true;
    }
}
