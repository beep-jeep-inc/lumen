namespace Pult.Admin;

/// admin.log с потолком размера: запросы может слать любая программа пользователя, журнал не должен расти бесконечно.
public static class AdminLog
{
    public const long DefaultMaxBytes = 1024 * 1024;

    public static void Append(string path, string message, long maxBytes = DefaultMaxBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > maxBytes) File.Move(path, path + ".1", overwrite: true);   // одна прежняя копия
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
