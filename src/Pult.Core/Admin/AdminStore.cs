using System.Text;
using System.Text.Json;

namespace Pult.Core.Admin;

/// C:\ProgramData\Pult\admin.json — файл помощника (сейчас без полей; меняет только помощник).
public sealed class AdminStore
{
    public const long MaxBytes = 1024 * 1024;

    /// Корень ProgramData считаем от системной папки, а не от %SystemDrive% из окружения пользователя (его можно подменить через HKCU\Environment).
    public static string ProgramDataDir => System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.SystemDirectory)!, "ProgramData");

    public static string DefaultPath => System.IO.Path.Combine(ProgramDataDir, "Pult", "admin.json");

    // Раньше здесь лежали настройки прежних версий (сторонний обход блокировок и список разрешённых программ). Поля убраны: в старом admin.json они игнорируются при чтении и не пишутся при сохранении.

    /// Пустой набор, если файла нет, он битый или больше 1 МБ.
    public static AdminStore Load(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > MaxBytes) return new();
            return JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), AdminJson.Default.AdminStore) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, AdminJson.Default.AdminStore), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
