using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Radar
{
    /// <summary>
    /// The receiver's picture of who is emitting at this craft, built from the pings it receives: per emitting craft whether its radar
    /// is sweeping past (search), holding a lock (lock), has launched on that lock (launch) or is a missile's own seeker (missile); the
    /// bearing, which a receiver measures well; and a range it can only estimate from signal strength, off by up to 30% at first and
    /// better the longer the emitter is heard. Radars with rwrDetectability below 1 (low probability of intercept) are heard only from
    /// closer in. AI pilots plan with this picture (PilotRWR) and the on-screen cues show its top threat.
    /// </summary>
    public partial class RadarWarningReceiver
    {
        public enum EmitterStates { Search, Lock, Launch, Missile }

        public sealed class Emitter
        {
            public Vessel vessel;
            public RWRThreatTypes type;
            public string radar;            // The emitting radar's name (the receiver's identification of it).
            public bool friendly = true, missile; // Friendly until its side is known to be hostile.
            public Vector3 position;        // Where it was when last heard.
            public float firstHeard, lastHeard, lockHeard = -100f, launchHeard = -100f, reported = -100f;
            public float rangeError;        // Share by which the first range estimate is off.
            public EmitterStates State => missile ? EmitterStates.Missile
                : Time.time - launchHeard < launchMemory ? EmitterStates.Launch
                : Time.time - lockHeard < lockMemory ? EmitterStates.Lock : EmitterStates.Search;
        }

        const float lockMemory = 3f, launchMemory = 45f, emitterMemory = 10f, rangeErrorMax = 0.3f, rangeSettleTime = 20f, reportInterval = 2f;
        readonly List<Emitter> emitters = new List<Emitter>();
        public IReadOnlyList<Emitter> Emitters => emitters;
        /// <summary>Totals over all receivers, for the pilot AI's metrics log.</summary>
        public static int Spikes, LaunchWarnings, QuietRadarPingsMissed, CuedReports;

        // Set around a ping sent by a radar (PingFromRadar), for the receivers it reaches.
        static bool pingIsLock;
        static ModuleRadar pingRadar;

        /// <summary>
        /// A radar's emission reaching a craft: a sweep or, with isLock, a lock held on it. distance in km. A radar that is hard to
        /// intercept (rwrDetectability below 1) is only heard inside that share of the range it could be heard from otherwise.
        /// </summary>
        public static void PingFromRadar(Vessel v, Vector3 source, ModuleRadar radar, bool isLock, float distance, float persistTime, Vessel vSource)
        {
            if (radar.rwrDetectability < 1f)
            {
                float reach = radar.rwrDetectability * (isLock ? radar.radarMaxDistanceLockTrack : radar.radarMaxDistanceDetect) * RadarUtils.RWR_PING_RANGE_FACTOR;
                if (distance > reach) { ++QuietRadarPingsMissed; return; }
            }
            pingIsLock = isLock;
            pingRadar = radar;
            try { PingRWR(v, source, radar.rwrType, persistTime, vSource); }
            finally { pingIsLock = false; pingRadar = null; }
        }

        Emitter EmitterFor(Vessel source, bool create)
        {
            for (int i = 0; i < emitters.Count; ++i)
                if (emitters[i].vessel == source) return emitters[i];
            if (!create) return null;
            var e = new Emitter
            {
                vessel = source,
                firstHeard = Time.time,
                rangeError = rangeErrorMax * (((source.id.GetHashCode() ^ vessel.id.GetHashCode()) & 0xFF) / 127.5f - 1f)
            };
            emitters.Add(e);
            return e;
        }

        /// <summary>
        /// Whether an emitter's side is hostile to ours, looked up each time it is heard: a craft's team may not be set yet when it is
        /// first heard (just spawned), and one whose side isn't known is never treated as hostile.
        /// </summary>
        void UpdateSide(Emitter e)
        {
            var weaponManager = vessel.ActiveController().WM;
            Vessel craft = e.vessel;
            var missile = VesselModuleRegistry.GetMissileBase(craft);
            if (missile != null && missile.SourceVessel != null) craft = missile.SourceVessel;
            var theirs = craft.ActiveController().WM;
            e.friendly = !(weaponManager != null && theirs != null && weaponManager.Team != null && theirs.Team != null && weaponManager.Team.IsEnemy(theirs.Team));
        }

        /// <summary>The emitter picture's share of a received ping (called once the receiver has accepted it).</summary>
        void NoteEmitter(Vector3 source, RWRThreatTypes type, Vessel vSource)
        {
            if (vSource == null || vSource == vessel || type == RWRThreatTypes.Jamming || type == RWRThreatTypes.MWS) return;
            var e = EmitterFor(vSource, true);
            e.type = type;
            e.position = source;
            e.lastHeard = Time.time;
            UpdateSide(e);
            if (pingRadar != null) e.radar = pingRadar.radarName;
            if (type == RWRThreatTypes.MissileLock || type == RWRThreatTypes.TorpedoLock) { e.missile = true; return; }
            if (type == RWRThreatTypes.MissileLaunch || type == RWRThreatTypes.Torpedo) return; // A seeker going active: the missile lock pings follow.
            if (!pingIsLock) return;
            if (Time.time - e.lockHeard > lockMemory && !e.friendly)
            {
                ++Spikes;
                if (BDModulePilotAI.steeringMetricsEnabled)
                    Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[BDArmory.RadarWarningReceiver]: Spike {0} locked by {1} ({2}) range={3:F0} estimate={4:F0}",
                        vessel.vesselName, vSource.vesselName, e.radar ?? "?", (source - vessel.CoM).magnitude, EstimatedRange(e)));
            }
            e.lockHeard = Time.time;
            // A lock gives its own direction away: the craft holding it is known to our side from here on.
            if (!e.friendly && Time.time - e.reported > reportInterval && BDModulePilotAI.NewAwarenessFor(vessel))
            {
                var weaponManager = vessel.ActiveController().WM;
                if (weaponManager != null) { BDATargetManager.ReportVessel(vSource, weaponManager); e.reported = Time.time; ++CuedReports; }
            }
        }

        /// <summary>A launch warning accepted by the receiver: the craft that launched (the missile's source) has fired on us.</summary>
        void NoteLaunch(Vector3 source, Vessel missileVessel)
        {
            var missile = missileVessel != null ? VesselModuleRegistry.GetMissileBase(missileVessel) : null;
            Vessel launcher = missile != null && missile.SourceVessel != null ? missile.SourceVessel : null;
            if (launcher == null || launcher == vessel) return;
            var e = EmitterFor(launcher, true);
            e.position = source;
            e.lastHeard = Time.time;
            UpdateSide(e);
            if (Time.time - e.launchHeard > 2f && !e.friendly)
            {
                ++LaunchWarnings;
                if (BDModulePilotAI.steeringMetricsEnabled)
                    Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[BDArmory.RadarWarningReceiver]: Launch {0} warned of a launch by {1} range={2:F0} estimate={3:F0}",
                        vessel.vesselName, launcher.vesselName, (source - vessel.CoM).magnitude, EstimatedRange(e)));
            }
            e.launchHeard = Time.time;
        }

        void CleanEmitters()
        {
            for (int i = emitters.Count - 1; i >= 0; --i)
            {
                var e = emitters[i];
                if (e.vessel == null || (Time.time - e.lastHeard > emitterMemory && Time.time - e.launchHeard > launchMemory)) emitters.RemoveAt(i);
            }
        }

        /// <summary>What the receiver holds on a craft, or null if it isn't hearing it.</summary>
        public Emitter EmitterOf(Vessel v) => v != null && rwrEnabled ? EmitterFor(v, false) : null;

        /// <summary>
        /// The receiver's range to an emitter (m): the true range off by the emitter's error, which fades as it keeps being heard (a
        /// launch or a held lock gives more to measure, so those settle at once).
        /// </summary>
        public float EstimatedRange(Emitter e)
        {
            float error = e.State >= EmitterStates.Launch ? 0f : e.rangeError * Mathf.Exp(-(Time.time - e.firstHeard) / rangeSettleTime);
            return (e.position - vessel.CoM).magnitude * (1f + error);
        }

        /// <summary>The most dangerous hostile emitter that isn't a missile: by state (launch, lock, search), then by estimated range.</summary>
        public Emitter TopThreat()
        {
            Emitter top = null;
            float topRange = float.MaxValue;
            for (int i = 0; i < emitters.Count; ++i)
            {
                var e = emitters[i];
                if (e.friendly || e.missile || e.vessel == null) continue;
                float range = EstimatedRange(e);
                if (top == null || e.State > top.State || (e.State == top.State && range < topRange)) { top = e; topRange = range; }
            }
            return top;
        }

        /// <summary>Hostile craft the receiver hears holding a lock on us or having launched on one.</summary>
        public int LockCount()
        {
            int count = 0;
            for (int i = 0; i < emitters.Count; ++i)
                if (!emitters[i].friendly && !emitters[i].missile && emitters[i].State >= EmitterStates.Lock) ++count;
            return count;
        }
    }
}
