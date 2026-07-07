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
            if (mode != StalkMode.TruckSim) { _kb.ReleaseAll(); _kb.Flush(); }
        }

        /// <summary>Push the current game context each DataUpdate tick.</summary>
        public void SetGameContext(string gameName, bool gameRunning)
        {
            bool truck = gameRunning && IsTruckSimGame(gameName);
            _truckGameRunning = truck;
            if (!truck)
            {
                _kb.ReleaseAll();
                _kb.Flush();
            }
            // Do NOT re-home the tracked stages when the game re-enters focus. Alt-
            // tabbing flips GameRunning while the truck keeps its lights/wipers, so
            // resetting to 0 here desynced them. Tracking persists across focus
            // changes; a fresh spawn starts at 0 (field defaults) and the Re-sync
            // button realigns if anything ever drifts.
        }

        /// <summary>Handle a stalk button edge (from the HID read thread).</summary>
        public void OnStalkButton(int index, bool pressed)
        {
            StalkAction? action = null;
            StalkTruckSimSettings cfg;
            lock (_lock)
            {
                cfg = _cfg;
                if (_mode == StalkMode.TruckSim && cfg.ButtonActions != null &&
                    cfg.ButtonActions.TryGetValue(index, out var a))
                    action = a;
            }

            // Release edge: only held keys act — always send the key-up so a key can
            // never stick down (a mode/game change also calls ReleaseAll()).
            if (!pressed)
            {
                if (action != null && action.Kind == StalkActionKind.HeldKey)
                    _kb.KeyUp(action.Key);
                return;
            }

            // Press edge: gate on the game being active AND the foreground window.
            // While alt-tabbed the key output is dropped, so processing the press
            // would advance the tracked wiper/light stage and desync it — ignore it.
            if (!Active || action == null || !_kb.IsGameForeground()) return;

            switch (action.Kind)
            {
                case StalkActionKind.HeldKey:
                    _kb.KeyDown(action.Key);
                    break;
                case StalkActionKind.LatchKey:
                    // Latch down and keep held; released by a ReleaseHeld button
                    // (e.g. the neutral stalk position), not this button's release.
                    _kb.KeyDown(action.Key);
                    break;
                case StalkActionKind.ReleaseHeld:
                    _kb.ReleaseAll();
                    break;
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
            // Drive the wipers to off (we have a back key); the light knob's cycle
            // key is forward-only so we can't force it — just reset the light tracker
            // and assume the user has set the lights off.
            if (Active && _kb.IsGameForeground())
            {
                int backs = Math.Max(0, cfg.WiperStageCount - 1);
                for (int i = 0; i < backs; i++) _kb.Tap(cfg.WiperBackKey);
            }
            lock (_lock) { _wiperStage = 0; _lightStage = 0; }
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
