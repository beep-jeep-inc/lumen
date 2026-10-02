using System.Diagnostics;
using System.Text;

namespace Pult.Core.Maintenance;

public sealed record WingetRun(int ExitCode, string Output, bool TimedOut, bool Cancelled, string? StartError = null, bool StillRunning = false)
{
    public bool Ok => StartError == null && !TimedOut && !Cancelled && ExitCode == 0;
}

/// Запуск winget и разбор его вывода. Ничего не ставит сам: команды собирает Winget.UpgradeCommand / InstallCommand.
public static class WingetRunner
{
    public const int NoApplicableUpdate = unchecked((int)0x8A15002B);

    /// Вывод winget с индикатором прогресса: «\r» перезаписывает строку, крутилка «- \ | /» и полоски «█▒» — не таблица.
    public static string CleanOutput(string raw)
    {
        var sb = new StringBuilder();
        foreach (var chunk in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = chunk.Contains('\r') ? chunk[(chunk.LastIndexOf('\r') + 1)..] : chunk;
            var t = line.Trim();
            if (t.Length == 0) { sb.AppendLine(); continue; }
            if (t.Length <= 2 && t.All(c => c is '-' or '\\' or '|' or '/')) continue;
            if (t.Contains('█') || t.Contains('▒')) continue;
            sb.AppendLine(line.TrimEnd());
        }
        return sb.ToString();
    }

    public static string Hex(int code) => "0x" + ((uint)code).ToString("X8");

    /// Человеческое описание неудачи: код winget и последняя осмысленная строка вывода.
    public static string DescribeFailure(WingetRun r)
    {
        if (r.StartError != null) return r.StartError;
        if (r.Cancelled) return "отменено";
        if (r.TimedOut) return r.StillRunning ? "установщик ещё работает — проверь позже" : "не уложилось во время";
        if (r.ExitCode == NoApplicableUpdate) return "обновление уже не требуется (код " + Hex(r.ExitCode) + ")";
        var last = r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 3 && !l.StartsWith("Found", StringComparison.OrdinalIgnoreCase));
        if (last != null && last.Length > 110) last = last[..110] + "…";
        return $"код winget {Hex(r.ExitCode)}" + (last != null ? " — " + last : "");
    }

    public static Task<WingetRun> ListUpgradesAsync(string wingetPath, CancellationToken ct, Version? wingetVersion = null) =>
        RunAsync(wingetPath, Winget.ListUpgradesArgs(wingetVersion), TimeSpan.FromMinutes(3), ct);

    public static async Task<WingetRun> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, bool killOnTimeout = true)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            p = Process.Start(psi);
            if (p == null) return new(-1, "", false, false, "не удалось запустить winget");
            var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                var byTimeout = !ct.IsCancellationRequested;
                if (byTimeout && !killOnTimeout)                                  // установку/обновление посреди работы не убиваем: сломается программа
                    return new(-1, "", true, false, StillRunning: true);
                try { p.Kill(true); } catch (Exception) { }
                return new(-1, CleanOutput(SafeResult(so)), byTimeout, ct.IsCancellationRequested);
            }
            var text = CleanOutput(await so + "\n" + await se);
            return new(p.ExitCode, text, false, false);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return new(-1, "", false, false, "не удалось запустить winget: " + e.Message); }
        finally { p?.Dispose(); }
    }

    private static string SafeResult(Task<string> t) { try { return t.IsCompletedSuccessfully ? t.Result : ""; } catch { return ""; } }
}

/// Запущена ли программа из списка обновлений: имена процессов угадываем по ИД и названию winget (точного соответствия нет).
public static class RunningApps
{
    public static IReadOnlyList<string> Candidates(WingetRow r)
    {
        var set = new List<string>();
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (s.Length < 3 || set.Contains(s, StringComparer.OrdinalIgnoreCase)) return;
            set.Add(s);
        }
        var parts = r.Id.Split('.');
        Add(parts[^1]);
        if (parts.Length > 1) Add(parts[0] + parts[^1]);
        var name = r.Name; var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];
        Add(name.Replace(" ", ""));
        Add(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        return set;
    }

    public static bool IsRunning(WingetRow r, Monitoring.IProcessQuery q) => Candidates(r).Any(q.IsRunning);
}
