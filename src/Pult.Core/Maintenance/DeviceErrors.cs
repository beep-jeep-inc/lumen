using System.Management;

namespace Pult.Core.Maintenance;

public sealed record DeviceProblem(string Name, int Code, string Explain);

/// Устройства, у которых Диспетчер устройств показывает ошибку (жёлтый значок), — с понятным объяснением.
public static class DeviceErrors
{
    /// Код 45 («устройство не подключено») — не проблема, такие в список не попадают.
    public const int NotConnected = 45;

    private static readonly Dictionary<int, string> Table = new()
    {
        [1] = "устройство настроено неправильно",
        [3] = "драйвер повреждён или не хватает памяти",
        [10] = "устройство не запускается",
        [12] = "не хватает свободных ресурсов для устройства",
        [14] = "чтобы заработало, нужна перезагрузка компьютера",
        [18] = "драйвер нужно переустановить",
        [19] = "настройки устройства в реестре повреждены",
        [21] = "Windows удаляет это устройство",
        [22] = "устройство отключено",
        [24] = "устройство отсутствует или работает неправильно",
        [28] = "драйвер не установлен",
        [31] = "Windows не может загрузить драйвер устройства",
        [32] = "драйвер отключён",
        [33] = "Windows не может определить нужные устройству ресурсы",
        [34] = "нужно настроить устройство вручную",
        [35] = "прошивка компьютера не даёт данных для устройства",
        [37] = "драйвер вернул ошибку при запуске",
        [38] = "драйвер не загрузился, потому что предыдущая копия ещё в памяти",
        [39] = "драйвер повреждён или отсутствует",
        [40] = "в реестре неправильные данные об устройстве",
        [41] = "драйвер загружен, но устройство не найдено",
        [42] = "такое устройство уже есть в системе",
        [43] = "устройство сообщило о сбое",
        [44] = "программа или служба остановила устройство",
        [45] = "устройство сейчас не подключено",
        [47] = "устройство подготовлено к извлечению",
        [48] = "драйвер заблокирован — не подходит этой версии Windows",
        [49] = "реестр слишком большой, новые устройства не добавляются",
        [50] = "Windows не может применить свойства устройства",
        [51] = "устройство ждёт другое устройство",
        [52] = "у драйвера нет действительной цифровой подписи",
    };

    public static string Explain(int code) =>
        Table.TryGetValue(code, out var s) ? s : $"ошибка устройства (код {code})";

    /// Из пар «имя, код» оставляет настоящие проблемы (без кода 0 и 45).
    public static IReadOnlyList<DeviceProblem> Build(IEnumerable<(string? Name, int Code)> devices) =>
        devices.Where(d => d.Code != 0 && d.Code != NotConnected)
               .Select(d => new DeviceProblem(string.IsNullOrWhiteSpace(d.Name) ? "Неизвестное устройство" : d.Name!, d.Code, Explain(d.Code)))
               .ToList();

    public static IReadOnlyList<DeviceProblem> Current()
    {
        var raw = new List<(string?, int)>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode<>0");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    try { raw.Add((o["Name"] as string, Convert.ToInt32(o["ConfigManagerErrorCode"]))); } catch { }
                }
            }
        }
        catch { }
        return Build(raw);
    }
}
