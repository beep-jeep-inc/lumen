using Pult.Core.Admin;
using Pult.Core.Maintenance;

namespace Pult.Admin;

/// Что умеет помощник пользователя для действия WingetUpgrade (реально — UserWinget; в тестах подставной).
public interface IWingetExecutor
{
    MaintenanceResult Run(WingetMode mode, string id);
}

/// WingetUpgrade: winget upgrade/install с правами администратора, в контексте ПОЛЬЗОВАТЕЛЯ (задача «Pult Admin», повышенно) — НЕ от SYSTEM:
/// у SYSTEM нет профиля и источников пользователя, winget под ним не работает, да и SYSTEM-исполнитель такие задания вообще не принимает.
/// Перед запуском помощник САМ спрашивает владельца (окно «Да/Нет» поверх всего, авто-«Нет» через 5 минут) и показывает Id пакета.
/// winget ищется только в папке пакета WindowsApps (не через PATH и не по псевдониму из профиля) и до окна проверяется, что сам файл, все .dll/.exe его папки и папки до корня менять могут только администраторы; окружение — чистое; аргументы фиксированные, Id — отдельным элементом.
public sealed class UserWinget(Func<string, ConfirmAnswer> confirm, Func<string?> findWinget,
                               Func<string, IReadOnlyList<string>, TimeSpan, ProcessOutcome> run,
                               Func<string, string?> trustProblem) : IWingetExecutor
{
    public static UserWinget ForThisPc() => new(
        text => NativeMethods.AskYesNo(text, "Lumen — помощник администратора", AdminMaintenance.ConfirmTimeout),
        Winget.FindForHelper,
        (exe, args, timeout) => WingetTemp.RunIn(WingetTemp.Root,
            temp => SystemProcess.Run(exe, "", timeout, CleanEnvironment.ForWinget(temp), null, args, killOnTimeout: false)),
        new PathTrust().PackageProblem);

    public MaintenanceResult Run(WingetMode mode, string id)
    {
        // Повторная проверка: исполнитель не доверяет тому, что проверила политика.
        if (!WingetArg.IsValidId(id)) return new(false, "недопустимый Id пакета winget");
        var exe = findWinget();
        if (exe == null) return new(false, "winget не найден в защищённой папке «Установщика приложений» — обнови App Installer в Microsoft Store");

        if (trustProblem(exe) is { } why)
            return new(false, "winget лежит в папке, которую могут менять обычные программы (" + why + ") — запуск с правами администратора отменён. Верни стандартные права папке C:\\Program Files\\WindowsApps.", AdminCodes.WingetUntrusted);

        var answer = confirm(WingetArg.ConfirmText(mode, id));
        if (answer == ConfirmAnswer.Timeout) return new(false, "Нет ответа в окне помощника за 5 минут — считаю «Нет». " + WingetArg.DeclinedMessage, AdminCodes.DeclinedTimeout);
        if (answer != ConfirmAnswer.Yes) return new(false, WingetArg.DeclinedMessage, AdminCodes.WingetDeclined);

        var o = run(exe, WingetArg.CommandArgs(mode, id), AdminMaintenance.WingetProcessTimeout);
        var wr = new WingetRun(o.ExitCode, WingetRunner.CleanOutput(o.Output + "\n" + o.Error), o.TimedOut, false, null, StillRunning: o.TimedOut);
        if (o.TimedOut) return new(false, WingetRunner.DescribeFailure(wr));
        if (o.ExitCode == 0) return new(true, mode == WingetMode.Install ? "Пакет установлен" : "Пакет обновлён");
        if (o.ExitCode == WingetRunner.NoApplicableUpdate) return new(true, "Обновление уже не требуется");
        return new(false, WingetRunner.DescribeFailure(wr), WingetArg.ExitCode(o.ExitCode));
    }
}
