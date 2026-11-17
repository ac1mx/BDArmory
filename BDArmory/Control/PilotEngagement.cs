using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Engagement planning (with Advanced Tactics): the judgement a good pilot flies a missile fight with, on top of the geometry in
    /// PilotTactics. About once a second the pilot builds a picture of the fight from the missile flight replica: each side's best shot
    /// if it turned its nose onto the other now and a few seconds on, whether that shot could still be outrun (its no-escape zone), the
    /// missiles in the air both ways and when they arrive, which of ours can no longer reach, what each side has left to shoot, our
    /// damage, both sides' turn performance, whether the target's radar has us, and other enemies pointing at us. From it:
    ///  - forcing shot: a low-chance launch that still reaches, to put an opponent that is about to shoot on the defensive (needs a
    ///    spare missile, never while unseen), then press the defending target for the killing shot;
    ///  - shoot-look-shoot: a missile that can no longer reach stops holding up the next one;
    ///  - press: close on a target that is defending, or that has nothing left to shoot, instead of cranking or pumping;
    ///  - cut-off: fly a collision course at a crossing target instead of chasing it;
    ///  - support through: keep guiding our missile instead of defending when it arrives before the incoming one can guide itself;
    ///  - abort before the opponent's no-escape zone when it has the better of the shot, diving cold, and pitch back once clear;
    ///  - no merge: turn away from a close-in fight we'd lose (out-turned, out-gunned, damaged or another bandit about) while we
    ///    still have missiles for range and can outrun its shot;
    ///  - angles or energy: weight the close-in planner towards energy against a better turner, towards angles against a worse one;
    ///  - patience while unseen; crank away from other threats;
    ///  - bugout from a scissors the close-in planner keeps scoring as lost (PilotBFM).
    /// Two-ship tactics (bracket, grinder, sandwich, drag, split, support) are in PilotSection.
    /// Each decision is counted for the steering metrics log, and the whole layer can be switched off for comparison.
    /// </summary>
    public partial class BDModulePilotAI
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Engagement Planner", // Shot judgement and BVR planning (needs Advanced Tactics).
            groupName = "pilotAI_EvadeExtend", groupDisplayName = "#LOC_BDArmory_AI_EvadeExtend", groupStartCollapsed = true),
            UI_Toggle(enabledText = "#LOC_BDArmory_Enabled", disabledText = "#LOC_BDArmory_Disabled", scene = UI_Scene.All),]
        public bool engagementPlanner = true;

        #region Picture
        class EngagementPicture
        {
            public Vessel target;
            public float time = -100f, lead;
            public float ourShot, ourShotLater, theirShot, theirShotLater, theirFlightTime;
            public bool ourReaches, theirInescapable, theirInescapableLater;
            public int ourLong, ourShort, theirLong, theirShort;
            public float ourMissileTimeToGo = -1f; // Earliest arrival of one of ours that still reaches the target flying on as it is.
            public bool ourMissileNeedsSupport;
            public int ourMissilesInFlight; // At the target, written off or not.
            public bool detectedByTarget = true;
            public float health = 1f;
            public int otherThreats;
            public float otherThreatShot;
            public bool otherThreatInescapable;
            public Vector3 otherThreatPosition;
            // Launch zones along the present line (m, 0 until worked out): our best missile's maximum and no-escape range against the
            // target, and the target's no-escape range against us.
            public float ourMaxRange, ourNoEscapeRange, theirNoEscapeRange;
            public float assistGain; // What lofting the launch from higher up would add to our best shot's chance.
            public void CopyFrom(EngagementPicture p)
            {
                ourMaxRange = p.ourMaxRange; ourNoEscapeRange = p.ourNoEscapeRange; theirNoEscapeRange = p.theirNoEscapeRange; assistGain = p.assistGain;
                target = p.target; time = p.time; lead = p.lead;
                ourShot = p.ourShot; ourShotLater = p.ourShotLater; theirShot = p.theirShot; theirShotLater = p.theirShotLater; theirFlightTime = p.theirFlightTime;
                ourReaches = p.ourReaches; theirInescapable = p.theirInescapable; theirInescapableLater = p.theirInescapableLater;
                ourLong = p.ourLong; ourShort = p.ourShort; theirLong = p.theirLong; theirShort = p.theirShort;
                ourMissileTimeToGo = p.ourMissileTimeToGo; ourMissileNeedsSupport = p.ourMissileNeedsSupport; ourMissilesInFlight = p.ourMissilesInFlight; detectedByTarget = p.detectedByTarget; health = p.health;
                otherThreats = p.otherThreats; otherThreatShot = p.otherThreatShot; otherThreatInescapable = p.otherThreatInescapable; otherThreatPosition = p.otherThreatPosition;
            }
        }
        readonly EngagementPicture picture = new EngagementPicture(), pictureWork = new EngagementPicture();
        const float pictureInterval = 1f; // s between pictures; the work of one is spread over several physics ticks.
        int pictureStage = -1;
        float pictureStartTime = -100f;
        long plannerTicks; int plannerCycles;
        float health = 1f; // Airframe integrity at the last picture.
        readonly List<MissileBase> ownMissilesSeen = new List<MissileBase>();

        bool PlanActive(MissileFire weaponManager) => engagementPlanner && TacticsAllowed(weaponManager);
        bool PictureOf(Vessel v) => v != null && picture.target == v && Time.time - picture.time < 3f * pictureInterval;

        /// <summary>
        /// How hard the target and anyone else are pressing us, for sizing a salvo at it: the target's best shot at us now and whether it would
        /// be inescapable, other threats, and our airframe integrity. False without a fresh picture of the target.
        /// </summary>
        public bool SalvoContext(Vessel target, out float theirShot, out bool theirInescapable, out int otherThreats, out float ourHealth)
        {
            bool fresh = PictureOf(target);
            theirShot = fresh ? picture.theirShot : -1f;
            theirInescapable = fresh && picture.theirInescapable;
            otherThreats = fresh ? picture.otherThreats : 0;
            ourHealth = fresh ? picture.health : 1f;
            return fresh;
        }

        /// <summary>One stage of the picture per physics tick (each costs at most a few missile flyouts).</summary>
        void UpdateEngagementPicture()
        {
            var weaponManager = WeaponManager;
            if (!PlanActive(weaponManager)) { pictureStage = -1; picture.target = null; return; }
            Vessel target = targetVessel != null && targetVessel.loaded && !targetVessel.LandedOrSplashed ? targetVessel : null;
            if (pictureStage < 0)
            {
                if (target == null || Time.time - pictureStartTime < pictureInterval * (0.9f + 0.2f * ((vessel.id.GetHashCode() & 7) / 7f))) return;
                if (target.IsMissile()) return; // Targeting an incoming missile (point defence): not a fight to plan.
                pictureStartTime = Time.time;
                pictureStage = 0;
            }
            if (target == null || (pictureStage > 0 && target != pictureWork.target)) { pictureStage = -1; return; }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var p = pictureWork;
            switch (pictureStage)
            {
                case 0:
                    if (p.target != target) { p.ourMaxRange = p.ourNoEscapeRange = p.theirNoEscapeRange = p.assistGain = 0f; zoneCycle = 0; }
                    p.target = target;
                    p.lead = PlanLead();
                    CountMissiles(vessel, out p.ourLong, out p.ourShort);
                    CountMissiles(target, out p.theirLong, out p.theirShort);
                    p.detectedByTarget = DetectedBy(weaponManager, target);
                    p.health = health = GameModes.AirframeIntegrity.Of(vessel);
                    TrackOwnMissiles(weaponManager, target, p);
                    UpdateMatchup(weaponManager, target, p);
                    break;
                case 1: p.ourShot = BestPointedShot(vessel, target, 0f, out var ours); p.ourReaches = ours.valid && ours.straight.reached; planOwnMissile = bestPointedMissile; break;
                case 2: p.ourShotLater = BestPointedShot(vessel, target, p.lead, out _); break;
                case 3: p.theirShot = BestPointedShot(target, vessel, 0f, out var theirs); p.theirInescapable = theirs.valid && theirs.escape.reached; p.theirFlightTime = theirs.valid ? theirs.straight.time : 0f; planTheirMissile = bestPointedMissile; break;
                case 4: p.theirShotLater = BestPointedShot(target, vessel, p.lead, out var theirsLater); p.theirInescapableLater = theirsLater.valid && theirsLater.escape.reached; break;
                case 5: OtherThreats(weaponManager, target, p); break;
                // The launch zones take several flights each: one a tick, and only every fifth picture.
                case 6: if (zoneCycle % zoneInterval == 0 && planOwnMissile != null) p.ourMaxRange = ShotModel.ZoneRange(planOwnMissile, vessel, target, false, p.ourMaxRange > 0f ? p.ourMaxRange : (target.CoM - vessel.CoM).magnitude); break;
                case 7: if (zoneCycle % zoneInterval == 0 && planOwnMissile != null) p.ourNoEscapeRange = Mathf.Min(p.ourMaxRange, ShotModel.ZoneRange(planOwnMissile, vessel, target, true, p.ourNoEscapeRange > 0f ? p.ourNoEscapeRange : 0.5f * Mathf.Max(p.ourMaxRange, 2000f))); break; // A target that runs is never reached from further than one that doesn't.
                case 8: if (zoneCycle % zoneInterval == 0 && planTheirMissile != null) p.theirNoEscapeRange = ShotModel.ZoneRange(planTheirMissile, target, vessel, true, p.theirNoEscapeRange > 0f ? p.theirNoEscapeRange : 0.5f * Mathf.Max(p.ourMaxRange, 2000f)); break;
                case 9:
                    p.assistGain = planOwnMissile != null && planOwnMissile.TargetingMode != MissileBase.TargetingModes.Heat
                        ? ShotModel.EstimateAssisted(planOwnMissile, vessel, target, launchLoftAngle, launchLoftClimb) - p.ourShot : 0f;
                    if (steeringMetricsEnabled && zoneCycle % (5 * zoneInterval) == 0 && planOwnMissile != null)
                        Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[BDArmory.BDModulePilotAI]: Zones {0} at {1}: {2} range={3:F0} maxRange={4:F0} noEscape={5:F0} theirNoEscape={6:F0} shot={7:F2} assistGain={8:F2}",
                            vessel.vesselName, target.vesselName, planOwnMissile.GetShortName(), (target.CoM - vessel.CoM).magnitude, p.ourMaxRange, p.ourNoEscapeRange, p.theirNoEscapeRange, p.ourShot, p.assistGain));
                    ++zoneCycle;
                    break;
            }
            plannerTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (++pictureStage > 9)
            {
                p.time = Time.time;
                picture.CopyFrom(p);
                pictureStage = -1;
                ++plannerCycles;
            }
        }

        /// <summary>Seconds ahead for the "a little later" shots: the time to turn our back on the target, plus a margin.</summary>
        float PlanLead()
        {
            float available = Mathf.Max(LiftCapability * liftMachFactor * (float)vessel.dynamicPressurekPa, 10f);
            return Mathf.Clamp(Mathf.PI * (float)vessel.srfSpeed / available + 2f, 4f, 14f);
        }

        static void CountMissiles(Vessel v, out int longRange, out int shortRange)
        {
            longRange = shortRange = 0;
            foreach (var missile in VesselModuleRegistry.GetModules<MissileBase>(v))
            {
                if (missile == null || missile.HasFired || !missile.engageAir) continue;
                if (missile.TargetingMode == MissileBase.TargetingModes.Heat) shortRange += RoundsAboard(missile);
                else if (missile.TargetingMode == MissileBase.TargetingModes.Radar || missile.TargetingMode == MissileBase.TargetingModes.Inertial) longRange += RoundsAboard(missile);
            }
        }

        const int zoneInterval = 5;
        const float launchLoftAngle = 12f, launchLoftClimb = 1000f; // The loft (deg above the line to the target) and climb (m) the pilot can give a launch.
        int zoneCycle;
        MissileBase planOwnMissile, planTheirMissile;
        static MissileBase bestPointedMissile; // The missile behind the last BestPointedShot.
        static readonly HashSet<string> pointedTypesSeen = new HashSet<string>();
        /// <summary>The best chance any of a craft's unfired air-to-air missiles would have against another if launched lead seconds from now with the nose on it.</summary>
        static float BestPointedShot(Vessel shooter, Vessel target, float lead, out ShotModel.Features best)
        {
            float bestChance = 0f;
            best = default;
            bestPointedMissile = null;
            pointedTypesSeen.Clear();
            foreach (var missile in VesselModuleRegistry.GetModules<MissileBase>(shooter))
            {
                if (missile == null || missile.HasFired || !missile.engageAir || missile.part == null || missile.part.partInfo == null || !pointedTypesSeen.Add(missile.part.partInfo.name)) continue;
                float chance = ShotModel.EstimatePointed(missile, shooter, target, lead, out var features);
                if (!best.valid || chance > bestChance) { bestChance = chance; best = features; bestPointedMissile = missile; }
            }
            return bestChance;
        }

        /// <summary>Whether the target's radar has us, from our warning receiver (assumed so without one).</summary>
        static bool DetectedBy(MissileFire weaponManager, Vessel target)
        {
            var rwr = weaponManager.rwr;
            if (rwr == null || !rwr.rwrEnabled || rwr.pingsData == null) return true;
            if (rwr.EmitterOf(target) != null) return true;
            for (int i = 0; i < rwr.pingsData.Length; ++i)
                if (rwr.pingsData[i].exists && rwr.pingsData[i].vessel == target && rwr.pingsData[i].signalType != RadarWarningReceiver.RWRThreatTypes.MWS) return true;
            return false;
        }

        /// <summary>Our missiles in the air at the target: the earliest that still reaches it, whether it needs our radar, and the ones that can't reach any more.</summary>
        void TrackOwnMissiles(MissileFire weaponManager, Vessel target, EngagementPicture p)
        {
            p.ourMissileTimeToGo = -1f;
            p.ourMissileNeedsSupport = false;
            p.ourMissilesInFlight = 0;
            for (int i = ownMissilesSeen.Count - 1; i >= 0; --i)
                if (ownMissilesSeen[i] == null || ownMissilesSeen[i].HasExploded || ownMissilesSeen[i].HasMissed) ownMissilesSeen.RemoveAt(i);
            foreach (var weapon in BDATargetManager.FiredMissiles)
            {
                if (weapon is not MissileBase missile || missile.SourceVessel != vessel || !missile.HasFired || missile.HasExploded || missile.HasMissed) continue;
                if (!ownMissilesSeen.Contains(missile)) { ownMissilesSeen.Add(missile); NoteLaunch(missile, missile.targetVessel != null ? missile.targetVessel.Vessel : null); }
                if (missile.targetVessel == null || missile.targetVessel.Vessel != target) continue;
                ++p.ourMissilesInFlight;
                if (missile.judgedDefeated || missile is not MissileLauncher ml || ml.vessel == null) continue;
                var result = ShotModel.Intercept(ml, target.CoM, target.Velocity(), Vector3.zero, Mathf.Max((float)target.srfSpeed, 50f));
                if (result.reached)
                {
                    if (p.ourMissileTimeToGo < 0f || result.time < p.ourMissileTimeToGo) p.ourMissileTimeToGo = result.time;
                    // Until its own seeker is on, and after that for as long as our lock can put it back on the target over its data link.
                    if (missile.TargetingMode == MissileBase.TargetingModes.Radar && (!missile.ActiveRadar || (missile.smartSeeker && missile.datalink)))
                    {
                        p.ourMissileNeedsSupport = true;
                        if (missile.ActiveRadar) ++planLinkSupports;
                    }
                }
                else if (ml.MissileState == MissileBase.MissileStates.PostThrust && ml.TimeIndex > 3f && (result.gaveUp || result.missDistance > 5f * Mathf.Max(ml.GetBlastRadius(), 10f)))
                {
                    // Out of motor and can't catch it any more: write it off so that it doesn't hold up the next shot.
                    missile.judgedDefeated = true;
                    lastDefeatedTime = Time.time;
                    ++planDefeatedMissiles;
                    if (steeringMetricsEnabled) Debug.Log($"[BDArmory.BDModulePilotAI]: Planner {vessel.vesselName}: missile at {target.vesselName} written off after {ml.TimeIndex:F0}s (miss {result.missDistance:F0}m)");
                }
            }
        }

        /// <summary>Other enemies pointing at us within reach: how many, and the best of their shots.</summary>
        void OtherThreats(MissileFire weaponManager, Vessel target, EngagementPicture p)
        {
            p.otherThreats = 0; p.otherThreatShot = 0f; p.otherThreatInescapable = false;
            float nearest = float.MaxValue;
            int evaluated = 0;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || other == vessel || other == target || !other.loaded || other.LandedOrSplashed) continue;
                var otherWM = other.ActiveController().WM;
                if (otherWM == null || weaponManager.Team == null || !weaponManager.Team.IsEnemy(otherWM.Team)) continue;
                Vector3 toUs = vessel.CoM - other.CoM;
                float distance = toUs.magnitude;
                if (distance > otherThreatRange || Vector3.Dot(other.srf_vel_direction, toUs) < 0.766f * distance) continue; // Not within 40° of pointing at us.
                if (!KnownThreat(weaponManager, other, distance)) { ++rwrUnseenThreats; continue; } // Nothing tells us it is there.
                ++p.otherThreats;
                if (distance < nearest) { nearest = distance; p.otherThreatPosition = other.CoM; }
                if (evaluated++ >= 2) continue; // The two nearest are enough for the shot.
                float shot = BestPointedShot(other, vessel, 0f, out var features);
                p.otherThreatShot = Mathf.Max(p.otherThreatShot, shot);
                p.otherThreatInescapable |= features.valid && features.escape.reached;
            }
        }
        const float otherThreatRange = 25000f;
        #endregion

        #region Shot judgement
        // Forcing shot: taken below the normal threshold if it reaches and the opponent is about to have a shot of its own.
        const float forcingFloor = 0.08f, forcingPressure = 0.15f, forcingInterval = 20f, followUpWindow = 45f, unseenWaitGain = 0.05f;
        float forcingPendingUntil = -1f, lastForcingTime = -100f, lastDefeatedTime = -100f;
        Vessel lastForcingTarget;
        int planForcingShots, planFollowUps, planReshots, planDefeatedMissiles, planUnseenHolds, planNoWaits, planNoEscapeShots;
        float noEscapePendingUntil = -1f;
        const float noEscapeThresholdShare = 0.75f;

        /// <summary>
        /// Inside the missile's no-escape zone (the target can't outrun it by turning away now) a shot a little under the usual
        /// threshold is taken: only countermeasures can still beat it. Keeps the last missile for a full-value shot.
        /// </summary>
        bool NoEscapeShotWanted(MissileFire weaponManager, Vessel target, MissileBase missile, in ShotModel.Features features)
        {
            if (!PlanActive(weaponManager) || !NewAwareness || !features.valid || !features.escape.reached) return false;
            if (ShotEstimate < noEscapeThresholdShare * ShotThreshold) return false;
            if (weaponManager.GetMissilesAway(weaponManager.currentTarget).numAway > 0) return false;
            int spare = 0;
            foreach (var other in VesselModuleRegistry.GetModules<MissileBase>(vessel))
                if (other != null && !other.HasFired && other.TargetingMode == missile.TargetingMode) spare += RoundsAboard(other);
            return spare >= 2;
        }

        /// <summary>Whether the target is turned away from us (its nose more than the given angle off us): defending, not shooting.</summary>
        bool TurnedAway(Vessel v, float angle)
        {
            Vector3 toUs = vessel.CoM - v.CoM;
            return Vector3.Dot(v.srf_vel_direction, toUs) < Mathf.Cos(angle * Mathf.Deg2Rad) * toUs.magnitude;
        }

        /// <summary>
        /// A launch below the normal threshold to put the opponent on the defensive before it can shoot. A missile truck (flight orders
        /// huntHighValue) lobs them from further out and without waiting for the opponent's shot: its rounds are there to be spent at range.
        /// </summary>
        bool ForcingShotWanted(MissileFire weaponManager, Vessel target, MissileBase missile, in ShotModel.Features features)
        {
            if (!PlanActive(weaponManager) || !PictureOf(target)) return false;
            bool truck = orders != null && orders.huntHighValue;
            if (ShotEstimate >= ShotThreshold) return false; // A good shot held for a better one isn't a forcing shot.
            if (ShotEstimate < (truck ? truckForcingFloor : forcingFloor) || !features.valid || !features.straight.reached) return false; // It has to get there if the target does nothing.
            if (!picture.detectedByTarget && !truck) return false; // Unseen: don't give ourselves away for a poor shot.
            if (picture.theirLong + picture.theirShort == 0 && !truck) return false; // Nothing to force: press instead.
            if (TurnedAway(target, 60f)) return false; // Already not pointing at us.
            if (Time.time - lastForcingTime < forcingInterval) return false;
            if (weaponManager.GetMissilesAway(weaponManager.currentTarget).numAway > 0) return false;
            int spare = 0;
            foreach (var other in VesselModuleRegistry.GetModules<MissileBase>(vessel))
                if (other != null && !other.HasFired && other.TargetingMode == missile.TargetingMode) spare += RoundsAboard(other);
            if (spare < 2) return false; // The last one is kept for a real shot.
            return truck || picture.theirShotLater >= forcingPressure || picture.theirShot >= ShotEstimate; // It's about to have a shot, or already has the better one.
        }
        const float truckForcingFloor = 0.05f;

        /// <summary>While the target's radar hasn't found us, a shot is held for a smaller gain than usual: there's no threat to hurry us.</summary>
        bool UnseenByTarget(MissileFire weaponManager, Vessel target) => PlanActive(weaponManager) && PictureOf(target) && !picture.detectedByTarget && !weaponManager.missileIsIncoming;

        /// <summary>
        /// The target would have a fair shot at us once it turns its nose onto us a few seconds from now: no time to wait for a better
        /// shot of our own (its shot from where its nose points now is what the patience rule already checks).
        /// </summary>
        bool OpponentShotSoon(MissileFire weaponManager, Vessel target)
        {
            if (!PlanActive(weaponManager) || !PictureOf(target) || Mathf.Max(picture.theirShot, picture.theirShotLater) < shotWaitOpponentLimit) return false;
            ++planNoWaits;
            return true;
        }

        void NoteLaunch(MissileBase missile, Vessel target)
        {
            string kind = ShotKind(target);
            if (kind == "forcing") { ++planForcingShots; lastForcingTime = Time.time; lastForcingTarget = target; forcingPendingUntil = -1f; }
            else if (kind == "noEscape") { ++planNoEscapeShots; noEscapePendingUntil = -1f; }
            else if (kind == "free") { ++matchupFreeShots; freeShotPendingUntil = -1f; }
            else if (kind == "reshot") ++planReshots;
            else if (kind == "followUp") ++planFollowUps;
            if (steeringMetricsEnabled) Debug.Log($"[BDArmory.BDModulePilotAI]: Planner {vessel.vesselName}: {kind} shot at {(target != null ? target.vesselName : "nothing")}, estimate {ShotEstimate:F2} (threshold {ShotThreshold:F2}), {(target != null ? (target.CoM - vessel.CoM).magnitude : 0f):F0}m, {missile.TimeIndex:F1}s after launch");
        }

        string ShotKind(Vessel target)
        {
            if (Time.time < forcingPendingUntil) return "forcing";
            if (Time.time < noEscapePendingUntil) return "noEscape";
            if (Time.time < freeShotPendingUntil) return "free";
            if (Time.time - lastDefeatedTime < 10f) return "reshot"; // A missile was written off just before.
            if (target == lastForcingTarget && Time.time - lastForcingTime < followUpWindow) return "followUp";
            return "normal";
        }

        /// <summary>Extra fields for a missile's shot record: the pilot's decision behind it.</summary>
        public string ShotRecordContext()
        {
            var target = WeaponManager != null && WeaponManager.currentTarget != null ? WeaponManager.currentTarget.Vessel : null;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, " decision={0:F2} threshold={1:F2} shotKind={2} planner={3}",
                ShotEstimate, ShotThreshold, engagementPlanner && target != null ? ShotKind(target) : "normal", engagementPlanner);
        }
        #endregion

        #region Geometry
        enum PlanMode { None, Abort, Press, Cutoff }
        PlanMode planMode;
        float planModeSince, abortStartDistance;
        Vessel planModeVessel;
        Vector3 abortFrom;
        readonly List<float> abortTimes = new List<float>();
        int planAborts, planRecommits, planPressTicks, planCutoffTicks, planAbortTicks, planSupportThroughTicks, planLinkSupports, planDoubleNotchTicks;
        const float abortShotFloor = 0.3f, abortRecommitShot = 0.2f, abortMinTime = 6f, abortMaxTime = 25f, abortBudgetWindow = 180f;
        const int abortBudget = 3;

        void SetPlanMode(PlanMode mode, Vessel v)
        {
            if (mode == planMode && v == planModeVessel) return;
            if (planMode == PlanMode.Abort && mode != PlanMode.Abort) ++planRecommits;
            planMode = mode; planModeVessel = v; planModeSince = Time.time;
        }

        /// <summary>
        /// The planner's geometry for flying at the target, ahead of cranking and the offset approach. Returns the tactic's name and sets
        /// the fly-to position, or null to leave it to the rest.
        /// </summary>
        string PlannedGeometry(MissileFire weaponManager, Vessel v, Vector3 vectorToTarget, float distanceToTarget, bool settingUpLaunch, ref Vector3 target)
        {
            if (!PlanActive(weaponManager) || !PictureOf(v)) { SetPlanMode(PlanMode.None, null); return null; }
            CountMatchupTicks(v);
            float bvrRange = Mathf.Max(4000f, 2f * GunEmploymentRange(weaponManager));
            bool theyCanShoot = picture.theirLong + picture.theirShort > 0;
            float threshold = ShotThresholdFor(picture.ourLong + picture.ourShort);

            // Abort: don't fly into the opponent's no-escape zone without a better shot of our own; dive away cold and come back once clear.
            bool aborting = planMode == PlanMode.Abort && planModeVessel == v;
            if (aborting)
            {
                float held = Time.time - planModeSince;
                bool clear = (!picture.theirInescapable && picture.theirShot < abortRecommitShot) || TurnedAway(v, 90f) || distanceToTarget > 1.5f * abortStartDistance;
                if (held > abortMaxTime || (held > abortMinTime && clear) || !theyCanShoot) aborting = false;
            }
            else if (theyCanShoot && distanceToTarget > bvrRange && !TurnedAway(v, 45f)
                && picture.theirInescapableLater && !picture.theirInescapable // Still time to turn away; once inside, turning our back only helps its shot.
                && picture.theirShotLater >= (picture.health < 0.6f ? 0.67f : 1f) * abortShotFloor
                && picture.ourShot < threshold && !(picture.ourMissileTimeToGo >= 0f && picture.ourMissileTimeToGo < 8f)
                && PlanAgainst(v) != FightPlan.Compress // Out-reached: turning away only gives its long shots more time.
                && AbortBudgetLeft())
            {
                aborting = true;
                abortTimes.Add(Time.time);
                abortStartDistance = distanceToTarget;
                ++planAborts;
                if (steeringMetricsEnabled) Debug.Log($"[BDArmory.BDModulePilotAI]: Planner {vessel.vesselName}: abort from {v.vesselName} at {distanceToTarget:F0}m, its shot {picture.theirShot:F2} now / {picture.theirShotLater:F2} in {picture.lead:F0}s (no escape {picture.theirInescapable}/{picture.theirInescapableLater}), ours {picture.ourShot:F2}");
            }
            if (aborting)
            {
                SetPlanMode(PlanMode.Abort, v);
                abortFrom = v.CoM;
                target = ColdPoint(abortFrom);
                AdjustThrottle(maxSpeed, false, true);
                turningTimer = 0;
                ++planAbortTicks;
                NoteTactic("Abort");
                return "Abort";
            }

            // Press: the target is defending (or has nothing left to shoot): close on it for the killing shot instead of cranking or pumping.
            bool defending = TurnedAway(v, planMode == PlanMode.Press && planModeVessel == v ? 50f : 70f);
            bool recentlyShot = picture.ourMissilesInFlight > 0 || Time.time - lastForcingTime < followUpWindow || SectionGrinder(v); // Ours or the wingman's.
            bool press = picture.ourLong + picture.ourShort > 0 && distanceToTarget > bvrRange
                && ((defending && recentlyShot && (picture.theirShot < forcingPressure || TurnedAway(v, 90f))) || !theyCanShoot)
                && picture.otherThreatShot < forcingPressure;
            if (press)
            {
                SetPlanMode(PlanMode.Press, v);
                ++planPressTicks;
                NoteTactic("Press");
                if (settingUpLaunch && picture.ourShot >= 0.8f * threshold) return null; // A shot is coming: nose on it.
                target = CutoffPoint(v, target);
                turningTimer = 0;
                return "Press";
            }

            if (HoldRange(v, distanceToTarget, settingUpLaunch, threshold, ref target)) { SetPlanMode(PlanMode.None, null); return "Hold range"; }
            if (SpikeCrank(weaponManager, v, vectorToTarget, distanceToTarget, threshold, ref target)) { SetPlanMode(PlanMode.None, null); return "Spike crank"; }
            if (CompressTo(v, distanceToTarget, settingUpLaunch, threshold, ref target)) { SetPlanMode(PlanMode.None, null); return "Compress"; }

            if (SectionBracket(weaponManager, v, vectorToTarget, distanceToTarget, ref target)) { SetPlanMode(PlanMode.None, null); return "Bracket"; }

            // Cut-off: a crossing target far away is met on a collision course rather than chased.
            if (distanceToTarget > 8000f && picture.ourShot < forcingFloor && picture.ourMissilesInFlight == 0 && !weaponManager.missileIsIncoming)
            {
                float aspect = VectorUtils.Angle(v.srf_vel_direction, -vectorToTarget);
                if (aspect > 45f && aspect < 135f)
                {
                    SetPlanMode(PlanMode.Cutoff, v);
                    ++planCutoffTicks;
                    NoteTactic("Cut-off");
                    target = CutoffPoint(v, target);
                    return "Cut-off";
                }
            }
            SetPlanMode(PlanMode.None, null);
            return null;
        }

        bool AbortBudgetLeft()
        {
            abortTimes.RemoveAll(t => Time.time - t > abortBudgetWindow);
            return abortTimes.Count < abortBudget;
        }

        /// <summary>Away from a position level or diving into thicker air (which bleeds a chasing missile), keeping well above the minimum altitude.</summary>
        Vector3 ColdPoint(Vector3 from)
        {
            Vector3 away = (vessel.CoM - from).ProjectOnPlanePreNormalized(upDirection);
            if (away.sqrMagnitude < 1f) away = vessel.srf_vel_direction.ProjectOnPlanePreNormalized(upDirection);
            away.Normalize();
            if (NewAwareness && TerrainMask(from, away, out Vector3 masked)) { ++maskTicks; return vessel.CoM + 3000f * masked; } // Behind a ridge if there is one.
            float floor = Mathf.Max(minAltitude + 1000f, 0.5f * defaultAltitude);
            float descent = Mathf.Clamp((float)vessel.radarAltitude - floor, 0f, 800f); // At most about 15° down.
            return vessel.CoM + 3000f * away - descent * upDirection;
        }

        /// <summary>
        /// The point to fly at to meet the target on a collision course at our speed, turned back towards the line of sight as far as
        /// needed to keep it within the radar's view, at the height the original fly-to position asked for.
        /// </summary>
        Vector3 CutoffPoint(Vessel v, Vector3 original)
        {
            Vector3 rel = v.CoM - vessel.CoM, tv = v.Velocity();
            float s = Mathf.Max((float)vessel.srfSpeed, 50f);
            float a = tv.sqrMagnitude - s * s, b = 2f * Vector3.Dot(rel, tv), c = rel.sqrMagnitude;
            float t = -1f;
            if (Mathf.Abs(a) < 1e-3f) { if (b < 0f) t = -c / b; }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float root = Mathf.Sqrt(disc);
                    float t1 = (-b - root) / (2f * a), t2 = (-b + root) / (2f * a);
                    t = t1 > 0f && (t2 <= 0f || t1 < t2) ? t1 : t2;
                }
            }
            if (t <= 0f) return original; // Can't catch it on any heading: chase it.
            Vector3 aim = (rel + Mathf.Min(t, 60f) * tv).ProjectOnPlanePreNormalized(upDirection);
            Vector3 line = rel.ProjectOnPlanePreNormalized(upDirection);
            Vector3 direction = Vector3.RotateTowards(line.normalized, aim.normalized, CrankAngle(WeaponManager) * Mathf.Deg2Rad, 0f);
            float height = Vector3.Dot(original - vessel.CoM, upDirection);
            return vessel.CoM + line.magnitude * direction + height * upDirection;
        }

        /// <summary>
        /// Keep guiding our missile instead of defending from the incoming one, when ours arrives first and the incoming one can't yet guide
        /// itself (it needs its launcher, which ours is about to kill), with time to defend afterwards.
        /// </summary>
        bool SupportThrough(MissileFire weaponManager)
        {
            if (!PlanActive(weaponManager) || targetVessel == null || !PictureOf(targetVessel)) return false;
            if (picture.ourMissileTimeToGo < 0f || !picture.ourMissileNeedsSupport) return false;
            float ourArrival = picture.ourMissileTimeToGo - (Time.time - picture.time);
            if (weaponManager.incomingMissileTime < Mathf.Max(6f, ourArrival + 3f)) return false;
            var incoming = weaponManager.incomingMissileVessel != null ? VesselModuleRegistry.GetMissileBase(weaponManager.incomingMissileVessel) : null;
            if (incoming == null || incoming.SourceVessel != targetVessel) return false; // Only the target dies with our hit.
            if (incoming.TargetingMode != MissileBase.TargetingModes.Radar || SeekerHeardActive(weaponManager, incoming)) return false;
            float distance = (weaponManager.incomingMissileVessel.CoM - vessel.CoM).magnitude;
            float closing = Mathf.Max(Vector3.Dot(weaponManager.incomingMissileVessel.Velocity() - vessel.Velocity(), (vessel.CoM - weaponManager.incomingMissileVessel.CoM) / Mathf.Max(distance, 1f)), 50f);
            float timeToActive = (distance - incoming.activeRadarRange) / closing; // Semi-active (no seeker range) only when it arrives.
            if (ourArrival + 2f >= timeToActive) return false;
            ++planSupportThroughTicks;
            NoteTactic("Support through");
            return true;
        }
        #endregion

        #region Merge and fight style
        const string noMergeReason = "no merge";
        readonly List<float> noMergeTimes = new List<float>();
        float lastNoMergeTime = -100f;
        int planNoMerges, planAnglesTicks, planEnergyTicks;
        const int noMergeBudget = 3;
        const float noMergeBudgetWindow = 240f, noMergeCooldown = 30f;

        bool ExtendingForNoMerge => IsExtending && extendingReason == noMergeReason;

        /// <summary>
        /// Turn away from a close-in fight we'd lose — out-turned, out-gunned, badly damaged or with another bandit pointing at us — while we
        /// have missiles for range, aren't the slower craft and could outrun its shot. Requests the extend and returns true while it lasts.
        /// </summary>
        bool AvoidMerge(Vessel v, float distance)
        {
            if (ExtendingForNoMerge) return true;
            var weaponManager = WeaponManager;
            if (!PlanActive(weaponManager) || !canExtend || !PictureOf(v) || picture.ourLong == 0) return false;
            var plan = PlanAgainst(v);
            if (plan == FightPlan.Compress) return false; // The merge is where we're stronger.
            if (distance < 2500f || distance > 9000f || TurnedAway(v, 60f)) return false; // Not coming to fight.
            if (Vector3.Dot(vessel.Velocity() - v.Velocity(), v.CoM - vessel.CoM) < 50f * distance) return false; // Not closing.
            bool outTurned = ownTrack.have && targetTrack.vessel == v && targetTrack.maxNormal > 1.15f * ownTrack.maxNormal;
            bool outGunned = picture.theirShort > 0 && picture.ourShort == 0;
            if (!(outTurned || outGunned || picture.health < 0.6f || picture.otherThreats > 0 || plan == FightPlan.Standoff)) return false;
            if (EnergyAdvantage(v) > 1500f || vessel.srfSpeed < 0.9f * v.srfSpeed || picture.theirInescapable) return false; // We'd control it, or couldn't get away.
            noMergeTimes.RemoveAll(t => Time.time - t > noMergeBudgetWindow);
            if (noMergeTimes.Count >= noMergeBudget || Time.time - lastNoMergeTime < noMergeCooldown) return false;
            noMergeTimes.Add(Time.time);
            lastNoMergeTime = Time.time;
            ++planNoMerges;
            if (steeringMetricsEnabled) Debug.Log($"[BDArmory.BDModulePilotAI]: Planner {vessel.vesselName}: no merge with {v.vesselName} at {distance:F0}m (out-turned {outTurned}: {targetTrack.maxNormal:F0} vs {ownTrack.maxNormal:F0} m/s², out-gunned {outGunned}, health {picture.health:F2}, other threats {picture.otherThreats})");
            RequestExtend(noMergeReason, v, Mathf.Max(8000f, distance + 4000f), ignoreCooldown: true);
            return true;
        }

        const string disengageReason = "disengage";
        const float disengageHealth = 0.8f, disengageHealthGap = 0.2f, disengageCooldown = 20f; // 0.8: about two thirds of control left (AirframeIntegrity).
        float lastDisengageTime = -100f;
        int planDisengages;
        bool ExtendingToDisengage => IsExtending && extendingReason == disengageReason;

        /// <summary>
        /// Leave a fight our damage has already lost: badly damaged (missing panels, surfaces, thrust) against a clearly healthier opponent,
        /// with no killing shot of our own, at a moment we can get away — its nose off us, or far enough out — and not inside its
        /// inescapable shot. Requests the extend and returns true while it lasts.
        /// </summary>
        bool Disengage(Vessel v, float distance)
        {
            if (ExtendingToDisengage) return true;
            var weaponManager = WeaponManager;
            if (!PlanActive(weaponManager) || !canExtend || !PictureOf(v) || Time.time - lastDisengageTime < disengageCooldown) return false;
            float theirHealth = GameModes.AirframeIntegrity.Of(v);
            if (picture.health >= disengageHealth || theirHealth - picture.health < disengageHealthGap) return false;
            if (picture.ourShot >= ShotThresholdFor(0) || picture.theirInescapable) return false; // We have the kill, or couldn't get away.
            if (distance < 1200f || !(TurnedAway(v, 45f) || distance > 4000f)) return false; // Defend until it overshoots or we have room.
            lastDisengageTime = Time.time;
            ++planDisengages;
            if (steeringMetricsEnabled) Debug.Log($"[BDArmory.BDModulePilotAI]: Planner {vessel.vesselName}: disengaging from {v.vesselName} at {distance:F0}m (health {picture.health:F2} vs {theirHealth:F2}, our shot {picture.ourShot:F2})");
            RequestExtend(disengageReason, v, Mathf.Max(10000f, distance + 6000f), ignoreCooldown: true);
            return true;
        }

        /// <summary>Stay out of the close-in fight: no merge we'd lose, disengaging from one our damage has lost, or bugging out of a scissors we're losing.</summary>
        bool StandOff(Vessel v, float distance) => AvoidMerge(v, distance) || Disengage(v, distance) || Bugout(v, distance);

        /// <summary>Weight on energy in the close-in planner: more against an opponent that turns better than us, less against one that turns worse.</summary>
        float FightStyleEnergyFactor(Vessel opponent)
        {
            if (!PlanActive(WeaponManager)) return 1f;
            float ratio; // Theirs over ours: measured once both have been seen turning, else the matchup's estimate.
            if (ownTrack.have && targetTrack.vessel == opponent) ratio = targetTrack.maxNormal / Mathf.Max(ownTrack.maxNormal, 1f);
            else if (MatchupActive(WeaponManager) && matchup.opponent == opponent) ratio = 1f / Mathf.Max(matchup.turnRatio, 1e-3f);
            else return 1f;
            if (ratio > 1.1f) { ++planEnergyTicks; return 2.5f; }
            if (ratio < 0.9f) { ++planAnglesTicks; return 0.5f; }
            return 1f;
        }

        /// <summary>Side to offset or crank to: away from the nearest other enemy pointing at us, if there is one (0 otherwise).</summary>
        float SideAwayFromOtherThreats(Vector3 toTarget)
        {
            if (!PlanActive(WeaponManager) || targetVessel == null || !PictureOf(targetVessel) || picture.otherThreats == 0) return 0f;
            Vector3 toThreat = (picture.otherThreatPosition - vessel.CoM).ProjectOnPlanePreNormalized(upDirection);
            return Vector3.Dot(Vector3.Cross(toTarget, toThreat), upDirection) >= 0 ? -1f : 1f;
        }
        #endregion

        string SalvoMetrics() => WeaponManager != null ? WeaponManager.SalvoMetrics() : "";

        string PlannerMetrics() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "planner={0} plan=forcing {1}/followUp {2}/reshot {3}/writtenOff {4}/unseenHolds {5}/noWait {17} aborts {6}/recommits {7}/abort {8:F1}s press {9:F1}s cutoff {10:F1}s supportThrough {11:F1}s noMerge {12} disengage {18} style angles {13:F1}s/energy {14:F1}s planCost={15:F0}us/picture health={16:F2} linkSupports={19} noEscapeShots={20} doubleNotch={21:F1}s launchLoft={22:F1}s launchClimb={23:F1}s drag={24:F1}s mask={25:F1}s zones={26:F0}/{27:F0}/{28:F0} allCraft: jammerHeldOff={29} homeOnJam={30} inertialCoasts={31} salvoRoles={32} spikes={33} launchWarnings={34} quietRadarPingsMissed={35} rwrCued={36} | spikeCrank={37:F1}s unseenThreats={38} rwrLocks={39}",
            engagementPlanner, planForcingShots, planFollowUps, planReshots, planDefeatedMissiles, planUnseenHolds, planAborts, planRecommits, planAbortTicks * Time.fixedDeltaTime,
            planPressTicks * Time.fixedDeltaTime, planCutoffTicks * Time.fixedDeltaTime, planSupportThroughTicks * Time.fixedDeltaTime, planNoMerges,
            planAnglesTicks * bfmReplanInterval, planEnergyTicks * bfmReplanInterval,
            plannerCycles > 0 ? plannerTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / plannerCycles : 0, health, planNoWaits, planDisengages, planLinkSupports,
            planNoEscapeShots, planDoubleNotchTicks * Time.fixedDeltaTime, launchLoftTicks * Time.fixedDeltaTime, energyClimbTicks * Time.fixedDeltaTime, dragTicks * Time.fixedDeltaTime, maskTicks * Time.fixedDeltaTime,
            picture.ourMaxRange, picture.ourNoEscapeRange, picture.theirNoEscapeRange,
            BDAStealth.JamPatches.JammerHeldOff, MissileBase.homeOnJamCount, MissileBase.inertialCoastCount, MissileBase.salvoCoordinatedCount,
            RadarWarningReceiver.Spikes, RadarWarningReceiver.LaunchWarnings, RadarWarningReceiver.QuietRadarPingsMissed, RadarWarningReceiver.CuedReports,
            spikeCrankTicks * Time.fixedDeltaTime, rwrUnseenThreats, WeaponManager != null && WeaponManager.rwr != null ? WeaponManager.rwr.LockCount() : 0);
    }
}
