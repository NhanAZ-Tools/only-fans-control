using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace OnlyFansControl
{
    internal static class SelfTests
    {
        private static int count;
        private static readonly List<string> results = new List<string>();
        private static void Assert(bool condition, string name)
        { if (!condition) throw new Exception("FAIL: " + name); count++; results.Add(name); }

        internal static object Run()
        {
            count = 0; results.Clear(); FanConfig config = FanConfig.Load(FanConfig.FilePath);
            FakeEc fake = new FakeEc(); FanController controller = new FanController(fake, config);
            Assert(fake.Writes.Count == 0, "Startup does not write EC registers");
            for (int level = 1; level <= 7; level++) { controller.Select(FanMode.Custom, level); Assert(fake.Last == level, "Custom " + level + " applies immediately"); }
            controller.Select(FanMode.Custom, 8); Assert(fake.Last == 0x40, "Custom Max writes exact 0x40");
            controller.Select(FanMode.Max, 3); Assert(fake.Last == 0x40, "Max writes exact 0x40");
            controller.Select(FanMode.Bios, 3); Assert(fake.Last == 0x80 && !controller.IsActive, "BIOS default writes 0x80 and releases ownership");
            bool rejected = false; try { controller.Select(FanMode.Custom, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Fan-off level 0 is rejected");
            fake.Temperature = 60; controller.Select(FanMode.Smart, 3); Assert(fake.Last == 3, "Smart auto selects curve boundary immediately");
            fake.Temperature = 67; controller.Tick(); Assert(fake.Last == 4, "Smart auto raises fan at 67 C");
            int writes = fake.Writes.Count; fake.Temperature = 65; controller.Tick(); Assert(fake.Last == 4 && fake.Writes.Count == writes, "Hysteresis holds level during small cooling change");
            fake.Temperature = 63; controller.Tick(); Assert(fake.Last == 3, "Hysteresis permits lower level below threshold");
            fake.Temperature = 92; fake.Rpm = null; controller.Select(FanMode.Custom, 1); Assert(fake.Last == 0x40, "Critical heat overrides manual low level with Max even when RPM is temporarily unavailable"); fake.Rpm = 2500;
            fake.Temperature = 88; controller.Tick(); Assert(fake.Last == 0x40, "Critical heat hysteresis holds Max");
            fake.Temperature = 86; controller.Tick(); Assert(fake.Last == 1, "Manual level resumes after safe cooling");
            fake.Raw = 0x80; controller.Tick(); Assert(controller.Mode == FanMode.Bios && !controller.IsActive, "External firmware change returns control to BIOS");
            fake.Temperature = 45; controller.Select(FanMode.Custom, 2); fake.FailRead = true;
            try { controller.Tick(); } catch (IOException) { }
            Assert(fake.Last == 0x80 && !controller.IsActive, "Sensor loss immediately attempts BIOS recovery");
            controller.Select(FanMode.Bios, 3); Assert(fake.Last == 0x80, "BIOS default works even when temperature reading fails");
            fake.FailRead = false; controller.Select(FanMode.Custom, 2); fake.FailRead = true; fake.FailBios = true;
            int emergency = 0; controller.EmergencyRestore = delegate { emergency++; };
            try { controller.Tick(); } catch (IOException) { }
            Assert(controller.IsActive && controller.Mode == FanMode.Bios && emergency > 0, "Failed BIOS recovery keeps watchdog armed and signals emergency");
            fake.FailRead = false; fake.FailBios = false; controller.Tick(); Assert(!controller.IsActive && fake.Last == 0x80, "Failed BIOS restore is retried before any manual command");
            fake.Temperature = 58; controller.Select(FanMode.Smart, 3);
            FanConfig newConfig = FanConfig.Load(FanConfig.FilePath); newConfig.curve[1].level = "3"; controller.Configure(newConfig);
            Assert(fake.Last == 3, "Config reload changes Smart auto immediately");
            controller.Dispose(); Assert(fake.Last == 0x80 && fake.Disposed, "Normal dispose restores BIOS before closing backend");
            Assert(EcBackend.Matches(0x83, 0x80), "BIOS readback permits level bits");
            Assert(EcBackend.Matches(0x45, 0x40), "Max readback permits level bits");
            Assert(!EcBackend.Matches(0xC0, 0x80) && !EcBackend.Matches(0xC0, 0x40), "Conflicting firmware mode flags are rejected");
            Assert(!EcBackend.Matches(3, 4), "Manual write readback mismatch is rejected");
            EcReading validation = new EcReading { Temperature = 45, CpuTemperature = 45, Rpm = 0, Raw = 0x80 };
            validation.Temperatures[0x78] = 45; EcBackend.ValidateReading(validation);
            Assert(true, "BIOS fan stopped at zero RPM is a valid reading");
            validation.CpuTemperature = null; validation.Temperatures.Remove(0x78); validation.Temperatures[0x7F] = 20;
            rejected = false; try { EcBackend.ValidateReading(validation); } catch (IOException) { rejected = true; }
            Assert(rejected, "Battery temperature cannot substitute for a missing CPU sensor");
            validation.CpuTemperature = 45; validation.Temperatures[0x78] = 45; validation.Rpm = null;
            EcBackend.ValidateReading(validation);
            Assert(true, "Temporary RPM unavailability does not suppress valid CPU monitoring");
            validation.Rpm = 1000; validation.Raw = 0xC0;
            rejected = false; try { EcBackend.ValidateReading(validation); } catch (IOException) { rejected = true; }
            Assert(rejected, "Incompatible EC mode flags prevent direct control");
            newConfig = FanConfig.Load(FanConfig.FilePath); newConfig.curve[1].level = "0";
            rejected = false; try { newConfig.Validate(); } catch (ArgumentException) { rejected = true; } Assert(rejected, "JSON level 0 rejected");
            newConfig = FanConfig.Load(FanConfig.FilePath); newConfig.curve[1].temperature_c = 61;
            rejected = false; try { newConfig.Validate(); } catch (ArgumentException) { rejected = true; } Assert(rejected, "Unsorted JSON temperature curve rejected");
            newConfig = FanConfig.Load(FanConfig.FilePath); newConfig.critical_temperature_c = 110;
            rejected = false; try { newConfig.Validate(); } catch (ArgumentException) { rejected = true; } Assert(rejected, "Unsafe critical-temperature configuration rejected");
            rejected = false; try { new EcBackend(new DeviceProfile()); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Wrong model is rejected before opening PawnIO");
            TestProfiles(config);
            TestSingleInstance();
            TestWatchdog("parent-death"); TestWatchdog("heartbeat-timeout"); TestWatchdog("disarmed-exit");
            return new { success = true, passed = count, hardware_register_writes = 0, backend = "Simulated EC", checks = results };
        }

        private static void TestProfiles(FanConfig config)
        {
            DeviceProfile l14 = DeviceProfile.FromIdentity("LENOVO", "21H6S2QQ00", "ThinkPad L14 Gen 4", "AMD Ryzen 5 PRO 7530U with Radeon Graphics");
            DeviceProfile t495 = DeviceProfile.FromIdentity("LENOVO", "20NKS02N00", "ThinkPad T495", "AMD Ryzen 5 PRO 3500U with Radeon Vega Mobile Gfx");
            Assert(l14.Supported && l14.Ec == EcProfiles.L14, "L14 identity selects its EC profile");
            Assert(t495.Supported && t495.Ec == EcProfiles.T495, "T495 identity selects its EC profile");
            Assert(!DeviceProfile.FromIdentity("Other", t495.Model, t495.Product, t495.Cpu).Supported, "Matching model under another manufacturer is rejected");
            Assert(!DeviceProfile.FromIdentity("LENOVO", "20NKS02N00", "ThinkPad T495s", t495.Cpu).Supported, "Similar marketing names do not select the T495 profile");
            Assert(!DeviceProfile.FromIdentity("LENOVO", "20NKS02N00", "ThinkPad T495", "Intel Core i5").Supported, "T495 identity with an incompatible CPU is rejected");
            Assert(!DeviceProfile.FromIdentity("LENOVO", "21H6S2QQ00", "ThinkPad L14 Gen 4", "AMD Ryzen 7 PRO 7730U").Supported, "Unlisted L14 CPU variant stays outside the profile");
            l14.Ec = EcProfiles.T495; bool rejected = false;
            try { new EcBackend(l14); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Assigning another model's profile cannot bypass the backend guard");
            FanConfig t495Settings = FanConfig.Load(FanConfig.FilePath, EcProfiles.T495);
            bool hasT495Curve = config.profile_curves != null && config.profile_curves.ContainsKey(EcProfiles.T495.Id);
            Assert(t495Settings.LevelAt(60) == (hasT495Curve ? 1 : config.LevelAt(60)), "Configuration selects the T495 curve or the shared fallback");
            FakeEc fake = new FakeEc { Temperature = 68 };
            using (FanController controller = new FanController(fake, t495Settings))
            {
                controller.Select(FanMode.Smart, 3);
                Assert(fake.Last == t495Settings.LevelAt(68), "T495 Smart auto uses the selected profile curve");
            }
            Assert(fake.Last == 0x80, "T495 simulated controller releases control to BIOS");
            FanConfig invalid = FanConfig.Load(FanConfig.FilePath);
            invalid.profile_curves = new Dictionary<string, List<CurvePoint>> { { EcProfiles.T495.Id, new List<CurvePoint> { new CurvePoint { temperature_c = 0, level = "0" }, new CurvePoint { temperature_c = 50, level = "2" } } } };
            rejected = false; try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Unsafe level in another profile's curve is rejected");
            invalid.profile_curves = new Dictionary<string, List<CurvePoint>> { { "misspelled-profile", config.curve } };
            rejected = false; try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Unknown profile curve keys are rejected");
            FanConfig shared = FanConfig.Load(FanConfig.FilePath); shared.profile_curves = null;
            FanConfig fallback = FanConfig.Parse(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(shared), EcProfiles.T495);
            Assert(fallback.LevelAt(60) == shared.LevelAt(60), "Existing shared-curve configuration remains usable on T495");
            FanConfig earlier = FanConfig.Parse("{\"fan_policy\":{\"poll_ms\":2000,\"hysteresis_c\":3,\"failsafe_temp_c\":92,\"smart_curve\":[{\"temp_c\":0,\"level\":1},{\"temp_c\":62,\"level\":2},{\"temp_c\":86,\"level\":7}]}}", EcProfiles.T495);
            Assert(earlier.schema_version == 1 && earlier.LevelAt(70) == 2 && earlier.critical_temperature_c == 92, "Earlier T495 fan_policy configuration preserves curve and protection settings");
        }

        private static void TestSingleInstance()
        {
            string prefix = "Local\\OnlyFansControl.Test." + Guid.NewGuid().ToString("N");
            using (SingleInstance primary = new SingleInstance(prefix))
            {
                using (SingleInstance secondary = new SingleInstance(prefix))
                {
                    Assert(primary.IsPrimary && !secondary.IsPrimary, "Second launch does not create another controller");
                    Assert(primary.TakeActivation() && !primary.TakeActivation(), "Second launch requests the existing window exactly once");
                }
            }
            using (SingleInstance replacement = new SingleInstance(prefix)) Assert(replacement.IsPrimary, "A new instance can start after the previous instance exits");
        }

        private static void TestWatchdog(string scenario)
        {
            string token = Guid.NewGuid().ToString("N"), prefix = "Local\\OnlyFansControl." + token;
            string output = Path.Combine(Path.GetTempPath(), "only-fans-watchdog-" + token + ".txt");
            Process parent = null, helper = null;
            using (EventWaitHandle heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".heartbeat"))
            using (EventWaitHandle armed = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".armed"))
            using (EventWaitHandle ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".ready"))
            using (EventWaitHandle emergency = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".emergency"))
            try
            {
                parent = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                helper = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                    "--watchdog-test " + parent.Id + " " + token + " \"" + output + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                if (!ready.WaitOne(4000)) throw new Exception("Simulated watchdog did not become ready");
                if (scenario != "disarmed-exit") { heartbeat.Set(); armed.Set(); }
                if (scenario != "heartbeat-timeout") parent.Kill();
                Stopwatch deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 9000 && !File.Exists(output) && !helper.HasExited) Thread.Sleep(100);
                if (scenario == "disarmed-exit") Assert(helper.HasExited && !File.Exists(output), "Watchdog exits without EC writes when disarmed");
                else Assert(File.Exists(output) && File.ReadAllText(output) == "0x80", "Independent watchdog restores BIOS on " + scenario);
            }
            finally
            {
                if (parent != null) { if (!parent.HasExited) parent.Kill(); parent.Dispose(); }
                if (helper != null) { if (!helper.WaitForExit(1500)) helper.Kill(); helper.Dispose(); }
                if (File.Exists(output)) File.Delete(output);
            }
        }

        private sealed class FakeEc : IEcBackend
        {
            internal int Temperature = 45; internal byte Raw = 0x80; internal bool FailRead, FailBios, Disposed;
            internal int? Rpm = 2500;
            internal readonly List<byte> Writes = new List<byte>(); internal byte Last { get { return Writes[Writes.Count - 1]; } }
            public EcReading Read()
            {
                if (FailRead) throw new IOException("Simulated sensor failure");
                return new EcReading { Temperature = Temperature, CpuTemperature = Temperature, Rpm = Rpm, Raw = Raw,
                    Temperatures = new Dictionary<int, int> { { 0x78, Temperature } } };
            }
            public void WriteFan(byte value) { if (value == 0x80 && FailBios) throw new IOException("Simulated BIOS write failure"); Writes.Add(value); Raw = value; }
            public void Dispose() { Disposed = true; }
        }
    }
}
