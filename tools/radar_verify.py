#!/usr/bin/env python3
"""Verify the MOZA radar ri formula against a same-session PitHouse capture.

The plugin encodes each opponent's signed WORLD relZ gap (oppZ - playerZ, metres)
into the low 20 bits of a 32-bit ri slot:

    low20 = (RadarZCenter + round(RadarZScale * relZ)) mod 2^20
    field = RadarZHigh | low20

This tool decodes the ri slots from a real PitHouse <-> wheel capture (jsonl made
by extract.py), aligns each radar frame to the same-session SimHub replay by lap
time, carId-matches each ri slot to the replay's opponent, and checks that the
decoded relZ reproduces the replay's true (oppZ - playerZ). It both:

  * scores the SHIPPED constants (residual RMS, R^2) — reproduces the handoff's
    held-out 0.24 m (radar4) / 0.11 m (radar6) numbers, and
  * independently re-fits (center, scale) by least squares over near-range
    (non-wrapping) points, so the constants are recovered from the bytes, not
    assumed.

Usage:
    python tools/radar_verify.py <capture.jsonl> <replay-stem>
      capture.jsonl : extract.py output, e.g. .../scratchpad/radar6.jsonl
      replay-stem   : SimHub replay path WITHOUT extension, e.g.
                      ".../SimHub/Replays/AssettoCorsa/20260626_144337"
                      (reads <stem>.telemetry.json + <stem>.telemetry.jsonidx)

Optional:
    --tol <s>     max |lapTime| gap (s) to accept a frame alignment (default 0.05)
    --near <m>    near-range half-window (m) for the wrap-free fit (default 20)
    --range <m>   ignore opponents farther than this (matches plugin gate; default 150)
"""
from __future__ import annotations
import json, struct, zlib, sys, argparse, bisect
from pathlib import Path

# --- shipped constants (mirror TelemetryFrameBuilder.cs) ---------------------
RADAR_MAGIC   = 0x1687FDFF
RADAR_Z_HIGH  = 0x16700000
RADAR_Z_CENTER = 0x80000      # 2^19
RADAR_Z_SCALE  = 21630.0
MASK20 = 0xFFFFF
# Full-field (UNMASKED) model under test: field = base + scale*relZ, no 2^20 wrap.
# base = (0x167 << 20) | 0x80000 = the field value at relZ = 0.
RADAR_Z_BASE = (RADAR_Z_HIGH | RADAR_Z_CENTER)   # 0x16780000


def bget(d, s, n):
    """Read n bits LSB-first from bit offset s of bytes d."""
    v = 0
    for i in range(n):
        bit = s + i
        bi = bit >> 3
        if bi < len(d):
            v |= ((d[bi] >> (bit & 7)) & 1) << i
    return v


def ri_relZ_shipped(field):
    """Decode with the shipped MASKED formula (low 20 bits, wraps at 2^20)."""
    return ((field & MASK20) - RADAR_Z_CENTER) / RADAR_Z_SCALE


def ri_relZ_full(field):
    """Decode with the UNMASKED full-field formula (no wrap)."""
    return (field - RADAR_Z_BASE) / RADAR_Z_SCALE


def radar_frames(path):
    """Yield decoded radar-tier frames from a capture jsonl.

    Each -> dict(t, lap, plenN, ri={slot:field}). Only h2b group-0x43 value
    frames whose ri0 == magic are radar-tier frames.
    """
    out = []
    with open(path) as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            o = json.loads(line)
            if o.get('dir') != 'h2b':
                continue
            b = bytes.fromhex(o['hex'])
            if len(b) < 6 or b[2] != 0x43:
                continue
            pl = b[4:-1]
            if len(pl) < 8 or pl[0] != 0x7d or pl[1] != 0x23:
                continue
            d = pl[8:]
            total_bits = len(d) * 8
            if total_bits < 131 + 32:
                continue
            if bget(d, 131, 32) != RADAR_MAGIC:
                continue
            lap = struct.unpack_from('<f', d, 0)[0]   # CurrentLapTime, seconds
            n_ri = (total_bits - 131) // 32           # ri0..ri(n_ri-1)
            ri = {}
            for k in range(1, n_ri):                  # ri0 is the magic header
                ri[k] = bget(d, 131 + 32 * k, 32)
            out.append({'t': o['t'], 'lap': lap, 'nri': n_ri, 'ri': ri,
                        'plen': len(pl)})
    return out


def load_replay(stem):
    """Return list of frames: dict(lap, px, pz, cars={carId:(x,z)}, player_cid).

    lap = lap time in seconds (iCurrentTime/1000). px/pz = player world X/Z from
    CarCoordinates. cars = every vehicle's world (x,z) keyed by carId. player_cid
    = the carId whose worldPosition matches CarCoordinates (the slot to skip).
    """
    tj = Path(stem + '.telemetry.json').read_bytes()
    ir = Path(stem + '.telemetry.jsonidx').read_bytes()
    offs = []
    p = 5
    while p + 25 <= len(ir):
        if ir[p] == 1:
            offs.append(struct.unpack_from('<I', ir, p + 1)[0])
        p += 25
    frames = []
    for i in range(len(offs)):
        o0 = offs[i]
        o1 = offs[i + 1] if i + 1 < len(offs) else len(tj)
        try:
            f = json.loads(zlib.decompress(tj[o0:o1][5:], -15))
        except Exception:
            continue
        g = f.get('Graphics') or {}
        cc = g.get('CarCoordinates')
        if not cc or len(cc) < 3:
            continue
        px, pz = float(cc[0]), float(cc[2])
        lap = float(g.get('iCurrentTime', 0)) / 1000.0
        veh = ((f.get('Opponents') or {}).get('vehicle')) or []
        cars = {}
        player_cid = None
        best = 1e9
        for v in veh:
            cid = v.get('carId')
            wp = v.get('worldPosition') or {}
            if cid is None or 'x' not in wp:
                continue
            x, z = float(wp['x']), float(wp['z'])
            cars[cid] = (x, z)
            dd = (x - px) ** 2 + (z - pz) ** 2
            if dd < best:
                best = dd
                player_cid = cid
        frames.append({'lap': lap, 'px': px, 'pz': pz, 'cars': cars,
                       'player_cid': player_cid})
    return frames


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('capture')
    ap.add_argument('replay_stem')
    ap.add_argument('--tol', type=float, default=0.05)
    ap.add_argument('--near', type=float, default=20.0)
    ap.add_argument('--range', type=float, default=150.0, dest='rng')
    args = ap.parse_args()

    rf = radar_frames(args.capture)
    print(f"radar-tier frames in capture: {len(rf)}", file=sys.stderr)
    if not rf:
        print("no radar-tier frames found", file=sys.stderr)
        sys.exit(2)
    tier_sizes = {}
    for f in rf:
        tier_sizes[f['plen']] = tier_sizes.get(f['plen'], 0) + 1
    print(f"payload-len histogram (plen:count): {dict(sorted(tier_sizes.items()))}",
          file=sys.stderr)

    rep = load_replay(args.replay_stem)
    print(f"replay frames: {len(rep)}", file=sys.stderr)

    # Index replay frames by lap time for nearest-match alignment. Lap time can
    # reset across a start/finish line, so multiple replay frames may share a
    # value; bisect to the closest and accept only within --tol.
    rep_sorted = sorted(rep, key=lambda r: r['lap'])
    rep_laps = [r['lap'] for r in rep_sorted]

    def match(lap):
        j = bisect.bisect_left(rep_laps, lap)
        best = None
        bestd = args.tol
        for jj in (j - 1, j, j + 1):
            if 0 <= jj < len(rep_sorted):
                dd = abs(rep_laps[jj] - lap)
                if dd <= bestd:
                    bestd = dd
                    best = rep_sorted[jj]
        return best

    # Collect matched (relZ_expected, low20, ri_relZ_shipped, range_ok).
    samples = []     # (relZ_true, low20, k, carId-presence)
    n_frames_matched = 0
    n_slot_total = 0
    n_slot_nonzero = 0
    n_slot_nocar = 0
    for f in rf:
        m = match(f['lap'])
        if m is None:
            continue
        n_frames_matched += 1
        pz = m['pz']
        for k, field in f['ri'].items():
            n_slot_total += 1
            if field == 0:
                continue
            n_slot_nonzero += 1
            if k == m['player_cid']:
                continue                      # player's own slot (skipped by plugin)
            car = m['cars'].get(k)            # ri slot k == carId k
            if car is None:
                n_slot_nocar += 1
                continue
            relZ_true = car[1] - pz
            samples.append((relZ_true, field, k))   # keep the FULL field

    print(f"frames matched within tol: {n_frames_matched}/{len(rf)}", file=sys.stderr)
    print(f"ri slots: total={n_slot_total} nonzero={n_slot_nonzero} "
          f"no-car={n_slot_nocar} usable={len(samples)}", file=sys.stderr)
    if not samples:
        print("no usable samples — check alignment/tol", file=sys.stderr)
        sys.exit(3)

    # --- score the shipped constants over near-range (wrap-free) points -------
    near = [s for s in samples if abs(s[0]) <= args.near]
    in_rng = [s for s in samples if abs(s[0]) <= args.rng]
    print(f"\nsamples: usable={len(samples)} near(|relZ|<={args.near}m)={len(near)} "
          f"in-range(<={args.rng}m)={len(in_rng)}")

    def stats(pts, decode):
        # residual of `decode(field)` vs truth, in metres
        res = [decode(field) - relZ for (relZ, field, _k) in pts]
        n = len(res)
        if n == 0:
            return None
        mean = sum(res) / n
        rms = (sum(r * r for r in res) / n) ** 0.5
        mae = sum(abs(r) for r in res) / n
        # R^2 of decoded-relZ vs true-relZ
        ys = [relZ for (relZ, _f, _k) in pts]
        yh = [decode(field) for (_r, field, _k) in pts]
        ybar = sum(ys) / n
        ss_tot = sum((y - ybar) ** 2 for y in ys)
        ss_res = sum((y - h) ** 2 for y, h in zip(ys, yh))
        r2 = 1 - ss_res / ss_tot if ss_tot > 0 else float('nan')
        return n, mean, rms, mae, r2

    for model, decode in (("SHIPPED masked", ri_relZ_shipped),
                          ("UNMASKED full", ri_relZ_full)):
        print(f"\n--- {model} decode ---")
        for label, pts in (("near-range", near), ("in-range", in_rng), ("all", samples)):
            st = stats(pts, decode)
            if st:
                n, mean, rms, mae, r2 = st
                print(f"  [{label:>10}] n={n:5d}  bias={mean:+.3f}m  RMS={rms:.3f}m  "
                      f"MAE={mae:.3f}m  R^2={r2:.4f}")

    # --- independent least-squares refits --------------------------------
    # y = a + b*relZ  ->  b ~ scale (units/m), a ~ base (field value at relZ=0).
    def fit(pts, yfunc, label, base_ref):
        n = len(pts)
        if n < 2:
            return
        xs = [p[0] for p in pts]
        ys = [yfunc(p[1]) for p in pts]
        sx = sum(xs); sy = sum(ys)
        sxx = sum(x * x for x in xs); sxy = sum(x * y for x, y in zip(xs, ys))
        denom = n * sxx - sx * sx
        if denom == 0:
            return
        b = (n * sxy - sx * sy) / denom
        a = (sy - b * sx) / n
        ybar = sy / n
        ss_tot = sum((y - ybar) ** 2 for y in ys)
        ss_res = sum((y - (a + b * x)) ** 2 for x, y in zip(xs, ys))
        r2 = 1 - ss_res / ss_tot if ss_tot > 0 else float('nan')
        print(f"\nleast-squares refit [{label}] (n={n}):")
        print(f"  fitted scale = {b:9.2f}   (shipped {RADAR_Z_SCALE:.1f})")
        print(f"  fitted base  = {a:11.2f} = 0x{int(round(a)) & 0xFFFFFFFF:08X}   "
              f"(ref {base_ref} = 0x{base_ref & 0xFFFFFFFF:08X})")
        print(f"  fit R^2      = {r2:.6f}")

    # Masked model, near-range only (the wrap-free window): center fit.
    fit(near, lambda f: f & MASK20, "MASKED low20 ~ center+scale*relZ (near-range)",
        RADAR_Z_CENTER)
    # Unmasked model, ALL samples: full-field base fit — the decisive test.
    fit(samples, lambda f: f, "UNMASKED full-field ~ base+scale*relZ (ALL)",
        RADAR_Z_BASE)

    # --- high-12-bits sanity (should be constant 0x167) ----------------------
    highs = {}
    for f in rf:
        for k, field in f['ri'].items():
            if field == 0:
                continue
            h = (field >> 20) & 0xFFF
            highs[h] = highs.get(h, 0) + 1
    print(f"\nhigh-12-bit histogram (expect 0x167 dominant): "
          f"{{ {', '.join(f'0x{h:03X}:{c}' for h, c in sorted(highs.items(), key=lambda x:-x[1])[:6])} }}")


if __name__ == '__main__':
    main()
