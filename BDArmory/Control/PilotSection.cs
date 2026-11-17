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
    ///  - 1v2: the nearest other bandit pointing at us is planned against as a threat in the close-in fight;
    ///  - flight orders: the wingman is the flight-mate, sorting stays within mutual support of the lead, the pair is held together
    ///    at missile range and kept at height while it has radar rounds (SectionTether), and the other side's AWACS is a target once
    ///    it can be reached (HighValueBonus).
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

        bool SectionActive(MissileFire weaponManager) => sectionTactics && Knows(SkillFeature.SectionTactics) && TacticsAllowed(weaponManager);
        bool SectionFresh => Time.time - section.time < 3f * sectionInterval && section.wingman != null && section.wingman.loaded;

        /// <summary>Rebuild the section picture (twice a second).</summary>
        void UpdateSection()
        {
            var weaponManager = WeaponManager;
            if (!SectionActive(weaponManager)) { section.wingman = null; section.ourAttacker = section.secondThreat = null; return; }
            if (Time.time - section.time < sectionInterval * (0.9f + 0.2f * ((vessel.id.GetHashCode() & 7) / 7f))) return;
            if (section.wingman != null) sectionWingmanTime += Time.time - section.time > 3f * sectionInterval ? 0f : Time.time - section.time;
            section.time = Time.time;

            // Wingman: the nearest airborne teammate flown by a pilot AI; with flight orders, only our flight-mate (not a support craft
            // that happens to be nearer).
            section.wingman = null; section.wingmanAI = null; section.wingmanTarget = null; section.wingmanAttacker = null;
            section.wingmanMissilesAtOurTarget = 0;
            float best = sectionRange;
            Vessel flightMate = orders != null ? WingmanForOrders() : null;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || other == vessel || !other.loaded || other.LandedOrSplashed || other.IsMissile()) continue;
                if (orders != null && other != flightMate) continue;
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
        /// unless one is on ours (sandwich), and the launcher of a missile coming at the wingman; with flight orders, less for the
        /// lead's own target while another bandit is near enough to take without leaving the lead (sort), and more for the other
        /// side's high-value asset (an AWACS) when we can reach it.
        /// </summary>
        public float SectionTargetBonus(Vessel candidate)
        {
            if (candidate == null) return 0f;
            float bonus = 0f;
            // Sort (flight orders): a wingman leaves its lead's target to the lead when there's another fighter to take within mutual
            // support of the lead (not a flanker or an AWACS far off: chasing that splits the section).
            if (orders != null && orders.lead != null && orders.lead.vessel != null && orders.lead.orders != null && orders.lead.orders.flight == orders.flight
                && orders.lead.targetVessel == candidate && WeaponManager != null)
                foreach (var info in BDATargetManager.TargetList(WeaponManager.Team))
                    if (info != null && info.Vessel != null && info.Vessel != candidate && !info.isMissile && !info.Vessel.LandedOrSplashed && !IsHighValueAsset(info.Vessel)
                        && (info.Vessel.CoM - orders.lead.vessel.CoM).magnitude < (candidate.CoM - orders.lead.vessel.CoM).magnitude + supportRange) { bonus -= 1.5f; break; }
            if (orders != null && IsHighValueAsset(candidate)) bonus += HighValueBonus(candidate);
            if (!SectionActive(WeaponManager) || !SectionFresh) return bonus;
            if (candidate == section.wingmanAttacker && section.ourAttacker == null) bonus += 2f;
            var wingmanWM = section.wingman.ActiveController().WM;
            if (wingmanWM != null && wingmanWM.missileIsIncoming && wingmanWM.incomingMissileVessel != null)
            {
                var incoming = VesselModuleRegistry.GetMissileBase(wingmanWM.incomingMissileVessel);
                if (incoming != null && incoming.SourceVessel == candidate && incoming.TargetingMode == MissileBase.TargetingModes.Radar && !incoming.ActiveRadar) bonus += 1f; // Kill its guidance.
            }
            return bonus;
        }

        /// <summary>
        /// Priority for the other side's AWACS (flight orders): always a hunter's first target (most once in reach of its longest
        /// radar missile); for anyone else, once a radar missile of ours reaches it and no hostile fighter is close enough to be the
        /// first threat.
        /// </summary>
        float HighValueBonus(Vessel asset)
        {
            var weaponManager = WeaponManager;
            if (weaponManager == null) return 0f;
            float distance = (asset.CoM - vessel.CoM).magnitude, reach = 0f;
            foreach (var missile in VesselModuleRegistry.GetModules<MissileBase>(vessel))
            {
                if (missile == null || missile.HasFired || !missile.engageAir || missile.TargetingMode != MissileBase.TargetingModes.Radar) continue;
                reach = Mathf.Max(reach, MissileLaunchParams.GetDynamicLaunchParams(missile, asset.Velocity(), asset.CoM).maxLaunchRange);
            }
            if (orders.huntHighValue) return distance < 1.2f * reach ? 4f : 1.5f;
            if (reach <= 0f || distance > reach) return 0f;
            foreach (var info in BDATargetManager.TargetList(weaponManager.Team))
                if (info != null && info.Vessel != null && info.Vessel != asset && !info.isMissile && !info.Vessel.LandedOrSplashed && !IsHighValueAsset(info.Vessel)
                    && (info.Vessel.CoM - vessel.CoM).magnitude < 0.8f * reach) return 0.5f; // A fighter in range comes first.
            return 2f;
        }
        #endregion

        #region Mutual support
        const float supportRange = 18000f, supportBreak = 30000f, supportFloorDrop = 3000f;
        int sectionTetherTicks;

        /// <summary>
        /// Mutual support (flight orders): while the fight is still at missile range, beyond supportRange of our flight-mate the fly-to
        /// point turns towards it, all the way by supportBreak, so a pair sorting two bandits doesn't split across the sky; and at any
        /// range, while we have radar rounds, it is kept no more than supportFloorDrop below the flight's altitude (a bandit low or
        /// diving away isn't followed down to the deck, where the height and speed our missiles need are lost and its close-in game
        /// wins). Not while defending, or with the nose needed for a shot about to be taken.
        /// </summary>
        bool SectionTether(float distanceToTarget, ref Vector3 target)
        {
            if (orders == null || IsEvading || (ShotEstimate >= 0f && ShotEstimate >= 0.8f * ShotThreshold)) return false;
            bool changed = false;
            Vector3 fly = target - vessel.CoM;
            float floor = OrdersAltitude() - supportFloorDrop;
            float flyAltitude = (float)FlightGlobals.getAltitudeAtPos(target);
            CountMissiles(vessel, targetVessel, out int longRange, out _);
            if (flyAltitude < floor && longRange > 0) { target += (floor - flyAltitude) * upDirection; fly = target - vessel.CoM; changed = true; } // At any range while there are radar rounds to shoot down with.
            Vessel mate = distanceToTarget > 10000f ? WingmanForOrders() : null;
            if (mate != null && !mate.LandedOrSplashed && !(mate.ActiveController().AI is BDModulePilotAI mateAI && mateAI.OrdersRecovering))
            {
                Vector3 toMate = (mate.CoM - vessel.CoM).ProjectOnPlanePreNormalized(upDirection);
                float separation = toMate.magnitude;
                Vector3 horizontal = fly.ProjectOnPlanePreNormalized(upDirection);
                if (separation > supportRange && horizontal.sqrMagnitude > 1f)
                {
                    float angle = Mathf.Clamp01((separation - supportRange) / (supportBreak - supportRange)) * Vector3.Angle(horizontal, toMate);
                    Vector3 turned = Vector3.RotateTowards(horizontal.normalized, toMate.normalized, angle * Mathf.Deg2Rad, 0f) * horizontal.magnitude;
                    target = vessel.CoM + turned + Vector3.Dot(fly, upDirection) * upDirection;
                    changed = true;
                }
            }
            if (!changed) return false;
            ++sectionTetherTicks;
            NoteTactic("Mutual support");
            return true;
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
            // The bracket sets up missile shots from two sides. With no missile to shoot (guns only), or with the wingman already in a
            // close fight with it, the offset only keeps us out of the fight: go straight in.
            if (NewDogfight && (missile == null || section.wingmanEngaged)) return false;
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

        bool bfmSandwich;
        int sectionDragRefusals;
        const float dragWingmanReach = 1500f;

        /// <summary>
        /// Whether dragging the bandit on our tail towards the free wingman works: the wingman must get within reach of it (gun or short
        /// missile range) before it closes into its own gun range on us. Against a faster bandit already near its gun range the drag is a
        /// straight-line run in front of its guns, and it kills the bait before the hunter arrives.
        /// </summary>
        bool DragBuysTime(Vessel attacker)
        {
            if (!NewDogfight) return true;
            Vector3 toUs = vessel.CoM - attacker.CoM;
            float range = toUs.magnitude, reach = 1.5f * bfmThreatGunRange;
            if (range < reach) return false; // Already in (or at the edge of) its gun envelope.
            float gaining = Vector3.Dot(attacker.Velocity() - vessel.Velocity(), toUs / range);
            if (gaining <= 0f) return true; // It isn't gaining on us.
            float timeToItsShot = (range - reach) / gaining;
            Vector3 wingmanToIt = attacker.CoM - section.wingman.CoM;
            float wingmanRange = wingmanToIt.magnitude;
            float wingmanClosing = Mathf.Max(Vector3.Dot(section.wingman.Velocity() - attacker.Velocity(), wingmanToIt / Mathf.Max(wingmanRange, 1f)), 50f);
            float timeToWingmanShot = Mathf.Max(0f, wingmanRange - dragWingmanReach) / wingmanClosing;
            return timeToWingmanShot < 0.8f * timeToItsShot;
        }

        /// <summary>Set up the section terms of the close-in planner's score for this pass; may widen the stand-off range when supporting.</summary>
        void SectionBFMContext(Vessel opponent, ref float energyWeight)
        {
            bfmHaveWingman = false; bfmSandwich = false; bfmDragWeight = bfmSplitWeight = 0f;
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
                if (DragBuysTime(opponent)) { bfmDragWeight = 0.4f; ++sectionDragTicks; NoteTactic("Drag"); } // Bring it across the wingman's nose.
                else ++sectionDragRefusals; // It would run us down first: fight it (the defensive manoeuvres) until the wingman arrives.
            }
            bfmSandwich = opponent == section.wingmanAttacker;
            if (bfmSandwich) { ++sectionSandwichTicks; NoteTactic("Sandwich"); energyWeight *= 0.5f; } // The wingman is buying the time: spend energy to get there.
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
            "section={0} wingman {1:F0}s sandwich {2:F1}s drag {3:F1}s dragRefused {10:F1}s split {4:F1}s support {5:F1}s grinder {6:F1}s bracket {7:F1}s sharedHolds {8} tether {9:F1}s",
            sectionTactics, sectionWingmanTime, sectionSandwichTicks * bfmReplanInterval, sectionDragTicks * bfmReplanInterval, sectionSplitTicks * bfmReplanInterval,
            sectionSupportTicks * bfmReplanInterval, sectionGrinderTicks * Time.fixedDeltaTime, sectionBracketTicks * Time.fixedDeltaTime, sectionSharedHolds, sectionTetherTicks * Time.fixedDeltaTime,
            sectionDragRefusals * bfmReplanInterval);
    }
}
