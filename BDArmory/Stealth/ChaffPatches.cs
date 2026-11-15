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
    /// Chaff works in the notch (the target beaming, so that chaff at rest shares its doppler) and for small radar cross-sections,
    /// measured at the aspect the sensor sees; outside the seeker's velocity gate it does little.
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
            public float Pull, PullTime;   // smart seekers: pull gathered from recent bundles (StealthMath.ChaffPull)
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
            // Doppler overlap between the target and its chaff, which stops dead in the air: only while the target's speed along the
            // line of sight is small (beaming, or nearly standing still) does the bloom fall inside the seeker's velocity gate.
            beam = StealthMath.DopplerOverlap(Mathf.Abs(Vector3.Dot(vel, los.normalized)));
            rcs = SkinRcs(ti, sensor, range);
            return StealthMath.ChaffSeduction(Settings.ChaffChance, effectivity, Settings.CmFloor, beam, rcs);
        }

        /// <summary>
        /// Chance that one bundle tv dispenses now seduces the radar missile m, at the current geometry, and what it would be beaming (inside
        /// the seeker's doppler gate); seduced when m is already following chaff. Negative when chaff seduction is off.
        /// </summary>
        internal static float BundleChance(MissileBase m, Vessel tv, out float beamingChance, out bool seduced)
        {
            seduced = m != null && tracks.TryGetValue(m, out Track t) && t.Target == tv && Time.time < t.Until;
            beamingChance = -1f;
            if (!Settings.Enabled || !Settings.ChaffSeduction || m == null || tv == null) return -1f;
            float eff = m.chaffEffectivity;
            ModuleRadar radar = !m.ActiveRadar && m.radarTarget.exists ? m.radarTarget.lockedByRadar : null;
            if (radar != null) eff *= radar.radarChaffClutterFactor;
            if (m.smartSeeker && m.HomingOnJam) { beamingChance = 0f; return 0f; } // Homing on our jamming: chaff is no echo it listens for.
            if (m.smartSeeker)
            {
                // The share of the pull needed that one bundle gives now, and would give dead on the beam, on top of what is already gathered.
                TargetInfo info = m.radarTarget.exists ? m.radarTarget.targetInfo : tv.gameObject.GetComponent<TargetInfo>();
                float gathered = tracks.TryGetValue(m, out Track held) && held.Target == tv ? StealthMath.PullAfter(held.Pull, Time.time - held.PullTime) : 0f;
                float one = SmartPull(m, tv, info, 1, eff, out _, out float skin, out _);
                beamingChance = Mathf.Clamp01(gathered + StealthMath.ChaffPull(1, eff, Settings.CmFloor, 0f, m.velocityGate, skin));
                return one > 0f ? Mathf.Clamp01(gathered + one) : 0f;
            }
            float p = Chance(tv, m.radarTarget.exists ? m.radarTarget.targetInfo : tv.gameObject.GetComponent<TargetInfo>(), m.transform.position, eff, out _, out float rcs, out _);
            beamingChance = StealthMath.ChaffSeduction(Settings.ChaffChance, eff, Settings.CmFloor, 1f, rcs);
            return p;
        }

        // ---- missiles ----

        internal static void RadarTargetPostfix(MissileBase __instance)
        {
            MissileBase m = __instance;
            Vessel tv = m.TargetAcquired && m.radarTarget.exists ? m.radarTarget.vessel : null;
            m.UpdateHomeOnJam(tv);
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
            if (m.smartSeeker && tv != null && (Resolved(m, tv) || m.HomingOnJam)) { t.Until = now; return; } // Close enough to pick the craft out again, or homing on its jamming.
            m.TargetPosition = VectorUtils.GetWorldSurfacePostion(t.Geo, t.Body) + t.Velocity * (now - t.From);
            m.TargetVelocity = t.Velocity;
            m.TargetAcceleration = Vector3.zero;
        }

        /// <summary>Pull of bundles from tv on the smart seeker m's track, at the current geometry.</summary>
        static float SmartPull(MissileBase m, Vessel tv, TargetInfo ti, int bundles, float effectivity, out float radial, out float rcs, out float range, bool acrossLineOfSight = false)
        {
            // The track is held from wherever it is measured: the missile's own seeker once that is active, the launching radar before.
            ModuleRadar radar = !m.ActiveRadar && m.radarTarget.exists ? m.radarTarget.lockedByRadar : null;
            Vector3 sensor = radar != null ? radar.transform.position : m.transform.position;
            Vector3 los = (Vector3)tv.CoM - sensor;
            range = los.magnitude;
            radial = acrossLineOfSight ? 0f : Vector3.Dot(tv.Velocity(), los.normalized);
            rcs = 0f;
            if (Resolved(m, tv) || m.HomingOnJam) return 0f;
            rcs = SkinRcs(ti, sensor, range);
            return StealthMath.ChaffPull(bundles, effectivity, Settings.CmFloor, radial, m.velocityGate, rcs);
        }

        /// <summary>
        /// Smart seekers: bundles tv has to release together now to take m's track (0: none can at this geometry), and whether it would
        /// only be re-cued by its launcher's lock. acrossLineOfSight: the count if tv were flying exactly across the tracking sensor's
        /// line of sight instead of as it is.
        /// </summary>
        internal static int SmartBundlesNeeded(MissileBase m, Vessel tv, out bool recued, bool acrossLineOfSight = false)
        {
            recued = false;
            if (!Settings.Enabled || !Settings.ChaffSeduction || m == null || tv == null || Resolved(m, tv) || m.HomingOnJam) return 0;
            float eff = m.chaffEffectivity;
            ModuleRadar radar = !m.ActiveRadar && m.radarTarget.exists ? m.radarTarget.lockedByRadar : null;
            if (radar != null) eff *= radar.radarChaffClutterFactor;
            TargetInfo info = m.radarTarget.exists ? m.radarTarget.targetInfo : tv.gameObject.GetComponent<TargetInfo>();
            float gathered = tracks.TryGetValue(m, out Track held) && held.Target == tv ? StealthMath.PullAfter(held.Pull, Time.time - held.PullTime) : 0f;
            recued = Recued(m, tv);
            return StealthMath.ChaffBundlesNeeded(gathered, SmartPull(m, tv, info, 1, eff, out _, out _, out _, acrossLineOfSight));
        }

        /// <summary>Smart seekers: whether m is close enough to tv (MissileBase.resolveRange) to tell the craft from a chaff bloom beside it.</summary>
        internal static bool Resolved(MissileBase m, Vessel tv) =>
            m.resolveRange > 0f && m.vessel != null && ((Vector3)tv.CoM - (Vector3)m.vessel.CoM).sqrMagnitude < m.resolveRange * m.resolveRange;

        /// <summary>Smart seekers: whether m's track of its target is held by its own seeker (else by the radar that launched it).</summary>
        internal static bool TrackedByOwnSeeker(MissileBase m) => m.ActiveRadar || !m.radarTarget.exists || m.radarTarget.lockedByRadar == null;

        /// <summary>Whether the radar that launched m still holds its own lock on tv, to put the missile back on it over a data link.</summary>
        static bool Recued(MissileBase m, Vessel tv)
        {
            if (!m.datalink || !m.ActiveRadar) return false; // Before it goes active the launcher's lock is the track being pulled.
            return m.SupportingLock(tv, out _);
        }

        /// <summary>
        /// Smart seekers: fresh bundles add their pull to what the track has gathered; at 1 the track goes to the bloom, for as long as the
        /// margin holds it. Nothing is rolled: the same release in the same geometry against the same seeker always ends the same way.
        /// </summary>
        static void Gather(MissileBase m, Vessel tv, TargetInfo ti, Bloom b, Track t, int bundles, float effectivity, float now)
        {
            float added = SmartPull(m, tv, ti, bundles, effectivity, out float radial, out float rcs, out float range);
            t.Pull = StealthMath.PullAfter(t.Pull, now - t.PullTime) + added;
            t.PullTime = now;
            if (t.Pull < 1f || now < t.Until) return;
            bool recued = Recued(m, tv);
            t.From = b.Time; t.Geo = b.Geo; t.Velocity = b.Velocity; t.Body = b.Body;
            t.Until = now + StealthMath.ChaffHold(t.Pull, recued);
            Seductions++;
            m.RecordShotDecoy();
            if (recued) m.RecordShotSupport(true);
            Debug.Log($"[BDAStealth] Chaff: {m.shortName} seduced off {tv.vesselName} for {t.Until - now:0.0} s (pull {t.Pull:0.00}, radial speed {radial:0} m/s, gate {m.velocityGate:0} m/s, RCS {rcs:0.##} m², range {range:0} m{(recued ? ", re-cued by data link" : "")})");
            if (Notify.Involves(m.SourceVessel, tv)) Notify.Post(recued ? $"{m.shortName} · chaff · re-cued · {tv.vesselName}" : $"{m.shortName} · chaff · {tv.vesselName}");
            t.Pull = 0f;
        }

        static void Roll(MissileBase m, Vessel tv, TargetInfo ti, Bloom b, Track t, int bundles, float effectivity, float now)
        {
            if (m.smartSeeker) { Gather(m, tv, ti, b, t, bundles, effectivity, now); return; }
            float p = Chance(tv, ti, m.transform.position, effectivity, out float beam, out float rcs, out float range);
            for (int i = 0; i < bundles; i++)
            {
                if (!(Random.value < p)) continue;
                t.From = b.Time; t.Geo = b.Geo; t.Velocity = b.Velocity; t.Body = b.Body;
                t.Until = now + Random.Range(1.5f, 3.5f);
                Seductions++;
                m.RecordShotDecoy();
                Debug.Log($"[BDAStealth] Chaff: {m.shortName} seduced off {tv.vesselName} for {t.Until - now:0.0} s (chance {100f * p:0}%, doppler overlap {beam:0.00}, RCS {rcs:0.##} m², range {range:0} m)");
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
                Debug.Log($"[BDAStealth] Chaff: {radar.vessel.vesselName} radar lock on {tv.vesselName} broken (chance {100f * p:0}%, doppler overlap {beam:0.00}, RCS {rcs:0.##} m², range {range:0} m)");
                if (Notify.Involves(radar.vessel, tv)) Notify.Post($"LOCK LOST · chaff · {tv.vesselName}");
                radar.UnlockTargetAt(i);
                return;   // the lock list changed; the rest are checked next frame
            }
        }
    }
}
