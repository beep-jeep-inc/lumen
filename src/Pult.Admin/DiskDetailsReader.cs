using Pult.Core.Admin;
using Pult.Core.Maintenance;

namespace Pult.Admin;

/// ТОЛЬКО ЧТЕНИЕ. Фиксированный скрипт (константа, ни одного значения из запроса) спрашивает у Windows счётчики надёжности дисков
/// (MSFT_StorageReliabilityCounter) и печатает числа. Ответ пульту — не сырой вывод PowerShell, а заново собранные и проверенные строки
/// (DiskDetailsProtocol.Parse → Format): ни пути, ни имена, ни тексты ошибок наружу не идут.
public static class DiskDetailsScript
{
    /// Модули — только системные (как у остальных скриптов помощника); команды — только Get-CimInstance по фиксированному классу.
    public static readonly string Text = MaintenanceScripts.Header("Microsoft.PowerShell.Utility", "CimCmdlets") + """
        $ErrorActionPreference = 'Stop'
        try {
          # Прямой перебор MSFT_StorageReliabilityCounter Windows отдаёт пустым — счётчики есть только через связь с диском.
          foreach ($d in @(Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName MSFT_PhysicalDisk)) {
           foreach ($c in @(Get-CimAssociatedInstance -InputObject $d -ResultClassName MSFT_StorageReliabilityCounter -ErrorAction SilentlyContinue)) {
            try {
              $f = @()
              foreach ($v in @($c.DeviceId, $c.Wear, $c.Temperature, $c.ReadErrorsUncorrected, $c.WriteErrorsUncorrected, $c.PowerOnHours)) {
                if ($null -eq $v) { $f += '-' } else { $f += [string][int64]$v }
              }
              Write-Output ('D|' + ($f -join '|'))
            } catch { }
           }
          }
          Write-Output 'END'
        } catch { Write-Output ('ERR|' + $_.Exception.HResult) }
        """;
}

public static class DiskDetailsRun
{
    public static MaintenanceResult Run(IScriptRunner runner)
    {
        var o = runner.Run(DiskDetailsScript.Text, AdminMaintenance.DiskDetailsProcessTimeout);
        if (o.TimedOut) return new(false, "Windows не ответила про диски вовремя — попробуй ещё раз позже.");
        var counters = DiskDetailsProtocol.Parse(o.Output);
        if (counters.Count == 0) return new(false, "Windows не отдала подробные данные о дисках (износ, температура).");
        return new(true, DiskDetailsProtocol.Format(counters), DiskDetailsProtocol.Code);
    }
}
