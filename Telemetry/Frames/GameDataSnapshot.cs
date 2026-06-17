using System;
using System.Reflection;
using GameReaderCommon;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// A flat snapshot of the game data fields used by the telemetry sender.
    /// Decouples TelemetryFrameBuilder from the SimHub StatusDataBase API,
    /// enabling test patterns and unit testing without live game data.
    /// </summary>
    public struct GameDataSnapshot
    {
        public double SpeedKmh;
        public double Rpms;
        public double Gear;         // 0=R/N, 1=1st, 2=2nd, …
        public double Throttle;     // 0.0–1.0 (converted from SimHub's 0–100)
        public double Brake;        // 0.0–1.0 (converted from SimHub's 0–100)
        public double BestLapTimeSeconds;
        public double CurrentLapTimeSeconds;
        public double LastLapTimeSeconds;
        public double DeltaToSessionBest;
        public double FuelPercent;  // 0–100
        public double DrsEnabled;   // 0 or 1
        public double ErsPercent;   // 0–100
        public double TyreWearFrontLeft;
        public double TyreWearFrontRight;
        public double TyreWearRearLeft;
        public double TyreWearRearRight;
        public double CurrentLap;       // lap counter (1+)

        // Track-map (patch/Location*) per-car ground-plane positions. Each
        // entry feeds one 64-bit location_t slot, packed by
        // TelemetryFrameBuilder as two little-endian float32 (X, Z) — the
        // wire format reverse-engineered from the PitHouse FSR2 capture.
        // CarLocations is indexed by SimHub Opponents[] order; PlayerLocation
        // is the local car. Null/default when there's no game or no opponents.
        // Source: Opponent.Coordinates / StatusDataBase.CarCoordinates, which
        // are double[X,Y,Z] — the ground plane is indices 0 (X) and 2 (Z);
        // index 1 (Y) is elevation and unused by the 2-D map.
        public (float X, float Y)[]? CarLocations;
        public (float X, float Y) PlayerLocation;
        // Index of the local car within CarLocations (= the IsPlayer opponent),
        // so the wheel can highlight "you" on the track map. 0 when unknown.
        public int PlayerIndex;

        // Per-car position RELATIVE to the player (already rotated into the
        // player's frame by SimHub), for the radar (patch/ri*) channels. Each
        // entry feeds one uint32 ri slot, packed by TelemetryFrameBuilder as
        // two int16 (X, Y) — the wire format reverse-engineered from the
        // PitHouse FSR2 radar capture. Indexed by Opponents[] order; (0,0)
        // when SimHub has no relative coordinate for that car (out of radar
        // range), which matches PitHouse's empty-slot behaviour. Source:
        // Opponent.RelativeCoordinatesToPlayer (PointF, metres).
        public (float X, float Y)[]? CarRelative;

        /// <summary>Populate from a live StatusDataBase instance. When
        /// <paramref name="includeCarPositions"/> is false the per-car track-map /
        /// radar arrays are NOT built — skips the reflection chain and the
        /// per-opponent allocation/loop in <see cref="PopulateCarLocations"/>.
        /// Callers pass false when no active channel consumes those arrays
        /// (radar/track-map channels disabled), which is the shipped default.</summary>
        public static GameDataSnapshot FromStatusData(StatusDataBase? data, bool includeCarPositions = true)
        {
            if (data == null) return default;
            var snap = new GameDataSnapshot
            {
                SpeedKmh               = data.SpeedKmh,
                Rpms                   = data.Rpms,
                Gear                   = ParseGear(data.Gear),
                Throttle               = data.Throttle / 100.0,  // SimHub 0–100 → float_001 expects 0.0–1.0
                Brake                  = data.Brake    / 100.0,  // SimHub 0–100 → float_001 expects 0.0–1.0
                BestLapTimeSeconds     = data.BestLapTime.TotalSeconds,
                CurrentLapTimeSeconds  = data.CurrentLapTime.TotalSeconds,
                LastLapTimeSeconds     = data.LastLapTime.TotalSeconds,
                DeltaToSessionBest     = data.DeltaToSessionBest ?? 0.0,
                FuelPercent            = data.FuelPercent,
                DrsEnabled             = data.DRSEnabled != 0 ? 1.0 : 0.0,
                ErsPercent             = data.ERSPercent,
                TyreWearFrontLeft      = data.TyreWearFrontLeft,
                TyreWearFrontRight     = data.TyreWearFrontRight,
                TyreWearRearLeft       = data.TyreWearRearLeft,
                TyreWearRearRight      = data.TyreWearRearRight,
                CurrentLap             = data.CurrentLap,
            };
            if (includeCarPositions)
                PopulateCarLocations(data, ref snap);
            return snap;
        }

        // Ground-plane (X, Z) for the local car and every opponent, for the
        // track-map location_t channels. Defensive against null/short arrays
        // (games that don't expose coordinates leave them null).
        private static void PopulateCarLocations(StatusDataBase data, ref GameDataSnapshot snap)
        {
            // SimHub's recorded per-track map (PersistantTrackerPlugin) converts
            // lap-relative coordinates to world space for games that report them
            // (iRacing); a passthrough for world-coord games (AC/AMS2) and null
            // until a lap has been recorded. Read reflectively to avoid a hard
            // SimHub.Plugins compile-time dependency.
            var map = TryGetMapRecord();

            var pc = data.CarCoordinates;
            float px = 0f, pz = 0f; bool havePlayer = false;
            if (pc != null && pc.Length >= 3)
            {
                (px, pz) = ToGroundPlane(map, pc); havePlayer = true;
                snap.PlayerLocation = (px, pz);
            }

            var opps = data.Opponents;
            if (opps == null || opps.Count == 0) return;
            int count = opps.Count;

            // Resolve each car's ABSOLUTE ground-plane position (X, Z) — this is
            // what the track-map patch/Location* channels emit; the wheel's Map
            // widget plots each car's path around the track from absolute world
            // positions (see TelemetryFrameBuilder.WriteLocationPair). Source
            // priority matches what each game exposes:
            //   1. AC raw struct (DataContainer.Graphics.CarCoordinates, a flat
            //      [x,y,z,…] per slot) — world coords, the only reliable source
            //      for AC where Opponent.Coordinates is ~98.5% empty. Read
            //      reflectively (no compile-time AC reference).
            //   2. SimHub's generic Opponent.Coordinates, normalised through the
            //      recorded track map (ToGroundPlane) so lap-relative-coordinate
            //      games (iRacing) map to world space; a passthrough otherwise.
            //      Mirrors SimHub's own RadarItem.UpdateData.
            // CarRelative (Δ from player, WORLD-frame, unrotated) feeds the radar
            // patch/ri* channels — the player-relative "cars nearby" view — which
            // stays disabled until that format is verified; heading rotation is
            // deferred.
            float[]? raw = TryReadRawCarCoordinates(data);

            var locs = new (float X, float Y)[count];
            var rels = new (float X, float Y)[count];
            int playerIdx = 0;
            double bestD = double.MaxValue;
            bool playerByDist = false;
            for (int i = 0; i < count; i++)
            {
                var opp = opps[i];
                (float X, float Y) abs;
                bool haveAbs = false;

                if (raw != null && i * 3 + 2 < raw.Length
                    && (raw[i * 3] != 0f || raw[i * 3 + 2] != 0f))
                {
                    abs = (raw[i * 3], raw[i * 3 + 2]); // ground plane = X, Z
                    haveAbs = true;
                }
                else
                {
                    var c = opp?.Coordinates;
                    if (c != null && c.Length >= 3 && (c[0] != 0.0 || c[2] != 0.0))
                    {
                        abs = ToGroundPlane(map, c);
                        haveAbs = true;
                    }
                    else
                    {
                        abs = (0f, 0f);
                    }
                }

                locs[i] = abs;
                if (haveAbs && havePlayer)
                {
                    rels[i] = (abs.X - px, abs.Y - pz);
                    double d = (abs.X - px) * (abs.X - px) + (abs.Y - pz) * (abs.Y - pz);
                    if (d < bestD) { bestD = d; playerIdx = i; playerByDist = true; }
                }
                else
                {
                    var rc = opp?.RelativeCoordinatesToPlayer;
                    rels[i] = rc.HasValue ? (rc.Value.X, rc.Value.Y) : (0f, 0f);
                }

                if (!playerByDist && opp != null && opp.IsPlayer)
                    playerIdx = i;
            }
            snap.CarLocations = locs;
            snap.CarRelative = rels;
            snap.PlayerIndex = playerIdx;
        }

        // Per-car absolute ground coordinates from the raw game struct, when the
        // game exposes them (AC: DataContainer.Graphics.CarCoordinates, a flat
        // float[] of x,y,z per slot). Reflection-only so we don't take a
        // compile-time dependency on any game plugin; any failure returns null
        // and the caller falls back to SimHub's generic fields.
        private static float[]? TryReadRawCarCoordinates(StatusDataBase data)
        {
            try
            {
                object? raw = data.GetRawDataObject();
                if (raw == null) return null;
                object? gfx = raw.GetType().GetProperty("Graphics")?.GetValue(raw);
                if (gfx == null) return null;
                return gfx.GetType().GetField("CarCoordinates")?.GetValue(gfx) as float[];
            }
            catch
            {
                return null;
            }
        }

        // Cached MethodInfo for SimHub.Plugins
        // PersistantTrackerPlugin.GetMap() — the public static accessor for the
        // recorded track map. Reflected (not a compile-time call) so a SimHub
        // build that moves or renames it degrades to null instead of throwing.
        private static MethodInfo? s_getMapMethod;
        private static bool s_getMapResolved;

        // The live recorded track-map record, or null when none exists yet or
        // SimHub's internals have shifted.
        private static DataRecordBase? TryGetMapRecord()
        {
            try
            {
                if (!s_getMapResolved)
                {
                    s_getMapResolved = true;
                    var t = Type.GetType(
                        "SimHub.Plugins.DataPlugins.PersistantTracker.PersistantTrackerPlugin, SimHub.Plugins");
                    s_getMapMethod = t?.GetMethod("GetMap",
                        BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                }
                return s_getMapMethod?.Invoke(null, null) as DataRecordBase;
            }
            catch
            {
                return null;
            }
        }

        // Project a car's coordinate array to ground-plane (X, Z). When a track
        // map is recorded for a lap-relative-coordinate game, route through
        // DataRecordBase.ToAbsoluteCoordinates (→ world space); a passthrough for
        // world-coordinate games. Mirrors RadarItem.UpdateData. Defensive: any
        // failure or short result keeps the raw [0],[2].
        private static (float X, float Y) ToGroundPlane(DataRecordBase? map, double[] c)
        {
            double x = c[0], z = c.Length > 2 ? c[2] : 0.0;
            if (map != null)
            {
                try
                {
                    double[]? a = map.ToAbsoluteCoordinates(c);
                    if (a != null && a.Length >= 3) { x = a[0]; z = a[2]; }
                }
                catch { /* keep raw [0],[2] */ }
            }
            return ((float)x, (float)z);
        }

        private static double ParseGear(string? gear)
        {
            if (gear == null || gear.Length == 0) return 0;
            if (gear == "R") return -1.0; // int30: -1 encodes as raw=31 (5-bit two's complement)
            if (gear == "N") return 0;
            if (int.TryParse(gear, out int n)) return n;
            return 0;
        }

        public double GetField(SimHubField field)
        {
            switch (field)
            {
                case SimHubField.SpeedKmh:              return SpeedKmh;
                case SimHubField.Rpms:                  return Rpms;
                case SimHubField.Gear:                  return Gear;
                case SimHubField.Throttle:              return Throttle;
                case SimHubField.Brake:                 return Brake;
                case SimHubField.BestLapTimeSeconds:    return BestLapTimeSeconds;
                case SimHubField.CurrentLapTimeSeconds: return CurrentLapTimeSeconds;
                case SimHubField.LastLapTimeSeconds:    return LastLapTimeSeconds;
                case SimHubField.DeltaToSessionBest:    return DeltaToSessionBest;
                case SimHubField.FuelPercent:           return FuelPercent;
                case SimHubField.DrsEnabled:            return DrsEnabled;
                case SimHubField.ErsPercent:            return ErsPercent;
                case SimHubField.TyreWearFrontLeft:     return TyreWearFrontLeft;
                case SimHubField.TyreWearFrontRight:    return TyreWearFrontRight;
                case SimHubField.TyreWearRearLeft:      return TyreWearRearLeft;
                case SimHubField.TyreWearRearRight:     return TyreWearRearRight;
                case SimHubField.CurrentLap:            return CurrentLap;
                case SimHubField.PlayerIndex:           return PlayerIndex;
                default:                                return 0.0;
            }
        }
    }
}
