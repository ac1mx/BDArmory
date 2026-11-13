using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Guidances;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Air combat tactics of the pilot AI that sit on top of the basic engage/evade/extend behaviour.
    /// Everything here is switched by advancedTactics and stands down whenever the terrain, waypoint, ramming or auto-tuning logic is in charge.
    /// Stock settings are honoured rather than duplicated: canExtend (pump), maxAltitude (climb), each radar's Dynamic Radar option (radar silence),
    /// the weapon manager's salvo decisions (or its fixed maxMissilesOnTarget) and the afterburner priority.
    /// </summary>
    public partial class BDModulePilotAI
    {
        bool TacticsAllowed(MissileFire weaponManager) =>
            advancedTactics && pilotSkill > 25f && weaponManager != null && weaponManager.guardMode && !AutoTune && !belowMinAltitude && !ramming && !IsRunningWaypoints
            && !vessel.LandedOrSplashed && !vessel.InNearVacuum();

        #region Energy
        /// <summary>Our specific energy (height plus speed converted to height) minus the other craft's, in metres.</summary>
        float EnergyAdvantage(Vessel v) =>
            (float)(vessel.altitude - v.altitude + (vessel.srfSpeed * vessel.srfSpeed - v.srfSpeed * v.srfSpeed) / (2.0 * bodyGravity));

        float lastEnergyExtendTime = -100f;
        /// <summary>
        /// A turning fight against an opponent with much more energy is a losing one: it can always go up and come back down on us.
        /// Outside gun range and not under fire, leave to rebuild energy instead (needs canExtend; at most once every 30s).
        /// </summary>
        bool ShouldExtendForEnergy(Vessel v, float distance)
        {
            var weaponManager = WeaponManager;
            if (!TacticsAllowed(weaponManager) || !canExtend || v == null || v.LandedOrSplashed || Time.time - lastEnergyExtendTime < 30f) return false;
            if (weaponManager.underFire || weaponManager.missileIsIncoming) return false;
            if (distance < weaponManager.gunRange || distance > 3000f) return false;
            if (EnergyAdvantage(v) > -1500f) return false;
            lastEnergyExtendTime = Time.time;
            ++energyExtendCount;
            return true;
        }
        #endregion

        #region Engagement geometry
        float engagementSide = 0; // Side of the target we're offsetting to (±1), 0 when flying straight at it.
        float mergeTurnTimer = -1f, lastMergeTime = -100f, mergeTurnSide;
        float offAspectLimit = 60f; // Largest angle off the nose (°) we can show a threat radar without a large rise in radar cross-section.

        /// <summary>
        /// Pick the geometry for flying at the target when we aren't pointing a weapon at it right now.
        /// Returns the name of the tactic in use (and sets the fly-to position), or null to fly at the target as usual.
        /// </summary>
        string SelectEngagementTactic(Vessel v, Vector3 vectorToTarget, float distanceToTarget, ref Vector3 target)
        {
            var weaponManager = WeaponManager;
            if (!TacticsAllowed(weaponManager) || v.LandedOrSplashed || weaponManager.currentTarget == null || weaponManager.currentTarget.Vessel != v)
            {
                engagementSide = 0;
                mergeTurnTimer = -1f;
                return null;
            }
            if (OneCircleTurn(v, vectorToTarget, distanceToTarget, ref target)) return "One Circle";
            bool settingUpLaunch = weaponManager.CurrentMissile != null && weaponManager.firedMissiles < weaponManager.MissilesOnTargetLimit; // The nose is needed on the target.
            string planned = PlannedGeometry(weaponManager, v, vectorToTarget, distanceToTarget, settingUpLaunch, ref target);
            if (planned != null) return planned;
            if (!settingUpLaunch)
            {
                string support = CrankOrPump(weaponManager, v, vectorToTarget, distanceToTarget, ref target);
                if (support != null) return support;
                if (OffsetApproach(weaponManager, v, vectorToTarget, distanceToTarget, ref target)) return "Offset";
            }
            engagementSide = 0;
            return null;
        }

        Vector3 OffsetDirection(Vector3 vectorToTarget, float angle)
        {
            Vector3 toTarget = vectorToTarget.ProjectOnPlanePreNormalized(upDirection).normalized;
            if (engagementSide == 0)
            {
                // Bracket: with a teammate on the same target, take the side of it the teammate isn't on, so that it can't face both of us.
                // Otherwise go to the side we're already heading towards.
                Vessel wingman = TeammateOnTarget();
                if (wingman != null)
                {
                    engagementSide = Vector3.Dot(Vector3.Cross(toTarget, (wingman.CoM - vessel.CoM).ProjectOnPlanePreNormalized(upDirection)), upDirection) >= 0 ? -1 : 1;
                    ++bracketCount;
                    NoteTactic("Bracket");
                }
                else
                {
                    engagementSide = SideAwayFromOtherThreats(toTarget); // Away from another bandit pointing at us, if there is one.
                    if (engagementSide == 0) engagementSide = Vector3.Dot(Vector3.Cross(toTarget, vessel.Velocity().ProjectOnPlanePreNormalized(upDirection)), upDirection) >= 0 ? 1 : -1;
                }
            }
            return Quaternion.AngleAxis(engagementSide * angle, upDirection) * toTarget;
        }

        /// <summary>
        /// With missiles in the air and nothing further to launch: crank (fly with the target at the edge of the radar's view) while they
        /// need our radar or while a missile is coming at us, or pump (turn away) once they guide themselves and the target is coming at us.
        /// Both take range away from the opponent's missiles; the pump gives up the radar picture, so it needs canExtend.
        /// </summary>
        string CrankOrPump(MissileFire weaponManager, Vessel v, Vector3 vectorToTarget, float distanceToTarget, ref Vector3 target)
        {
            if (distanceToTarget < Mathf.Max(2f * weaponManager.gunRange, 4000f)) return null; // Too close for this to matter; fight normally.
            var (missilesAway, needingRadar) = weaponManager.GetMissilesAway(weaponManager.currentTarget);
            if (missilesAway <= 0 && !weaponManager.missileIsIncoming) return null;
            bool targetHot = Vector3.Dot(v.srf_vel_direction, -vectorToTarget) > 0.5f * distanceToTarget;
            if (needingRadar <= 0 && missilesAway > 0 && canExtend && targetHot)
            {
                target = vesselTransform.position + 1000f * OffsetDirection(vectorToTarget, 150f);
                { ++pumpTicks; NoteTactic("Pump"); }
                if (BDArmorySettings.DEBUG_TELEMETRY || BDArmorySettings.DEBUG_AI) debugString.AppendLine($"Pumping away from {v.vesselName} with {missilesAway} missiles on the way");
                return "Pumping";
            }
            float crankAngle = CrankAngle(weaponManager);
            target = vesselTransform.position + 1000f * OffsetDirection(vectorToTarget, crankAngle);
            { ++crankTicks; NoteTactic("Crank"); }
            if (BDArmorySettings.DEBUG_TELEMETRY || BDArmorySettings.DEBUG_AI) debugString.AppendLine($"Cranking {crankAngle:0}° {(engagementSide > 0 ? "right" : "left")} of {v.vesselName}");
            return "Cranking";
        }

        /// <summary>How far off the nose the target can be kept: the edge of the radar's view, within the radar cross-section's limit.</summary>
        float CrankAngle(MissileFire weaponManager)
        {
            float fov = weaponManager != null && weaponManager.vesselRadarData != null ? weaponManager.vesselRadarData.GetCrankFOV() : 120f;
            return Mathf.Min(fov >= 360f ? 60f : Mathf.Clamp(fov / 2f - 5f, 5f, 60f), offAspectLimit);
        }

        /// <summary>
        /// Closing head-on outside gun range: approach to one side so that we arrive with room to turn, instead of passing canopy to canopy
        /// with no turning room and a collision to avoid.
        /// </summary>
        bool OffsetApproach(MissileFire weaponManager, Vessel v, Vector3 vectorToTarget, float distanceToTarget, ref Vector3 target)
        {
            if (distanceToTarget > 8000f || distanceToTarget < Mathf.Max(1.5f * weaponManager.gunRange, 4f * turnRadius)) return false;
            if (Vector3.Dot(v.srf_vel_direction, -vectorToTarget) < 0.7f * distanceToTarget) return false; // It isn't coming at us.
            if (Vector3.Dot(vessel.srf_vel_direction, vectorToTarget) < 0.7f * distanceToTarget) return false; // We aren't going at it.
            float angle = Mathf.Min(Mathf.Asin(Mathf.Clamp01(2f * turnRadius / distanceToTarget)) * Mathf.Rad2Deg, 30f, offAspectLimit);
            if (angle < 2f) return false;
            Vector3 direction = OffsetDirection(vectorToTarget, angle);
            target = vesselTransform.position + distanceToTarget * direction + Vector3.Dot(vectorToTarget, upDirection) * upDirection;
            { ++offsetTicks; NoteTactic("Offset approach"); }
            if (BDArmorySettings.DEBUG_TELEMETRY || BDArmorySettings.DEBUG_AI) debugString.AppendLine($"Offset approach {angle:0}° {(engagementSide > 0 ? "right" : "left")} of {v.vesselName}");
            return true;
        }

        /// <summary>
        /// At a head-on pass, turning towards the opponent gives a two-circle fight, which the higher turn rate wins; that is what flying
        /// at the target does anyway. When we're the slower aircraft our circle is the smaller one, so turn the other way instead for a
        /// one-circle (nose-to-nose) fight, which the smaller circle wins.
        /// </summary>
        bool OneCircleTurn(Vessel v, Vector3 vectorToTarget, float distanceToTarget, ref Vector3 target)
        {
            Vector3 velDir = vessel.srf_vel_direction;
            if (mergeTurnTimer < 0)
            {
                if (distanceToTarget > 800f || Time.time - lastMergeTime < 10f) return false;
                if (Vector3.Dot(vectorToTarget, vesselTransform.up) > 0 || Vector3.Dot(velDir, v.srf_vel_direction) > -0.5f) return false; // Not yet passed, or not a head-on pass.
                lastMergeTime = Time.time;
                var weaponManager = WeaponManager;
                if (weaponManager != null && (weaponManager.salvoDecisions || preemptiveFlareSalvos < maxPreemptiveFlareSalvos) && weaponManager.FirePreemptiveFlares()) // The pass is where a heat-seeker gets its tail shot.
                    ++preemptiveFlareSalvos;
                if (vessel.srfSpeed > 0.85f * v.srfSpeed) return false; // No radius advantage: take the two-circle fight.
                mergeTurnSide = Vector3.Dot(vectorToTarget, vesselTransform.right) >= 0 ? -1f : 1f; // Away from the side it passed on.
                mergeTurnTimer = 0;
            }
            mergeTurnTimer += Time.fixedDeltaTime;
            if (mergeTurnTimer > 6f || distanceToTarget > 2500f || Vector3.Dot(vectorToTarget, vesselTransform.up) > 0.5f * distanceToTarget)
            {
                mergeTurnTimer = -1f;
                return false;
            }
            Vector3 level = velDir.ProjectOnPlanePreNormalized(upDirection).normalized;
            Vector3 right = Vector3.Cross(upDirection, level);
            target = vesselTransform.position + 500f * Vector3.RotateTowards(level, mergeTurnSide * right, 80f * Mathf.Deg2Rad, 0f);
            { ++oneCircleTicks; NoteTactic("One-circle turn"); }
            if (BDArmorySettings.DEBUG_TELEMETRY || BDArmorySettings.DEBUG_AI) debugString.AppendLine($"One-circle turn {(mergeTurnSide > 0 ? "right" : "left")}, {mergeTurnTimer:0.0}s");
            return true;
        }
        #endregion

        #region Missile employment
        /// <summary>
        /// Outside launch range with a missile selected: use the time to gain height over the target, which adds to the missile's range
        /// (speed is already at maximum here). Stays within maxAltitude.
        /// </summary>
        bool EnergyBeforeShot(Vessel v, MissileBase missile, float distanceToTarget, ref Vector3 target)
        {
            if (!TacticsAllowed(WeaponManager) || v.LandedOrSplashed) return false;
            if (distanceToTarget < MissileLaunchParams.GetDynamicLaunchParams(missile, v.Velocity(), v.CoM).maxLaunchRange) return false; // In range: point and shoot.
            float climb = (float)(v.altitude - vessel.altitude) + Mathf.Min(2000f, 0.1f * distanceToTarget);
            if (maxAltitudeEnabled) climb = Mathf.Min(climb, maxAltitude - (float)vessel.radarAltitude);
            if (climb <= 0) return false;
            target += Mathf.Min(climb, 0.2f * distanceToTarget) * upDirection;
            { ++energyClimbTicks; NoteTactic("Climb before shot"); }
            return true;
        }

        /// <summary>
        /// Fraction of the maximum launch range at which the weapon manager may launch. A target that isn't closing can defeat a
        /// maximum-range shot by simply continuing, so those shots are held until it is well inside the envelope.
        /// </summary>
        public float MissileLaunchRangeFactor(Vessel target)
        {
            if (target != null && !MissileHasTerrainClearance(target)) { ++missileShotsHeldForTerrain; return 0f; }
            if (target != null && Committed)
            {
                if (ShotWorthTaking(target)) return 1f;
                ++missileShotsHeldForOdds;
                return 0f;
            }
            if (!advancedTactics || target == null || target.LandedOrSplashed) return 1f;
            Vector3 toUs = vessel.CoM - target.CoM;
            return Vector3.Dot(target.srf_vel_direction, toUs) > 0.3f * toUs.magnitude ? 1f : 0.75f;
        }
        #endregion

        #region Fight geometry
        public enum FightSituation { None, Offensive, Defensive, Neutral }
        public FightSituation Situation { get; private set; }
        int offensiveTicks, defensiveTicks, neutralTicks, lagPursuitTicks, purePursuitTicks, leadPursuitTicks;
        const float situationFrontAngle = 60f, situationRearAngle = 100f; // Degrees off the nose: in front of / behind.
        const float lagRange = 1500f, lagAngleOff = 40f, lagFraction = 0.25f, maxLagDistance = 200f;

        /// <summary>
        /// Who has the advantage, from where each craft is relative to the other's nose: offensive when the target is in front of us and
        /// we're behind it, defensive the other way round, neutral otherwise (head-on, beam, or chasing each other round a circle).
        /// </summary>
        FightSituation ClassifyFight(Vessel v, Vector3 vectorToTarget, float distanceToTarget)
        {
            Vector3 line = vectorToTarget / Mathf.Max(distanceToTarget, 1f);
            float ourAngle = VectorUtils.Angle(vessel.srf_vel_direction, line); // Target off our nose.
            float theirAngle = VectorUtils.Angle(v.srf_vel_direction, -line); // Us off the target's nose.
            if (ourAngle < situationFrontAngle && theirAngle > situationRearAngle) { ++offensiveTicks; return FightSituation.Offensive; }
            if (theirAngle < situationFrontAngle && ourAngle > situationRearAngle) { ++defensiveTicks; return FightSituation.Defensive; }
            ++neutralTicks;
            return FightSituation.Neutral;
        }

        /// <summary>
        /// Choose the pursuit curve for a gun attack from the geometry, given the lead a constant-rate turn onto the target would need.
        /// Lead pursuit (aim ahead) closes the range and brings the guns to bear, and is right when there's range to close.
        /// Lag pursuit (aim behind) is for an attacker that is closing too fast or sitting outside a hard-turning target's circle:
        /// it keeps us behind the target instead of flying out in front of it. Pure pursuit (aim at it) is the default in between.
        /// Returns the offset to add to the target's position.
        /// </summary>
        Vector3 PursuitLead(Vessel v, Vector3 lead, Vector3 vectorToTarget, float distanceToTarget, float angleToTarget)
        {
            var weaponManager = WeaponManager;
            Situation = ClassifyFight(v, vectorToTarget, distanceToTarget);
            if (Situation != FightSituation.Offensive || weaponManager == null) return lead; // Not behind it: the turn onto it needs all the lead it can get.
            float closure = Vector3.Dot(vessel.Velocity() - v.Velocity(), vectorToTarget) / Mathf.Max(distanceToTarget, 1f);
            float angleOff = VectorUtils.Angle(vessel.srf_vel_direction, v.srf_vel_direction); // Between our flight paths.
            bool closingTooFast = distanceToTarget < lagRange && closure > Mathf.Max(30f, (distanceToTarget - vesselStandoffDistance) / 4f);
            bool outsideItsTurn = distanceToTarget < lagRange && angleOff > lagAngleOff;
            if (closingTooFast || outsideItsTurn)
            {
                ++lagPursuitTicks;
                NoteTactic("Offensive: lag pursuit");
                return -Mathf.Min(lagFraction * distanceToTarget, maxLagDistance) * v.srf_vel_direction;
            }
            if (distanceToTarget > 1.2f * weaponManager.gunRange)
            {
                ++leadPursuitTicks;
                NoteTactic("Offensive: lead pursuit");
                return lead;
            }
            ++purePursuitTicks;
            NoteTactic("Offensive: pure pursuit");
            return Vector3.zero;
        }
        #endregion

        #region Commitment
        /// <summary>Decisions are held for a sensible time once made: on for pilots flying the advanced tactics above rookie level.</summary>
        bool Committed => advancedTactics && pilotSkill > 25f;
        const float committedEvasionTime = 2f; // s
        const float missileEvadeHoldFactor = 1.3f; // Of the evade threshold, while already evading.
        const float committedTargetBias = 1.5f;
        float missileEvadeHoldUntil, evasionHoldUntil;
        Vector3 heldEvasionDirection;
        void HoldEvasionDirection(Vector3 direction)
        {
            heldEvasionDirection = direction.normalized;
            evasionHoldUntil = Time.time + committedEvasionTime;
        }
        bool aimingLatched;
        int missileShotsHeldForOdds;

        /// <summary>Preference for staying on the current target (the weapon manager's own bias if that's higher).</summary>
        public float TargetCommitment(float targetBias) => Committed ? Mathf.Max(targetBias, committedTargetBias) : targetBias;

        // A shot is taken when its estimated chance of hitting (ShotModel) clears a threshold that depends on how many missiles are left:
        // with a full load a modest chance is worth a missile, the last ones are kept for good shots.
        const float shotThresholdLast = 0.34f, shotThresholdPerMissile = 0.03f, shotThresholdFloor = 0.12f, shotThresholdTrained = -0.05f;
        public float ShotEstimate { get; private set; } = -1f;
        public float ShotThreshold { get; private set; }

        // Patience: a shot that clears the threshold is still held while carrying on for shotWaitLead seconds would raise its chance by
        // shotWaitGain, as long as nothing is coming at us and the opponent's own best shot at us is still poor. Otherwise the first shot
        // is worth more than a better one later: it puts the opponent on the defensive.
        const float shotWaitLead = 6f, shotWaitGain = 0.12f, shotWaitOpponentLimit = 0.2f;
        const float shotDecisionInterval = 0.5f; // s: each decision flies the missile up to 8 times (~0.3 ms each in the bench).
        public float OpponentShotEstimate { get; private set; } = -1f;
        int missileShotsHeldForTiming;
        Vessel shotDecisionTarget;
        MissileBase shotDecisionMissile;
        float shotDecisionTime = -1f;
        bool shotDecision;

        float ShotThresholdFor(int remaining) => Mathf.Clamp(shotThresholdLast - shotThresholdPerMissile * remaining, shotThresholdFloor, shotThresholdLast) + (pilotSkill <= 50f ? shotThresholdTrained : 0f);

        /// <summary>
        /// Whether the selected missile is worth launching at the target now (re-decided a few times a second: each estimate flies the missile).
        /// </summary>
        bool ShotWorthTaking(Vessel target)
        {
            var weaponManager = WeaponManager;
            var missile = weaponManager != null ? weaponManager.CurrentMissile : null;
            if (missile == null || target.LandedOrSplashed) { ShotEstimate = -1f; return true; }
            if (target == shotDecisionTarget && missile == shotDecisionMissile && Time.time - shotDecisionTime < shotDecisionInterval) return shotDecision;
            shotDecisionTarget = target; shotDecisionMissile = missile; shotDecisionTime = Time.time;
            int remaining = 0;
            foreach (var other in VesselModuleRegistry.GetModules<MissileBase>(vessel))
                if (other != null && !other.HasFired) ++remaining;
            ShotThreshold = ShotThresholdFor(remaining);
            ShotEstimate = ShotModel.Estimate(missile, vessel, target, out var features);
            shotDecision = ShotEstimate >= ShotThreshold;
            if (shotDecision && !weaponManager.missileIsIncoming)
            {
                OpponentShotEstimate = BestShotAt(target, vessel);
                bool unseen = UnseenByTarget(weaponManager, target);
                bool patient = unseen || OpponentShotEstimate < shotWaitOpponentLimit && !OpponentShotSoon(weaponManager, target);
                if (patient && ShotModel.EstimateLater(missile, vessel, target, shotWaitLead) >= ShotEstimate + (unseen ? unseenWaitGain : shotWaitGain))
                {
                    shotDecision = false;
                    ++missileShotsHeldForTiming;
                    if (unseen) ++planUnseenHolds;
                }
            }
            if (!shotDecision && ForcingShotWanted(weaponManager, target, missile, features))
            {
                shotDecision = true;
                forcingPendingUntil = Time.time + 3f;
            }
            if (!shotDecision && FreeShotWanted(weaponManager, target, missile, features))
            {
                shotDecision = true;
                freeShotPendingUntil = Time.time + 3f;
            }
            return shotDecision;
        }

        /// <summary>The latest shot estimate if it was made for this target and missile in the last 2 s, else -1.</summary>
        public float ShotEstimateFor(Vessel target, MissileBase missile) =>
            target != null && target == shotDecisionTarget && missile == shotDecisionMissile && Time.time - shotDecisionTime < 2f ? ShotEstimate : -1f;

        static readonly System.Collections.Generic.HashSet<string> shotTypesSeen = new System.Collections.Generic.HashSet<string>();
        /// <summary>The best chance any of a craft's unfired air-to-air missiles would have against another, launched now (0 if none); one estimate per missile type.</summary>
        static float BestShotAt(Vessel shooter, Vessel target)
        {
            float best = 0f;
            shotTypesSeen.Clear();
            foreach (var missile in VesselModuleRegistry.GetModules<MissileBase>(shooter))
            {
                if (missile == null || missile.HasFired || !missile.engageAir || missile.part == null || missile.part.partInfo == null || !shotTypesSeen.Add(missile.part.partInfo.name)) continue;
                best = Mathf.Max(best, ShotModel.Estimate(missile, shooter, target));
            }
            return best;
        }
        #endregion

        #region Skill, readouts and teammates
        public static readonly string[] skillNames = { "Rookie", "Trained", "Veteran", "Ace" };
        /// <summary>0 (rookie) to 3 (ace).</summary>
        public int SkillLevel
        {
            get => Mathf.Clamp(Mathf.RoundToInt(pilotSkill / 25f) - 1, 0, skillNames.Length - 1);
            set => pilotSkill = 25f * (Mathf.Clamp(value, 0, skillNames.Length - 1) + 1);
        }
        const float rookieAimError = 2.5f; // Degrees of wander in a rookie's aim; none for an ace.

        /// <summary>
        /// Smooth wander of the aiming direction, larger the less skilled the pilot.
        /// </summary>
        Vector3 SkillAimError(Vector3 direction)
        {
            float amplitude = rookieAimError * (1f - 0.01f * pilotSkill) / 0.75f;
            float time = 0.7f * Time.time, seed = vessel.id.GetHashCode() & 0xFF;
            Vector3 wander = (2f * Mathf.PerlinNoise(time, seed) - 1f) * vesselTransform.right + (2f * Mathf.PerlinNoise(time, seed + 31.7f) - 1f) * vesselTransform.forward;
            return Vector3.RotateTowards(direction, wander.normalized, amplitude * wander.magnitude * Mathf.Deg2Rad, 0f);
        }

        string activeTactic;
        float activeTacticTime = -100f;
        int bracketCount;
        void NoteTactic(string tactic)
        {
            activeTactic = tactic;
            activeTacticTime = Time.time;
        }

        /// <summary>A teammate whose weapon manager has the same target as ours, or null.</summary>
        Vessel TeammateOnTarget()
        {
            var weaponManager = WeaponManager;
            if (weaponManager == null || targetVessel == null) return null;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || other == vessel || !other.loaded) continue;
                var otherWM = other.ActiveController().WM;
                if (otherWM != null && otherWM.Team == weaponManager.Team && otherWM.currentTarget != null && otherWM.currentTarget.Vessel == targetVessel) return other;
            }
            return null;
        }

        static readonly System.Collections.Generic.List<string> readout = new System.Collections.Generic.List<string>();
        /// <summary>
        /// Lines for the AI window: what the pilot is doing and what it can see.
        /// </summary>
        public System.Collections.Generic.List<string> TacticsReadout()
        {
            readout.Clear();
            var weaponManager = WeaponManager;
            string role = weaponManager == null ? "—" : weaponManager.isVIP ? "VIP" : VIPTracker.GetVIP(weaponManager.Team) != null ? "Escort" : "—";
            readout.Add($"Role: {role}");
            if (Situation != FightSituation.None && targetVessel != null) readout.Add($"Position: {Situation}");
            readout.Add($"Doing: {(Time.time - activeTacticTime < 1f ? activeTactic : currentStatus)}");
            if (weaponManager != null && targetVessel != null)
            {
                var visual = weaponManager.VisualSample(targetVessel);
                float range = Mathf.Min(weaponManager.guardRange, visual.blocked ? 0f : visual.range);
                readout.Add($"Target: {(targetVessel.CoM - vessel.CoM).magnitude / 1000f:0.0} km");
                readout.Add($"Visible to: {range / 1000f:0.0} km ({visual.reason})");
            }
            else readout.Add("Target: —");
            if (ShotEstimate >= 0f) readout.Add($"Shot chance: {ShotEstimate:0.00} (fires at {ShotThreshold:0.00})");
            if (targetVessel != null && PictureOf(targetVessel))
                readout.Add($"Plan: {planMode} · ours {picture.ourShot:0.00}→{picture.ourShotLater:0.00} · theirs {picture.theirShot:0.00}→{picture.theirShotLater:0.00}{(picture.theirInescapableLater ? " NEZ" : "")}");
            readout.Add($"Shots held: {missileShotsHeldForOdds} odds, {missileShotsHeldForTiming} timing, {missileShotsHeldForTerrain} terrain");
            return readout;
        }
        #endregion

        #region VIP and escorts
        int vipFleeTicks, escortTicks, escortRecallCount;
        bool escortRecalled;
        const float vipSelfDefenceRange = 1500f; // m: inside this the VIP fights if the attacker is in front of it.
        const float escortLeash = 12000f; // m from the VIP beyond which an escort won't chase a target that isn't going for the VIP.
        const float escortRejoin = 6000f; // m from the VIP at which a recalled escort is free to engage again.
        const float escortStationSide = 250f, escortStationBack = 150f, escortStationUp = 50f; // m

        /// <summary>
        /// Behaviour of a craft marked as its team's VIP, and of the craft on a team that has one. Marking the VIP is the only setup.
        /// The VIP's job is to survive: while it has an escort it turns away from any enemy it knows of and runs at full power, and only
        /// fights an attacker that is close and in front of it. Without an escort it fights like anyone else.
        /// An escort with nothing to shoot at flies on the VIP's wing instead of orbiting, and doesn't chase a target far from the VIP
        /// unless that target is going for the VIP or for the escort itself.
        /// Missile evasion, terrain avoidance and the defensive break all come before this.
        /// Returns true if it flew the craft this frame.
        /// </summary>
        bool VIPBehaviour(FlightCtrlState s)
        {
            var weaponManager = WeaponManager;
            if (weaponManager == null || !weaponManager.guardMode || vessel.LandedOrSplashed || belowMinAltitude || ramming) return false;
            if (weaponManager.isVIP)
            {
                if (targetVessel == null || VIPTracker.EscortCount(weaponManager.Team) == 0) return false;
                Vector3 toThreat = targetVessel.CoM - vessel.CoM;
                float distance = toThreat.magnitude;
                if (distance < vipSelfDefenceRange && Vector3.Dot(toThreat, vesselTransform.up) > 0) return false; // Cornered with a shot available: take it.
                Vector3 away = (-toThreat).ProjectOnPlanePreNormalized(upDirection).normalized;
                if (away.sqrMagnitude < 0.5f) away = vessel.srf_vel_direction.ProjectOnPlanePreNormalized(upDirection).normalized;
                // Run for home (the position this pilot was assigned to hold) when that doesn't mean running at the threat.
                Vector3 home = (assignedPositionWorld - vessel.CoM).ProjectOnPlanePreNormalized(upDirection);
                if (home.sqrMagnitude > 4e6f && Vector3.Dot(home.normalized, away) > 0) away = (away + home.normalized).normalized;
                Vector3 flyTo = vessel.CoM + 5000f * away;
                flyTo += (defaultAltitude - BodyUtils.GetRadarAltitudeAtPos(flyTo)) * upDirection;
                SetStatus("VIP: Withdrawing");
                steerMode = SteerModes.NormalFlight;
                useAB = true; useBrakes = false;
                AdjustThrottle(maxSpeed, false, true);
                FlyToPosition(s, flyTo, true);
                { ++vipFleeTicks; NoteTactic("VIP withdrawing"); }
                return true;
            }
            Vessel vip = VIPTracker.GetVIP(weaponManager.Team);
            if (vip == null || vip == vessel) { escortRecalled = false; return false; }
            float distanceToVIP = (vip.CoM - vessel.CoM).magnitude;
            if (targetVessel != null)
            {
                // Engage as usual, unless the target is leading us away from a VIP it isn't threatening.
                var targetWM = targetVessel.ActiveController().WM;
                Vessel targetsTarget = targetWM != null && targetWM.currentTarget != null ? targetWM.currentTarget.Vessel : null;
                bool threatening = targetsTarget == vip || targetsTarget == vessel || (targetVessel.CoM - vip.CoM).sqrMagnitude < escortLeash * escortLeash;
                if (threatening) escortRecalled = false;
                else if (!escortRecalled && distanceToVIP > escortLeash) { escortRecalled = true; ++escortRecallCount; }
                else if (escortRecalled && distanceToVIP < escortRejoin) escortRecalled = false;
                if (!escortRecalled) return false;
            }
            else escortRecalled = false;

            // Take station on the VIP's wing: alternate sides by craft so that two escorts don't share a spot.
            Vector3 vipForward = vip.srf_vel_direction;
            Vector3 vipRight = Vector3.Cross(upDirection, vipForward).normalized;
            float side = (vessel.id.GetHashCode() & 1) == 0 ? 1f : -1f;
            Vector3 station = vip.CoM + side * escortStationSide * vipRight - escortStationBack * vipForward + escortStationUp * upDirection;
            float distanceToStation = (station - vessel.CoM).magnitude;
            float vipSpeed = (float)vip.srfSpeed;
            Vector3 flyToStation = station + Mathf.Clamp(distanceToStation / Mathf.Max(50f, (float)vessel.srfSpeed), 0f, 5f) * vip.Velocity(); // Where the station will be when we get there.
            SetStatus(targetVessel != null ? "Escort: Rejoining VIP" : "Escorting VIP");
            steerMode = SteerModes.NormalFlight;
            if (distanceToStation > 1000f)
            {
                useAB = true; useBrakes = false;
                AdjustThrottle(maxSpeed, false, true);
            }
            else
            {
                // Close the remaining gap at a speed that falls to the VIP's own as the gap closes.
                float along = Vector3.Dot(station - vessel.CoM, vipForward);
                float wanted = Mathf.Clamp(vipSpeed + 0.2f * along, Mathf.Max(minSpeed, 0.6f * vipSpeed), maxSpeed);
                useAB = false; useBrakes = true;
                AdjustThrottle(wanted, true, false);
                flyToStation = vessel.CoM + 300f * (flyToStation + 300f * vipForward - vessel.CoM).normalized; // Converge on the VIP's heading, not on a point.
            }
            FlyToPosition(s, flyToStation, true);
            { ++escortTicks; NoteTactic("Escorting VIP"); }
            return true;
        }
        #endregion

        int snapshotTicks, stalemateBreakCount;
        float stalemateTimer, stalemateBreakUntil, stalemateBreakDirection;
        const float stalemateRange = 2000f, stalemateAngle = 30f, stalemateTime = 6f, stalemateBreakTime = 4f;

        /// <summary>
        /// Two craft of similar performance turning after each other get nowhere: each stays well off the other's nose for as long as both
        /// keep turning in the same plane. After some seconds of that, leave the plane for a few seconds — down and inside the turn where
        /// there's height to spare (which also adds speed), otherwise up and over, where the slower top of the turn tightens it.
        /// Returns the offset to add to the fly-to position.
        /// </summary>
        Vector3 StalemateBreak(float distanceToTarget, float angleToTarget)
        {
            if (Time.time < stalemateBreakUntil) return stalemateBreakDirection * Mathf.Min(0.35f * distanceToTarget, 400f) * upDirection;
            if (distanceToTarget > stalemateRange || angleToTarget < stalemateAngle || !TacticsAllowed(WeaponManager)) { stalemateTimer = 0; return Vector3.zero; }
            stalemateTimer += Time.fixedDeltaTime;
            if (stalemateTimer < stalemateTime / Mathf.Max(0.25f, 0.01f * pilotSkill)) return Vector3.zero; // Less skilled pilots take longer to notice.
            stalemateTimer = 0;
            stalemateBreakUntil = Time.time + stalemateBreakTime;
            float spareHeight = (float)vessel.radarAltitude - minAltitude - 2f * turnRadiusTwiddleFactorMax * turnRadius;
            stalemateBreakDirection = spareHeight > 300f ? -1f : 1f;
            ++stalemateBreakCount;
            NoteTactic(stalemateBreakDirection < 0 ? "Stalemate break: low" : "Stalemate break: high");
            return Vector3.zero;
        }

        int missileShotsHeldForTerrain;
        const float missileGroundMargin = 40f; // m
        const float missileSteeringDelay = 2.5f; // Seconds under power before a missile has turned appreciably from the direction it was launched in.
        const float missileBoostSpeedGain = 150f; // Average speed gained over that time (m/s).

        /// <summary>
        /// Whether a missile released now would stay clear of the ground: through its unpowered drop, through the first seconds under power
        /// in the direction our nose is pointing, and along the line to an airborne target. A shot that fails this is held, not skipped:
        /// the weapon manager asks again on its next pass.
        /// </summary>
        bool MissileHasTerrainClearance(Vessel target)
        {
            if (vessel.LandedOrSplashed || vessel.InNearVacuum()) return true;
            var weaponManager = WeaponManager;
            var missile = weaponManager != null ? weaponManager.CurrentMissile : null;
            if (missile == null) return true;
            float dropTime = missile is MissileLauncher launcher ? launcher.dropTime : 0f;
            Vector3 velocity = vessel.Velocity();
            Vector3 gravity = -(float)vessel.mainBody.GeeASL * 9.81f * upDirection;
            Vector3 ignition = vessel.CoM + velocity * dropTime + 0.5f * dropTime * dropTime * gravity;
            if (BodyUtils.GetRadarAltitudeAtPos(ignition) < missileGroundMargin) return false;
            if (target.LandedOrSplashed) return true; // It's meant to go down.
            Vector3 heading = missile.GetForwardTransform();
            float distance = ((float)vessel.srfSpeed + missileBoostSpeedGain) * missileSteeringDelay;
            Vector3 fall = dropTime * missileSteeringDelay * gravity; // Downward speed picked up in the drop carries on.
            if (BodyUtils.GetRadarAltitudeAtPos(ignition + 0.5f * (distance * heading + fall)) < missileGroundMargin) return false;
            if (BodyUtils.GetRadarAltitudeAtPos(ignition + distance * heading + fall) < missileGroundMargin) return false;
            return !VisualDetection.BlockedByBody(ignition, target.CoM, vessel.mainBody);
        }

        #region Sensors and countermeasures
        const int maxPreemptiveFlareSalvos = 6;
        bool rearAspectHeatThreat; // An opponent is behind us in a position to launch a heat-seeker.
        float lastPreemptiveFlareTime = -100f, radarSilenceHoldUntil;
        bool radarSilent;
        int tacticalStateCounter;
        TargetInfo ownTargetInfo;

        /// <summary>
        /// Slow (5Hz) decisions: heat-seeker denial, jamming a radar lock, radar silence and how much of our side we can afford to show.
        /// </summary>
        void UpdateTacticalState()
        {
            var weaponManager = WeaponManager;
            if (!TacticsAllowed(weaponManager))
            {
                rearAspectHeatThreat = false;
                if (radarSilent) SetRadarSilence(weaponManager, false);
                return;
            }
            Vessel threat = targetVessel;
            float distance = threat != null && threat.transform != null && !threat.LandedOrSplashed ? (threat.CoM - vessel.CoM).magnitude : float.MaxValue;

            // Heat: an opponent behind us, beyond gun range and pointing at us can launch at our exhaust. Keep the afterburner off (see AdjustThrottle)
            // and put flares out before the launch rather than after it.
            rearAspectHeatThreat = false;
            if (distance < 5000f && distance > Mathf.Clamp(weaponManager.gunRange, 600f, 2000f))
            {
                Vector3 toThreat = (threat.CoM - vessel.CoM) / distance;
                rearAspectHeatThreat = Vector3.Dot(toThreat, vesselTransform.up) < 0 && Vector3.Dot(threat.ReferenceTransform.up, -toThreat) > 0.866f;
                if (rearAspectHeatThreat && distance < 4000f && Time.time - lastPreemptiveFlareTime > 6f && (weaponManager.salvoDecisions || preemptiveFlareSalvos < maxPreemptiveFlareSalvos)
                    && weaponManager.FirePreemptiveFlares()) // Rationed against what the enemies' heat-seekers will need: the rest are kept for missiles actually in the air.
                {
                    lastPreemptiveFlareTime = Time.time;
                    ++preemptiveFlareSalvos;
                }
            }

            // Jamming: start when a radar locks us up instead of waiting for the missile, unless something is homing on emissions.
            var rwr = weaponManager.rwr;
            if (rwr != null && rwr.rwrEnabled && rwr.pingsData != null && weaponManager.jammers.Count > 0 && !weaponManager.isECMJamming && !weaponManager.results.foundAntiRadiationMissile)
            {
                for (int i = 0; i < rwr.pingsData.Length; ++i)
                {
                    if (!rwr.pingsData[i].exists || rwr.pingsData[i].signalType != RadarWarningReceiver.RWRThreatTypes.MissileLock) continue;
                    weaponManager.FireECM(10);
                    break;
                }
            }

            // Radar silence: with an infrared track or a wingman's radar picture of the target and nothing that needs our own radar,
            // leave it off so that the opponent's warning receiver has nothing to hear from us. Only radars the user has marked as
            // switchable by the AI (Dynamic Radar) are touched.
            bool otherTrack = (weaponManager._irstsEnabled && weaponManager.irsts.Count > 0)
                || (weaponManager.vesselRadarData != null && weaponManager.vesselRadarData.MaxRadarLocksExternal > 0);
            bool wantSilent = distance < float.MaxValue && distance > Mathf.Max(2f * weaponManager.gunRange, 4000f)
                && otherTrack
                && !weaponManager.missileIsIncoming
                && (weaponManager.CurrentMissile == null || weaponManager.CurrentMissile.TargetingMode != MissileBase.TargetingModes.Radar)
                && weaponManager.currentTarget != null && weaponManager.GetMissilesAway(weaponManager.currentTarget).numAway == 0
                && !(weaponManager.staleTarget.TryGetValue(threat, out bool stale) && stale)
                && Time.time > radarSilenceHoldUntil;
            if (radarSilent && wantSilent)
            {
                // Something else has switched a radar back on, i.e., it's needed: leave it alone for a while.
                foreach (var radar in weaponManager.radars)
                    if (radar != null && radar.radarEnabled && (radar.DynamicRadar || weaponManager.DynamicRadarOverride)) { radarSilenceHoldUntil = Time.time + 15f; wantSilent = false; radarSilent = false; break; }
            }
            if (wantSilent != radarSilent) SetRadarSilence(weaponManager, wantSilent);

            // Radar cross-section: how far off the nose we can turn before a threat radar sees more than twice our head-on return.
            if (tacticalStateCounter++ % 25 == 0)
            {
                offAspectLimit = 60f;
                if (ownTargetInfo == null) ownTargetInfo = vessel.gameObject.GetComponent<TargetInfo>();
                if (ownTargetInfo != null && ownTargetInfo.radarSignatureMatrix != null && ownTargetInfo.radarBaseSignature > 0)
                {
                    float headOn = RadarUtils.RCSMatrixEval(ownTargetInfo.radarSignatureMatrix, ownTargetInfo.radarBaseSignature, 0f, 0f);
                    if (headOn > 0)
                    {
                        offAspectLimit = 10f;
                        for (float angle = 20f; angle <= 60f; angle += 10f)
                        {
                            if (RadarUtils.RCSMatrixEval(ownTargetInfo.radarSignatureMatrix, ownTargetInfo.radarBaseSignature, angle, 0f) > 2f * headOn) break;
                            offAspectLimit = angle;
                        }
                    }
                }
            }
        }

        void SetRadarSilence(MissileFire weaponManager, bool silent)
        {
            radarSilent = silent;
            if (weaponManager == null) return;
            if (!silent && weaponManager.results.foundAntiRadiationMissile) return; // The stock anti-radiation logic wants them off.
            bool changed = false;
            foreach (var radar in weaponManager.radars)
            {
                if (radar == null || !(radar.DynamicRadar || weaponManager.DynamicRadarOverride)) continue;
                if (silent && radar.radarEnabled) { radar.DisableRadar(); changed = true; }
                else if (!silent && !radar.radarEnabled) { radar.EnableRadar(); changed = true; }
            }
            if (changed) weaponManager._radarsEnabled = !silent;
            if (silent) ++radarSilenceCount;
        }
        #endregion

        // How long each tactic has been flown (physics ticks) and how often the radar has been silenced, for the steering metrics log.
        int pumpTicks, offsetTicks, oneCircleTicks, energyClimbTicks, radarSilenceCount, energyExtendCount, zoomTicks, preemptiveFlareSalvos;
    }
}
