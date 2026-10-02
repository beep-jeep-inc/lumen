using System.Text;
using Pult.Core.Admin;

namespace Pult.Admin;

public interface IScriptRunner { ProcessOutcome Run(string script, TimeSpan timeout); }

/// Фиксированные скрипты помощника запускаются встроенным Windows PowerShell 5.1 (System32, полный путь).
/// Почему не COM напрямую из PultAdmin: NativeAOT не умеет dynamic/IDispatch, а ручные [ComImport]-интерфейсы WUA
/// (IUpdate — десятки методов по порядку vtable) нельзя проверить без установки драйверов; скрипты обращаются к COM по именам.
/// Защита от подмены: -NoProfile; окружение — только машинное (CleanEnvironment.ForSystem); PSModulePath — только папка модулей
/// самого PowerShell (иначе PowerShell добавил бы пользовательскую Documents\WindowsPowerShell\Modules, и программа пользователя
/// подложила бы модуль, который автозагрузится в процессе с правами администратора); скрипт — константа помощника, кодируется
/// в -EncodedCommand; единственные подставляемые значения (id обновлений) перед подстановкой проверяются регулярным выражением.
public sealed class PowerShellHost : IScriptRunner
{
    public static string ExePath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static Dictionary<string, string> Env()
    {
        var env = CleanEnvironment.ForSystem();
        env["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        return env;
    }

    public static string Arguments(string script)
    {
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (b64.Length > 30000) throw new InvalidOperationException("скрипт слишком длинный");
        return "-NoProfile -NonInteractive -NoLogo -OutputFormat Text -EncodedCommand " + b64;
    }

    public ProcessOutcome Run(string script, TimeSpan timeout) => SystemProcess.Run(ExePath, Arguments(script), timeout, Env());
}

/// Тексты скриптов и разбор их ответов. Ответы — только ASCII-токены (кодировка вывода консоли не важна), русские тексты — в C#.
public static class MaintenanceScripts
{
    public const string SearchQuery = "IsInstalled=0 and Type='Driver'";

    // COM-объекты — по фиксированным CLSID (не по ProgID: ProgID ищется в HKCU\Software\Classes, которую пишет обычный пользователь).
    public const string SessionClsid = "4CB43D7F-7EEE-4906-8698-60DA1C38F2FE", UpdateCollClsid = "13639463-00DB-4646-803D-528026140D88";

    /// Первые строки каждого скрипта: модули — только системные (путь PowerShell из реестра HKCU\Environment/окружения не действует),
    /// автозагрузка выключена, нужные системные модули подключаются по полному пути под $PSHOME.
    public static string Header(params string[] modules) =>
        "$env:PSModulePath = \"$PSHOME\\Modules\"\n$PSModuleAutoLoadingPreference = 'None'\n"
        + string.Concat(modules.Select(m => $"Import-Module \"$PSHOME\\Modules\\{m}\\{m}.psd1\"\n"));

    private static readonly string[] SearchModules = { "Microsoft.PowerShell.Utility" };
    private static readonly string[] RestoreModules = { "Microsoft.PowerShell.Utility", "Microsoft.PowerShell.Management", "CimCmdlets" };

    public static readonly string SearchDrivers = Header(SearchModules) + """
        $ErrorActionPreference = 'Stop'
        try {
          $s = [Activator]::CreateInstance([type]::GetTypeFromCLSID([guid]'4CB43D7F-7EEE-4906-8698-60DA1C38F2FE'))
          $r = $s.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver'")
          foreach ($u in $r.Updates) {
            $t = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$u.Title))
            $c = ''; try { $c = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$u.DriverClass)) } catch { }
            $h = ''; try { $h = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$u.DriverHardwareID)) } catch { }
            $b = ''; try { $b = [string][int]$u.InstallationBehavior.RebootBehavior } catch { }
            Write-Output ('U|' + [string]$u.Identity.UpdateID + '|' + [int]$u.Identity.RevisionNumber + '|' + $t + '|' + $c + '|' + $h + '|' + $b)
          }
          Write-Output 'END'
        } catch { Write-Output ('ERR|' + $_.Exception.HResult) }
        """;

    /// ids — уже проверенные (AdminMaintenance.IsValidUpdateId) id вида «guid» или «guid_rev»; повторная проверка здесь — защита от ошибки вызывающего.
    public static string InstallDrivers(IReadOnlyList<string> ids)
    {
        foreach (var id in ids) if (!AdminMaintenance.IsValidUpdateId(id)) throw new ArgumentException("недопустимый id обновления");
        var want = string.Join(",", ids.Select(i => "'" + i.ToLowerInvariant() + "'"));
        return Header(SearchModules) + $$"""
            $ErrorActionPreference = 'Stop'
            $want = @({{want}})
            try {
              $s = [Activator]::CreateInstance([type]::GetTypeFromCLSID([guid]'4CB43D7F-7EEE-4906-8698-60DA1C38F2FE'))
              $r = $s.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver'")
              $coll = [Activator]::CreateInstance([type]::GetTypeFromCLSID([guid]'13639463-00DB-4646-803D-528026140D88'))
              $skipped = 0
              foreach ($u in $r.Updates) {
                $rev = [int]$u.Identity.RevisionNumber
                $full = ([string]$u.Identity.UpdateID).ToLowerInvariant()
                if ($rev -gt 0) { $full = $full + '_' + $rev }
                if ($want -contains $full) {
                  # CanRequestUserInput не проверяем: у драйверов из Центра обновления он почти всегда True (живая проверка 30.09), а ставятся они без вопросов;
                  # от зависания — AllowSourcePrompts=false и таймаут установки
                  if (-not $u.EulaAccepted) { $skipped++ } else { [void]$coll.Add($u) }
                }
              }
              if ($coll.Count -eq 0) { Write-Output ('R|0|0|' + $skipped + '|0'); return }
              $d = $s.CreateUpdateDownloader()
              $d.Updates = $coll
              [void]$d.Download()
              $ready = [Activator]::CreateInstance([type]::GetTypeFromCLSID([guid]'13639463-00DB-4646-803D-528026140D88'))
              $failed = 0
              foreach ($u in $coll) { if ($u.IsDownloaded) { [void]$ready.Add($u) } else { $failed++ } }
              if ($ready.Count -eq 0) { Write-Output ('R|0|' + $failed + '|' + $skipped + '|0'); return }
              $i = $s.CreateUpdateInstaller()
              $i.Updates = $ready
              $i.AllowSourcePrompts = $false
              $res = $i.Install()
              $ok = 0
              $rb = @()
              for ($k = 0; $k -lt $ready.Count; $k++) {
                $ur = $res.GetUpdateResult($k)
                if ($ur.ResultCode -eq 2) { $ok++ } else { $failed++ }
                if ($ur.RebootRequired) { try { $rb += [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$ready.Item($k).Title)) } catch { } }
              }
              Write-Output ('R|' + $ok + '|' + $failed + '|' + $skipped + '|' + [int][bool]$res.RebootRequired + '|' + ($rb -join ','))
            } catch { Write-Output ('ERR|' + $_.Exception.HResult) }
            """;
    }

    /// Точка восстановления: WMI root\default:SystemRestore.CreateRestorePoint(описание, 12 = MODIFY_SETTINGS, 100 = BEGIN_SYSTEM_CHANGE).
    /// Свежая (по SystemRestorePointCreationFrequency, по умолчанию 1440 мин) уже есть — Windows тихо ничего не создаст, поэтому проверяем заранее.
    public static readonly string RestorePoint = Header(RestoreModules) + """
        $ErrorActionPreference = 'Stop'
        try {
          $freq = 1440
          $p = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -ErrorAction SilentlyContinue
          if ($p -and $p.SystemRestorePointCreationFrequency -is [int]) { $freq = $p.SystemRestorePointCreationFrequency }
          $last = $null
          foreach ($pt in @(Get-CimInstance -Namespace 'root/default' -ClassName SystemRestore)) {
            $t = [Management.ManagementDateTimeConverter]::ToDateTime([string]$pt.CreationTime)   # WMI-время строкой DMTF; [datetime] её не разбирает
            if ($last -eq $null -or $t -gt $last) { $last = $t }
          }
          if ($freq -gt 0 -and $last -ne $null -and ((Get-Date) - $last).TotalMinutes -lt $freq) { Write-Output 'RECENT'; return }
          $o = Invoke-CimMethod -Namespace 'root/default' -ClassName SystemRestore -MethodName CreateRestorePoint -Arguments @{ Description = 'Lumen: перед драйверами'; RestorePointType = [uint32]12; EventType = [uint32]100 }
          $rc = [int]$o.ReturnValue
          if ($rc -eq 1058) { Write-Output 'DISABLED' } elseif ($rc -ne 0) { Write-Output ('FAIL|' + $rc) } else { Write-Output 'CREATED' }
        } catch { Write-Output ('FAIL|' + $_.Exception.HResult) }
        """;

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string? B64(string s)
    {
        try { var t = Encoding.UTF8.GetString(Convert.FromBase64String(s)); return string.IsNullOrWhiteSpace(t) ? null : t; } catch (FormatException) { return null; }
    }

    /// Список найденных драйверов. null в Error, если скрипт дошёл до END.
    public static (IReadOnlyList<DriverUpdateRow> Rows, string? Error) ParseSearch(string output)
    {
        var rows = new List<DriverUpdateRow>(); var ended = false; string? err = null;
        foreach (var l in Lines(output))
        {
            if (l == "END") ended = true;
            else if (l.StartsWith("ERR|", StringComparison.Ordinal)) err = "ошибка Центра обновления " + HResult(l[4..]);
            else if (l.StartsWith("U|", StringComparison.Ordinal))
            {
                var f = l.Split('|');
                if ((f.Length != 4 && f.Length != 6 && f.Length != 7) || !Guid.TryParse(f[1], out var g) || !int.TryParse(f[2], System.Globalization.NumberStyles.None, null, out var rev)) continue;
                var title = "";
                try { title = Encoding.UTF8.GetString(Convert.FromBase64String(f[3])); } catch (FormatException) { }
                string? cls = f.Length >= 6 ? B64(f[4]) : null, hw = f.Length >= 6 ? B64(f[5]) : null;
                int? rb = f.Length == 7 && int.TryParse(f[6], System.Globalization.NumberStyles.None, null, out var rbv) && rbv <= 2 ? rbv : null;
                var id = g.ToString("D") + (rev > 0 ? "_" + rev : "");
                if (AdminMaintenance.IsValidUpdateId(id)) rows.Add(new DriverUpdateRow(id, title, cls, hw, rb));
            }
        }
        return (rows, err ?? (ended ? null : "Центр обновления не вернул список"));
    }

    public static DriverInstallReport ParseInstall(string output)
    {
        foreach (var l in Lines(output).Reverse())
        {
            if (l.StartsWith("ERR|", StringComparison.Ordinal)) return new(0, 0, 0, false, "ошибка Центра обновления " + HResult(l[4..]));
            var f = l.Split('|');
            if ((f.Length == 5 || f.Length == 6) && f[0] == "R" && int.TryParse(f[1], out var ok) && int.TryParse(f[2], out var bad) && int.TryParse(f[3], out var skip))
            {
                List<string>? titles = null;
                if (f.Length == 6)
                {
                    titles = new();
                    foreach (var part in f[5].Split(',', StringSplitOptions.RemoveEmptyEntries)) if (B64(part) is { } t) titles.Add(t);
                }
                return new(ok, bad, skip, f[4] == "1", null, titles);
            }
        }
        return new(0, 0, 0, false, "установка не вернула результат");
    }

    public static RestoreOutcome ParseRestore(string output, out string detail)
    {
        detail = "";
        foreach (var l in Lines(output).Reverse())
        {
            switch (l)
            {
                case "CREATED": return RestoreOutcome.Created;
                case "RECENT": return RestoreOutcome.TooRecent;
                case "DISABLED": return RestoreOutcome.Disabled;
            }
            if (l.StartsWith("FAIL|", StringComparison.Ordinal)) { detail = l[5..]; return RestoreOutcome.Failed; }
        }
        detail = "нет ответа";
        return RestoreOutcome.Failed;
    }

    private static string HResult(string s) => int.TryParse(s, out var v) ? "0x" + v.ToString("X8") : "";
}
