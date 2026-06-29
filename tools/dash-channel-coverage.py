#!/usr/bin/env python3
"""Cross-reference the channel URLs an .mzdash dashboard binds against
Data/Telemetry.json, reporting which channels the plugin can map to a
SimHub property and which it cannot.

For every distinct v1/... channel URL referenced in the dashboard JSON:
  - is the URL defined in Telemetry.json at all? (else: plugin has no entry)
  - does it carry a simhub_property default mapping? (else: no auto value)
  - compression class (string channels ride a different path)

Usage: tools/dash-channel-coverage.py <dashboard.mzdash> [Data/Telemetry.json]
"""
import sys, json, re, os

CHAN = re.compile(r'v1/(?:gameData|preset)/[A-Za-z0-9_/]+')


def load_telemetry(path):
    with open(path) as fh:
        data = json.load(fh)
    by_url = {}
    for s in data.get("sectors", []):
        url = s.get("url")
        if url:
            by_url[url] = s
    return by_url


def main():
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    dash_path = sys.argv[1]
    tel_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(__file__), "..", "Data", "Telemetry.json")

    with open(dash_path, encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    counts = {}
    for m in CHAN.finditer(text):
        counts[m.group(0)] = counts.get(m.group(0), 0) + 1

    by_url = load_telemetry(tel_path)

    mapped, no_prop, missing, string_ch = [], [], [], []
    for url in sorted(counts):
        s = by_url.get(url)
        if s is None:
            missing.append(url)
        elif s.get("compression") == "string":
            string_ch.append((url, s.get("simhub_property")))
        elif s.get("simhub_property"):
            mapped.append((url, s.get("simhub_property")))
        else:
            no_prop.append(url)

    print(f"Dashboard: {os.path.basename(dash_path)}")
    print(f"Distinct channels referenced: {len(counts)}")
    print(f"Telemetry.json entries: {len(by_url)}")
    print()
    print(f"== MAPPED ({len(mapped)}): URL has simhub_property ==")
    for url, prop in mapped:
        print(f"  {url:50s} -> {prop}")
    print()
    print(f"== STRING channels ({len(string_ch)}): ride type=0x05 path ==")
    for url, prop in string_ch:
        print(f"  {url:50s} -> {prop}")
    print()
    print(f"== PRESENT but NO simhub_property ({len(no_prop)}) ==")
    for url in no_prop:
        print(f"  {url}")
    print()
    print(f"== MISSING from Telemetry.json ({len(missing)}) -- plugin cannot map ==")
    for url in missing:
        print(f"  {url}  (used {counts[url]}x)")


if __name__ == "__main__":
    main()
