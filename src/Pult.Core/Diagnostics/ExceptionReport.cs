using System.Text;

namespace Pult.Core.Diagnostics;

/// Полная запись об исключении для журнала: контекст (раздел/действие), тип, сообщение, вся цепочка InnerException
/// (и все ветки AggregateException) со стеком каждого звена. Чистая функция — без обращения к системе.
public static class ExceptionReport
{
    public static string Describe(Exception e, string? context = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context)) sb.Append("[").Append(context).Append("] ");
        Append(sb, e, 0);
        return sb.ToString().TrimEnd();
    }

    private static void Append(StringBuilder sb, Exception e, int depth)
    {
        if (depth > 8) { sb.AppendLine().Append(' ', depth * 2).Append("… (цепочка обрезана)"); return; }
        var pad = new string(' ', depth * 2);
        if (depth > 0) sb.AppendLine().Append(pad).Append("--> внутреннее: ");
        sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
        if (e.TargetSite is { } site) sb.AppendLine().Append(pad).Append("  метод: ").Append(site.DeclaringType?.FullName).Append('.').Append(site.Name);
        if (!string.IsNullOrWhiteSpace(e.StackTrace))
            foreach (var line in e.StackTrace.Split('\n')) sb.AppendLine().Append(pad).Append("  ").Append(line.TrimEnd('\r'));
        if (e is AggregateException ag) foreach (var inner in ag.InnerExceptions) Append(sb, inner, depth + 1);
        else if (e.InnerException != null) Append(sb, e.InnerException, depth + 1);
    }
}
