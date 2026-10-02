using System.Management;

namespace Pult.Core.Maintenance;

public enum DiskState { Healthy, Warning, Unhealthy, Unknown }

/// Один физический диск. Счётчики (износ, температура, ошибки, наработка) — null, если Windows их не отдала без прав.
public sealed record DiskInfo(string Name, string MediaType, long SizeBytes, int HealthStatus, IReadOnlyList<int> OperationalStatus,
                              int? Wear, int? TemperatureC, long? ReadErrors, long? PowerOnHours,
                              string DeviceId = "", IReadOnlyList<VolumeUsage>? Volumes = null, long? WriteErrors = null);

public sealed record DiskVerdict(DiskState State, string Summary, IReadOnlyList<string> Reasons);

/// Разбор того, что сообщили MSFT_PhysicalDisk и MSFT_StorageReliabilityCounter, простыми словами. Чистые функции — без обращения к системе.
public static class DiskHealth
{
    public const int WearWarnPercent = 80, TempWarnSsd = 70, TempWarnHdd = 55;
    public const string HealthyText = "Исправен", UnhealthyText = "Диск сообщает о неисправности — сохраните важные данные", NoDataText = "Нет данных о состоянии";

    /// MSFT_PhysicalDisk.HealthStatus: 0 исправен, 1 предупреждение, 2 неисправен, 5 неизвестно.
    private static DiskState FromHealth(int h) => h switch { 0 => DiskState.Healthy, 1 => DiskState.Warning, 2 => DiskState.Unhealthy, _ => DiskState.Unknown };

    /// MSFT_PhysicalDisk.OperationalStatus: 2 OK, 3 снижена работоспособность, 5 ожидается отказ, 6 ошибка, 7 неисправимая ошибка, 10 остановлен, 0xD00E потеряна связь…
    /// MSFT_Partition.DiskNumber — номер MSFT_Disk, а не DeviceId физического диска. Они совпадают у обычных дисков; у Storage Spaces, файловых виртуальных
    /// и дублирующихся номеров — нет. Тома показываем только у номеров, где ровно один MSFT_Disk и это не пул/виртуальный диск (BusType 15, 16).
    public static IReadOnlySet<string> TrustedDiskNumbers(IEnumerable<(string Number, int BusType)> msftDisks)
    {
        var groups = msftDisks.GroupBy(d => d.Number, StringComparer.OrdinalIgnoreCase);
        return groups.Where(g => g.Count() == 1 && g.Single().BusType is not (15 or 16)).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsBadOperational(int code) => code is 5 or 6 or 7;
    public static bool IsOddOperational(int code) => code is not (0 or 2) && !IsBadOperational(code);

    public static string MediaTypeName(int mediaType, int busType) => mediaType switch
    {
        3 => "HDD", 4 => "SSD", 5 => "SCM", _ => busType == 17 ? "SSD" : "накопитель",           // 17 — NVMe
    };

    /// Итог для одного диска: Неисправен > Внимание > Исправен; нет ни здоровья, ни счётчиков — «нет данных».
    public static DiskVerdict Assess(DiskInfo d)
    {
        var reasons = new List<string>();
        var state = FromHealth(d.HealthStatus);
        if (d.OperationalStatus.Any(IsBadOperational)) { state = DiskState.Unhealthy; reasons.Add("Windows ждёт отказа диска или видит ошибку"); }
        else if (d.OperationalStatus.Any(IsOddOperational) && state == DiskState.Healthy) { state = DiskState.Warning; reasons.Add("диск работает с отклонениями"); }

        if (state == DiskState.Unhealthy) reasons.Insert(0, "диск сообщает о неисправности");
        else if (state == DiskState.Warning && reasons.Count == 0) reasons.Add("диск сообщает о проблемах");

        var warn = new List<string>();
        if (d.Wear is { } w && w >= WearWarnPercent) warn.Add($"износ {w}%");
        if (d.ReadErrors is { } e && e > 0) warn.Add($"ошибки чтения: {e}");
        var tMax = d.MediaType == "HDD" ? TempWarnHdd : TempWarnSsd;
        if (d.TemperatureC is { } t && t >= tMax) warn.Add($"температура {t} °C");

        if (warn.Count > 0 && state is DiskState.Healthy or DiskState.Unknown) state = DiskState.Warning;
        reasons.AddRange(warn);

        // Нет статуса здоровья и нет ни одного счётчика — ничего не знаем.
        if (state == DiskState.Unknown && d.Wear == null && d.TemperatureC == null && d.ReadErrors == null)
            return new(DiskState.Unknown, NoDataText, new[] { "Windows не сообщила состояние этого диска" });
        if (state == DiskState.Unknown) state = DiskState.Healthy;                              // здоровье неизвестно, но счётчики в порядке

        return state switch
        {
            DiskState.Unhealthy => new(state, UnhealthyText, reasons),
            DiskState.Warning => new(state, "Внимание: " + string.Join(", ", reasons), reasons),
            _ => new(DiskState.Healthy, HealthyText, reasons),
        };
    }

    /// Сколько дисков требуют внимания (Warning или Unhealthy).
    public static int NeedAttention(IEnumerable<DiskVerdict> v) => v.Count(x => x.State is DiskState.Warning or DiskState.Unhealthy);
}

public sealed record DiskReadResult(IReadOnlyList<DiskInfo> Disks, bool CountersAvailable, string? Error);

/// Чтение дисков через CIM (root\Microsoft\Windows\Storage). Только чтение, без прав администратора: если счётчики не отдаются —
/// диски показываются без них. Любой сбой — текст в Error, без исключений наружу. Ограничен по времени.
public static class DiskReader
{
    private const string Ns = @"\\.\ROOT\Microsoft\Windows\Storage";

    public static async Task<DiskReadResult> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        var work = Task.Run(Read, ct);
        var done = await Task.WhenAny(work, Task.Delay(timeout, ct));
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (done != work) return new(Array.Empty<DiskInfo>(), false, "Windows не ответила вовремя — попробуйте ещё раз позже.");
        return await work;
    }

    private static DiskReadResult Read()
    {
        try
        {
            var opts = new ConnectionOptions { Timeout = TimeSpan.FromSeconds(20) };
            var scope = new ManagementScope(Ns, opts);
            scope.Connect();
            var enumOpts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(20), ReturnImmediately = false };

            var counters = new Dictionary<string, (int? Wear, int? Temp, long? Errors, long? Hours)>(StringComparer.OrdinalIgnoreCase);
            bool countersOk = true;
            try
            {
                using var cs = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_StorageReliabilityCounter"), enumOpts);
                foreach (ManagementObject o in cs.Get())
                    using (o)
                    {
                        var id = Convert.ToString(o["DeviceId"]) ?? "";
                        counters[id] = (ToInt(o["Wear"]), ToInt(o["Temperature"]), ToLong(o["ReadErrorsUncorrected"]), ToLong(o["PowerOnHours"]));
                    }
            }
            catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            { countersOk = false; counters.Clear(); }

            var volumes = ReadVolumes(scope, enumOpts);
            var disks = new List<DiskInfo>();
            using var ds = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"), enumOpts);
            foreach (ManagementObject o in ds.Get())
                using (o)
                {
                    var id = Convert.ToString(o["DeviceId"]) ?? "";
                    var name = (Convert.ToString(o["FriendlyName"]) ?? "").Trim();
                    if (name.Length == 0) name = "Диск " + id;
                    var op = (o["OperationalStatus"] as Array)?.Cast<object>().Select(x => Convert.ToInt32(x)).ToList() ?? new List<int>();
                    counters.TryGetValue(id, out var c);
                    disks.Add(new(name, DiskHealth.MediaTypeName(ToInt(o["MediaType"]) ?? 0, ToInt(o["BusType"]) ?? 0), ToLong(o["Size"]) ?? 0,
                                  ToInt(o["HealthStatus"]) ?? 5, op, c.Wear, c.Temp is > 0 ? c.Temp : null, c.Errors, c.Hours,
                                  id, volumes.TryGetValue(id, out var vs) ? vs : null));
                }
            return new(disks.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), countersOk, null);
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
        {
            return new(Array.Empty<DiskInfo>(), false, "Не удалось получить сведения о дисках: " + e.Message);
        }
    }

    /// Тома по номеру физического диска: MSFT_Partition (номер диска + буква) и размер/свободное место из DriveInfo. Без прав; любой сбой — просто без томов.
    private static Dictionary<string, IReadOnlyList<VolumeUsage>> ReadVolumes(ManagementScope scope, System.Management.EnumerationOptions opts)
    {
        var map = new Dictionary<string, List<VolumeUsage>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var trusted = new List<(string, int)>();
            using (var dk = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Number, BusType FROM MSFT_Disk"), opts))
                foreach (ManagementObject o in dk.Get())
                    using (o) trusted.Add((Convert.ToString(o["Number"]) ?? "", ToInt(o["BusType"]) ?? 0));
            var ok = DiskHealth.TrustedDiskNumbers(trusted);
            using var ps = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"), opts);
            foreach (ManagementObject o in ps.Get())
                using (o)
                {
                    var letterCode = ToInt(o["DriveLetter"]) ?? 0;
                    if (letterCode is < 'A' or > 'Z' && letterCode is < 'a' or > 'z') continue;
                    var letter = char.ToUpperInvariant((char)letterCode).ToString();
                    var disk = Convert.ToString(o["DiskNumber"]) ?? "";
                    if (!ok.Contains(disk)) continue;                  // неоднозначный номер (Storage Spaces и т.п.) — без томов, чтобы не приписать чужому диску
                    try
                    {
                        var di = new DriveInfo(letter);
                        if (!di.IsReady) continue;
                        if (!map.TryGetValue(disk, out var l)) map[disk] = l = new();
                        if (l.All(x => x.Letter != letter)) l.Add(new VolumeUsage(letter, di.TotalSize, di.AvailableFreeSpace));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        return map.ToDictionary(k => k.Key, k => (IReadOnlyList<VolumeUsage>)k.Value.OrderBy(v => v.Letter, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static int? ToInt(object? v) { try { return v == null ? null : Convert.ToInt32(v); } catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; } }
    private static long? ToLong(object? v) { try { return v == null ? null : Convert.ToInt64(v); } catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; } }
}
