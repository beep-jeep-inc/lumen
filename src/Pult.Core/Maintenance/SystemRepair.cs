using System.Text;
using System.Text.RegularExpressions;
using Pult.Core.Monitoring;

namespace Pult.Core.Maintenance;

/// Что сказал sfc (по его тексту и, если текста не хватило, по журналу CBS).
public enum SfcVerdict { Clean, Repaired, Unfixable, CouldNotRun, RebootPending, Unknown }

/// Итог «Починки Windows»: Ok — удалось; Code — машинный признак (AdminCodes.RebootRequired или RepairUnfixable), Message — текст простыми словами.
public sealed record RepairOutcome(bool Ok, string Message, string Code = "");

/// «Починка Windows»: DISM /RestoreHealth, затем sfc /scannow. Здесь только чистая логика (разбор вывода и итог) — запуск делает помощник.
public static class SystemRepair
{
    public const string DismArguments = "/Online /Cleanup-Image /RestoreHealth", SfcArguments = "/scannow";
    public static readonly TimeSpan DismTimeout = TimeSpan.FromMinutes(45), SfcTimeout = TimeSpan.FromMinutes(30);
    public const int DismRebootRequired = 3010;

    /// Имена процессов, при которых вторую починку не запускаем (идёт чужая проверка или наша прошлая).
    public static readonly string[] BusyProcessNames = { "sfc", "dism", "dismhost" };
    public static bool AnotherRunning(IProcessQuery q) => BusyProcessNames.Any(q.IsRunning);

    /// Вывод sfc при перенаправлении — в UTF-16 (с BOM или без; в нём всегда есть нулевые байты). Остальное читаем как UTF-8.
    public static string DecodeConsole(byte[]? raw)
    {
        if (raw == null || raw.Length == 0) return "";
        string s;
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) s = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        else if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) s = Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
        else
        {
            var probe = Math.Min(raw.Length, 4096);
            var hasNul = false;
            for (var i = 0; i < probe; i++) if (raw[i] == 0) { hasNul = true; break; }
            s = hasNul ? Encoding.Unicode.GetString(raw) : Encoding.UTF8.GetString(raw);
        }
        return s.Replace("\0", "");
    }

    // «для завершения которого/которой требуется перезагрузка» — форма зависит от сборки Windows; запасной вариант — «требуется перезагрузка» рядом со словами про восстановление/sfc.
    private static readonly Regex RebootPendingRu = new(@"для завершения котор(?:ого|ой|ых) требуется перезагрузка|требуется перезагрузка.{0,80}(?:восстановлени|sfc)|(?:восстановлени|sfc).{0,120}требуется перезагрузка", RegexOptions.CultureInvariant);

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ");

    /// Итоговая фраза sfc (английская или русская Windows) → вердикт; null — фразы нет.
    public static SfcVerdict? ParseConsole(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Norm(text);
        if (t.Contains("pending which requires reboot") || RebootPendingRu.IsMatch(t)) return SfcVerdict.RebootPending;
        if (t.Contains("could not perform the requested operation") || t.Contains("не может выполнить запрошенную операцию")) return SfcVerdict.CouldNotRun;
        if (t.Contains("unable to fix") || t.Contains("не может восстановить")) return SfcVerdict.Unfixable;
        if (t.Contains("successfully repaired") || t.Contains("успешно их восстановила") || t.Contains("успешно восстановила")) return SfcVerdict.Repaired;
        if (t.Contains("did not find any integrity violations") || t.Contains("не обнаружила нарушений целостности")) return SfcVerdict.Clean;
        return null;
    }

    private static readonly Regex RepairingSome = new(@"\[SR\]\s+Repairing\s+([1-9][0-9]*)\s+components", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// Запасной путь: кусок CBS.log, дописанный за время sfc. «[SR] Cannot repair member file» — не починено; «[SR] Repairing N components» (N > 0)
    /// или «Repairing corrupted file» — чинилось; «[SR] Verify complete» без этого — чисто. «Repairing 0 components» бывает и в чистом прогоне.
    public static SfcVerdict? ParseCbs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("[SR] Cannot repair", StringComparison.OrdinalIgnoreCase)) return SfcVerdict.Unfixable;
        if (RepairingSome.IsMatch(text) || text.Contains("[SR] Repairing corrupted file", StringComparison.OrdinalIgnoreCase)) return SfcVerdict.Repaired;
        if (text.Contains("[SR] Verify complete", StringComparison.OrdinalIgnoreCase)) return SfcVerdict.Clean;
        return null;
    }

    /// Сначала текст sfc (самое надёжное), потом журнал CBS; код выхода один сам по себе (sfc отдаёт 0 и при починке) ничего не решает.
    public static SfcVerdict ParseSfc(string? consoleText, string? cbsText) => ParseConsole(consoleText) ?? ParseCbs(cbsText) ?? SfcVerdict.Unknown;

    public const string UnfixableCode = "repair-unfixable";

    /// Общий итог: DISM (код выхода) + sfc (вердикт). Перезагрузка предлагается только кнопкой (код RebootRequired); сам Пульт ничего не перезагружает.
    public static RepairOutcome Summarize(int dismExit, bool dismTimedOut, SfcVerdict sfc, int sfcExit, bool sfcTimedOut)
    {
        const string reboot = Admin.AdminCodes.RebootRequired;
        var dismNote = dismTimedOut ? "DISM не уложился в 45 минут и остановлен"
                     : dismExit is 0 or DismRebootRequired ? "" : $"DISM не смог восстановить хранилище компонентов (код {dismExit}); для него нужен интернет";
        var dismNeedsReboot = !dismTimedOut && dismExit == DismRebootRequired;
        string Add(string head) => dismNote.Length == 0 ? head : head + " " + dismNote + ".";

        if (sfcTimedOut) return new(false, Add("Проверка файлов (sfc) не уложилась в 30 минут и остановлена."), dismNeedsReboot ? reboot : "");
        switch (sfc)
        {
            case SfcVerdict.Clean:
                return dismNeedsReboot ? new(true, "Повреждений нет, но DISM просит перезагрузку — перезагрузите ПК.", reboot)
                                       : new(true, Add("Повреждений нет."));
            case SfcVerdict.Repaired:
                return new(true, Add("Повреждения найдены и исправлены. Перезагрузите ПК, чтобы изменения вступили в силу."), reboot);
            case SfcVerdict.Unfixable:
                return new(false, Add("Не удалось исправить часть системных файлов — подробности в журнале C:\\Windows\\Logs\\CBS\\CBS.log."), UnfixableCode);
            case SfcVerdict.RebootPending:
                return new(false, "Windows ждёт перезагрузки, чтобы закончить прежнюю починку. Перезагрузите ПК и запустите проверку ещё раз.", reboot);
            case SfcVerdict.CouldNotRun:
                return new(false, Add("Проверка файлов не смогла выполниться (sfc отказался работать). Перезагрузите ПК и повторите."), dismNeedsReboot ? reboot : "");
            default:
                return sfcExit == 0
                    ? new(true, Add("Проверка завершилась без ошибок, но итог в тексте распознать не удалось — подробности в журнале C:\\Windows\\Logs\\CBS\\CBS.log."))
                    : new(false, Add($"Проверка завершилась с кодом {sfcExit}, итог распознать не удалось — подробности в журнале C:\\Windows\\Logs\\CBS\\CBS.log."));
        }
    }
}
