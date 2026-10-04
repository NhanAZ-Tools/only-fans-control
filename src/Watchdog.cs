using System;
using System.Diagnostics;
using System.Threading;

namespace OnlyFansControl
{
    internal sealed class WatchdogOwner : IDisposable
    {
        private readonly string token = Guid.NewGuid().ToString("N");
        private readonly EventWaitHandle heartbeat, armed, ready, emergency;
        private readonly Process worker;
        private bool disposed;

        internal WatchdogOwner()
        {
            string prefix = "Local\\OnlyFansControl." + token;
            heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".heartbeat");
            armed = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".armed");
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".ready");
            emergency = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".emergency");
            worker = Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                "--watchdog " + Process.GetCurrentProcess().Id + " " + token) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        internal void SetOwned(bool value)
        {
            if (disposed) throw new ObjectDisposedException("watchdog");
            if (value)
            {
                if (worker == null || worker.HasExited || !ready.WaitOne(4000)) throw new InvalidOperationException("The watchdog is not ready; BIOS retains fan control.");
                emergency.Reset(); heartbeat.Set(); armed.Set();
            }
            else { armed.Reset(); emergency.Reset(); }
        }
        internal void Pulse()
        {
            if (disposed || worker == null || worker.HasExited) throw new InvalidOperationException("The watchdog has stopped; restoring BIOS control.");
            heartbeat.Set();
        }
        internal void RequestRestore() { if (!disposed) emergency.Set(); }
        public void Dispose()
        {
            disposed = true; heartbeat.Dispose(); armed.Dispose(); ready.Dispose(); emergency.Dispose(); if (worker != null) worker.Dispose();
            // Do not kill the helper: an armed helper must outlive its parent until BIOS recovery succeeds.
        }

        internal static int Run(int parentId, string token, string simulationOutput = null)
        {
            string prefix = "Local\\OnlyFansControl." + token;
            try
            {
                using (EventWaitHandle heartbeat = EventWaitHandle.OpenExisting(prefix + ".heartbeat"))
                using (EventWaitHandle armed = EventWaitHandle.OpenExisting(prefix + ".armed"))
                using (EventWaitHandle ready = EventWaitHandle.OpenExisting(prefix + ".ready"))
                using (EventWaitHandle emergency = EventWaitHandle.OpenExisting(prefix + ".emergency"))
                using (Process parent = Process.GetProcessById(parentId))
                using (IEcBackend ec = simulationOutput == null ? (IEcBackend)new EcBackend(DeviceProfile.Detect()) : new SimulationBackend(simulationOutput))
                {
                    Stopwatch lastBeat = Stopwatch.StartNew(); ready.Set();
                    bool restored = false;
                    while (true)
                    {
                        if (heartbeat.WaitOne(500)) { lastBeat.Restart(); restored = false; }
                        bool active = armed.WaitOne(0);
                        if (!active) { if (parent.HasExited) return 0; lastBeat.Restart(); continue; }
                        if (!restored && (parent.HasExited || lastBeat.ElapsedMilliseconds > 6500 || emergency.WaitOne(0)))
                        {
                            try { ec.WriteFan(0x80); restored = true; armed.Reset(); emergency.Reset(); if (simulationOutput == null) Log.Write("Watchdog restored BIOS fan control."); }
                            catch (Exception error) { if (simulationOutput == null) Log.Write("Watchdog BIOS restore retry: " + error.Message); }
                        }
                        if (parent.HasExited && restored) return 0;
                    }
                }
            }
            catch (Exception error) { if (simulationOutput == null) Log.Write("Watchdog startup failure: " + error); return 1; }
        }

        private sealed class SimulationBackend : IEcBackend
        {
            private readonly string path;
            internal SimulationBackend(string output) { path = output; }
            public EcReading Read() { return new EcReading { Temperature = 45, Raw = 0x80 }; }
            public void WriteFan(byte value) { System.IO.File.WriteAllText(path, "0x" + value.ToString("X2")); }
            public void Dispose() { }
        }
    }
}
