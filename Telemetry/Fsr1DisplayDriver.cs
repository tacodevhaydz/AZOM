using System;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Standalone driver for the FSR V1 group-0x42 display push (dev 0x17). Runs on
    /// its OWN timer, independent of the tier-def <see cref="TelemetrySender"/>, so an
    /// FSR1 wheel screen and a CM2 dash (driven by a separate tier-def sender on the
    /// same or a different connection) can run concurrently. Shares the wheelbase
    /// <see cref="MozaSerialConnection"/> but uses only the wheel-lane stream slots
    /// (0..8), disjoint from a co-resident bus-CM2 pipeline at slot-base 18.
    ///
    /// Reuses <see cref="Fsr1DisplayEmitter"/> / <see cref="Fsr1DashboardCatalog"/> and
    /// the plugin's per-field mappings, active-index, pending-select, and Param-6
    /// follow (all on <see cref="MozaPlugin"/>). See docs/protocol/devices/wheel-0x17.md
    /// § Group 0x42.
    /// </summary>
    internal sealed class Fsr1DisplayDriver : IDisposable
    {
        private const double TickIntervalMs = 35.0;   // ~28.6 Hz, matches capture
        private const double SecondaryHz = 3.0;       // low-rate non-primary records
        // FSR1 occupies the wheel lane: TierDash0..6 (records) + Enable (keepalive).
        private const int LaneSlotCount = 9;          // slots 0..8

        private readonly MozaSerialConnection _connection;
        private readonly Func<string, double> _resolve;

        private System.Timers.Timer? _timer;
        private int _tickInProgress;
        private volatile bool _running;
        private bool _lastConnected;

        private volatile StatusDataBase? _latestGameData;
        private volatile bool _gameRunning;
        private int _tickCounter;
        private int _lastStreamedIndex = -1;

        public Fsr1DisplayDriver(MozaSerialConnection connection, Func<string, double> resolve)
        {
            _connection = connection;
            _resolve = resolve;
        }

        public bool IsRunning => _running;
        public void UpdateGameData(StatusDataBase? data) => _latestGameData = data;
        public void SetGameRunning(bool running) => _gameRunning = running;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _tickCounter = 0;
            _lastStreamedIndex = -1;
            _lastConnected = _connection.IsConnected;
            SendDeclarationSweep();
            _timer = new System.Timers.Timer(TickIntervalMs) { AutoReset = true };
            _timer.Elapsed += OnTick;
            _timer.Start();
            MozaLog.Info("[Moza] FSR V1 display driver started (group-0x42 → 0x17)");
        }

        public void Stop()
        {
            if (!_running && _timer == null) return;
            _running = false;
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Elapsed -= OnTick;
                _timer.Dispose();
                _timer = null;
            }
            // Clear only the FSR1 wheel-lane slots — leave any co-resident CM2 lane.
            try { _connection.ClearStreamSlots(0, LaneSlotCount); } catch { }
        }

        public void Dispose() => Stop();

        private void SendDeclarationSweep()
        {
            try
            {
                foreach (var frame in Fsr1DisplayEmitter.DeclarationSweep)
                    _connection.Send(frame);
            }
            catch { }
        }

        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _tickInProgress, 1, 0) != 0) return;
            try { Tick(); }
            catch (Exception ex) { MozaLog.Debug($"[Moza] FSR1 driver tick error: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _tickInProgress, 0); }
        }

        private void Tick()
        {
            bool connected = _connection.IsConnected;
            if (!connected) { _lastConnected = false; return; }
            if (!_lastConnected)
            {
                // Reconnect: re-send the declaration sweep so the wheel re-accepts records.
                _lastConnected = true;
                _lastStreamedIndex = -1;
                SendDeclarationSweep();
            }

            var plugin = MozaPlugin.Instance;
            int oneHzEvery = Math.Max(1, (int)Math.Round(1000.0 / TickIntervalMs));

            // Telemetry disabled by the user: keepalive only (keeps the wheel engaged).
            if (!(plugin?.ActiveTelemetryEnabled ?? false))
            {
                if (_tickCounter % oneHzEvery == 0)
                    _connection.SendStream(StreamKind.Enable, Fsr1DisplayEmitter.Keepalive43);
                _tickCounter++;
                return;
            }

            var data = _latestGameData;
            bool engineRunning = (data?.Rpms ?? 0.0) > 1.0;
            var resolve = _resolve;

            // Host-initiated dashboard switch: emit the group-0x32/0x81 select command.
            int pending = plugin?.TakePendingFsr1Select() ?? -1;
            if (pending >= 0)
                _connection.Send(Fsr1DisplayEmitter.BuildSelect(pending));

            long ValueFor(Fsr1Dashboard dash, Fsr1FieldDef f)
            {
                if (f.Kind == Fsr1FieldKind.EngineFlag)
                    return engineRunning ? Fsr1DisplayEmitter.EngineFlagValue : 0;

                var m = plugin?.GetFsr1FieldMapping(dash.Key, f.FieldId);
                string prop = m?.Property ?? f.DefaultProperty;
                if (string.IsNullOrEmpty(prop)) return 0;

                double raw = resolve != null ? resolve(prop) : 0.0;
                long outMax = f.OutputMax;
                if (f.Kind == Fsr1FieldKind.Direct)
                    return Clamp((long)Math.Round(raw), 0, outMax);

                double inMin = m != null ? m.InMin : f.DefaultInMin;
                double inMax = m != null ? m.InMax : f.DefaultInMax;
                double span = inMax - inMin;
                double t = span > 0 ? (raw - inMin) / span : 0.0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                return Clamp((long)Math.Round(t * outMax), 0, outMax);
            }

            // Active page's primary record at full rate + the other field-bearing
            // records at the low secondary rate (all the channels; see wheel-0x17.md).
            int activeIdx = plugin?.GetActiveFsr1Index() ?? 0;
            var primary = Fsr1DashboardCatalog.ByIndex(activeIdx);
            byte primaryType = primary?.RecordType ?? 0xFF;
            int secondaryEvery = Math.Max(1, (int)Math.Round(1000.0 / TickIntervalMs / SecondaryHz));

            if (primary != null && activeIdx != _lastStreamedIndex)
            {
                _lastStreamedIndex = activeIdx;
                _connection.Send(Fsr1DisplayEmitter.BuildDeclaration(primary));
            }
            else if (primary == null)
            {
                _lastStreamedIndex = -1;
            }

            var live = Fsr1DashboardCatalog.LiveDashboards;
            for (int slot = 0; slot < live.Length; slot++)
            {
                var dash = live[slot];
                if (dash.Fields.Length == 0) continue;
                bool isPrimary = primary != null && dash.RecordType == primaryType;
                bool emit = isPrimary || primary == null || (_tickCounter % secondaryEvery == 0);
                if (!emit) continue;
                _connection.SendStream(
                    (StreamKind)((int)StreamKind.TierDash0 + slot),
                    Fsr1DisplayEmitter.BuildRecord(dash, f => ValueFor(dash, f)));
            }

            if (_tickCounter % oneHzEvery == 0)
                _connection.SendStream(StreamKind.Enable, Fsr1DisplayEmitter.Keepalive43);

            _tickCounter++;
        }

        private static long Clamp(long v, long lo, long hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
