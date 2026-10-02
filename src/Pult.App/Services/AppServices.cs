using System.IO;
using Pult.Core.Admin;
using Pult.Core.Config;

namespace Pult.App.Services;

/// Общие службы приложения: настройки, помощник администратора, сохранение.
public sealed class AppServices
{
    public ConfigStore Store { get; }
    public PultConfig Config { get; private set; }
    public AdminClient Admin { get; }

    private AppServices()
    {
        Store = new ConfigStore(ConfigStore.DefaultPath);
        Config = Store.Load();                         // нет файла — чистые настройки; старые поля (программы, профили) игнорируются
        Admin = AdminClient.ForThisPc();
    }

    public static AppServices Create() => new();

    private readonly object _saveLock = new();

    /// Сохранение зовут с потока окна; из фона — переносим на него, чтобы не писать один файл одновременно.
    public void Save()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) { d.Invoke(SaveCore); return; }
        SaveCore();
    }

    /// Не удалось записать настройки (файл занят или только для чтения) — окно показывает баннер.
    public event Action<string>? SaveFailed;

    private void SaveCore()
    {
        if (App.Preview) return;                       // режим --preview ничего не сохраняет (в нём вымышленные данные)
        lock (_saveLock)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { Store.Save(Config); break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 2) { Log.Error("сохранение настроек", e); SaveFailed?.Invoke("Не удалось сохранить настройки: " + e.Message); return; }
                    Thread.Sleep(200);
                }
            }
        }
    }
}
