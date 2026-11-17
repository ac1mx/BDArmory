using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Matchup: before choosing how to fight a bandit, size up both aircraft and pick the range that favours us.
    ///
    /// Beyond visual range, each side's reach is the shorter of how far its radar sees the other's head-on radar cross-section (or its
    /// eyes, without a radar) and how far its best air-to-air missile flies: a jet that can't see a stealthy bandit can't shoot it, and
    /// one with only short-range missiles can't use a good radar. Close in, it's turn performance (measured once both have been seen
    /// turning, else each pilot's learned lift per dynamic pressure), thrust-to-weight, guns and heat missiles.
    ///
    /// The fight plan follows from where we're better: Standoff (out-reached bandit, or one that would win the turning fight) keeps the
    /// fight outside the bandit's reach and refuses the merge; Compress (out-reached by a bandit we'd beat close in: the non-stealthy jet
    /// against a stealthy one, the dogfighter against the sniper) drives straight to the merge at full speed and doesn't abort at range,
    /// which would only give the bandit's long shots more time.
    /// </summary>
    public partial class BDModulePilotAI
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Matchup Tactics", // Fight at the range where we're stronger than the bandit (needs Advanced Tactics and the Engagement Planner).
            groupName = "pilotAI_EvadeExtend", groupDisplayName = "#LOC_BDArmory_AI_EvadeExtend", groupStartCollapsed = true),
            UI_Toggle(enabledText = "#LOC_BDArmory_Enabled", disabledText = "#LOC_BDArmory_Disabled", scene = UI_Scene.All),]
        public bool matchupTactics = true;

        enum FightPlan { Neutral, Standoff, Compress }

        class Matchup
        {
            public Vessel opponent;
            public float time = -100f;
            public float ourSight, theirSight, ourMissileReach, theirMissileReach, ourReach, theirReach; // m
            public float turnRatio = 1f, thrustRatio = 1f; // Ours over theirs.
            public float bvrEdge, wvrEdge; // Log advantage: positive for us.
            public FightPlan plan;
        }
        readonly Matchup matchup = new Matchup();
        const float matchupInterval = 5f, matchupMargin = 0.35f, matchupPreference = 0.2f, compressMergeRange = 3000f;
        const float visualSight = 8000f; // Eyes (and IRST-less sensors) on a fighter-sized target.
        int matchupStandoffTicks, matchupCompressTicks, matchupHoldRanges, matchupPlanChanges;
        float holdRangeSince = -100f;

        bool MatchupActive(MissileFire weaponManager) => matchupTactics && Knows(SkillFeature.Matchup) && PlanActive(weaponManager);
        FightPlan PlanAgainst(Vessel v) => MatchupActive(WeaponManager) && matchup.opponent == v && Time.time - matchup.time < 3f * matchupInterval ? matchup.plan : FightPlan.Neutral;

        /// <summary>Size up the target (every few seconds, from the engagement picture's first stage).</summary>
        void UpdateMatchup(MissileFire weaponManager, Vessel target, EngagementPicture p)
        {
            if (!matchupTactics || target == null) return;
            if (matchup.opponent == target && Time.time - matchup.time < matchupInterval) return;
            if (matchup.opponent != target) matchup.plan = FightPlan.Neutral;
            matchup.opponent = target;
            matchup.time = Time.time;

            var theirWM = target.ActiveController().WM;
            matchup.ourSight = Mathf.Max(RadarSight(vessel, HeadOnRCS(target)), visualSight);
            matchup.theirSight = Mathf.Max(RadarSight(target, HeadOnRCS(vessel)), visualSight);
            matchup.ourMissileReach = MissileReach(vessel, target);
            matchup.theirMissileReach = theirWM != null ? MissileReach(target, vessel) : 0f;
            matchup.ourReach = Mathf.Min(matchup.ourSight, matchup.ourMissileReach);
            matchup.theirReach = Mathf.Min(matchup.theirSight, matchup.theirMissileReach);
            const float reachFloor = 1500f; // Guns only: the merge.
            matchup.bvrEdge = Mathf.Clamp(Mathf.Log(Mathf.Max(matchup.ourReach, reachFloor) / Mathf.Max(matchup.theirReach, reachFloor)), -2f, 2f);
            if (p.ourLong == 0 && p.theirLong > 0) matchup.bvrEdge -= 0.5f;
            else if (p.theirLong == 0 && p.ourLong > 0) matchup.bvrEdge += 0.5f;

            // Close in: turning, then thrust, then the weapons for it.
            var theirPilot = VesselModuleRegistry.GetBDModulePilotAI(target, true);
            // Both sides from the same measure, each pilot's learned lift: the turning seen in our own recent manoeuvres against what a
            // bandit flying straight at range has shown would make every opponent look like a worse turner.
            matchup.turnRatio = theirPilot != null ? TurnAt(LiftCapability, maxAllowedGForce) / Mathf.Max(TurnAt(theirPilot.LiftCapability, theirPilot.maxAllowedGForce), 1e-3f) : 1f;
            float ourThrust = ThrustToWeight(vessel), theirThrust = ThrustToWeight(target);
            // Rated thrust reads implausibly low on some engine setups (multi-mode jets read 0.1-0.2): only fighter-like figures count,
            // and no more than 1.5 times either way.
            matchup.thrustRatio = ourThrust >= 0.4f && theirThrust >= 0.4f ? Mathf.Clamp(ourThrust / theirThrust, 1f / 1.5f, 1.5f) : 1f;
            bool ourGuns = HasAirGuns(vessel), theirGuns = HasAirGuns(target);
            matchup.wvrEdge = 2f * Beyond(Mathf.Log(Mathf.Clamp(matchup.turnRatio, 0.3f, 3f)), 0.1f) + Beyond(Mathf.Log(Mathf.Clamp(matchup.thrustRatio, 0.3f, 3f)), 0.1f)
                + (ourGuns == theirGuns ? 0f : ourGuns ? 0.3f : -0.3f) + ((p.ourShort > 0) == (p.theirShort > 0) ? 0f : p.ourShort > 0 ? 0.3f : -0.3f);

            var previous = matchup.plan;
            matchup.plan = orders != null && orders.huntHighValue && p.ourLong > 0 ? FightPlan.Standoff // A missile truck fights from range: closing in only makes it the target.
                : ChoosePlan(matchup.bvrEdge, matchup.wvrEdge, previous);
            if (matchup.plan != previous)
            {
                ++matchupPlanChanges;
                if (steeringMetricsEnabled) Debug.Log($"[BDArmory.BDModulePilotAI]: Matchup {vessel.vesselName} vs {target.vesselName}: {matchup.plan} (bvr {matchup.bvrEdge:+0.00;-0.00} wvr {matchup.wvrEdge:+0.00;-0.00}: sight {matchup.ourSight / 1000:F0}/{matchup.theirSight / 1000:F0}km, missiles {matchup.ourMissileReach / 1000:F0}/{matchup.theirMissileReach / 1000:F0}km, turn {matchup.turnRatio:F2}, thrust {matchup.thrustRatio:F2}, guns {ourGuns}/{theirGuns}, missiles long {p.ourLong}/{p.theirLong} short {p.ourShort}/{p.theirShort})");
            }
        }

        /// <summary>
        /// Standoff where the bandit is out-reached, or would win close in and we're not out-reached; Compress where we're out-reached by
        /// one we'd beat close in, or where we'd win close in and aren't out-reached; neutral otherwise. The plan in force keeps going
        /// until its case weakens past a smaller margin, so it doesn't flicker.
        /// </summary>
        static FightPlan ChoosePlan(float bvr, float wvr, FightPlan current)
        {
            float Margin(FightPlan plan) => plan == current ? 0.6f * matchupMargin : matchupMargin;
            bool standoff = (bvr > Margin(FightPlan.Standoff) && bvr > wvr + matchupPreference) || (wvr < -Margin(FightPlan.Standoff) && bvr > wvr + matchupPreference && bvr > -0.2f);
            bool compress = (bvr < -Margin(FightPlan.Compress) && wvr > bvr + matchupPreference) || (wvr > Margin(FightPlan.Compress) && wvr > bvr + matchupPreference && bvr < 0.2f);
            if (standoff && compress) return current == FightPlan.Compress ? FightPlan.Compress : FightPlan.Standoff;
            return standoff ? FightPlan.Standoff : compress ? FightPlan.Compress : FightPlan.Neutral;
        }

        /// <summary>A log ratio with differences under the dead band treated as even (learned estimates scatter by about that much).</summary>
        static float Beyond(float logRatio, float deadBand) => Mathf.Sign(logRatio) * Mathf.Max(Mathf.Abs(logRatio) - deadBand, 0f);

        /// <summary>Turning acceleration (m/s²) at a merge's dynamic pressure from lift per kPa, within the pilot's g limit.</summary>
        static float TurnAt(float liftPerKPa, float maxG)
        {
            const float mergeDynamicPressure = 40f; // kPa: about 260 m/s low down, the speed a turning fight settles at.
            return Mathf.Min(liftPerKPa * mergeDynamicPressure, maxG * 9.81f);
        }

        /// <summary>
        /// Standoff, between the bandit's reach and ours: a shot that reaches is taken below the normal threshold, since the bandit can't
        /// answer it. One missile at a time (shoot, look, shoot), the last of that kind kept.
        /// </summary>
        bool FreeShotWanted(MissileFire weaponManager, Vessel target, MissileBase missile, in ShotModel.Features features)
        {
            if (PlanAgainst(target) != FightPlan.Standoff || !PictureOf(target)) return false;
            if (ShotEstimate >= ShotThreshold || ShotEstimate < forcingFloor || !features.valid || !features.straight.reached) return false;
            if ((target.CoM - vessel.CoM).magnitude < 1.05f * matchup.theirReach) return false; // Inside its reach: the normal threshold.
            if (weaponManager.GetMissilesAway(weaponManager.currentTarget).numAway > 0) return false;
            int spare = 0;
            foreach (var other in VesselModuleRegistry.GetModules<MissileBase>(vessel))
                if (other != null && !other.HasFired && other.TargetingMode == missile.TargetingMode) ++spare;
            return spare >= 2;
        }
        float freeShotPendingUntil = -1f;
        int matchupFreeShots;

        /// <summary>Radar cross-section the other side's radar sees with this craft's nose on it (m²), or 0 if unknown.</summary>
        static float HeadOnRCS(Vessel v)
        {
            var ti = v.gameObject.GetComponent<TargetInfo>();
            if (ti == null || !(ti.radarBaseSignature > 0f)) return 0f;
            if (ti.radarSignatureMatrix != null) { float headOn = RadarUtils.RCSMatrixEval(ti.radarSignatureMatrix, ti.radarBaseSignature, 0f, 0f); if (headOn > 0f) return headOn; }
            return ti.radarBaseSignature;
        }

        /// <summary>Farthest any of the observer's search radars detects this cross-section (m); 0 without one. An unknown RCS counts as seen at full range.</summary>
        static float RadarSight(Vessel observer, float rcs)
        {
            float best = 0f;
            foreach (var radar in VesselModuleRegistry.GetModules<ModuleRadar>(observer))
            {
                if (radar == null || !radar.canScan || radar.sonarMode != ModuleRadar.SonarModes.None) continue;
                float max = radar.radarMaxDistanceDetect, min = radar.radarMinDistanceDetect; // km
                if (rcs <= 0f) { best = Mathf.Max(best, max * 1000f); continue; }
                for (float d = max; d > min; d -= Mathf.Max(0.5f, 0.01f * max))
                    if (radar.radarDetectionCurve.Evaluate(d) <= rcs) { best = Mathf.Max(best, d * 1000f); break; }
            }
            return best;
        }

        /// <summary>
        /// Longest launch range among the shooter's unfired air-to-air missiles against the target where both are now (m): the dynamic
        /// launch envelope, so height and speed count (an R-37 from 12 km reaches twice its rated static range).
        /// </summary>
        static float MissileReach(Vessel shooter, Vessel target)
        {
            float best = 0f;
            foreach (var missile in VesselModuleRegistry.GetModules<MissileBase>(shooter))
                if (missile != null && !missile.HasFired && missile.engageAir)
                    best = Mathf.Max(best, MissileLaunchParams.GetDynamicLaunchParams(missile, target.Velocity(), target.CoM).maxLaunchRange);
            return best;
        }

        /// <summary>Running engines' rated thrust over weight at 1 g, in the mode each is in.</summary>
        static float ThrustToWeight(Vessel v)
        {
            float thrust = 0f;
            foreach (var engine in VesselModuleRegistry.GetModules<ModuleEngines>(v))
            {
                if (engine == null || !engine.EngineIgnited) continue;
                var multiMode = engine.part.FindModuleImplementing<MultiModeEngine>();
                if (multiMode != null && multiMode.mode != engine.engineID) continue;
                thrust += Mathf.Max(0f, engine.maxThrust * engine.thrustPercentage / 100f);
            }
            float weight = (float)v.totalMass * 9.81f;
            return weight > 0f ? thrust / weight : 0f;
        }

        static bool HasAirGuns(Vessel v)
        {
            foreach (var weapon in VesselModuleRegistry.GetModules<ModuleWeapon>(v))
                if (weapon != null && weapon.engageAir && weapon.GetWeaponClass() == WeaponClasses.Gun) return true;
            return false;
        }

        /// <summary>
        /// Standoff: while the bandit closes inside its own reach (plus a margin) and our missiles don't need us pointing at it, turn cold
        /// and run until we're back outside; never closer than our own reach lets us shoot from. Returns true while holding range.
        /// </summary>
        bool HoldRange(Vessel v, float distance, bool settingUpLaunch, float threshold, ref Vector3 target)
        {
            if (PlanAgainst(v) != FightPlan.Standoff || !canExtend) { holdRangeSince = -100f; return false; }
            float holdRange = Mathf.Clamp(1.15f * matchup.theirReach, 5000f, Mathf.Max(5000f, 0.85f * matchup.ourReach));
            bool holding = Time.time - holdRangeSince < 30f;
            bool closing = Vector3.Dot(vessel.Velocity() - v.Velocity(), v.CoM - vessel.CoM) > 0f;
            bool needed = distance < (holding ? 1.25f : 1f) * holdRange && !TurnedAway(v, 60f) && (holding || closing)
                && !(picture.ourMissileNeedsSupport && picture.ourMissileTimeToGo >= 0f) && !(settingUpLaunch && picture.ourShot >= threshold)
                && distance > compressMergeRange; // Already merged: the close-in fight decides.
            if (!needed) { holdRangeSince = -100f; return false; }
            if (!holding) { holdRangeSince = Time.time; ++matchupHoldRanges; }
            target = ColdPoint(v.CoM);
            AdjustThrottle(maxSpeed, false, true);
            turningTimer = 0;
            NoteTactic("Hold range");
            return true;
        }

        /// <summary>Compress: out-reached by a bandit we'd beat close in, so close straight to the merge on a collision course at full speed.</summary>
        bool CompressTo(Vessel v, float distance, bool settingUpLaunch, float threshold, ref Vector3 target)
        {
            if (PlanAgainst(v) != FightPlan.Compress || distance < compressMergeRange) return false;
            if (settingUpLaunch && picture.ourShot >= 0.8f * threshold) return false; // A shot is coming: nose on it.
            target = CutoffPoint(v, target);
            AdjustThrottle(maxSpeed, false, true);
            turningTimer = 0;
            NoteTactic("Compress");
            return true;
        }

        void CountMatchupTicks(Vessel v)
        {
            var plan = PlanAgainst(v);
            if (plan == FightPlan.Standoff) ++matchupStandoffTicks;
            else if (plan == FightPlan.Compress) ++matchupCompressTicks;
        }

        string MatchupMetrics() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "matchup={0} plan={1} bvr={2:+0.00;-0.00} wvr={3:+0.00;-0.00} standoff {4:F1}s compress {5:F1}s holdRange {6} freeShots {7} planChanges {8}",
            matchupTactics, matchup.plan, matchup.bvrEdge, matchup.wvrEdge, matchupStandoffTicks * Time.fixedDeltaTime, matchupCompressTicks * Time.fixedDeltaTime, matchupHoldRanges, matchupFreeShots, matchupPlanChanges);
    }
}
