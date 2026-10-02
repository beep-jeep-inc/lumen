using System.Text.RegularExpressions;

namespace Pult.Core.Admin;

/// Проверки путей и имён из недоверенных мест (config.json, .url, реестр).
public static class PathSafety
{
    private static readonly Regex Reserved = new(@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// Только обычный путь на локальном диске: «C:\…». UNC (\\сервер\…), «\\?\», «\\.\» и относительные пути отвергаются —
    /// иначе простая проверка File.Exists уже ходит по сети (утечка NTLM) или открывает устройство.
    public static bool IsLocalFilePath(string? p) =>
        !string.IsNullOrEmpty(p) && p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')
        && !p.Contains("\\\\") && !p.Contains("//") && Path.IsPathFullyQualified(p);

    /// Файл существует и не больше max байт (нет файла — false).
    public static bool WithinSize(string path, long max)
    {
        try { var fi = new FileInfo(path); return fi.Exists && fi.Length <= max; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// Зарезервированные имена устройств DOS (con, nul, aux, prn, com1…lpt9): Windows открывает их как устройство при любом расширении.
    public static bool IsReservedDosName(string fileName)
    {
        var dot = fileName.IndexOf('.');
        var stem = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ');
        return Reserved.IsMatch(stem);
    }
}

/// Системные утилиты — по полному пути из System32, а не по имени (иначе первой найдётся лежащая рядом с пультом подделка).
public static class SystemTools
{
    public static string Exe(string name) => Path.Combine(Environment.SystemDirectory, name);

    /// Имя задачи планировщика из настроек: без кавычек и управляющих символов.
    public static bool IsValidTaskName(string? n) =>
        !string.IsNullOrWhiteSpace(n) && n.Length <= 238 && !n.Any(c => c == '"' || char.IsControl(c));
}

/// Помощник рядом с пультом можно ставить только если он побайтно тот, чей SHA256 вшит в Pult.exe при сборке (publish.ps1).
public static class AdminBinaryGate
{
    public const string DevBuildMessage = "помощник доступен только в установленной сборке";
    private static readonly Regex Hex = new(@"^[0-9a-f]{64}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// Открывает файл без права записи для других, сверяет хеш и возвращает открытый поток: держите его, пока идёт установка,
    /// тогда файл нельзя подменить между проверкой и запуском. null — нельзя (причина в problem).
    public static FileStream? OpenVerified(string path, string? expectedSha256, out string? problem)
    {
        problem = null;
        if (string.IsNullOrEmpty(expectedSha256) || !Hex.IsMatch(expectedSha256)) { problem = DevBuildMessage; return null; }
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)) { problem = "PultAdmin.exe рядом с Lumen не совпадает со встроенной в него версией"; fs.Dispose(); return null; }
            fs.Position = 0;
            return fs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            fs?.Dispose(); problem = "не удалось открыть PultAdmin.exe: " + e.Message; return null;
        }
    }
}
