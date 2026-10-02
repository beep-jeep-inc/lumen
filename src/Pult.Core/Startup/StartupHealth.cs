using System.Text.RegularExpressions;

namespace Pult.Core.Startup;

/// «Важная» запись автозагрузки: часть Windows или драйвера. Kind — короткая метка, Warning — что может пропасть, если выключить.
public sealed record ImportantInfo(string Kind, string Warning);

/// Чистые проверки записей автозагрузки: нет ли файла программы и не часть ли это Windows/драйвера. Ничего не читает и не пишет сама
/// (существование файла проверяет переданная функция).
public static class StartupHealth
{
    private static readonly string[] ProgramExts = { ".exe", ".com", ".bat", ".cmd", ".scr", ".lnk" };

    /// Файл программы записи не найден. Проверяем только уверенные случаи: полный локальный путь (не сетевой, не «просто имя») к исполняемому файлу.
    /// Всё сомнительное (имя без пути, сетевой путь, путь с аргументами без «.exe») считается «неизвестно» = НЕ помечается.
    public static bool IsMissing(StartupEntry e, Func<string, bool> exists)
    {
        var p = e.ExePath;
        if (string.IsNullOrWhiteSpace(p) || p.Length > 520 || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.Contains('%')) return false;                    // сеть и нераскрытые переменные не проверяем
        if (!(p.Length > 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/')) return false;    // только «C:\…»
        if (!ProgramExts.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) return false;
        try { return !exists(p); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private sealed record Rule(string Kind, string Warning, Regex Rx);
    private static Regex R(string words) => new(@"(?<![A-Za-z])(" + words + @")(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Rule[] Rules =
    {
        new("защита Windows", "Это часть защиты Windows. Если выключить, значок и уведомления Защитника могут пропасть, и проблемы с безопасностью останутся незамеченными.",
            R(@"SecurityHealth|Windows Defender|Windows Security|WindowsDefender|MsMpEng")),
        new("драйвер звука", "Это часть звукового драйвера. Если выключить, может пропасть управление звуком: микрофон, эффекты, переключение наушников и колонок.",
            R(@"Realtek|Rtk[A-Za-z0-9]*|RTHDVCPL|Nahimic|Dolby|Conexant|Waves ?Maxx|HD ?Audio|SoundResearch")),
        new("драйвер видеокарты", "Это часть драйвера видеокарты. Если выключить, могут пропасть настройки экрана и видеокарты, а на ноутбуках — регулировка яркости клавишами.",
            R(@"NVIDIA|NvTray|NvBackend|AMD|Radeon|RadeonSoftware|igfx[A-Za-z0-9]*|Intel\(R\) Graphics|Intel Graphics|IntelGraphics")),
        new("драйвер тачпада", "Это часть драйвера тачпада. Если выключить, могут перестать работать жесты и прокрутка двумя пальцами.",
            R(@"Synaptics|SynTP[A-Za-z0-9]*|ETDCtrl|Elan|Touchpad|ApntEx|Alps|Precision Touchpad")),
        new("программа производителя", "Это программа производителя компьютера. Она может отвечать за функциональные клавиши, яркость, режим питания и вентиляторы; без неё они могут перестать работать.",
            R(@"Lenovo|Dell|Hewlett|HP|ASUS|Acer|MSI")),
        new("драйвер мыши или клавиатуры", "Это программа для мыши или клавиатуры. Если выключить, дополнительные кнопки, подсветка и настройки могут перестать работать.",
            R(@"LGHUB|Logitech|Logi|Razer|SteelSeries|iCUE|Corsair")),
    };

    /// null — запись обычная. Смотрим имя, описание, издателя и файл; программы из хранилища драйверов Windows — всегда «важные».
    public static ImportantInfo? Important(StartupEntry e)
    {
        if (e.IsSelf) return null;
        var hay = string.Join(" | ", new[] { e.Name, e.Description, e.Publisher, e.ExePath ?? "" });
        foreach (var r in Rules) if (r.Rx.IsMatch(hay)) return new(r.Kind, r.Warning);
        if ((e.ExePath ?? e.Command).Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase))
            return new("драйвер устройства", "Это часть драйвера какого-то устройства. Если выключить, у этого устройства могут пропасть настройки или функции.");
        return null;
    }

    /// Текст вопроса перед выключением важной записи.
    public static string ConfirmText(StartupEntry e, ImportantInfo i) => $"«{e.Name}» — {i.Warning}\n\nВыключить всё равно? Включить обратно можно этой же кнопкой.";
}
