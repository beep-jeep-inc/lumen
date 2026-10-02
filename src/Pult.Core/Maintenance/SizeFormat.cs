namespace Pult.Core.Maintenance;

public static class SizeFormat
{
    public const string Unknown = "размер узнаю при очистке";

    /// «1,4 ГБ», «230 МБ», «12 КБ»; Bytes = -1 — размер неизвестен.
    public static string Human(long bytes)
    {
        if (bytes < 0) return Unknown;
        var ci = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", ci) + " ГБ";
        if (bytes >= 1L << 20) return Math.Round(bytes / (double)(1L << 20)).ToString("0", ci) + " МБ";
        if (bytes >= 1L << 10) return Math.Round(bytes / 1024.0).ToString("0", ci) + " КБ";
        return bytes + " Б";
    }
}

public static class RuText
{
    /// Склонение по числу: 1 драйвер, 2 драйвера, 5 драйверов.
    public static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100; var b = a % 10;
        return a is >= 11 and <= 19 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }

    /// «1 драйвер», «2 драйвера», «5 драйверов».
    public static string Count(int n, string one, string few, string many) => $"{n} {Plural(n, one, few, many)}";
}
