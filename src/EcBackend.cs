using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace OnlyFansControl
{
    internal sealed class EcReading
    {
        internal int Temperature;
        internal int? CpuTemperature, Rpm;
        internal byte Raw;
        internal Dictionary<int, int> Temperatures = new Dictionary<int, int>();
    }

    internal interface IEcBackend : IDisposable
    {
        EcReading Read();
        void WriteFan(byte value);
    }

    internal sealed class EcBackend : IEcBackend
    {
        private const uint DeviceType = 41394u << 16;
        private const uint LoadCode = DeviceType | (0x821u << 2);
        private const uint ExecuteCode = DeviceType | (0x841u << 2);
        private readonly EcProfile profile;
        private readonly SafeFileHandle handle;
        private readonly Mutex access;
        private readonly Mutex thinkpadAccess;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

        internal EcBackend(DeviceProfile profile)
        {
            if (!profile.Supported) throw new InvalidOperationException("No EC profile matches this device; direct fan control is disabled.");
            this.profile = profile.Ec;
            if (!Monitor.Administrator) throw new UnauthorizedAccessException("Run the app as administrator to connect to the EC.");
            access = new Mutex(false, @"Global\Access_EC"); thinkpadAccess = new Mutex(false, @"Global\Access_Thinkpad_EC");
            handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000u, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); access.Dispose(); thinkpadAccess.Dispose();
                throw new Win32Exception(error, "Unable to open PawnIO. Install the Official version from pawnio.eu, then restart the app."); }
            try
            {
                byte[] module;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LpcACPIEC.bin"))
                using (MemoryStream memory = new MemoryStream()) { if (stream == null) throw new IOException("The signed EC module is missing."); stream.CopyTo(memory); module = memory.ToArray(); }
                uint returned;
                if (!DeviceIoControl(handle, LoadCode, module, (uint)module.Length, null, 0, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "PawnIO rejected the signed EC module.");
                // Reading plausibility is a compatibility gate, not proof of this model's register semantics.
                Read();
            }
            catch { handle.Dispose(); access.Dispose(); thinkpadAccess.Dispose(); throw; }
        }

        private static bool Acquire(Mutex mutex)
        {
            try { return mutex.WaitOne(200); } catch (AbandonedMutexException) { return true; }
        }

        private T Transaction<T>(Func<T> action)
        {
            if (!Acquire(access)) throw new TimeoutException("Another application is using the EC.");
            try
            {
                if (!Acquire(thinkpadAccess)) throw new TimeoutException("The ThinkPad EC is busy.");
                try { return action(); } finally { thinkpadAccess.ReleaseMutex(); }
            }
            finally { access.ReleaseMutex(); }
        }

        private long[] Execute(string function, long[] values, int outputCount)
        {
            byte[] input = new byte[32 + values.Length * 8];
            byte[] name = Encoding.ASCII.GetBytes(function); Buffer.BlockCopy(name, 0, input, 0, name.Length);
            Buffer.BlockCopy(values, 0, input, 32, values.Length * 8);
            byte[] output = outputCount == 0 ? null : new byte[outputCount * 8]; uint returned;
            if (!DeviceIoControl(handle, ExecuteCode, input, (uint)input.Length, output, (uint)(outputCount * 8), out returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The EC communication command failed.");
            if (returned != outputCount * 8) throw new IOException("PawnIO returned an invalid byte count.");
            long[] result = new long[outputCount]; if (outputCount > 0) Buffer.BlockCopy(output, 0, result, 0, (int)returned);
            return result;
        }

        private byte ReadPort(ushort port) { return checked((byte)Execute("ioctl_pio_read", new long[] { port }, 1)[0]); }
        private void WritePort(ushort port, byte value) { Execute("ioctl_pio_write", new long[] { port, value }, 0); }
        private void Wait(byte mask, bool set)
        {
            Stopwatch timer = Stopwatch.StartNew();
            byte status = 0;
            do { status = ReadPort(profile.CommandPort); if (((status & mask) != 0) == set) return; Thread.Sleep(1); } while (timer.ElapsedMilliseconds < 250);
            throw new TimeoutException("EC did not respond on ports 0x" + profile.DataPort.ToString("X2") + "/0x" + profile.CommandPort.ToString("X2") + " (status=0x" + status.ToString("X2") + ", mask=0x" + mask.ToString("X2") + ", set=" + set + "); fan control is disabled.");
        }

        private byte ReadRegister(byte address)
        {
            Wait(3, false); WritePort(profile.CommandPort, 0x80);
            Wait(2, false); WritePort(profile.DataPort, address);
            // ThinkPad transactions wait for IBF to clear, as in TPFanControl.
            Wait(2, false); return ReadPort(profile.DataPort);
        }

        private void WriteRegister(byte address, byte value)
        {
            if (address != profile.FanRegister) throw new ArgumentException("Only the profile's fan register may be written.");
            Wait(3, false); WritePort(profile.CommandPort, 0x81);
            Wait(2, false); WritePort(profile.DataPort, address);
            Wait(2, false); WritePort(profile.DataPort, value); Wait(2, false);
        }

        public EcReading Read()
        {
            return Transaction(delegate
            {
                EcReading reading = new EcReading();
                for (int address = profile.CpuTemperatureRegister; address <= profile.LastTemperatureRegister; address++)
                {
                    byte measured = ReadRegister((byte)address);
                    if (measured >= 5 && measured <= 125)
                    { reading.Temperatures[address] = measured; reading.Temperature = Math.Max(reading.Temperature, measured); if (address == profile.CpuTemperatureRegister) reading.CpuTemperature = measured; }
                }
                if (reading.Temperatures.Count == 0) throw new IOException("The EC returned no valid temperature; direct control is disabled.");
                // The low RPM byte must be read before the high byte (ThinkPad EC latches the pair).
                int low = ReadRegister(profile.RpmLowRegister), high = ReadRegister(profile.RpmHighRegister), rpm = low | (high << 8);
                if (rpm != 65535 && rpm < 20000) reading.Rpm = rpm;
                reading.Raw = ReadRegister(profile.FanRegister);
                ValidateReading(reading, profile);
                return reading;
            });
        }

        internal static void ValidateReading(EcReading reading, EcProfile profile = null)
        {
            byte cpuRegister = (profile ?? EcProfiles.L14).CpuTemperatureRegister;
            if (!reading.CpuTemperature.HasValue || !reading.Temperatures.ContainsKey(cpuRegister))
                throw new IOException("CPU temperature at EC 0x" + cpuRegister.ToString("X2") + " is unavailable; battery temperature cannot replace it and direct control is disabled.");
            if ((reading.Raw & 0x38) != 0 || (reading.Raw & 0xC0) == 0xC0)
                throw new IOException("The fan mode register does not match the ThinkPad protocol; writes are disabled.");
        }

        public void WriteFan(byte value)
        {
            if (value != 0x40 && value != 0x80 && (value < 1 || value > 7)) throw new ArgumentOutOfRangeException("value");
            Transaction(delegate
            {
                WriteRegister(profile.FanRegister, value);
                byte actual = ReadRegister(profile.FanRegister);
                if (!Matches(actual, value)) throw new IOException("The EC did not confirm fan command 0x" + value.ToString("X2") + "; read back 0x" + actual.ToString("X2") + ".");
                return true;
            });
        }

        internal static bool Matches(byte actual, byte requested)
        { return (actual & 0x38) == 0 && (requested == 0x80 ? (actual & 0xC0) == 0x80 : requested == 0x40 ? (actual & 0xC0) == 0x40 : actual == requested); }
        public void Dispose() { handle.Dispose(); access.Dispose(); thinkpadAccess.Dispose(); }
    }
}
