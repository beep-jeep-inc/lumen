namespace Pult.Core.Maintenance;

/// Подписи под числом на плитках «Обзора» — с правильным склонением («1 обновление», «2 обновления», «5 обновлений»).
public static class TileCaptions
{
    public static string Programs(int n) => RuText.Plural(n, "обновление доступно", "обновления доступны", "обновлений доступно");
    public static string Drivers(int n) => RuText.Plural(n, "драйвер можно поставить", "драйвера можно поставить", "драйверов можно поставить");
    public static string Startup(int n) => RuText.Plural(n, "запись включена в автозагрузке", "записи включены в автозагрузке", "записей включено в автозагрузке");
    public static string Disks(int n) => RuText.Plural(n, "диск требует внимания", "диска требуют внимания", "дисков требуют внимания");
    public const string BiosCareful = "из них прошивка BIOS — ставить осторожно";
    /// «≈13,3 ГБ» для плитки очистки.
    public static string Approx(long bytes) => "≈" + SizeFormat.Human(Math.Max(0, bytes));
    public static string Hidden(int n) => n > 0 ? $"и {n} скрыто" : "";
    public static string Biggest(string name) => $"Больше всего: {name}";
}
