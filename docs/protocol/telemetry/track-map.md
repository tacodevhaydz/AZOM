# Track-map & radar channels (`patch/Location*`, `patch/ri*`)

How PitHouse encodes the MOZA "track map" (everyone's dots on a mini circuit
map) and "radar" (close-proximity spotter) channels. Reverse-engineered from
paired captures: a USBPcap recording of PitHouse↔wheel serial traffic during an
Assetto Corsa session, correlated against a **parallel SimHub telemetry replay**
of the same session (the replay is the game-side ground truth — see
[Method](#method)). Verified to <0.3 m on two tracks (Imola, Spa).

## Channel split

| Feature | Channels | Count | Indexing | Meaning |
|---|---|---|---|---|
| **Track map** | `patch/Location`, `patch/Location_0..63` | 1 + 64 | static — `Location_N` = car index `N` (player at `PlayerIndex`) | **absolute** world position of every car |
| **Radar** | `patch/ri0..ri63` (8 subscribed) | up to 64 | static — `ri_N` = car index `N` ("Player N+1 Radar Position") | **pre-computed radar-screen** position of nearby cars |

Both are gated behind `EnableRadarTrackMapChannels`; `OpponentCount` /
`PlayerIndex` are not (ordinary dashboards read them). The wheel masks unused
`Location_N` / `ri_N` slots via `OpponentCount`.

## Track map — `location_t` (64-bit) — SOLVED

`location_t` is advertised in the tier-def as **compression code `0x09`, bit
width 64** (confirmed from the cold-start tier-def capture; the wheel packs it
as the last field of each per-car tier). The 64 bits are **packed fixed-point
integers — NOT two `float32`** (the earlier guess). Little-endian byte layout:

| Bytes | Field | Width | Encoding |
|---|---|---|---|
| `[0:2]` | **Y** (elevation) | u16 LE | `clamp(centerY + round(S·y))` |
| `[2:5]` | **Z** (world z) | u24 LE | `clamp(centerZ + round(16·S·z))` |
| `[5:8]` | **X** (world x) | u24 LE | `clamp(centerX + round(S·x))` |

It is provably integer, not float: regressing world x/z against the raw bytes is
linear (R²=1.0) **across x=0**, where a float's sign bit would flip and break
linearity. The high byte of each field also ramps smoothly (a float exponent
would not).

### Scale & offset are track-specific (and the wheel auto-fits)

The per-axis scale `S` and the centre offsets differ by track and are **not**
derivable from spline length or AC's `map.ini` (checked: `S/SCALE_FACTOR` is
459.8 for Imola vs 379.0 for Spa — not constant):

| Track | S (X & Y) | Z scale | Z/X ratio |
|---|---|---|---|
| Imola (spline 4864 m) | 551.7 /m | 8815 /m | 15.98 |
| Spa (spline 6946 m) | 492.7 /m | 7881 /m | 16.00 |

Two robust facts hold on both tracks:

- **X and Y share one scale `S`; Z always uses `16·S`** (exact).
- The track occupies only a small, off-centre slice of the field (PitHouse's
  values sit around 4.3–4.9 M of the 16.7 M u24 range).

Because the track fills <5 % of the field off-centre, **and** Z is sent 16×
finer than X (a 1:1 renderer would squash the map 16:1), the wheel's map widget
**must auto-normalise each axis independently** for display. Therefore the
absolute scale/offset are **not load-bearing** — any consistent linear
projection in the right packed layout renders correctly. The plugin
(`TelemetryFrameBuilder.WritePackedLocation`) uses fixed `S=128`, `Z=2048`,
centres `2^23` (X/Z) and `2^15` (Y) — chosen so any track (±several km) stays in
range — and preserves PitHouse's 16× Z ratio. Confirm on hardware; tune the
`MapScale*` constants if the map looks mis-sized.

### Empty slots

An absent car (no position, or a slot past `OpponentCount`) is sent as **all
zero** (8 zero bytes). The plugin emits the same and treats `X==0 && Z==0` (and
any non-finite coordinate) as the empty marker.

## Radar — `patch/ri*` (32-bit `uint32_t`) — NOT yet solved for AC

`ri_N` is a 32-bit slot ("Player N+1 Radar Position"). It is a **pre-computed
radar-SCREEN position**, not a raw relative coordinate, so it does not decode as
a linear/rotated/polar function of the car's relative position (all such fits
cap at R² ≤ 0.5 against AC ground truth). Observed behaviour, consistent with a
proximity spotter UI:

- `ri0` is constant — the local player (PlayerIndex 0) pinned at the radar centre.
- Cars beyond a "close" radius **hover at the display edge** (one component
  sticks near a constant ~5800, the other clamps near int16 max) and only move
  linearly toward centre once inside the close radius.

So `ri` carries a 2-zone (edge-hover + linear close-zone) clamped screen
projection. Cracking the exact scale/clamp needs a capture with **sustained
side-by-side racing** (cars actually inside the close radius for a stretch) — the
current captures have cars mostly at the edge (clamped), giving no clean linear
signal. It is also possible PitHouse cannot populate `ri` meaningfully for AC
(AC shared memory exposes no relative/radar data) and only does so for sims that
expose it (ACC, iRacing); an ACC/iRacing capture would settle that.

The plugin's `WriteRadarPair` (2× int16 × 100) is a placeholder pending this.

## Method

Tooling lives under `tools/` + scratch (not committed):

1. **pcapng → frames**: extract USBPcap bulk transfers, 0x7E-destuff both
   directions, emit `moza_trace`-format JSONL (so `tierdef-decode` /
   `catalog-decode` run on it). The `tools/pcap_to_jsonl.py` extractor depends on
   `extract_moza_frames.py` (now in the moza-simulator repo); reconstruct it if
   absent. On Windows set `PYTHONUTF8=1` (the tools read the CJK-bearing
   `Telemetry.json`).
2. **SimHub replay → ground truth**: a `.telemetry.json` replay is `FF01` +
   per-frame `[01][u32 len][raw-deflate]`, indexed by `.jsonidx`
   (25-byte records: `01 [u24 offset] …`). Each frame inflates to the full AC
   shared-memory JSON (`Graphics.CarCoordinates`, `Opponents.vehicle[].worldPosition`).
3. **Correlate**: align capture↔replay by wall-clock UTC (same machine; refine
   by motion cross-correlation), then regress decoded wire fields against known
   world positions. Use the 8 opponents (varied positions) to decorrelate x/z —
   a single player path can't separate the axes.
