using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pult.Core.Config;

public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pult", "config.json");

    private readonly string _path;
    public ConfigStore(string path) => _path = path;

    /// Текст для пользователя, если настройки пришлось восстанавливать; иначе null.
    public string? LoadWarning { get; private set; }

    public PultConfig Load()
    {
        LoadWarning = null;
        KeepLegacyCopy();
        if (TryRead(_path, out var main)) return main!;
        bool mainBroken = File.Exists(_path);
        if (mainBroken) KeepBroken(_path);
        if (TryRead(_path + ".bak", out var bak))
        {
            LoadWarning = "Файл настроек был повреждён — восстановил предыдущую версию.";
            return bak!;
        }
        if (mainBroken) LoadWarning = "Файл настроек был повреждён и копии нет — начинаю с чистых настроек. Старый файл сохранён рядом.";
        return new PultConfig();
    }

    /// Файл от версии с карточками программ и профилями (ключи Programs/Profiles): первое сохранение новой версией их сотрёт,
    /// а .bak хранит только одну прошлую версию. Один раз кладём рядом полную копию config.v1.json (существующую не трогаем).
    private void KeepLegacyCopy()
    {
        try
        {
            var legacy = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, "config.v1.json");
            if (!File.Exists(_path) || File.Exists(legacy)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("Programs", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Profiles", StringComparison.OrdinalIgnoreCase))
                { File.Copy(_path, legacy, overwrite: false); return; }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
    }

    public void Save(PultConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Json), new UTF8Encoding(false));
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak");
        else File.Move(tmp, _path);
    }

    private static bool TryRead(string path, out PultConfig? config)
    {
        config = null;
        if (!File.Exists(path)) return false;
        for (int attempt = 0; ; attempt++)
        {
            try { config = JsonSerializer.Deserialize<PultConfig>(File.ReadAllText(path, Encoding.UTF8), Json); break; }
            catch (JsonException) { return false; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 2) return false;                                  // файл занят (антивирус, синхронизация) — он цел, поэтому его не откладываем
                Thread.Sleep(150);
            }
        }
        if (config == null) return false;
        Normalize(config);
        return true;
    }

    /// null в списках (config.json может править любая программа) — приводим к рабочему виду. Старые поля (Programs, Profiles…) при чтении игнорируются.
    private static void Normalize(PultConfig c)
    {
        if (c.SchemaVersion < 2) c.SchemaVersion = 2;
        c.NoUpdate = (c.NoUpdate ?? new()).Where(x => x != null).ToList();
        c.DevRoots = (c.DevRoots ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    }

    private static void KeepBroken(string path)
    {
        var target = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Move(path, target, overwrite: true); } catch (IOException) { }
    }
}
