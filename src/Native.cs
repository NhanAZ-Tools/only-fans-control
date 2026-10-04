using System;
using System.Runtime.InteropServices;

namespace OnlyFansControl
{
    internal static class Native
    {
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out PowerStatus status);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PowerStatus
        {
            internal byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            internal uint BatteryLifeTime, BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatus
        {
            internal uint Length, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        internal static bool? OnAC()
        {
            PowerStatus value;
            if (!GetSystemPowerStatus(out value) || value.ACLineStatus == 255) return null;
            return value.ACLineStatus == 1;
        }
    }
}
