using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pult.App.Theme;

/// Общий вид окон «Чернила»: тёмный заголовок (на Windows 11 — цвета полосы под фон окна, чтобы выглядело слитно), скруглённые углы (DWM), чернильно-синий фон из ресурса WindowBg.
/// Системные кнопки окна остаются настоящими. На Windows 10 цветовые атрибуты не поддерживаются — остаётся тёмная системная полоса.
public static class Backdrop
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DarkMode = 20, CornerPreference = 33, BorderColor = 34, CaptionColor = 35, TextColor = 36, RoundCorners = 2;

    private static int Bgr(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static void Apply(Window w) => Apply(w, true);

    /// caption: красить ли системную полосу заголовка под фон (главное окно); у диалогов без полосы не нужно.
    public static void Apply(Window w, bool caption)
    {
        w.SetResourceReference(Window.BackgroundProperty, "WindowBg");
        w.SetResourceReference(Window.ForegroundProperty, "Text");
        w.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        void Do()
        {
            var h = new WindowInteropHelper(w).Handle;
            int dark = 1, round = RoundCorners;
            DwmSetWindowAttribute(h, DarkMode, ref dark, sizeof(int));
            DwmSetWindowAttribute(h, CornerPreference, ref round, sizeof(int));
            if (!caption) return;
            int cap = Bgr(0x0A, 0x0E, 0x1F), txt = Bgr(0xAA, 0xB2, 0xCE), border = Bgr(0x25, 0x2D, 0x4E);
            DwmSetWindowAttribute(h, CaptionColor, ref cap, sizeof(int));      // Windows 11 (22000+); на Windows 10 вернёт ошибку — ничего страшного
            DwmSetWindowAttribute(h, TextColor, ref txt, sizeof(int));
            DwmSetWindowAttribute(h, BorderColor, ref border, sizeof(int));
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Do(); else w.SourceInitialized += (_, _) => Do();
    }
}
