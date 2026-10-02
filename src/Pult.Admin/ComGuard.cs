using Microsoft.Win32;

namespace Pult.Admin;

/// Ключ в HKCU (корень — Software\Classes пользователя).
public interface IUserClassesRegistry { bool KeyExists(string pathUnderClasses); }

public sealed class HkcuClasses : IUserClassesRegistry
{
    public bool KeyExists(string pathUnderClasses)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + pathUnderClasses);
            return k != null;
        }
        catch (Exception) { return true; }                                   // не смогли проверить — считаем, что есть (отказ безопаснее)
    }
}

/// (Защита в глубину: основную работу делает SYSTEM, у которого свой HKCU.)
/// Помощник с правами администратора создаёт COM-объекты (Центр обновления, WMI). Регистрации COM в HKCU\Software\Classes
/// пишет обычный пользователь, а Windows не документирует, когда они игнорируются для высокого уровня целостности, —
/// поэтому при любой такой регистрации для используемых классов помощник отказывается работать (fail closed).
public static class ComGuard
{
    public static readonly string[] ProgIds = { "Microsoft.Update.Session", "Microsoft.Update.UpdateColl", "Microsoft.Update.Searcher" };
    public static readonly string[] Clsids =
    {
        "{" + MaintenanceScripts.SessionClsid + "}", "{" + MaintenanceScripts.UpdateCollClsid + "}",
        "{5BAF654A-5A07-4264-A255-9FF54C7151E7}",   // UpdateDownloader
        "{D2E0FE7F-D23E-48E1-93C0-6FA8CC346474}",   // UpdateInstaller
        "{B699E5E8-67FF-4177-88B0-3684A3388BFB}",   // UpdateSearcher
        "{4590F811-1D3A-11D0-891F-00AA004B2E24}",   // WbemLocator
        "{CF4CC405-E2C5-4DDD-B3CE-5E7582D8C9FA}",   // WbemDefPath
        "{8BC3F05E-D86B-11D0-A075-00C04FB68820}",   // WbemLevel1Login (локальный протокол MI/CIM)
    };

    public const string Message = "Найдена пользовательская регистрация COM-класса Центра обновления/WMI (HKCU\\Software\\Classes) — "
                                  + "помощник отказывается работать, пока её нет: так программа пользователя могла бы подложить свой код";

    /// null — чисто; иначе найденный ключ.
    public static string? Problem(IUserClassesRegistry reg)
    {
        foreach (var p in ProgIds) if (reg.KeyExists(p)) return p;
        foreach (var c in Clsids)
        {
            if (reg.KeyExists(@"CLSID\" + c)) return "CLSID\\" + c;
            if (reg.KeyExists(@"Wow6432Node\CLSID\" + c)) return "Wow6432Node\\CLSID\\" + c;
        }
        return null;
    }
}
