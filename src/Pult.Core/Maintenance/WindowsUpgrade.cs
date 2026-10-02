using Microsoft.Win32;

namespace Pult.Core.Maintenance;

/// Доступное большое обновление Windows (feature update, категория «Upgrades»). Пульт его не ставит — только сообщает.
public sealed record WindowsUpgrade(string Title);

/// Текущая версия Windows из реестра: «25H2 (сборка 26200.9457)».
public static class WindowsVersion
{
    public static string? Format(string? displayVersion, string? build, int? ubr)
    {
        var v = string.IsNullOrWhiteSpace(displayVersion) ? null : displayVersion.Trim();
        var b = string.IsNullOrWhiteSpace(build) ? null : build.Trim();
        var bt = b == null ? null : ubr is { } u ? $"{b}.{u}" : b;
        if (v != null && bt != null) return $"{v} (сборка {bt})";
        if (v != null) return v;
        return bt != null ? $"сборка {bt}" : null;
    }

    public static string? Current()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k == null) return null;
            int? ubr = k.GetValue("UBR") is int i ? i : null;
            return Format(k.GetValue("DisplayVersion") as string, k.GetValue("CurrentBuild") as string, ubr);
        }
        catch { return null; }
    }
}

/// Поиск большого обновления Windows через COM Microsoft.Update.Session (как WuaSearch: свой STA-поток, таймаут, ошибка → пусто + LastError).
public sealed class WuaUpgradeSearch
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public string? LastError { get; private set; }

    private sealed class ErrorBox { public volatile string? Text; }

    /// Оставляет только обновления из категории «Upgrades» (без учёта регистра).
    public static IReadOnlyList<WindowsUpgrade> FilterUpgrades(IEnumerable<(string Title, IReadOnlyList<string> Categories)> updates) =>
        updates.Where(u => u.Categories != null && u.Categories.Any(c => string.Equals(c?.Trim(), "Upgrades", StringComparison.OrdinalIgnoreCase)))
               .Select(u => new WindowsUpgrade(u.Title)).ToList();

    public Task<IReadOnlyList<WindowsUpgrade>> SearchAsync(CancellationToken ct)
    {
        LastError = null;
        if (ct.IsCancellationRequested) { LastError = "Поиск отменён."; return Task.FromResult<IReadOnlyList<WindowsUpgrade>>(Array.Empty<WindowsUpgrade>()); }
        var tcs = new TaskCompletionSource<IReadOnlyList<WindowsUpgrade>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var err = new ErrorBox();
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(SearchCore()); }
            catch (Exception ex) { err.Text = WuaSearch.FriendlyError(ex); tcs.TrySetResult(Array.Empty<WindowsUpgrade>()); }
        }) { IsBackground = true, Name = "WUA upgrade search" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return Wait(tcs.Task, err, ct);
    }

    private async Task<IReadOnlyList<WindowsUpgrade>> Wait(Task<IReadOnlyList<WindowsUpgrade>> work, ErrorBox err, CancellationToken ct)
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
            LastError = ct.IsCancellationRequested ? "Поиск отменён." : "Центр обновления не ответил за 3 минуты.";
            return Array.Empty<WindowsUpgrade>();
        }
    }

    private static IReadOnlyList<WindowsUpgrade> SearchCore()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Агент обновления Windows недоступен.");
        dynamic session = Activator.CreateInstance(type)!;
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic found = searcher.Search("IsInstalled=0 and DeploymentAction='OptionalInstallation'");
        var list = new List<(string, IReadOnlyList<string>)>();
        foreach (dynamic u in found.Updates)
        {
            try
            {
                string title = (string)u.Title;
                var cats = new List<string>();
                foreach (dynamic c in u.Categories) { try { cats.Add((string)c.Name); } catch { } }
                list.Add((title, cats));
            }
            catch { }
        }
        return FilterUpgrades(list);
    }
}
