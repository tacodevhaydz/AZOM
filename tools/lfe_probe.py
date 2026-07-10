#!/usr/bin/env python3
"""Interactive probe for the MOZA wheelbase low-frequency-effects (LFE) system.

Drives cmd 0x2D/0x77 frames directly to the base over its serial port, so you can
explore the effect channels live — vary frequency / intensity / smoothness at
will, run several channels at once (to hear the base sum them), and scan effect
ids to find whether channels beyond 0/1/2 do anything.

  WIRE: 7E 0A 2D 13 77 [00][id][play][period16][freq16][int16] CHK   (+0x7E stuffing)
        freq16 = round(hz/200*65536)   int16 = round(amp01*65535)
        period16 = floor(ParamK/hz)    (ParamK 1000 engine / 2000 abs, per id)

IMPORTANT: the base's serial port is exclusive — CLOSE SimHub and PitHouse first,
or this can't open the port (and they'll fight over it).

Usage:
    python3 tools/lfe_probe.py [--port /dev/ttyACM0] [--rate 40]

REPL commands (type `help`):
    set ID FREQ INT [SMOOTH]   add/update a channel (id, Hz, 0-100%, 0-100 smooth)
    clear ID                   disable + stop streaming one channel
    clearall                   disable every channel
    sweep ID A B SECS          sweep a channel's frequency A->B Hz
    scan [LO HI]               pulse each effect id LO..HI (default 0..15) in turn
    classic V                  set classic (pre-LFE) gearshift intensity 0..5 (cmd 0x2E)
    bump                       fire the classic gearshift bump (cmd 0x76) — test it
                               alongside busy LFE channels; needs `classic V` > 0 first
    dev HEX                    device byte (default 13; try 12 = main)
    rate HZ                    stream rate
    k ID K                     ParamK for a channel's auto-period (0 = fixed 0x000F)
    raw HEX                    send raw bytes verbatim (you supply 7E/len/checksum)
    status                     show active channels
    quit
"""
import argparse, glob, math, os, sys, threading, time

try:
    import serial
except ImportError:
    sys.exit("pyserial required:  pip install --user pyserial")

MAGIC = 0x0D
STREAM_GROUP = 0x2D
STREAM_CMD = 0x77
DEFAULT_DEV = 0x13

def enc_freq(hz):
    if hz <= 0: return 0
    return min(0xFFFF, int(round(hz * 65536.0 / 200.0)))

def enc_amp(a01):
    if a01 <= 0: return 0
    return max(0, min(0xFFFF, int(round(a01 * 65535.0))))

def enc_period(paramk, hz):
    if paramk <= 0 or hz <= 0: return 0x000F   # gearshift placeholder / fixed
    return max(1, min(0xFFFF, int(math.floor(paramk / hz))))

def wire_checksum(frame, length):
    s = MAGIC + sum(frame[:length])
    for i in range(2, length):
        if frame[i] == 0x7E: s += 0x7E
    return s & 0xFF

def stuff(frame):
    out = bytearray([frame[0]])          # leading 0x7E delimiter, not doubled
    for b in frame[1:]:
        out.append(b)
        if b == 0x7E: out.append(0x7E)
    return bytes(out)

def build_lfe(dev, effect_id, play, freq_hz, amp01, paramk):
    period = enc_period(paramk, freq_hz)
    f16 = enc_freq(freq_hz); a16 = enc_amp(amp01)
    body = [0x0A, STREAM_GROUP, dev, STREAM_CMD,
            0x00, effect_id & 0xFF, 1 if play else 0,
            (period >> 8) & 0xFF, period & 0xFF,
            (f16 >> 8) & 0xFF, f16 & 0xFF,
            (a16 >> 8) & 0xFF, a16 & 0xFF]
    frame = [0x7E] + body
    frame.append(wire_checksum(frame, len(frame)))
    return stuff(bytes(frame))

def build_disable(dev, effect_id):
    return build_lfe(dev, effect_id, False, 0, 0, 0)

# ── Classic (pre-LFE) gearshift: intensity setting + fire-and-forget bump ──────
CLASSIC_VIB_GROUP = 0x29   # base-gearshift-vibration write group
CLASSIC_VIB_CMD = 0x2E     #   value 0..5 (BE16)
EVENT_GROUP = 0x2D         # base-gearshift-event
EVENT_CMD = 0x76           #   fixed body 76 00 01

def _wrap(body):
    frame = [0x7E] + body
    frame.append(wire_checksum(frame, len(frame)))
    return stuff(bytes(frame))

def build_classic_intensity(dev, value):   # 7E 03 29 13 2E 00 0V chk
    return _wrap([0x03, CLASSIC_VIB_GROUP, dev, CLASSIC_VIB_CMD, 0x00, value & 0xFF])

def build_gearshift_bump(dev):             # 7E 03 2D 13 76 00 01 chk
    return _wrap([0x03, EVENT_GROUP, dev, EVENT_CMD, 0x00, 0x01])

DEFAULT_PARAMK = {0: 0, 1: 1000, 2: 2000}   # gearshift fixed, engine, abs

class Channel:
    def __init__(self, freq, intensity_pct, smooth_pct=100, paramk=None):
        self.freq = freq
        self.amp = max(0.0, min(1.0, intensity_pct / 100.0))
        self.smooth = max(0.0, min(1.0, smooth_pct / 100.0))
        self.paramk = paramk
        self.phase = 0.0
        self.sweep = None   # (a, b, t0, secs)

class Probe:
    def __init__(self, port, rate):
        self.dev = DEFAULT_DEV
        self.rate = rate
        self.ser = serial.Serial(port, 115200, timeout=0.2, write_timeout=1.0)
        self.chans = {}          # id -> Channel
        self.lock = threading.Lock()
        self.stop = False
        self.scan = None         # (lo, hi, dwell, idx, t0)
        self.t = threading.Thread(target=self._loop, daemon=True)
        self.t.start()

    def _loop(self):
        dt = 1.0 / self.rate
        while not self.stop:
            now = time.monotonic()
            with self.lock:
                if self.scan is not None:
                    self._scan_tick(now)
                else:
                    for cid, ch in list(self.chans.items()):
                        f = ch.freq
                        if ch.sweep:
                            a, b, t0, secs = ch.sweep
                            u = min(1.0, (now - t0) / secs)
                            f = a + (b - a) * u
                            if u >= 1.0: ch.sweep = None
                        depth = 1.0 - ch.smooth
                        if depth > 1e-6 and f > 0:
                            ch.phase += 2 * math.pi * f * dt
                            ch.phase %= 2 * math.pi
                        env = (1 - depth) + depth * (0.5 + 0.5 * math.sin(ch.phase))
                        pk = ch.paramk if ch.paramk is not None else DEFAULT_PARAMK.get(cid, 1000)
                        try: self.ser.write(build_lfe(self.dev, cid, True, f, ch.amp * env, pk))
                        except Exception as ex: print("write:", ex)
            time.sleep(dt)

    def _scan_tick(self, now):
        lo, hi, dwell, idx, t0 = self.scan
        cid = lo + idx
        if now - t0 >= dwell:
            try: self.ser.write(build_disable(self.dev, cid))
            except Exception: pass
            idx += 1
            if lo + idx > hi:
                self.scan = None
                print(f"\n[scan done]  probed ids {lo}..{hi}. > ", end="", flush=True)
                return
            self.scan = (lo, hi, dwell, idx, now)
            print(f"\n[scan] effect id {lo+idx} ...", flush=True)
            return
        pk = DEFAULT_PARAMK.get(cid, 1000)
        self.ser.write(build_lfe(self.dev, cid, True, 45, 0.6, pk))   # clear, feelable pulse

    # ---- commands ----
    def set(self, cid, freq, intensity, smooth=100):
        with self.lock:
            self.chans[cid] = Channel(freq, intensity, smooth)
        print(f"channel {cid}: {freq:.0f} Hz  {intensity:.0f}%  smooth {smooth:.0f}")

    def clear(self, cid):
        with self.lock:
            self.chans.pop(cid, None)
        for _ in range(3): self.ser.write(build_disable(self.dev, cid)); time.sleep(0.01)
        print(f"channel {cid} off")

    def clearall(self):
        with self.lock:
            ids = list(self.chans); self.chans.clear()
        for cid in set(ids) | set(range(0, 16)):
            self.ser.write(build_disable(self.dev, cid)); time.sleep(0.005)
        print("all channels off")

    def do_sweep(self, cid, a, b, secs):
        with self.lock:
            ch = self.chans.get(cid)
            if not ch:
                ch = Channel(a, 50); self.chans[cid] = ch
            ch.sweep = (a, b, time.monotonic(), secs)
        print(f"channel {cid} sweep {a:.0f}->{b:.0f} Hz over {secs}s")

    def classic(self, value):
        v = max(0, min(5, int(value)))
        with self.lock:
            self.ser.write(build_classic_intensity(self.dev, v))
        print(f"classic gearshift intensity = {v}  (cmd 0x2E, needed for the bump to be felt)")

    def bump(self):
        with self.lock:
            self.ser.write(build_gearshift_bump(self.dev))
        print("classic gearshift BUMP fired  (cmd 0x76 00 01, fire-and-forget)")

    def do_scan(self, lo, hi, dwell=0.7):
        with self.lock:
            self.chans.clear()
            self.scan = (lo, hi, dwell, 0, time.monotonic())
        print(f"[scan] effect id {lo} ...")

    def raw(self, hexstr):
        b = bytes.fromhex(hexstr.replace(" ", ""))
        # auto-frame if they gave a bare body starting with a length byte? Keep it
        # literal: stuff+checksum only if it looks like a full 7E..body (no chk).
        self.ser.write(b)
        print("sent", b.hex())

    def status(self):
        with self.lock:
            print(f"dev=0x{self.dev:02x} rate={self.rate}Hz  channels:")
            for cid, ch in self.chans.items():
                pk = ch.paramk if ch.paramk is not None else DEFAULT_PARAMK.get(cid, 1000)
                print(f"  id {cid}: {ch.freq:.0f}Hz {ch.amp*100:.0f}% smooth{ch.smooth*100:.0f} k={pk}"
                      + ("  [sweeping]" if ch.sweep else ""))

    def close(self):
        self.stop = True; self.t.join(1.0)
        try: self.clearall()
        except Exception: pass
        self.ser.close()

HELP = __doc__.split("REPL commands")[1]

def find_port():
    for p in glob.glob("/dev/serial/by-id/*MOZA*"):
        return os.path.realpath(p)
    acm = sorted(glob.glob("/dev/ttyACM*"))
    return acm[0] if acm else "/dev/ttyACM0"

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", default=None)
    ap.add_argument("--rate", type=float, default=40.0)
    args = ap.parse_args()
    port = args.port or find_port()
    print(f"opening {port} @115200  (close SimHub/PitHouse first)")
    try:
        pr = Probe(port, args.rate)
    except serial.SerialException as ex:
        sys.exit(f"cannot open {port}: {ex}\n(is SimHub/PitHouse holding it?)")
    print("ready. type 'help'.  e.g.  set 1 60 45   |   scan   |   clearall\n")
    try:
        while True:
            try: line = input("> ").strip()
            except EOFError: break
            if not line: continue
            a = line.split()
            c = a[0].lower()
            try:
                if c in ("quit", "q", "exit"): break
                elif c == "help": print("REPL commands" + HELP)
                elif c == "set": pr.set(int(a[1]), float(a[2]), float(a[3]), float(a[4]) if len(a) > 4 else 100)
                elif c == "clear": pr.clear(int(a[1]))
                elif c == "clearall": pr.clearall()
                elif c == "sweep": pr.do_sweep(int(a[1]), float(a[2]), float(a[3]), float(a[4]))
                elif c == "scan": pr.do_scan(int(a[1]) if len(a) > 1 else 0, int(a[2]) if len(a) > 2 else 15)
                elif c == "classic": pr.classic(a[1])
                elif c == "bump": pr.bump()
                elif c == "dev": pr.dev = int(a[1], 16)
                elif c == "rate": pr.rate = float(a[1]); print("rate", pr.rate)
                elif c == "k":
                    with pr.lock:
                        if int(a[1]) in pr.chans: pr.chans[int(a[1])].paramk = float(a[2])
                elif c == "raw": pr.raw(" ".join(a[1:]))
                elif c == "status": pr.status()
                else: print("?  type 'help'")
            except (IndexError, ValueError) as ex:
                print("bad args:", ex)
    finally:
        print("\nclosing, silencing channels...")
        pr.close()

if __name__ == "__main__":
    main()
