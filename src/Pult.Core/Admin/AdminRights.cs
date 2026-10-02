using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Pult.Core.Admin;

/// Может ли текущий пользователь вообще получить права администратора (состоит в группе «Администраторы»).
/// Не то же самое, что «сейчас повышен»: у обычного администратора с UAC токен урезанный, но группа в нём есть (deny-only).
public static class AdminRights
{
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string NeedAdminText = "Нужна учётная запись администратора Windows — у тебя обычная. Эти функции недоступны.";

    private static bool? _cached;

    /// Результат запоминается на сессию: состав групп токена не меняется, пока процесс жив.
    public static bool CanElevate() => _cached ??= Probe();

    /// Логика выбора по списку SID групп токена (с любыми атрибутами, в том числе deny-only) — отдельно, чтобы проверить без системы.
    public static bool ContainsAdministrators(IEnumerable<string> tokenGroupSids) =>
        tokenGroupSids.Any(s => string.Equals(s, AdministratorsSid, StringComparison.OrdinalIgnoreCase));

    private static bool Probe()
    {
        try { return ContainsAdministrators(TokenGroupSids()); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { return true; }   // не смогли узнать — не блокируем
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int returnLength);

    private const int TokenGroups = 2;

    /// Все группы токена процесса. WindowsIdentity.Groups deny-only группы пропускает, поэтому читаем TokenGroups сами.
    private static List<string> TokenGroupSids()
    {
        using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var token = id.AccessToken.DangerousGetHandle();
        GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out var need);
        if (need <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buf = Marshal.AllocHGlobal(need);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buf, need, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var count = Marshal.ReadInt32(buf);
            var ptr = IntPtr.Size;                                              // SID_AND_ATTRIBUTES[] выровнен по размеру указателя
            var stride = IntPtr.Size * 2;
            var list = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var sidPtr = Marshal.ReadIntPtr(buf, ptr + i * stride);
                list.Add(new SecurityIdentifier(sidPtr).Value);
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// Повышен ли процесс сейчас (полный токен при включённом UAC). TokenElevationType: 1 — без разделения, 2 — полный, 3 — урезанный.
    public static bool IsProcessElevatedByUac()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var token = id.AccessToken.DangerousGetHandle();
            var buf = Marshal.AllocHGlobal(4);
            try { return GetTokenInformation(token, 18 /* TokenElevationType */, buf, 4, out _) && Marshal.ReadInt32(buf) == 2; }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException) { return false; }
    }
}

/// Задание «Pult Admin» принадлежит пользователю, который его ставил. Если на ПК сидит другой — запросы этого пользователя помощник не прочитает.
public static class TaskOwner
{
    public const string OtherUserText = "Помощник установлен для другого пользователя Windows — переустанови его (одно окно «Да»).";
    private static readonly Regex UserIdRx = new(@"<UserId>\s*([^<]+?)\s*</UserId>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// true — задание принадлежит этому пользователю (по SID или по имени DOMAIN\user). Непонятный XML (null/без UserId) считаем «не знаю» — не блокируем.
    public static bool BelongsTo(string? taskXml, string currentSid, string currentName)
    {
        if (string.IsNullOrWhiteSpace(taskXml)) return true;
        var m = UserIdRx.Match(taskXml);
        if (!m.Success) return true;
        var id = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        return string.Equals(id, currentSid, StringComparison.OrdinalIgnoreCase) || string.Equals(id, currentName, StringComparison.OrdinalIgnoreCase);
    }
}
