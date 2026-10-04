using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;

[assembly: AssemblyTitle("Only Fans Control")]
[assembly: AssemblyDescription("Windows EC fan monitor and control for selected ThinkPad models")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace OnlyFansControl
{
    internal static class Program
    {
        private static SingleInstance instance;
        private static bool ownsInstance;
        [STAThread]
        private static int Main(string[] args)
        {
            string output = args.Length > 1 ? args[1] : null;
            try
            {
                CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                if (args.Length == 3 && args[0] == "--watchdog") return WatchdogOwner.Run(int.Parse(args[1]), args[2]);
                if (args.Length == 4 && args[0] == "--watchdog-test") return WatchdogOwner.Run(int.Parse(args[1]), args[2], args[3]);
                if (args.Length == 2 && args[0] == "--self-test") { SaveJson(output, SelfTests.Run()); return 0; }
                if (args.Length == 2 && args[0] == "--hardware-test")
                {
                    AcquireSingleInstance();
                    if (!ownsInstance) throw new InvalidOperationException("Close the app before running hardware verification.");
                    return HardwareTests.Run(output) ? 0 : 1;
                }
                if (args.Length == 2 && args[0] == "--prepare-crash-test")
                {
                    AcquireSingleInstance();
                    if (!ownsInstance) throw new InvalidOperationException("Close the app before running watchdog verification.");
                    HardwareTests.RunCrashPreparation(output); return 0;
                }
                if (args.Length == 2 && (args[0] == "--diagnose" || args[0] == "--probe-ec"))
                {
                    bool probe = args[0] == "--probe-ec";
                    DiagnosticReport report = Diagnose(probe); SaveJson(output, report);
                    return report.success ? 0 : 1;
                }
                if (args.Length == 2 && args[0] == "--preview")
                { Application app = new Application(); using (MainWindow window = new MainWindow(DeviceProfile.Detect(), false)) window.Preview(output); app.Shutdown(); return 0; }
                bool uiTest = args.Length == 2 && args[0] == "--ui-test";
                bool startup = args.Length == 1 && args[0] == "--startup";
                if (args.Length > 0 && !uiTest && !startup) throw new ArgumentException("Invalid arguments.");
                AcquireSingleInstance();
                if (!ownsInstance) return 0;
                Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using (MainWindow window = new MainWindow(DeviceProfile.Detect(), true))
                {
                    application.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                    { Log.Write(e.Exception.ToString()); e.Handled = true; window.End(); };
                    if (uiTest) window.Window.Dispatcher.BeginInvoke(new Action(async delegate { await window.VerifyRuntimeAsync(output); }));
                    if (!startup || uiTest) window.Show(); application.Run();
                    if (uiTest) return window.VerificationSucceeded ? 0 : 1;
                }
                return 0;
            }
            catch (Exception error)
            {
                Log.Write(error.ToString());
                if (output != null && args.Length == 2) { try { SaveJson(output, new { success = false, error = error.ToString() }); } catch { } }
                else MessageBox.Show(error.Message, "Only Fans Control", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            finally { ReleaseSingleInstance(); }
        }

        private static void AcquireSingleInstance()
        {
            instance = new SingleInstance(); ownsInstance = instance.IsPrimary;
        }
        internal static bool TakeActivation() { return instance != null && instance.TakeActivation(); }
        private static void ReleaseSingleInstance()
        { if (instance != null) { instance.Dispose(); instance = null; ownsInstance = false; } }

        internal static bool RestartElevated()
        {
            ReleaseSingleInstance();
            try { Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location) { Verb = "runas", UseShellExecute = true }); return true; }
            catch (Exception error) { AcquireSingleInstance(); Log.Write("Elevation canceled: " + error.Message); return false; }
        }

        internal static void SaveJson(string path, object value)
        { File.WriteAllText(Path.GetFullPath(path), new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Serialize(value), new System.Text.UTF8Encoding(false)); }

        internal sealed class DiagnosticReport
        {
            public bool success, profile_match, administrator, pawnio_installed, ec_probe_requested, ec_probe_success;
            public int ec_register_writes;
            public string product, model, cpu, bios, ec_error, profile_id;
            public object ec;
            public Sample sample;
        }

        private static DiagnosticReport Diagnose(bool probeEc)
        {
            DeviceProfile device = DeviceProfile.Detect(); Sample sample;
            using (Monitor monitor = new Monitor()) { monitor.Read(); Thread.Sleep(150); sample = monitor.Read(); }
            string ecError = null; object ecData = null;
            if (probeEc)
            {
                try { using (EcBackend ec = new EcBackend(device)) { EcReading reading = ec.Read(); ecData = new { temperature_c = reading.Temperature, rpm = reading.Rpm, fan_raw = reading.Raw, temperatures = reading.Temperatures.ToDictionary(item => "0x" + item.Key.ToString("X2"), item => item.Value) }; } }
                catch (Exception error) { ecError = error.Message; }
            }
            return new DiagnosticReport { success = !probeEc || ecData != null, product = device.Product, model = device.Model, cpu = device.Cpu, bios = device.Bios, profile_id = device.Ec == null ? null : device.Ec.Id,
                profile_match = device.Supported, administrator = Monitor.Administrator, pawnio_installed = Monitor.PawnInstalled,
                ec_probe_requested = probeEc, ec_probe_success = ecData != null, ec_register_writes = 0, ec = ecData, ec_error = ecError, sample = sample };
        }
    }
}
