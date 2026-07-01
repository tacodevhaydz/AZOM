# Moza mBooster Pedals

Vibration-motor pedal product on USB-CDC PID `0x0008`. The mBooster is a
single analog pedal with a built-in vibration motor — the user picks
whether the pedal serves as throttle, brake, or clutch in the plugin
UI, and the motor is driven by host-rendered effects (ABS, Lockup,
Threshold, Engine) per the documented protocol.

**Multi-device:** the plugin supports more than one mBooster on the
same host concurrently (one each for throttle / brake / clutch is the
canonical layout). Each unit gets its own [`MozaSerialConnection`](../../../Protocol/MozaSerialConnection.cs)
under [`MBoosterDeviceController`](../../../Devices/MBoosterDeviceController.cs);
all controllers are owned by [`MozaMBoosterRegistry`](../../../Devices/MozaMBoosterRegistry.cs).

## Reference protocol

The user-supplied protocol note in
[`../../MozamBooster — Protocol Note.md`](../../MozamBooster%20—%20Protocol%20Note.md)
is the authoritative wire-format reference. It includes verified
known-good frames against real hardware captures + the host-side
synthesizer formulas the plugin reproduces verbatim (see
[`MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)).

The plugin-side implementation diverges from the protocol note in only
two ways:

1. **No firmware-version handshake** — the note says there is none.
2. **No probe fallback** — the registry-driven discovery is the only
   path. The serial probe fallback under `MozaProbeTarget.MBooster`
   returns null because mBooster device id `0x12` collides with
   wheelbase Main + AB9 Main, so writing a discovery probe at every
   COM port is high-risk for non-mBooster peripherals.

## USB identification

| Field           | Value                                       |
|-----------------|---------------------------------------------|
| Vendor ID       | `0x346E` (Gudsen / Moza)                    |
| Product ID      | `0x0008`                                    |
| Category        | `MozaDeviceCategory.MBooster`               |
| HID match       | VID+PID (no name regex — see [`MozaHidReader.cs`](../../../Protocol/MozaHidReader.cs)) |
| Baud rate       | 115200                                      |
| Stable identity | USB device instance segment from the registry walk; fallback to the device instance ID surfaced by `HidDevice.DevicePath` — see `MBoosterDeviceController.Identity` |

## Frame shapes

mBooster uses the same Moza wire framing as the wheelbase
(`7E LEN GRP DEV PAYLOAD CHK` with `data_len` excluding group + device
and the wire-aware checksum that compensates for `0x7E` byte stuffing).
[`MozaProtocol`](../../../Protocol/MozaProtocol.cs)'s checksum +
stuffing routines handle all framing.

### Motor write — cmd `0xb1`

Built inline by [`MozaMBoosterProtocol.BuildMotorFrame`](../../../Protocol/MozaMBoosterProtocol.cs).
14 bytes pre-stuffing.

```
7e  09  24  12   b1  EF  EN  00   P1  FH  FL  AH  AL   CK
                 │   │   │   │    │   └─┴─freq u16 BE
                 │   │   │   │    └ param1 (1..255)
                 │   │   │   └ pad (0x00)
                 │   │   └ enable (0 = off, 1 = on)
                 │   └ effect type (1..4)
                 └ cmd id (0xb1)
```

**Effect IDs** (enum [`MBoosterEffectId`](../../../Protocol/MozaMBoosterProtocol.cs)):

| ID  | Name      | ParamK | Trigger condition (host-side, doc § 4) |
|-----|-----------|--------|----------------------------------------|
| `1` | ABS       | 2000   | `absActive > 0.1` from SimHub          |
| `2` | Lockup    | 2640   | Heavy brake (>0.8) + wheels < 30 % of vehicle speed (fallback: brake > 0.9 when wheel speed unavailable) |
| `3` | Threshold | 3080   | Rising edge on brake > 0.6; release at < 0.3 (hysteresis) |
| `4` | Engine    | 1000   | `rpm > 0.8 × idleRpm` — runs continuously |

**Known-good frames** (verified against the protocol note's hardware
captures — diff against these in a `SerialTrafficCapture` export to
confirm wire correctness):

```
ABS on, 22Hz, amp=0x08e8:  7e 09 24 12 b1 01 01 00 5a 1c 28 08 e8 0b
ABS off:                   7e 09 24 12 b1 01 00 00 00 00 00 00 00 7c
Lockup on, 55Hz, ramp 0:   7e 09 24 12 b1 02 01 00 30 46 66 00 00 5a
Lockup off:                7e 09 24 12 b1 02 00 00 00 00 00 00 00 7d
Engine on, 10Hz, amp=0x020c: 7e 09 24 12 b1 04 01 00 64 0c cc 02 0c ca
Engine off:                7e 09 24 12 b1 04 00 00 00 00 00 00 00 7f
```

### Keepalive

Degenerate 0-payload frame targeting device 0x12 — `7e 00 00 12 9d`.
Built by [`MozaMBoosterProtocol.BuildKeepalive`](../../../Protocol/MozaMBoosterProtocol.cs).
Emitted every ~500 ms from `MBoosterEffectWorker` regardless of
effect state. Stops being sent → motor eventually drops connection
state and may stop responding to writes.

**Detection note:** the keepalive and motor frames are write-only —
the device never replies to either. With all effects disabled (the
default for a freshly-detected device) the worker sends nothing else,
so nothing would ever elicit a parseable response for
`MBoosterDeviceController.MarkDetected` to latch onto. `TryConnect`
fires `RequestCalibrationReads()` immediately after the port opens for
exactly this reason — without it the UI sits at "Probing…"
indefinitely until something (previously, only the user manually
clicking "Read from device") prompts a response.

### Disable

Same opcode with `enable = 0` and all params zeroed.
[`MozaMBoosterProtocol.BuildDisableFrame(effect)`](../../../Protocol/MozaMBoosterProtocol.cs).
Sent at every effect deactivation edge AND for all four effect IDs on
controller dispose, otherwise the last-active waveform can latch on
the motor after the port closes.

## Stream lane

All motor frames go through a single per-connection lane:

```
StreamKind.MBoosterEffect = 17
```

Per protocol note § 4 the worker emits at most one motor frame per tick
(~50 Hz), so a single coalesced lane is sufficient — the latest-wins
behaviour on writer lag is the same property the doc relies on when it
says "the motor plays the instantaneous amplitude you send".

Keepalives go via the one-shot FIFO (`MozaSerialConnection.Send`) so
they aren't coalesced.

## Effect synthesis

[`MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)
reproduces protocol note § 4 verbatim:

| Effect    | Waveform                                                    |
|-----------|-------------------------------------------------------------|
| ABS       | `wave = 0.9 + 0.1 * sin(phase); amp = wave * intensity`      |
| Lockup    | `ramp = clamp(elapsed / 0.5, 0, 1); amp = ramp * intensity`  |
| Threshold | 5 Hz envelope: 20 ms full + 120 ms 80 % + 60 ms gap          |
| Engine    | `wave = 0.5 + 0.5 * sin(phase); amp = wave * intensity`      |

Engine intensity is clamped to 10 % at apply time (doc § 4 default
`engineScale = 0.01`, clamped to `[0, 0.1]`) — engine runs
continuously and would dominate the other effects without this cap.

## Calibration surface (experimental)

The protocol note marks the pedal-config command surface (group 35
read, group 36 write) as "likely but unverified" on mBooster firmware.
The plugin ships the full surface in
[`MozaCommandDatabase.cs`](../../../Protocol/MozaCommandDatabase.cs)
under the `mbooster-*` prefix anyway — the user opted in. The UI's
Calibration card (Direction / Min Raw / Max Raw / Read from device /
Apply) surfaces this as experimental with a yellow warning.

## Sim Input Mapping

## Pedal Feel (host-side only)

A card above Sim Input Mapping holds a second 5-point curve,
`InputCurveY` on `MBoosterDeviceSettings`. Unlike `CurveY`, this one has
**no wire command at all** — it's pure host-side shaping, applied in
`MozaMBoosterRegistry.OnHidAxisUpdate` to the raw HID axis position
before it becomes `c.LastHidPosition`, i.e. before it reaches
`MozaData.{Throttle,Brake,Clutch}Position` (game telemetry) *and*
before the effect worker's brake-position test-pulse fallback. `CurveY`
is completely unaffected — it still writes to the device's own
output-curve command exactly as before.

`MozaMBoosterRegistry.EvaluateInputCurve` reproduces
`MozaControls.MozaCurveEditor`'s Catmull-Rom rendering exactly (same
1/6-tangent formula, anchored at the origin), inverted via bisection to
solve X(t)=x for the requested input X — so the applied shaping always
matches what's drawn on screen. Verified: the Linear preset is an exact
identity function (not just at the 5 breakpoints), and the S-Curve
preset interpolates smoothly through all 5 breakpoints. `null` (the
default) means no shaping — existing profiles are unaffected until a
user opens this section.

The same card also has a **Start/End of Travel (mm)** control —
`TravelStartMm`/`TravelEndMm` on `MBoosterDeviceSettings`, applied in
`MozaMBoosterRegistry.ApplyTravelRangeMm`, which runs *first* in the
pipeline (physical travel bounds are the most fundamental sensor
characteristic — the load cell/pedal's actual usable stroke — so
everything else shapes what's left of the signal, not the raw one).
This is a genuine dual-thumb range slider (`MozaControls.MozaRangeSlider`,
`UI/Controls/MozaRangeSlider.cs`) — no dual-thumb control existed
anywhere in this app before; every other "linked min/max" pair
(Handbrake, Throttle, Brake, Clutch, mBooster's own raw Min/Max
calibration) is two separate `Slider` controls with mutual clamping
via the shared `OnMinMaxSliderChanged` helper. The two thumbs
(`LowValue`/`HighValue`) are bounded to `[3.8mm, 49.7mm]`
(`MBoosterUiConstants.TravelMinMm`/`TravelMaxMm`) and clamped against
each other so their gap always stays within `[4mm, 32.5mm]`
(`TravelMinGapMm`/`TravelMaxGapMm`) — dragging one thumb simply can't
push the gap outside that range. Defaults anchor at the slider's
minimum with the maximum allowed gap (`3.8` / `36.3`) so a fresh
profile starts with the widest usable window. Like the kg-based
controls below, raw 0–100% travel is treated as spanning the full
`[TravelMinMm, TravelMaxMm]` range (no independent mm calibration
exists on the raw HID axis either), clipped to `[TravelStartMm,
TravelEndMm]` and rescaled back to 0–100%.

The same card also has two force-based sliders, both host-side only and
both applied in `MozaMBoosterRegistry.ApplyDeadzoneAndMaxForce`, which
runs *after* Start/End of Travel and *before* `EvaluateInputCurve`:

- **Deadzone** (`DeadzoneKg`, 0–40kg, default 0 = off) — force below
  this clamps to 0.
- **Max Force** (`MaxForceKg`, 0–200kg, default 200 = off) — the force
  at which the *input curve's* X-axis reaches 100%. Lets a user who
  never presses past, say, 100kg use the curve's full 0–100% range
  instead of only ever reaching its midpoint.

All three clip-and-rescale stages (mm, then kg, then the input curve)
share one `ClipAndRescale(xPercent, loPercent, hiPercent)` primitive —
`ApplyTravelRangeMm` and `ApplyDeadzoneAndMaxForce` just convert their
own units to percent first, then call it. Verified numerically:
`ApplyDeadzoneAndMaxForce`'s behavior is byte-for-byte unchanged after
this refactor (defaults are an exact identity; `maxForce=100kg` still
makes raw 50% map to exactly 100%), and the mm version behaves the
same shape with the new unit.

Both are combined into one kg-space remap rather than two independent
percent-space steps: raw 0–100% travel is first treated as 0–200kg
(the raw HID axis has no independent force calibration of its own —
same assumption `MaxThresholdKg`/`EncodeThresholdKg` makes), force
below the deadzone clamps to 0, and everything between the deadzone
and Max Force rescales linearly to 0–100%. This is stated plainly in
the UI hint (`Hint_DeadzoneScaleAssumption`) since it's an assumption,
not a measurement. Verified numerically: with defaults (dz=0,
maxForce=200) the remap is an exact identity; with maxForce=100kg, raw
50% (=100kg on the fixed scale) maps to exactly 100%.

### Live position indicator on the curves

Both the Pedal Feel input curve and the Sim Input Mapping output curve
show a live dot on the spline (plus a dashed guide line down to the
X axis) tracking the pedal as it's pressed, at 30Hz
(`SettingsControl.UpdateMBoosterCurveMarkers`, which sets
`MozaCurveEditor.LiveX` on both editors — this used to also drive a
standalone position bar in the Pedal Role card, since removed in favor
of these two curve markers).

`MozaCurveEditor.LiveX` is a data-space X (0–100, same domain as
`XAxisLabels`); `NaN` (default) hides the indicator. `Recompute()`
maps it to a pixel X via the same `XAxisLabels`/`XLabelFractions`
correspondence used for tick labels (linear interpolation between
whichever two labels bracket it), locates which cached Bezier segment
contains that pixel X, then inverts that segment's X(t) via bisection
— the same approach as `EvaluateInputCurve`, just in pixel space — to
read off the exact point ON the spline. The two editors get different
values so each shows what it actually receives:

- **Input Curve**: `LastRawPercentPreCurve` — post deadzone/max-force,
  pre-`InputCurveY` (what this curve's evaluator receives).
- **Output Curve**: `LastHidPosition * 100` (post-`InputCurveY`, i.e.
  what's sent onward to game telemetry) — an approximation of what the
  device's own firmware curve sees, since that runs on the device's
  own raw sensor reading, a separate signal path we don't otherwise
  observe.

A separate card (Pit House calls this class of setting "input
mapping") holds the Pit House-parity controls, all still under
`MBoosterDeviceSettings`:

- **Sensor Output Ratio** (`SensorOutputRatioPct`, 0–100%) — blend
  between the mBooster's angle sensor (0%) and its load cell (100%).
  Wired to `mbooster-brake-angle-ratio` (cmdId 26) — the mBooster-side
  twin of the wheelbase Brake tab's own "Sensor Ratio" slider
  (`pedals-brake-angle-ratio`). Live-pushes on every drag.
- **Max Threshold (kg)** (`MaxThresholdKg`) — Pit House's "load cell
  force at which output reaches 100%" setting. **Reverse-engineered
  from two real Pit House USB captures** (not in any protocol note —
  see below). Wire command `mbooster-brake-threshold`, cmdId `0xB3`,
  group 35 read / 36 write, 4 bytes — but unlike every other 4-byte
  mbooster command, this one is a **big-endian unsigned int, not an
  IEEE-754 float**. It encodes kg on a fixed 0–200kg scale over the
  same 0–65535 range used elsewhere, mirroring the exact
  `EncodeFreq`/`ComputeParam1` "value × 65536 / 200" pattern already
  used for the motor effects in this same file:
  `raw = round(kg × 65536 / 200)`. See
  `MozaMBoosterProtocol.EncodeThresholdKg`/`DecodeThresholdKg`.

  **Evidence**: a capture isolating a drag to exactly 4kg produced
  `7e 05 24 12 b3 00 00 05 1f 61` → raw `1311`, and
  `round(4 × 65536 / 200) = 1311` exactly. A second, earlier capture
  (target value not recorded) produced raw `41287`, which decodes to
  `125.9998kg` — matching an independently-reported real Pit House
  setting of ~125kg to within rounding error. Two independent
  confirmations is about as solid as unofficial reverse-engineering
  gets, but it's still unconfirmed by Moza — the in-UI warning says so.
- **Output curve** (`CurveY`, 5-point) — moved here from Calibration.
  `MozaCurveEditor`-driven, mirrors the wheelbase pedal Y curves.
  Like Direction/Min/Max, always writes through the
  `mbooster-throttle-y1..y5` slot regardless of the device's assigned
  role — the mBooster is a single physical axis, so role-specific
  slots are reserved for symmetry with the wheelbase's three-pedal
  command surface but unlikely to matter on real hardware (see
  `SetMBoosterCurveY` in `SettingsControl.xaml.cs`).

  Unlike every other curve in the app, this one's nodes are also
  **draggable horizontally** (`MozaCurveEditor.AllowHorizontalDrag`,
  set only on this instance), so a node can be moved to a lower X and
  "100% output" reached before "100% input" — the same idea as Pedal
  Feel's Max Force slider, applied to the output side instead. The
  wheelbase's own FFB curve has real `base-ffb-curve-x1..x4` write
  commands for this; **no equivalent exists for the mbooster y-curve**
  (nothing found in captures or the command table), so this is
  implemented purely host-side: `MBoosterDeviceSettings.CurveX` stores
  each node's dragged X (null = untouched, fixed 20/40/60/80/100), and
  `MozaMBoosterRegistry.ResampleCurveAtFixedBreakpoints` — via the new
  `EvaluateCurveArbitraryX`, the same Catmull-Rom/bisection approach as
  `EvaluateInputCurve` but generalized to non-fixed node X — resamples
  the whole (CurveX, CurveY) shape at the wire protocol's actual fixed
  breakpoints before every push (`PushResampledMBoosterCurve` in
  `SettingsControl.xaml.cs`, called on every X or Y change, plus
  `ApplyMBoosterToHardware` on detect). Beyond the last dragged node,
  the resample returns that node's Y (flat plateau) — verified
  numerically: with `CurveX` untouched, resampling is the exact
  identity; dragging the last node from X=100 to X=60 (Y unchanged)
  makes breakpoints 60/80/100 all resample to that node's Y.

| Command                       | Group (R/W) | CmdId | Bytes | Type  |
|-------------------------------|-------------|-------|-------|-------|
| `mbooster-throttle-dir/min/max` | 35 / 36   | 1/2/3 | 2     | int   |
| `mbooster-brake-dir/min/max`    | 35 / 36   | 4/5/6 | 2     | int   |
| `mbooster-clutch-dir/min/max`   | 35 / 36   | 7/8/9 | 2     | int   |
| `mbooster-{throttle,brake,clutch}-y1..y5` | 35 / 36 | 14-29 | 4 | float |
| `mbooster-{throttle,brake,clutch}-output` | 37 / —  | 1/2/3 | 2 | int   |
| `mbooster-brake-angle-ratio`    | 35 / 36   | 26    | 4     | float |

All targeted at device id `0x12` on the mBooster's own CDC port. The
plugin's [`MozaResponseParser`](../../../Protocol/MozaResponseParser.cs)
disambiguates from wheelbase Main / AB9 Main via the
`busHint: "mbooster"` argument set in `MBoosterDeviceController.OnConnectionMessage`.

## HID identity reconciliation

The registry walk in [`MozaPortDiscovery`](../../../Protocol/MozaPortDiscovery.cs)
surfaces an `InstanceId` per CDC composite (e.g. `a&399b951f&0&0000`).
HidSharp's `HidDevice.DevicePath` contains a similar parent-USB
instance segment between the second and third `#` separators (e.g.
`\\?\HID#VID_346E&PID_0008&MI_02#a&399b951f&0&0002#{4d1e55b2-...}`).
The plugin's `MozaHidReader.ExtractUsbParentInstance` extracts that
segment as the HID-side identity.

**Resolved by real-hardware logs** (a support bundle showing the
position bar stuck at 0 despite the device showing "Connected"): the
"shared prefix, differing only in trailing interface index" theory
above is **wrong**. A real capture showed:

```
HID: 9&1bd82a3a&0&0000
CDC: 8&1709245b&0&0000
```

No shared segment at all — Windows assigned the HID and CDC
interfaces of the same physical device completely unrelated instance
IDs (different hash, not just a different trailing index). An
exact-match lookup in `MozaMBoosterRegistry.OnHidAxisUpdate` never
pairs these, so the position bar never updates even though the CDC
side detects fine, and no amount of prefix-stripping can fix it for
this device.

`MozaMBoosterRegistry.OnHidAxisUpdate` tries three things in order:

1. **Exact match** — works if Windows ever does assign the same
   instance ID to both interfaces (kept for hardware/driver versions
   where it might).
2. **`FindByInstancePrefixLocked`** — strips the trailing `&NNNN`
   segment from both sides and matches on the remainder. Kept as a
   fallback for the case the original theory *did* describe, even
   though it's now known not to be the common case.
3. **Single-device fallback** — if neither match and exactly one
   mBooster is registered, pair the HID identity to it unconditionally;
   there's no ambiguity with only one device. This is what actually
   fixes the common single-mBooster case given the finding above.

Each path logs once per HID identity at Info level so a support-bundle
log confirms which one resolved the device (`"...via instance-prefix
fallback..."` / `"...via single-device fallback..."`). With two or
more mBoosters that never exact- or prefix-match, there is currently
no way to disambiguate which HID stream belongs to which CDC device —
`LogUnmatchedHidIdentityOnceLocked` logs a Warn (visible in SimHub's
regular log, not just the bundle) so that gap is at least visible
rather than silent.

## Source-of-truth files in this repo

- Protocol primitives — [`Protocol/MozaMBoosterProtocol.cs`](../../../Protocol/MozaMBoosterProtocol.cs)
- Effect synthesis — [`Devices/MBoosterEffectSynthesizer.cs`](../../../Devices/MBoosterEffectSynthesizer.cs)
- Settings types — [`Devices/MBoosterTypes.cs`](../../../Devices/MBoosterTypes.cs)
- Per-device controller — [`Devices/MBoosterDeviceController.cs`](../../../Devices/MBoosterDeviceController.cs)
- 50 Hz effect worker — [`Devices/MBoosterEffectWorker.cs`](../../../Devices/MBoosterEffectWorker.cs)
- Multi-device registry — [`Devices/MozaMBoosterRegistry.cs`](../../../Devices/MozaMBoosterRegistry.cs)
- HID extension — [`Protocol/MozaHidReader.cs`](../../../Protocol/MozaHidReader.cs) (`MozaHidClass.MBooster` path)
- Profile storage — [`UI/MozaProfile.cs`](../../../UI/MozaProfile.cs) (`MBoosterSettings` dict)
- UI tab — [`UI/SettingsControl.xaml`](../../../UI/SettingsControl.xaml) (`MBoosterTab`) + handlers in `SettingsControl.xaml.cs` under "mBooster tab — multi-device"
