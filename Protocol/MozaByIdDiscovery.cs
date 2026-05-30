using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Wine-only device discovery by USB identity via <c>/dev/serial/by-id</c>
    /// (exposed to Wine as <c>Z:\dev\serial\by-id</c>). Instead of blind-probing
    /// every COM port — which opens whatever else is on the bus (e.g. an Android
    /// tablet) and segfaults the shared wineserver — we enumerate the stable
    /// identity names, keep only MOZA data interfaces, and hand back their by-id
    /// paths for <see cref="WineByIdMozaPort"/> to open directly.
    ///
    /// <para>Resolution to a Windows COM name is deliberately NOT attempted: under
    /// Wine the symlink target is unreadable read-only (reparse/QueryDosDevice/
    /// GetFinalPathName all blocked) and <see cref="WineByIdMozaPort"/> opens the
    /// by-id path directly, so no COM name is needed.</para>
    /// </summary>
    internal static class MozaByIdDiscovery
    {
        // Wine maps the Linux root to Z:.
        private const string ByIdDir = @"Z:\dev\serial\by-id";
        // USB vendor/product hints (case-insensitive) present in MOZA by-id names,
        // e.g. "usb-Gudsen_MOZA_R5_Base_<serial>-if00". Extend if a future device
        // enumerates under a different string.
        private static readonly string[] NameHints = { "gudsen", "moza" };
        // The data/command CDC interface. MOZA exposes it as interface 0; the
        // diag/secondary interface (and unrelated devices like a phone) are other
        // interface numbers we must never open.
        private const string DataIfaceSuffix = "-if00";

        /// <summary>A discovered MOZA data port: the by-id path to open and a stable
        /// display/key name (the by-id basename — persistent across re-enumeration).</summary>
        public readonly struct Candidate
        {
            public readonly string ByIdPath;
            public readonly string Name;
            public Candidate(string path, string name) { ByIdPath = path; Name = name; }
        }

        /// <summary>True if <c>/dev/serial/by-id</c> exists (i.e. by-id discovery is
        /// usable on this host). When false, the caller keeps the legacy path.</summary>
        public static bool ByIdAvailable()
        {
            try { return Directory.Exists(ByIdDir); }
            catch { return false; }
        }

        /// <summary>Enumerate MOZA data-interface candidates, highest-quality first
        /// (order is by-id enumeration order). Returns false if none are present.</summary>
        public static bool TryListMozaDataPorts(out List<Candidate> candidates)
        {
            candidates = new List<Candidate>();
            string[] entries;
            try { entries = Directory.GetFiles(ByIdDir); }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] by-id enumerate failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            foreach (var path in entries)
            {
                string name = Path.GetFileName(path);
                string lower = name.ToLowerInvariant();
                if (!lower.EndsWith(DataIfaceSuffix)) continue;
                bool isMoza = false;
                foreach (var h in NameHints) { if (lower.IndexOf(h, StringComparison.Ordinal) >= 0) { isMoza = true; break; } }
                if (isMoza) candidates.Add(new Candidate(path, name));
            }
            if (candidates.Count == 0)
                MozaLog.Debug("[Moza] by-id: no MOZA data interface present (ignoring non-MOZA devices)");
            return candidates.Count > 0;
        }
    }

    /// <summary>
    /// Runs the existing MOZA discovery probe over an already-open
    /// <see cref="IMozaPort"/>, reusing <see cref="SerialProbeCore"/>'s frame and
    /// response-group constants. Used to confirm device identity + readiness on a
    /// by-id port BEFORE committing the connection, without ever opening a
    /// non-MOZA device. (Mirrors <c>SerialProbeCore.ProbeOnePort</c>'s poll loop;
    /// kept in-assembly so <c>SerialProbeCore</c> stays standalone for the helper.)
    /// </summary>
    internal static class MozaPortProbe
    {
        public static bool Confirm(IMozaPort port, ProbeKind kind, Action<string> log)
        {
            byte[] msg = SerialProbeCore.GetProbeFrame(kind);
            byte expected = SerialProbeCore.GetExpectedRespGroup(kind);
            try { port.DiscardInBuffer(); } catch { }

            const int TotalBudgetMs = 600;
            const int ProbeRepeatMs = 200;
            const int PollSliceMs = 25;
            const int MaxAccum = 4096;

            var accum = new List<byte>(512);
            byte firstSeen = 0xFF;
            int waited = 0;
            int nextProbeAt = 0;
            var buf = new byte[1024];

            while (waited < TotalBudgetMs)
            {
                if (waited >= nextProbeAt)
                {
                    try { port.Write(msg, 0, msg.Length); }
                    catch { return false; }
                    nextProbeAt = waited + ProbeRepeatMs;
                }
                Thread.Sleep(PollSliceMs);
                waited += PollSliceMs;

                int avail;
                try { avail = port.BytesToRead; } catch { return false; }
                if (avail <= 0) continue;

                int want = Math.Min(avail, Math.Min(buf.Length, MaxAccum - accum.Count));
                if (want > 0)
                {
                    int n;
                    try { n = port.Read(buf, 0, want); } catch { return false; }
                    for (int i = 0; i < n; i++) accum.Add(buf[i]);
                }
                for (int i = 0; i + 2 < accum.Count; i++)
                {
                    if (accum[i] != SerialProbeCore.MessageStart) continue;
                    byte g = accum[i + 2];
                    if (firstSeen == 0xFF) firstSeen = g;
                    if (g == expected) return true;
                }
            }

            if (firstSeen != 0xFF)
                log?.Invoke($"by-id probe {kind}: {accum.Count}B in {waited}ms, no 0x{expected:X2} (first 0x{firstSeen:X2})");
            return false;
        }
    }
}
