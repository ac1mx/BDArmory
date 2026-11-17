using UnityEngine;

using BDArmory.Extensions;

namespace BDArmory.Control
{
    /// <summary>
    /// Dogfight basics on top of the close-in planner: a hard deck the fight is kept above, never pushing the nose down at a target
    /// below it when rolling the lift onto it does the job, recovering speed when too slow to fight even with the target near the nose,
    /// and choosing the flow at the merge from both aircraft's turning performance: the smaller circle (more lift per unit of dynamic
    /// pressure) turns away from the side the opponent passed on for a one-circle fight, the faster turn rate turns towards it for a
    /// two-circle fight.
    /// </summary>
    public partial class BDModulePilotAI
    {
        // Comparison in one fight: GameData/BDArmory/PluginData/test_dogfight.on holds a word; only pilots of craft whose name contains
        // it fly these (a word no craft has: none do).
        static readonly string dogfightTestCraft = ReadTestWord("test_dogfight.on");
        bool NewDogfight => string.IsNullOrEmpty(dogfightTestCraft) || (vessel != null && vessel.vesselName.Contains(dogfightTestCraft));

        #region Hard deck
        /// <summary>Height above the ground the close-in fight is kept above: the craft's minimum altitude, or 30% of its default altitude (600–1500 m) if higher.</summary>
        float HardDeck => Mathf.Max(minAltitude, Mathf.Clamp(0.3f * defaultAltitude, 600f, 1500f));

        /// <summary>Planner score for a predicted height: nothing above the deck, growing to -1 at the craft's minimum altitude.</summary>
        float HardDeckScore(float height)
        {
            if (!NewDogfight) return 0f;
            float deck = HardDeck;
            if (height >= deck || deck <= minAltitude) return 0f;
            return -Mathf.Clamp01((deck - height) / (deck - minAltitude));
        }
        #endregion

        #region Roll, don't push
        int pushLimitedTicks;
        const float pushLimitPitchError = 15f, pushLimitRollError = 60f, pushLimit = -0.15f;
        /// <summary>
        /// A target well below the nose with the lift vector far from it: roll the lift onto it and pull, instead of pushing negative g
        /// (which flips sign every time the roll carries the target across the wings, and bleeds speed for no turn). Not while avoiding
        /// terrain, below the minimum altitude, or for the deliberate pitch-down of an Immelmann reversal.
        /// </summary>
        float LimitPush(float steerPitch, float pitchError, float rollError)
        {
            if (!NewDogfight || steerPitch >= pushLimit || avoidingTerrain || belowMinAltitude || invertRollTarget || vessel.LandedOrSplashed) return steerPitch;
            if (pitchError > -pushLimitPitchError || Mathf.Abs(rollError) < pushLimitRollError) return steerPitch;
            ++pushLimitedTicks;
            return pushLimit;
        }
        #endregion

        #region Fight flow
        public enum FightFlow { Undecided, OneCircle, TwoCircle }
        const float flowWindow = 8f, flowBonus = 0.12f, flowRadiusMargin = 1.1f, passRange = 1500f;
        FightFlow passFlow = FightFlow.Undecided;
        float passTime = -100f;
        Vector3 passSide; // World direction from us to the opponent at the pass, square to our flight path.
        Vessel passOpponent;
        bool opponentAhead;
        int oneCircleChoices, twoCircleChoices;

        /// <summary>
        /// One- or two-circle against this opponent: the craft with more lift per unit of dynamic pressure has the smaller lift-limited
        /// circle; the turn rate at the speeds flown is that lift per unit of pressure times the dynamic pressure over the speed.
        /// Undecided until both have been seen pulling hard.
        /// </summary>
        FightFlow ChooseFlow(Vessel opponent)
        {
            if (opponent == null || targetTrack.vessel != opponent || ownTrack.liftPerQ < 2f || targetTrack.liftPerQ < 2f) return FightFlow.Undecided;
            float radiusAdvantage = ownTrack.liftPerQ / targetTrack.liftPerQ;
            float ourRate = ownTrack.liftPerQ * (float)vessel.dynamicPressurekPa / Mathf.Max((float)vessel.srfSpeed, 30f);
            float theirRate = targetTrack.liftPerQ * (float)opponent.dynamicPressurekPa / Mathf.Max((float)opponent.srfSpeed, 30f);
            return radiusAdvantage > flowRadiusMargin && ourRate < theirRate ? FightFlow.OneCircle : FightFlow.TwoCircle;
        }

        /// <summary>Note a head-on pass with the opponent and the flow to fly out of it.</summary>
        void NotePass(Vessel opponent)
        {
            if (!NewDogfight || !Knows(SkillFeature.FightChoice)) { passFlow = FightFlow.Undecided; return; }
            Vector3 toOpponent = opponent.CoM - vessel.CoM;
            bool ahead = Vector3.Dot(toOpponent, vessel.srf_vel_direction) > 0f;
            if (opponent != passOpponent) { passOpponent = opponent; opponentAhead = ahead; passFlow = FightFlow.Undecided; return; }
            if (opponentAhead && !ahead && toOpponent.sqrMagnitude < passRange * passRange && Vector3.Dot(vessel.srf_vel_direction, opponent.srf_vel_direction) < -0.3f)
            {
                passTime = Time.time;
                passSide = toOpponent.ProjectOnPlanePreNormalized(vessel.srf_vel_direction).normalized;
                passFlow = ChooseFlow(opponent);
                if (passFlow == FightFlow.OneCircle) ++oneCircleChoices; else if (passFlow == FightFlow.TwoCircle) ++twoCircleChoices;
                if (steeringMetricsEnabled && passFlow != FightFlow.Undecided)
                    Debug.Log($"[BDArmory.BDModulePilotAI]: Pass {vessel.vesselName} vs {opponent.vesselName}: {passFlow} (lift/q {ownTrack.liftPerQ:F1} vs {targetTrack.liftPerQ:F1}, speed {vessel.srfSpeed:F0} vs {opponent.srfSpeed:F0})");
            }
            opponentAhead = ahead;
        }

        bool FlowActive => passFlow != FightFlow.Undecided && Time.time - passTime < flowWindow;

        /// <summary>Planner bonus for a first move that flies the chosen flow out of the pass: away from the opponent's side for one circle, towards it for two.</summary>
        float FlowBonus(Vector3 lift)
        {
            if (!FlowActive) return 0f;
            float towards = Vector3.Dot(lift, passSide);
            return flowBonus * Mathf.Clamp01(passFlow == FightFlow.TwoCircle ? towards : -towards);
        }

        /// <summary>Energy weighting in the planner for the chosen flow: the one-circle fight is flown slow, the two-circle fight fast.</summary>
        float FlowEnergyFactor => !FlowActive ? 1f : passFlow == FightFlow.OneCircle ? 0.5f : 1.5f;
        #endregion

        #region Spending an energy advantage
        const float spendAdvantage = 600f, spendRange = 1200f, spendFactor = 0.3f;
        int energySpendPlans;
        /// <summary>
        /// Height and speed in hand over an opponent out of gun range are for converting into a position behind it: while that holds,
        /// the planner's weight on keeping energy drops, so the dive onto its tail outscores sitting above it.
        /// </summary>
        float EnergySpendFactor(Vessel opponent, float distance)
        {
            if (!NewDogfight || opponent == null) return 1f;
            // A gun shot within reach of a pull: cash the energy in for it.
            if (distance < 1.5f * bfmGunRange && Vector3.Angle(vessel.srf_vel_direction, opponent.CoM - vessel.CoM) < 60f) { ++energySpendPlans; return spendFactor; }
            if (distance < spendRange || EnergyAdvantage(opponent) < spendAdvantage) return 1f;
            ++energySpendPlans;
            return spendFactor;
        }
        #endregion

        #region Forcing the overshoot
        const float overshootRange = 900f, overshootClosure = 25f, overshootBonus = 0.1f;
        bool overshootWanted;
        Vector3 overshootTowards;
        int overshootPlans;

        /// <summary>
        /// An attacker close in our rear quarter and closing fast will fly past if we turn hard into it and kill our speed: decided once
        /// per plan, then the idle first moves turning towards it score higher.
        /// </summary>
        void NoteOvershootChance(Vessel opponent)
        {
            overshootWanted = false;
            if (!NewDogfight || opponent == null) return;
            Vector3 toOpponent = opponent.CoM - vessel.CoM;
            float range = toOpponent.magnitude;
            if (range > overshootRange || range < 1f) return;
            Vector3 line = toOpponent / range;
            float closure = Vector3.Dot(opponent.Velocity() - vessel.Velocity(), -line);
            bool behindUs = Vector3.Dot(vessel.srf_vel_direction, line) < -0.7f; // Within 45° of our tail.
            bool pointedAtUs = Vector3.Dot(opponent.srf_vel_direction, -line) > 0.8f;
            if (!behindUs || !pointedAtUs || closure < overshootClosure) return;
            overshootWanted = true;
            overshootTowards = toOpponent.ProjectOnPlanePreNormalized(vessel.srf_vel_direction).normalized;
            ++overshootPlans;
        }

        float OvershootBonus(Vector3 lift, bool fullPower) =>
            !overshootWanted ? 0f : (fullPower ? 0f : overshootBonus) + 0.5f * overshootBonus * Mathf.Clamp01(Vector3.Dot(lift, overshootTowards));
        #endregion

        #region Running from a gun
        const float runGunRangeFactor = 1.5f, runNoseOnAngle = 30f, runOpening = 20f;
        int runAborts, runRefusals;

        /// <summary>
        /// Whether turning away from this opponent now gets us out of its gun range before its nose comes round onto us: the time it needs
        /// to swing its nose on (at its recent best turn), spent by us running at our speed, must put us beyond its gun range, and it must
        /// not be faster. Otherwise running only gives it a straight-line tracking shot.
        /// </summary>
        bool CanRunFrom(Vessel v, float distance)
        {
            if (!NewDogfight) return true;
            float reach = runGunRangeFactor * bfmThreatGunRange;
            if (distance > reach) return true;
            if (v.srfSpeed > vessel.srfSpeed) { ++runRefusals; return false; }
            float theirAta = Vector3.Angle(v.srf_vel_direction, vessel.CoM - v.CoM);
            float theirRate = (targetTrack.vessel == v ? targetTrack.maxNormal : 40f) / Mathf.Max((float)v.srfSpeed, 50f) * Mathf.Rad2Deg; // deg/s
            float noseOnTime = Mathf.Max(0f, theirAta - runNoseOnAngle) / Mathf.Max(theirRate, 5f);
            if (distance + noseOnTime * (float)vessel.srfSpeed > reach) return true;
            ++runRefusals;
            return false;
        }

        /// <summary>
        /// A turn-away in progress that the opponent has run down: inside its gun range, its nose on us and the range not opening. The
        /// run is over; the fight (and its defensive moves) take over again.
        /// </summary>
        bool RunDown(Vessel v, float distance)
        {
            if (!NewDogfight || v == null || distance > runGunRangeFactor * bfmThreatGunRange || TurnedAway(v, runNoseOnAngle)) return false;
            Vector3 line = (vessel.CoM - v.CoM) / Mathf.Max(distance, 1f);
            if (Vector3.Dot(vessel.Velocity() - v.Velocity(), line) > runOpening) return false; // Opening fast: still getting away.
            ++runAborts;
            return true;
        }
        #endregion

        string DogfightMetrics() => $"hardDeck={HardDeck:F0}m pushLimited={pushLimitedTicks * Time.fixedDeltaTime:F1}s flow=one {oneCircleChoices}/two {twoCircleChoices} overshootPlans={overshootPlans} energySpendPlans={energySpendPlans} liftPerQ={ownTrack.liftPerQ:F1}/{targetTrack.liftPerQ:F1} runAborts={runAborts} runRefusals={runRefusals} {ManoeuvreMetrics()}";
    }
}
