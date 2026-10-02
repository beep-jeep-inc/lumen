namespace Pult.Core.Config;

public sealed class PultConfig
{
    public int SchemaVersion { get; set; } = 2;
    public string Language { get; set; } = "ru";
    /// «Запускать вместе с Windows»: по умолчанию выключено. Включённая галочка пишет ключ Run с аргументом --minimized.
    public bool StartWithWindows { get; set; }
    /// Id программ winget, которые не обновлять («не обновлять»).
    public List<string> NoUpdate { get; set; } = new();
    /// Папки, где искать проекты для очистки мусора разработчика (по умолчанию Рабочий стол).
    public List<string> DevRoots { get; set; } = new() { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
    /// Проект старше стольких дней отмечается для очистки по умолчанию.
    public int DevAgeDays { get; set; } = 30;
}
