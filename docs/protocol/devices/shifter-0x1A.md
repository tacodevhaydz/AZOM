# H-Pattern (HGP) / Sequential (SGP) Shifter — Device `0x1A` / 26

Both passive shifters share internal-bus device id `0x1A` (26). USB PIDs (VID `0x346E`):
**HGP = `0x001E`**, **SGP = `0x0023`** — see [`usb-ids.md`](usb-ids.md). On their own USB-CDC
pipe they answer as the root `main` device `0x12` (nibble-swap `0x21`), exactly like standalone
pedals/handbrake; when attached behind a base/hub they are addressed at bus id `0x1A`.

Command IDs/groups below are verified against `usb-capture/rs21_parameter.db`
(`ShifterGetCfg_*` / `ShifterSetCfg_*`) and cross-checked with the boxflat/foxblat
`serial.yml` table. Settings: read group `0x51` (81) / write group `0x52` (82); output read
group `0x53` (83); calibration write group `0x54` (84). All payloads 2 bytes, big-endian.

The plugin implements these as the `shifter-*` command family
([`Protocol/MozaCommandDatabase.cs`](../../../Protocol/MozaCommandDatabase.cs)); config surface
via the standalone-peripheral lane + a "Shifter" settings tab.

## H-Pattern Shifter (Device `0x1A` / 26)

### Group `0x51` / `0x52` (81 / 82) — Settings

| Command | ID | Bytes | Type | Range | Notes |
|---------|----|-------|------|-------|-------|
| hid-mode | `01` | 2 | int | {0,1} | game-compat mode |
| shifter-type / apply-mode | `02` | 2 | int | {0,1} | DB name `ShifterApplyMode` ("game apply mode") |
| direction | `05` | 2 | int | {0,1} | reverse shift output direction |
| paddle-sync | `06` | 2 | int | {1,2} | wheel-paddle sync (default 1) |

### Group `0x53` (83) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output-x | `01` | 2 | int | |
| output-y | `02` | 2 | int | |

### Group `0x54` (84) — Calibration (write-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| calibration-start | `03` | 2 | int | |
| calibration-stop | `04` | 2 | int | |

---

## Sequential Shifter (Device `0x1A` / 26)

Shares device ID `0x1A` and group numbers with the H-pattern shifter. Distinguish by command IDs or the `shifter-type` setting.

### Group `0x51` / `0x52` (81 / 82) — Settings

| Command | ID | Bytes | Type | Range | Notes |
|---------|----|-------|------|-------|-------|
| hid-mode | `01` | 2 | int | {0,1} | |
| shifter-type / apply-mode | `02` | 2 | int | {0,1} | `ShifterApplyMode` |
| brightness | `03` | 2 | int | [0,10] | LED brightness (default 10) |
| colors | `04` | 2 | array | — | **the 2 LEDs** — see below |
| direction | `05` | 2 | int | {0,1} | |
| paddle-sync | `06` | 2 | int | {1,2} | default 1 |

#### The 2 LEDs (`colors`, cmd `0x04`)

The SGP has **2 RGB LEDs (S1, S2)** set via a single command whose 2-byte payload is
`[S1, S2]` — **each byte is a palette INDEX 0–7, not an RGB triplet** (DB params
`ShifterLedRgbColor_1` / `ShifterLedRgbColor_2`, both int8 `[0,7]`, default 0). They are a
stored/static setting (read back on group `0x51`), *not* a live telemetry stream. Fixed
8-colour palette (index → approximate swatch, matching PitHouse / foxblat `data/style.css`):

| Index | Colour | Hex |
|-------|--------|-----|
| 0 | Red | `#cf2727` |
| 1 | Orange | `#dfa500` |
| 2 | Yellow | `#dfdf3a` |
| 3 | Green | `#3a903a` |
| 4 | Cyan | `#00d0d0` |
| 5 | Blue | `#3a3aff` |
| 6 | Purple | `#802080` |
| 7 | White | `#dddddd` |

### Group `0x53` (83) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output-x (theta) | `01` | 2 | int | raw axis; DB `ShifterGetState_ShifterTheta` |
| output-y | `02` | 2 | int | (boxflat table; not in the local DB) |

## Automatic downshift throttle-blip (HGP) — host-side, NO wire command

The HGP "auto-blip" (blip the throttle on a downshift for rev-matching) has **no MOZA wire
command** — a search of all 919 commands in `rs21_parameter.db` finds nothing blip/handing-shifter
related, and the SDK exposes it only as host-side free functions
(`get/setHandingShifterAutoBlipOutput` 0–100, `…AutoBlipDuration` 0–1000 ms, `…AutoBlipSwitch` 0/1;
see [`../../sdk/api-inventory.md`](../../sdk/api-inventory.md) §3.9, all marked `[G]`/gap). It is
implemented in host software: foxblat detects a single-gear downshift from HID and injects a
synthetic throttle-axis value via Linux evdev for the configured duration. On Windows a SimHub
plugin has no throttle-output path, so this feature is **not implemented** and would require a
virtual-controller (ViGEm/vJoy) approach.
