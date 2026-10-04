using System;
using System.Threading;

namespace OnlyFansControl
{
    internal sealed class SingleInstance : IDisposable
    {
        private readonly Mutex mutex;
        private readonly EventWaitHandle activation;
        internal readonly bool IsPrimary;

        internal SingleInstance(string prefix = "Local\\NhanAZTools.OnlyFansControl")
        {
            bool created;
            mutex = new Mutex(false, prefix + ".Mutex", out created);
            try
            {
                activation = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Activate");
                IsPrimary = created;
                if (!IsPrimary) activation.Set();
            }
            catch { mutex.Dispose(); throw; }
        }

        internal bool TakeActivation()
        {
            if (!IsPrimary || !activation.WaitOne(0)) return false;
            activation.Reset(); return true;
        }

        public void Dispose() { activation.Dispose(); mutex.Dispose(); }
    }
}
