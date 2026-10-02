using Pult.Core.Monitoring;
using Pult.Core.Admin;


namespace Pult.Core.Cleanup;

/// Строка списка очистки. Bytes = -1 — размер неизвестен («размер узнаю при очистке»).
public sealed record CleanupItem(string Id, string Group, string Title, long Bytes, int Files, bool NeedsAdmin, string? BlockedBy, bool Checked, string? Note = null)
{
    public bool Blocked => BlockedBy != null;
    /// Показывать ли строку: пустые и отсутствующие категории не показываются (размер -1 — неизвестен, но есть что чистить).
    public bool Visible => Bytes != 0 || Files != 0;
}

/// FreedBytes освобождено, Skipped — сколько файлов пропущено (заняты / нет доступа), Error — человеческая причина, если не сделано.
public sealed record CleanResult(long FreedBytes, int Skipped, string? Error)
{
    public static readonly CleanResult Empty = new(0, 0, null);
    public CleanResult Plus(CleanResult o) => new(FreedBytes + o.FreedBytes, Skipped + o.Skipped, Error ?? o.Error);
}

public interface ICleanupCategory
{
    string Id { get; }
    string Group { get; }
    string Title { get; }
    bool NeedsAdmin { get; }
    Task<CleanupItem> ScanAsync(CancellationToken ct);
    /// Никогда не бросает наружу (кроме отмены): ошибки — в CleanResult.Error.
    Task<CleanResult> CleanAsync(CancellationToken ct);
}

public static class CleanupGroups
{
    public const string System = "Система", Apps = "Браузеры и программы", Dev = "Разработка", Space = "Что занимает место";
}

/// Корзина (Shell). Подставляется в тестах.
public interface IRecycleBin
{
    (long Bytes, long Items) Query();
    bool Empty();
}

/// Всё, от чего зависят категории: пути профиля, процессы, корзина, помощник администратора. Подставляется в тестах.
public sealed record CleanupEnv(
    string Temp, string LocalAppData, string AppData, string UserProfile, string? SteamPath,
    string WindowsDir, string ProgramData, string SystemDrive,
    IProcessQuery Procs, Func<DateTime> UtcNow, IRecycleBin Bin,
    Func<string, CancellationToken, Task<AdminResponse>>? Admin)
{
    public static CleanupEnv ForThisPc(AdminClient? admin = null)
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var win = F(Environment.SpecialFolder.Windows);
        var procs = new SystemProcessQuery(); procs.Snapshot();
        return new CleanupEnv(Path.GetTempPath().TrimEnd('\\'), F(Environment.SpecialFolder.LocalApplicationData), F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.UserProfile), SteamPaths.SteamPath(), win, F(Environment.SpecialFolder.CommonApplicationData),
            Path.GetPathRoot(win)!.TrimEnd('\\'), procs, () => DateTime.UtcNow, new ShellRecycleBin(),
            admin == null ? null : (id, ct) => admin.SendAsync(AdminAction.CleanSystem, id, ct, "Очищаю…"));
    }
}
