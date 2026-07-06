using System;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Translates MOZA Stalks button presses into keyboard output for ETS2/ATS
    /// ("truck-sim mode"). Momentary buttons tap a key; wiper/light-knob positions
    /// step the game's cycling controls to the mapped stage. All key output goes
    /// through <see cref="KeyboardSender"/> (its own worker thread + foreground gate),
    /// so the HID and SimHub threads never block.
    ///
    /// Wiring: the plugin subscribes <see cref="OnStalkButton"/> to
    /// <c>MozaHidReader.StalksButtonChanged</c>, calls <see cref="ApplySettings"/>
    /// whenever settings change, and <see cref="SetGameContext"/> each DataUpdate.
    /// </summary>
    internal sealed class StalksTruckSimController : IDisposable
    {
        private readonly KeyboardSender _kb = new KeyboardSender();
        private readonly object _lock = new object();

        // Config snapshot (guarded by _lock).
        private StalkMode _mode = StalkMode.ButtonBox;
        private StalkTruckSimSettings _cfg = new StalkTruckSimSettings();

        // Fast gates (read on the HID thread).
        private volatile bool _truckSimEnabled;   // _mode == TruckSim
        private volatile bool _truckGameRunning;  // running && ETS2/ATS

        // Open-loop tracked game stages (guarded by _lock).
        private int _wiperStage;
        private int _lightStage;
        private int _activeIndicator; // 0 none, 1 left, 2 right

        public StalksTruckSimController()
        {
            // Only inject keys when a truck game is the foreground window.
            _kb.SetForegroundProcesses("eurotrucks2", "amtrucks");
        }

        private bool Active => _truckSimEnabled && _truckGameRunning;

        /// <summary>Apply the current mode + config (call on load and on every UI edit).</summary>
        public void ApplySettings(StalkMode mode, StalkTruckSimSettings cfg)
        {
            lock (_lock)
            {
                _mode = mode;
                _cfg = cfg ?? new StalkTruckSimSettings();
                _kb.HoldMs = Math.Max(1, _cfg.KeyHoldMs);
                _kb.GapMs = Math.Max(0, _cfg.KeyGapMs);
            }
            _truckSimEnabled = mode == StalkMode.TruckSim;
            if (mode != StalkMode.TruckSim) _kb.Flush();
        }

        /// <summary>Push the current game context each DataUpdate tick.</summary>
        public void SetGameContext(string gameName, bool gameRunning)
        {
            bool truck = gameRunning && IsTruckSimGame(gameName);
            bool was = _truckGameRunning;
            _truckGameRunning = truck;
            if (!truck)
            {
                _kb.Flush();
            }
            else if (!was)
            {
                // Entering a truck game: assume its cycling controls start at 0
                // (wipers off / lights off at spawn). Re-home the open-loop trackers.
                lock (_lock) { _wiperStage = 0; _lightStage = 0; _activeIndicator = 0; }
            }
        }

        /// <summary>Handle a stalk button edge (from the HID read thread).</summary>
        public void OnStalkButton(int index, bool pressed)
        {
            if (!pressed || !Active) return;

            StalkAction action;
            StalkTruckSimSettings cfg;
            lock (_lock)
            {
                if (_mode != StalkMode.TruckSim) return;
                cfg = _cfg;
                if (cfg.ButtonActions == null ||
                    !cfg.ButtonActions.TryGetValue(index, out action) || action == null)
                    return;
            }

            switch (action.Kind)
            {
                case StalkActionKind.Momentary:
                    _kb.Tap(action.Key);
                    break;
                case StalkActionKind.WiperStage:
                    ConvergeWiper(action.Stage, cfg);
                    break;
                case StalkActionKind.LightStage:
                    ConvergeLight(action.Stage, cfg);
                    break;
                case StalkActionKind.IndicatorLeft:
                    Indicate(cfg.IndicatorLeftKey, side: 1);
                    break;
                case StalkActionKind.IndicatorRight:
                    Indicate(cfg.IndicatorRightKey, side: 2);
                    break;
                case StalkActionKind.IndicatorCancel:
                    CancelIndicator(cfg);
                    break;
                case StalkActionKind.WiperSingleSwipe:
                    // One forward step (wipers on). The spring-loaded stalk returning
                    // to the "off" position fires that button's WiperStage 0 action,
                    // which converges back down and sends the back key. Bump the
                    // tracked stage so that return sends exactly one back tap.
                    _kb.Tap(cfg.WiperForwardKey);
                    lock (_lock) _wiperStage = WrapStage(_wiperStage + 1, cfg.WiperStageCount);
                    break;
            }
        }

        // Turn-signal: tap the side's key and remember it so a later neutral press
        // (IndicatorCancel) can re-tap it to toggle the blinker off.
        private void Indicate(string key, int side)
        {
            lock (_lock) _activeIndicator = side;
            _kb.Tap(key);
        }

        private void CancelIndicator(StalkTruckSimSettings cfg)
        {
            string? key = null;
            lock (_lock)
            {
                if (_activeIndicator == 1) key = cfg.IndicatorLeftKey;
                else if (_activeIndicator == 2) key = cfg.IndicatorRightKey;
                _activeIndicator = 0;
            }
            if (key != null) _kb.Tap(key);
        }

        private void ConvergeWiper(int target, StalkTruckSimSettings cfg)
        {
            int cur;
            lock (_lock) cur = _wiperStage;
            var steps = StageCycle.PlanSteps(cur, target, cfg.WiperStageCount, cfg.WiperForwardWraps, hasBackKey: true);
            foreach (var s in steps)
                _kb.Tap(s > 0 ? cfg.WiperForwardKey : cfg.WiperBackKey);
            lock (_lock) _wiperStage = WrapStage(target, cfg.WiperStageCount);
        }

        private void ConvergeLight(int target, StalkTruckSimSettings cfg)
        {
            int cur;
            lock (_lock) cur = _lightStage;
            // Light knob uses a single forward-only cycle key (wraps).
            var steps = StageCycle.PlanSteps(cur, target, cfg.LightStageCount, wrap: true, hasBackKey: false);
            foreach (var _ in steps)
                _kb.Tap(cfg.LightCycleKey);
            lock (_lock) _lightStage = WrapStage(target, cfg.LightStageCount);
        }

        /// <summary>Force the game wipers to stage 0 and re-sync the tracker
        /// (the UI "Re-sync wipers" button) — drops enough back-taps to reach off
        /// from any stage.</summary>
        public void ResyncWipers()
        {
            StalkTruckSimSettings cfg;
            lock (_lock) cfg = _cfg;
            if (!Active) { lock (_lock) _wiperStage = 0; return; }
            int backs = Math.Max(0, cfg.WiperStageCount - 1);
            for (int i = 0; i < backs; i++) _kb.Tap(cfg.WiperBackKey);
            lock (_lock) _wiperStage = 0;
        }

        private static int WrapStage(int stage, int count)
        {
            if (count <= 0) return 0;
            int r = stage % count;
            return r < 0 ? r + count : r;
        }

        /// <summary>SimHub game code / name that indicates ETS2 or ATS.</summary>
        public static bool IsTruckSimGame(string gameName)
        {
            if (string.IsNullOrEmpty(gameName)) return false;
            return gameName.Equals("Ets2", StringComparison.OrdinalIgnoreCase)
                || gameName.Equals("Ats", StringComparison.OrdinalIgnoreCase)
                || gameName.IndexOf("truck", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void Dispose()
        {
            try { _kb.Dispose(); } catch { }
        }
    }
}
