using Microsoft.Win32;

namespace Pult.Core.Startup;

/// Где лежат записи и флаги для одного источника.
public sealed record StartupLocation(RegistryHive Hive, string? RunPath, string ApprovedPath, string? Folder);

/// Настоящее чтение реестра и папок. Запись — только значение в Explorer\StartupApproved\… (ничего другого этот класс не пишет).
public sealed class RegistryStartupAccess(Func<StartupSource, StartupLocation> locate) : IStartupAccess
{
    private const string Cur = @"Software\Microsoft\Windows\CurrentVersion\";
    private const string Machine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";

    public static RegistryStartupAccess ForThisPc() => new(Real);

    public static StartupLocation Real(StartupSource s) => s switch
    {
        StartupSource.HkcuRun => new(RegistryHive.CurrentUser, Cur + "Run", Cur + @"Explorer\StartupApproved\Run", null),
        StartupSource.HklmRun => new(RegistryHive.LocalMachine, Machine + "Run", Machine + @"Explorer\StartupApproved\Run", null),
        StartupSource.HklmRun32 => new(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", Machine + @"Explorer\StartupApproved\Run32", null),
        StartupSource.UserFolder => new(RegistryHive.CurrentUser, null, Cur + @"Explorer\StartupApproved\StartupFolder", Environment.GetFolderPath(Environment.SpecialFolder.Startup)),
        StartupSource.CommonFolder => new(RegistryHive.LocalMachine, null, Machine + @"Explorer\StartupApproved\StartupFolder", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public IReadOnlyList<StartupRaw> ReadEntries(StartupSource s)
    {
        var loc = locate(s);
        var list = new List<StartupRaw>();
        if (loc.RunPath != null)
        {
            using var b = RegistryKey.OpenBaseKey(loc.Hive, RegistryView.Registry64);
            using var k = b.OpenSubKey(loc.RunPath);
            if (k == null) return list;
            foreach (var n in k.GetValueNames())
            {
                if (n.Length == 0) continue;                                   // «(по умолчанию)» — не запись автозагрузки
                var v = k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                list.Add(new(n, v as string ?? ""));
            }
        }
        else if (loc.Folder != null && Directory.Exists(loc.Folder))
        {
            foreach (var f in Directory.EnumerateFiles(loc.Folder))
            {
                var name = Path.GetFileName(f);
                if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new(name, f));
            }
        }
        return list;
    }

    public byte[]? ReadApproved(StartupSource s, string name)
    {
        var loc = locate(s);
        using var b = RegistryKey.OpenBaseKey(loc.Hive, RegistryView.Registry64);
        using var k = b.OpenSubKey(loc.ApprovedPath);
        return k?.GetValue(name) as byte[];
    }

    public void WriteApproved(StartupSource s, string name, byte[] flag)
    {
        var loc = locate(s);
        using var b = RegistryKey.OpenBaseKey(loc.Hive, RegistryView.Registry64);
        using var k = b.CreateSubKey(loc.ApprovedPath, writable: true);
        k.SetValue(name, flag, RegistryValueKind.Binary);
    }
}

/// Издатель и описание файла из его свойств (только чтение, без запуска).
public static class StartupFileInfo
{
    public static (string Description, string Publisher) Describe(string exePath)
    {
        if (exePath.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(exePath)) return ("", "");   // сетевые пути не трогаем
        var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
        return ((v.FileDescription ?? "").Trim(), (v.CompanyName ?? "").Trim());
    }
}
