using UnityEngine;

using BDArmory.Radar;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// What the pilot takes from the radar warning receiver's emitter picture (RadarWarningReceiver.Threats), in place of knowing it
    /// outright:
    ///  - which enemies count as threats: one the receiver hears, or one close enough to see; an enemy that is neither (a quiet radar,
    ///    or one not emitting, beyond sight) isn't planned against until it shows itself;
    ///  - whether an incoming missile's own seeker is on: only once the receiver hears it;
    ///  - spike crank: locked by the target with no shot of our own yet, fly with it at the edge of the radar's view instead of
    ///    straight down its missile's path, until we have a shot.
    /// A lock heard also puts the craft holding it on our side's picture (the receiver reports it).
    /// </summary>
    public partial class BDModulePilotAI
    {
        const float visualThreatRange = 10000f, spikeShotFloor = 0.15f;
        int spikeCrankTicks, rwrUnseenThreats;

        // Comparison in one fight: GameData/BDArmory/PluginData/test_pilot_awareness.on holds a word; only craft whose name contains it
        // use the warning-receiver picture, the no-escape shot, the lofted launch, the double notch and the drag and masking runs.
        static readonly string awarenessTestCraft = ReadAwarenessTestCraft();
        static string ReadAwarenessTestCraft()
        {
            string path = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/test_pilot_awareness.on");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
        }
        public static bool NewAwarenessFor(Vessel v) => string.IsNullOrEmpty(awarenessTestCraft) || (v != null && v.vesselName.Contains(awarenessTestCraft));
        bool NewAwareness => NewAwarenessFor(vessel) && Knows(SkillFeature.Awareness);

        static RadarWarningReceiver ReceiverOf(MissileFire weaponManager) =>
            weaponManager != null && weaponManager.rwr != null && weaponManager.rwr.rwrEnabled ? weaponManager.rwr : null;

        /// <summary>Whether we have any way of knowing an enemy is there: heard by the receiver, or within sight. Without a receiver, assumed so.</summary>
        bool KnownThreat(MissileFire weaponManager, Vessel other, float distance)
        {
            var rwr = NewAwareness ? ReceiverOf(weaponManager) : null;
            return rwr == null || distance < visualThreatRange || rwr.EmitterOf(other) != null;
        }

        /// <summary>Whether the receiver hears a missile's own seeker (without a receiver: whether it is on).</summary>
        bool SeekerHeardActive(MissileFire weaponManager, MissileBase incoming)
        {
            var rwr = NewAwareness ? ReceiverOf(weaponManager) : null;
            return rwr != null ? rwr.IsRadarMissileDetected(incoming.vessel) : incoming.ActiveRadar;
        }

        /// <summary>Whether the receiver hears the craft holding a lock on us (or having launched on one).</summary>
        bool LockedBy(MissileFire weaponManager, Vessel v)
        {
            var emitter = ReceiverOf(weaponManager)?.EmitterOf(v);
            return emitter != null && emitter.State >= RadarWarningReceiver.EmitterStates.Lock;
        }

        /// <summary>
        /// Locked by the target beyond close range, with nothing of ours in the air, no shot of our own yet and the target's shot worth
        /// something: crank. The lock is the warning that its launch comes next, and a missile launched at a cranking craft has further
        /// to fly; our radar keeps the target, and the nose goes back on it as soon as we have the shot.
        /// </summary>
        bool SpikeCrank(MissileFire weaponManager, Vessel v, Vector3 vectorToTarget, float distanceToTarget, float threshold, ref Vector3 target)
        {
            if (!NewAwareness || !LockedBy(weaponManager, v) || weaponManager.missileIsIncoming) return false;
            if (distanceToTarget < Mathf.Max(4000f, 2f * GunEmploymentRange(weaponManager))) return false;
            if (picture.ourMissilesInFlight > 0 || picture.ourShot >= 0.8f * threshold) return false; // Cranking with missiles away, or about to shoot, is handled elsewhere.
            if (picture.theirShot < spikeShotFloor && picture.theirShotLater < spikeShotFloor) return false; // It can't hurt us from there.
            if (PlanAgainst(v) == FightPlan.Compress) return false; // Out-reached: closing is the plan.
            target = vesselTransform.position + 1000f * OffsetDirection(vectorToTarget, CrankAngle(weaponManager));
            turningTimer = 0;
            ++spikeCrankTicks;
            NoteTactic("Spike crank");
            return true;
        }
    }
}
