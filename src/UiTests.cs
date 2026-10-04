using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OnlyFansControl
{
    internal sealed partial class MainWindow
    {
        internal bool VerificationSucceeded;
        private sealed class UiReport
        {
            public bool success, complete, real_hardware = true, bios_restored_on_exit, original_config_restored;
            public string started_utc = DateTime.UtcNow.ToString("o"), finished_utc, error, stage, executable_sha256 = HardwareTests.ExecutableHash();
            public List<string> checks = new List<string>();
            public List<object> samples = new List<object>();
        }

        private async Task WaitForUiAsync(Func<bool> condition, string description)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.ElapsedMilliseconds > 15000) throw new TimeoutException(description);
                await Task.Delay(50);
            }
        }

        internal async Task VerifyRuntimeAsync(string output)
        {
            UiReport report = new UiReport(); byte[] originalConfig = File.ReadAllBytes(FanConfig.FilePath);
            Action persist = delegate { Program.SaveJson(output, report); };
            Action<bool, string> check = delegate(bool condition, string name)
            { if (!condition) throw new InvalidOperationException(name); report.checks.Add(name); persist(); };
            Action<string> record = delegate(string stage)
            {
                report.stage = stage;
                report.samples.Add(new { stage = stage, utc = DateTime.UtcNow.ToString("o"), temperature_c = latest.EcTemperature,
                    rpm = latest.FanRpm, fan_raw = latest.EcFanRaw, mode = controller.Mode.ToString(), status = controller.Status }); persist();
            };
            BeforeShutdown = delegate
            {
                try
                {
                    using (EcBackend ec = new EcBackend(device)) report.bios_restored_on_exit = EcBackend.Matches(ec.Read().Raw, 0x80);
                    if (!report.bios_restored_on_exit) throw new InvalidOperationException("GUI exit failed to restore BIOS fan control.");
                    report.checks.Add("Exit button restored real fan register to BIOS before process shutdown");
                    VerificationSucceeded = report.error == null && report.original_config_restored;
                }
                catch (Exception error) { report.error += "\nExit: " + error; VerificationSucceeded = false; }
                report.success = VerificationSucceeded; report.complete = true; report.finished_utc = DateTime.UtcNow.ToString("o"); report.stage = "complete"; persist();
            };
            Func<Button, FanMode, byte, Task> click = async delegate(Button button, FanMode expectedMode, byte expectedRaw)
            {
                check(button.IsEnabled, "Control enabled: " + button.Name + " " + Convert.ToString(button.Content));
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await WaitForUiAsync(delegate { return !selecting && !busy && controller.Mode == expectedMode && latest.EcFanRaw.HasValue && EcBackend.Matches(latest.EcFanRaw.Value, expectedRaw); }, "GUI mode did not apply " + expectedMode + " / 0x" + expectedRaw.ToString("X2"));
                record("Click " + expectedMode + " / 0x" + expectedRaw.ToString("X2"));
            };
            try
            {
                persist();
                await WaitForUiAsync(delegate { return controller != null && !busy && latest != null && latest.EcTemperature.HasValue; }, "GUI did not connect to real EC: " + connectionStatus);
                check(Monitor.Administrator && device.Supported, "GUI detected exact model with administrator rights");
                check(latest.EcTemperatures.Count > 0, "GUI displays real EC temperature");
                check(((TextBlock)Window.FindName("FanValue")).Text == (latest.FanRpm.HasValue ? latest.FanRpm.Value.ToString("0") : "—"), "GUI displays measured RPM or marks unavailable RPM as unknown");
                check(latest.EcTemperature.Value < 80, "Temperature below 80 C before GUI mode tests");
                for (int i = 0; i < 7; i++) await click(levelButtons[i], FanMode.Custom, (byte)(i + 1));
                await click(levelButtons[7], FanMode.Custom, 0x40);
                await click(Button("MaxButton"), FanMode.Max, 0x40);
                await click(Button("BiosButton"), FanMode.Bios, 0x80);
                int smartLevel = config.LevelAt(latest.EcTemperature.Value);
                await click(Button("SmartButton"), FanMode.Smart, smartLevel == 8 ? (byte)0x40 : (byte)smartLevel);
                FanConfig changed = FanConfig.Load(FanConfig.FilePath, device.Ec); foreach (CurvePoint point in changed.curve) point.level = "7";
                changed.Validate(); Program.SaveJson(FanConfig.FilePath, changed);
                await WaitForUiAsync(delegate { return !busy && configError == null && controller.Mode == FanMode.Smart && latest.EcFanRaw == 7; }, "GUI did not reload JSON fan curve");
                check(latest.EcFanRaw == 7, "Saving JSON changes Smart auto to real level 7 without Apply button"); record("JSON hot reload");
                File.WriteAllText(FanConfig.FilePath, "{");
                await WaitForUiAsync(delegate { return !busy && configError != null && controller.Mode == FanMode.Bios && latest.EcFanRaw.HasValue && EcBackend.Matches(latest.EcFanRaw.Value, 0x80); }, "Invalid JSON did not return control to BIOS");
                check(!Button("SmartButton").IsEnabled, "Invalid JSON returns real EC to BIOS and disables Smart auto"); record("Invalid JSON recovery");
                File.WriteAllBytes(FanConfig.FilePath, originalConfig);
                await WaitForUiAsync(delegate { return !busy && configError == null && Button("SmartButton").IsEnabled; }, "GUI did not recover from restored valid JSON");
                report.original_config_restored = true;
                await click(Button("MaxButton"), FanMode.Max, 0x40);
                Window.Close();
                check(!Window.IsVisible && tray != null && tray.Visible && controller.IsActive, "Closing window hides to tray while real fan control continues");
                using (Process second = Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    await WaitForUiAsync(delegate { return second.HasExited && Window.IsVisible; }, "Second launch did not restore the existing window");
                    check(second.ExitCode == 0, "Second GUI launch exits without starting another controller");
                    check(Window.IsVisible && controller.IsActive, "Second launch restores the window while fan control continues");
                }
                Show(); check(Window.IsVisible, "Tray reopen restores window");
                Preview(Path.ChangeExtension(output, ".png"));
                check(controller.IsActive, "GUI is still controlling fan before explicit exit");
            }
            catch (Exception error) { report.error = error.ToString(); Log.Write("GUI hardware verification: " + error); }
            finally
            {
                try { File.WriteAllBytes(FanConfig.FilePath, originalConfig); report.original_config_restored = true; }
                catch (Exception error) { report.error += "\nConfig restore: " + error; }
                persist(); Button("ExitButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
        }
    }
}
