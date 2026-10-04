using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OnlyFansControl
{
    internal sealed class DeviceProfile
    {
        internal string Manufacturer = "Unknown", Model = "Unknown", Product = "Windows PC", Cpu = "CPU", Bios = "";
        internal EcProfile Ec;
        internal bool Supported { get { return Ec != null && ReferenceEquals(EcProfiles.Find(Ec.Id), Ec) && Ec.Matches(this); } }

        internal static DeviceProfile FromIdentity(string manufacturer, string model, string product, string cpu, string bios = "")
        {
            DeviceProfile device = new DeviceProfile { Manufacturer = manufacturer, Model = model, Product = product, Cpu = cpu, Bios = bios };
            device.Ec = EcProfiles.Match(device); return device;
        }

        internal static DeviceProfile Detect()
        {
            DeviceProfile device = new DeviceProfile();
            try
            {
                using (ManagementObjectSearcher search = Query("root\\cimv2", "SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
                foreach (ManagementObject row in search.Get()) using (row)
                { device.Manufacturer = Convert.ToString(row["Manufacturer"]); device.Model = Convert.ToString(row["Model"]); }
                using (ManagementObjectSearcher search = Query("root\\cimv2", "SELECT Version FROM Win32_ComputerSystemProduct"))
                foreach (ManagementObject row in search.Get()) using (row) device.Product = Convert.ToString(row["Version"]);
                using (ManagementObjectSearcher search = Query("root\\cimv2", "SELECT Name FROM Win32_Processor"))
                foreach (ManagementObject row in search.Get()) using (row) device.Cpu = Convert.ToString(row["Name"]).Trim();
                using (ManagementObjectSearcher search = Query("root\\cimv2", "SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
                foreach (ManagementObject row in search.Get()) using (row) device.Bios = Convert.ToString(row["SMBIOSBIOSVersion"]);
                device.Ec = EcProfiles.Match(device);
            }
            catch (Exception error) { Log.Write("Device detection: " + error.Message); }
            return device;
        }

        internal static ManagementObjectSearcher Query(string scope, string query)
        {
            return new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true });
        }
    }

    internal sealed class Sample
    {
        public DateTime Time { get; set; }
        public double? CpuLoad { get; set; }
        public double? CpuTemperature { get; set; }
        public double? GpuTemperature { get; set; }
        public double? FanRpm { get; set; }
        public double? MemoryLoad { get; set; }
        public int? Battery { get; set; }
        public bool? OnAC { get; set; }
        public string TemperatureSource { get; set; }
        public string FanSource { get; set; }
        public string SensorStatus { get; set; }
        public int? EcTemperature { get; set; }
        public byte? EcFanRaw { get; set; }
        public Dictionary<string, int> EcTemperatures { get; set; }
    }

    internal sealed class Monitor : IDisposable
    {
        private ulong previousIdle, previousTotal;
        private bool hasPrevious;
        internal static bool Administrator
        { get { using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
        internal static bool PawnInstalled
        { get { using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO")) return key != null; } }

        internal Sample Read()
        {
            Sample value = new Sample { Time = DateTime.Now, SensorStatus = "Monitoring the system" };
            ulong idle, kernel, user;
            if (Native.GetSystemTimes(out idle, out kernel, out user))
            {
                ulong total = kernel + user;
                if (hasPrevious && total > previousTotal && idle >= previousIdle)
                    value.CpuLoad = Math.Max(0, Math.Min(100, 100.0 * (1 - (double)(idle - previousIdle) / (total - previousTotal))));
                previousIdle = idle; previousTotal = total; hasPrevious = true;
            }
            Native.MemoryStatus memory = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(Native.MemoryStatus)) };
            if (Native.GlobalMemoryStatusEx(ref memory)) value.MemoryLoad = memory.Load;
            Native.PowerStatus battery;
            if (Native.GetSystemPowerStatus(out battery))
            {
                value.OnAC = battery.ACLineStatus == 255 ? (bool?)null : battery.ACLineStatus == 1;
                if (battery.BatteryLifePercent <= 100 && (battery.BatteryFlag & 128) == 0) value.Battery = battery.BatteryLifePercent;
            }
            return value;
        }

        internal static bool ValidTemperature(double value) { return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value <= 115; }
        internal static bool ValidRpm(double value) { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value < 20000; }
        public void Dispose() { }
    }

    internal static class Log
    {
        internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnlyFansControl");
        internal static void Write(string text)
        {
            try { Directory.CreateDirectory(Folder); string path = Path.Combine(Folder, "app.log");
                lock (Folder) { if (File.Exists(path) && new FileInfo(path).Length > 256000) File.WriteAllText(path, "");
                    File.AppendAllText(path, DateTime.Now.ToString("s") + " " + text + Environment.NewLine); } }
            catch { }
        }
    }
}
