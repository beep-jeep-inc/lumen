using System.Text;
using Pult.Core.Admin;

namespace Pult.Admin;

/// Один запрос: прочитать %LOCALAPPDATA%\Pult\admin.req (только читать — в профиле пользователя помощник ничего
/// не пишет и не удаляет), проверить аргументы по фиксированным правилам (AdminPolicy), выполнить, ответ — в C:\ProgramData\Pult\res\&lt;nonce&gt;.json.
public sealed class RequestProcessor(string requestPath, string responseDir, Func<string, string?>? outputDirProblem = null, IMaintenanceExecutor? maintenance = null,
                                     Pult.Core.Startup.IStartupAccess? startup = null, IWingetExecutor? winget = null,
                                     Func<string, ConfirmAnswer>? startupConfirm = null)
{
    public const long MaxRequestBytes = 16 * 1024;
    public static readonly TimeSpan ResponseLifetime = TimeSpan.FromMinutes(10);

    public AdminResponse? ProcessOnce()
    {
        if (outputDirProblem?.Invoke(responseDir) is { } bad)
            throw new InvalidOperationException("папка ответов не под защитой: " + bad);
        DeleteOldResponses();
        var bytes = SafeFile.ReadRegular(requestPath, MaxRequestBytes);
        if (bytes == null) return null;
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); } catch (DecoderFallbackException) { return null; }
        var req = AdminRequest.Parse(text.TrimStart('﻿'));                           // nonce — только 32 hex, иначе null
        if (req == null) return null;

        var d = AdminPolicy.Decide(req);
        AdminResponse res;
        if (!d.Allowed) res = new(req.Nonce, false, d.Reason, d.Code);
        else
        {
            try
            {
                MaintenanceResult? m = null;
                switch (req.Action)
                {
                    // Политика уже проверила аргументы; исполнитель получает только проверенные id из фиксированных наборов.
                    case AdminAction.DriverInstall:
                        AdminMaintenance.TryParseDriverIds(req.Arg, out var ids, out var skipRestore, out _);
                        m = (maintenance ?? throw new InvalidOperationException("обслуживание недоступно")).InstallDrivers(ids, skipRestore); break;
                    case AdminAction.RestorePoint:
                        m = (maintenance ?? throw new InvalidOperationException("обслуживание недоступно")).CreateRestorePoint(); break;
                    case AdminAction.CleanSystem:
                        m = (maintenance ?? throw new InvalidOperationException("обслуживание недоступно")).CleanSystem(req.Arg); break;
                    case AdminAction.RepairSystem:
                        m = (maintenance ?? throw new InvalidOperationException("обслуживание недоступно")).RepairSystem(); break;
                    // winget с правами администратора — в контексте этого процесса (пользователь, повышенно), не через SYSTEM-исполнителя.
                    // Окно подтверждения с Id пакета показывает сам исполнитель (UserWinget) до запуска.
                    case AdminAction.WingetUpgrade:
                        Pult.Core.Maintenance.WingetArg.TryParse(req.Arg, out var wMode, out var wId, out _);
                        m = (winget ?? throw new InvalidOperationException("winget недоступен")).Run(wMode, wId); break;
                    // Короткое действие: пишет только флаг StartupApproved для СУЩЕСТВУЮЩЕЙ машинной записи (проверяет сам), но только после «Да»
                    // в окне помощника; текст окна строится из прочитанной записи, не из запроса. Нет окна (startupConfirm == null) — отказ.
                    case AdminAction.StartupToggle:
                        Pult.Core.Startup.StartupArg.TryParseForHelper(req.Arg, out var src, out var on, out var entry, out _);
                        if (startupConfirm == null) { m = new(false, "подтверждение недоступно — автозапуск не изменён", Pult.Core.Startup.StartupToggler.RefusedCode); break; }
                        var t = Pult.Core.Startup.StartupToggler.Toggle(startup ?? Pult.Core.Startup.RegistryStartupAccess.ForThisPc(), src, entry, on, DateTime.UtcNow, Pult.Core.Startup.StartupSources.HelperSources,
                            raw => startupConfirm(Pult.Core.Startup.StartupArg.ConfirmText(src, on, raw)));
                        m = new(t.Ok, t.Message, t.Code); break;
                    // Только чтение: аргументов нет (политика отказала бы), окна подтверждения нет — ничего не меняется. Читает SYSTEM-исполнитель (через задание), не этот процесс. Ответ — проверенные числа.
                    case AdminAction.DiskDetails:
                        m = (maintenance ?? throw new InvalidOperationException("чтение дисков недоступно")).ReadDiskDetails(); break;
                }
                res = m == null ? new(req.Nonce, true, "ok") : new(req.Nonce, m.Ok, m.Message, m.Code);
            }
            catch (Exception e) { res = new(req.Nonce, false, "ошибка: " + e.Message); }
        }
        if (outputDirProblem?.Invoke(responseDir) is { } badNow)                       // проверка прямо перед записью ответа
            throw new InvalidOperationException("папка ответов не под защитой, ответ не записан: " + badNow);
        var path = Path.Combine(responseDir, req.Nonce + ".json");
        File.WriteAllText(path + ".tmp", res.ToJson(), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
        return res;
    }

    private void DeleteOldResponses()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(responseDir))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > ResponseLifetime)
                    try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
