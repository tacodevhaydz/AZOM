using System;
using System.Collections.Generic;
using System.Threading;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Retry-with-backoff for one-shot commands awaiting a named response.
    /// Caller wires NoteResponse from the parser; thread-safe across send / retry / read.
    /// </summary>
    public sealed class PendingResponseTracker
    {
        private sealed class Entry
        {
            public string Name = "";
            public byte[] Frame = Array.Empty<byte>();
            public int[] BackoffMs = Array.Empty<int>();
            public int Attempts;
            public int MaxAttempts;
            public int LastSentTickCount;
            public long EnqueuedAt;
        }

        private readonly Dictionary<string, Entry> _pending = new();
        private readonly object _lock = new object();
        // Lock-free count mirror; NoteResponse early-outs when idle (~50-200 Hz path).
        private volatile int _pendingCount;

        public int PendingCount => _pendingCount;

        /// <summary>Total number of entries that exhausted their attempt budget
        /// without ever receiving a response. High counts indicate the wheel
        /// is dropping frames or never producing the expected named response.</summary>
        public int TimeoutCount => Interlocked.CompareExchange(ref _timeoutCount, 0, 0);
        private int _timeoutCount;

        /// <summary>Track an outbound frame awaiting a named response (caller has already Send'd).</summary>
        public void Track(string expectedName, byte[] frame, int[] backoffMs, int maxAttempts)
        {
            if (string.IsNullOrEmpty(expectedName) || frame == null || backoffMs == null) return;
            if (maxAttempts < 1) maxAttempts = 1;
            lock (_lock)
            {
                // Caller owns the backoff array (callers pass static-readonly
                // schedules). Frame buffer is cloned because callers may reuse
                // their build buffer across Sends.
                _pending[expectedName] = new Entry
                {
                    Name = expectedName,
                    Frame = (byte[])frame.Clone(),
                    BackoffMs = backoffMs,
                    Attempts = 1,
                    MaxAttempts = maxAttempts,
                    LastSentTickCount = Environment.TickCount,
                    EnqueuedAt = DateTime.UtcNow.Ticks,
                };
                _pendingCount = _pending.Count;
            }
        }

        /// <summary>
        /// Note a response from the wheel matching <paramref name="responseName"/>.
        /// Clears the pending entry if present. Called from the response-dispatch
        /// path so retries stop immediately on first match.
        /// </summary>
        public void NoteResponse(string responseName)
        {
            if (string.IsNullOrEmpty(responseName)) return;
            // Fast-path: in steady state nothing is pending and this fires on
            // every response — skipping the lock + dictionary hash here is the
            // difference between ~0 and ~50–200 lock acquisitions/sec on the
            // read thread.
            if (_pendingCount == 0) return;
            lock (_lock)
            {
                if (_pending.Remove(responseName))
                    _pendingCount = _pending.Count;
            }
        }

        /// <summary>
        /// Walk the pending entries, re-emit frames whose backoff has elapsed,
        /// drop entries past the attempt budget. Called periodically from the
        /// telemetry tick.
        /// </summary>
        /// <param name="resend">Callback invoked for each frame that should be
        /// re-emitted on the wire. Caller is responsible for the actual
        /// connection.Send call so this class stays transport-agnostic.</param>
        public void TickRetransmits(Action<byte[]> resend)
        {
            if (resend == null) return;
            // Fast-path mirror of NoteResponse — this fires every 250 ms even
            // when the tracker is idle.
            if (_pendingCount == 0) return;
            List<byte[]>? toResend = null;
            int dropped = 0;
            lock (_lock)
            {
                int now = Environment.TickCount;
                List<string>? doomed = null;
                foreach (var kv in _pending)
                {
                    var e = kv.Value;
                    int gateMs = e.BackoffMs[Math.Min(e.Attempts - 1, e.BackoffMs.Length - 1)];
                    if (now - e.LastSentTickCount < gateMs) continue;
                    if (e.Attempts >= e.MaxAttempts)
                    {
                        (doomed ??= new List<string>()).Add(kv.Key);
                        continue;
                    }
                    (toResend ??= new List<byte[]>()).Add(e.Frame);
                    e.Attempts++;
                    e.LastSentTickCount = now;
                }
                if (doomed != null)
                {
                    foreach (var k in doomed) _pending.Remove(k);
                    dropped = doomed.Count;
                    _pendingCount = _pending.Count;
                }
            }
            if (toResend != null)
            {
                foreach (var f in toResend) resend(f);
            }
            if (dropped > 0)
            {
                Interlocked.Add(ref _timeoutCount, dropped);
                global::MozaPlugin.MozaLog.Debug(
                    $"[Moza] PendingResponseTracker dropped {dropped} entries past attempt budget");
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _pending.Clear();
                _pendingCount = 0;
            }
        }
    }
}
