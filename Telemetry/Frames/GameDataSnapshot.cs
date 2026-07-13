using System;
using System.Collections.Generic;
using System.Reflection;
using GameReaderCommon;
using MozaPlugin.Diagnostics;
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

        // Track-map (patch/Location*) per-car WORLD position (X, Y=elevation, Z).
        // Each entry feeds one 64-bit location_t slot, packed by
        // TelemetryFrameBuilder as PitHouse's fixed-point layout
        // [u16 Y | u24 Z | u24 X] — reverse-engineered + verified to <0.3 m
        // against AC ground truth on Imola+Spa (see
        // docs/protocol/telemetry/track-map.md). NOT two float32 (the prior
        // guess). CarLocations is indexed by SimHub Opponents[] order;
        // PlayerLocation is the local car. (0,0,0) marks an empty slot.
        // Source: Opponent.Coordinates / StatusDataBase.CarCoordinates
        // (double[X,Y,Z]); index 0=X, 1=Y elevation, 2=Z.
        public (float X, float Y, float Z)[]? CarLocations;
        public (float X, float Y, float Z) PlayerLocation;
        // Index of the local car within CarLocations (= the IsPlayer opponent),
        // so the wheel can highlight "you" on the track map. 0 when unknown.
        public int PlayerIndex;

        // Slot-indexed map ri-slot -> CarLocations index (carId) for the radar, or
        // -1 for an empty slot. Index 0 is unused (ri0 is the magic header). PitHouse
        // gives each car a STABLE slot it keeps while relevant and emits ONLY the
        // in-range cars (~24 m 2-D); a car entering/leaving range never reshuffles the
        // others (re-packing every frame made the radar go wild the instant cars
        // moved and crossed the range boundary). Built by AssignStableRadarSlots.
        // Null when car positions weren't requested.
        public int[]? RadarSlotCarIds;

        // Track folder name (AC: content/tracks/<name>, e.g. "ks_zandvoort"),
        // used as the per-track cache key for the map bounds / transform.
        public string? TrackFolderName;

        // World bounding box of the track, from SimHub's recorded track map
        // (PersistantTracker), in metres. Feeds TrackMapTransform.FromBounds to
        // pick the per-track world→field transform for the location_t channels —
        // SimHub-specific + game-agnostic. Invalid until SimHub has a recorded
        // map for the track (then the builder upgrades off the Imola fallback).
        public bool MapBoundsValid;
        public double MapMinX, MapMaxX, MapMinZ, MapMaxZ;

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
                PopulateCarPositionsShared(data, ref snap);
            return snap;
        }

        // Car-position state (track folder, map bounds, car locations, radar
        // slots) is profile-independent — identical for every sender that binds
        // radar/track-map channels. A dual-display setup (wheel screen + CM2
        // dash) runs TWO sender tick threads through here, and the slot maps /
        // bounds cache / reflection caches / scratch buffers below are process
        // statics — unsynchronized, they'd race. Compute once per game frame
        // (SimHub allocates a fresh StatusDataBase per DataUpdate — the
        // NewData/OldData swap — so the reference is the frame key) under a
        // leaf lock; the other sender's tick copies the cached fields and
        // shares the arrays (consumers only read them). Timer threads only —
        // never the serial read thread.
        private static readonly object s_carPosLock = new object();
        private static StatusDataBase? s_carPosSource;
        private static GameDataSnapshot s_carPosCache;   // car-position fields only

        private static void PopulateCarPositionsShared(StatusDataBase data, ref GameDataSnapshot snap)
        {
            lock (s_carPosLock)
            {
                if (!ReferenceEquals(data, s_carPosSource))
                {
                    var fresh = default(GameDataSnapshot);
                    fresh.TrackFolderName = ResolveTrackFolder(data);
                    PopulateCarLocations(data, ref fresh);
                    s_carPosCache = fresh;
                    s_carPosSource = data;
                }
                snap.TrackFolderName = s_carPosCache.TrackFolderName;
                snap.MapBoundsValid  = s_carPosCache.MapBoundsValid;
                snap.MapMinX = s_carPosCache.MapMinX; snap.MapMaxX = s_carPosCache.MapMaxX;
                snap.MapMinZ = s_carPosCache.MapMinZ; snap.MapMaxZ = s_carPosCache.MapMaxZ;
                snap.PlayerLocation  = s_carPosCache.PlayerLocation;
                snap.CarLocations    = s_carPosCache.CarLocations;
                snap.CarRelative     = s_carPosCache.CarRelative;
                snap.PlayerIndex     = s_carPosCache.PlayerIndex;
                snap.RadarSlotCarIds = s_carPosCache.RadarSlotCarIds;
            }
        }

        // The track's content-folder name. AC's raw Static.track is the
        // authoritative source (the content/tracks/<folder> id); SimHub's
        // TrackId is the fallback. Reflection-only (no compile-time game ref).
        // Member lookups are cached per runtime type — this runs every tick on
        // the radar path, and uncached GetProperty/GetField per call was
        // measurable churn. Statics guarded by s_carPosLock, like the slot
        // maps below.
        private static Type? s_rawType;
        private static PropertyInfo? s_rawStaticProp;
        private static Type? s_staticType;
        private static FieldInfo? s_staticTrackField;
        private static PropertyInfo? s_staticTrackProp;

        private static string? ResolveTrackFolder(StatusDataBase data)
        {
            try
            {
                object? raw = data.GetRawDataObject();
                if (raw != null)
                {
                    var rt = raw.GetType();
                    if (rt != s_rawType)
                    {
                        s_rawType = rt;
                        s_rawStaticProp = rt.GetProperty("Static");
                    }
                    object? st = s_rawStaticProp?.GetValue(raw);
                    if (st != null)
                    {
                        var stt = st.GetType();
                        if (stt != s_staticType)
                        {
                            s_staticType = stt;
                            s_staticTrackField = stt.GetField("track");
                            s_staticTrackProp = stt.GetProperty("Track");
                        }
                        var t = (s_staticTrackField?.GetValue(st) as string)
                              ?? (s_staticTrackProp?.GetValue(st) as string);
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                }
            }
            catch { /* not AC, or shape changed — fall back to SimHub's id */ }
            try { return data.TrackId; } catch { return null; }
        }

        // World position (X, Y=elevation, Z) for the local car and every
        // opponent, for the track-map location_t channels. Defensive against
        // null/short arrays (games that don't expose coordinates leave them null).
        private static void PopulateCarLocations(StatusDataBase data, ref GameDataSnapshot snap)
        {
            // SimHub's recorded per-track map (PersistantTrackerPlugin) converts
            // lap-relative coordinates to world space for games that report them
            // (iRacing); a passthrough for world-coord games (AC/AMS2) and null
            // until a lap has been recorded. Read reflectively to avoid a hard
            // SimHub.Plugins compile-time dependency.
            var map = TryGetMapRecord();

            // World bounding box of the track from SimHub's recorded map points,
            // for the per-track location_t transform (cached per track).
            var bounds = GetMapBounds(map, snap.TrackFolderName);
            if (bounds.HasValue)
            {
                snap.MapBoundsValid = true;
                snap.MapMinX = bounds.Value.minX; snap.MapMaxX = bounds.Value.maxX;
                snap.MapMinZ = bounds.Value.minZ; snap.MapMaxZ = bounds.Value.maxZ;
            }

            var pc = data.CarCoordinates;
            float px = 0f, py = 0f, pz = 0f; bool havePlayer = false;
            if (pc != null && pc.Length >= 3)
            {
                (px, py, pz) = ToWorldXyz(map, pc); havePlayer = true;
                snap.PlayerLocation = (px, py, pz);
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

            // The radar (patch/ri*) and track-map (patch/Location*) channels need
            // each car to keep a STABLE slot frame-to-frame (a car that jumps slots
            // streaks across the wheel's radar and corrupts its per-slot heading
            // history). SimHub's Opponents list is sorted by RACE POSITION, so the
            // list index is NOT stable. Two stable indexings, by data source:
            //
            //  • Live AC exposes the raw Graphics.CarCoordinates array, which is
            //    indexed by AC carId (slot i = the car with carId i) — so locs[i]
            //    is carId-indexed and slot i == carId i, matching PitHouse exactly.
            //  • A SimHub replay only records the player's CarCoordinates (raw is
            //    too short), and exposes opponents only as a position-sorted list
            //    with a stable driver Id (no carId). We map Id -> a fixed slot
            //    (player -> 0, others first-seen) so the slot stays put; the slot
            //    numbers differ from live carIds but each car renders correctly.
            bool rawCarIdIndexed = raw != null && raw.Length >= count * 3;

            (float X, float Y, float Z)[] locs;
            (float X, float Y)[] rels;
            int playerIdx = 0;

            if (rawCarIdIndexed)
            {
                locs = new (float X, float Y, float Z)[count];
                rels = new (float X, float Y)[count];
                double bestD = double.MaxValue;
                bool playerByDist = false;
                for (int i = 0; i < count; i++)             // i == carId
                {
                    (float X, float Y, float Z) abs = (raw![i * 3] != 0f || raw[i * 3 + 2] != 0f)
                        ? (raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2])
                        : (0f, 0f, 0f);
                    locs[i] = abs;
                    if (havePlayer && (abs.X != 0f || abs.Z != 0f))
                    {
                        rels[i] = (abs.X - px, abs.Z - pz);
                        double d = (abs.X - px) * (abs.X - px) + (abs.Z - pz) * (abs.Z - pz);
                        if (d < bestD) { bestD = d; playerIdx = i; playerByDist = true; }
                    }
                    var opp = opps[i];
                    if (!playerByDist && opp != null && opp.IsPlayer) playerIdx = i;
                }
            }
            else
            {
                int[] slotOf = new int[count];
                int maxSlot = 0;
                for (int i = 0; i < count; i++)
                {
                    var opp = opps[i];
                    slotOf[i] = StableOpponentSlot(opp?.Id, opp?.IsPlayer ?? false, snap.TrackFolderName);
                    if (slotOf[i] > maxSlot) maxSlot = slotOf[i];
                }
                locs = new (float X, float Y, float Z)[maxSlot + 1];
                rels = new (float X, float Y)[maxSlot + 1];
                for (int i = 0; i < count; i++)
                {
                    int s = slotOf[i];
                    if (s < 0) continue;
                    var opp = opps[i];
                    var c = opp?.Coordinates;
                    (float X, float Y, float Z) abs = (c != null && c.Length >= 3 && (c[0] != 0.0 || c[2] != 0.0))
                        ? ToWorldXyz(map, c)
                        : (0f, 0f, 0f);
                    locs[s] = abs;
                    if (havePlayer && (abs.X != 0f || abs.Z != 0f))
                        rels[s] = (abs.X - px, abs.Z - pz);
                    if (opp?.IsPlayer ?? false) playerIdx = s;
                }
            }

            snap.CarLocations = locs;
            snap.CarRelative = rels;
            snap.PlayerIndex = playerIdx;

            snap.RadarSlotCarIds = havePlayer
                ? AssignStableRadarSlots(locs, playerIdx, px, pz, snap.TrackFolderName)
                : null;
        }

        // Radar selection range (PitHouse shows opponents within ~24 m 2-D). 24 m
        // also bounds |relZ| < 24 m so the wrapping ri field stays in its principal
        // window — no un-wrap dependency for the shown set.
        private const float RadarSelectRange2DSq = 24f * 24f;
        private const int RadarMaxSlots = Dashboard.DashboardProfileStore.MaxRadarSlotIndex;   // ri1..ri47
        // Keep a car's slot for this long after it drops out of range, so brief
        // range-boundary flicker doesn't free+reassign (and reshuffle) slots.
        private const int RadarSlotHoldMs = 2000;
        private static readonly System.Collections.Generic.Dictionary<int, int> _radarSlotOf
            = new System.Collections.Generic.Dictionary<int, int>();        // carId -> slot
        private static readonly System.Collections.Generic.Dictionary<int, int> _radarLastInRangeMs
            = new System.Collections.Generic.Dictionary<int, int>();        // carId -> last in-range tick
        private static string? _radarSlotTrack;
        // Per-frame scratch, reused (guarded by s_carPosLock like the maps above).
        private static readonly bool[] s_slotUsedScratch = new bool[RadarMaxSlots + 1];
        private static readonly System.Collections.Generic.HashSet<int> s_inRangeScratch
            = new System.Collections.Generic.HashSet<int>();

        // Assign each in-range opponent a STABLE ri slot (matches PitHouse). A car
        // keeps its slot while it stays relevant, so one car entering/leaving radar
        // range never moves the others — the fix for the radar going wild the moment
        // cars started moving (re-packing every frame shuffled every slot). First car
        // into range takes the lowest free slot and holds it; the slot frees only
        // after RadarSlotHoldMs out of range. Returns slot->carId (-1 = empty); a
        // held-but-currently-out-of-range car emits 0 (its slot stays reserved).
        private static int[] AssignStableRadarSlots(
            (float X, float Y, float Z)[] locs, int playerIdx, float px, float pz, string? track)
        {
            if (track != _radarSlotTrack)
            {
                _radarSlotOf.Clear(); _radarLastInRangeMs.Clear(); _radarSlotTrack = track;
            }
            int now = Environment.TickCount;
            var slotUsed = s_slotUsedScratch;
            Array.Clear(slotUsed, 0, slotUsed.Length);
            foreach (var s in _radarSlotOf.Values) if (s >= 1 && s <= RadarMaxSlots) slotUsed[s] = true;

            var inRange = s_inRangeScratch;
            inRange.Clear();
            for (int idx = 0; idx < locs.Length; idx++)
            {
                if (idx == playerIdx) continue;
                var c = locs[idx];
                if ((c.X == 0f && c.Z == 0f) || float.IsNaN(c.X) || float.IsNaN(c.Z)) continue;
                float rx = c.X - px, rz = c.Z - pz;
                if (rx * rx + rz * rz > RadarSelectRange2DSq) continue;
                inRange.Add(idx);
                _radarLastInRangeMs[idx] = now;
                if (!_radarSlotOf.ContainsKey(idx))
                    for (int s = 1; s <= RadarMaxSlots; s++)
                        if (!slotUsed[s]) { _radarSlotOf[idx] = s; slotUsed[s] = true; break; }
            }

            System.Collections.Generic.List<int>? toFree = null;
            foreach (var kv in _radarSlotOf)
                if (!inRange.Contains(kv.Key)
                    && (!_radarLastInRangeMs.TryGetValue(kv.Key, out int t)
                        || unchecked(now - t) > RadarSlotHoldMs))
                    (toFree ??= new System.Collections.Generic.List<int>()).Add(kv.Key);
            if (toFree != null)
                foreach (var cid in toFree) { _radarSlotOf.Remove(cid); _radarLastInRangeMs.Remove(cid); }

            var arr = new int[RadarMaxSlots + 1];
            for (int i = 0; i < arr.Length; i++) arr[i] = -1;
            foreach (var kv in _radarSlotOf)
                if (inRange.Contains(kv.Key) && kv.Value >= 1 && kv.Value <= RadarMaxSlots)
                    arr[kv.Value] = kv.Key;   // emit only in-range cars; held-out slots stay empty (0)
            return arr;
        }

        // Stable per-car radar/track-map slot. SimHub sorts Opponents by race
        // position, so the list index churns; we pin each stable driver Id to a
        // fixed slot (player -> 0, the ri0 magic-header slot that's skipped;
        // others first-seen 1..N). Cleared on track change so a fresh session
        // re-packs from slot 1. Guarded by s_carPosLock.
        private static readonly Dictionary<string, int> _oppSlotById = new Dictionary<string, int>();
        private static string? _oppSlotTrack;
        private static int _oppSlotNext = 1;
        private static int StableOpponentSlot(string? id, bool isPlayer, string? track)
        {
            if (track != _oppSlotTrack)
            {
                _oppSlotById.Clear();
                _oppSlotNext = 1;
                _oppSlotTrack = track;
            }
            if (isPlayer) return 0;
            if (string.IsNullOrEmpty(id)) return -1;
            if (!_oppSlotById.TryGetValue(id!, out int s))
            {
                s = _oppSlotNext++;
                _oppSlotById[id!] = s;
            }
            return s;
        }

        // Per-car absolute ground coordinates from the raw game struct, when the
        // game exposes them (AC: DataContainer.Graphics.CarCoordinates, a flat
        // float[] of x,y,z per slot). Reflection-only so we don't take a
        // compile-time dependency on any game plugin; any failure returns null
        // and the caller falls back to SimHub's generic fields.
        private static Type? s_gfxRawType;
        private static PropertyInfo? s_rawGraphicsProp;
        private static Type? s_gfxType;
        private static FieldInfo? s_gfxCarCoordsField;

        private static float[]? TryReadRawCarCoordinates(StatusDataBase data)
        {
            try
            {
                object? raw = data.GetRawDataObject();
                if (raw == null) return null;
                var rt = raw.GetType();
                if (rt != s_gfxRawType)
                {
                    s_gfxRawType = rt;
                    s_rawGraphicsProp = rt.GetProperty("Graphics");
                }
                object? gfx = s_rawGraphicsProp?.GetValue(raw);
                if (gfx == null) return null;
                var gt = gfx.GetType();
                if (gt != s_gfxType)
                {
                    s_gfxType = gt;
                    s_gfxCarCoordsField = gt.GetField("CarCoordinates");
                }
                return s_gfxCarCoordsField?.GetValue(gfx) as float[];
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

        // ── Track world bounding box (from SimHub's recorded map) ─────────────
        // SimHub's PersistantTracker records each track's path as
        // DataRecord.CarCoordinates; its min/max give the world bounding box that
        // TrackMapTransform maps into the wheel's field window. SimHub-specific
        // and game-agnostic (no per-game files). Cached per track; recomputed
        // while unavailable (map not yet recorded) and cached once valid.
        private static string? s_boundsTrack;
        private static (double minX, double maxX, double minZ, double maxZ)? s_boundsCache;
        // Recorded-point count at the last ComputeMapBounds run — the negative
        // cache key. While the map yields no valid bounds (track still being
        // recorded), recompute only when the map has GROWN; the previous
        // valid-only cache re-enumerated + sorted the whole map every tick for
        // the entire first recorded lap.
        private static int s_boundsCoordCount = -1;
        private static bool s_boundsDiag;

        private static (double minX, double maxX, double minZ, double maxZ)? GetMapBounds(
            DataRecordBase? map, string? trackKey)
        {
            if (map == null) return null;
            if (trackKey == s_boundsTrack && s_boundsCache.HasValue) return s_boundsCache;
            int coordCount = TryGetMapCoordCount(map);
            if (trackKey == s_boundsTrack && !s_boundsCache.HasValue
                && coordCount >= 0 && coordCount == s_boundsCoordCount)
                return null;
            var b = ComputeMapBounds(map);
            s_boundsTrack = trackKey;
            s_boundsCache = b;
            s_boundsCoordCount = coordCount;
            if (b.HasValue)
                MozaLog.Info($"[AZOM] track map bounds '{trackKey}': X {b.Value.minX:F0}..{b.Value.maxX:F0} " +
                    $"Z {b.Value.minZ:F0}..{b.Value.maxZ:F0} " +
                    $"({(int)(b.Value.maxX - b.Value.minX)}×{(int)(b.Value.maxZ - b.Value.minZ)} m, SimHub recorded map)");
            return b;
        }

        private static Type? s_mapType;
        private static PropertyInfo? s_mapCoordsProp;

        // O(1) recorded-point count via ICollection, or -1 when the shape is
        // unknown (caller then recomputes per tick, the pre-cache behaviour).
        private static int TryGetMapCoordCount(DataRecordBase map)
        {
            try
            {
                var mt = map.GetType();
                if (mt != s_mapType)
                {
                    s_mapType = mt;
                    s_mapCoordsProp = mt.GetProperty("CarCoordinates");
                }
                return (s_mapCoordsProp?.GetValue(map) as System.Collections.ICollection)?.Count ?? -1;
            }
            catch { return -1; }
        }

        // Fraction trimmed off each end of each axis to drop pit-lane / off-track
        // excursions — sparse spatial tails that inflate the raw min/max but aren't
        // part of the drawn track surface (e.g. Imola's recorded X runs to 879 in
        // the pit lane vs the track's 331). Percentiles, so it's robust to however
        // many outlier points there are, up to this fraction.
        private const double BoundsTrimPct = 1.0;

        private static double Percentile(List<double> sorted, double pct)
        {
            if (sorted.Count == 0) return 0.0;
            int i = (int)Math.Round(pct / 100.0 * (sorted.Count - 1));
            if (i < 0) i = 0; else if (i >= sorted.Count) i = sorted.Count - 1;
            return sorted[i];
        }

        private static (double minX, double maxX, double minZ, double maxZ)? ComputeMapBounds(DataRecordBase map)
        {
            try
            {
                var mt = map.GetType();
                if (mt != s_mapType)
                {
                    s_mapType = mt;
                    s_mapCoordsProp = mt.GetProperty("CarCoordinates");
                }
                var coords = s_mapCoordsProp?.GetValue(map) as System.Collections.IEnumerable;
                if (coords == null) return null;
                bool relative = false;
                try { relative = (map.GetType().GetProperty("HasRelativeCarCoordinates")?.GetValue(map) as bool?) ?? false; }
                catch { }

                var xs = new List<double>(2048);
                var zs = new List<double>(2048);
                foreach (var pt in coords)
                {
                    if (pt == null || !TryPointXZ(pt, out double x, out double z)) continue;
                    if (relative)
                    {
                        try { var a = map.ToAbsoluteCoordinates(new[] { x, 0.0, z }); if (a != null && a.Length >= 3) { x = a[0]; z = a[2]; } }
                        catch { }
                    }
                    xs.Add(x); zs.Add(z);
                }
                if (xs.Count < 20) return null;
                xs.Sort(); zs.Sort();

                double p = BoundsTrimPct;
                double minX = Percentile(xs, p), maxX = Percentile(xs, 100 - p);
                double minZ = Percentile(zs, p), maxZ = Percentile(zs, 100 - p);

                if (!s_boundsDiag)
                {
                    s_boundsDiag = true;
                    MozaLog.Info($"[AZOM] map bounds DIAG: n={xs.Count} trim={p}% | " +
                        $"X raw[{xs[0]:F0}..{xs[xs.Count - 1]:F0}] p0.5[{Percentile(xs, 0.5):F0}..{Percentile(xs, 99.5):F0}] " +
                        $"p1[{Percentile(xs, 1):F0}..{Percentile(xs, 99):F0}] p2[{Percentile(xs, 2):F0}..{Percentile(xs, 98):F0}] " +
                        $"p5[{Percentile(xs, 5):F0}..{Percentile(xs, 95):F0}] | " +
                        $"Z raw[{zs[0]:F0}..{zs[zs.Count - 1]:F0}] p1[{Percentile(zs, 1):F0}..{Percentile(zs, 99):F0}] " +
                        $"p2[{Percentile(zs, 2):F0}..{Percentile(zs, 98):F0}] p5[{Percentile(zs, 5):F0}..{Percentile(zs, 95):F0}]");
                }
                if (maxX - minX < 10.0 || maxZ - minZ < 10.0) return null;
                return (minX, maxX, minZ, maxZ);
            }
            catch { return null; }
        }

        // Extract (X, Z) from a recorded coordinate, however SimHub shapes it
        // (double[]/float[] [x,y,z], or a point type with X/Z members). Member
        // lookups cached per point type — ComputeMapBounds calls this once per
        // recorded point.
        private static Type? s_ptType;
        private static FieldInfo? s_ptValueField;
        private static PropertyInfo? s_ptXProp, s_ptZProp;
        private static FieldInfo? s_ptXField, s_ptZField;

        private static bool TryPointXZ(object pt, out double x, out double z)
        {
            x = 0; z = 0;
            if (pt is double[] da && da.Length >= 3) { x = da[0]; z = da[2]; return true; }
            if (pt is float[] fa && fa.Length >= 3) { x = fa[0]; z = fa[2]; return true; }
            try
            {
                var ty = pt.GetType();
                if (ty != s_ptType)
                {
                    s_ptType = ty;
                    // SimHub recorded-map point (GameReaderCommon.PositionItem): the
                    // world coordinate lives in .Value as double[3] { X, Y, Z }.
                    s_ptValueField = ty.GetField("Value");
                    s_ptXProp = ty.GetProperty("X") ?? ty.GetProperty("x");
                    s_ptZProp = ty.GetProperty("Z") ?? ty.GetProperty("z");
                    s_ptXField = ty.GetField("X") ?? ty.GetField("x");
                    s_ptZField = ty.GetField("Z") ?? ty.GetField("z");
                }
                if (s_ptValueField != null)
                {
                    var val = s_ptValueField.GetValue(pt);
                    if (val is double[] vd && vd.Length >= 3) { x = vd[0]; z = vd[2]; return true; }
                    if (val is float[] vff && vff.Length >= 3) { x = vff[0]; z = vff[2]; return true; }
                }
                if (s_ptXProp != null && s_ptZProp != null)
                { x = Convert.ToDouble(s_ptXProp.GetValue(pt)); z = Convert.ToDouble(s_ptZProp.GetValue(pt)); return true; }
                if (s_ptXField != null && s_ptZField != null)
                { x = Convert.ToDouble(s_ptXField.GetValue(pt)); z = Convert.ToDouble(s_ptZField.GetValue(pt)); return true; }
            }
            catch { }
            return false;
        }

        // Project a car's coordinate array to world (X, Y=elevation, Z). When a
        // track map is recorded for a lap-relative-coordinate game, route through
        // DataRecordBase.ToAbsoluteCoordinates (→ world space); a passthrough for
        // world-coordinate games. Mirrors RadarItem.UpdateData. Defensive: any
        // failure or short result keeps the raw [0],[1],[2].
        private static (float X, float Y, float Z) ToWorldXyz(DataRecordBase? map, double[] c)
        {
            double x = c[0], y = c.Length > 1 ? c[1] : 0.0, z = c.Length > 2 ? c[2] : 0.0;
            if (map != null)
            {
                try
                {
                    double[]? a = map.ToAbsoluteCoordinates(c);
                    if (a != null && a.Length >= 3) { x = a[0]; y = a[1]; z = a[2]; }
                }
                catch { /* keep raw [0],[1],[2] */ }
            }
            return ((float)x, (float)y, (float)z);
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
