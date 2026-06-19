using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MozaPlugin
{
    /// <summary>
    /// Holds the Windows multimedia timer resolution at 1 ms while requested
    /// (the "FFB Lag Fix"). The default ~15.6 ms scheduler tick rounds up the
    /// short timed waits in some games' DirectInput force-feedback pacing path,
    /// pushing each frame just over its vsync budget and halving the frame rate
    /// whenever a wheel is connected. Requesting 1 ms restores full pacing.
    ///
    /// <see cref="Apply"/> is idempotent and thread-safe: it issues exactly one
    /// <c>timeBeginPeriod</c> per <c>timeEndPeriod</c> (begin/end must be balanced,
    /// and the request is reference-counted by the OS), so it is safe to call from
    /// the data thread, the poll timer, and the UI thread. The request is released
    /// at idle (no game), when the toggle is turned off, and on plugin shutdown.
    /// </summary>
    internal sealed class TimerResolutionManager : IDisposable
    {
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        private const uint PeriodMs = 1;

        // 0 = released, 1 = raised. Interlocked-guarded so the begin/end pair stays
        // balanced under concurrent callers (project targets x86 — see threading rules).
        private int _active;

        /// <summary>Raise to 1 ms when <paramref name="wanted"/>, release otherwise. Idempotent.</summary>
        public void Apply(bool wanted)
        {
            if (wanted)
            {
                if (Interlocked.CompareExchange(ref _active, 1, 0) == 0)
                {
                    try
                    {
                        timeBeginPeriod(PeriodMs);
                        MozaLog.Info("FFB Lag Fix: timer resolution raised to 1ms");
                    }
                    catch (Exception ex)
                    {
                        // winmm exists under Wine but be defensive; undo the latch on failure.
                        Interlocked.Exchange(ref _active, 0);
                        MozaLog.Warn("FFB Lag Fix: timeBeginPeriod failed: " + ex.Message);
                    }
                }
            }
            else
            {
                if (Interlocked.CompareExchange(ref _active, 0, 1) == 1)
                {
                    try
                    {
                        timeEndPeriod(PeriodMs);
                        MozaLog.Info("FFB Lag Fix: timer resolution released");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn("FFB Lag Fix: timeEndPeriod failed: " + ex.Message);
                    }
                }
            }
        }

        public void Dispose() => Apply(false);
    }
}
