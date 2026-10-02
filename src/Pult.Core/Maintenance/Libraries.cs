namespace Pult.Core.Maintenance;

public enum UpdateGroup { Library, Program }

public static class Libraries
{
    private static readonly string[] LibraryPrefixes =
    {
        "Microsoft.VCRedist.", "Microsoft.DotNet.Runtime.", "Microsoft.DotNet.DesktopRuntime.", "Microsoft.DotNet.AspNetCore.",
        "Microsoft.DirectX", "Microsoft.GameInput", "Oracle.JavaRuntimeEnvironment", "EclipseAdoptium.",
        "Microsoft.WindowsAppRuntime", "Microsoft.UI.Xaml",
    };

    /// Системная библиотека (рантайм) или обычная программа. SDK — инструмент разработчика, это программа.
    public static UpdateGroup Classify(WingetRow r) =>
        LibraryPrefixes.Any(p => r.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? UpdateGroup.Library : UpdateGroup.Program;
}
