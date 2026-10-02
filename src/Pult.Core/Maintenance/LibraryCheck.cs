using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pult.Core.Maintenance;

public sealed record LibraryStatus(string Key, string Title, bool Installed, string? Version, bool EndOfLife, string? WingetId, string Explain);

/// Системные библиотеки, без которых не запускаются программы и игры: есть ли, не устарели ли.
public static class LibraryCheck
{
    private static readonly Regex Vc = new(
        @"Visual C\+\+\s+(2015-2022|2015-2019|2017|v14)\b.*Redistributable\s*\((x64|x86)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Desktop = new(@"Windows Desktop Runtime\s*-\s*((\d+)\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JavaName = new(@"^Java\s+\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string EolExplain = "больше не получает исправлений безопасности. Нужна только старым программам; если не знаешь — не удаляй";

    /// Итог проверки: «Обновлений нет. 2 версии больше не получают исправлений безопасности». actionable — сколько можно установить/обновить, eol — сколько версий без поддержки.
    public static string Summary(int actionable, int eol)
    {
        var head = actionable == 0 ? "Всё на месте, обновлений нет." : $"Можно установить или обновить: {actionable}.";
        if (eol == 0) return head;
        var tail = $"{eol} {RuText.Plural(eol, "версия больше не получает", "версии больше не получают", "версий больше не получают")} исправлений безопасности (нужны только старым программам; если не знаешь — не удаляй).";
        return actionable == 0 ? "Обновлений нет. " + tail : head + " " + tail;
    }

    public static string SystemDir(string sub) =>
        Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", sub);

    public static IReadOnlyList<LibraryStatus> Evaluate(
        IEnumerable<(string DisplayName, string? Version)> uninstallEntries,
        IEnumerable<string> dotnetRuntimes,
        Func<string, bool> fileExists)
    {
        var entries = uninstallEntries.Where(e => !string.IsNullOrWhiteSpace(e.DisplayName)).ToList();
        var result = new List<LibraryStatus>();

        // Visual C++ 2015-2022
        foreach (var arch in new[] { "x64", "x86" })
        {
            var hit = entries.FirstOrDefault(e => Vc.Match(e.DisplayName) is { Success: true } m && m.Groups[2].Value.Equals(arch, StringComparison.OrdinalIgnoreCase));
            bool have = hit.DisplayName != null;
            result.Add(new LibraryStatus($"vcredist-{arch}", $"Visual C++ 2015–2022 ({arch})", have, hit.Version, false,
                $"Microsoft.VCRedist.2015+.{arch}",
                have ? "нужна многим программам и играм" : "не установлена — программы и игры могут не запускаться; можно установить"));
        }

        // DirectX (старые d3dx)
        bool dx = fileExists(SystemDir(@"SysWOW64\d3dx9_43.dll")) || fileExists(SystemDir(@"System32\d3dx9_43.dll"));
        result.Add(new LibraryStatus("directx", "DirectX (библиотеки d3dx)", dx, null, false, "Microsoft.DirectX",
            dx ? "нужна старым играм" : "не найдена d3dx9_43.dll — старые игры могут не запускаться; можно установить"));

        // .NET Desktop Runtime 6 и 7 — строки только когда стоят (у них закончилась поддержка)
        var dotnetVersions = new Dictionary<int, string>();
        foreach (var line in dotnetRuntimes)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[0] == "Microsoft.WindowsDesktop.App") AddNet(dotnetVersions, p[1]);
        }
        foreach (var e in entries)
            if (Desktop.Match(e.DisplayName) is { Success: true } m && int.TryParse(m.Groups[2].Value, out var major))
                AddNet(dotnetVersions, m.Groups[1].Value);   // версию берём из имени: DisplayVersion у MSI-рантаймов нечитаемый (48.x, 56.x)
        foreach (var major in new[] { 6, 7 })
            if (dotnetVersions.TryGetValue(major, out var v))
                result.Add(new LibraryStatus($"dotnet-{major}", $".NET Desktop Runtime {major}", true, string.IsNullOrEmpty(v) ? null : v, true,
                    $"Microsoft.DotNet.DesktopRuntime.{major}", EolExplain));

        // Только показ версии
        var gi = entries.FirstOrDefault(e => e.DisplayName.Contains("GameInput", StringComparison.OrdinalIgnoreCase));
        if (gi.DisplayName != null)
            result.Add(new LibraryStatus("gameinput", "Microsoft GameInput", true, gi.Version, false, "Microsoft.GameInput", "ввод для игр; обновляется вместе со списком программ"));
        var java = entries.FirstOrDefault(e => JavaName.IsMatch(e.DisplayName) && !e.DisplayName.Contains("Auto Updater", StringComparison.OrdinalIgnoreCase));
        if (java.DisplayName != null)
            result.Add(new LibraryStatus("java", java.DisplayName, true, java.Version, false, null, "нужна некоторым играм и программам (например, Minecraft)"));

        return result;
    }

    private static void AddNet(Dictionary<int, string> d, string version)
    {
        var dot = version.IndexOf('.');
        if (dot <= 0 || !int.TryParse(version[..dot], out var major)) return;
        if (!d.TryGetValue(major, out var have) || Version.TryParse(version, out var nv) && Version.TryParse(have, out var ov) && nv > ov) d[major] = version;
    }

    /// Собирает данные этого ПК и вызывает Evaluate. Любые сбои чтения — пустые данные, не исключение.
    public static IReadOnlyList<LibraryStatus> ForThisPc()
    {
        var entries = new List<(string, string?)>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var un = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un == null) continue;
                foreach (var name in un.GetSubKeyNames())
                {
                    try
                    {
                        using var k = un.OpenSubKey(name);
                        if (k?.GetValue("DisplayName") is string dn && dn.Length > 0)
                            entries.Add((dn, k.GetValue("DisplayVersion") as string));
                    }
                    catch { }
                }
            }
            catch { }
        }
        return Evaluate(entries, DotnetRuntimes(), File.Exists);
    }

    private static IReadOnlyList<string> DotnetRuntimes()
    {
        try
        {
            var exe = new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") }
                .Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, @"dotnet\dotnet.exe")).FirstOrDefault(File.Exists);
            if (exe == null) return Array.Empty<string>();
            var psi = new ProcessStartInfo(exe, "--list-runtimes") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return Array.Empty<string>();
            var read = p.StandardOutput.ReadToEndAsync();                      // сначала ждём с таймаутом: зависший dotnet.exe не должен вешать страницу
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return Array.Empty<string>(); }
            var text = read.Result;
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
