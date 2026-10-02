using System.Text.Json;

namespace Pult.Core.Admin;

/// Задание SYSTEM-исполнителю («Pult System»): помощник пользователя после подтверждения кладёт его в C:\ProgramData\Pult\jobs.
/// COM/PowerShell работают только от SYSTEM: у SYSTEM свой HKCU, подложить COM-регистрацию из профиля пользователя нельзя.
public enum AdminJobKind { DriverSearch, DriverInstall, RestorePoint, CleanSystem, RepairSystem, DiskDetails }

public sealed record AdminJob(string Nonce, AdminJobKind Kind, string Arg)
{
    public const long MaxBytes = 8 * 1024;

    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Default.AdminJob);

    public static AdminJob? Parse(string json)
    {
        try
        {
            var j = JsonSerializer.Deserialize(json, AdminJson.Default.AdminJob);
            return j != null && Enum.IsDefined(j.Kind) && AdminRequest.IsValidNonce(j.Nonce) ? j with { Arg = j.Arg ?? "" } : null;
        }
        catch (JsonException) { return null; }
    }

    /// Задание заново проходит обычную политику (аргументы — из фиксированных наборов), файлу не доверяем.
    public AdminRequest ToRequest() => new(Nonce, Kind == AdminJobKind.RestorePoint ? AdminAction.RestorePoint
                                                 : Kind == AdminJobKind.CleanSystem ? AdminAction.CleanSystem
                                                 : Kind == AdminJobKind.RepairSystem ? AdminAction.RepairSystem
                                                 : Kind == AdminJobKind.DiskDetails ? AdminAction.DiskDetails : AdminAction.DriverInstall, Arg);
}

public sealed record JobResult(string Nonce, bool Ok, string Message, string Code = "", string[]? Ids = null, string[]? Titles = null)
{
    public const long MaxBytes = 128 * 1024;

    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Default.JobResult);

    public static JobResult? Parse(string json)
    {
        try { return JsonSerializer.Deserialize(json, AdminJson.Default.JobResult); } catch (JsonException) { return null; }
    }
}
