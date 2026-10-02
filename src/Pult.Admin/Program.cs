using Pult.Admin;
using Pult.Core.Admin;

// Недостающие DLL — только из System32 и папки самого помощника (C:\Program Files\Pult), не из PATH пользователя.
NativeMethods.SetDefaultDllDirectories(NativeMethods.LoadLibrarySearchSystem32 | NativeMethods.LoadLibrarySearchApplicationDir);

var log = Path.Combine(Installer.DataDir, "admin.log");
void Log(string m)
{
    if (SafeFile.ProtectedDirProblem(Installer.DataDir) != null) return;                // в чужую папку не пишем
    AdminLog.Append(log, m);
}

try
{
    switch (args.FirstOrDefault())
    {
        case null:
        {
            var bad = SafeFile.ProtectedDirProblem(Installer.DataDir) ?? SafeFile.ProtectedDirProblem(Installer.ResDir)
                      ?? SafeFile.ProtectedFileProblem(AdminStore.DefaultPath);
            if (bad != null)                                                             // admin.json/res не под защитой — ничего не делаем
            {
                Log("ОТКАЗ: данные помощника не под защитой (" + bad + ") — переустанови помощника");
                Console.Error.WriteLine(bad);
                return 8;
            }
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pult");
            var res = new RequestProcessor(Path.Combine(local, "admin.req"), Installer.ResDir,
                SafeFile.ProtectedDirProblem, BrokeredMaintenance.ForThisPc(),
                winget: UserWinget.ForThisPc(),
                startupConfirm: t => NativeMethods.AskYesNo(t, "Lumen — помощник администратора", AdminMaintenance.ConfirmTimeout)).ProcessOnce();
            if (res != null) Log($"{(res.Ok ? "OK" : "ОТКАЗ")} {res.Message}");
            return 0;
        }
        case "--system-job" when args.Length == 1:                                       // только от задания «Pult System» (SYSTEM)
            return SystemWorker.Run(SystemWorker.CurrentIsSystem, new JobStore(JobStore.DefaultDir, new SystemJobTrust()),
                                    new SystemJobExecutor(SystemMaintenance.ForSystemWorker()), Log);
        case "--install" when args.Length >= 3 && args[1] == "--user":
        {
            var rc = Installer.Install(args[2], Log); Log($"установка для {args[2]} → {rc}"); return rc;
        }
        case "--uninstall":
            return Installer.Uninstall(Log);
        default:
            Console.Error.WriteLine("PultAdmin: неизвестная команда"); return 1;
    }
}
catch (Exception e) { Log("ошибка: " + e); Console.Error.WriteLine(e.Message); return 9; }
