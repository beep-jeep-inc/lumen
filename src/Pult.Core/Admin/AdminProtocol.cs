using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pult.Core.Admin;

/// Машинно-читаемые причины отказа (текст в Message — для человека и может меняться).
public static class AdminCodes
{
    public const string NotInstalled = "not-installed";
    /// Обслуживание: нужна перезагрузка; защита системы выключена; свежая точка восстановления уже есть.
    public const string RebootRequired = "reboot-required", RestoreDisabled = "restore-disabled", RestoreRecent = "restore-recent";
    /// Владелец нажал «Нет» в окне помощника; найдена пользовательская регистрация COM (подмена) — помощник отказывается.
    public const string Busy = "busy";
    /// Обычная учётная запись (не администратор); задание «Pult Admin» принадлежит другому пользователю Windows.
    public const string NoAdminAccount = "no-admin-account", OtherUser = "other-user";
    public const string StartupMissing = "startup-missing", StartupRefused = "startup-refused";
    /// Починка Windows: sfc не смог исправить часть файлов. Winget: владелец нажал «Нет» в окне помощника перед обновлением/установкой пакета.
    public const string RepairUnfixable = "repair-unfixable", WingetDeclined = "winget-declined";
    /// Код ответа WingetUpgrade при неудаче: «winget=<код выхода winget, беззнаковое число>». Нужен Пульту, чтобы решить, что предложить дальше.
    public const string WingetExitPrefix = "winget=";
    /// winget лежит в папке, которую могут менять обычные программы — повышенный запуск отменён. Помощник завершился, не ответив (устарел).
    public const string WingetUntrusted = "winget-untrusted", HelperGone = "helper-gone";
    public const string Declined = "declined", DeclinedTimeout = "declined-timeout", SystemTaskMissing = "system-task-missing", ComRegistration = "com-registration-found";
    /// Код выхода PultAdmin: владелец нажал «Нет» в окне подтверждения самого помощника.
    public const int ExitDeclined = 10;
    /// Код выхода PultAdmin --install: рядом лежит сам Pult.exe — ставить помощника в эту папку нельзя.
    public const int ExitWrongFolder = 11;
}

/// Ответ владельца в окне подтверждения помощника.
public enum ConfirmAnswer { Yes, No, Timeout }

/// Протокол — по ИМЕНАМ (UseStringEnumConverter); имена не переименовывать, новые — в конец. StartupToggle — короткое действие (не обслуживание): меняет только флаг автозагрузки (после «Да» в окне помощника).
/// RepairSystem — долгое обслуживание (DISM + sfc от SYSTEM, без аргументов). WingetUpgrade — долгое действие помощника в контексте пользователя («Pult Admin», повышенно), НЕ от SYSTEM.
/// DiskDetails — короткое ТОЛЬКО ЧИТАЮЩЕЕ действие (счётчики износа/температуры дисков), без аргументов; окна подтверждения нет (ничего не меняет и не запускает по данным запроса).
public enum AdminAction { DriverInstall, RestorePoint, CleanSystem, StartupToggle, RepairSystem, WingetUpgrade, DiskDetails }

public sealed record AdminRequest(string Nonce, AdminAction Action, string Arg)
{
    private static readonly Regex NonceRx = new(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

    /// Nonce становится именем файла ответа (res\<nonce>.json) — только 32 строчные hex-цифры.
    public static bool IsValidNonce(string? n) => n != null && NonceRx.IsMatch(n);

    public static AdminRequest New(AdminAction a, string arg) => new(Guid.NewGuid().ToString("N"), a, arg);
    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Default.AdminRequest);
    public static AdminRequest? Parse(string json)
    {
        try
        {
            var r = JsonSerializer.Deserialize(json, AdminJson.Default.AdminRequest);
            return r != null && Enum.IsDefined(r.Action) && IsValidNonce(r.Nonce) ? r with { Arg = r.Arg ?? "" } : null;
        }
        catch (JsonException) { return null; }
    }
}

public sealed record AdminResponse(string Nonce, bool Ok, string Message, string Code = "")
{
    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Default.AdminResponse);
    public static AdminResponse? Parse(string json)
    {
        try { return JsonSerializer.Deserialize(json, AdminJson.Default.AdminResponse); } catch (JsonException) { return null; }
    }
}

/// JSON помощника без отражения (PultAdmin собирается NativeAOT). Кодировщик по умолчанию: &lt; &gt; &amp; экранируются.
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AdminRequest))]
[JsonSerializable(typeof(AdminResponse))]
[JsonSerializable(typeof(AdminStore))]
[JsonSerializable(typeof(AdminJob))]
[JsonSerializable(typeof(JobResult))]
public sealed partial class AdminJson : JsonSerializerContext;
