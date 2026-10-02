namespace Pult.Core.Startup;

/// Откуда Windows берёт программы для автозапуска. Идентификатор источника (Id) — фиксированная строка: только такие и принимает помощник.
public enum StartupSource { HkcuRun, HklmRun, HklmRun32, UserFolder, CommonFolder }

public static class StartupSources
{
    public static string Id(StartupSource s) => s switch
    {
        StartupSource.HkcuRun => "HkcuRun", StartupSource.HklmRun => "HklmRun", StartupSource.HklmRun32 => "HklmRun32",
        StartupSource.UserFolder => "UserFolder", StartupSource.CommonFolder => "CommonFolder", _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    /// Только точные имена (не Enum.TryParse: он принимает числа и любой регистр).
    public static bool TryParse(string? id, out StartupSource s)
    {
        switch (id)
        {
            case "HkcuRun": s = StartupSource.HkcuRun; return true;
            case "HklmRun": s = StartupSource.HklmRun; return true;
            case "HklmRun32": s = StartupSource.HklmRun32; return true;
            case "UserFolder": s = StartupSource.UserFolder; return true;
            case "CommonFolder": s = StartupSource.CommonFolder; return true;
            default: s = default; return false;
        }
    }

    public static bool IsFolder(StartupSource s) => s is StartupSource.UserFolder or StartupSource.CommonFolder;
    /// Общие для всех пользователей источники (HKLM и общая папка): писать флаг может только администратор.
    public static bool IsMachineWide(StartupSource s) => s is StartupSource.HklmRun or StartupSource.HklmRun32 or StartupSource.CommonFolder;

    /// Что разрешено помощнику: только машинные источники. Пользовательские (HKCU, своя папка) Пульт переключает сам.
    public static readonly IReadOnlyList<StartupSource> HelperSources = new[] { StartupSource.HklmRun, StartupSource.HklmRun32, StartupSource.CommonFolder };
    public static readonly IReadOnlyList<StartupSource> UserSources = new[] { StartupSource.HkcuRun, StartupSource.UserFolder };
    public static readonly IReadOnlyList<StartupSource> All = new[]
        { StartupSource.HkcuRun, StartupSource.HklmRun, StartupSource.HklmRun32, StartupSource.UserFolder, StartupSource.CommonFolder };

    public static string Title(StartupSource s) => s switch
    {
        StartupSource.HkcuRun => "Только для вас (реестр)", StartupSource.HklmRun => "Для всех пользователей (реестр)",
        StartupSource.HklmRun32 => "Для всех пользователей (реестр, 32-битные)", StartupSource.UserFolder => "Папка «Автозагрузка» (ваша)",
        StartupSource.CommonFolder => "Папка «Автозагрузка» (общая)", _ => "",
    };
}

/// Запись автозагрузки «как есть»: для реестра — имя значения и команда, для папки — имя файла и его полный путь.
public sealed record StartupRaw(string Name, string Command);

/// Доступ к реестру и папкам автозагрузки. Настоящая реализация — RegistryStartupAccess; в тестах — подделка.
public interface IStartupAccess
{
    IReadOnlyList<StartupRaw> ReadEntries(StartupSource s);
    /// Байты значения Explorer\StartupApproved\… для записи; null — значения нет (значит, включено).
    byte[]? ReadApproved(StartupSource s, string name);
    /// Пишет ТОЛЬКО флаг в Explorer\StartupApproved\… (сама запись автозагрузки не меняется).
    void WriteApproved(StartupSource s, string name, byte[] flag);
}

/// 12-байтовый флаг Explorer\StartupApproved — ровно как пишет Диспетчер задач.
public static class StartupApproval
{
    public const int FlagLength = 12;

    /// Нет значения или первый байт чётный (02, 06) — включено; нечётный (03) — выключено.
    public static bool IsEnabled(byte[]? data) => data == null || data.Length == 0 || (data[0] & 1) == 0;

    /// Включено: 02 00 00 00 и нули. Выключено: 03 00 00 00 и время выключения (FILETIME, 8 байт).
    public static byte[] Flag(bool on, DateTime utcNow)
    {
        var b = new byte[FlagLength];
        if (on) { b[0] = 0x02; return b; }
        b[0] = 0x03;
        BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(b, 4);
        return b;
    }
}

/// Разбор команды записи автозагрузки на путь программы и сокращение пути для показа.
public static class StartupCommand
{
    /// Путь программы из строки команды: в кавычках, либо до «.exe», либо первое слово. Переменные вида %ProgramFiles% раскрывает expand.
    public static string? ExePath(string command, Func<string, string>? expand = null)
    {
        var c = (command ?? "").Trim();
        if (c.Length == 0) return null;
        if (expand != null) c = expand(c);
        if (c.StartsWith('"')) { var end = c.IndexOf('"', 1); return end > 1 ? c[1..end] : null; }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return c[..(exe + 4)];
        var sp = c.IndexOf(' ');
        return sp > 0 ? c[..sp] : c;
    }

    /// Длинный путь → «C:\…\папка\файл.exe» не длиннее max знаков.
    public static string Shorten(string path, int max = 58)
    {
        if (path.Length <= max || max < 12) return path;
        var root = path.Length > 3 && path[1] == ':' ? path[..3] : "";
        var tail = path[^(max - root.Length - 2)..];
        var cut = tail.IndexOf('\\');
        if (cut >= 0 && cut < tail.Length - 1) tail = tail[(cut + 1)..];
        return root + "…\\" + tail;
    }
}

public sealed record StartupEntry(StartupSource Source, string Name, string Command, string? ExePath, bool Enabled, bool IsSelf, string Description, string Publisher, bool Missing = false);

/// Список автозагрузки: чтение записей + состояние из StartupApproved. Ничего не пишет.
public static class StartupScanner
{
    public const string SelfName = "Pult";

    public static bool IsSelfEntry(StartupSource s, string name, string? exePath, string? ownExe) =>
        s == StartupSource.HkcuRun && (string.Equals(name, SelfName, StringComparison.OrdinalIgnoreCase)
            || (exePath != null && ownExe != null && string.Equals(exePath, ownExe, StringComparison.OrdinalIgnoreCase)));

    public static IReadOnlyList<StartupEntry> Scan(IStartupAccess access, Func<string, (string Description, string Publisher)>? describe = null,
                                                   Func<string, string>? expand = null, string? ownExe = null, Func<string, bool>? exists = null)
    {
        var list = new List<StartupEntry>();
        foreach (var s in StartupSources.All)
        {
            IReadOnlyList<StartupRaw> raw;
            try { raw = access.ReadEntries(s); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var r in raw.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string? exe = StartupSources.IsFolder(s)
                    ? (r.Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? r.Command : null)       // ярлык .lnk без разбора: цель не читаем
                    : StartupCommand.ExePath(r.Command, expand);
                (string Description, string Publisher) info = ("", "");
                if (exe != null && describe != null) { try { info = describe(exe); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
                bool on;
                try { on = StartupApproval.IsEnabled(access.ReadApproved(s, r.Name)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { on = true; }
                var entry = new StartupEntry(s, r.Name, r.Command, exe, on, IsSelfEntry(s, r.Name, exe, ownExe), info.Description, info.Publisher);
                list.Add(exists != null && !entry.IsSelf && StartupHealth.IsMissing(entry, exists) ? entry with { Missing = true } : entry);
            }
        }
        return list;
    }
}

public sealed record StartupToggleResult(bool Ok, string Message, string Code = "");

/// Строка аргумента помощника: «Источник|on|имя» или «Источник|off|имя» (имя — всё после второй «|»).
public static class StartupArg
{
    public const int MaxNameLength = 260;

    public static string Build(StartupSource s, bool on, string name) => $"{StartupSources.Id(s)}|{(on ? "on" : "off")}|{name}";

    /// Имя записи: 1…260 знаков, без управляющих символов и пробелов по краям; для папки — только имя файла (без путей и недопустимых для файла знаков).
    public static bool IsValidName(StartupSource s, string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return false;
        foreach (var ch in name) if (char.IsControl(ch)) return false;
        if (name != name.Trim()) return false;
        if (StartupSources.IsFolder(s))
        {
            if (name is "." or "..") return false;
            if (name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) return false;
        }
        return true;
    }

    /// Текст окна помощника: из ПРОЧИТАННОЙ им записи, не из запроса; без управляющих и «невидимых» символов (bidi, разделители строк).
    public static string ConfirmText(StartupSource source, bool on, StartupRaw raw) =>
        $"{(on ? "Включить" : "Выключить")} автозапуск «{Clean(raw.Name)}» для всех пользователей?" + NL + NL + StartupSources.Title(source) + NL +
        "Команда: " + StartupCommand.Shorten(Clean(raw.Command), 200) + NL + NL + "Если ты этого не запрашивал(а) — нажми «Нет».";

    private const string NL = "\n";

    /// Убирает управляющие символы и символы категорий Format/LineSeparator/ParagraphSeparator.
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) continue;
            var cat = char.GetUnicodeCategory(ch);
            if (cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// false — формат не тот, источник не из набора помощника, значение не on/off или плохое имя.
    public static bool TryParseForHelper(string? arg, out StartupSource source, out bool on, out string name, out string problem)
    {
        source = default; on = false; name = ""; problem = "";
        if (string.IsNullOrEmpty(arg) || arg.Length > MaxNameLength + 24) { problem = "неверный аргумент автозагрузки"; return false; }
        var p = arg.Split('|', 3);
        if (p.Length != 3) { problem = "неверный аргумент автозагрузки"; return false; }
        if (!StartupSources.TryParse(p[0], out source) || !StartupSources.HelperSources.Contains(source)) { problem = "источник автозагрузки не разрешён помощнику"; return false; }
        if (p[1] != "on" && p[1] != "off") { problem = "нужно «on» или «off»"; return false; }
        on = p[1] == "on";
        if (!IsValidName(source, p[2])) { problem = "недопустимое имя записи автозагрузки"; return false; }
        name = p[2];
        return true;
    }
}

/// Единственное место, где меняется флаг автозагрузки (Пульт — для своих источников, помощник — для машинных).
public static class StartupToggler
{
    public const string MissingCode = "startup-missing", RefusedCode = "startup-refused";

    /// confirm — окно подтверждения самого помощника (получает прочитанную запись); у Пульта (свои источники) его нет.
    public static StartupToggleResult Toggle(IStartupAccess access, StartupSource s, string name, bool on, DateTime utcNow, IReadOnlyCollection<StartupSource> allowed,
                                             Func<StartupRaw, Pult.Core.Admin.ConfirmAnswer>? confirm = null)
    {
        if (!allowed.Contains(s)) return new(false, "этот источник автозагрузки здесь менять нельзя", RefusedCode);
        if (!StartupArg.IsValidName(s, name)) return new(false, "недопустимое имя записи автозагрузки", RefusedCode);
        IReadOnlyList<StartupRaw> entries;
        try { entries = access.ReadEntries(s); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return new(false, "не удалось прочитать автозагрузку: " + e.Message); }
        // Пишем под именем из самого списка (а не из запроса): запись обязана существовать.
        var found = entries.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found == null) return new(false, "такой записи в автозагрузке нет (возможно, её уже убрали)", MissingCode);
        if (s == StartupSource.HkcuRun && string.Equals(found.Name, StartupScanner.SelfName, StringComparison.OrdinalIgnoreCase))
            return new(false, "это сам Lumen — он управляется галочкой внизу меню", RefusedCode);
        if (confirm != null)
        {
            var answer = confirm(found);
            if (answer == Pult.Core.Admin.ConfirmAnswer.Timeout) return new(false, "Нет ответа в окне помощника за 5 минут — считаю «Нет»", Pult.Core.Admin.AdminCodes.DeclinedTimeout);
            if (answer != Pult.Core.Admin.ConfirmAnswer.Yes) return new(false, "Ты отказался в окне помощника — ничего не изменено", Pult.Core.Admin.AdminCodes.Declined);
        }
        try { access.WriteApproved(s, found.Name, StartupApproval.Flag(on, utcNow)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return new(false, "не удалось записать: " + e.Message); }
        return new(true, on ? "включено" : "выключено");
    }
}
