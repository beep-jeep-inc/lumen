namespace Pult.Core.Admin;

public sealed record Decision(bool Allowed, string Reason, string Code = "");

public static class AdminPolicy
{
    public static Decision Decide(AdminRequest r)
    {
        switch (r.Action)
        {
            // Обслуживание: аргументы — только из фиксированных наборов; пути, поиск обновлений и проверки делает сам помощник.
            case AdminAction.DriverInstall:
                return AdminMaintenance.TryParseDriverIds(r.Arg, out _, out _, out var idProblem) ? new(true, "ok") : Deny(idProblem);

            case AdminAction.RestorePoint:
                return r.Arg.Length == 0 ? new(true, "ok") : Deny("у этого действия не бывает аргументов");

            case AdminAction.CleanSystem:
                return AdminMaintenance.IsCleanId(r.Arg) ? new(true, "ok") : Deny("неизвестный вид очистки");

            // Починка Windows: фиксированные команды (DISM, sfc), аргументов не бывает вовсе.
            case AdminAction.RepairSystem:
                return r.Arg.Length == 0 ? new(true, "ok") : Deny("у этого действия не бывает аргументов");

            // Winget с правами администратора: только «upgrade|Id» или «install|Id», Id по строгому шаблону. Окно подтверждения показывает сам помощник.
            case AdminAction.WingetUpgrade:
                return Pult.Core.Maintenance.WingetArg.TryParse(r.Arg, out _, out _, out var wingetProblem) ? new(true, "ok") : Deny(wingetProblem);

            // Автозагрузка: источник из фиксированного набора (только машинные), «on»/«off», имя без управляющих символов.
            // Что запись существует и что писать — проверяет и делает сам помощник; путей и команд из запроса он не исполняет.
            case AdminAction.StartupToggle:
                return Pult.Core.Startup.StartupArg.TryParseForHelper(r.Arg, out _, out _, out _, out var startupProblem) ? new(true, "ok") : Deny(startupProblem);

            // Здоровье дисков (только чтение): аргументов не бывает вовсе — читается фиксированный набор счётчиков.
            case AdminAction.DiskDetails:
                return r.Arg.Length == 0 ? new(true, "ok") : Deny("у этого действия не бывает аргументов");
        }
        return Deny("неизвестное действие");
    }

    private static Decision Deny(string why, string code = "") => new(false, why, code);
}
