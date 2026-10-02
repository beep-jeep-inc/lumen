using System.Management;
using System.Text.RegularExpressions;

namespace Pult.Core.Maintenance;

public sealed record DriverUpdate(string UpdateId, string Title, string? DriverModel, string? DriverVersion, DateTime? DriverDate, string? CurrentVersion, string? DriverClass = null, string? HardwareId = null, int? RebootBehavior = null)
{
    /// Нужна ли перезагрузка по данным WUA (InstallationBehavior.RebootBehavior: 0 — никогда, 1 — всегда, 2 — возможно).
    /// Неизвестно (null) считаем «возможно»: «перезагрузка не понадобится» обещаем, только если Windows сама сказала «никогда».
    public bool MayNeedReboot => RebootBehavior != 0;

    /// Обновление прошивки (BIOS/UEFI): рискованнее обычного драйвера, точка восстановления его не откатит.
    public bool IsFirmware => string.Equals(DriverClass, "Firmware", StringComparison.OrdinalIgnoreCase)
        || (HardwareId?.StartsWith("uefi\\", StringComparison.OrdinalIgnoreCase) ?? false);
}

public interface IWindowsUpdateSearch
{
    Task<IReadOnlyList<DriverUpdate>> SearchDriversAsync(CancellationToken ct);
}

/// Поиск драйверов в Центре обновления Windows (COM Microsoft.Update.Session). Только поиск — установку делает помощник.
/// Любая ошибка (нет службы, нет сети, таймаут) — пустой список и текст в LastError.
public sealed class WuaSearch : IWindowsUpdateSearch
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static readonly Regex VersionInTitle = new(@"(\d+(?:\.\d+){2,})\)?\s*$", RegexOptions.Compiled);

    /// Версия из конца заголовка: «Intel System Driver Update (2.3.20306.4)» → 2.3.20306.4.
    public static string? VersionFromTitle(string title) => VersionInTitle.Match(title) is { Success: true } m ? m.Groups[1].Value : null;

    public string? LastError { get; private set; }

    private sealed class ErrorBox { public volatile string? Text; }

    /// Понятный текст вместо сырого сообщения COM: нет сети / служба обновления остановлена.
    public static string FriendlyError(Exception ex)
    {
        switch ((uint)ex.HResult)
        {
            case 0x80072EE7: case 0x80072EFD: case 0x8024402C: case 0x8024401C:
                return "нет связи с Центром обновления (проверь интернет)";
            case 0x8024001E:
                return "служба обновления остановлена";
            default:
                return ex.Message;
        }
    }

    public Task<IReadOnlyList<DriverUpdate>> SearchDriversAsync(CancellationToken ct)
    {
        LastError = null;
        if (ct.IsCancellationRequested) { LastError = "Поиск отменён."; return Task.FromResult<IReadOnlyList<DriverUpdate>>(Array.Empty<DriverUpdate>()); }
        var tcs = new TaskCompletionSource<IReadOnlyList<DriverUpdate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var err = new ErrorBox();                                              // ошибка этого поиска: брошенный по таймауту поток не затирает LastError следующего
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(SearchCore()); }
            catch (Exception ex) { err.Text = FriendlyError(ex); tcs.TrySetResult(Array.Empty<DriverUpdate>()); }
        }) { IsBackground = true, Name = "WUA driver search" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return Wait(tcs.Task, err, ct);
    }

    private async Task<IReadOnlyList<DriverUpdate>> Wait(Task<IReadOnlyList<DriverUpdate>> work, ErrorBox err, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await work.WaitAsync(timeout.Token).WaitAsync(ct).ConfigureAwait(false);
            LastError = err.Text;
            return work.Result;
        }
        catch (OperationCanceledException)
        {
            // Поток-поиск остаётся фоновым и сам завершится; результат выбрасываем.
            LastError = ct.IsCancellationRequested ? "Поиск отменён." : "Центр обновления не ответил за 3 минуты.";
            return Array.Empty<DriverUpdate>();
        }
    }

    private static IReadOnlyList<DriverUpdate> SearchCore()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Агент обновления Windows недоступен.");
        dynamic session = Activator.CreateInstance(type)!;
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic found = searcher.Search("IsInstalled=0 and Type='Driver'");
        var list = new List<DriverUpdate>();
        foreach (dynamic u in found.Updates)
        {
            try
            {
                string title = (string)u.Title;
                string id = (string)u.Identity.UpdateID;
                int rev = (int)u.Identity.RevisionNumber;
                string? model = SafeStr(() => (string)u.DriverModel);
                DateTime? date = null;
                try { date = (DateTime)u.DriverVerDate; } catch { }
                var ver = VersionFromTitle(title);
                string? cls = SafeStr(() => (string)u.DriverClass);
                string? hw = SafeStr(() => (string)u.DriverHardwareID);
                int? rb = null;
                try { var v = (int)u.InstallationBehavior.RebootBehavior; if (v is >= 0 and <= 2) rb = v; } catch { }
                list.Add(new DriverUpdate(rev > 0 ? $"{id}_{rev}" : id, title, model, ver, date, null, cls, hw, rb));
            }
            catch { }
        }
        return list;
    }

    private static string? SafeStr(Func<string> f) { try { var s = f(); return string.IsNullOrWhiteSpace(s) ? null : s; } catch { return null; } }
}

public sealed record GpuDriver(string Name, string Version, DateTime? Date, bool Old, string VendorAppHint, string VendorUrl);

/// Драйверы видеокарт: возраст и куда идти за свежим (только официальные приложения производителей).
public static class Drivers
{
    public static readonly TimeSpan OldAfter = TimeSpan.FromDays(365);

    /// «A, B, C и ещё N»: не больше max названий, каждое укорочено.
    public static string TitleList(IReadOnlyList<string> titles, int max = 3, int width = 45)
    {
        string Short(string t) { t = t.Replace('\r', ' ').Replace('\n', ' ').Trim(); return t.Length > width ? t[..width].TrimEnd() + "…" : t; }
        var s = string.Join(", ", titles.Take(max).Select(Short));
        return titles.Count > max ? $"{s} и ещё {titles.Count - max}" : s;
    }

    public static GpuDriver MakeGpu(string name, string version, DateTime? date, DateTime now)
    {
        bool old = date.HasValue && now - date.Value > OldAfter;
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce"))
            return new GpuDriver(name, version, date, old, "NVIDIA App", "https://www.nvidia.com/ru-ru/software/nvidia-app/");
        if (n.Contains("intel"))
            return new GpuDriver(name, version, date, old, "Intel Driver & Support Assistant", "https://www.intel.com/content/www/us/en/support/detect.html");
        if (n.Contains("amd") || n.Contains("radeon"))
            return new GpuDriver(name, version, date, old, "AMD Adrenalin", "https://www.amd.com/ru/support/download/drivers.html");
        return new GpuDriver(name, version, date, old, "Центр обновления Windows", "ms-settings:windowsupdate");
    }

    private static bool IsVirtual(string name) =>
        name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<GpuDriver> Gpus()
    {
        var list = new List<GpuDriver>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var name = o["Name"] as string;
                    if (string.IsNullOrWhiteSpace(name) || IsVirtual(name)) continue;
                    DateTime? date = null;
                    try { if (o["DriverDate"] is string d && d.Length > 0) date = ManagementDateTimeConverter.ToDateTime(d); } catch { }
                    list.Add(MakeGpu(name, o["DriverVersion"] as string ?? "", date, DateTime.Now));
                }
            }
        }
        catch { }
        return list;
    }
}
