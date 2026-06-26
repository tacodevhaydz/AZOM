using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Per-track world→field transform for the track-map <c>patch/Location*</c>
    /// (location_t) channels.
    ///
    /// Reverse-engineered from PitHouse captures on Imola, Spa and Zandvoort
    /// (player slot of the low-rate 48-byte track-map tier, fit corr ~1.0). The
    /// key finding: PitHouse sends MAP-PIXEL coordinates, not bounding-box-
    /// normalised values — so the scale is keyed to the map's pixel scale
    /// (<c>map.ini SCALE_FACTOR</c>), NOT the track extent:
    /// <code>
    ///   scale_X = PixelK / SCALE_FACTOR     (PixelK ≈ 669; scale_X·SCALE was
    ///                                         672/661/673 across the 3 tracks)
    ///   scale_Z = 16 · scale_X              (Z carries 16× the X resolution)
    ///   field   = scale · world + CENTER    (CENTER ~universal: measured X-center
    ///                                         4,721,866 / 4,716,730 on Imola/Spa)
    /// </code>
    /// The Y (elevation) field is 16-bit and secondary to the 2-D map.
    /// Falls back to a SCALE_FACTOR=1.2 transform when no map.ini is found.
    /// </summary>
    internal sealed class TrackMapTransform
    {
        public float ScaleX { get; private set; }
        public float ScaleZ { get; private set; }
        public float ScaleY { get; private set; }
        public int CenterX { get; private set; }
        public int CenterZ { get; private set; }
        public int CenterY { get; private set; }
        public string Source { get; private set; } = "fallback";

        // PitHouse map constants (calibrated on Imola/Spa/Zandvoort, corr ~1.0).
        // X is the map-pixel coordinate (scale = PixelK / SCALE_FACTOR). Z is
        // normalised so the track's LARGER map dimension fills the 24-bit field:
        //   scale_Z = 2^24 / (SCALE_FACTOR · max(WIDTH, HEIGHT))
        private const double PixelK = 669.0;       // field counts per map pixel (X)
        private const double FieldFull = 16777216.0; // 2^24, the Z field span
        private const double ZRatio = 16.0;        // fallback Z/X when dims unknown
        // X centre scales with the map's larger dimension (fits all 3 tracks:
        // (center_X − 4,500,000)·max(W,H) ≈ 343M — Imola 342.5M, Spa 345.4M,
        // Zandvoort 340.3M). Same max(W,H) that drives the Z scale.
        private const double CenterXBase = 4360000.0;
        private const double CenterXK = 558200000.0; // center_X = Base + K/max(W,H)
        private const int CenterXConst = 4719000;  // fallback X center (no dims)
        private const int CenterZConst = 8300000;  // field at world_z = 0
        private const int CenterYConst = 32768;    // mid 16-bit; elevation secondary

        public static TrackMapTransform Fallback() => FromMapIni(1.2, 0.0, 0.0, "fallback(SCALE=1.2)");

        private static TrackMapTransform FromMapIni(double scaleFactor, double width, double height, string src)
        {
            double sx = PixelK / scaleFactor;
            double maxDim = Math.Max(width, height);
            double sz = maxDim > 1.0 ? FieldFull / (scaleFactor * maxDim) : ZRatio * sx;
            int cx = maxDim > 1.0
                ? (int)Math.Round(CenterXBase + CenterXK / maxDim)
                : CenterXConst;
            return new TrackMapTransform
            {
                ScaleX = (float)sx,
                ScaleZ = (float)sz,
                ScaleY = (float)sx,
                CenterX = cx,
                CenterZ = CenterZConst,
                CenterY = CenterYConst,
                Source = src,
            };
        }

        /// <summary>Resolve the transform for a track folder (e.g. "ks_zandvoort").
        /// Reads SCALE_FACTOR from AC's map.ini; falls back when unavailable.</summary>
        public static TrackMapTransform Resolve(string? trackFolder)
        {
            if (string.IsNullOrEmpty(trackFolder)) return Fallback();
            try
            {
                string? ini = FindMapIni(trackFolder!);
                if (ini == null)
                {
                    MozaLog.Info($"[AZOM] track transform: no map.ini for '{trackFolder}' — using fallback");
                    return Fallback();
                }
                double? scale = null, width = null, height = null;
                foreach (var line in File.ReadAllLines(ini))
                {
                    var m = Regex.Match(line, @"^\s*([A-Za-z_]+)\s*=\s*([-0-9.]+)");
                    if (!m.Success) continue;
                    if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double v)) continue;
                    switch (m.Groups[1].Value.ToUpperInvariant())
                    {
                        case "SCALE_FACTOR": scale = v; break;
                        case "WIDTH": width = v; break;
                        case "HEIGHT": height = v; break;
                    }
                }
                if (scale.HasValue && scale.Value > 0.0)
                {
                    var t = FromMapIni(scale.Value, width ?? 0.0, height ?? 0.0, $"map.ini:{trackFolder}");
                    MozaLog.Info($"[AZOM] track transform '{trackFolder}': SCALE_FACTOR={scale.Value} " +
                        $"WIDTH={width ?? 0} HEIGHT={height ?? 0} " +
                        $"-> X_scale={t.ScaleX:F1} X_center={t.CenterX} Z_scale={t.ScaleZ:F0} (from {ini})");
                    return t;
                }
                MozaLog.Info($"[AZOM] track transform: map.ini for '{trackFolder}' has no SCALE_FACTOR — fallback");
                return Fallback();
            }
            catch (Exception e)
            {
                MozaLog.Info($"[AZOM] track transform resolve failed for '{trackFolder}': {e.Message}");
                return Fallback();
            }
        }

        private static string? FindMapIni(string trackFolder)
        {
            string? content = FindAcContentPath();
            if (content == null) return null;
            foreach (var p in new[]
            {
                Path.Combine(content, "tracks", trackFolder, "data", "map.ini"),
                Path.Combine(content, "tracks", trackFolder, "map.ini"),
            })
                if (File.Exists(p)) return p;
            return null;
        }

        private static string? _acContentCache;
        private static bool _acResolved;

        // Locate AC's content/ directory: a running AC process, else Steam libraries.
        private static string? FindAcContentPath()
        {
            if (_acResolved) return _acContentCache;
            _acResolved = true;
            try
            {
                foreach (var name in new[] { "acs", "AssettoCorsa" })
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        string? exe = proc.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exe)) continue;
                        string c = Path.Combine(Path.GetDirectoryName(exe)!, "content");
                        if (Directory.Exists(c)) return _acContentCache = c;
                    }
            }
            catch { }
            try
            {
                string? steam =
                    Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(steam))
                {
                    var libs = new System.Collections.Generic.List<string> { steam! };
                    string vdf = Path.Combine(steam!, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    foreach (var lib in libs)
                    {
                        string c = Path.Combine(lib, "steamapps", "common", "assettocorsa", "content");
                        if (Directory.Exists(c)) return _acContentCache = c;
                    }
                }
            }
            catch { }
            return _acContentCache = null;
        }
    }
}
