using System;

namespace OnlyFansControl
{
    internal sealed class EcProfile
    {
        internal readonly string Id, Name, MachineType, CpuToken;
        internal readonly ushort DataPort, CommandPort;
        internal readonly byte FanRegister, CpuTemperatureRegister, LastTemperatureRegister, RpmLowRegister, RpmHighRegister;

        internal EcProfile(string id, string name, string machineType, string cpuToken)
        {
            Id = id; Name = name; MachineType = machineType; CpuToken = cpuToken;
            DataPort = 0x62; CommandPort = 0x66; FanRegister = 0x2F;
            CpuTemperatureRegister = 0x78; LastTemperatureRegister = 0x7F; RpmLowRegister = 0x84; RpmHighRegister = 0x85;
        }

        internal bool Matches(DeviceProfile device)
        {
            return string.Equals(device.Manufacturer, "LENOVO", StringComparison.OrdinalIgnoreCase)
                && (device.Model ?? "").StartsWith(MachineType, StringComparison.OrdinalIgnoreCase)
                && string.Equals((device.Product ?? "").Trim(), Name, StringComparison.OrdinalIgnoreCase)
                && (device.Cpu ?? "").IndexOf("AMD Ryzen", StringComparison.OrdinalIgnoreCase) >= 0
                && (device.Cpu ?? "").IndexOf(CpuToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class EcProfiles
    {
        internal static readonly EcProfile L14 = new EcProfile("thinkpad-l14-gen4-amd", "ThinkPad L14 Gen 4", "21H6", "7530U");
        internal static readonly EcProfile T495 = new EcProfile("thinkpad-t495", "ThinkPad T495", "20NK", "AMD Ryzen");
        private static readonly EcProfile[] all = { L14, T495 };

        internal static EcProfile Match(DeviceProfile device)
        { foreach (EcProfile profile in all) if (profile.Matches(device)) return profile; return null; }
        internal static EcProfile Find(string id)
        { foreach (EcProfile profile in all) if (profile.Id == id) return profile; return null; }
    }
}
