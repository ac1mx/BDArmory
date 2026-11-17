using UnityEngine;

using BDArmory.Extensions;

namespace BDArmory.Control
{
    /// <summary>
    /// Basic fighter manoeuvres flown relative to the bandit, following the Navy's BFM doctrine (CNATRA P-821 chapters 9-10): the
    /// geometry is range, angle off the bandit's tail (AOT), closure and the bandit's turn circle ("bubble") with its centre ("post")
    /// and the control point one bandit turn radius behind it. The planner looks a few seconds ahead, which is shorter than most of these
    /// take, so each is entered on the situation it is made for and held until it is complete or no longer makes sense; the gun shot
    /// itself is always taken by the normal aiming as soon as it is there. Lift vector placement does the work: lift on the bandit (pure),
    /// ahead of it (lead) or behind it on its control point (lag), in or out of its plane of motion.
    ///
    /// Offensive (behind the bandit's wing line):
    ///   lag to control zone  — closing into its bubble ahead of its post: lift on the control point to get behind the post.
    ///   high yo-yo           — an overshoot developing at high AOT: quarter roll away, nose up out of its plane, as high as the closure
    ///                          needs, then back down on to it.
    ///   lag roll             — an overshoot developing at low AOT with the range right: pull up and roll against its turn, coming down behind it.
    ///   low yo-yo            — too little closure: inside its turn below its plane of motion, then the nose back up to it.
    /// Defensive (bandit behind our wing line, nose towards us):
    ///   threat turn          — hard turn into it, nose low to hold corner speed.
    ///   unload               — its nose has come off us (yo-yo out of plane): ease off and get knots back until it is back on.
    ///   break turn           — pressing inside gun range with its nose on: maximum turn into it, to make it overshoot.
    ///   turn reversal        — it overshot our extended six in close: reverse and pull for its six (not after a control-zone overshoot).
    ///   guns jink            — in gun range and able to pull lead: out of plane, alternating, to spoil its tracking.
    ///   Split-S              — defending with height to spare and an energy deficit: roll inverted and pull through, reversing with speed.
    /// Neutral:
    ///   merge game plan      — out of the pass, the flow chosen from both aircraft: one circle "max knots, close aboard, pitch" (turn
    ///                          away from its side, nose high out of plane) or two circle "corner speed, close aboard, slice" (turn
    ///                          across its tail, nose low).
    ///   Immelmann            — out of the pass with plenty of speed for a one-circle fight: half loop up, roll upright at the top.
    ///   flat scissors        — a slow scissors: early turn as it nears our six, reverse each time it crosses, least downrange travel.
    ///   rolling scissors     — a faster scissors: pull up wings level, early turn over the top, lag down the back towards its control
    ///                          zone, unload at the bottom for speed, again; flat once the deck is close.
    /// </summary>
    public partial class BDModulePilotAI
    {
        public enum Manoeuvre { None, BreakTurn, Reversal, HighYoYo, LowYoYo, FlatScissors, RollingScissors, Immelmann, SplitS, ThreatTurn, Unload, GunsJink, LagRoll, LagToControl, MergePlan }
        static readonly string[] manoeuvreNames = { "", "break turn", "turn reversal", "high yo-yo", "low yo-yo", "flat scissors", "rolling scissors", "Immelmann", "Split-S", "threat turn", "unload", "guns jink", "lag roll", "lag to control zone", "merge" };
        static readonly float[] manoeuvreCooldowns = { 0f, 4f, 3f, 6f, 6f, 8f, 8f, 15f, 15f, 0f, 2f, 3f, 6f, 4f, 6f };
        static readonly float[] manoeuvreLimits = { 0f, 5f, 3f, 6f, 6f, 15f, 15f, 14f, 14f, 30f, 4f, 4f, 5f, 4f, 6f }; // s at most (the threat turn is held round the circle).
        // Veteran and above; the others from Trained.
        static readonly bool[] manoeuvreAdvanced = { false, false, false, false, false, false, true, true, true, false, false, true, true, false, true };
        static readonly bool[] manoeuvreDefensive = { false, true, true, false, false, false, false, false, true, true, true, true, false, false, false };
        const int manoeuvreKinds = 15;

        /// <summary>The bandit relative to us, as a BFM crew reads it.</summary>
        struct BanditPicture
        {
            public Vector3 toBandit, dir, banditDir, up, banditLift, post, controlPoint, towards;
            public float range, closure, aot, ata, theirAta, hca, banditRadius, ourRadius, controlDistance, speed, banditSpeed, height, ourPitch, banditPitch, lateralSeparation;
            public float ourTurnRate, banditTurnRate, banditOutOfPlane; // rad/s from the measured turning accelerations; ° of the bandit above (+) or below our plane of motion.
            public bool behindItsWingline, itBehindOurWingline, insideBubble, behindPost, inControlZone;
        }
        BanditPicture bp;

        Manoeuvre manoeuvre = Manoeuvre.None;
        int manoeuvrePhase;
        float manoeuvreStart, manoeuvrePhaseStart, manoeuvreValue, jinkSign = 1f;
        Vector3 manoeuvreDirection, manoeuvrePlane, manoeuvreLift;
        Vessel manoeuvreOpponent;
        readonly float[] manoeuvreEnded = new float[manoeuvreKinds];
        readonly int[] manoeuvreCounts = new int[manoeuvreKinds];
        float lastCrossingSeen = -100f;

        bool ManoeuvreAllowed(Manoeuvre m) =>
            NewDogfight && Knows(manoeuvreAdvanced[(int)m] ? SkillFeature.FightChoice : SkillFeature.Tactics) && Time.time - manoeuvreEnded[(int)m] > manoeuvreCooldowns[(int)m];

        void ReadBandit(Vessel bandit)
        {
            Vector3 velocity = vessel.Velocity();
            bp.speed = Mathf.Max(velocity.magnitude, 1f);
            bp.dir = velocity / bp.speed;
            Vector3 banditVelocity = bandit.Velocity();
            bp.banditSpeed = Mathf.Max(banditVelocity.magnitude, 1f);
            bp.banditDir = banditVelocity / bp.banditSpeed;
            bp.toBandit = bandit.CoM - vessel.CoM;
            bp.range = Mathf.Max(bp.toBandit.magnitude, 1f);
            Vector3 line = bp.toBandit / bp.range;
            bp.closure = Vector3.Dot(velocity - banditVelocity, line);
            bp.ata = Vector3.Angle(bp.dir, line);                 // Our flight path off the bandit.
            bp.theirAta = Vector3.Angle(bp.banditDir, -line);     // Its flight path off us.
            bp.aot = Vector3.Angle(-bp.banditDir, -line);         // Our angle off its tail.
            bp.hca = Vector3.Angle(bp.dir, bp.banditDir);         // Heading crossing angle.
            bp.behindItsWingline = Vector3.Dot(-line, bp.banditDir) < 0f;
            bp.itBehindOurWingline = Vector3.Dot(line, bp.dir) < 0f;
            bp.up = upDirection.ProjectOnPlanePreNormalized(bp.dir);
            bp.up = bp.up.sqrMagnitude > 1e-4f ? bp.up.normalized : -vesselTransform.forward;
            bp.towards = bp.toBandit.ProjectOnPlanePreNormalized(bp.dir).normalized;
            bp.lateralSeparation = bp.toBandit.ProjectOnPlanePreNormalized(bp.banditDir).magnitude;
            bp.height = (float)vessel.radarAltitude;
            bp.ourPitch = 90f - Vector3.Angle(bp.dir, upDirection);
            bp.banditPitch = 90f - Vector3.Angle(bp.banditDir, upDirection);
            // Its turn circle from the turn it is flying (the opponent track's specific force).
            Vector3 force = targetTrack.vessel == bandit && targetTrack.have ? targetTrack.force : Vector3.zero;
            Vector3 normal = force - Vector3.Dot(force, bp.banditDir) * bp.banditDir;
            float n = normal.magnitude;
            bp.banditLift = n > 1f ? normal / n : (-bandit.ReferenceTransform.forward).ProjectOnPlanePreNormalized(bp.banditDir).normalized;
            bp.banditRadius = bp.banditSpeed * bp.banditSpeed / Mathf.Max(n, bodyGravity);
            bp.ourRadius = bp.speed * bp.speed / Mathf.Max(LiftCapability * liftMachFactor * (float)vessel.dynamicPressurekPa, bodyGravity);
            bp.post = bandit.CoM + Mathf.Min(bp.banditRadius, 5000f) * bp.banditLift;
            bp.insideBubble = (vessel.CoM - bp.post).magnitude < bp.banditRadius;
            bp.behindPost = Vector3.Dot(vessel.CoM - bp.post, bp.banditDir) < 0f;
            // The control point: one turn radius behind it on its flight path (the control zone is a 20-40° cone round it).
            bp.controlDistance = Mathf.Clamp(bp.banditRadius, 300f, 900f);
            bp.controlPoint = bandit.CoM - bp.controlDistance * bp.banditDir;
            bp.inControlZone = bp.behindItsWingline && bp.aot < 40f && bp.range > 0.6f * bp.controlDistance && bp.range < 1.6f * bp.controlDistance;
            bp.banditTurnRate = n / bp.banditSpeed;
            Vector3 ownForce = ownTrack.have ? ownTrack.force : Vector3.zero;
            bp.ourTurnRate = (ownForce - Vector3.Dot(ownForce, bp.dir) * bp.dir).magnitude / bp.speed;
            // Its elevation off our plane of motion (the plane of our flight path and lift), signed towards our canopy side's up.
            Vector3 ourLift = (-vesselTransform.forward).ProjectOnPlanePreNormalized(bp.dir).normalized;
            Vector3 planeNormal = Vector3.Cross(bp.dir, ourLift);
            float elevation = Mathf.Asin(Mathf.Clamp(Vector3.Dot(line, planeNormal), -1f, 1f)) * Mathf.Rad2Deg;
            bp.banditOutOfPlane = Vector3.Dot(planeNormal, upDirection) >= 0f ? elevation : -elevation;
        }

        /// <summary>The attacker has gone out of plane above us with its nose off: a high yo-yo, not a lag it is still turning in.</summary>
        bool BanditYoYo() => bp.theirAta > 35f && bp.banditOutOfPlane > 20f && bp.banditPitch > bp.ourPitch + 15f;

        /// <summary>Fly the manoeuvre under way, or start one the situation calls for. False: leave the craft to the planner.</summary>
        bool FlyManoeuvre(FlightCtrlState s, Vessel opponent)
        {
            if (!NewDogfight || opponent == null || !Knows(SkillFeature.Tactics)) { EndManoeuvre(); return false; }
            ReadBandit(opponent);
            if (manoeuvre != Manoeuvre.None && (opponent != manoeuvreOpponent || Time.time - manoeuvreStart > manoeuvreLimits[(int)manoeuvre])) EndManoeuvre();
            // An urgent defence takes over from anything offensive or neutral.
            if (manoeuvre == Manoeuvre.None || !manoeuvreDefensive[(int)manoeuvre]) ChooseDefence(opponent);
            if (manoeuvre == Manoeuvre.None) ChooseManoeuvre(opponent);
            if (manoeuvre == Manoeuvre.None) return false;

            Vector3 dir = bp.dir, up = bp.up, towards = bp.towards;
            Vector3 currentLift = (-vesselTransform.forward).ProjectOnPlanePreNormalized(dir).normalized;
            Vector3 lift = towards;
            float load = 1f;
            bool fullPower = true, done = false;
            switch (manoeuvre)
            {
                case Manoeuvre.ThreatTurn:
                    {
                        // Into it at the best sustained rate: nose low (slice) below the corner speed to hold it, a little nose high above it
                        // to put the excess into height rather than drag. Held round the circle while it is still behind our wing line: out-
                        // rating it is how the defender gets back to neutral, and leaving the turn gives that away.
                        float slice = bp.speed < 0.95f * cornerSpeed ? 0.35f : bp.speed > 1.1f * cornerSpeed ? -0.15f : 0.1f;
                        lift = (towards - slice * up).normalized;
                        fullPower = true;
                        done = !bp.itBehindOurWingline || bp.range > 3000f || bp.theirAta > 90f && bp.range > 1500f;
                        if (!done && bp.range < 650f && bp.theirAta < 15f) { Switch(Manoeuvre.BreakTurn); lift = towards; }
                        else if (!done && BanditYoYo() && bp.speed < cornerSpeed && bp.ourTurnRate < bp.banditTurnRate) Switch(Manoeuvre.Unload); // Its nose off, out of plane above: get knots back.
                    }
                    break;
                case Manoeuvre.Unload:
                    lift = towards; load = 0.25f; // Ease the turn, keep the lift on it, get knots back.
                    done = bp.theirAta < 20f || !bp.itBehindOurWingline || bp.speed > cornerSpeed; // Nose back on: pitch back, lift vector on (the threat turn or break takes over).
                    break;
                case Manoeuvre.BreakTurn:
                    lift = towards;
                    fullPower = bp.speed < 1.2f * cornerSpeed; // Above the corner, shed speed to tighten the turn.
                    done = bp.theirAta > 50f || !bp.itBehindOurWingline;
                    break;
                case Manoeuvre.Reversal:
                    lift = towards; // Pull for its six.
                    done = Time.time - manoeuvreStart > 1f && Vector3.Dot(currentLift, towards) > 0.8f && bp.ata < 60f;
                    break;
                case Manoeuvre.GunsJink:
                    {
                        // Roll into the bandit's plane and pull out of it, up or down, reversing every second or so.
                        if (Time.time - manoeuvrePhaseStart > 0.9f + 0.4f * Mathf.PerlinNoise(Time.time, vessel.id.GetHashCode() & 0xFF)) { jinkSign = -jinkSign; NextPhase(); }
                        Vector3 outOfPlane = Vector3.Cross(bp.banditDir, bp.toBandit).ProjectOnPlanePreNormalized(dir);
                        if (outOfPlane.sqrMagnitude < 1e-4f) outOfPlane = up;
                        float vertical = Vector3.Dot(outOfPlane.normalized, upDirection);
                        float sign = bp.height < HardDeck + 600f ? Mathf.Sign(vertical + 1e-3f) : jinkSign; // Low: only upwards.
                        lift = (sign * outOfPlane.normalized + 0.3f * towards).normalized;
                        done = bp.theirAta > 20f || bp.range > 1200f;
                    }
                    break;
                case Manoeuvre.LagToControl:
                    {
                        Vector3 toControl = (bp.controlPoint - vessel.CoM).ProjectOnPlanePreNormalized(dir);
                        lift = toControl.sqrMagnitude > 1f ? toControl.normalized : towards;
                        load = 0.7f;
                        done = bp.behindPost || bp.inControlZone || !bp.behindItsWingline;
                    }
                    break;
                case Manoeuvre.HighYoYo:
                    if (manoeuvrePhase == 0)
                    {
                        // Quarter roll away and nose up out of its plane, as far as the closure asks for.
                        float tilt = Mathf.Lerp(0.6f, 2f, Mathf.InverseLerp(20f, 120f, manoeuvreValue));
                        lift = (towards + tilt * up).normalized;
                        if (Time.time - manoeuvrePhaseStart > 2.5f || bp.closure < 10f || bp.ourPitch > 45f) NextPhase();
                    }
                    else
                    {
                        lift = towards; // Back down on to it.
                        done = bp.ata < 15f || Time.time - manoeuvrePhaseStart > 3f;
                    }
                    break;
                case Manoeuvre.LagRoll:
                    {
                        // A displacement roll: pull up, then roll the lift against its turn and over, coming down behind it.
                        float angle = Mathf.Min(1.3f * (Time.time - manoeuvreStart), Mathf.PI);
                        Vector3 against = (-bp.banditLift).ProjectOnPlanePreNormalized(dir);
                        against = against.sqrMagnitude > 1e-4f ? against.normalized : -towards;
                        lift = (Mathf.Cos(angle) * up + Mathf.Sin(angle) * against).normalized;
                        load = 0.8f;
                        done = angle >= Mathf.PI || bp.closure < 0f || bp.aot < 10f && bp.range < bp.controlDistance;
                        if (done) { manoeuvreLift = towards; }
                    }
                    break;
                case Manoeuvre.LowYoYo:
                    if (manoeuvrePhase == 0)
                    {
                        lift = (towards - 0.7f * up).normalized; // Inside its turn, below its plane of motion.
                        if (Time.time - manoeuvrePhaseStart > 2.5f || bp.height < HardDeck + 300f || bp.closure > 60f) NextPhase();
                    }
                    else
                    {
                        lift = towards; // Work the nose back up to it.
                        done = bp.ata < 15f || Time.time - manoeuvrePhaseStart > 3f;
                    }
                    break;
                case Manoeuvre.MergePlan:
                    if (passFlow == FightFlow.OneCircle) { lift = (-passSide + 0.6f * up).ProjectOnPlanePreNormalized(dir).normalized; } // Pitch, away from its side.
                    else { lift = (passSide - 0.35f * up).ProjectOnPlanePreNormalized(dir).normalized; fullPower = bp.speed < 1.05f * cornerSpeed; } // Slice across its tail at the corner.
                    done = !FlowActive || bp.ata < 40f;
                    break;
                case Manoeuvre.FlatScissors:
                    {
                        // Turn towards it in the horizontal; the side flips as it crosses (a reversal), and an early turn as it nears our
                        // six. Least downrange travel wins: idle and brakes when faster than it.
                        Vector3 level = towards.ProjectOnPlanePreNormalized(upDirection);
                        lift = level.sqrMagnitude > 1e-3f ? level.normalized : towards;
                        fullPower = bp.speed < 1.3f * minSpeed || vessel.srfSpeed < opponent.srfSpeed - 15f;
                        done = !InScissors || bp.ata < 10f;
                    }
                    break;
                case Manoeuvre.RollingScissors:
                    switch (manoeuvrePhase)
                    {
                        case 0: // Pull up wings level into the vertical.
                            lift = up;
                            if (bp.ourPitch > 60f || bp.speed < 1.5f * minSpeed) { manoeuvreValue = bp.speed; NextPhase(); }
                            break;
                        case 1: // Early turn over the top: the lift towards its control zone, not on it (that flies us out in front).
                            {
                                Vector3 toControl = (bp.controlPoint - vessel.CoM).ProjectOnPlanePreNormalized(dir);
                                lift = toControl.sqrMagnitude > 1f ? toControl.normalized : towards;
                                fullPower = true;
                                if (bp.ourPitch < -20f) NextPhase();
                            }
                            break;
                        default: // Lag down the back, unloaded, for the speed to go up again.
                            {
                                Vector3 toControl = (bp.controlPoint - vessel.CoM).ProjectOnPlanePreNormalized(dir);
                                lift = toControl.sqrMagnitude > 1f ? toControl.normalized : towards;
                                load = 0.35f;
                                if (bp.speed > Mathf.Max(1.5f * manoeuvreValue, 1.1f * cornerSpeed) || bp.ourPitch > -5f) { manoeuvrePhase = -1; NextPhase(); }
                            }
                            break;
                    }
                    if (bp.height < HardDeck + 2.5f * bp.ourRadius) { Switch(Manoeuvre.FlatScissors); break; } // Too close to the deck for another roll.
                    done = !InScissors && Time.time - manoeuvreStart > 4f || bp.ata < 10f;
                    break;
                case Manoeuvre.Immelmann:
                    if (manoeuvrePhase == 0)
                    {
                        lift = Vector3.Cross(manoeuvrePlane, dir).normalized; // Pull through the vertical loop plane.
                        if (Vector3.Dot(dir, manoeuvreDirection) < -0.85f) NextPhase(); // Heading back the way we came, inverted.
                        if (bp.speed < 1.4f * minSpeed) done = true; // Not enough speed to get over the top.
                    }
                    else
                    {
                        lift = up; load = 0.3f; // Roll upright, wings level.
                        done = Vector3.Dot(currentLift, up) > 0.9f || Time.time - manoeuvrePhaseStart > 2.5f;
                    }
                    break;
                case Manoeuvre.SplitS:
                    if (manoeuvrePhase == 0)
                    {
                        lift = -up; load = 0.1f; fullPower = false; // Roll inverted, unloaded.
                        if (Vector3.Dot(currentLift, -up) > 0.85f || Time.time - manoeuvrePhaseStart > 2f)
                        {
                            manoeuvrePlane = Vector3.Cross(dir, -up).normalized;
                            NextPhase();
                        }
                    }
                    else
                    {
                        lift = Vector3.Cross(manoeuvrePlane, dir).normalized; // Pull through, down and round.
                        fullPower = bp.speed < cornerSpeed;
                        done = Vector3.Dot(dir, manoeuvreDirection) < -0.8f && bp.ourPitch > -10f;
                        if (bp.height < HardDeck) done = true; // The recovery is the terrain logic's now.
                    }
                    break;
            }
            if (done) { EndManoeuvre(); return false; }
            if (manoeuvre == Manoeuvre.None) return false;

            float angleOff = (load < 0.2f ? 8f : load < 0.5f ? 20f : 45f) * Mathf.Deg2Rad;
            Vector3 flyTo = vessel.CoM + 100f * (Mathf.Cos(angleOff) * dir + Mathf.Sin(angleOff) * lift);
            steerMode = SteerModes.Manoeuvering;
            finalMaxSteer = Mathf.Clamp(load * EnvelopeShare * 1.05f + 0.05f, 0.1f, 1f);
            if (fullPower) AdjustThrottle(maxSpeed, false, true, true);
            else AdjustThrottle(minSpeed, true, false);
            string name = manoeuvreNames[(int)manoeuvre];
            SetStatus($"Engaging ({name})");
            NoteTactic(name);
            bfmLift = lift; // The planner picks up from the lift being flown.
            bfmPlan.valid = false;
            FlyToPosition(s, flyTo, true);
            return true;
        }

        /// <summary>The defensive reads: the bandit behind our wing line with its nose towards us.</summary>
        void ChooseDefence(Vessel bandit)
        {
            if (!bp.itBehindOurWingline || bp.theirAta > 45f || bp.range > 3000f) return;
            Manoeuvre wanted = Manoeuvre.None;
            if (bp.range < 900f && bp.theirAta < 6f && ManoeuvreAllowed(Manoeuvre.GunsJink)) wanted = Manoeuvre.GunsJink; // It can pull lead: last ditch.
            else if (bp.range < 650f && bp.theirAta < 15f && ManoeuvreAllowed(Manoeuvre.BreakTurn)) wanted = Manoeuvre.BreakTurn;
            else if (manoeuvre != Manoeuvre.None) return; // Something offensive or neutral under way stands until it ends.
            else if (bp.range > 900f && EnergyAdvantage(bandit) < -500f && bp.height > HardDeck + 2.5f * bp.ourRadius && bp.speed < 1.1f * cornerSpeed && ManoeuvreAllowed(Manoeuvre.SplitS)) wanted = Manoeuvre.SplitS;
            else if (bp.range < 2000f && ManoeuvreAllowed(Manoeuvre.ThreatTurn)) wanted = Manoeuvre.ThreatTurn;
            if (wanted == Manoeuvre.None || wanted == manoeuvre) return;
            EndManoeuvre();
            Begin(wanted, bandit, bp.dir);
            if (wanted == Manoeuvre.SplitS) manoeuvrePlane = Vector3.zero;
        }

        void ChooseManoeuvre(Vessel bandit)
        {
            if (bp.range > 3000f) return;
            Vector3 dir = bp.dir;
            Vector3 currentLift = (-vesselTransform.forward).ProjectOnPlanePreNormalized(dir).normalized;

            // It just crossed our extended six: in close, reverse and pull for its six; a control-zone overshoot (further back) keeps the turn.
            if (crossingTimes.Count > 0 && crossingTimes[crossingTimes.Count - 1] > lastCrossingSeen)
            {
                lastCrossingSeen = crossingTimes[crossingTimes.Count - 1];
                bool inClose = bp.range < Mathf.Max(bp.ourRadius, 500f);
                if (inClose && Vector3.Dot(currentLift, bp.towards) < -0.2f && ManoeuvreAllowed(Manoeuvre.Reversal)) { Begin(Manoeuvre.Reversal, bandit, dir); return; }
            }
            // Scissors.
            if (InScissors && bp.range < 1200f)
            {
                bool slow = bp.speed < 0.85f * cornerSpeed && bp.banditSpeed < 0.85f * cornerSpeed;
                bool roomForRoller = bp.height > HardDeck + 2.5f * bp.ourRadius;
                if ((slow || !roomForRoller) && ManoeuvreAllowed(Manoeuvre.FlatScissors)) { Begin(Manoeuvre.FlatScissors, bandit, dir); return; }
                if (!slow && roomForRoller && bp.speed > 1.5f * minSpeed && ManoeuvreAllowed(Manoeuvre.RollingScissors)) { Begin(Manoeuvre.RollingScissors, bandit, dir); return; }
            }
            // Offensive: behind its wing line.
            if (bp.behindItsWingline && bp.aot < 90f && bp.range < 2500f)
            {
                // An overshoot developing: more closure than it takes to slow down into the control zone in a couple of seconds.
                float excess = bp.closure - (bp.range - bp.controlDistance) / 2.5f;
                if (excess > 15f && bp.ata > 20f && bp.range < 2f * bp.controlDistance)
                {
                    if (bp.aot >= 30f && ManoeuvreAllowed(Manoeuvre.HighYoYo)) { Begin(Manoeuvre.HighYoYo, bandit, dir); manoeuvreValue = bp.closure; return; }
                    if (bp.aot < 30f && ManoeuvreAllowed(Manoeuvre.LagRoll)) { Begin(Manoeuvre.LagRoll, bandit, dir); return; }
                }
                // Entering its bubble ahead of its post: lag correction to the control zone first.
                if (bp.insideBubble && !bp.behindPost && !bp.inControlZone && bp.aot > 30f && ManoeuvreAllowed(Manoeuvre.LagToControl)) { Begin(Manoeuvre.LagToControl, bandit, dir); return; }
                // Not closing: low yo-yo inside its turn, if there is height to give.
                if (bp.closure < 10f && bp.range > 1.3f * bp.controlDistance && bp.aot < 60f && bp.height > HardDeck + 800f && ManoeuvreAllowed(Manoeuvre.LowYoYo)) { Begin(Manoeuvre.LowYoYo, bandit, dir); return; }
            }
            // Out of a head-on pass: the game plan for the flow chosen.
            if (FlowActive && Time.time - passTime < 1.5f && passOpponent == bandit)
            {
                if (passFlow == FightFlow.OneCircle && bp.speed > 1.25f * cornerSpeed && bp.speed > 2f * minSpeed
                    && (!maxAltitudeEnabled || vessel.altitude + 2.2f * bp.ourRadius < maxAltitude) && ManoeuvreAllowed(Manoeuvre.Immelmann))
                {
                    Begin(Manoeuvre.Immelmann, bandit, dir);
                    manoeuvrePlane = Vector3.Cross(dir, bp.up).normalized;
                    return;
                }
                if (ManoeuvreAllowed(Manoeuvre.MergePlan)) Begin(Manoeuvre.MergePlan, bandit, dir);
            }
        }

        void Begin(Manoeuvre m, Vessel opponent, Vector3 dir)
        {
            manoeuvre = m;
            manoeuvreOpponent = opponent;
            manoeuvreStart = manoeuvrePhaseStart = Time.time;
            manoeuvrePhase = 0;
            manoeuvreDirection = dir;
            manoeuvreLift = bp.towards;
            ++manoeuvreCounts[(int)m];
            if (steeringMetricsEnabled) Debug.Log($"[BDArmory.BDModulePilotAI]: Manoeuvre {vessel.vesselName}: {manoeuvreNames[(int)m]} against {opponent.vesselName} at {bp.range:F0}m, AOT {bp.aot:F0}°, closure {bp.closure:F0}m/s, {vessel.srfSpeed:F0}m/s, {vessel.radarAltitude:F0}m");
        }

        /// <summary>Hand over to a related manoeuvre without a cooldown gap (threat turn to break or unload).</summary>
        void Switch(Manoeuvre m)
        {
            if (!ManoeuvreAllowed(m) || manoeuvreOpponent == null) return;
            var opponent = manoeuvreOpponent;
            EndManoeuvre();
            Begin(m, opponent, bp.dir);
        }

        void NextPhase() { ++manoeuvrePhase; manoeuvrePhaseStart = Time.time; }

        /// <summary>
        /// Gunfire from the bandit we are fighting close in is answered by the defensive manoeuvres (threat turn, break, guns jink)
        /// rather than the generic evasion, which jinks at random and throws the fight away.
        /// </summary>
        bool GunThreatFoughtInBFM(MissileFire weaponManager)
        {
            if (!NewDogfight || !bfmPlanner || !TacticsAllowed(weaponManager) || targetVessel == null) return false;
            var shooter = weaponManager.incomingThreatVessel;
            return shooter != null && shooter == targetVessel && (shooter.CoM - vessel.CoM).sqrMagnitude < bfmRange * bfmRange;
        }

        void EndManoeuvre()
        {
            if (manoeuvre == Manoeuvre.None) return;
            manoeuvreEnded[(int)manoeuvre] = Time.time;
            manoeuvre = Manoeuvre.None;
            manoeuvreOpponent = null;
        }

        string ManoeuvreMetrics()
        {
            var sb = new System.Text.StringBuilder("manoeuvres=");
            for (int i = 1; i < manoeuvreKinds; ++i) sb.Append(i > 1 ? "/" : "").Append(manoeuvreNames[i].Replace(' ', '-')).Append(' ').Append(manoeuvreCounts[i]);
            return sb.ToString();
        }
    }
}
