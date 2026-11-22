using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Settings;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Estimate of how likely a missile launched now is to hit, as a logistic function of features of the shot. Most of them come from
    /// flying the missile's own config forward (MissileFlyout) against the target as it's moving now and against the target turning away
    /// and running, so that the estimate follows each missile's motor, drag and agility instead of a fixed range scale. The others are the
    /// launch geometry and the seeker. Weights can be refitted from logged shots (bench/range) and saved as
    /// GameData/BDArmory/PluginData/shot_model.cfg (lines "name = value"; "bias.PartName = value" adds a per-missile offset).
    /// </summary>
    public static class ShotModel
    {
        public static readonly string[] FeatureNames = { "bias", "reaches", "escapeReaches", "energyMargin", "flightTime", "hot", "cold", "offBoresight", "heat", "launcherGuided", "lateralG", "low", "reach", "offNose" };
        // Until fitted: start from what the flight prediction says, tempered by the geometry.
        static readonly float[] defaultWeights = { -1.2f, 1.6f, 1.0f, 0.5f, -0.3f, 0.0f, -0.3f, -1.5f, 0.4f, -0.6f, 0.2f, -0.3f, -1.5f, -1.0f };
        static float[] weights = (float[])defaultWeights.Clone();
        static readonly Dictionary<string, float> missileBias = new Dictionary<string, float>();
        static bool loaded;
        const float hotAspect = 60f, coldAspect = 135f, lowAltitude = 500f;

        public struct Features
        {
            public bool valid;
            public MissileFlyout.Result straight, escape;
            public float aspect, offBoresightFraction, launcherGuidedFraction, energyMargin;
            /// <summary>
            /// The distance the missile has to make up itself while its motor burns (range less what the closing speed covers in that
            /// time), as a share of what it flies under power at its optimum speed: past about 1 it arrives coasting and slow.
            /// </summary>
            public float reach;
            /// <summary>How far past 45° off the missile's nose the target is, in units of 45°: the turn the missile spends its motor on.</summary>
            public float offNose;
            public bool heat;
            public float[] Vector(bool low)
            {
                return new float[] {
                    1f, straight.reached ? 1f : 0f, escape.reached ? 1f : 0f, energyMargin, straight.time / 30f,
                    aspect < hotAspect ? 1f : 0f, aspect > coldAspect ? 1f : 0f, offBoresightFraction, heat ? 1f : 0f, launcherGuidedFraction,
                    Mathf.Min(straight.lateralG, 40f) / 10f, low ? 1f : 0f, reach, offNose };
            }
        }

        static void Load()
        {
            loaded = true;
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/shot_model.cfg");
                if (!File.Exists(path)) return;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Split(new[] { "//" }, StringSplitOptions.None)[0];
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string name = line.Substring(0, eq).Trim();
                    if (!float.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) continue;
                    if (name.StartsWith("bias.")) { missileBias[name.Substring(5)] = value; continue; }
                    int index = Array.IndexOf(FeatureNames, name);
                    if (index >= 0) weights[index] = value;
                }
                Debug.Log($"[BDArmory.ShotModel]: Loaded shot model weights from {path} ({missileBias.Count} per-missile offsets)");
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmory.ShotModel]: Couldn't read the shot model, using the built-in one: {e.Message}"); }
        }

        #region Flight prediction inputs
        public static MissileSpec Spec(MissileLauncher ml)
        {
            ml.EnsureAdvancedFlight();
            return SpecOf(ml);
        }
        static MissileSpec SpecOf(MissileLauncher ml) => new MissileSpec
        {
            law = ml.Law,
            // A lofted missile flies its law once it has come down on the target, one with an energy midcourse once that is over.
            lawRange = ml.GuidanceMode == MissileBase.GuidanceModes.AAMLoft ? ml.terminalHomingRange : ml.energyMidcourse && ml.AdvancedFlight ? MissileLauncher.MidcourseMinRange : 0f,
            targetMax = 80f,
            motor = ml.Motor,
            minThrottle = ml.minThrottle,
            sustainUsed = ml.SustainUsed,
            sustainLit = ml.SustainLit,
            mass = ml.part != null ? ml.part.mass : 0.1f,
            thrust = ml.thrust,
            boostTime = ml.boostTime,
            cruiseThrust = ml.cruiseThrust,
            cruiseTime = ml.cruiseTime,
            dropTime = ml.dropTime,
            liftArea = ml.currLiftArea,
            dragArea = ml.currDragArea > 0 ? ml.currDragArea : ml.currLiftArea,
            maxAoA = ml.maxAoA,
            maxTorque = ml.currMaxTorque,
            blastRadius = Mathf.Max(ml.GetBlastRadius(), 1f),
            liftMultiplier = BDArmorySettings.GLOBAL_LIFT_MULTIPLIER,
            dragMultiplier = BDArmorySettings.GLOBAL_DRAG_MULTIPLIER,
            machDrag = ml.MachDrag,
            responseTime = SteeringResponseTime
        };
        /// <summary>Time constant of a missile's turn following its guidance, fitted to range shots (bench/range, 0.4 s best of 0–1 s on 675 shots).</summary>
        const float SteeringResponseTime = 0.4f;

        // Air density against altitude for the current body, tabulated once per body (200 m steps): the atmosphere doesn't change.
        static CelestialBody densityBody;
        static float[] densityTable, soundTable;
        const float densityStep = 200f;
        static float Lookup(float[] table, double altitude)
        {
            if (table == null) return 0f;
            float index = Mathf.Clamp((float)altitude / densityStep, 0f, table.Length - 1.001f);
            int i = (int)index;
            return Mathf.Lerp(table[i], table[i + 1], index - i);
        }
        static float Density(double altitude) => Lookup(densityTable, altitude);
        static float SoundSpeed(double altitude) => Mathf.Max(Lookup(soundTable, altitude), 1f);
        static void EnsureDensityTable(CelestialBody body)
        {
            if (body == densityBody && densityTable != null) return;
            densityBody = body;
            int n = body.atmosphere ? Mathf.Max(2, (int)(body.atmosphereDepth / densityStep) + 2) : 2;
            densityTable = new float[n]; soundTable = new float[n];
            for (int i = 0; i < n; ++i)
            {
                double altitude = i * densityStep;
                if (!body.atmosphere) { soundTable[i] = 300f; continue; }
                double pressure = FlightGlobals.getStaticPressure(altitude, body);
                double density = FlightGlobals.getAtmDensity(pressure, FlightGlobals.getExternalTemperature(altitude, body), body);
                densityTable[i] = (float)density;
                soundTable[i] = density > 0 ? (float)body.GetSpeedOfSound(pressure, density) : soundTable[Math.Max(i - 1, 0)];
            }
        }
        static readonly Func<double, float> densityFunction = Density, soundFunction = SoundSpeed;
        /// <summary>Air density (kg/m³) at an altitude above the body's sea level.</summary>
        public static float AirDensity(CelestialBody body, double altitude) { EnsureDensityTable(body); return Density(altitude); }

        /// <summary>A world vector in east, north, up components at a position.</summary>
        static Vector3 Local(Vector3 world, Vector3 east, Vector3 north, Vector3 up) => new Vector3(Vector3.Dot(world, east), Vector3.Dot(world, north), Vector3.Dot(world, up));
        #endregion

        /// <summary>
        /// Features of a shot from where the missile is now (on its rail or just launched) at the target, or, with lead > 0, of the same
        /// shot taken lead seconds later with both craft carrying straight on.
        /// </summary>
        public static Features Compute(MissileBase missile, Vessel from, Vessel target, float lead = 0f, bool pointed = false, float pitchUp = 0f, float climb = 0f)
        {
            var features = new Features();
            if (missile is not MissileLauncher ml || from == null || target == null) return features;
            var body = from.mainBody;
            EnsureDensityTable(body);
            Vector3 origin = missile.transform.position + lead * (Vector3)from.Velocity();
            Vector3 up = (origin - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(body.transform.up, up).normalized;
            Vector3 east = Vector3.Cross(up, north);
            Vector3 toTarget = target.CoM + lead * (Vector3)target.Velocity() - origin;
            float range = toTarget.magnitude;
            if (range < 1f) return features;
            Vector3 line = toTarget / range;
            Vector3 velocity = Local(from.Velocity(), east, north, up), relative = Local(toTarget, east, north, up), targetVelocity = Local(target.Velocity(), east, north, up);
            if (velocity.sqrMagnitude < 1f) velocity = Local(missile.GetForwardTransform(), east, north, up) * Mathf.Max(1f, (float)from.srfSpeed);
            if (pointed) velocity = velocity.magnitude * relative.normalized; // Launched from a nose already turned onto the target.
            var spec = Spec(ml);
            float gravity = (float)(body.GeeASL * PhysicsGlobals.GravitationalAcceleration);
            float speedOfSound = from.speedOfSound > 0 ? (float)from.speedOfSound : 300f;
            double altitude = FlightGlobals.getAltitudeAtPos(origin, body);
            if (climb != 0f) { relative.z -= climb; altitude += climb; } // Launched from that much higher.
            if (pitchUp > 0f)
            {
                // Launched with the nose that far above the line to the target.
                Vector3 level = new Vector3(relative.x, relative.y, 0f);
                float elevation = Mathf.Atan2(relative.z, level.magnitude) + pitchUp * Mathf.Deg2Rad;
                velocity = velocity.magnitude * (Mathf.Cos(elevation) * level.normalized + Mathf.Sin(elevation) * Vector3.forward);
            }
            features.straight = MissileFlyout.Fly(spec, velocity.x, velocity.y, velocity.z, relative.x, relative.y, relative.z, targetVelocity.x, targetVelocity.y, targetVelocity.z, 0, 0, 0,
                Math.Max(target.srfSpeed, 1.0), altitude, densityFunction, gravity, speedOfSound, soundSpeed: soundFunction);
            // The target turns its back on the launch and runs, speeding up by a quarter.
            Vector3 away = new Vector3(relative.x, relative.y, 0f).normalized;
            float targetSpeed = Mathf.Max((float)target.srfSpeed, 50f);
            Vector3 escapeVelocity = targetSpeed * away;
            features.escape = MissileFlyout.Fly(spec, velocity.x, velocity.y, velocity.z, relative.x, relative.y, relative.z, escapeVelocity.x, escapeVelocity.y, escapeVelocity.z,
                10f * away.x, 10f * away.y, 0, 1.25 * targetSpeed, altitude, densityFunction, gravity, speedOfSound, soundSpeed: soundFunction);
            float kinematic = MissileFlyout.KinematicSpeed(spec, Density(altitude), gravity, speedOfSound);
            features.energyMargin = Mathf.Clamp(Mathf.Log(Mathf.Max(features.straight.speed, 1f) / Mathf.Max(kinematic, 50f)), -1f, 2f);
            features.aspect = VectorUtils.Angle(target.srf_vel_direction, -line);
            features.offBoresightFraction = pointed ? Mathf.Clamp(pitchUp / Mathf.Clamp(missile.maxOffBoresight, 1f, 180f), 0f, 2f)
                : Mathf.Clamp(VectorUtils.Angle(missile.GetForwardTransform(), line) / Mathf.Clamp(missile.maxOffBoresight, 1f, 180f), 0f, 2f);
            float burnTime = Mathf.Max(ml.boostTime + ml.cruiseTime, 1f);
            float closure = Vector3.Dot(from.Velocity() - target.Velocity(), line);
            features.reach = Mathf.Clamp((range - closure * burnTime) / (Mathf.Max(ml.optimumAirspeed, 100f) * burnTime), -1f, 3f);
            features.offNose = pointed ? 0f : Mathf.Clamp((VectorUtils.Angle(missile.GetForwardTransform(), line) - 45f) / 45f, 0f, 3f);
            features.heat = missile.TargetingMode == MissileBase.TargetingModes.Heat;
            features.launcherGuidedFraction = (missile.TargetingMode == MissileBase.TargetingModes.Radar || missile.TargetingMode == MissileBase.TargetingModes.Inertial)
                ? Mathf.Clamp01(1f - missile.activeRadarRange / range) : 0f;
            features.valid = true;
            return features;
        }

        /// <summary>
        /// The closest approach of a missile already flying to a craft that leaves targetPosition at targetVelocity and keeps accelerating at
        /// targetAcceleration (up to targetMaxSpeed): the flight replica from the missile's state now, motor phases following its time
        /// since launch. World-frame inputs.
        /// </summary>
        public static MissileFlyout.Result Intercept(MissileLauncher ml, Vector3 targetPosition, Vector3 targetVelocity, Vector3 targetAcceleration, float targetMaxSpeed)
        {
            var v = ml.vessel;
            var body = v.mainBody;
            EnsureDensityTable(body);
            Vector3 origin = v.CoM;
            Vector3 up = (origin - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(body.transform.up, up).normalized;
            Vector3 east = Vector3.Cross(up, north);
            Vector3 velocity = Local(v.Velocity(), east, north, up), relative = Local(targetPosition - origin, east, north, up);
            Vector3 tv = Local(targetVelocity, east, north, up), ta = Local(targetAcceleration, east, north, up);
            float gravity = (float)(body.GeeASL * PhysicsGlobals.GravitationalAcceleration);
            float speedOfSound = v.speedOfSound > 0 ? (float)v.speedOfSound : 300f;
            return MissileFlyout.Fly(Spec(ml), velocity.x, velocity.y, velocity.z, relative.x, relative.y, relative.z, tv.x, tv.y, tv.z, ta.x, ta.y, ta.z,
                Mathf.Max(targetMaxSpeed, tv.magnitude), v.altitude, densityFunction, gravity, speedOfSound, dt: 0.2f, maxTime: 60f, elapsed: ml.TimeIndex, soundSpeed: soundFunction);
        }

        /// <summary>
        /// One edge of a missile's launch zone along the present line from the shooter to the target, the shooter's nose on it: the
        /// longest range (m) from which it still reaches the target flying on as it is (maximum range), or, with escape, the target
        /// turning away at launch and running (no-escape range). Searched outwards or inwards from start, to about a tenth; 0 if it
        /// reaches from nowhere on the line.
        /// </summary>
        public static float ZoneRange(MissileBase missile, Vessel from, Vessel target, bool escape, float start)
        {
            if (missile is not MissileLauncher ml || from == null || target == null) return 0f;
            var body = from.mainBody;
            EnsureDensityTable(body);
            Vector3 origin = missile.transform.position;
            Vector3 up = (origin - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(body.transform.up, up).normalized;
            Vector3 east = Vector3.Cross(up, north);
            Vector3 line = Local(target.CoM - origin, east, north, up).normalized, tv = Local(target.Velocity(), east, north, up);
            Vector3 velocity = Mathf.Max((float)from.srfSpeed, 50f) * line;
            var spec = Spec(ml);
            float gravity = (float)(body.GeeASL * PhysicsGlobals.GravitationalAcceleration);
            float speedOfSound = from.speedOfSound > 0 ? (float)from.speedOfSound : 300f;
            double altitude = FlightGlobals.getAltitudeAtPos(origin, body);
            float targetSpeed = Mathf.Max((float)target.srfSpeed, 50f);
            Vector3 away = new Vector3(line.x, line.y, 0f).normalized;
            bool Reaches(float range)
            {
                Vector3 rel = range * line;
                if (!escape)
                    return MissileFlyout.Fly(spec, velocity.x, velocity.y, velocity.z, rel.x, rel.y, rel.z, tv.x, tv.y, tv.z, 0, 0, 0, Math.Max(targetSpeed, 1.0), altitude, densityFunction, gravity, speedOfSound, dt: 0.2f, soundSpeed: soundFunction).reached;
                return MissileFlyout.Fly(spec, velocity.x, velocity.y, velocity.z, rel.x, rel.y, rel.z, targetSpeed * away.x, targetSpeed * away.y, 0, 10f * away.x, 10f * away.y, 0,
                    1.25 * targetSpeed, altitude, densityFunction, gravity, speedOfSound, dt: 0.2f, soundSpeed: soundFunction).reached;
            }
            float lo, hi;
            start = Mathf.Clamp(start, 1000f, 150000f);
            if (Reaches(start))
            {
                lo = start; hi = 0f;
                for (int i = 0; i < 6 && hi == 0f; ++i) { float next = 1.5f * lo; if (next > 250000f) return lo; if (Reaches(next)) lo = next; else hi = next; }
                if (hi == 0f) return lo;
            }
            else
            {
                hi = start; lo = 0f;
                for (int i = 0; i < 8 && lo == 0f; ++i) { float next = 0.65f * hi; if (next < 500f) return 0f; if (Reaches(next)) lo = next; else hi = next; }
                if (lo == 0f) return 0f;
            }
            for (int i = 0; i < 3; ++i) { float mid = 0.5f * (lo + hi); if (Reaches(mid)) lo = mid; else hi = mid; }
            return lo;
        }

        /// <summary>
        /// The chance of the shot taken with the nose pitchUp degrees above the line to the target, from climb metres higher: what the
        /// pilot could give the missile by lofting the launch and by climbing first.
        /// </summary>
        public static float EstimateAssisted(MissileBase missile, Vessel from, Vessel target, float pitchUp, float climb)
        {
            if (!loaded) Load();
            var features = Compute(missile, from, target, 0f, true, pitchUp, climb);
            if (!features.valid) return -1f;
            return Logistic(features.Vector(from.radarAltitude + climb < lowAltitude), missile.part != null && missile.part.partInfo != null ? missile.part.partInfo.name : null);
        }

        static float Logistic(float[] x, string part)
        {
            float z = missileBias.TryGetValue(part ?? "", out float b) ? b : 0f;
            for (int i = 0; i < x.Length && i < weights.Length; ++i) z += weights[i] * x[i];
            return 1f / (1f + Mathf.Exp(-z));
        }

        /// <summary>Chance (0—1) that the missile hits the target if launched from the vessel now.</summary>
        public static float Estimate(MissileBase missile, Vessel from, Vessel target) => Estimate(missile, from, target, out _);

        public static float Estimate(MissileBase missile, Vessel from, Vessel target, out Features features) => Estimate(missile, from, target, 0f, out features);

        /// <summary>Chance that the missile hits if launched lead seconds from now, both craft carrying straight on meanwhile.</summary>
        public static float EstimateLater(MissileBase missile, Vessel from, Vessel target, float lead) => Estimate(missile, from, target, lead, out _);

        /// <summary>
        /// The chance of a shot launched lead seconds from now with the launcher's nose on the target (whichever way it points now): what a
        /// threat could do to us once it turns to shoot. features.escape.reached says whether we could still outrun that missile.
        /// </summary>
        public static float EstimatePointed(MissileBase missile, Vessel from, Vessel target, float lead, out Features features) => Estimate(missile, from, target, lead, out features, pointed: true);

        static float Estimate(MissileBase missile, Vessel from, Vessel target, float lead, out Features features, bool pointed = false)
        {
            if (!loaded) Load();
            features = Compute(missile, from, target, lead, pointed);
            if (!features.valid) return LegacyEstimate(missile, from, target);
            return Logistic(features.Vector(from.radarAltitude < lowAltitude), missile.part != null && missile.part.partInfo != null ? missile.part.partInfo.name : null);
        }

        /// <summary>Key=value fields describing the features, for the shot record.</summary>
        public static string Describe(in Features f) => !f.valid ? "flyout=none" : string.Format(CultureInfo.InvariantCulture,
            "flyReaches={0} flyTime={1:F1} flyMiss={2:F0} flySpeed={3:F0} flyG={4:F1} flyGaveUp={5} escReaches={6} escTime={7:F1} escMiss={8:F0} energyMargin={9:F2} offBoresightFraction={10:F2} launcherGuided={11:F2} reach={12:F2} offNose={13:F2}",
            f.straight.reached, f.straight.time, f.straight.missDistance, f.straight.speed, f.straight.lateralG, f.straight.gaveUp,
            f.escape.reached, f.escape.time, f.escape.missDistance, f.energyMargin, f.offBoresightFraction, f.launcherGuidedFraction, f.reach, f.offNose);

        #region Legacy model (modular missiles)
        // bias, range per 10 km, target coming at us, target running away, degrees off the missile's nose per 45, heat seeker, launched below 500 m.
        static readonly float[] legacyWeights = { -0.679f, -0.540f, -0.004f, -0.355f, 0.006f, 1.646f, -0.240f };
        static float LegacyEstimate(MissileBase missile, Vessel from, Vessel target)
        {
            Vector3 toTarget = target.CoM - from.CoM;
            float range = toTarget.magnitude;
            if (range < 1f) return 1f;
            Vector3 line = toTarget / range;
            float aspect = VectorUtils.Angle(target.srf_vel_direction, -line);
            float offNose = Mathf.Min(VectorUtils.Angle(missile.GetForwardTransform(), line), 90f);
            float z = legacyWeights[0] + legacyWeights[1] * range / 10000f + (aspect < hotAspect ? legacyWeights[2] : 0f) + (aspect > coldAspect ? legacyWeights[3] : 0f)
                + legacyWeights[4] * offNose / 45f + (missile.TargetingMode == MissileBase.TargetingModes.Heat ? legacyWeights[5] : 0f) + (from.radarAltitude < lowAltitude ? legacyWeights[6] : 0f);
            return 1f / (1f + Mathf.Exp(-z));
        }
        #endregion
    }
}
