using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pult.Core.Admin;

namespace Pult.Admin;

/// Окружение для программ, которые запускает помощник: только системные переменные (HKLM), ничего из
/// HKCU\Environment и окружения пользователя (DOTNET_STARTUP_HOOKS, PSModulePath, NODE_OPTIONS, PATH пользователя…).
public static class CleanEnvironment
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// forApp — для разрешённых программ с окном: ещё папки профиля пользователя (USERPROFILE, APPDATA, LOCALAPPDATA).
    public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> machine, string systemDir,
                                                   IReadOnlyDictionary<string, string>? userProfile = null)
    {
        var sysRoot = Path.GetDirectoryName(systemDir)!;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = sysRoot,
            ["windir"] = sysRoot,
            ["SystemDrive"] = Path.GetPathRoot(sysRoot)!.TrimEnd('\\'),
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["CommonProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            ["ProgramData"] = AdminStore.ProgramDataDir,
            ["ALLUSERSPROFILE"] = AdminStore.ProgramDataDir,
            ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(),
        };
        var raw = machine.Where(kv => !kv.Key.Equals("USERNAME", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (k, v) in raw) env.TryAdd(k, v);                              // значения HKLM, ещё не раскрытые
        foreach (var (k, v) in raw) env[k] = Expand(v, env);
        env["ComSpec"] = Path.Combine(systemDir, "cmd.exe");
        env["TEMP"] = env["TMP"] = Path.Combine(sysRoot, "Temp");
        env.TryAdd("PATHEXT", ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC");
        env.TryAdd("Path", $@"{systemDir};{sysRoot};{systemDir}\Wbem;{systemDir}\WindowsPowerShell\v1.0\");
        if (userProfile != null)
            foreach (var k in new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA" })
                if (userProfile.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) env[k] = v;
        return env;
    }

    public static Dictionary<string, string> ForSystem() => Build(MachineValues(), Environment.SystemDirectory);

    /// Для программ в контексте ПОЛЬЗОВАТЕЛЯ (winget с правами администратора): системное окружение + папки профиля по известным папкам Windows
    /// (а не по переменным окружения из HKCU\Environment). Всё остальное из окружения пользователя не берётся.
    public static Dictionary<string, string> ForUser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var env = Build(MachineValues(), Environment.SystemDirectory, new Dictionary<string, string>
        {
            ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["LOCALAPPDATA"] = local,
        });
        env["TEMP"] = env["TMP"] = Path.Combine(local, "Temp");
        env["USERNAME"] = Environment.UserName;
        env["USERDOMAIN"] = Environment.UserDomainName;
        return env;
    }

    /// ForUser с собственной временной папкой (TEMP и TMP): установщики winget скачиваются туда, а не в %LOCALAPPDATA%\Temp, куда пишет любая программа пользователя.
    public static Dictionary<string, string> ForWinget(string tempDir) => WithTemp(ForUser(), tempDir);

    public static Dictionary<string, string> WithTemp(Dictionary<string, string> env, string tempDir)
    {
        env["TEMP"] = env["TMP"] = tempDir;
        return env;
    }

    public static void Apply(ProcessStartInfo psi, IReadOnlyDictionary<string, string> env)
    {
        psi.Environment.Clear();
        foreach (var (k, v) in env) psi.Environment[k] = v;
    }

    private static Dictionary<string, string> MachineValues()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.LocalMachine.OpenSubKey(MachineKey);
        if (key == null) return d;
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string s) d[name] = s;
        return d;
    }

    /// Раскрыть %VAR% только по чистому окружению (не Environment.ExpandEnvironmentVariables — там переменные пользователя).
    private static string Expand(string v, IReadOnlyDictionary<string, string> env) =>
        Regex.Replace(v, "%([^%;]+)%", m => env.TryGetValue(m.Groups[1].Value, out var r) && !r.Contains('%') ? r : m.Value);
}
