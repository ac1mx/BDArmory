using UnityEngine;

using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Section tactics (with Advanced Tactics): flying as one of a pair with the nearest teammate, at every range. Twice a second the pilot
    /// builds a picture of its wingman: where it is, what it's fighting, whether a bandit is on its tail (or on ours), and its missiles in
    /// the air at our target. From it:
    ///  - sort: a bandit the wingman alone is engaging counts as taken (fix in TargetInfo), so pairs split across two bandits;
    ///  - bracket: closing on a shared bandit beyond missile range, come in from the side away from the wingman so it can't face both;
    ///  - grinder: the wingman's missiles in the air at our target count towards our salvo, and while the bandit defends against them we
    ///    press for the next shot instead of spending our own;
    ///  - sandwich: a bandit on the wingman's tail becomes our target (target priority), and the wingman drags it across our nose;
    ///  - drag: defensive with a free wingman near, turn the fight towards it;
    ///  - defensive split and deconfliction: in a close fight, keep clear of the wingman's flight path, and split when one bandit is on both;
    ///  - support: in the wingman's fight with our target, stand off at missile range and keep height rather than crowd its circle;
    ///  - 1v2: the nearest other bandit pointing at us is planned against as a threat in the close-in fight.
    /// Counted for the steering metrics log; the whole layer can be switched off for comparison.
    /// </summary>
    public partial class BDModulePilotAI
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Section Tactics", // Fly as a pair with the nearest teammate (needs Advanced Tactics).
            groupName = "pilotAI_EvadeExtend", groupDisplayName = "#LOC_BDArmory_AI_EvadeExtend", groupStartCollapsed = true),
            UI_Toggle(enabledText = "#LOC_BDArmory_Enabled", disabledText = "#LOC_BDArmory_Disabled", scene = UI_Scene.All),]
        public bool sectionTactics = true;

        const float sectionInterval = 0.5f, sectionRange = 30000f, tailRange = 3000f, engagedRange = 3000f, secondThreatRange = 4000f;

        class SectionPicture
        {
            public float time = -100f;
            public Vessel wingman, wingmanTarget, wingmanAttacker, ourAttacker, secondThreat;
            public BDModulePilotAI wingmanAI;
            public float wingmanDistance, wingmanTargetDistance;
            public int wingmanMissilesAtOurTarget;
            public bool wingmanDefensive => wingmanAttacker != null;
            public bool wingmanEngaged => wingmanTarget != null && wingmanTargetDistance < engagedRange;
        }
        readonly SectionPicture section = new SectionPicture();
        int sectionSandwichTicks, sectionDragTicks, sectionSplitTicks, sectionSupportTicks, sectionGrinderTicks, sectionBracketTicks, sectionSharedHolds;
        float sectionWingmanTime;

        bool SectionActive(MissileFire weaponManager) => sectionTactics && TacticsAllowed(weaponManager);
        bool SectionFresh => Time.time - section.time < 3f * sectionInterval && section.wingman != null && section.wingman.loaded;

        /// <summary>Rebuild the section picture (twice a second).</summary>
        void UpdateSection()
        {
            var weaponManager = WeaponManager;
            if (!SectionActive(weaponManager)) { section.wingman = null; section.ourAttacker = section.secondThreat = null; return; }
            if (Time.time - section.time < sectionInterval * (0.9f + 0.2f * ((vessel.id.GetHashCode() & 7) / 7f))) return;
            if (section.wingman != null) sectionWingmanTime += Time.time - section.time > 3f * sectionInterval ? 0f : Time.time - section.time;
            section.time = Time.time;

            // Wingman: the nearest airborne teammate flown by a pilot AI.
            section.wingman = null; section.wingmanAI = null; section.wingmanTarget = null; section.wingmanAttacker = null;
            section.wingmanMissilesAtOurTarget = 0;
            float best = sectionRange;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || other == vessel || !other.loaded || other.LandedOrSplashed || other.IsMissile()) continue;
                var controller = other.ActiveController();
                if (controller == null || controller.WM == null || controller.WM.Team != weaponManager.Team || controller.AI is not BDModulePilotAI ai) continue;
                float distance = (other.CoM - vessel.CoM).magnitude;
                if (distance < best) { best = distance; section.wingman = other; section.wingmanAI = ai; }
            }
            if (section.wingman != null)
            {
                section.wingmanDistance = best;
                var wingmanWM = section.wingman.ActiveController().WM;
                section.wingmanTarget = wingmanWM.currentTarget != null ? wingmanWM.currentTarget.Vessel : null;
                section.wingmanTargetDistance = section.wingmanTarget != null ? (section.wingmanTarget.CoM - section.wingman.CoM).magnitude : float.MaxValue;
                section.wingmanAttacker = Attacker(section.wingman, weaponManager.Team);
                if (targetVessel != null)
                    foreach (var weapon in BDATargetManager.FiredMissiles)
                        if (weapon is MissileBase missile && missile.SourceVessel == section.wingman && missile.HasFired && !missile.HasExploded && !missile.HasMissed && !missile.judgedDefeated
                            && missile.targetVessel != null && missile.targetVessel.Vessel == targetVessel) ++section.wingmanMissilesAtOurTarget;
            }
            section.ourAttacker = Attacker(vessel, weaponManager.Team);
            section.secondThreat = SecondThreat(weaponManager);
        }

        /// <summary>The nearest enemy behind a craft within tailRange with its nose on it: a bandit on its tail.</summary>
        static Vessel Attacker(Vessel v, Competition.BDTeam team)
        {
            Vessel attacker = null;
            float best = tailRange;
            Vector3 heading = v.srf_vel_direction;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || other == v || !other.loaded || other.LandedOrSplashed || other.IsMissile()) continue;
                var otherWM = other.ActiveController().WM;
                if (otherWM == null || team == null || !team.IsEnemy(otherWM.Team)) continue;
                Vector3 toV = v.CoM - other.CoM;
                float distance = toV.magnitude;
                if (distance > best || distance < 1f) continue;
                if (Vector3.Dot(other.srf_vel_direction, toV) < 0.707f * distance) continue; // Its nose within 45° of the craft.
                if (Vector3.Dot(heading, -toV) > 0f) continue; // In front of the craft, not behind it.
                best = distance; attacker = other;
            }
            return attacker;
        }

        /// <summary>The nearest enemy other than our target within secondThreatRange with its nose within 60° of us (1v2 awareness).</summary>
        Vessel SecondThreat(MissileFire weaponManager)
        {
            Vessel threat = null;
            float best = secondThreatRange;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || other == vessel || other == targetVessel || !other.loaded || other.LandedOrSplashed || other.IsMissile()) continue;
                var otherWM = other.ActiveController().WM;
                if (otherWM == null || weaponManager.Team == null || !weaponManager.Team.IsEnemy(otherWM.Team)) continue;
                Vector3 toUs = vessel.CoM - other.CoM;
                float distance = toUs.magnitude;
                if (distance > best || Vector3.Dot(other.srf_vel_direction, toUs) < 0.5f * distance) continue;
                best = distance; threat = other;
            }
            return threat;
        }

        #region Target choice
        /// <summary>
        /// Extra target-priority score from the section (added to the weapon manager's weighted sum): a bandit on the wingman's tail,
        /// unless one is on ours (sandwich), and the launcher of a missile coming at the wingman.
        /// </summary>
        public float SectionTargetBonus(Vessel candidate)
        {
            if (candidate == null || !SectionActive(WeaponManager) || !SectionFresh) return 0f;
            float bonus = 0f;
            if (candidate == section.wingmanAttacker && section.ourAttacker == null) bonus += 2f;
            var wingmanWM = section.wingman.ActiveController().WM;
            if (wingmanWM != null && wingmanWM.missileIsIncoming && wingmanWM.incomingMissileVessel != null)
            {
                var incoming = VesselModuleRegistry.GetMissileBase(wingmanWM.incomingMissileVessel);
                if (incoming != null && incoming.SourceVessel == candidate && incoming.TargetingMode == MissileBase.TargetingModes.Radar && !incoming.ActiveRadar) bonus += 1f; // Kill its guidance.
            }
            return bonus;
        }
        #endregion

        #region Missiles
        /// <summary>The wingman's live missiles at the given target (our current target only; 0 otherwise).</summary>
        public int SectionMissilesAt(Vessel target)
        {
            if (target == null || target != targetVessel || !SectionActive(WeaponManager) || !SectionFresh) return 0;
            return section.wingmanMissilesAtOurTarget;
        }
        public void NoteSharedMissileHold() => ++sectionSharedHolds;

        /// <summary>The bandit is defending against the wingman's missiles: press for the next shot (grinder).</summary>
        bool SectionGrinder(Vessel target)
        {
            if (target != targetVessel || !SectionActive(WeaponManager) || !SectionFresh || section.wingmanMissilesAtOurTarget == 0) return false;
            ++sectionGrinderTicks;
            return true;
        }
        #endregion

        #region Geometry
        /// <summary>Beyond missile range on a bandit the wingman also has: come in from the far side of it (bracket).</summary>
        bool SectionBracket(MissileFire weaponManager, Vessel v, Vector3 vectorToTarget, float distanceToTarget, ref Vector3 target)
        {
            if (!SectionActive(weaponManager) || !SectionFresh || section.wingmanTarget != v || distanceToTarget < 8000f) return false;
            var missile = weaponManager.CurrentMissile;
            if (missile != null && distanceToTarget < 1.2f * MissileLaunchParams.GetDynamicLaunchParams(missile, v.Velocity(), v.CoM).maxLaunchRange) return false; // In range: nose on.
            if (weaponManager.missileIsIncoming) return false;
            float angle = Mathf.Min(30f, CrankAngle(weaponManager));
            Vector3 direction = OffsetDirection(vectorToTarget, angle);
            target = vessel.CoM + distanceToTarget * direction + Vector3.Dot(target - vessel.CoM, upDirection) * upDirection;
            ++sectionBracketTicks;
            NoteTactic("Bracket");
            return true;
        }

        // Close-in planner context for the section (set at each replan).
        Vector3 bfmWingmanPosition, bfmWingmanVelocity;
        bool bfmHaveWingman;
        float bfmDragWeight, bfmSplitWeight, bfmWingmanStartDistance;

        /// <summary>Set up the section terms of the close-in planner's score for this pass; may widen the stand-off range when supporting.</summary>
        void SectionBFMContext(Vessel opponent, ref float energyWeight)
        {
            bfmHaveWingman = false; bfmDragWeight = bfmSplitWeight = 0f;
            if (!SectionActive(WeaponManager) || !SectionFresh || section.wingmanDistance > 12000f) return;
            bfmHaveWingman = true;
            bfmWingmanPosition = section.wingman.CoM;
            bfmWingmanVelocity = section.wingman.Velocity();
            bfmWingmanStartDistance = section.wingmanDistance;
            bool weAreDefensive = section.ourAttacker != null && section.ourAttacker == opponent;
            if (weAreDefensive && section.wingmanAttacker == section.ourAttacker && section.wingmanDistance < 2500f)
            {
                bfmSplitWeight = 0.3f; ++sectionSplitTicks; NoteTactic("Defensive split"); // One bandit on both of us: split, it can follow only one.
            }
            else if (weAreDefensive && !section.wingmanDefensive)
            {
                bfmDragWeight = 0.4f; ++sectionDragTicks; NoteTactic("Drag"); // Bring it across the wingman's nose.
            }
            if (opponent == section.wingmanAttacker) { ++sectionSandwichTicks; NoteTactic("Sandwich"); }
            else if (opponent == section.wingmanTarget && section.wingmanEngaged && section.wingmanTargetDistance < (opponent.CoM - vessel.CoM).magnitude)
            {
                // The wingman's fight with our target: stand off at missile range with height rather than crowd its circle.
                bfmOptimalRange = Mathf.Max(bfmOptimalRange, 2000f);
                energyWeight += 0.2f;
                ++sectionSupportTicks; NoteTactic("Support");
            }
        }

        /// <summary>Section terms of the close-in planner's score at step k (time t) for us at p.</summary>
        float SectionStepScore(Vector3 p, float t)
        {
            if (!bfmHaveWingman) return 0f;
            Vector3 wingman = bfmWingmanPosition + bfmWingmanVelocity * t;
            float distance = (p - wingman).magnitude;
            float s = 0f;
            if (distance < 500f) s -= 1.5f * (1f - distance / 500f); // Deconfliction: its flight path, its line of fire.
            if (bfmSplitWeight > 0f) s += bfmSplitWeight * Mathf.Clamp((distance - bfmWingmanStartDistance) / 1500f, -1f, 1f);
            if (bfmDragWeight > 0f) s += bfmDragWeight * Mathf.Clamp((bfmWingmanStartDistance - distance) / 2000f, -1f, 1f);
            return s;
        }
        #endregion

        string SectionMetrics() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "section={0} wingman {1:F0}s sandwich {2:F1}s drag {3:F1}s split {4:F1}s support {5:F1}s grinder {6:F1}s bracket {7:F1}s sharedHolds {8}",
            sectionTactics, sectionWingmanTime, sectionSandwichTicks * bfmReplanInterval, sectionDragTicks * bfmReplanInterval, sectionSplitTicks * bfmReplanInterval,
            sectionSupportTicks * bfmReplanInterval, sectionGrinderTicks * Time.fixedDeltaTime, sectionBracketTicks * Time.fixedDeltaTime, sectionSharedHolds);
    }
}
