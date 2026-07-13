namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Operating mode for the MOZA Multi-Function Stalks.
    /// </summary>
    public enum StalkMode
    {
        /// <summary>Default — the plugin does nothing; the stalks act as a raw
        /// 28-button HID joystick (bind them yourself in the game / SimHub).</summary>
        ButtonBox = 0,

        /// <summary>ETS2/ATS truck-sim keyboard emulation — the plugin translates
        /// stalk button presses into keyboard keys for the truck game.</summary>
        TruckSim = 1,
    }

    /// <summary>
    /// What a single stalk button does while in <see cref="StalkMode.TruckSim"/>.
    /// </summary>
    public enum StalkActionKind
    {
        /// <summary>Unassigned — the button does nothing.</summary>
        None = 0,

        /// <summary>Tap a keyboard key once on press (horn, hazard, indicators, …).
        /// The game handles any stateful behavior (e.g. indicator auto-cancel).</summary>
        Momentary = 1,

        /// <summary>Select a wiper stage: the controller steps the game's wiper to
        /// <see cref="StalkAction.Stage"/> using the forward/back cycle keys.</summary>
        WiperStage = 2,

        /// <summary>Select a light-knob stage: the controller steps the game's light
        /// mode to <see cref="StalkAction.Stage"/> using the light cycle key.</summary>
        LightStage = 3,

        /// <summary>Turn-signal stalk moved LEFT: taps the left-indicator key and
        /// remembers it as the active blinker (for <see cref="IndicatorCancel"/>).</summary>
        IndicatorLeft = 4,

        /// <summary>Turn-signal stalk moved RIGHT: taps the right-indicator key and
        /// remembers it as the active blinker.</summary>
        IndicatorRight = 5,

        /// <summary>Turn-signal stalk returned to neutral: re-taps whichever indicator
        /// key is active to cancel it (games where the indicator key toggles).</summary>
        IndicatorCancel = 6,

        /// <summary>Wiper "single swipe" position: pulses the wipers on then off
        /// (forward + back cycle tap) without changing the tracked stage.</summary>
        WiperSingleSwipe = 7,

        /// <summary>Hold a key down while the button is pressed and release it when the
        /// button is released (for a stalk position that reports a sustained hold).</summary>
        HeldKey = 8,

        /// <summary>Latch a key down on press and keep it held (auto-repeated) — NOT
        /// released on this button's own release. Released by a <see cref="ReleaseHeld"/>
        /// button. Use for a flash lever that pulses instead of reporting a hold: the
        /// key stays down until the stalk returns to neutral.</summary>
        LatchKey = 9,

        /// <summary>Release every currently-held/latched key. Map to the neutral stalk
        /// position so returning there drops the flash.</summary>
        ReleaseHeld = 10,
    }

    /// <summary>
    /// Per-button configuration in truck-sim mode. Plain serializable POCO — stored
    /// in <c>MozaPluginSettings</c> as a <c>Dictionary&lt;int, StalkAction&gt;</c>
    /// keyed by 0-based stalk button index.
    /// </summary>
    public sealed class StalkAction
    {
        public StalkActionKind Kind { get; set; } = StalkActionKind.None;

        /// <summary>Key name for <see cref="StalkActionKind.Momentary"/> — resolved to a
        /// scan code by <c>KeyboardSender</c> (e.g. "Comma", "Period", "H", "F", "K").</summary>
        public string Key { get; set; } = "";

        /// <summary>Target stage index (0-based) for
        /// <see cref="StalkActionKind.WiperStage"/> / <see cref="StalkActionKind.LightStage"/>.</summary>
        public int Stage { get; set; }

        public StalkAction Clone() => new StalkAction { Kind = Kind, Key = Key, Stage = Stage };
    }
}
