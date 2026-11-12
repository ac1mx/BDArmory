using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BDArmory.CounterMeasure;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.Targeting;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>
    /// Chaff seduction. Stock chaff only jitters the aim point of a radar missile; the track itself never leaves the target.
    /// Here each bundle gets one chance against every radar track on the dispensing craft:
    ///  - a radar missile (on its own seeker, riding the launcher's lock, or on inertial mid-course updates) steers for the
    ///    chaff bloom for a few seconds, then returns to the target if it still can;
    ///  - an aircraft or ground radar holding a lock drops it and has to lock again.
    /// Chaff works best while beaming and for small radar cross-sections, measured at the aspect the sensor sees.
    /// </summary>
    internal static class ChaffPatches
    {
        sealed class Bloom
        {
            public int Serial;          // bundles dispensed by this vessel so far
            public float Time;
            public Vector3d Geo;        // lat, lon, alt of the latest bundle
            public Vector3 Velocity;    // only kept in vacuum; in air the bloom stops with the air mass
            public CelestialBody Body;
        }

        sealed class Track
        {
            public Vessel Target;
            public int Serial;          // bundles of Target already rolled against
            public float Until, From;
            public Vector3d Geo;
            public Vector3 Velocity;
            public CelestialBody Body;
        }

        sealed class RadarLocks { public readonly ConditionalWeakTable<Vessel, StrongBox<int>> Seen = new ConditionalWeakTable<Vessel, StrongBox<int>>(); }

        static readonly ConditionalWeakTable<Vessel, Bloom> blooms = new ConditionalWeakTable<Vessel, Bloom>();
        static readonly ConditionalWeakTable<MissileBase, Track> tracks = new ConditionalWeakTable<MissileBase, Track>();
        static readonly ConditionalWeakTable<ModuleRadar, RadarLocks> radarLocks = new ConditionalWeakTable<ModuleRadar, RadarLocks>();
        const float FreshBundle = 0.5f;   // s: only a bundle dispensed this recently is still next to the target
        const int MaxRolls = 4;           // per track per update, when several dispensers fire together
        public static int Seductions, LockBreaks;

        internal static void ChaffPostfix(VesselChaffInfo __instance)
        {
            Vessel v = __instance != null ? __instance.GetComponent<Vessel>() : null;
            if (v == null || v.mainBody == null) return;
            Bloom b = blooms.GetValue(v, _ => new Bloom());
            b.Serial++;
            b.Time = Time.time;
            b.Body = v.mainBody;
            b.Geo = VectorUtils.WorldPositionToGeoCoords(v.CoM, v.mainBody);
            b.Velocity = v.InVacuum() ? (Vector3)v.Velocity() : Vector3.zero;
        }

        /// <summary>
        /// Skin RCS the bloom competes with, from the sensor's side: after stealth, without the noise a jammer adds to
        /// detection, and at the aspect the sensor sees when BDArmory's aspect RCS is on.
        /// </summary>
        static float SkinRcs(TargetInfo ti, Vector3 sensor, float distance)
        {
            if (ti == null) return 1f;
            float reduced = ti.radarRCSReducedSignature;
            if (!BDArmorySettings.ASPECTED_RCS || ti.radarSignatureMatrix == null || !(ti.radarModifiedSignature > 0f)) return reduced;
            return RadarUtils.GetVesselRadarSignatureAtAspect(ti, sensor, distance) * reduced / ti.radarModifiedSignature;
        }

        /// <summary>Chance for one bundle from tv against a radar track held from sensor.</summary>
        static float Chance(Vessel tv, TargetInfo ti, Vector3 sensor, float effectivity, out float beam, out float rcs, out float range)
        {
            Vector3 los = (Vector3)tv.CoM - sensor;
            range = los.magnitude;
            Vector3 vel = tv.Velocity();
            // Doppler separation between the target and its chaff: none while beaming or standing still.
            beam = 1f - Mathf.Clamp01(Mathf.Abs(Vector3.Dot(vel, los.normalized)) / Mathf.Max(vel.magnitude, 1f));
            rcs = SkinRcs(ti, sensor, range);
            return StealthMath.ChaffSeduction(Settings.ChaffChance, effectivity, Settings.CmFloor, beam, rcs);
        }

        // ---- missiles ----

        internal static void RadarTargetPostfix(MissileBase __instance)
        {
            MissileBase m = __instance;
            Vessel tv = m.TargetAcquired && m.radarTarget.exists ? m.radarTarget.vessel : null;
            float eff = m.chaffEffectivity;
            if (tv != null && !m.ActiveRadar)
            {
                ModuleRadar radar = m.radarTarget.lockedByRadar;   // semi-active / datalink: the launching radar holds the track
                if (radar != null) eff *= radar.radarChaffClutterFactor;
            }
            Process(m, tv, tv != null ? m.radarTarget.targetInfo : null, eff);
        }

        /// <summary>Inertial mid-course: the missile flies to the position its launcher's sensors report, so chaff can pull that too.</summary>
        internal static void InertialTargetPostfix(MissileBase __instance)
        {
            MissileBase m = __instance;
            TargetInfo ti = m.TargetAcquired && m.gpsUpdates >= 0f ? m.targetVessel : null;
            Process(m, ti != null ? ti.Vessel : null, ti, m.chaffEffectivity);
        }

        static void Process(MissileBase m, Vessel tv, TargetInfo ti, float effectivity)
        {
            if (!Settings.Enabled || !Settings.ChaffSeduction || !m.HasFired) return;
            float now = Time.time;
            tracks.TryGetValue(m, out Track t);

            if (tv != null)
            {
                blooms.TryGetValue(tv, out Bloom b);
                int serial = b != null ? b.Serial : 0;
                if (t == null) { t = tracks.GetValue(m, _ => new Track()); t.Target = tv; t.Serial = serial; }
                else if (t.Target != tv) { t.Target = tv; t.Serial = serial; t.Until = 0f; }   // new target: its earlier bundles don't count
                else if (serial != t.Serial)
                {
                    int bundles = Mathf.Min(serial - t.Serial, MaxRolls);
                    t.Serial = serial;
                    if (now - b.Time < FreshBundle) Roll(m, tv, ti, b, t, bundles, effectivity, now);
                }
            }

            if (t == null || now >= t.Until || !m.TargetAcquired || t.Body == null) return;
            m.TargetPosition = VectorUtils.GetWorldSurfacePostion(t.Geo, t.Body) + t.Velocity * (now - t.From);
            m.TargetVelocity = t.Velocity;
            m.TargetAcceleration = Vector3.zero;
        }

        static void Roll(MissileBase m, Vessel tv, TargetInfo ti, Bloom b, Track t, int bundles, float effectivity, float now)
        {
            float p = Chance(tv, ti, m.transform.position, effectivity, out float beam, out float rcs, out float range);
            for (int i = 0; i < bundles; i++)
            {
                if (!(Random.value < p)) continue;
                t.From = b.Time; t.Geo = b.Geo; t.Velocity = b.Velocity; t.Body = b.Body;
                t.Until = now + Random.Range(1.5f, 3.5f);
                Seductions++;
                Debug.Log($"[BDAStealth] Chaff: {m.shortName} seduced off {tv.vesselName} for {t.Until - now:0.0} s (chance {100f * p:0}%, beam {beam:0.00}, RCS {rcs:0.##} m², range {range:0} m)");
                if (Notify.Involves(m.SourceVessel, tv)) Notify.Post($"{m.shortName} · chaff · {tv.vesselName}");
                return;
            }
        }

        // ---- radar locks ----

        /// <summary>Called once per radar per physics frame: bundles from a locked target can break that lock.</summary>
        public static void RadarLocksUpdate(ModuleRadar radar)
        {
            if (!Settings.Enabled || !Settings.ChaffSeduction || !radar.locked || Sensors.IsSonar(radar)) return;
            List<TargetSignatureData> locks = radar.lockedTargets;
            if (locks == null) return;
            RadarLocks state = radarLocks.GetValue(radar, _ => new RadarLocks());
            float now = Time.time;
            for (int i = 0; i < locks.Count; i++)
            {
                Vessel tv = locks[i].vessel;
                if (tv == null) continue;
                blooms.TryGetValue(tv, out Bloom b);
                int serial = b != null ? b.Serial : 0;
                if (!state.Seen.TryGetValue(tv, out StrongBox<int> seen)) { state.Seen.Add(tv, new StrongBox<int>(serial)); continue; }
                if (seen.Value == serial) continue;
                int bundles = Mathf.Min(serial - seen.Value, MaxRolls);
                seen.Value = serial;
                if (b == null || now - b.Time >= FreshBundle) continue;
                float p = Chance(tv, locks[i].targetInfo, radar.transform.position, radar.radarChaffClutterFactor, out float beam, out float rcs, out float range);
                bool broken = false;
                for (int k = 0; k < bundles && !broken; k++) broken = Random.value < p;
                if (!broken) continue;
                LockBreaks++;
                Debug.Log($"[BDAStealth] Chaff: {radar.vessel.vesselName} radar lock on {tv.vesselName} broken (chance {100f * p:0}%, beam {beam:0.00}, RCS {rcs:0.##} m², range {range:0} m)");
                if (Notify.Involves(radar.vessel, tv)) Notify.Post($"LOCK LOST · chaff · {tv.vesselName}");
                radar.UnlockTargetAt(i);
                return;   // the lock list changed; the rest are checked next frame
            }
        }
    }
}
