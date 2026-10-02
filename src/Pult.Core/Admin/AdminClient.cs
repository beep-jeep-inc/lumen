using System.ComponentModel;
using System.Diagnostics;

namespace Pult.Core.Admin;

public sealed class AdminClient
{
    private readonly string _dir; private readonly string _resDir; private readonly Func<bool> _runTask; private readonly Func<bool> _isInstalled;
    private readonly Func<bool> _canElevate; private readonly Func<bool> _ownsTask; private readonly Func<bool> _helperAlive;
    private readonly SemaphoreSlim _one = new(1, 1);
    private volatile int _holder = -1;                       // AdminAction текущего держателя, -1 — никто

    /// Долгие действия (драйверы, очистка, починка Windows, winget с правами): помощник занят до часа, короткие действия быстро получают «занято».
    private static bool IsMaintenance(AdminAction a) => a is AdminAction.DriverInstall or AdminAction.CleanSystem or AdminAction.RestorePoint
                                                         or AdminAction.RepairSystem or AdminAction.WingetUpgrade;

    /// Сколько короткое действие (проверки и запуск) ждёт, пока помощник занят долгим обслуживанием.
    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(5);

    /// Сколько после запуска не проверять «помощник ещё жив» (задание стартует не мгновенно) и как часто проверять потом.
    public TimeSpan HelperGrace { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan AliveCheckEvery { get; init; } = TimeSpan.FromSeconds(2);

    /// userDir — куда пульт кладёт admin.req (%LOCALAPPDATA%\Pult); resDir — где помощник оставляет ответы (C:\ProgramData\Pult\res, обычным программам — только чтение).
    /// canElevate — пользователь состоит в Администраторах (иначе помощник без прав, функции недоступны сразу);
    /// ownsTask — задание «Pult Admin» принадлежит текущему пользователю Windows. Оба по умолчанию «да» (тесты, режим предпросмотра).
    public AdminClient(string userDir, string resDir, Func<bool> runTask, Func<bool> isInstalled, Func<bool>? canElevate = null, Func<bool>? ownsTask = null,
                       Func<bool>? helperAlive = null)
    { _dir = userDir; _resDir = resDir; _runTask = runTask; _isInstalled = isInstalled; _canElevate = canElevate ?? (() => true); _ownsTask = ownsTask ?? (() => true); _helperAlive = helperAlive ?? DefaultHelperAlive; }

    /// Есть ли живой процесс PultAdmin (имена повышенных процессов видны и обычному пользователю; SYSTEM-исполнитель — тот же PultAdmin.exe).
    private static bool DefaultHelperAlive()
    {
        var ps = Process.GetProcessesByName("PultAdmin");
        try { return ps.Length > 0; } finally { foreach (var p in ps) p.Dispose(); }
    }

    public bool CanElevate => _canElevate();
    /// Задание принадлежит другому пользователю (только если помощник вообще установлен).
    public bool TaskBelongsToOtherUser => _isInstalled() && !_ownsTask();

    public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pult");
    public static string ResponseDir => Path.Combine(AdminStore.ProgramDataDir, "Pult", "res");
    public static string InstalledExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pult", "PultAdmin.exe");

    public static AdminClient ForThisPc() => new(UserDir, ResponseDir,
        runTask: () => RunQuiet(SystemTools.Exe("schtasks.exe"), $"/Run /TN \"{TaskXml.TaskName}\"") == 0,
        isInstalled: () => File.Exists(InstalledExe) && RunQuiet(SystemTools.Exe("schtasks.exe"), $"/Query /TN \"{TaskXml.TaskName}\"") == 0,
        canElevate: AdminRights.CanElevate,
        ownsTask: TaskOwnedByMe);

    private static readonly object OwnerLock = new();
    private static (DateTime At, bool Owns)? _ownerCache;

    /// После (пере)установки помощника прежний ответ про владельца задания устарел.
    public static void ForgetTaskOwner() { lock (OwnerLock) _ownerCache = null; }

    /// Только чтение: XML задания (schtasks /Query /XML) → UserId. Ответ помним минуту — не запускать schtasks на каждый запрос.
    private static bool TaskOwnedByMe()
    {
        lock (OwnerLock)
        {
            if (_ownerCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Owns;
            bool owns;
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                owns = TaskOwner.BelongsTo(ReadTaskXml(), id.User?.Value ?? "", id.Name);
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { owns = true; }
            _ownerCache = (DateTime.UtcNow, owns);
            return owns;
        }
    }

    private static string? ReadTaskXml()
    {
        var psi = new ProcessStartInfo(SystemTools.Exe("schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/Query"); psi.ArgumentList.Add("/TN"); psi.ArgumentList.Add(TaskXml.TaskName); psi.ArgumentList.Add("/XML");
        using var p = Process.Start(psi);
        if (p == null) return null;
        var text = p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
        return p.ExitCode == 0 ? text.Result : null;
    }

    public bool IsInstalled => _isInstalled();

    /// Ждать ответ столько, сколько нужно этому действию (установка драйверов — до 35 мин, DISM — до 65 мин).
    public Task<AdminResponse> SendAsync(AdminAction a, string arg, CancellationToken ct, string? progressText = null, IProgress<string>? progress = null) =>
        SendAsync(a, arg, AdminMaintenance.WaitTimeout(a, arg), ct, progressText, progress);

    /// progressText — подпись для долгих действий («Устанавливаю драйверы…»): пока ждём ответа, раз в секунду в progress уходит «подпись (м:сс)».
    public async Task<AdminResponse> SendAsync(AdminAction a, string arg, TimeSpan timeout, CancellationToken ct, string? progressText = null, IProgress<string>? progress = null)
    {
        if (!_canElevate()) return new("", false, AdminRights.NeedAdminText, AdminCodes.NoAdminAccount);
        if (!_isInstalled()) return new("", false, "помощник администратора не установлен — Lumen предложит поставить его", AdminCodes.NotInstalled);
        if (!_ownsTask()) return new("", false, TaskOwner.OtherUserText, AdminCodes.OtherUser);
        // Быстро отказываем (5 с) только когда помощника держит долгое обслуживание; короткие действия друг друга ждут как раньше.
        var holderIsMaintenance = _holder >= 0 && IsMaintenance((AdminAction)_holder);
        var wait = IsMaintenance(a) || !holderIsMaintenance ? timeout : BusyWait;
        if (!await _one.WaitAsync(wait, ct))
            return _holder >= 0 && IsMaintenance((AdminAction)_holder)
                ? new("", false, "помощник занят обслуживанием (драйверы, очистка, починка Windows или обновление программ) — попробуй позже", AdminCodes.Busy)
                : new("", false, "помощник занят другим действием — попробуй ещё раз через минуту", AdminCodes.Busy);
        _holder = (int)a;
        var reqPath = Path.Combine(_dir, "admin.req");
        try
        {
            Directory.CreateDirectory(_dir);
            var req = AdminRequest.New(a, arg);
            var res = Path.Combine(_resDir, req.Nonce + ".json");
            File.WriteAllText(reqPath, req.ToJson());
            if (!_runTask()) return new(req.Nonce, false, "не удалось запустить помощника (задача «Pult Admin»)");
            var started = DateTime.UtcNow; var until = started + timeout; var shown = -1;
            var nextAlive = started + HelperGrace; var gone = 0;
            AdminResponse? Read()
            {
                if (!File.Exists(res)) return null;
                string text; try { text = File.ReadAllText(res); } catch (IOException) { return null; }
                return AdminResponse.Parse(text) is { } r && r.Nonce == req.Nonce ? r : null;
            }
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(150, ct);
                if (progress != null && progressText != null)
                {
                    var el = DateTime.UtcNow - started;
                    if ((int)el.TotalSeconds != shown) { shown = (int)el.TotalSeconds; progress.Report($"{progressText} ({(int)el.TotalMinutes}:{el.Seconds:00})"); }
                }
                if (Read() is { } got) return got;
                // Помощник завершился, не ответив (старый помощник не знает действия, сбой до записи ответа): не ждём весь срок.
                if (DateTime.UtcNow >= nextAlive)
                {
                    nextAlive = DateTime.UtcNow + AliveCheckEvery;
                    gone = _helperAlive() ? 0 : gone + 1;
                    if (gone >= 2)
                    {
                        if (Read() is { } late) return late;                          // гонка «записал ответ и вышел»
                        return new(req.Nonce, false, "помощник завершился, не ответив — скорее всего, он устарел. Нажми «Обновить помощника».", AdminCodes.HelperGone);
                    }
                }
            }
            return new(req.Nonce, false, "помощник не ответил вовремя");
        }
        finally
        {
            try { File.Delete(reqPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // запрос удаляет пульт, не помощник
            _holder = -1;
            _one.Release();
        }
    }

    /// Запуск с окном «Да». false — если пользователь нажал «Нет» или запуск не удался.
    public static bool RunElevated(string exe, string args, bool wait = true, string? workDir = null)
    {
        var rc = RunElevatedCode(exe, args, wait, workDir);
        return rc == 0;
    }

    /// То же, но возвращает код выхода помощника; null — «Нет» в окне Windows или запуск не удался.
    public static int? RunElevatedCode(string exe, string args, bool wait = true, string? workDir = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" };
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!wait) return 0;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return null; }
    }

    /// Путь в кавычках для командной строки помощника: хвостовой «\» перед закрывающей кавычкой экранировал бы её.
    public static string QuotePath(string path) => "\"" + path.Trim().TrimEnd('\\', '/').TrimEnd() + "\"";

    public static string? FindAdminExe()
    {
        var near = Path.Combine(AppContext.BaseDirectory, "PultAdmin.exe");
        return File.Exists(near) ? near : File.Exists(InstalledExe) ? InstalledExe : null;
    }

    private static int RunQuiet(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
        if (p == null) return -1;
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (InvalidOperationException) { } return -1; }
        return p.ExitCode;
    }
}
