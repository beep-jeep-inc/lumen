using System.Runtime.InteropServices;

namespace Pult.App.Services;

/// Питание от сети или от батареи (без WinForms): нужно, чтобы не ставить прошивку BIOS от батареи.
public static class PowerLine
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus s);

    /// true — ноутбук точно работает от батареи (ACLineStatus = 0). Неизвестно (255) — не считаем батареей.
    public static bool IsOnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
}
