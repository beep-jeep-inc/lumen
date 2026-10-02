using Pult.Core.Admin;

namespace Pult.Core.Cleanup;

/// Цель очистки: dir чистится, base — корень категории (путь от base до dir не должен содержать ссылок).
public readonly record struct CleanTarget(string Base, string Dir);

/// Категории «Система» и «Браузеры и программы». Все имена папок — из фиксированных списков: кук, паролей, истории и закладок здесь нет ни в каком виде.
public static class UserCategories
{
    public static readonly TimeSpan TempMinAge = TimeSpan.FromDays(1);

    public static IReadOnlyList<ICleanupCategory> All(CleanupEnv env)
    {
        var list = new List<ICleanupCategory>();
        list.AddRange(SystemGroup(env));
        list.AddRange(AppsGroup(env));
        return list;
    }

    /// Сканирует все категории; пустые и отсутствующие не показываются. Ошибка одной категории остальные не ломает.
    public static async Task<IReadOnlyList<CleanupItem>> ScanAllAsync(IEnumerable<ICleanupCategory> cats, CancellationToken ct, IProgress<string>? progress = null)
    {
        var res = new List<CleanupItem>();
        foreach (var c in cats)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(c.Title);
            try { var i = await c.ScanAsync(ct); if (i.Visible) res.Add(i); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
        }
        return res;
    }

    public static IReadOnlyList<ICleanupCategory> SystemGroup(CleanupEnv env)
    {
        var explorer = Path.Combine(env.LocalAppData, "Microsoft", "Windows", "Explorer");
        Func<FileSystemInfo, bool> old = i => env.UtcNow() - i.LastWriteTimeUtc >= TempMinAge;
        var sys = Path.Combine(env.WindowsDir, "Temp");
        var wu = Path.Combine(env.WindowsDir, "SoftwareDistribution", "Download");
        var wer = Path.Combine(env.ProgramData, "Microsoft", "Windows", "WER");
        return new ICleanupCategory[]
        {
            new DirsCategory("UserTemp", CleanupGroups.System, "Временные файлы (старше суток)", env.Procs,
                () => new[] { new CleanTarget(env.Temp, env.Temp) }, old, true),
            new RecycleBinCategory(env.Bin),
            new DirsCategory("Thumbnails", CleanupGroups.System, "Кеш эскизов Проводника", env.Procs,
                () => new[] { new CleanTarget(explorer, explorer) },
                i => (i.Attributes & FileAttributes.Directory) == 0 && i.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase)
                     && i.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase), true),
            new AdminCategory(env, AdminMaintenance.WindowsTemp, "Временные файлы Windows (старше суток)", () => new[] { sys }, old),
            new AdminCategory(env, AdminMaintenance.WuDownload, "Скачанные обновления Windows", () => new[] { wu }, null),
            new AdminCategory(env, AdminMaintenance.Wer, "Отчёты об ошибках Windows",
                () => new[] { Path.Combine(wer, "ReportArchive"), Path.Combine(wer, "ReportQueue") }, null),
            new AdminCategory(env, AdminMaintenance.ComponentCleanup, "Старые компоненты Windows (DISM)", () => Array.Empty<string>(), null,
                "размер узнаю при очистке; необратимо: обновления Windows потом нельзя будет удалить; может идти до часа"),
            new WindowsOldCategory(env),
        };
    }

    public static IReadOnlyList<ICleanupCategory> AppsGroup(CleanupEnv env)
    {
        var list = new List<ICleanupCategory>();
        string L(params string[] p) => Path.Combine(new[] { env.LocalAppData }.Concat(p).ToArray());
        string R(params string[] p) => Path.Combine(new[] { env.AppData }.Concat(p).ToArray());

        // Браузеры: в профилях только папки кеша из белого списка.
        foreach (var (id, title, proc, ud) in new[]
        {
            ("Chrome", "Chrome", "chrome", L("Google", "Chrome", "User Data")),
            ("Edge", "Edge", "msedge", L("Microsoft", "Edge", "User Data")),
            ("Brave", "Brave", "brave", L("BraveSoftware", "Brave-Browser", "User Data")),
            ("Yandex", "Яндекс Браузер", "browser", L("Yandex", "YandexBrowser", "User Data")),
        })
        {
            var root = ud;
            list.Add(new DirsCategory("Browser." + id, CleanupGroups.Apps, $"{title}: кеш", env.Procs, () => BrowserTargets(root), null, true, processes: new[] { (title, proc) }, visibleWindowOnly: true));
        }
        var operaLocal = L("Opera Software", "Opera GX Stable"); var operaRoaming = R("Opera Software", "Opera GX Stable");
        list.Add(new DirsCategory("Browser.OperaGX", CleanupGroups.Apps, "Opera GX: кеш", env.Procs,
            () => ProfileTargets(operaLocal, operaLocal).Concat(ProfileTargets(operaRoaming, operaRoaming)).ToList(), null, true, processes: new[] { ("Opera GX", "opera") }, visibleWindowOnly: true));

        var discord = R("discord");
        list.Add(new DirsCategory("Discord", CleanupGroups.Apps, "Discord: кеш", env.Procs,
            () => new[] { "Cache", "Code Cache", "GPUCache" }.Select(d => new CleanTarget(discord, Path.Combine(discord, d))).ToList(), null, true, processes: new[] { ("Discord", "discord") }));

        var tg = R("Telegram Desktop");
        list.Add(new DirsCategory("Telegram", CleanupGroups.Apps, "Telegram: кеш и медиа-кеш", env.Procs,
            () => new[] { new CleanTarget(tg, Path.Combine(tg, "tdata", "user_data", "cache")), new CleanTarget(tg, Path.Combine(tg, "tdata", "user_data", "media_cache")) },
            null, true, processes: new[] { ("Telegram", "Telegram") }));

        var sp = L("Spotify");
        list.Add(new DirsCategory("Spotify", CleanupGroups.Apps, "Spotify: кеш", env.Procs,
            () => new[] { new CleanTarget(sp, Path.Combine(sp, "Storage")), new CleanTarget(sp, Path.Combine(sp, "Data")) }, null, true, processes: new[] { ("Spotify", "Spotify") }));

        var nv = L("NVIDIA");
        list.Add(new DirsCategory("Nvidia", CleanupGroups.Apps, "NVIDIA: кеш шейдеров", env.Procs,
            () => new[] { new CleanTarget(env.LocalAppData, Path.Combine(nv, "DXCache")), new CleanTarget(env.LocalAppData, Path.Combine(nv, "GLCache")),
                          new CleanTarget(env.LocalAppData, L("D3DSCache")) }, null, true));

        if (env.SteamPath is { } steam)
            list.Add(new DirsCategory("Steam", CleanupGroups.Apps, "Steam: кеш шейдеров, веб-кеш, логи", env.Procs,
                () => new[] { new CleanTarget(steam, Path.Combine(steam, "steamapps", "shadercache")), new CleanTarget(steam, Path.Combine(steam, "appcache", "httpcache")),
                              new CleanTarget(steam, Path.Combine(steam, "logs")) }, null, true, processes: new[] { ("Steam", "steam"), ("Steam", "steamwebhelper") }));
        return list;
    }

    /// Только эти подпапки профиля браузера. Cookies, Login Data, History, Bookmarks, Web Data, Local State, Preferences сюда не попадают.
    private static readonly string[] BrowserCacheDirs = { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", "ShaderCache" };

    public static IReadOnlyList<CleanTarget> ProfileTargets(string baseRoot, string profileDir) =>
        BrowserCacheDirs.Select(d => new CleanTarget(baseRoot, Path.Combine(profileDir, d))).ToList();

    public static IReadOnlyList<CleanTarget> BrowserTargets(string userData)
    {
        var list = new List<CleanTarget>();
        foreach (var d in new[] { "ShaderCache", "GrShaderCache" }) list.Add(new(userData, Path.Combine(userData, d)));
        try
        {
            if (!Directory.Exists(userData)) return list;
            foreach (var p in new DirectoryInfo(userData).EnumerateDirectories())
            {
                if ((p.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (p.Name == "Default" || p.Name.StartsWith("Profile ", StringComparison.Ordinal))
                    list.AddRange(ProfileTargets(userData, p.FullName));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }
}

/// Обычная папка-кеш: сканирует размер, чистит содержимое (сами папки кеша остаются). Закрытая программа — условие.
internal sealed class DirsCategory : ICleanupCategory
{
    private readonly Func<IReadOnlyList<CleanTarget>> _targets;
    private readonly Func<FileSystemInfo, bool>? _filter;
    private readonly IReadOnlyList<(string Display, string Process)> _procs;
    private readonly Monitoring.IProcessQuery _q;
    private readonly bool _checked; private readonly string? _note;

    public DirsCategory(string id, string group, string title, Monitoring.IProcessQuery q, Func<IReadOnlyList<CleanTarget>> targets,
                        Func<FileSystemInfo, bool>? filter, bool isChecked, string? note = null, (string, string)[]? processes = null, bool visibleWindowOnly = false)
    { Id = id; Group = group; Title = title; _q = q; _targets = targets; _filter = filter; _checked = isChecked; _note = note; _procs = processes ?? Array.Empty<(string, string)>(); _windowOnly = visibleWindowOnly; }

    /// Браузеры работают в фоне без окон (ускорение запуска, «продолжать работу в фоне»): блокирует только видимое окно; занятые файлы и так пропускаются.
    /// Discord/Steam/Telegram пишут в кеш активно — для них блокирует любой процесс.
    private readonly bool _windowOnly;

    public string Id { get; }
    public string Group { get; }
    public string Title { get; }
    public bool NeedsAdmin => false;

    private string? Blocker() { foreach (var (d, p) in _procs) if (_windowOnly ? _q.HasVisibleWindow(p) : _q.IsRunning(p)) return d; return null; }

    private IEnumerable<string> Safe() =>
        _targets().Where(t => SafeDelete.Norm(t.Dir).Equals(SafeDelete.Norm(t.Base), StringComparison.OrdinalIgnoreCase) || SafeDelete.IsSafeUnder(t.Base, t.Dir)).Select(t => t.Dir);

    public Task<CleanupItem> ScanAsync(CancellationToken ct) => Task.Run(() =>
    {
        long bytes = 0; int files = 0;
        foreach (var d in Safe()) { ct.ThrowIfCancellationRequested(); var m = SafeDelete.Measure(d, _filter); bytes += m.Bytes; files += m.Files; }
        var blocked = Blocker();
        return new CleanupItem(Id, Group, Title, bytes, files, false, blocked, _checked && blocked == null, _note);
    }, ct);

    public Task<CleanResult> CleanAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            if (Blocker() is { } b) return new CleanResult(0, 0, $"закрой {b}");
            var r = CleanResult.Empty;
            foreach (var d in Safe()) { ct.ThrowIfCancellationRequested(); r = r.Plus(SafeDelete.DeleteContents(d, _filter)); }
            return r;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new CleanResult(0, 0, "ошибка очистки"); }
    }, ct);
}

internal sealed class RecycleBinCategory(IRecycleBin bin) : ICleanupCategory
{
    public string Id => "RecycleBin";
    public string Group => CleanupGroups.System;
    public string Title => "Корзина";
    public bool NeedsAdmin => false;
    public Task<CleanupItem> ScanAsync(CancellationToken ct) => Task.Run(() =>
    {
        var (b, n) = bin.Query();
        return new CleanupItem(Id, Group, Title, b, (int)Math.Min(n, int.MaxValue), false, null, false);
    }, ct);
    public Task<CleanResult> CleanAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var before = bin.Query().Bytes;
            return bin.Empty() ? new CleanResult(before, 0, null) : new CleanResult(0, 0, "не удалось очистить Корзину");
        }
        catch (Exception) { return new CleanResult(0, 0, "не удалось очистить Корзину"); }
    }, ct);
}

/// Категории с правами администратора: пульт сам ничего не удаляет, просит помощника (CleanSystem с id из фиксированного набора).
/// Размер — только чтение, если получается; иначе -1.
internal sealed class AdminCategory : ICleanupCategory
{
    private readonly CleanupEnv _env; private readonly Func<string[]> _roots; private readonly Func<FileSystemInfo, bool>? _filter; private readonly string? _note;

    public AdminCategory(CleanupEnv env, string id, string title, Func<string[]> roots, Func<FileSystemInfo, bool>? filter, string? note = null)
    { _env = env; Id = id; Title = title; _roots = roots; _filter = filter; _note = note; }

    public string Id { get; }
    public string Group => CleanupGroups.System;
    public string Title { get; }
    public bool NeedsAdmin => true;

    public Task<CleanupItem> ScanAsync(CancellationToken ct) => Task.Run(() =>
    {
        long bytes = 0; int files = 0; var unknown = Id == AdminMaintenance.ComponentCleanup;
        foreach (var r in _roots())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(r)) continue;
            var m = SafeDelete.Measure(r, _filter);
            bytes += m.Bytes; files += m.Files;
            if (m.Unreadable) unknown = true;
        }
        return new CleanupItem(Id, Group, Title, unknown ? -1 : bytes, files, true, null, false,
            _note ?? (unknown ? "размер узнаю при очистке" : null));
    }, ct);

    public async Task<CleanResult> CleanAsync(CancellationToken ct)
    {
        if (_env.Admin == null) return new(0, 0, "помощник администратора не установлен");
        try
        {
            var r = await _env.Admin(Id, ct);
            if (!r.Ok) return new(0, 0, string.IsNullOrEmpty(r.Message) ? "помощник отказал" : r.Message);
            return AdminMaintenance.TryParseCleanCode(r.Code, out var freed, out var skipped) ? new(freed, skipped, null) : CleanResult.Empty;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(0, 0, "не удалось связаться с помощником"); }
    }
}

/// C:\Windows.old: только размер; удаляет её сама Windows (Параметры → Память → Временные файлы).
internal sealed class WindowsOldCategory(CleanupEnv env) : ICleanupCategory
{
    public string Id => "WindowsOld";
    public string Group => CleanupGroups.System;
    public string Title => "Прежняя установка Windows (Windows.old)";
    public bool NeedsAdmin => false;
    public const string OpenHint = "ms-settings:storagepolicies";

    public Task<CleanupItem> ScanAsync(CancellationToken ct) => Task.Run(() =>
    {
        var dir = env.SystemDrive + "\\Windows.old";
        if (!Directory.Exists(dir)) return new CleanupItem(Id, Group, Title, 0, 0, false, null, false);
        var m = SafeDelete.Measure(dir);
        return new CleanupItem(Id, Group, Title, m.Unreadable && m.Files == 0 ? -1 : m.Bytes, m.Files, false, null, false,
            "удаляет сама Windows: кнопка «Открыть очистку Windows»");
    }, ct);

    public Task<CleanResult> CleanAsync(CancellationToken ct) =>
        Task.FromResult(new CleanResult(0, 0, "Windows.old удаляет сама Windows — открой «Параметры → Память → Временные файлы»"));
}
