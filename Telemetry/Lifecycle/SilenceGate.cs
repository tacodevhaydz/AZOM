using System;

namespace MozaPlugin.Telemetry.Lifecycle
{
    /// <summary>
    /// Two host-side silence timers that keep <see cref="TelemetrySender"/>
    /// in sync with the wheel's sess=0x09 dashboard-binding interlock:
    ///
    /// <list type="bullet">
    /// <item><b>Stop→Start gate</b> — Every <see cref="MarkStopped"/> must
    /// precede the next reopen by <see cref="StopReopenSilenceMs"/> (~11 s).
    /// Inside that window the wheel ignores host re-opens because its
    /// internal session-0x09 state hasn't timed out yet. Verified
    /// 2026-05-08 wire trace: failing cycles at 8.4 s of silence, working
    /// at 13.9 s. The wheel does not signal when this state is settled, so
    /// the host enforces the wait.</item>
    /// <item><b>Post-switch UI cooldown</b> — <see cref="MarkSwitchEmitted"/>
    /// arms <see cref="IsInSilenceCooldown"/>, which the UI reads to disable
    /// the dashboard combo / Test buttons while a kind=4 + Stop+Start is in
    /// flight. Cooldown length depends on whether hot-renegotiation is
    /// enabled (queried via the constructor callback).</item>
    /// </list>
    ///
    /// The timestamps are <b>per-instance</b> (one gate per
    /// <see cref="TelemetrySender"/>). They were once static "to survive plugin
    /// recycle", but that scope error coupled the two senders that share a bus: a
    /// CM2 Stop overwrote the static so the WHEEL's next reopen waited ~11 s though
    /// the wheel never stopped (the "could not acquire start lock" storms + the
    /// cross-sender double-stalls). Each sender must gate only on its OWN last Stop
    /// — the wheel's sess=0x09 interlock is per-device-session, keyed by dev-id on
    /// the wire. Cross-reload survival for the WHEEL is preserved another way: the
    /// main sender (and hence its gate instance, a readonly field) is REUSED across
    /// plugin recycle via <c>s_persistentTelemetrySender</c>, so its last-Stop
    /// timestamp travels with it. The CM2 sender is recreated each Init and
    /// legitimately starts fresh (its bus-0x14 session close targets its own dev,
    /// not the wheel's 0x17).
    /// </summary>
    internal sealed class SilenceGate
    {
        // ── Constants — also referenced via public getters below ─────────
        public const int StopReopenSilenceMs = 11000;
        public const int HotSwitchCooldownMs = 200;

        // ── State ─────────────────────────────────────────────────────────
        // Per-instance: each sender gates only on its OWN last Stop/switch, so
        // one sender's Stop never stalls the other's reopen on a shared bus.
        // Cross-reload survival rides the reused persistent main-sender instance
        // (see the class summary), not a process-static.
        private long _lastStopUtcTicks;
        private long _lastSwitchEmittedUtcTicks;

        private readonly Func<bool> _isHotRenegotiationEnabled;

        /// <param name="isHotRenegotiationEnabled">Getter for the sender's
        /// <c>EnableHotRenegotiation</c> property. The post-switch UI cooldown
        /// uses a shorter window in hot mode because the wheel tolerates
        /// kind=4 as close as 0 ms; in stop+start mode we hold the cooldown
        /// for the full silence window so the UI doesn't show the dropdown
        /// as enabled while sessions are still settling.</param>
        public SilenceGate(Func<bool> isHotRenegotiationEnabled)
        {
            _isHotRenegotiationEnabled = isHotRenegotiationEnabled
                ?? throw new ArgumentNullException(nameof(isHotRenegotiationEnabled));
        }

        /// <summary>Stamp the Stop→Start gate. Called from
        /// <c>TelemetrySender.Stop</c>. Every Stop must precede the next
        /// reopen by <see cref="StopReopenSilenceMs"/>.</summary>
        public void MarkStopped(long nowUtcTicks) => _lastStopUtcTicks = nowUtcTicks;

        /// <summary>Read the prior Stop timestamp. Returns 0 if no Stop has
        /// happened in this SimHub process — the caller treats that as
        /// "first start, skip the silence wait" because any stale wheel
        /// session has long since timed out from a previous process.</summary>
        public long LastStopUtcTicks => _lastStopUtcTicks;

        /// <summary>Compute how many milliseconds we still need to wait
        /// before reopening. Caller passes the pre-Stop timestamp (captured
        /// BEFORE the internal Stop() inside StartInner overwrites
        /// <see cref="LastStopUtcTicks"/>); returns 0 if the wait is already
        /// satisfied or this is a first-start.</summary>
        public int RemainingStopReopenWaitMs(long preStopUtcTicks)
        {
            if (preStopUtcTicks == 0) return 0;  // first start in process
            long elapsedMs = (DateTime.UtcNow.Ticks - preStopUtcTicks)
                / TimeSpan.TicksPerMillisecond;
            return (int)Math.Max(0, StopReopenSilenceMs - elapsedMs);
        }

        /// <summary>Arm the UI cooldown after a kind=4 dashboard-switch
        /// frame has gone out on the wire.</summary>
        public void MarkSwitchEmitted(long nowUtcTicks) => _lastSwitchEmittedUtcTicks = nowUtcTicks;

        /// <summary>True while the post-emit silence gate is active. UI
        /// consumers reflect this in dashboard-switch affordances (disable
        /// dropdown / Start Test) so the user can't trigger races against
        /// the in-flight Stop+Start.</summary>
        public bool IsInSilenceCooldown
        {
            get
            {
                if (_lastSwitchEmittedUtcTicks == 0) return false;
                long elapsedMs = (DateTime.UtcNow.Ticks - _lastSwitchEmittedUtcTicks)
                    / TimeSpan.TicksPerMillisecond;
                int gateMs = _isHotRenegotiationEnabled()
                    ? HotSwitchCooldownMs
                    : StopReopenSilenceMs;
                return elapsedMs < gateMs;
            }
        }
    }
}
