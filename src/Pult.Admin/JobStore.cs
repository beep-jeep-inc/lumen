using System.Text;
using System.Text.RegularExpressions;
using Pult.Core.Admin;

namespace Pult.Admin;

/// Кто и как проверяет папку/файлы заданий (в тестах подставной: владельца в тестах не сменить).
public interface IJobTrust
{
    string? DirProblem(string dir);
    /// Владелец — система, писать может только система; не ссылка.
    string? FileProblem(string file);
}

public sealed class SystemJobTrust : IJobTrust
{
    public string? DirProblem(string dir) => SafeFile.ProtectedDirProblem(dir);
    public string? FileProblem(string file) => File.Exists(file) ? SafeFile.ProtectedFileProblem(file) : "нет файла";
}

/// C:\ProgramData\Pult\jobs: сюда пишет только администратор (помощник пользователя после «Да»), читает и исполняет SYSTEM.
/// Любое чтение: обычный файл (не ссылка, не hardlink), размер ограничен, владелец/права — как у папки res, имя = nonce.
public sealed class JobStore(string dir, IJobTrust trust)
{
    private static readonly Regex JobName = new(@"^[0-9a-f]{32}\.job\z", RegexOptions.CultureInvariant);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);
    /// Подтверждённое задание живёт недолго: после сбоя/перезагрузки старое не должно выполниться без нового «Да».
    public static readonly TimeSpan MaxJobAge = TimeSpan.FromMinutes(15);

    public static string DefaultDir => Path.Combine(Installer.DataDir, "jobs");
    public string Dir => dir;

    private string? DirCheck() => trust.DirProblem(dir);

    /// Папку создаёт только защищённая установка/помощник: права наследуются от защищённой папки данных.
    public static void EnsureDir(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    public void Submit(AdminJob job)
    {
        EnsureDir(dir);
        if (DirCheck() is { } bad) throw new InvalidOperationException("папка заданий не под защитой: " + bad);
        var path = Path.Combine(dir, job.Nonce + ".job");
        File.WriteAllText(path + ".tmp", job.ToJson(), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void Cancel(string nonce)
    {
        if (!AdminRequest.IsValidNonce(nonce)) return;
        try { File.Delete(Path.Combine(dir, nonce + ".job")); File.Delete(Path.Combine(dir, nonce + ".run")); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Самое старое годное свежее задание; негодные и устаревшие (старше MaxJobAge) — игнорируются и удаляются.
    /// Годное забирается (переименование в .run) до выполнения: оставшееся от убитого исполнителя .run никогда не запускается снова.
    /// Возвращает путь к .run — исполнитель удаляет его сам.
    public (AdminJob Job, string Path)? TakeOldest(Action<string>? log = null, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        if (!Directory.Exists(dir)) return null;
        if (DirCheck() is { } bad) { log?.Invoke("ОТКАЗ: папка заданий не под защитой: " + bad); return null; }
        List<FileInfo> files;
        try { files = new DirectoryInfo(dir).EnumerateFiles().OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var f in files)
        {
            if (!JobName.IsMatch(f.Name)) continue;                                  // .result/.tmp/.run/чужое — не задание
            var age = now - f.CreationTimeUtc;
            if (age > MaxJobAge || age < TimeSpan.FromMinutes(-1))
            {
                log?.Invoke("задание устарело — не выполняю");
                try { File.Delete(f.FullName); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                continue;
            }
            var job = Read(f.FullName, log);
            if (job != null)
            {
                var run = Path.ChangeExtension(f.FullName, ".run");
                try { File.Move(f.FullName, run, overwrite: false); return (job, run); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }   // не смогли забрать — пропускаем
            }
            else try { File.Delete(f.FullName); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private AdminJob? Read(string path, Action<string>? log)
    {
        try
        {
            if (trust.FileProblem(path) is { } why) { log?.Invoke("задание проигнорировано: " + why); return null; }
            var bytes = SafeFile.ReadRegular(path, AdminJob.MaxBytes);
            if (bytes == null) { log?.Invoke("задание проигнорировано: не обычный файл или слишком большой"); return null; }
            var job = AdminJob.Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            if (job == null || !string.Equals(Path.GetFileNameWithoutExtension(path), job.Nonce, StringComparison.Ordinal))
            { log?.Invoke("задание проигнорировано: плохой формат или имя"); return null; }
            return job;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { return null; }
    }

    public void WriteResult(JobResult r)
    {
        if (!AdminRequest.IsValidNonce(r.Nonce)) return;
        if (DirCheck() is { } bad) throw new InvalidOperationException("папка заданий не под защитой: " + bad);
        var path = Path.Combine(dir, r.Nonce + ".result");
        File.WriteAllText(path + ".tmp", r.ToJson(), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// Результат своего задания; null — ещё нет (или файл негодный). Прочитанный результат удаляется.
    public JobResult? TryReadResult(string nonce)
    {
        if (!AdminRequest.IsValidNonce(nonce)) return null;
        var path = Path.Combine(dir, nonce + ".result");
        if (!File.Exists(path)) return null;
        try
        {
            if (DirCheck() != null || trust.FileProblem(path) != null) return null;
            var bytes = SafeFile.ReadRegular(path, JobResult.MaxBytes);
            if (bytes == null) return null;
            var r = JobResult.Parse(new UTF8Encoding(false, false).GetString(bytes).TrimStart('\uFEFF'));
            if (r == null || r.Nonce != nonce) return null;
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return r;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Старые файлы (брошенные задания/результаты) — убрать.
    public void DeleteStale(DateTime utcNow)
    {
        try
        {
            if (!Directory.Exists(dir) || DirCheck() != null) return;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                if (utcNow - f.LastWriteTimeUtc > StaleAfter)
                    try { f.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
