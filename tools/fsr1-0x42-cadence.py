#!/usr/bin/env python3
"""Measure PitHouse's group-0x42 display-push CADENCE per record type from a
JSONL capture (produced by tools/pcap_to_jsonl.py).

Answers: at what rate does PitHouse stream each 0x42 record type, and on the
dual-record GT page (types 0x11 + 0x12) how are the two interleaved/spaced?
This is the ground truth for the plugin's Fsr1DisplayDriver tick cadence.

Usage:
  python3 tools/fsr1-0x42-cadence.py /tmp/fsr1cad/gt.jsonl
  python3 tools/fsr1-0x42-cadence.py /tmp/fsr1cad/gt.jsonl --types 11,12 --window 5
"""
import argparse
import json
import statistics
from pathlib import Path

WHEEL = 0x17  # host->wheel device id for the display push


def frames_0x42(path):
    """Yield (t, type_byte) for each host->0x17 group-0x42 frame, in order."""
    for line in Path(path).open():
        rec = json.loads(line)
        if rec.get("dir") != "h2b":
            continue
        h = bytes.fromhex(rec["hex"])
        if len(h) < 6 or h[0] != 0x7E or h[2] != 0x42 or h[3] != WHEEL:
            continue
        yield rec["t"], h[4]


def fmt_ms(s):
    return f"{s*1000:.1f}ms"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jsonl")
    ap.add_argument("--types", default="", help="comma hex list to isolate, e.g. 11,12")
    ap.add_argument("--window", type=float, default=0.0,
                    help="restrict to the densest N-second window of the chosen types")
    args = ap.parse_args()

    rows = list(frames_0x42(args.jsonl))
    if not rows:
        print("no 0x42 frames")
        return
    t0 = rows[0][0]
    span = rows[-1][0] - t0
    print(f"{Path(args.jsonl).name}: {len(rows)} 0x42 frames over {span:.1f}s "
          f"= {len(rows)/span:.1f} frames/s overall\n")

    # per-type histogram + rate
    counts = {}
    for _, ty in rows:
        counts[ty] = counts.get(ty, 0) + 1
    print("record-type histogram (whole capture):")
    for ty in sorted(counts):
        print(f"  0x{ty:02x}: {counts[ty]:6d}  ({counts[ty]/span:.1f}/s avg)")

    sel = [int(x, 16) for x in args.types.split(",") if x.strip()] if args.types else None
    if not sel:
        return

    seq = [(t, ty) for t, ty in rows if ty in sel]
    if len(seq) < 3:
        print(f"\nonly {len(seq)} frames for types {args.types}")
        return

    # Optionally zoom to the densest contiguous window (skips page-switch gaps).
    if args.window > 0:
        best_i, best_n = 0, 0
        j = 0
        for i in range(len(seq)):
            while j < len(seq) and seq[j][0] - seq[i][0] <= args.window:
                j += 1
            if j - i > best_n:
                best_n, best_i = j - i, i
        end = best_i
        while end < len(seq) and seq[end][0] - seq[best_i][0] <= args.window:
            end += 1
        seq = seq[best_i:end]

    d = seq[-1][0] - seq[0][0]
    print(f"\n== types {args.types}: {len(seq)} frames over {d:.2f}s "
          f"= {len(seq)/d:.1f} frames/s total ==")
    for ty in sel:
        n = sum(1 for _, t in seq if t == ty)
        print(f"  0x{ty:02x}: {n} frames = {n/d:.1f}/s")

    # all-type inter-frame gaps (the wire cadence the wheel sees)
    gaps = [seq[i+1][0] - seq[i][0] for i in range(len(seq)-1)]
    gaps_ms = sorted(g*1000 for g in gaps)
    print(f"\ninter-frame gap (any type): median={statistics.median(gaps_ms):.1f}ms "
          f"mean={statistics.mean(gaps_ms):.1f}ms "
          f"p10={gaps_ms[len(gaps_ms)//10]:.1f} p90={gaps_ms[len(gaps_ms)*9//10]:.1f}")

    # per-type self-gap (how often each individual record refreshes)
    for ty in sel:
        ts = [t for t, t2 in seq if t2 == ty]
        g = sorted((ts[i+1]-ts[i])*1000 for i in range(len(ts)-1))
        if g:
            print(f"  0x{ty:02x} self-refresh: median={statistics.median(g):.1f}ms "
                  f"= {1000/statistics.median(g):.1f}Hz")

    # interleave pattern (first 50)
    print("\ninterleave (first 50): " + " ".join(f"{t:02x}" for _, t in seq[:50]))


if __name__ == "__main__":
    main()
