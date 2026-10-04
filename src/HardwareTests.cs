using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace OnlyFansControl
{
    internal static class HardwareTests
    {
        private sealed class Report
        {
            public bool success, complete, bios_restored;
            public string started_utc, finished_utc, product, profile_id, model, bios, executable_sha256, backend = "PawnIO 2.2.0 / LpcACPIEC 0.2.11 / 0x62,0x66", stage, error;
            public int fan_register_write_attempts, confirmed_fan_register_writes;
            public bool brief_custom_tests_due_to_high_temperature;
            public List<string> checks = new List<string>();
            public List<object> samples = new List<object>();
        }

        private sealed class CountingBackend : IEcBackend
        {
            private readonly EcBackend inner;
            private readonly Report report;
            internal CountingBackend(DeviceProfile device, Report value) { inner = new EcBackend(device); report = value; }
            public EcReading Read() { return inner.Read(); }
            public void WriteFan(byte value)
            {
                report.fan_register_write_attempts++;
                inner.WriteFan(value); report.confirmed_fan_register_writes++;
            }
            public void Dispose() { inner.Dispose(); }
        }

        internal static bool Run(string output)
        {
            DeviceProfile device = DeviceProfile.Detect();
            Report report = new Report { started_utc = DateTime.UtcNow.ToString("o"), product = device.Product, profile_id = device.Ec == null ? null : device.Ec.Id, model = device.Model, bios = device.Bios, executable_sha256 = ExecutableHash(), stage = "read-only baseline" };
            CountingBackend ec = null; FanController controller = null; WatchdogOwner watchdog = null;
            Stopwatch elapsed = Stopwatch.StartNew();
            Action persist = delegate { Program.SaveJson(output, report); };
            Action<string, EcReading> sample = delegate(string stage, EcReading reading)
            {
                report.stage = stage;
                report.samples.Add(new { elapsed_ms = elapsed.ElapsedMilliseconds, stage = stage, temperature_c = reading.Temperature,
                    cpu_temperature_c = reading.CpuTemperature, rpm = reading.Rpm, fan_raw = "0x" + reading.Raw.ToString("X2"),
                    temperatures = reading.Temperatures.ToDictionary(item => "0x" + item.Key.ToString("X2"), item => item.Value) });
                persist();
            };
            Action<bool, string> check = delegate(bool condition, string name)
            { if (!condition) throw new InvalidOperationException(name); report.checks.Add(name); persist(); };
            Action<string, int, byte> hold = delegate(string stage, int seconds, byte expected)
            {
                for (int i = 0; i < seconds; i++)
                {
                    Thread.Sleep(1000); EcReading reading = controller.Tick(); watchdog.Pulse(); sample(stage, reading);
                    if (!EcBackend.Matches(ec.Read().Raw, expected)) throw new InvalidOperationException(stage + ": firmware did not retain requested fan mode.");
                }
            };
            try
            {
                check(Monitor.Administrator && device.Supported, "Administrator and matching device profile confirmed");
                ec = new CountingBackend(device, report);
                for (int i = 0; i < 5; i++) { EcReading reading = ec.Read(); sample("baseline", reading); check(reading.CpuTemperature.HasValue, "Baseline CPU temperature sample " + (i + 1) + " available"); Thread.Sleep(1000); }
                check(report.fan_register_write_attempts == 0, "Baseline reads did not write the fan register");
                FanConfig config = FanConfig.Load(FanConfig.FilePath, device.Ec);
                watchdog = new WatchdogOwner();
                controller = new FanController(ec, config);
                controller.OwnershipChanged = watchdog.SetOwned;
                controller.EmergencyRestore = watchdog.RequestRestore;
                controller.Select(FanMode.Max, 3);
                check(EcBackend.Matches(ec.Read().Raw, 0x40), "Max immediately confirmed raw 0x40");
                hold("Max spin-up", 20, 0x40);
                EcReading maxReading = ec.Read();
                check(maxReading.Rpm.HasValue && maxReading.Rpm.Value > 1000, "Max fan spinning with valid measured RPM");
                int coolingSeconds = 0;
                while (ec.Read().Temperature >= 80 && coolingSeconds < 20) { hold("Max cooling before low levels", 1, 0x40); coolingSeconds++; }
                report.brief_custom_tests_due_to_high_temperature = ec.Read().Temperature >= 80;
                check(ec.Read().Temperature < config.critical_temperature_c - config.hysteresis_c, "Temperature below thermal-protection release threshold before Custom tests");
                for (int level = 1; level <= 7; level++)
                {
                    check(ec.Read().Temperature < config.critical_temperature_c, "Temperature allows brief Custom " + level + " command");
                    controller.Select(FanMode.Custom, level);
                    EcReading immediate = ec.Read(); sample("Custom " + level + " immediate", immediate);
                    check(immediate.Raw == level, "Custom " + level + " immediately confirmed raw " + level);
                    if (report.brief_custom_tests_due_to_high_temperature)
                    {
                        Thread.Sleep(250); sample("Custom " + level + " brief readback", ec.Read());
                        controller.Select(FanMode.Max, 3); hold("Max between brief Custom checks", 3, 0x40);
                    }
                    else hold("Custom " + level + " hold", 20, (byte)level);
                }
                controller.Select(FanMode.Custom, 8);
                check(EcBackend.Matches(ec.Read().Raw, 0x40), "Custom Max immediately confirmed raw 0x40");
                hold("Custom Max", 15, 0x40);
                controller.Select(FanMode.Bios, 3);
                check(EcBackend.Matches(ec.Read().Raw, 0x80) && !controller.IsActive, "BIOS default immediately restored firmware ownership");
                sample("BIOS restored", ec.Read());
                controller.Select(FanMode.Smart, 3);
                EcReading smart = ec.Read(); sample("Smart auto immediate", smart);
                byte target = smart.Temperature >= config.critical_temperature_c || config.LevelAt(smart.Temperature) == 8 ? (byte)0x40 : (byte)config.LevelAt(smart.Temperature);
                check(EcBackend.Matches(smart.Raw, target), "Smart auto immediately matched JSON curve using real EC temperature");
                for (int i = 0; i < 15; i++) { Thread.Sleep(1000); sample("Smart auto hold", controller.Tick()); watchdog.Pulse(); check(controller.IsActive, "Smart auto retained control sample " + (i + 1)); }
                controller.Select(FanMode.Max, 3); watchdog.Pulse();
                bool restoredByWatchdog = false;
                for (int i = 0; i < 11; i++)
                {
                    Thread.Sleep(1000); EcReading reading = ec.Read(); sample("Real watchdog heartbeat timeout", reading);
                    if (EcBackend.Matches(reading.Raw, 0x80)) { restoredByWatchdog = true; break; }
                }
                check(restoredByWatchdog, "Independent watchdog restored real EC to BIOS after missing heartbeat");
                controller.Tick();
                check(!controller.IsActive, "Controller released ownership after watchdog recovery");
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); report.success = false; Log.Write("Hardware verification: " + error); }
            finally
            {
                report.stage = "restore BIOS and finish";
                if (ec != null)
                {
                    try
                    {
                        if (controller != null) controller.Select(FanMode.Bios, 3); else ec.WriteFan(0x80);
                        EcReading final = ec.Read(); sample("Final BIOS state", final);
                        report.bios_restored = EcBackend.Matches(final.Raw, 0x80);
                        if (!report.bios_restored) report.success = false;
                    }
                    catch (Exception error) { report.success = false; report.error += "\nBIOS restore: " + error; if (watchdog != null) watchdog.RequestRestore(); }
                    try { if (controller != null) controller.Dispose(); else ec.Dispose(); }
                    catch (Exception error) { report.success = false; report.error += "\nDispose: " + error; }
                }
                if (watchdog != null) watchdog.Dispose();
                report.complete = true; report.finished_utc = DateTime.UtcNow.ToString("o"); persist();
            }
            return report.success;
        }

        internal static string ExecutableHash()
        {
            using (SHA256 hash = SHA256.Create())
            using (Stream stream = File.OpenRead(Assembly.GetExecutingAssembly().Location))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static void RunCrashPreparation(string output)
        {
            DeviceProfile device = DeviceProfile.Detect();
            using (WatchdogOwner watchdog = new WatchdogOwner())
            using (FanController controller = new FanController(new EcBackend(device), FanConfig.Load(FanConfig.FilePath, device.Ec)))
            {
                controller.OwnershipChanged = watchdog.SetOwned; controller.EmergencyRestore = watchdog.RequestRestore;
                controller.Select(FanMode.Max, 3); watchdog.Pulse();
                Program.SaveJson(output, new { ready = true, parent_pid = Process.GetCurrentProcess().Id, executable_sha256 = ExecutableHash(), fan_raw = 0x40 });
                Stopwatch timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 30000) { controller.Tick(); watchdog.Pulse(); Thread.Sleep(500); }
                // A test runner kills only this process while the helper remains alive.
                // If no runner arrives, normal disposal still returns control to BIOS.
            }
        }
    }
}
