using System;

namespace OnlyFansControl
{
    internal enum FanMode { Bios, Custom, Max, Smart }
    internal sealed class FanController : IDisposable
    {
        private readonly IEcBackend ec;
        private FanConfig config;
        private bool ownsControl;
        private int smartLevel;
        private byte? written;
        private bool emergency;
        internal FanMode Mode { get; private set; }
        internal int CustomLevel { get; private set; }
        internal EcReading Latest { get; private set; }
        internal string Status { get; private set; }
        internal Action<bool> OwnershipChanged;
        internal Action EmergencyRestore;
        internal bool IsActive { get { return ownsControl; } }

        internal FanController(IEcBackend backend, FanConfig settings)
        {
            ec = backend; config = settings; Mode = FanMode.Bios; CustomLevel = 3;
            Latest = ec.Read(); Status = "EC connected · " + RawName(Latest.Raw);
        }

        internal void Configure(FanConfig settings)
        {
            settings.Validate(); config = settings; smartLevel = 0; written = null;
            if (Mode == FanMode.Smart) Tick();
        }

        internal void Select(FanMode mode, int level)
        {
            if (level < 1 || level > 8) throw new ArgumentOutOfRangeException("level");
            try
            {
                if (mode == FanMode.Bios) { ec.WriteFan(0x80); Disarm(); Mode = FanMode.Bios; Status = "BIOS default · Lenovo controls the fan"; return; }
                Latest = ec.Read();
                Mode = mode; CustomLevel = level; smartLevel = 0; written = null;
                // Arm the independent process before any custom register write.
                ownsControl = true; if (OwnershipChanged != null) OwnershipChanged(true);
                Step();
            }
            catch { FailSafe(); throw; }
        }

        internal EcReading Tick()
        {
            try
            {
                if (Mode == FanMode.Bios && ownsControl) Release();
                Latest = ec.Read();
                if (ownsControl && written.HasValue && !EcBackend.Matches(Latest.Raw, written.Value))
                { FailSafe(); Status = "Firmware changed the mode · BIOS control restored"; }
                else if (ownsControl) Step();
                else Status = "Monitoring only · EC " + RawName(Latest.Raw);
                return Latest;
            }
            catch (Exception error) { FailSafe(); Status = "EC sensor error · " + error.Message; throw; }
        }

        private void Step()
        {
            int temperature = Latest.Temperature;
            if (temperature >= config.critical_temperature_c) emergency = true;
            else if (temperature < config.critical_temperature_c - config.hysteresis_c) emergency = false;
            byte target;
            if (emergency || Mode == FanMode.Max || (Mode == FanMode.Custom && CustomLevel == 8)) target = 0x40;
            else if (Mode == FanMode.Custom) target = (byte)CustomLevel;
            else
            {
                int desired = config.LevelAt(temperature);
                if (smartLevel == 0 || desired >= smartLevel) smartLevel = desired;
                else if (temperature < config.ThresholdFor(smartLevel) - config.hysteresis_c) smartLevel = desired;
                target = smartLevel == 8 ? (byte)0x40 : (byte)smartLevel;
            }
            if (!written.HasValue || written.Value != target) { ec.WriteFan(target); written = target; }
            Status = emergency ? "Thermal protection · Max at " + temperature + " °C"
                : (Mode == FanMode.Smart ? "Smart auto" : Mode == FanMode.Max ? "Max" : "Custom") + " · " + RawName(target) + " · applied immediately";
        }

        internal void Release()
        {
            if (!ownsControl) return;
            ec.WriteFan(0x80); Disarm(); Mode = FanMode.Bios; Status = "Fan control restored to BIOS";
        }

        private void Disarm()
        { ownsControl = false; written = null; emergency = false; if (OwnershipChanged != null) OwnershipChanged(false); }
        private void FailSafe()
        {
            Mode = FanMode.Bios;
            try { Release(); }
            catch (Exception error) { if (EmergencyRestore != null) EmergencyRestore(); Log.Write("BIOS restore failed; watchdog remains armed: " + error); Status = "BIOS restore not confirmed · watchdog is retrying"; }
        }
        internal static string RawName(byte value)
        { return (value & 0x80) != 0 ? "BIOS default" : (value & 0x40) != 0 ? "Max (0x40)" : "level " + (value & 7); }
        public void Dispose() { try { Release(); } finally { ec.Dispose(); } }
    }
}
