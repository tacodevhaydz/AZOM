using System;
using System.Drawing;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using MozaPlugin.Protocol;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// A virtual ILedDeviceManager for the MOZA CM2/CM1 dashboard.
    /// Always reports as connected to enable SimHub's LED effects UI.
    /// Receives computed RPM LED colors from Display() and sends a 16-bit on/off
    /// bitmask to the dash via dash-send-telemetry. Colors are stored on the
    /// device firmware — only the bitmask is sent per frame.
    ///
    /// PitHouse drives the bus CM2's RPM LEDs the same way: a per-frame
    /// dash-send-telemetry bitmask (group 0x41 cmd FD DE) that the firmware
    /// lights in its configured colours — verified cm2.pcapng 2026-06-08. An
    /// earlier build streamed per-LED colour on group 0x32 sub 0x0B to dev 0x12
    /// instead; the firmware ignores that, so CM2 LEDs never lit.
    /// </summary>
    internal class MozaDashLedDeviceManager : ILedDeviceManager
    {
        private LedDeviceState _lastState = new LedDeviceState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), 1.0, 1.0, 1.0, 1.0);

        private int _lastBitmask = -1;

        // LED-bitmask keepalive: the dash firmware blanks its LEDs if it doesn't
        // get a fresh dash-send-telemetry frame within a few seconds, even when the
        // value is unchanged. PitHouse re-sends the bitmask every telemetry frame
        // (cm2.pcapng: ~21/s, including FD DE 00000000 when static); we re-send the
        // last bitmask at 1 Hz when nothing changes — same pattern the wheel uses
        // (MozaLedDeviceManager.KeepaliveIntervalSeconds).
        private DateTime _lastSendTime = DateTime.MinValue;
        private const double KeepaliveIntervalSeconds = 1.0;

        public LedModuleSettings LedModuleSettings { get; set; } = null!;

        public LedDeviceState LastState => _lastState;

        private bool _wasConnected;

        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
        public event EventHandler? OnConnect;
#pragma warning disable CS0067 // Required by ILedDeviceManager interface
        public event EventHandler? OnError;
#pragma warning restore CS0067
        public event EventHandler? OnDisconnect;

        /// <summary>
        /// Check current detection state and fire OnConnect/OnDisconnect if it changed.
        /// Called from device extension's DataUpdate() every frame.
        /// </summary>
        internal void UpdateConnectionState()
        {
            bool connected = IsConnected();
            if (connected == _wasConnected) return;
            _wasConnected = connected;

            if (connected)
            {
                OnConnect?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _lastBitmask = -1;
                _lastSendTime = DateTime.MinValue;
                OnDisconnect?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsConnected() => MozaPlugin.Instance?.IsDashDetected ?? false;

        public string GetSerialNumber() => "MOZA-DASH-VIRTUAL";

        public string GetFirmwareVersion() => "1.0";

        public object GetDriverInstance() => this;

        public void Close() { }

        public void ResetDetection() { }

        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }

        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();

        public ILedDriverBase? GetLedDriver() => null;

        public void Display(
            Func<Color[]> leds,
            Func<Color[]> buttons,
            Func<Color[]> encoders,
            Func<Color[]> matrix,
            Func<Color[]> rawState,
            bool forceRefresh,
            Func<object>? extraData = null,
            double rpmBrightness = 1.0,
            double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0,
            double matrixBrightness = 1.0)
        {
            BeforeDisplay?.Invoke(this, EventArgs.Empty);

            try
            {
                var ledColors = leds?.Invoke() ?? Array.Empty<Color>();
                var buttonColors = buttons?.Invoke() ?? Array.Empty<Color>();
                var encoderColors = encoders?.Invoke() ?? Array.Empty<Color>();
                var matrixColors = matrix?.Invoke() ?? Array.Empty<Color>();
                var rawColors = rawState?.Invoke() ?? Array.Empty<Color>();

                _lastState = new LedDeviceState(
                    ledColors, buttonColors, encoderColors, matrixColors, rawColors,
                    rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

                // CM2/CM1 dashboards are 16 RPM LEDs with no flag section, so the
                // mask is RPM-only: bit i = telemetry LED i (from the SimHub `leds`
                // stream). The `buttons` channel has no flag section to draw from
                // and is always empty here.
                int rpmCount = Math.Min(ledColors.Length, 16);

                // Merge SimHub Individual-LED overrides into the RPM range. (No-op
                // on CM2 — its profile has the individual section disabled, so
                // rawColors is empty.)
                if (rawColors.Length > 0)
                    ledColors = MozaLedDeviceManager.ApplyOverrides(ledColors, rawColors, 0, rpmCount);

                if (ledColors.Length == 0)
                    return;

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected || !plugin.IsDashDetected)
                    return;

                bool alwaysResendBitmask = plugin.Settings.AlwaysResendBitmask;

                // Build bitmask: RPM LED i lit → bit i set.
                int bitmask = 0;
                for (int i = 0; i < rpmCount; i++)
                {
                    if (ledColors[i].R > 0 || ledColors[i].G > 0 || ledColors[i].B > 0)
                        bitmask |= (1 << i);
                }

                // The dash takes the 16-bit on/off bitmask via dash-send-telemetry
                // (group 0x41 cmd FD DE). The firmware lights each set bit in its
                // stored colour. PitHouse drives the bus CM2's RPM LEDs exactly this
                // way — a per-frame bitmask (verified cm2.pcapng 2026-06-08).
                // WriteDashLedBitmask routes to the right device id / connection for
                // the active dashboard sink (standalone-USB CM2 → 0x12 on the
                // dedicated pipe, behind-base CM2 → 0x14 on the base).
                var now = DateTime.UtcNow;
                bool keepaliveDue = (now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds;
                if (alwaysResendBitmask || bitmask != _lastBitmask || keepaliveDue)
                {
                    _lastBitmask = bitmask;
                    _lastSendTime = now;
                    plugin.WriteDashLedBitmask(bitmask);
                }

                // Dashboard brightness is stored config (set via plugin UI slider →
                // ApplySavedDashSettings on connect). Don't forward SimHub's per-frame
                // rpmBrightness here — SimHub passes 0 during scene transitions / no-game
                // states, which would blank the dashboard. SimHub brightness applies to
                // wheel RPM + button LEDs only.
            }
            finally
            {
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
