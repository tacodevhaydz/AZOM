using System;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry.Watchdog
{
    /// <summary>
    /// Four wheel-session watchdog loops (sess=0x09 retry, sess=0x02 engagement,
    /// configJson gap escalation, configJson stuck-state). Budget exhaustion parks
    /// or escalates to <see cref="TelemetrySender.RestartForSwitch"/>.
    /// </summary>
    internal sealed class SessionWatchdogManager
    {
        private readonly TelemetrySender _sender;

        // ── sess=0x09 retry ───────────────────────────────────────────────
        private int _s09RetryRounds;
        private int _s09RetryLastTickCount;
        private static readonly int[] S09BackoffMs =
            { 250, 500, 1000, 2000, 3000, 5000, 7000, 10000, 12000, 15000 };
        private const int S09RetryMaxRounds = 10;

        // ── sess=0x02 engagement watchdog ─────────────────────────────────
        private long _session02FirstInboundUtcTicks;
        private int _activeStateEnteredTickCount;
        private int _s02ReArmRounds;
        private int _s02ReArmLastTickCount;
        private static readonly int[] S02ReArmBackoffMs =
            { 3_000, 5_000, 7_000, 10_000, 15_000 };
        private const int S02ReArmMaxRounds = 5;
        private const int S02InitialGraceMs = 3_000;

        // ── configJson gap / stuck-state ──────────────────────────────────
        private int _configJsonGapCount;
        private long _configJsonLastChunkUtcTicks;
        private long _configJsonLastEscalationUtcTicks;
        private long _configJsonLastPrimeRetryUtcTicks;
        // Passive wait window before active prime+open-request: wheel's
        // outstanding-ack timer (~1.3 s) gets headroom before host escalates.
        private const int ConfigJsonGapPassiveWaitMs = 5_000;
        private const int ConfigJsonGapPrimeRetryAt = 1;
        private const int ConfigJsonGapRestartAt = 4;
        // Min gap between escalations so a chunk-drop storm can't cycle restart
        // faster than the wheel's 11 s sess=0x09 settle.
        private const long ConfigJsonEscalationCooldownTicks =
            15_000 * TimeSpan.TicksPerMillisecond;
        // No-progress watchdog: LastState still null this long after first chunk = restart.
        private const long ConfigJsonNoStateRestartTimeoutTicks =
            30_000 * TimeSpan.TicksPerMillisecond;

        public SessionWatchdogManager(TelemetrySender sender)
        {
            _sender = sender;
        }

        // ───── Notification API (called by sender/inbound dispatch) ───────

        /// <summary>Called when sess=FlagByte (0x02) receives its first inbound chunk.</summary>
        public void NoteSession02FirstInbound()
        {
            if (_session02FirstInboundUtcTicks == 0)
                _session02FirstInboundUtcTicks = DateTime.UtcNow.Ticks;
        }

        public void NoteConfigJsonChunkArrived() =>
            _configJsonLastChunkUtcTicks = DateTime.UtcNow.Ticks;

        public void ResetConfigJsonGapTracking()
        {
            _configJsonGapCount = 0;
            _configJsonLastPrimeRetryUtcTicks = 0;
        }

        public void NoteActiveStateEntered() => _activeStateEnteredTickCount = Environment.TickCount;

        /// <summary>Reset all watchdog state at sender Start/Stop boundary.</summary>
        public void Reset()
        {
            _s09RetryRounds = 0;
            _s09RetryLastTickCount = 0;
            _session02FirstInboundUtcTicks = 0;
            _activeStateEnteredTickCount = 0;
            _s02ReArmRounds = 0;
            _s02ReArmLastTickCount = 0;
            _configJsonGapCount = 0;
            _configJsonLastChunkUtcTicks = 0;
            _configJsonLastPrimeRetryUtcTicks = 0;
            // _configJsonLastEscalationUtcTicks NOT reset — cooldown spans restarts.
        }

        // ───── Tick loops (called by sender's tick driver) ────────────────

        /// <summary>Re-emit prime+open-request on sess=0x09 until device-init or budget exhaustion (parks pipeline).</summary>
        public void TickRetryS09IfNotEstablished()
        {
            if (_sender.StateIsIdle) return;
            if (_s09RetryRounds >= S09RetryMaxRounds) return;
            if (!_sender.ConnectionIsConnected) return;

            var s09 = _sender.SessionsGetOrCreate(0x09);
            if (s09.DeviceInitiated) return;

            int now = Environment.TickCount;
            if (_s09RetryRounds > 0)
            {
                int gateMs = S09BackoffMs[Math.Min(_s09RetryRounds - 1, S09BackoffMs.Length - 1)];
                if ((now - _s09RetryLastTickCount) < gateMs) return;
            }

            _s09RetryRounds++;
            _s09RetryLastTickCount = now;

            // Fresh seq so the wheel doesn't dedupe against the prior open.
            ushort recoverySeq = (ushort)(0x000B + _s09RetryRounds * 0x10);
            MozaLog.Warn(
                $"[Moza] sess=0x09 not yet device-initiated; retry round " +
                $"{_s09RetryRounds}/{S09RetryMaxRounds} (open-seq=0x{recoverySeq:X4})");

            try
            {
                _sender.SendSessionPrime(0x09, (ushort)(0x0001 + _s09RetryRounds));
                SendConfigJsonOpenRequest(0x09, recoverySeq);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] sess=0x09 retry emit failed: {ex.Message}");
            }

            if (_s09RetryRounds >= S09RetryMaxRounds)
            {
                MozaLog.Warn(
                    $"[Moza] sess=0x09 retry budget exhausted after {S09RetryMaxRounds} rounds " +
                    "— wheel never engaged the configJson handshake. Parking dashboard pipeline " +
                    "(closing sessions 0x01/0x02/0x03, transitioning to Idle) to prevent port wedge. " +
                    "Cause is usually a wheel with no display sub-device or a wheel that refused the " +
                    "dashboard session — common for displayless wheels that slipped past the static " +
                    "HasDisplay gate. A wheel hot-swap or telemetry toggle will re-attempt.");
                // Defer Stop+Park to a worker thread so the rest of the current tick
                // (TickConfigJsonStuckWatchdog, TickEmitWidgetPoll, etc.) can complete
                // without operating on torn-down state.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _sender.Stop(); }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[Moza] sess=0x09 park Stop() raised: {ex.GetType().Name}: {ex.Message}");
                    }
                    try { _sender.RaiseDashboardPipelineParked(); } catch { }
                });
            }
        }

        /// <summary>
        /// Soft-watchdog for configJson gaps that have stalled past the
        /// passive-wait window. Sends prime+open-request to nudge the wheel
        /// without yet escalating to RestartForSwitch.
        /// </summary>
        public void TickConfigJsonGapEscalation()
        {
            if (_sender.StateIsIdle) return;
            if (!_sender.ConnectionIsConnected) return;
            if (_sender.ConfigJsonHasLastState) return;
            long gapTs = _sender.ConfigJsonLastForwardGapUtcTicks;
            if (gapTs == 0) return;

            long now = DateTime.UtcNow.Ticks;
            long gapAgeMs = (now - gapTs) / TimeSpan.TicksPerMillisecond;
            if (gapAgeMs < ConfigJsonGapPassiveWaitMs) return;

            // Don't escalate if we already nudged within the passive window
            // (HandleConfigJsonGap and this share _configJsonLastPrimeRetryUtcTicks).
            long primeAgeTicks = now - _configJsonLastPrimeRetryUtcTicks;
            long passiveWaitTicks = ConfigJsonGapPassiveWaitMs * TimeSpan.TicksPerMillisecond;
            if (_configJsonLastPrimeRetryUtcTicks != 0 && primeAgeTicks < passiveWaitTicks)
                return;

            int recoveryOpenSeq = unchecked((ushort)(0x100 + _configJsonGapCount));
            int primeSeq = unchecked((ushort)(0x200 + _configJsonGapCount));
            byte session = 0x09;
            // Prefer 0x0a if the wheel's been talking on it instead.
            if (_sender.Session09InboundSeq == 0 && _configJsonLastChunkUtcTicks != 0)
                session = 0x0a;
            try
            {
                MozaLog.Warn(
                    $"[Moza] sess=0x{session:X2} configJson gap stale " +
                    $"({gapAgeMs}ms ≥ {ConfigJsonGapPassiveWaitMs}ms passive-wait window) — " +
                    $"wheel didn't auto-retransmit. " +
                    $"prime + open-request (open seq=0x{recoveryOpenSeq:X4}, prime seq=0x{primeSeq:X4})");
                _sender.SendSessionPrime(session, (ushort)primeSeq);
                SendConfigJsonOpenRequest(session, (ushort)recoveryOpenSeq);
                _configJsonLastPrimeRetryUtcTicks = now;
                if (_configJsonGapCount == 0) _configJsonGapCount = 1;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] sess=0x{session:X2} configJson tick-watchdog recovery failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stuck-state restart only when chunks arrived but no LastState materialised
        /// AND catalog+subscription aren't healthy. ConfigJson is library-UI only.
        /// </summary>
        public void TickConfigJsonStuckWatchdog()
        {
            if (_sender.StateIsIdle) return;
            if (!_sender.ConnectionIsConnected) return;
            if (_sender.ConfigJsonHasLastState) return;
            if (_configJsonLastChunkUtcTicks == 0) return;

            // Working dashboard despite missing library list — catalog + subscription is enough.
            bool haveCatalog = _sender.CatalogCount > 0;
            bool haveSubscription = _sender.HasActiveSubscription;
            if (haveCatalog && haveSubscription)
                return;

            long now = DateTime.UtcNow.Ticks;
            if (now - _configJsonLastChunkUtcTicks < ConfigJsonNoStateRestartTimeoutTicks)
                return;
            if (now - _configJsonLastEscalationUtcTicks < ConfigJsonEscalationCooldownTicks)
                return;

            _configJsonLastEscalationUtcTicks = now;
            _configJsonGapCount = 0;
            _configJsonLastChunkUtcTicks = now;
            MozaLog.Warn(
                "[Moza] configJson stuck-state watchdog: " +
                $"chunks arrived but no valid state for {ConfigJsonNoStateRestartTimeoutTicks / TimeSpan.TicksPerMillisecond / 1000}s, " +
                "and no catalog/subscription either — triggering full RestartForSwitch");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _sender.RestartForSwitch(); }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] RestartForSwitch from stuck-state watchdog failed: {ex.Message}");
                }
            });
        }

        /// <summary>Sess=0x02 engagement: re-arm (close+open+init+resubscribe) if no inbound; escalate on exhaustion.</summary>
        public void TickSession02EngagementWatchdog()
        {
            if (!_sender.StateIsActive) return;
            if (!_sender.ConnectionIsConnected) return;
            if (_session02FirstInboundUtcTicks != 0) return;
            if (_s02ReArmRounds >= S02ReArmMaxRounds) return;
            // Defensive: skip silently if a tick fires before NoteActiveStateEntered.
            if (_activeStateEnteredTickCount == 0) return;

            int now = Environment.TickCount;
            if (_s02ReArmRounds == 0)
            {
                // Initial grace — ~3 s covers the observed PitHouse cadence.
                if ((now - _activeStateEnteredTickCount) < S02InitialGraceMs) return;
            }
            else
            {
                int gateMs = S02ReArmBackoffMs[Math.Min(_s02ReArmRounds - 1, S02ReArmBackoffMs.Length - 1)];
                if ((now - _s02ReArmLastTickCount) < gateMs) return;
            }

            _s02ReArmRounds++;
            _s02ReArmLastTickCount = now;

            MozaLog.Warn(
                $"[Moza] sess=0x02 no inbound observed; re-arm round " +
                $"{_s02ReArmRounds}/{S02ReArmMaxRounds} — close+open+init+resubscribe");

            try
            {
                const int ReArmAckTimeoutMs = 500;
                _sender.TryCloseSession(0x02, ReArmAckTimeoutMs);
                _sender.TryOpenSession(0x02, ReArmAckTimeoutMs);
                _sender.SendSessionInitHandshake();
                _sender.ApplySubscription(force: true);
            }
            catch (Exception ex)
            {
                MozaLog.Warn(
                    $"[Moza] sess=0x02 re-arm round {_s02ReArmRounds} failed: {ex.Message}");
            }

            if (_s02ReArmRounds >= S02ReArmMaxRounds)
            {
                MozaLog.Warn(
                    $"[Moza] sess=0x02 re-arm budget exhausted after {S02ReArmMaxRounds} rounds " +
                    "— escalating to full RestartForSwitch (Stop+Start cycle)");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _sender.RestartForSwitch(); }
                    catch (Exception ex)
                    {
                        MozaLog.Warn(
                            $"[Moza] RestartForSwitch from sess=0x02 watchdog failed: {ex.Message}");
                    }
                });
            }
        }

        /// <summary>
        /// Three-tier gap recovery: passive wait (5 s) → prime+open → restart.
        /// Called from inbound dispatch on session-data gap.
        /// </summary>
        public void HandleConfigJsonGap(byte session, int seq)
        {
            _configJsonGapCount++;
            bool haveCachedState = _sender.ConfigJsonHasLastState;
            string tag = $"sess=0x{session:X2}";
            string cachedTag = haveCachedState ? "cached-state" : "no-state";

            if (haveCachedState)
            {
                MozaLog.Warn(
                    $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"buffer preserved, keeping cached state — no recovery action");
                return;
            }

            // T1 passive: wheel will retransmit from HighWaterSeq+1.
            long now = DateTime.UtcNow.Ticks;
            long gapAgeTicks = now - _sender.ConfigJsonLastForwardGapUtcTicks;
            long passiveWaitTicks = ConfigJsonGapPassiveWaitMs * TimeSpan.TicksPerMillisecond;
            if (_sender.ConfigJsonLastForwardGapUtcTicks != 0 && gapAgeTicks < passiveWaitTicks)
            {
                MozaLog.Debug(
                    $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"waiting up to {ConfigJsonGapPassiveWaitMs}ms for wheel auto-retransmit " +
                    $"(gap age={gapAgeTicks / TimeSpan.TicksPerMillisecond}ms)");
                return;
            }

            // Don't re-fire prime+open-request within the passive window of the prior retry.
            long primeAgeTicks = now - _configJsonLastPrimeRetryUtcTicks;
            if (_configJsonGapCount <= ConfigJsonGapPrimeRetryAt
                && _configJsonLastPrimeRetryUtcTicks != 0
                && primeAgeTicks < passiveWaitTicks)
            {
                MozaLog.Debug(
                    $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"prior prime+open-request {primeAgeTicks / TimeSpan.TicksPerMillisecond}ms ago — deferring next");
                return;
            }

            if (_configJsonGapCount <= ConfigJsonGapPrimeRetryAt)
            {
                try
                {
                    int recoveryOpenSeq = unchecked((ushort)(seq + 0x100 * _configJsonGapCount));
                    int primeSeq = unchecked((ushort)(seq + 0x200 + _configJsonGapCount));
                    MozaLog.Warn(
                        $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                        $"passive wait expired ({gapAgeTicks / TimeSpan.TicksPerMillisecond}ms) — " +
                        $"prime + open-request (open seq=0x{recoveryOpenSeq:X4}, prime seq=0x{primeSeq:X4})");
                    _sender.SendSessionPrime(session, (ushort)primeSeq);
                    SendConfigJsonOpenRequest(session, (ushort)recoveryOpenSeq);
                    _configJsonLastPrimeRetryUtcTicks = now;
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] {tag} configJson recovery emit failed: {ex.Message}");
                }
                return;
            }

            if (_configJsonGapCount >= ConfigJsonGapRestartAt)
            {
                if (now - _configJsonLastEscalationUtcTicks < ConfigJsonEscalationCooldownTicks)
                {
                    MozaLog.Warn(
                        $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                        "in escalation cooldown — deferring full restart");
                    return;
                }
                _configJsonLastEscalationUtcTicks = now;
                _configJsonGapCount = 0;
                _configJsonLastPrimeRetryUtcTicks = 0;
                MozaLog.Warn(
                    $"[Moza] {tag} configJson recovery escalation: " +
                    "no cached state and prime+open-request didn't recover the burst — " +
                    "triggering full RestartForSwitch");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _sender.RestartForSwitch(); }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[Moza] {tag} RestartForSwitch from gap recovery failed: {ex.Message}");
                    }
                });
                return;
            }

            // Mid-tier (count between prime-retry and restart): just log; next chunk may unblock.
            MozaLog.Warn(
                $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                "waiting for next chunk before escalating");
        }

        /// <summary>
        /// Host-initiated session-open for the configJson channel (port 9).
        /// Uses configJson-specific magic <c>7c 1e 6c 80</c> — upload-style
        /// <c>7c 23 46 80</c> does NOT trigger wheel device-init for 0x09.
        /// </summary>
        public void SendConfigJsonOpenRequest(byte port, ushort seq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x1E, 0x6C, 0x80,
                (byte)(seq & 0xFF), (byte)(seq >> 8),
                port, 0x00,
                0xFE, 0x01,
                0x00
            };
            frame[14] = MozaProtocol.CalculateWireChecksum(frame);
            _sender.SendRawFrame(frame);
        }
    }
}
