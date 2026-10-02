using System.IO;

namespace Pult.App.Services;

public static class Log
{
    private static readonly string Path_ = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pult", "pult.log");
    private static readonly object Lock = new();
    public static void Info(string m) => Write("INFO", m);
    /// Что делал человек в момент сбоя: открытый раздел и текущее действие. Идёт в строку каждой ошибки (по стеку одному причину не найти).
    public static volatile string? Section, Action;
    private static string? Context => Section == null && Action == null ? null : string.Join(" · ", new[] { Section == null ? null : "открыт: " + Section, Action == null ? null : "действие: " + Action }.Where(x => x != null));
    public static void Error(string m, Exception? e = null) => Write("ERR ", e == null ? m : $"{m}: {Pult.Core.Diagnostics.ExceptionReport.Describe(e, Context)}");
    private static void Write(string lvl, string m)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
                if (File.Exists(Path_) && new FileInfo(Path_).Length > 1_000_000) File.Move(Path_, Path_ + ".1", overwrite: true);
                File.AppendAllText(Path_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {lvl} {m}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

