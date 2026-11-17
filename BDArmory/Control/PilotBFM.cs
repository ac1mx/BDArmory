using System;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Settings;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Manoeuvre planner for close-in fights (with Advanced Tactics). Several times a second it flies a set of candidate manoeuvres
    /// forward for a few seconds — a lift direction around the flight path held in a fixed plane, a share of the available lift and full
    /// or idle power — with a point-mass model of this craft (lift per unit of dynamic pressure measured at full stick, and a speed change
    /// model fitted while flying) against two predictions of the opponent (carrying on with its current turn, or turning onto us), and
    /// flies the one whose path scores best for position (who is pointing at whose tail, at what range), weapon windows, energy and
    /// safety (ground, speed, collision). Lead, lag and pure pursuit, yo-yos, reversals, overshoot avoidance and defensive breaks come out
    /// of the scoring rather than being chosen by rules. When a gun or missile can be brought to bear now, the normal aiming takes over.
    /// </summary>
    public partial class BDModulePilotAI
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Manoeuvre Planner", // Close-in fights flown by the planner (needs Advanced Tactics).
            groupName = "pilotAI_EvadeExtend", groupDisplayName = "#LOC_BDArmory_AI_EvadeExtend", groupStartCollapsed = true),
            UI_Toggle(enabledText = "#LOC_BDArmory_Enabled", disabledText = "#LOC_BDArmory_Disabled", scene = UI_Scene.All),]
        public bool bfmPlanner = true;

        const float bfmRange = 5000f; // m: opponents closer than this are fought with the planner.
        const float bfmHorizon = 4f, bfmStep = 0.2f, bfmReplanInterval = 0.25f;
        const int bfmSteps = (int)(bfmHorizon / bfmStep);
        int bfmStepsNow = bfmSteps, bfmHypotheses = 2; // Steps flown forward and opponent predictions scored in this plan (pilot skill).
        const int bfmDirections = 12;
        const float bfmSlowPenalty = 0.6f, bfmSeparationCost = 0.35f, bfmPullReward = 0.5f;
        static readonly float bfmPullCos = Mathf.Cos(45f * Mathf.Deg2Rad); // The pull towards a gun shot is rewarded from 45° off.
        static readonly float[] bfmLoads = { 1f, 0.5f, 0.12f };

        #region Performance models
        // Speed change along the flight path: thrust per unit mass minus drag, drag = d1·q + d2·a_n²/(q + 1) (q in kPa, accelerations in
        // m/s²). Thrust is read from the engines (full-power thrust tracked while at full throttle), drag is fitted by recursive least squares
        // as thrust minus the measured acceleration, starting from typical fighter values.
        readonly float[] dragCoefficients = { 0.05f, 0.25f };
        readonly float[,] dragCovariance = { { 0.001f, 0f }, { 0f, 0.01f } };
        const float energyForgetting = 0.998f;
        const float idleThrustShare = 0.05f; // Of full-power thrust at idle (spool-down within the horizon is ignored).
        float fullThrustAccel = 12f; // m/s²
        bool fullThrustMeasured;
        Vector3 bfmLastVelocity, bfmSmoothedForce;
        bool bfmHaveLastVelocity;
        float observedRollRate = 2f; // rad/s, recent best.

        class OpponentTrack
        {
            public Vessel vessel;
            public Vector3 lastVelocity, force; // Smoothed specific force (gravity removed), world frame.
            public bool have;
            public float maxNormal = 40f; // Recent best turning acceleration (m/s²).
            public float liftPerQ; // Recent best turning acceleration per kPa of dynamic pressure (m/s² per kPa), 0 until seen above 3 kPa.
        }
        readonly OpponentTrack targetTrack = new OpponentTrack(), threatTrack = new OpponentTrack();
        readonly OpponentTrack ownTrack = new OpponentTrack(); // This craft, measured the same way, so that turn performance compares like with like.

        static void UpdateTrack(OpponentTrack track, Vessel v, Vector3 up, float gravity, float dt)
        {
            if (v != track.vessel) { track.vessel = v; track.have = false; track.maxNormal = 40f; track.liftPerQ = 0f; }
            if (v == null || v.LandedOrSplashed) { track.have = false; return; }
            Vector3 velocity = v.Velocity();
            if (track.have)
            {
                Vector3 force = (velocity - track.lastVelocity) / dt + gravity * up;
                if (force.sqrMagnitude < 4e4f) track.force = Vector3.Lerp(track.force, force, dt / 0.3f);
                float speed = velocity.magnitude;
                if (speed > 1f)
                {
                    Vector3 normal = track.force - Vector3.Dot(track.force, velocity / speed) * (velocity / speed);
                    track.maxNormal = Mathf.Max(track.maxNormal * (1f - dt / 20f), normal.magnitude, 30f);
                    float q = (float)v.dynamicPressurekPa;
                    if (q > 3f) track.liftPerQ = Mathf.Max(track.liftPerQ * (1f - dt / 30f), normal.magnitude / q);
                }
            }
            track.lastVelocity = velocity;
            track.have = true;
        }

        /// <summary>Learn this craft's speed change and roll rate, and follow the opponents' turns (every physics tick, with tactics on).</summary>
        void UpdateBFMModels()
        {
            float dt = Time.fixedDeltaTime;
            Vector3 velocity = vessel.Velocity();
            float speed = velocity.magnitude;
            if (bfmHaveLastVelocity && speed > 30f && !vessel.LandedOrSplashed)
            {
                Vector3 force = (velocity - bfmLastVelocity) / dt + bodyGravity * upDirection;
                if (force.sqrMagnitude < 4e4f) bfmSmoothedForce = Vector3.Lerp(bfmSmoothedForce, force, dt / 0.2f);
                Vector3 along = velocity / speed;
                float alongForce = Vector3.Dot(bfmSmoothedForce, along);
                float normalForce = (bfmSmoothedForce - alongForce * along).magnitude;
                float q = (float)vessel.dynamicPressurekPa;
                float mass = Mathf.Max((float)vessel.totalMass, 0.1f);
                float thrustAccel = RecordedThrust() / mass * Mathf.Max(0f, Vector3.Dot(vesselTransform.up, along)); // Along the flight path.
                float throttle = vessel.ctrlState.mainThrottle;
                bool lowAoA = Mathf.Abs(Vector3.Dot(along, vesselTransform.forward)) < 0.26f; // Within 15°.
                if (throttle > 0.95f && (lowAoA || !NewDogfight))
                {
                    fullThrustAccel = fullThrustMeasured ? Mathf.Lerp(fullThrustAccel, thrustAccel, dt / 2f) : thrustAccel;
                    fullThrustMeasured = true;
                }
                // Clean flight only: airbrakes, gear and post-stall flight add drag the model has no term for, and were what drove
                // its drag coefficient to the clamp (planner then sure full power can't hold speed, so it unloads at idle).
                bool clean = !NewGunnery || !vessel.ActionGroups[KSPActionGroup.Brakes] && !vessel.ActionGroups[KSPActionGroup.Gear]
                    && Mathf.Abs(Vector3.Dot(along, vesselTransform.forward)) < Mathf.Sin(Mathf.Clamp(stallAoA > 0f ? stallAoA : 20f, 10f, 35f) * Mathf.Deg2Rad);
                if (q > 2f && clean) UpdateDragModel(q, normalForce * normalForce / (q + 1f), thrustAccel - alongForce);
            }
            bfmLastVelocity = velocity;
            bfmHaveLastVelocity = true;
            observedRollRate = Mathf.Max(observedRollRate * (1f - dt / 30f), Mathf.Abs(vessel.angularVelocity.y), 0.8f);
            UpdateTrack(targetTrack, targetVessel, upDirection, bodyGravity, dt);
            UpdateTrack(ownTrack, vessel, upDirection, bodyGravity, dt);
            UpdateTrack(threatTrack, BFMThreat(WeaponManager), upDirection, bodyGravity, dt);
        }

        /// <summary>The craft to plan against besides the opponent: whatever has fired at us, else a bandit on our tail or pointing at us (1v2).</summary>
        Vessel BFMThreat(MissileFire weaponManager)
        {
            if (weaponManager == null) return null;
            if (weaponManager.incomingThreatVessel != null) return weaponManager.incomingThreatVessel;
            if (!SectionActive(weaponManager)) return null;
            if (section.ourAttacker != null && section.ourAttacker != targetVessel) return section.ourAttacker;
            return section.secondThreat;
        }

        void UpdateDragModel(float x0, float x1, float drag)
        {
            float p0 = dragCovariance[0, 0] * x0 + dragCovariance[0, 1] * x1, p1 = dragCovariance[1, 0] * x0 + dragCovariance[1, 1] * x1;
            float denominator = energyForgetting + x0 * p0 + x1 * p1;
            if (denominator <= 1e-6f) return;
            float error = drag - (dragCoefficients[0] * x0 + dragCoefficients[1] * x1);
            if (Mathf.Abs(error) > 60f) return; // A collision or a glitch, not flight.
            dragCoefficients[0] += p0 / denominator * error;
            dragCoefficients[1] += p1 / denominator * error;
            float c00 = (dragCovariance[0, 0] - p0 * p0 / denominator) / energyForgetting, c01 = (dragCovariance[0, 1] - p0 * p1 / denominator) / energyForgetting;
            float c11 = (dragCovariance[1, 1] - p1 * p1 / denominator) / energyForgetting;
            dragCovariance[0, 0] = Mathf.Min(c00, 1f); dragCovariance[0, 1] = dragCovariance[1, 0] = c01; dragCovariance[1, 1] = Mathf.Min(c11, 10f);
            dragCoefficients[0] = NewGunnery ? Mathf.Clamp(dragCoefficients[0], 0.005f, 0.3f) : Mathf.Clamp(dragCoefficients[0], 0f, 1f); // Six times a typical fighter's at most.
            dragCoefficients[1] = Mathf.Clamp(dragCoefficients[1], 0f, 3f);
        }

        float AlongAcceleration(float throttle, float q, float normal, bool brakes) =>
            fullThrustAccel * Mathf.Lerp(idleThrustShare, 1f, throttle) - dragCoefficients[0] * q * (brakes ? 2f : 1f) - dragCoefficients[1] * normal * normal / (q + 1f);
        #endregion

        #region Planning
        struct BFMPlan
        {
            public bool valid;
            public Vector3 planeNormal; // World-frame normal of the turn plane (lift = normal × velocity direction).
            public float load;          // Share of the available lift.
            public bool fullPower;
            public float score;
            public string label;
        }
        BFMPlan bfmPlan;
        Vector3 bfmLift; // Lift direction being flown (world), following the plan's at the craft's roll rate; zero when not flying a plan.
        float bfmNextPlanTime;
        int bfmTicks, bfmReplans, bfmAimHandovers;
        readonly Vector3[] bfmTargetA = new Vector3[bfmSteps + 1], bfmTargetAVel = new Vector3[bfmSteps + 1];
        readonly Vector3[] bfmTargetB = new Vector3[bfmSteps + 1], bfmTargetBVel = new Vector3[bfmSteps + 1];
        readonly Vector3[] bfmThreatA = new Vector3[bfmSteps + 1], bfmThreatAVel = new Vector3[bfmSteps + 1];
        readonly Vector3[] bfmThreatB = new Vector3[bfmSteps + 1], bfmThreatBVel = new Vector3[bfmSteps + 1];

        /// <summary>Opponent paths: (A) its current turn held in its current plane, (B) turning onto where we are now heading at its best rate.</summary>
        void PredictOpponent(OpponentTrack track, Vector3 ownPosition, Vector3 ownVelocity, Vector3[] posA, Vector3[] velA, Vector3[] posB, Vector3[] velB)
        {
            Vessel v = track.vessel;
            Vector3 p = v.CoM, vel = v.Velocity();
            float speed = Mathf.Max(vel.magnitude, 1f);
            Vector3 dir = vel / speed;
            Vector3 force = track.have ? track.force : Vector3.zero;
            float along = Vector3.Dot(force, dir);
            Vector3 normal = force - along * dir;
            float normalMagnitude = normal.magnitude;
            Vector3 plane = normalMagnitude > 1f ? Vector3.Cross(dir, normal / normalMagnitude) : Vector3.zero;
            Vector3 pA = p, vA = vel, pB = p, vB = vel;
            posA[0] = posB[0] = p; velA[0] = velB[0] = vel;
            for (int k = 1; k <= bfmStepsNow; ++k)
            {
                float sA = Mathf.Max(vA.magnitude, 1f);
                Vector3 dA = vA / sA;
                Vector3 liftA = plane.sqrMagnitude > 0.5f ? Vector3.Cross(plane, dA).normalized * normalMagnitude : Vector3.zero;
                Vector3 aA = along * dA + liftA - bodyGravity * upDirection;
                vA += aA * bfmStep; pA += vA * bfmStep;
                posA[k] = pA; velA[k] = vA;

                float sB = Mathf.Max(vB.magnitude, 1f);
                Vector3 dB = vB / sB;
                Vector3 ownAt = ownPosition + ownVelocity * (k * bfmStep);
                Vector3 toUs = (ownAt - pB).ProjectOnPlanePreNormalized(dB);
                Vector3 liftB = toUs.sqrMagnitude > 1f ? toUs.normalized * track.maxNormal : Vector3.zero;
                Vector3 aB = along * dB + liftB - bodyGravity * upDirection + bodyGravity * upDirection.ProjectOnPlanePreNormalized(dB); // Lift also carries its weight.
                vB += aB * bfmStep; pB += vB * bfmStep;
                posB[k] = pB; velB[k] = vB;
            }
        }

        // Scoring context for one planning pass.
        float bfmGunRange, bfmGunCos, bfmOptimalRange, bfmRangeScale, bfmThreatGunRange = 1200f, bfmGroundHeight, bfmStartAltitude;
        Vector3 bfmStartPosition;

        /// <summary>
        /// Position score in [-1, 1] for us at (p, v) against an opponent at (pt, vt): our nose on it and us on its tail, minus the same
        /// the other way round; angles enter as (1 − cos)/2 so that no arc-cosines are needed.
        /// </summary>
        static float Advantage(Vector3 p, Vector3 v, Vector3 pt, Vector3 vt, out float range, out float ourCos, out float theirCos)
        {
            Vector3 line = pt - p;
            range = Mathf.Max(line.magnitude, 1f);
            line /= range;
            float vs = Mathf.Max(v.magnitude, 1f), vts = Mathf.Max(vt.magnitude, 1f);
            ourCos = Vector3.Dot(v, line) / vs;            // Our flight path onto it.
            float onItsTail = Vector3.Dot(vt, line) / vts;  // It's flying away from us: we're behind it.
            theirCos = -Vector3.Dot(vt, line) / vts;       // Its flight path onto us.
            float onOurTail = -Vector3.Dot(v, line) / vs;  // We're flying away from it.
            float offence = 0.5f * ((1f + ourCos) * 0.5f + (1f + onItsTail) * 0.5f);
            float defence = 0.5f * ((1f + theirCos) * 0.5f + (1f + onOurTail) * 0.5f);
            return offence - defence;
        }

        float StepScore(Vector3 p, Vector3 v, int k, bool haveThreat)
        {
            float score = 0f;
            for (int h = 0; h < bfmHypotheses; ++h)
            {
                Vector3 pt = h == 0 ? bfmTargetA[k] : bfmTargetB[k], vt = h == 0 ? bfmTargetAVel[k] : bfmTargetBVel[k];
                float advantage = Advantage(p, v, pt, vt, out float range, out float ourCos, out float theirCos);
                float rangeError = (range - bfmOptimalRange) / bfmRangeScale;
                float s = advantage * (0.4f + 0.6f * Mathf.Exp(-rangeError * rangeError));
                if (ourCos > bfmGunCos && range < bfmGunRange) s += 1f * (ourCos - bfmGunCos) / (1f - bfmGunCos);           // Our weapon window.
                else if (NewDogfight && ourCos > bfmPullCos && range < 1.5f * bfmGunRange) s += bfmPullReward * (ourCos - bfmPullCos) / (bfmGunCos - bfmPullCos); // Pulling the nose towards it.
                if (theirCos > 0.978f && range < bfmThreatGunRange) s -= 1.2f * (theirCos - 0.978f) / 0.022f;              // Its gun window (12°).
                if (range < 80f) s -= 2f;                                                                                    // Collision.
                if (NewDogfight && advantage > -0.25f) s -= bfmSeparationCost * Mathf.Clamp01((range - 1500f) / 2000f);       // Leaving a fight we aren't losing.
                score += s / bfmHypotheses;
            }
            if (haveThreat)
            {
                for (int h = 0; h < 2; ++h)
                {
                    Vector3 pt = h == 0 ? bfmThreatA[k] : bfmThreatB[k], vt = h == 0 ? bfmThreatAVel[k] : bfmThreatBVel[k];
                    float advantage = Advantage(p, v, pt, vt, out float range, out _, out float theirCos);
                    float s = Mathf.Min(advantage, 0f) * Mathf.Exp(-range / 3000f); // Only what it can do to us counts here.
                    if (theirCos > 0.978f && range < bfmThreatGunRange) s -= 1.2f * (theirCos - 0.978f) / 0.022f;
                    if (range < 80f) s -= 2f;
                    score += 0.5f * s;
                }
            }
            // Safety: height above the ground (flat ground at the highest terrain ahead), speed, ceiling.
            float altitude = bfmStartAltitude + Vector3.Dot(p - bfmStartPosition, upDirection);
            float height = altitude - bfmGroundHeight;
            float speed = v.magnitude;
            float sink = -Vector3.Dot(v, upDirection);
            score += HardDeckScore(height);
            if (height < minAltitude) score -= 3f + (minAltitude - height) / 100f;
            else if (sink > 0f)
            {
                // Height needed to level out from this dive at the lift available now.
                float lift = Mathf.Max(LiftCapability * liftMachFactor * 0.0005f * ShotModel.AirDensity(vessel.mainBody, altitude) * speed * speed, bodyGravity);
                float pullOut = speed * speed / lift * (1f - Mathf.Sqrt(Mathf.Max(0f, 1f - sink * sink / (speed * speed))));
                if (height - pullOut < minAltitude) score -= 2f * Mathf.Clamp01((minAltitude - (height - pullOut)) / 300f + 0.25f);
            }
            if (speed < 1.2f * minSpeed) score -= 1f;
            if (maxAltitudeEnabled && altitude > maxAltitude) score -= 1f;
            score += SectionStepScore(p, k * bfmStep);
            return score;
        }

        // Two-part plans: the first manoeuvre until bfmSwitchStep, then one of these. Flown as a receding horizon, only the first part is
        // ever flown, but the second lets the planner value a first move that sets up a reversal (scissors), a pull to the opponent's
        // predicted position, an extension or a throttle chop, none of which one fixed control over the horizon can show.
        enum BFMSecond { None, Reverse, Pursue, Extend, Chop }
        static readonly string[] bfmSecondNames = { "", "reverse", "pursue", "extend", "chop" };
        const int bfmSwitchStep = 8, bfmTopCandidates = 4; // Switch at 1.6 s.
        struct BFMCandidate { public Vector3 lift; public float load; public bool fullPower; public float score; }
        readonly BFMCandidate[] bfmTop = new BFMCandidate[bfmTopCandidates];
        readonly int[] bfmSecondPlans = new int[5];

        /// <summary>Fly one candidate forward and score its path.</summary>
        float ScoreCandidate(Vector3 lift0, float load, bool fullPower, Vector3 currentLift, float currentNormal, bool haveThreat, float energyWeight, Vessel opponent,
            BFMSecond second = BFMSecond.None, float fullLoad = 1f)
        {
            Vector3 p = vessel.CoM, v = vessel.Velocity();
            float speed0 = Mathf.Max(v.magnitude, 1f);
            Vector3 dir0 = v / speed0;
            Vector3 plane = Vector3.Cross(dir0, lift0);
            Vector3 currentPlane = currentLift.sqrMagnitude > 0.5f ? Vector3.Cross(dir0, currentLift) : plane;
            float rollAngle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(currentLift.sqrMagnitude > 0.5f ? currentLift : lift0, lift0), -1f, 1f));
            float rollTime = rollAngle / Mathf.Max(0.7f * observedRollRate, 0.5f) + 0.15f;
            float total = 0f, weights = 0f;
            float switchTime = bfmSwitchStep * bfmStep, secondRollTime = 0f;
            for (int k = 1; k <= bfmStepsNow; ++k)
            {
                float t = k * bfmStep;
                float speed = Mathf.Max(v.magnitude, 1f);
                Vector3 dir = v / speed;
                if (second != BFMSecond.None && k == bfmSwitchStep + 1)
                {
                    // The second manoeuvre, chosen from where the first one has left us.
                    Vector3 liftNow = Vector3.Cross(plane, dir).normalized;
                    Vector3 lift2 = liftNow;
                    switch (second)
                    {
                        case BFMSecond.Reverse: // Mirror the lift across the vertical: roll from one bank to the other.
                            Vector3 levelUp = upDirection.ProjectOnPlanePreNormalized(dir);
                            if (levelUp.sqrMagnitude < 0.01f) levelUp = liftNow;
                            Vector3 side = Vector3.Cross(dir, levelUp.normalized);
                            lift2 = liftNow - 2f * Vector3.Dot(liftNow, side) * side;
                            load = fullLoad;
                            break;
                        case BFMSecond.Pursue:
                            Vector3 toOpponent = (bfmTargetA[k] - p).ProjectOnPlanePreNormalized(dir);
                            if (toOpponent.sqrMagnitude > 1f) lift2 = toOpponent.normalized;
                            load = fullLoad; fullPower = true;
                            break;
                        case BFMSecond.Extend: load = bfmLoads[bfmLoads.Length - 1]; fullPower = true; break;
                        case BFMSecond.Chop: load = fullLoad; fullPower = false; break;
                    }
                    float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(liftNow, lift2.normalized), -1f, 1f));
                    secondRollTime = angle > 0.2f ? angle / Mathf.Max(0.7f * observedRollRate, 0.5f) + 0.15f : 0f;
                    plane = Vector3.Cross(dir, lift2.normalized);
                }
                float altitude = bfmStartAltitude + Vector3.Dot(p - bfmStartPosition, upDirection);
                float q = 0.0005f * ShotModel.AirDensity(vessel.mainBody, altitude) * speed * speed;
                float available = LiftCapability * liftMachFactor * q;
                float normal; Vector3 liftDir;
                if (t <= rollTime) { normal = Mathf.Min(currentNormal, available); liftDir = Vector3.Cross(currentPlane, dir).normalized; }
                else if (second != BFMSecond.None && t > switchTime && t - switchTime <= secondRollTime)
                { normal = 0.3f * available; liftDir = Vector3.Cross(plane, dir).normalized; } // Unloaded while rolling to the new plane.
                else { normal = load * available; liftDir = Vector3.Cross(plane, dir).normalized; }
                float along = AlongAcceleration(fullPower ? 1f : 0f, q, normal, !fullPower);
                Vector3 a = along * dir + normal * liftDir - bodyGravity * upDirection;
                v += a * bfmStep;
                p += v * bfmStep;
                float w = k == bfmStepsNow ? 2f + (float)k / bfmStepsNow : (float)k / bfmStepsNow;
                total += w * StepScore(p, v, k, haveThreat);
                weights += w;
            }
            float score = total / weights;
            if (NewGunnery)
            {
                // A fighter that runs out of speed has run out of options: below a fighting speed (the craft's corner speed, or most of its
                // best turn speed) every manoeuvre's end state counts the shortfall against it.
                float fightSpeed = !NewDogfight ? Mathf.Max(cornerSpeed, 0.6f * bestTurnSpeed, 1.5f * minSpeed)
                    : Mathf.Max(1.5f * minSpeed, (FlowActive && passFlow == FightFlow.OneCircle ? 0.45f : 0.6f) * Mathf.Min(cornerSpeed, bestTurnSpeed));
                float endSpeed = v.magnitude;
                if (endSpeed < fightSpeed) score -= bfmSlowPenalty * (fightSpeed - endSpeed) / fightSpeed;
            }
            if (energyWeight > 0 && opponent != null)
            {
                float ourEnergy = bfmStartAltitude + Vector3.Dot(p - bfmStartPosition, upDirection) + v.sqrMagnitude / (2f * bodyGravity);
                Vector3 pt = bfmTargetA[bfmStepsNow];
                float theirEnergy = (float)opponent.altitude + Vector3.Dot(pt - opponent.CoM, upDirection) + bfmTargetAVel[bfmStepsNow].sqrMagnitude / (2f * bodyGravity);
                score += energyWeight * Mathf.Clamp((ourEnergy - theirEnergy) / 3000f, -1f, 1f);
            }
            return score;
        }

        void Replan(Vessel opponent, Vessel threat)
        {
            ++bfmReplans;
            bfmStepsNow = Mathf.Clamp(Mathf.RoundToInt(PlanHorizon / bfmStep), bfmSwitchStep + 2, bfmSteps);
            bfmHypotheses = Knows(SkillFeature.FightChoice) ? 2 : 1; // Only the better pilots expect the opponent to turn onto them.
            var weaponManager = WeaponManager;
            bfmStartPosition = vessel.CoM;
            bfmStartAltitude = (float)vessel.altitude;
            Vector3 velocity = vessel.Velocity();
            float speed = Mathf.Max(velocity.magnitude, 1f);
            Vector3 dir = velocity / speed;
            // Highest ground under the next few kilometres of the flight path (sea level at least).
            bfmGroundHeight = 0f;
            var body = vessel.mainBody;
            for (int i = 0; i <= 3; ++i)
            {
                Vector3 point = vessel.CoM + i * 1000f * dir.ProjectOnPlanePreNormalized(upDirection).normalized;
                bfmGroundHeight = Mathf.Max(bfmGroundHeight, (float)body.TerrainAltitude(body.GetLatitude(point), body.GetLongitude(point)));
            }
            // Weapon geometry: a heat missile wants the target in its seeker at a couple of kilometres, the gun wants it close and on the nose.
            var missile = weaponManager != null ? weaponManager.CurrentMissile : null;
            if (missile != null)
            {
                bfmOptimalRange = missile.TargetingMode == MissileBase.TargetingModes.Heat ? 2500f : 3500f; bfmRangeScale = 2500f;
                bfmGunRange = Mathf.Min(missile.maxStaticLaunchRange, 6000f);
                bfmGunCos = Mathf.Cos(MissileWindow(missile) * Mathf.Deg2Rad);
            }
            else
            {
                bfmGunRange = weaponManager != null ? Mathf.Clamp(GunEmploymentRange(weaponManager), 300f, 1200f) : 1000f;
                bfmOptimalRange = 0.5f * bfmGunRange; bfmRangeScale = Mathf.Max(bfmGunRange, 800f);
                bfmGunCos = Mathf.Cos(12f * Mathf.Deg2Rad);
            }
            bfmThreatGunRange = 1200f;
            PredictOpponent(targetTrack, vessel.CoM, velocity, bfmTargetA, bfmTargetAVel, bfmTargetB, bfmTargetBVel);
            bool haveThreat = threat != null && threatTrack.vessel == threat;
            if (haveThreat) PredictOpponent(threatTrack, vessel.CoM, velocity, bfmThreatA, bfmThreatAVel, bfmThreatB, bfmThreatBVel);
            float distance = (opponent.CoM - vessel.CoM).magnitude;
            float energyWeight = Mathf.Lerp(0.1f, 0.3f, Mathf.InverseLerp(1000f, bfmRange, distance)) // Energy matters more with room to use it,
                * FightStyleEnergyFactor(opponent) // and against a better turner,
                * FlowEnergyFactor // and in the fight flow chosen at the pass,
                * EnergySpendFactor(opponent, distance); // and less once there is energy in hand to spend on getting behind it.

            // Where the lift points now, and how hard we're pulling.
            Vector3 currentLift = (-vesselTransform.forward).ProjectOnPlanePreNormalized(dir).normalized;
            float currentNormal = (bfmSmoothedForce - Vector3.Dot(bfmSmoothedForce, dir) * dir).magnitude;
            Vector3 e1 = upDirection.ProjectOnPlanePreNormalized(dir);
            if (e1.sqrMagnitude < 0.01f) e1 = currentLift;
            e1.Normalize();
            Vector3 e2 = Vector3.Cross(dir, e1);
            float pilotLimit = EnvelopeShare; // Less skilled pilots don't use the whole envelope.
            SectionBFMContext(opponent, ref energyWeight);
            NoteOvershootChance(opponent);
            for (int i = 0; i < bfmTopCandidates; ++i) bfmTop[i].score = float.MinValue;

            var best = new BFMPlan { score = float.MinValue };
            for (int i = 0; i < bfmDirections + 3; ++i)
            {
                Vector3 lift;
                string label;
                if (i < bfmDirections)
                {
                    float phi = i * 2f * Mathf.PI / bfmDirections;
                    lift = Mathf.Cos(phi) * e1 + Mathf.Sin(phi) * e2;
                    label = null;
                }
                else
                {
                    // Turn planes through the opponent: lead, pure and lag pursuit.
                    Vector3 aim = opponent.CoM + (i == bfmDirections ? 1.5f : i == bfmDirections + 1 ? 0f : -1.5f) * opponent.Velocity();
                    lift = (aim - vessel.CoM).ProjectOnPlanePreNormalized(dir);
                    if (lift.sqrMagnitude < 1f) continue;
                    lift.Normalize();
                    label = i == bfmDirections ? "lead turn" : i == bfmDirections + 1 ? "pure turn" : "lag turn";
                }
                for (int l = 0; l < bfmLoads.Length; ++l)
                {
                    if (label != null && l > 0) break; // Pursuit planes at full lift only.
                    for (int power = 0; power < 2; ++power)
                    {
                        bool fullPower = power == 0;
                        if (label != null && !fullPower) break;
                        float load = bfmLoads[l] * pilotLimit;
                        float score = ScoreCandidate(lift, load, fullPower, currentLift, currentNormal, haveThreat, energyWeight, opponent);
                        score += FlowBonus(lift) + OvershootBonus(lift, fullPower);
                        KeepTopCandidate(lift, load, fullPower, score);
                        score += HoldBonus(lift, load, fullPower, dir);
                        if (score > best.score)
                        {
                            best = new BFMPlan { valid = true, planeNormal = Vector3.Cross(dir, lift), load = load, fullPower = fullPower, score = score, label = label };
                        }
                    }
                }
            }
            // Second parts for the best few first moves.
            BFMSecond bestSecond = BFMSecond.None;
            for (int c = 0; c < bfmTopCandidates; ++c)
            {
                var candidate = bfmTop[c];
                if (candidate.score == float.MinValue) continue;
                for (var second = BFMSecond.Reverse; second <= BFMSecond.Chop; ++second)
                {
                    float score = ScoreCandidate(candidate.lift, candidate.load, candidate.fullPower, currentLift, currentNormal, haveThreat, energyWeight, opponent, second, pilotLimit)
                        + FlowBonus(candidate.lift) + OvershootBonus(candidate.lift, candidate.fullPower) + HoldBonus(candidate.lift, candidate.load, candidate.fullPower, dir);
                    if (score > best.score)
                    {
                        best = new BFMPlan { valid = true, planeNormal = Vector3.Cross(dir, candidate.lift), load = candidate.load, fullPower = candidate.fullPower, score = score, label = null };
                        bestSecond = second;
                    }
                }
            }
            if (best.valid && best.label == null) best.label = DescribePlan(best, dir, e1, opponent);
            if (best.valid && bestSecond != BFMSecond.None) { best.label += ", then " + bfmSecondNames[(int)bestSecond]; ++bfmSecondPlans[(int)bestSecond]; }
            bfmPlan = best;
            bfmScoreTrend = Mathf.Lerp(bfmScoreTrend, best.valid ? best.score : 0f, bfmReplanInterval / 3f);
        }

        /// <summary>Hold on to the current plan unless something is clearly better: above all its lift direction, as swapping between neighbouring directions only rolls the craft back and forth.</summary>
        float HoldBonus(Vector3 lift, float load, bool fullPower, Vector3 dir) =>
            bfmPlan.valid && Vector3.Dot(Vector3.Cross(bfmPlan.planeNormal, dir).normalized, lift) > 0.94f
                ? (Mathf.Abs(bfmPlan.load - load) < 0.01f && bfmPlan.fullPower == fullPower ? 0.06f : 0.04f) : 0f;

        void KeepTopCandidate(Vector3 lift, float load, bool fullPower, float score)
        {
            int worst = 0;
            for (int i = 1; i < bfmTopCandidates; ++i) if (bfmTop[i].score < bfmTop[worst].score) worst = i;
            if (score > bfmTop[worst].score) bfmTop[worst] = new BFMCandidate { lift = lift, load = load, fullPower = fullPower, score = score };
        }

        string DescribePlan(in BFMPlan plan, Vector3 dir, Vector3 levelUp, Vessel opponent)
        {
            Vector3 lift = Vector3.Cross(plan.planeNormal, dir).normalized;
            float up = Vector3.Dot(lift, levelUp);
            string power = plan.fullPower ? "" : ", idle";
            if (plan.load < 0.2f) return "unload" + power;
            Vector3 toOpponent = (opponent.CoM - vessel.CoM).ProjectOnPlanePreNormalized(dir).normalized;
            float towards = Vector3.Dot(lift, toOpponent);
            string turn = towards > 0.7f ? "turn in" : towards < -0.7f ? "turn away" : up > 0.7f ? "pull up" : up < -0.7f ? "pull down" : "out of plane";
            return (plan.load > 0.9f ? "hard " : "") + turn + power;
        }
        #endregion

        /// <summary>
        /// Fly the close-in fight with the planner. Returns false to leave the craft to the rest of the logic: no airborne opponent within
        /// range, the planner is off, or a weapon can be brought to bear now (the normal aiming does that best).
        /// </summary>
        bool FlyBFM(FlightCtrlState s)
        {
            bool flying = FlyBFMPlan(s);
            if (!flying) bfmLift = Vector3.zero;
            return flying;
        }

        bool FlyBFMPlan(FlightCtrlState s)
        {
            var weaponManager = WeaponManager;
            if (!bfmPlanner || !TacticsAllowed(weaponManager) || regainEnergy || weaponManager.isVIP && VIPTracker.EscortCount(weaponManager.Team) > 0) { bfmPlan.valid = false; return false; }
            Vessel opponent = targetVessel != null && !targetVessel.LandedOrSplashed && (targetVessel.CoM - vessel.CoM).sqrMagnitude < bfmRange * bfmRange ? targetVessel : null;
            Vessel threat = BFMThreat(weaponManager);
            if (threat != null && (threat == opponent || threat.LandedOrSplashed || (threat.CoM - vessel.CoM).sqrMagnitude > bfmRange * bfmRange)) threat = null;
            if (opponent == null) { opponent = threat; threat = null; }
            if (opponent == null || targetTrack.vessel != opponent && threatTrack.vessel != opponent) { bfmPlan.valid = false; return false; }
            if (targetTrack.vessel != opponent) // Fighting the threat rather than our target: predict it as the opponent.
            {
                targetTrack.vessel = threatTrack.vessel; targetTrack.force = threatTrack.force; targetTrack.have = threatTrack.have; targetTrack.maxNormal = threatTrack.maxNormal;
            }

            // A shot available now: hand over to the aiming.
            if (opponent == targetVessel && WeaponOnTarget(weaponManager, opponent)) { ++bfmAimHandovers; bfmPlan.valid = false; return false; }
            // A close-in fight the engagement planner has turned down: leave it to the extend.
            if (opponent == targetVessel && StandOff(opponent, (opponent.CoM - vessel.CoM).magnitude)) { bfmPlan.valid = false; return false; }

            if (IsExtending && extendTarget == opponent) StopExtending("manoeuvre planner");
            NoteCrossing(opponent);
            NotePass(opponent);
            if (opponent == targetVessel && FlyManoeuvre(s, opponent)) { ++bfmTicks; return true; }
            if (!bfmPlan.valid || Time.time >= bfmNextPlanTime)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                Replan(opponent, threat);
                bfmPlanTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                bfmNextPlanTime = Time.time + ReplanInterval * (0.9f + 0.2f * ((vessel.id.GetHashCode() & 7) / 7f)); // Spread the work between craft.
            }
            if (!bfmPlan.valid) return false;
            ++bfmTicks;

            Vector3 dir = vessel.srf_vel_direction;
            Vector3 lift = Vector3.Cross(bfmPlan.planeNormal, dir);
            if (lift.sqrMagnitude < 1e-4f) lift = -vesselTransform.forward;
            lift.Normalize();
            // Fly the planned lift direction through a limit at the craft's roll rate: a new plan rolls the craft round to it rather
            // than jumping the roll target.
            if (bfmLift.sqrMagnitude > 0.5f)
            {
                Vector3 held = bfmLift.ProjectOnPlanePreNormalized(dir);
                if (held.sqrMagnitude > 1e-4f) lift = Vector3.RotateTowards(held.normalized, lift, 0.8f * observedRollRate * Time.fixedDeltaTime, 0f);
            }
            bfmLift = lift;
            float angle = (bfmPlan.load < 0.2f ? 8f : 45f) * Mathf.Deg2Rad;
            Vector3 flyTo = vessel.CoM + 100f * (Mathf.Cos(angle) * dir + Mathf.Sin(angle) * lift);
            steerMode = SteerModes.Manoeuvering;
            finalMaxSteer = Mathf.Clamp(bfmPlan.load * 1.05f + 0.05f, 0.1f, 1f);
            if (bfmPlan.fullPower) AdjustThrottle(maxSpeed, false, true, true);
            else AdjustThrottle(minSpeed, true, false);
            SetStatus($"Engaging ({bfmPlan.label})");
            NoteTactic($"BFM: {bfmPlan.label}");
            if (BDArmorySettings.DEBUG_TELEMETRY || BDArmorySettings.DEBUG_AI) debugString.AppendLine($"BFM plan: {bfmPlan.label}, load {bfmPlan.load:0.00}, {(bfmPlan.fullPower ? "full" : "idle")}, score {bfmPlan.score:0.000}, thrust {fullThrustAccel:0.0} m/s², drag {dragCoefficients[0]:0.000}/{dragCoefficients[1]:0.000}, roll rate {observedRollRate:0.0}");
            FlyToPosition(s, flyTo, true);
            return true;
        }

        #region Scissors and bugout
        // A scissors is a series of overshoots: each time the flight paths cross, the opponent changes sides of our nose. Crossings are
        // counted within crossingRange when the paths are neither head-on nor parallel.
        const float crossingRange = 1500f, scissorsWindow = 15f, bugoutCooldown = 30f, bugoutScore = -0.15f, bugoutEnergyLimit = 300f;
        readonly System.Collections.Generic.List<float> crossingTimes = new System.Collections.Generic.List<float>();
        float lastCrossingSide, bfmScoreTrend, lastBugoutTime = -100f;
        Vessel crossingOpponent;
        int bfmCrossings, bfmBugouts, scissorsTicks;
        long bfmPlanTicks;
        const string bugoutReason = "bugout";
        bool InScissors => crossingTimes.Count >= 2 && Time.time - crossingTimes[crossingTimes.Count - 2] < scissorsWindow;

        void NoteCrossing(Vessel opponent)
        {
            if (opponent != crossingOpponent) { crossingOpponent = opponent; lastCrossingSide = 0f; crossingTimes.Clear(); }
            Vector3 toOpponent = opponent.CoM - vessel.CoM;
            float distance = toOpponent.magnitude;
            float pathAngle = Vector3.Dot(vessel.srf_vel_direction, opponent.srf_vel_direction);
            if (distance > crossingRange || pathAngle < -0.94f || pathAngle > 0.94f) return; // Far, head-on (20°) or parallel.
            float side = Mathf.Sign(Vector3.Dot(toOpponent, vesselTransform.right));
            if (lastCrossingSide != 0f && side != lastCrossingSide)
            {
                crossingTimes.Add(Time.time);
                if (crossingTimes.Count > 4) crossingTimes.RemoveAt(0);
                ++bfmCrossings;
            }
            lastCrossingSide = side;
            if (InScissors) { ++scissorsTicks; NoteTactic("Scissors"); }
        }

        /// <summary>
        /// Leave a scissors we're losing (the planner's own position score has stayed poor) at a moment its nose is off us, with the
        /// energy to get away: the bugout. Requests the extend and returns true while it lasts.
        /// </summary>
        bool Bugout(Vessel v, float distance)
        {
            if (IsExtending && extendingReason == bugoutReason)
            {
                if (!RunDown(v, distance)) return true;
                StopExtending("bandit's nose on us");
                return false;
            }
            if (!PlanActive(WeaponManager) || !canExtend || Time.time - lastBugoutTime < bugoutCooldown) return false;
            if (!InScissors || bfmScoreTrend > bugoutScore || !TurnedAway(v, 60f)) return false;
            if (NewDogfight && EnergyAdvantage(v) > bugoutEnergyLimit) return false; // Energy in hand is for going over the top of it, not for leaving.
            if (EnergyAdvantage(v) < -300f || vessel.srfSpeed < 0.9f * v.srfSpeed) return false; // It would run us down.
            if (!CanRunFrom(v, distance)) return false; // Its nose would be back on us inside its gun range.
            lastBugoutTime = Time.time;
            ++bfmBugouts;
            if (steeringMetricsEnabled) Debug.Log($"[BDArmory.BDModulePilotAI]: Planner {vessel.vesselName}: bugout from {v.vesselName} at {distance:F0}m (score {bfmScoreTrend:F2}, energy {EnergyAdvantage(v):F0}m)");
            RequestExtend(bugoutReason, v, Mathf.Max(6000f, distance + 4000f), ignoreCooldown: true);
            return true;
        }

        string BFMMetrics() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "bfmSecond=reverse {0}/pursue {1}/extend {2}/chop {3} crossings={4} scissors={5:F1}s bugouts={6} bfmPlanCost={7:F0}us/replan {8}",
            bfmSecondPlans[1], bfmSecondPlans[2], bfmSecondPlans[3], bfmSecondPlans[4], bfmCrossings, scissorsTicks * Time.fixedDeltaTime, bfmBugouts,
            bfmReplans > 0 ? bfmPlanTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / bfmReplans : 0, DogfightMetrics());
        #endregion

        /// <summary>Angle off the nose (°) inside which the normal aiming takes over a missile shot: the weapon manager's boresight share, at most 25°.</summary>
        static float MissileWindow(MissileBase missile) => Mathf.Clamp(0.35f * missile.maxOffBoresight, 5f, 25f);

        /// <summary>Whether the selected weapon can be brought onto the opponent now (the same windows the normal aiming uses).</summary>
        bool WeaponOnTarget(MissileFire weaponManager, Vessel opponent)
        {
            Vector3 toOpponent = opponent.CoM - vessel.CoM;
            float distance = toOpponent.magnitude;
            var missile = weaponManager.CurrentMissile;
            if (missile != null)
                return distance < bfmRange && VectorUtils.Angle(missile.GetForwardTransform(), toOpponent) < MissileWindow(missile)
                    && distance > MissileLaunchParams.GetDynamicLaunchParams(missile, opponent.Velocity(), opponent.CoM).minLaunchRange;
            var gun = weaponManager.currentGun;
            if (gun == null) return false;
            Vector3 aim = opponent.CoM - gun.GetLeadOffset();
            float off = VectorUtils.Angle(vesselTransform.up, aim - vessel.CoM);
            if (!NewGunnery) return distance < 1.2f * GunEmploymentRange(weaponManager) && off < 20f;
            // Lead pursuit builds the gun shot (flights: ten times the time on target of the planner alone), so it takes over whenever
            // the nose is within reach of the lead point: the snapshot window inside 1.5 gun ranges, 20° out to 2.5 km.
            float range = GunEmploymentRange(weaponManager);
            return (distance < 1.5f * range && off < Mathf.Clamp(0.9f * EffectiveMaxAoA, 20f, 45f)) || (distance < Mathf.Max(2500f, 1.2f * range) && off < 20f);
        }
    }
}
