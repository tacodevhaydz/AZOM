using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Runs the serial probe in a short-lived child process
    /// (<c>MozaProbeHelper.exe</c>) so that a Wine segfault during
    /// <c>SerialPort.Open</c> on a not-ready CDC-ACM port kills only the helper,
    /// never SimHub. The helper exe is embedded in MozaPlugin.dll and extracted
    /// on first use (single-DLL deployment, same pattern as the CoAP stub).
    ///
    /// <para>Used only under Wine — on native Windows there is no such crash, so
    /// MozaSerialConnection.ProbeWithTimeout keeps probing in-process there to
    /// avoid the per-probe process-launch latency.</para>
    /// </summary>
    internal static class SerialProbeHelperLauncher
    {
        // Mirrors the EmbeddedResource LogicalName in MozaPlugin.csproj.
        private const string HelperResourceName = "MozaPlugin.Protocol.MozaProbeHelper.exe";
        private const string HelperFileName = "MozaProbeHelper.exe";

        private static string HelperExePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimHub", "MozaPlugin", "ProbeHelper", HelperFileName);

        private static readonly object _extractLock = new object();
        private static string? _extractedPath;
        private static bool _extractFailed;

        /// <summary>Extract the embedded helper exe once (hash-checked so a
        /// running copy is never overwritten). Returns the on-disk path, or null
        /// if extraction failed.</summary>
        private static string? EnsureExtracted()
        {
            if (_extractedPath != null) return _extractedPath;
            if (_extractFailed) return null;
            lock (_extractLock)
            {
                if (_extractedPath != null) return _extractedPath;
                if (_extractFailed) return null;
                try
                {
                    string path = HelperExePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    var asm = typeof(SerialProbeHelperLauncher).Assembly;
                    using var stream = asm.GetManifestResourceStream(HelperResourceName)
                        ?? throw new InvalidOperationException(
                            $"Embedded resource '{HelperResourceName}' not found in {asm.GetName().Name}.");
                    byte[] payload;
                    using (var ms = new MemoryStream()) { stream.CopyTo(ms); payload = ms.ToArray(); }

                    string embeddedHash = HashSha1(payload);
                    bool upToDate = false;
                    if (File.Exists(path))
                    {
                        try { upToDate = HashSha1(File.ReadAllBytes(path)) == embeddedHash; }
                        catch { upToDate = false; }
                    }
                    if (!upToDate) File.WriteAllBytes(path, payload);

                    _extractedPath = path;
                    return path;
                }
                catch (Exception ex)
                {
                    _extractFailed = true;
                    MozaLog.Warn($"[Moza] Probe-helper extraction failed: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
        }

        private static string HashSha1(byte[] data)
        {
            using var sha1 = SHA1.Create();
            return BitConverter.ToString(sha1.ComputeHash(data));
        }

        /// <summary>Probe one port via the child helper. Returns (responded,
        /// reachable). On launch failure, helper crash, or timeout → (false,
        /// false). <paramref name="timeoutMs"/> must allow for child-process
        /// launch overhead on top of the probe's internal ~500ms budget.</summary>
        public static (bool responded, bool reachable) ProbeViaHelper(
            string portName, ProbeKind kind, int timeoutMs)
        {
            string? exe = EnsureExtracted();
            if (exe == null) return (false, false);

            string kindArg = kind == ProbeKind.Base ? "base" : (kind == ProbeKind.Hub ? "hub" : "ab9");
            Process? p = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"{kindArg} {portName}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                p = Process.Start(psi);
                if (p == null) return (false, false);

                // Drain both pipes asynchronously BEFORE waiting, so a chatty
                // helper can't fill a pipe buffer and deadlock before exit.
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit(timeoutMs))
                {
                    // Helper hung in Open() — kill the child (safe: it's a
                    // separate process, not an in-proc thread we can't cancel).
                    try { p.Kill(); } catch { }
                    MozaLog.Debug($"[Moza] Probe helper for {portName} timed out after {timeoutMs}ms — killed.");
                    return (false, false);
                }

                string outText;
                try { outText = (outTask.Result ?? string.Empty).Trim(); }
                catch { outText = string.Empty; }
                try { var e = errTask.Result; if (!string.IsNullOrWhiteSpace(e)) MozaLog.Debug($"[Moza] {e.Trim()}"); }
                catch { }

                if (outText.IndexOf("RESP", StringComparison.Ordinal) >= 0) return (true, true);
                if (outText.IndexOf("REACH", StringComparison.Ordinal) >= 0) return (false, true);
                // "NONE", empty, or a crash exit with no token → not reachable.
                return (false, false);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] Probe helper launch failed for {portName}: {ex.GetType().Name}: {ex.Message}");
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                return (false, false);
            }
            finally
            {
                try { p?.Dispose(); } catch { }
            }
        }
    }
}
