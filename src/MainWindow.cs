using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace OnlyFansControl
{
    internal sealed partial class MainWindow : IDisposable
    {
        internal readonly Window Window;
        private readonly DeviceProfile device;
        private readonly Monitor monitor = new Monitor();
        private readonly object workerLock = new object();
        private readonly List<double?> history = new List<double?>();
        private readonly List<Button> levelButtons = new List<Button>();
        private readonly DispatcherTimer timer;
        private FanController controller;
        private WatchdogOwner watchdog;
        private FanConfig config;
        private Forms.NotifyIcon tray;
        private bool busy, exiting, runtime, attemptedConnection, suspended, selecting;
        private int customLevel = 3;
        private DateTime lastRead = DateTime.MinValue, configWrite = DateTime.MinValue;
        private string connectionStatus = "Connecting to EC…", configError, actionError;
        private Sample latest;
        internal Action BeforeShutdown;

        internal MainWindow(DeviceProfile profile, bool startRuntime)
        {
            device = profile; runtime = startRuntime;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            Window.Icon = LucideAssets.ApplicationIcon;
            Text("DeviceName", device.Product);
            Text("DeviceDetail", device.Model + " · " + device.Cpu.Replace("AMD ", "").Replace(" with Radeon Graphics", ""));
            Button("CustomButton").Click += delegate { SelectMode(FanMode.Custom); };
            Button("MaxButton").Click += delegate { SelectMode(FanMode.Max); };
            Button("BiosButton").Click += delegate { SelectMode(FanMode.Bios); };
            Button("SmartButton").Click += delegate { SelectMode(FanMode.Smart); };
            Button("ConfigButton").Click += delegate { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + FanConfig.FilePath + "\"") { UseShellExecute = true }); };
            Button("SensorsButton").Click += delegate { ShowSensors(); };
            Button("TrayButton").Click += delegate { Window.Hide(); };
            Button("ExitButton").Click += delegate { End(); };
            UniformGrid levels = (UniformGrid)Window.FindName("LevelsPanel");
            for (int level = 1; level <= 8; level++)
            {
                int chosen = level;
                Button button = new Button { Content = level == 8 ? "Max" : level.ToString(), Margin = new Thickness(0,0,6,0), Padding = new Thickness(6,8,6,8),
                    ToolTip = "Select Custom " + (level == 8 ? "Max" : level.ToString()) + " and apply immediately" };
                button.Click += delegate { customLevel = chosen; SelectMode(FanMode.Custom); };
                levelButtons.Add(button); levels.Children.Add(button);
            }
            Window.Closing += delegate(object sender, CancelEventArgs e) { if (!exiting && runtime) { e.Cancel = true; Window.Hide(); } };
            Window.StateChanged += delegate { if (Window.WindowState == WindowState.Minimized && runtime) Window.Hide(); };
            Window.SizeChanged += delegate { DrawHistory(); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += delegate { Poll(); };
            LoadConfig(); UpdateControls();
            if (runtime) { CreateTray(); SystemEvents.PowerModeChanged += OnPowerMode; SystemEvents.SessionEnding += OnSessionEnding; timer.Start(); Poll(); }
        }

        private Button Button(string name) { return (Button)Window.FindName(name); }
        private void Text(string name, string value) { ((TextBlock)Window.FindName(name)).Text = value; }
        private Brush ThemeBrush(string key) { return (Brush)Window.Resources[key]; }

        private void LoadConfig()
        {
            try
            {
                config = FanConfig.Load(FanConfig.FilePath, device.Ec); configWrite = File.GetLastWriteTimeUtc(FanConfig.FilePath); configError = null;
            }
            catch (Exception error) { configError = "Configuration error: " + error.Message; Log.Write(configError); }
        }

        private void Connect()
        {
            if (attemptedConnection || suspended) return;
            attemptedConnection = true;
            try
            {
                if (config == null) throw new InvalidOperationException(configError);
                EcBackend backend = new EcBackend(device);
                try { controller = new FanController(backend, config); }
                catch { backend.Dispose(); throw; }
                try
                {
                    watchdog = new WatchdogOwner(); controller.OwnershipChanged = watchdog.SetOwned; controller.EmergencyRestore = watchdog.RequestRestore;
                    connectionStatus = "EC connected · PawnIO Official";
                }
                catch { controller.Dispose(); controller = null; throw; }
            }
            catch (Exception error) { connectionStatus = error.Message; Log.Write("EC connection: " + error); }
        }

        private Sample ReadSample()
        {
            lock (workerLock)
            {
                Connect(); Sample sample = monitor.Read(); sample.SensorStatus = connectionStatus;
                if (controller != null && !suspended)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(FanConfig.FilePath) != configWrite)
                        {
                            LoadConfig();
                            if (configError == null) { controller.Configure(config); connectionStatus = "EC connected · PawnIO Official"; }
                            else { controller.Release(); connectionStatus = configError + " · BIOS control restored"; }
                        }
                        EcReading reading = controller.Tick();
                        sample.EcTemperature = reading.Temperature; sample.CpuTemperature = reading.CpuTemperature;
                        sample.FanRpm = reading.Rpm; sample.EcFanRaw = reading.Raw; sample.EcTemperatures = reading.Temperatures.ToDictionary(item => "0x" + item.Key.ToString("X2"), item => item.Value);
                        sample.TemperatureSource = "ThinkPad EC · highest reading from 0x78–0x7F"; sample.FanSource = "ThinkPad EC · 0x84/0x85";
                        sample.SensorStatus = configError ?? connectionStatus;
                        if (watchdog != null) watchdog.Pulse();
                    }
                    catch (Exception error)
                    {
                        sample.SensorStatus = "EC error · " + error.Message; Log.Write(sample.SensorStatus);
                        try { controller.Release(); } catch (Exception restoreError) { if (watchdog != null) watchdog.RequestRestore(); Log.Write("Read recovery: " + restoreError); }
                    }
                }
                return sample;
            }
        }

        private async void Poll()
        {
            if (Program.TakeActivation()) Show();
            if (busy || selecting || suspended)
            {
                if ((DateTime.Now - lastRead).TotalSeconds > 6 && latest != null) ShowStale(); return;
            }
            busy = true;
            try { Sample sample = await Task.Run(delegate { return ReadSample(); }); if (!exiting) { RenderSample(sample); UpdateControls(); } }
            catch (Exception error) { Log.Write(error.ToString()); if (!exiting) { ShowStale(); Text("SensorStatus", "Unable to read data · see logs"); } }
            finally { busy = false; }
        }

        private async void SelectMode(FanMode mode)
        {
            if (selecting || exiting || suspended) return;
            selecting = true; UpdateControls();
            try
            {
                await Task.Run(delegate
                {
                    lock (workerLock)
                    {
                        if (controller == null) throw new InvalidOperationException(connectionStatus);
                        if (mode == FanMode.Smart && configError != null) throw new InvalidOperationException(configError);
                        controller.Select(mode, customLevel); if (watchdog != null) watchdog.Pulse();
                    }
                });
                actionError = null;
            }
            catch (Exception error) { actionError = "Unable to apply: " + error.Message; Log.Write("Select mode: " + error); }
            finally { selecting = false; UpdateControls(); Poll(); }
        }

        internal void RenderSample(Sample sample)
        {
            latest = sample; lastRead = DateTime.Now;
            Text("TemperatureValue", sample.EcTemperature.HasValue ? sample.EcTemperature.Value + " °C" : "—");
            Text("TemperatureHint", sample.EcTemperature.HasValue ? "Highest · " + sample.EcTemperatures.Count + " sensors" : "EC not connected");
            ((TextBlock)Window.FindName("TemperatureValue")).ToolTip = sample.TemperatureSource;
            Text("FanValue", sample.FanRpm.HasValue ? sample.FanRpm.Value.ToString("0") : "—");
            Text("FanHint", sample.FanRpm.HasValue ? "RPM · measured by EC" : "RPM unavailable");
            Text("LoadValue", sample.CpuLoad.HasValue ? sample.CpuLoad.Value.ToString("0") + " %" : "—");
            Text("MemoryHint", sample.MemoryLoad.HasValue ? "RAM usage " + sample.MemoryLoad.Value.ToString("0") + " %" : "");
            Text("BatteryLabel", (sample.Battery.HasValue ? sample.Battery.Value + "% · " : "") + (sample.OnAC.HasValue ? (sample.OnAC.Value ? "Plugged in" : "On battery") : "Power source unknown"));
            ((LucideIcon)Window.FindName("PowerIcon")).Icon = sample.OnAC == true ? "plug" : "battery";
            Text("SensorStatus", sample.SensorStatus); Text("UpdateLabel", "Updated " + sample.Time.ToString("HH:mm:ss"));
            Text("PowerSource", sample.EcFanRaw.HasValue ? "EC · 0x" + sample.EcFanRaw.Value.ToString("X2") : "EC not connected");
            history.Add(sample.CpuLoad); if (history.Count > 60) history.RemoveAt(0); DrawHistory();
            if (tray != null) tray.Text = "Only Fans · " + (sample.EcTemperature.HasValue ? sample.EcTemperature.Value + "°C · " : "")
                + (sample.FanRpm.HasValue ? sample.FanRpm.Value.ToString("0") + " RPM" : "EC not connected");
        }

        private void ShowStale()
        {
            Text("TemperatureValue", "—"); Text("FanValue", "—"); Text("LoadValue", "—");
            Text("TemperatureHint", "Data is stale"); Text("FanHint", "Data is stale"); Text("UpdateLabel", "Sensor connection lost");
        }

        private void UpdateControls()
        {
            if (config != null) timer.Interval = TimeSpan.FromMilliseconds(config.poll_interval_ms);
            bool available = controller != null && !selecting && !suspended && !exiting;
            FanMode mode = controller == null ? FanMode.Bios : controller.Mode;
            StyleButton(Button("CustomButton"), available && mode == FanMode.Custom, available);
            StyleButton(Button("MaxButton"), available && mode == FanMode.Max, available);
            StyleButton(Button("BiosButton"), available && mode == FanMode.Bios && controller.Latest != null && (controller.Latest.Raw & 0x80) != 0, available);
            StyleButton(Button("SmartButton"), available && mode == FanMode.Smart, available && configError == null);
            for (int i = 0; i < levelButtons.Count; i++) StyleButton(levelButtons[i], available && mode == FanMode.Custom && customLevel == i + 1, available);
            string status = controller == null ? connectionStatus : controller.Status;
            if (actionError != null) status = actionError;
            if (selecting) status = "Sending command to EC…";
            if (suspended) status = "Suspended · restoring BIOS control";
            Text("ModeStatus", status);
            ((TextBlock)Window.FindName("ModeStatus")).Foreground = ThemeBrush(controller == null || configError != null || actionError != null ? "ErrorBrush" : "AccentBrush");
        }

        private void StyleButton(Button button, bool selected, bool enabled)
        {
            button.IsEnabled = enabled; button.Background = ThemeBrush(selected ? "AccentSoftBrush" : "SurfaceBrush");
            button.Foreground = ThemeBrush(selected ? "AccentTextBrush" : "TextBrush"); button.BorderBrush = ThemeBrush(selected ? "AccentBrush" : "OutlineBrush");
        }

        private void DrawHistory()
        {
            Canvas canvas = (Canvas)Window.FindName("HistoryCanvas"); canvas.Children.Clear(); double width = canvas.ActualWidth; if (width < 1) return;
            foreach (double y in new[] { 0.0, 28.0, 56.0 }) canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = ThemeBrush("ChartGridBrush"), StrokeThickness = 1 });
            Polyline line = null;
            for (int i = 0; i < history.Count; i++)
            {
                if (!history[i].HasValue) { line = null; continue; }
                if (line == null) { line = new Polyline { Stroke = ThemeBrush("AccentBrush"), StrokeThickness = 2 }; canvas.Children.Add(line); }
                line.Points.Add(new Point(width * (60 - history.Count + i) / 59, 56 * (1 - history[i].Value / 100)));
            }
        }

        private void ShowSensors()
        {
            string details = "Device: " + device.Product + " / " + device.Model + "\nCPU: " + device.Cpu + "\nBIOS: " + device.Bios
                + "\nProfile: " + (device.Ec == null ? "No matching profile" : device.Ec.Id)
                + "\n\nAdministrator rights: " + (Monitor.Administrator ? "Yes" : "No") + "\nPawnIO: " + (Monitor.PawnInstalled ? "Installed" : "Not installed")
                + "\n\n" + connectionStatus + "\n\nThe app uses the PawnIO Official driver and signed LpcACPIEC module through ports 0x62/0x66. Fan controls are disabled if the EC does not respond or its readings are invalid."
                + "\n\nCustom: discrete levels 1–7 or Max.\nMax: write 0x40 to EC register 0x2F.\nBIOS default: write 0x80 to restore firmware control.\nSmart auto: use the highest EC temperature and only_fans_config.json."
                + "\n\nAn independent watchdog monitors heartbeats while the app controls the fan. If no heartbeat arrives for over 6.5 seconds or the main process exits, it attempts to restore BIOS control. Hardware communication errors can prevent confirmation of the restore."
                + "\n\nDiscrete levels are not fixed percentages or RPM targets. Adjacent levels may share a speed; Max may run faster than level 7."
                + "\n\n" + (configError ?? "The JSON configuration is valid. Saved changes take effect on the next read.");
            Window dialog = new Window { Title = "EC connection", Owner = Window, Icon = LucideAssets.ApplicationIcon, Width = 580, Height = 595,
                Background = ThemeBrush("AppBackgroundBrush"), Foreground = ThemeBrush("TextBrush"), Resources = Window.Resources, FontFamily = new FontFamily("Segoe UI"),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanMinimize };
            DockPanel panel = new DockPanel { Margin = new Thickness(22) };
            StackPanel links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,14,0,0) }; DockPanel.SetDock(links, Dock.Bottom); panel.Children.Add(links);
            Button pawn = new Button { Content = IconLabel("download", "Install PawnIO"), Padding = new Thickness(10,7,10,7), Margin = new Thickness(0,0,8,0) };
            pawn.Click += delegate { string setup = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "driver", "PawnIO_setup.exe");
                Process.Start(new ProcessStartInfo(File.Exists(setup) ? setup : "https://pawnio.eu") { UseShellExecute = true, Verb = File.Exists(setup) ? "runas" : "" }); }; links.Children.Add(pawn);
            Button retry = new Button { Content = IconLabel(Monitor.Administrator ? "refresh-cw" : "shield", Monitor.Administrator ? "Reconnect" : "Run as administrator"), Padding = new Thickness(10,7,10,7), Margin = new Thickness(0,0,8,0) };
            retry.Click += delegate { dialog.Close(); if (!Monitor.Administrator) { if (Program.RestartElevated()) End(); }
                else { if (controller == null) attemptedConnection = false; Poll(); } }; links.Children.Add(retry);
            Button logs = new Button { Content = IconLabel("folder-open", "View logs"), Padding = new Thickness(10,7,10,7) };
            logs.Click += delegate { Directory.CreateDirectory(Log.Folder); Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true }); }; links.Children.Add(logs);
            panel.Children.Add(new ScrollViewer { Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            dialog.Content = panel; dialog.ShowDialog();
        }

        private void CreateTray()
        {
            tray = new Forms.NotifyIcon { Icon = LucideAssets.TrayIcon(), Text = "Only Fans Control", Visible = true }; tray.DoubleClick += delegate { Show(); };
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open Only Fans Control", LucideAssets.MenuImage("fan"), delegate { Show(); }); menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Custom", LucideAssets.MenuImage("sliders-horizontal"), delegate { SelectMode(FanMode.Custom); }); menu.Items.Add("Max", LucideAssets.MenuImage("zap"), delegate { SelectMode(FanMode.Max); });
            menu.Items.Add("BIOS default", LucideAssets.MenuImage("shield-check"), delegate { SelectMode(FanMode.Bios); }); menu.Items.Add("Smart auto", LucideAssets.MenuImage("activity"), delegate { SelectMode(FanMode.Smart); });
            menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Exit · restore BIOS control", LucideAssets.MenuImage("power"), delegate { End(); }); tray.ContextMenuStrip = menu;
        }

        private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            // Suspend notification can arrive shortly before sleep: attempt restoration synchronously.
            if (e.Mode == PowerModes.Suspend)
            {
                suspended = true; if (watchdog != null) watchdog.RequestRestore();
                try { lock (workerLock) { if (controller != null) controller.Release(); } } catch (Exception error) { Log.Write("Suspend: " + error); }
            }
            else if (e.Mode == PowerModes.Resume) { suspended = false; Window.Dispatcher.BeginInvoke(new Action(Poll)); }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            if (watchdog != null) watchdog.RequestRestore();
            try { lock (workerLock) { if (controller != null) controller.Release(); } } catch (Exception error) { Log.Write("Session ending: " + error); }
        }

        internal void Show() { Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate(); }
        internal async void End()
        {
            if (exiting) return; exiting = true; timer.Stop(); Window.Hide();
            if (watchdog != null) watchdog.RequestRestore();
            await Task.Run(delegate { lock (workerLock) { try { if (controller != null) controller.Dispose(); } catch (Exception error) { Log.Write("Exit BIOS restore: " + error); } controller = null; } });
            if (BeforeShutdown != null) { try { BeforeShutdown(); } catch (Exception error) { Log.Write("Exit verification: " + error); } }
            Window.Close(); Application.Current.Shutdown();
        }

        private static StackPanel IconLabel(string icon, string label)
        {
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new LucideIcon { Icon = icon, Width = 16, Height = 16, Margin = new Thickness(0,0,8,0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); return content;
        }

        internal void Preview(string path)
        {
            Sample sample = ReadSample(); System.Threading.Thread.Sleep(120); sample = ReadSample(); RenderSample(sample); UpdateControls();
            FrameworkElement content = (FrameworkElement)Window.Content; content.Width = 772; content.Height = 742;
            content.Measure(new Size(772,742)); content.Arrange(new Rect(0,0,772,742)); content.UpdateLayout(); DrawHistory(); content.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(772,742,96,96,PixelFormats.Pbgra32);
            DrawingVisual background = new DrawingVisual(); using (DrawingContext drawing = background.RenderOpen()) drawing.DrawRectangle(ThemeBrush("AppBackgroundBrush"), null, new Rect(0,0,772,742));
            bitmap.Render(background); bitmap.Render(content);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (Stream stream = File.Create(path)) encoder.Save(stream);
        }

        public void Dispose()
        {
            timer.Stop(); if (runtime) { SystemEvents.PowerModeChanged -= OnPowerMode; SystemEvents.SessionEnding -= OnSessionEnding; }
            if (tray != null) { tray.Visible = false; foreach (Forms.ToolStripItem item in tray.ContextMenuStrip.Items) if (item.Image != null) item.Image.Dispose(); tray.ContextMenuStrip.Dispose(); tray.Icon.Dispose(); tray.Dispose(); tray = null; }
            if (controller != null) { try { controller.Dispose(); } catch (Exception error) { Log.Write("Dispose restore: " + error); } controller = null; }
            if (watchdog != null) watchdog.Dispose(); monitor.Dispose();
        }
    }
}
