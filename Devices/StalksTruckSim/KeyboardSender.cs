using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Sends keystrokes to the foreground game via <c>SendInput</c> (scan-code based —
    /// what ETS2/ATS read). All taps are played on a dedicated worker thread so the
    /// callers (HID read thread, SimHub <c>DataUpdate</c> thread) never block on
    /// key hold / inter-tap timing, and every send is gated on the game being the
    /// foreground window so keys can never leak into another app.
    /// </summary>
    internal sealed class KeyboardSender : IDisposable
    {
        /// <summary>How long a key is held down per tap (ms).</summary>
        public int HoldMs { get; set; } = 30;
        /// <summary>Gap after a tap before the next queued tap (ms).</summary>
        public int GapMs { get; set; } = 40;

        // Process names (no .exe) that count as "the game is foreground". Empty = allow
        // all (the controller still gates on game-running, but set this in practice).
        private volatile string[] _foregroundProcs = Array.Empty<string>();

        private readonly BlockingCollection<ushort> _queue =
            new BlockingCollection<ushort>(new ConcurrentQueue<ushort>());
        private readonly Thread _worker;
        private volatile bool _disposed;

        public KeyboardSender()
        {
            _worker = new Thread(Run) { IsBackground = true, Name = "StalksKeyboard" };
            _worker.Start();
        }

        /// <summary>Restrict key output to when one of these process names is the
        /// foreground window (e.g. "eurotrucks2", "amtrucks").</summary>
        public void SetForegroundProcesses(params string[] names)
            => _foregroundProcs = names ?? Array.Empty<string>();

        /// <summary>Queue a single key tap (down+up) by scan code. No-op for scan 0.</summary>
        public void Tap(ushort scan)
        {
            if (scan != 0 && !_disposed && !_queue.IsAddingCompleted)
                _queue.TryAdd(scan);
        }

        /// <summary>Queue a key tap by key name (resolved via <see cref="ScanCode"/>).</summary>
        public void Tap(string keyName) => Tap(ScanCode(keyName));

        /// <summary>Drop any pending taps (e.g. on disable / focus loss).</summary>
        public void Flush()
        {
            while (_queue.TryTake(out _)) { }
        }

        private void Run()
        {
            try
            {
                foreach (var scan in _queue.GetConsumingEnumerable())
                {
                    if (_disposed) break;
                    // Drop the tap if the game isn't foreground — never inject elsewhere.
                    if (!IsGameForeground()) continue;
                    try
                    {
                        SendKey(scan, down: true);
                        Thread.Sleep(HoldMs);
                        SendKey(scan, down: false);
                        Thread.Sleep(GapMs);
                    }
                    catch { }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private bool IsGameForeground()
        {
            var procs = _foregroundProcs;
            if (procs.Length == 0) return true;
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return false;
                using (var p = Process.GetProcessById((int)pid))
                {
                    string name = p.ProcessName;
                    foreach (var want in procs)
                        if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------
        // Key name → scan code (Set 1 "make" codes). Accepts single chars
        // (case-insensitive) and friendly names ("Comma", "Minus", "F1"…).
        // ------------------------------------------------------------------
        private static readonly Dictionary<string, ushort> ScanCodes =
            new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            {"A",0x1E},{"B",0x30},{"C",0x2E},{"D",0x20},{"E",0x12},{"F",0x21},{"G",0x22},
            {"H",0x23},{"I",0x17},{"J",0x24},{"K",0x25},{"L",0x26},{"M",0x32},{"N",0x31},
            {"O",0x18},{"P",0x19},{"Q",0x10},{"R",0x13},{"S",0x1F},{"T",0x14},{"U",0x16},
            {"V",0x2F},{"W",0x11},{"X",0x2D},{"Y",0x15},{"Z",0x2C},
            {"0",0x0B},{"1",0x02},{"2",0x03},{"3",0x04},{"4",0x05},{"5",0x06},
            {"6",0x07},{"7",0x08},{"8",0x09},{"9",0x0A},
            {"Minus",0x0C},{"-",0x0C},{"Equals",0x0D},{"=",0x0D},
            {"Comma",0x33},{",",0x33},{"Period",0x34},{".",0x34},
            {"Slash",0x35},{"/",0x35},{"Semicolon",0x27},{";",0x27},
            {"Apostrophe",0x28},{"'",0x28},{"LeftBracket",0x1A},{"[",0x1A},
            {"RightBracket",0x1B},{"]",0x1B},{"Backslash",0x2B},{"\\",0x2B},
            {"Grave",0x29},{"`",0x29},
            {"Space",0x39},{"Enter",0x1C},{"Tab",0x0F},{"Escape",0x01},{"Esc",0x01},
            {"F1",0x3B},{"F2",0x3C},{"F3",0x3D},{"F4",0x3E},{"F5",0x3F},{"F6",0x40},
            {"F7",0x41},{"F8",0x42},{"F9",0x43},{"F10",0x44},{"F11",0x57},{"F12",0x58},
        };

        /// <summary>Resolve a key name to a Set-1 scan code. Returns 0 if unknown.</summary>
        public static ushort ScanCode(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return 0;
            return ScanCodes.TryGetValue(keyName.Trim(), out var s) ? s : (ushort)0;
        }

        /// <summary>Whether a key name resolves to a known scan code.</summary>
        public static bool IsKnownKey(string keyName) => ScanCode(keyName) != 0;

        // ------------------------------------------------------------------
        // Win32 SendInput
        // ------------------------------------------------------------------
        private void SendKey(ushort scan, bool down)
        {
            uint flags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP);
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = scan,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            };
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        // Union sized to the largest member (MOUSEINPUT) so sizeof(INPUT) matches the
        // OS's expectation on both x86 and x64 SimHub builds.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData;
            public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            try { _worker.Join(500); } catch { }
            try { _queue.Dispose(); } catch { }
        }
    }
}
