using System.IO;
using System.Reflection;
using System.Security.Principal;
using Pult.App.Views;
using Pult.Core.Admin;

namespace Pult.App.Services;

/// Всё, что просит окно «Да» у помощника администратора. Методы вызываются с потока окна: вопросы показываются здесь,
/// а долгое ожидание помощника (установка, до минуты) уходит в фон, чтобы окна не «зависали».
public static class AdminSetup
{
    private const long MinAotSize = 1024 * 1024;           // PultAdmin.exe меньше 1 МБ — не NativeAOT (обычная оболочка .NET без защиты)
    private static bool _updateDeclined;

    /// SHA256 помощника, вшитый в Pult.exe при сборке (publish.ps1). Пусто — обычная сборка разработчика: ставить и обновлять помощника из соседнего файла нельзя.
    private static string? ExpectedAdminSha256 =>
        typeof(AdminSetup).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "AdminSha256")?.Value;

    public static async Task<bool> EnsureInstalledAsync()
    {
        if (Pult.Core.Maintenance.QuietMode.Blocks()) return false;          // «Проверить всё» не ставит помощника и не вызывает окно Windows
        // Обычная (не администраторская) учётная запись: помощник без прав не заработает — не ставим и не ждём таймаутов.
        if (!App.Services.Admin.CanElevate) { ConfirmDialog.Info(AdminRights.NeedAdminText); return false; }
        var installed = await Task.Run(() => App.Services.Admin.IsInstalled);
        var near = NearAdminExe();
        if (installed && await Task.Run(() => App.Services.Admin.TaskBelongsToOtherUser))
        {
            // Задание «Pult Admin» принадлежит другому пользователю Windows: его помощник запросы этого пользователя не прочитает.
            if (near == null) { ConfirmDialog.Info(TaskOwner.OtherUserText + " " + Strings.HelperNoFile); return false; }
            if (!ConfirmDialog.Ask(TaskOwner.OtherUserText + " Переустановить сейчас?")) return false;
            return await ReinstallAsync(near);
        }
        if (installed) { await OfferUpdateAsync(near); return true; }    // отказ или сбой обновления не мешает работе установленного помощника
        if (near == null) { ConfirmDialog.Info("Не нашёл PultAdmin.exe рядом с Lumen."); return false; }
        // Файл рядом с пультом лежит в папке, куда может писать любая программа: ставим только тот, что совпал с вшитым хешем,
        // и держим его открытым (без права записи для других) до конца установки.
        var expected = ExpectedAdminSha256;
        string? why = null;
        using var locked = await Task.Run(() => AdminBinaryGate.OpenVerified(near, expected, out why));
        if (locked == null) { Log.Info("установка помощника отклонена: " + why); ConfirmDialog.Info((why ?? AdminBinaryGate.DevBuildMessage) + "."); return false; }
        if (!ConfirmDialog.Ask(Strings.AdminSetupText)) return false;
        return await InstallAsync(near);
    }

    private static async Task<bool> InstallAsync(string exe)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var rc = await Task.Run(() => AdminClient.RunElevatedCode(exe, $"--install --user \"{user}\""));
        Log.Info($"установка помощника: {rc?.ToString() ?? "отказ в окне Windows"}");
        AdminClient.ForgetTaskOwner();                                   // прежний ответ «чьё задание» устарел
        if (rc == AdminCodes.ExitWrongFolder) { ConfirmDialog.Info(Strings.HelperWrongFolder); return false; }
        return rc == 0 && await Task.Run(() => App.Services.Admin.IsInstalled);
    }

    /// Переустановка помощника под текущего пользователя (одно окно «Да»): кнопка при «установлен для другого пользователя».
    public static async Task<bool> ReinstallAsync(string? exe = null)
    {
        if (Pult.Core.Maintenance.QuietMode.Blocks()) return false;
        exe ??= NearAdminExe();
        if (exe == null) { ConfirmDialog.Info(Strings.HelperNoFile); return false; }
        string? why = null;
        using var locked = await Task.Run(() => AdminBinaryGate.OpenVerified(exe, ExpectedAdminSha256, out why));
        if (locked == null) { Log.Info("переустановка помощника отклонена: " + why); ConfirmDialog.Info((why ?? AdminBinaryGate.DevBuildMessage) + "."); return false; }
        var ok = await InstallAsync(exe);
        Log.Info($"переустановка помощника: {ok}");
        return ok;
    }

    /// PultAdmin.exe рядом с пультом (не тот, что уже установлен).
    private static string? NearAdminExe()
    {
        var near = Path.Combine(AppContext.BaseDirectory, "PultAdmin.exe");
        return File.Exists(near) && !string.Equals(Path.GetFullPath(near), Path.GetFullPath(AdminClient.InstalledExe), StringComparison.OrdinalIgnoreCase) ? near : null;
    }

    /// Установленный помощник старее (или просто другой), чем лежащий рядом с пультом — предлагаем обновить (одно окно «Да»).
    private static async Task<bool> OfferUpdateAsync(string? near)
    {
        if (near == null || _updateDeclined) return false;
        var expected = ExpectedAdminSha256;
        using var locked = await Task.Run(() => AdminBinaryGate.OpenVerified(near, expected, out _));   // обновление — только вшитой версией
        if (locked == null) return false;
        var (differs, big) = await Task.Run(() => (!string.Equals(expected, new FileHasher().Sha256(AdminClient.InstalledExe), StringComparison.OrdinalIgnoreCase), locked.Length >= MinAotSize));
        if (!differs) return false;
        if (!big) { Log.Info("PultAdmin.exe рядом с Lumen меньше 1 МБ — это не сборка NativeAOT, обновление помощника не предлагаю"); return false; }
        if (!ConfirmDialog.Ask("Рядом с Lumen лежит более новая версия помощника администратора. Обновить его? Windows один раз спросит «Да».")) { _updateDeclined = true; return false; }
        var ok = await InstallAsync(near);
        Log.Info($"обновление помощника: {ok}");
        return ok;
    }

    /// Кнопка «Обновить помощника» на странице «Обслуживание» (помощнику не хватает задачи «Pult System»): тот же путь, что и обновление при запуске —
    /// файл рядом с пультом только с вшитым хешем и NativeAOT, одно окно «Да» Windows.
    public static async Task<bool> UpdateHelperAsync()
    {
        if (Pult.Core.Maintenance.QuietMode.Blocks()) return false;
        var near = NearAdminExe();
        if (near == null) { ConfirmDialog.Info(Strings.HelperNoFile); return false; }
        var expected = ExpectedAdminSha256;
        string? why = null;
        using var locked = await Task.Run(() => AdminBinaryGate.OpenVerified(near, expected, out why));
        if (locked == null) { Log.Info("обновление помощника по кнопке отклонено: " + why); ConfirmDialog.Info((why ?? AdminBinaryGate.DevBuildMessage) + "."); return false; }
        if (locked.Length < MinAotSize) { ConfirmDialog.Info(Strings.HelperNotAot); return false; }
        if (!ConfirmDialog.Ask(Strings.HelperUpdateAsk)) return false;
        var ok = await InstallAsync(near);
        Log.Info($"обновление помощника по кнопке: {ok}");
        if (ok) _updateDeclined = false;
        return ok;
    }


}
