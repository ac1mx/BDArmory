using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.Utils;
using BDArmory.VesselSpawning;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Testing
{
    /// <summary>
    /// Missile range (test use only). With GameData/BDArmory/PluginData/missile_range.cfg present, craft named "... Shooter N" and
    /// "... Target N" (spawned by continuous spawning, say) are paired by N. For each trial the pair is placed in a launch geometry drawn
    /// from the config, a missile of one of the listed types is spawned beside the shooter and launched at the target after the lock it
    /// needs (radar lock or its seeker's heat lock), and the missile's shot record logs what happened. Nobody fires on their own, the
    /// targets fly their normal evasion (or straight on, for a share of trials) and paintball mode keeps them alive.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MissileRange : MonoBehaviour
    {
        static readonly string ConfigPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/missile_range.cfg");
        const string ShooterTeam = "Shooters", TargetTeam = "Targets";

        readonly List<string> missiles = new List<string>();
        int pairs = 4, trials = 0, seed = 1;
        float straightFraction = 0.2f, stationLatitude = 1f, stationLongitude = -64f, pairSpacing = 15000f;
        float minAltitude = 1500f, maxAltitude = 12000f, minSpeed = 200f, maxSpeed = 420f;
        float maxRange = 90000f, heatMaxRange = 30000f, hobsFraction = 0.3f;
        float lockTimeout = 3f, settleTime = 1.5f, maxFlightTime = 150f;

        System.Random random;
        int trialsStarted, trialsFired;
        bool running;

        void Start()
        {
            if (!File.Exists(ConfigPath)) { Destroy(this); return; }
            var node = ConfigNode.Load(ConfigPath)?.GetNode("MISSILE_RANGE");
            if (node == null) { Debug.LogError("[BDArmory.MissileRange]: missile_range.cfg has no MISSILE_RANGE node"); Destroy(this); return; }
            foreach (var name in (node.GetValue("missiles") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) missiles.Add(name.Trim());
            node.TryGetValue("pairs", ref pairs);
            node.TryGetValue("trials", ref trials);
            node.TryGetValue("seed", ref seed);
            node.TryGetValue("straightFraction", ref straightFraction);
            node.TryGetValue("stationLatitude", ref stationLatitude);
            node.TryGetValue("stationLongitude", ref stationLongitude);
            node.TryGetValue("pairSpacing", ref pairSpacing);
            node.TryGetValue("minAltitude", ref minAltitude);
            node.TryGetValue("maxAltitude", ref maxAltitude);
            node.TryGetValue("minSpeed", ref minSpeed);
            node.TryGetValue("maxSpeed", ref maxSpeed);
            node.TryGetValue("maxRange", ref maxRange);
            node.TryGetValue("heatMaxRange", ref heatMaxRange);
            node.TryGetValue("hobsFraction", ref hobsFraction);
            node.TryGetValue("lockTimeout", ref lockTimeout);
            node.TryGetValue("settleTime", ref settleTime);
            node.TryGetValue("maxFlightTime", ref maxFlightTime);
            missiles.RemoveAll(name => PartLoader.getPartInfoByName(name) == null || PartLoader.getPartInfoByName(name).partPrefab.FindModuleImplementing<MissileLauncher>() == null);
            if (missiles.Count == 0) { Debug.LogError("[BDArmory.MissileRange]: no known missile parts listed"); Destroy(this); return; }
            random = new System.Random(seed);
            Debug.Log($"[BDArmory.MissileRange]: {pairs} pairs, missiles {string.Join(", ", missiles)}, {(trials > 0 ? trials.ToString() : "unlimited")} trials");
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            // Wait for the competition to be under way, so that it doesn't reset guard modes or commands after we've set them.
            while (BDACompetitionMode.Instance == null || !BDACompetitionMode.Instance.competitionIsActive) yield return new WaitForSecondsFixed(1f);
            yield return new WaitForSecondsFixed(2f);
            WriteAtmosphere(FlightGlobals.currentMainBody);
            BDArmorySettings.PAINTBALL_MODE = true;
            running = true;
            for (int i = 0; i < pairs; ++i) StartCoroutine(RunPair(i));
        }

        /// <summary>Air density and speed of sound against altitude, for the offline bench (bench/MissileBench).</summary>
        static void WriteAtmosphere(CelestialBody body)
        {
            if (body == null || !body.atmosphere) return;
            var lines = new List<string> { $"// {body.bodyName}: altitude (m), density (kg/m^3), speed of sound (m/s)" };
            for (double altitude = 0; altitude <= Math.Min(body.atmosphereDepth, 40000); altitude += 500)
            {
                double pressure = FlightGlobals.getStaticPressure(altitude, body), temperature = FlightGlobals.getExternalTemperature(altitude, body);
                double density = FlightGlobals.getAtmDensity(pressure, temperature, body);
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0:F0} {1:G6} {2:F1}", altitude, density, body.GetSpeedOfSound(pressure, density)));
            }
            File.WriteAllLines(Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/range_atmosphere.txt"), lines);
        }

        void OnDestroy()
        {
            if (running) Debug.Log($"[BDArmory.MissileRange]: stopped after {trialsStarted} trials, {trialsFired} launched");
        }

        #region Pairing
        static (Vessel shooter, Vessel target) FindPair(int index)
        {
            Vessel shooter = null, target = null;
            string suffix = " " + (index + 1).ToString(CultureInfo.InvariantCulture);
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || !v.loaded || v.state == Vessel.State.DEAD || !v.vesselName.EndsWith(suffix)) continue;
                if (v.vesselName.Contains("Shooter")) shooter = v;
                else if (v.vesselName.Contains("Target")) target = v;
            }
            return (shooter, target);
        }

        static void Prepare(Vessel v, string team, Vessel opponent)
        {
            var wm = v.ActiveController().WM;
            if (wm == null) return;
            if (wm.Team == null || wm.Team.Name != team) wm.SetTeam(BDTeam.Get(team));
            wm.HoldFire = true;
            wm.ForcedTarget = opponent;
            if (!wm.guardMode) wm.ToggleGuardMode();
        }

        /// <summary>Every resource back to full (fuel, flares, chaff, ammunition): each trial is flown by a fresh aircraft.</summary>
        static void Refill(Vessel v)
        {
            foreach (var part in v.parts)
                foreach (var resource in part.Resources)
                    if (resource.amount < resource.maxAmount) resource.amount = resource.maxAmount;
        }
        #endregion

        #region Trial geometry
        class Trial
        {
            public int id, pair;
            public string missile, targetMode;
            public float range, aspect, offBoresight, shooterAltitude, targetAltitude, shooterSpeed, targetSpeed;
            public bool heat;
            public string Describe() => string.Format(CultureInfo.InvariantCulture,
                "id={0} pair={1} missile={2} targetMode={3} range={4:F0} aspect={5:F0} offBoresight={6:F0} shooterAlt={7:F0} targetAlt={8:F0} shooterSpeed={9:F0} targetSpeed={10:F0}",
                id, pair, missile, targetMode, range, aspect, offBoresight, shooterAltitude, targetAltitude, shooterSpeed, targetSpeed);
        }

        float Uniform(float min, float max) => min + (float)random.NextDouble() * (max - min);
        float LogUniform(float min, float max) => Mathf.Exp(Uniform(Mathf.Log(min), Mathf.Log(max)));
        float Sign() => random.NextDouble() < 0.5 ? -1f : 1f;

        Trial DrawTrial(int pair)
        {
            var trial = new Trial { id = ++trialsStarted, pair = pair + 1 };
            trial.missile = missiles[(trial.id - 1) % missiles.Count];
            var prefab = PartLoader.getPartInfoByName(trial.missile).partPrefab.FindModuleImplementing<MissileLauncher>();
            trial.heat = string.Equals(prefab.targetingType, "heat", StringComparison.OrdinalIgnoreCase);
            float rMin = Mathf.Max(500f, 1.1f * prefab.minStaticLaunchRange);
            // The launch zone adds closing speed × flight time to the static range: up to ~1.6× head-on.
            float rMax = Mathf.Min(maxRange, 1.8f * prefab.maxStaticLaunchRange, trial.heat ? heatMaxRange : float.MaxValue);
            if (rMax < 2f * rMin) rMax = 2f * rMin;
            trial.range = LogUniform(rMin, rMax);
            trial.aspect = Sign() * Uniform(0f, 180f);
            trial.offBoresight = trial.heat && prefab.maxOffBoresight >= 60f && random.NextDouble() < hobsFraction ? Sign() * Uniform(10f, Mathf.Min(70f, 0.6f * prefab.maxOffBoresight)) : Sign() * Uniform(0f, 5f);
            // Range is the slant range; the height difference is kept within half of it (up to ~30° above or below the shooter).
            trial.shooterAltitude = Uniform(minAltitude, maxAltitude);
            float climb = Mathf.Min(0.5f * trial.range, maxAltitude - minAltitude);
            trial.targetAltitude = Mathf.Clamp(trial.shooterAltitude + Uniform(-climb, climb), minAltitude, maxAltitude);
            trial.shooterSpeed = Uniform(minSpeed, maxSpeed);
            trial.targetSpeed = Uniform(minSpeed, maxSpeed);
            trial.targetMode = random.NextDouble() < straightFraction ? "straight" : "evade";
            return trial;
        }

        /// <summary>Put a vessel at a position with its nose along a direction, wings level, flying at a velocity.</summary>
        static void Place(Vessel v, Vector3d centreOfMass, Vector3 nose, Vector3 up, Vector3 velocity)
        {
            Quaternion rootRotation0 = v.transform.rotation, referenceRotation0 = v.ReferenceTransform.rotation;
            Vector3 belly = -Vector3.ProjectOnPlane(up, nose).normalized;
            Quaternion referenceRotation = Quaternion.LookRotation(belly, nose); // The reference transform's up is the nose, its forward the belly.
            Quaternion rootRotation = referenceRotation * Quaternion.Inverse(referenceRotation0) * rootRotation0;
            Vector3 comOffset = rootRotation * Quaternion.Inverse(rootRotation0) * (v.CoM - v.transform.position);
            v.IgnoreGForces(240);
            v.IgnoreSpeed(240);
            v.SetRotation(rootRotation);
            v.SetPosition(centreOfMass - (Vector3d)comOffset);
            v.SetVelocity(velocity);
            foreach (var part in v.parts) if (part.rb != null) part.rb.angularVelocity = Vector3.zero;
        }
        #endregion

        IEnumerator RunPair(int pair)
        {
            var wait = new WaitForFixedUpdate();
            while (running)
            {
                if (trials > 0 && trialsStarted >= trials) yield break;
                var (shooter, target) = FindPair(pair);
                if (shooter == null || target == null || shooter.LandedOrSplashed || target.LandedOrSplashed) { yield return new WaitForSecondsFixed(2f); continue; }
                var shooterWM = shooter.ActiveController().WM;
                var targetWM = target.ActiveController().WM;
                var shooterAI = shooter.ActiveController().PilotAI;
                var targetAI = target.ActiveController().PilotAI;
                if (shooterWM == null || targetWM == null || shooterAI == null || targetAI == null) { yield return new WaitForSecondsFixed(2f); continue; }
                Prepare(shooter, ShooterTeam, target);
                Prepare(target, TargetTeam, shooter);
                Refill(shooter);
                Refill(target);

                var trial = DrawTrial(pair);
                var body = FlightGlobals.currentMainBody;
                double metresPerDegree = body.Radius * Math.PI / 180.0;
                double longitude = stationLongitude + pair * pairSpacing / metresPerDegree;
                float heightDifference = trial.targetAltitude - trial.shooterAltitude;
                double halfSpan = 0.5 * Math.Sqrt(Math.Max(trial.range * trial.range - heightDifference * heightDifference, 1.0)) / metresPerDegree;
                Vector3d targetPosition = body.GetWorldSurfacePosition(stationLatitude + halfSpan, longitude, trial.targetAltitude);
                Vector3d shooterPosition = body.GetWorldSurfacePosition(stationLatitude - halfSpan, longitude, trial.shooterAltitude);
                Vector3 shooterUp = (shooterPosition - body.position).normalized, targetUp = (targetPosition - body.position).normalized;
                Vector3 shooterToTarget = ((Vector3)(targetPosition - shooterPosition)).normalized; // The nose on the target, climbing or diving.
                Vector3 targetToShooter = Vector3.ProjectOnPlane(shooterPosition - targetPosition, targetUp).normalized;
                Vector3 shooterHeading = Quaternion.AngleAxis(trial.offBoresight, shooterUp) * shooterToTarget;
                Vector3 targetHeading = Quaternion.AngleAxis(trial.aspect, targetUp) * targetToShooter; // Aspect 0: flying straight at the shooter.
                Place(shooter, shooterPosition, shooterHeading, shooterUp, trial.shooterSpeed * shooterHeading);
                Place(target, targetPosition, targetHeading, targetUp, trial.targetSpeed * targetHeading);
                shooterAI.StopExtending("missile range trial");
                targetAI.StopExtending("missile range trial");
                shooterAI.ReleaseCommand(false);
                if (trial.targetMode == "straight")
                    targetAI.CommandFlyTo(VectorUtils.WorldPositionToGeoCoords(targetPosition + 300000.0 * (Vector3d)targetHeading, body));
                else
                    targetAI.ReleaseCommand(false);
                yield return wait;
                if (shooter == null || target == null) continue;
                shooter.SetVelocity(trial.shooterSpeed * shooterHeading); // Krakensbane may have moved the frame under the first set.
                target.SetVelocity(trial.targetSpeed * targetHeading);
                yield return new WaitForSecondsFixed(settleTime);
                if (shooter == null || target == null || shooterWM == null) continue;

                // Locks: the radar first (no missile needed), then the missile, whose own seeker finds heat targets.
                float lockStart = Time.time;
                var prefab = PartLoader.getPartInfoByName(trial.missile).partPrefab.FindModuleImplementing<MissileLauncher>();
                if (!trial.heat)
                {
                    foreach (var radar in shooterWM.radars) if (radar != null && !radar.radarEnabled) radar.EnableRadar();
                    var vrd = shooterWM.vesselRadarData;
                    float lockEnd = Time.time + lockTimeout;
                    while (vrd != null && target != null && Time.time < lockEnd && (!vrd.locked || vrd.lockedTargetData.vessel != target))
                    {
                        if (!(vrd.locked && vrd.SwitchActiveLockedTarget(target))) vrd.TryLockTarget(target);
                        yield return new WaitForSecondsFixed(0.25f);
                    }
                    if (vrd == null || target == null || !vrd.locked || vrd.lockedTargetData.vessel != target)
                    {
                        LogTrial(trial, "NoLock", vrd == null ? "no radar" : "radar", Time.time - lockStart, shooter, target);
                        continue;
                    }
                }
                var missile = SpawnMissile(shooter, trial.missile);
                if (missile == null) { LogTrial(trial, "Failed", "spawn", 0f, shooter, target); continue; }
                missile.shotContext = $"trial={trial.id} pair={trial.pair} targetMode={trial.targetMode} harness=range";
                MissileFire.TestLaunchResult result = MissileFire.TestLaunchResult.Failed;
                string reason = null;
                bool finished = false;
                StartCoroutine(shooterWM.TestLaunch(missile, target, lockTimeout, ml => PutOnLauncher(ml, shooter), (r, why) => { result = r; reason = why; finished = true; }));
                while (!finished) yield return wait;
                LogTrial(trial, result.ToString(), reason, Time.time - lockStart, shooter, target);
                if (result != MissileFire.TestLaunchResult.Fired)
                {
                    if (missile != null && missile.part != null) SpawnUtils.RemoveVessel(missile.vessel);
                    continue;
                }
                ++trialsFired;
                // The missile logs its own result when it's destroyed (hit, miss or self-destruct).
                float launchTime = Time.time;
                while (missile != null && missile.part != null && !missile.HasExploded && Time.time - launchTime < maxFlightTime) yield return wait;
                if (missile != null && missile.part != null && !missile.HasExploded) missile.Detonate();
                yield return new WaitForSecondsFixed(0.5f);
            }
        }

        /// <summary>Where a missile sits on the shooter: under the belly, its own forward (missileTransform, or the part) along the nose.</summary>
        static (Vector3 position, Quaternion rotation) LauncherPose(Part prefab, Vessel shooter)
        {
            Transform missileTransform = prefab.FindModelTransform("missileTransform");
            Quaternion local = missileTransform != null ? Quaternion.Inverse(prefab.transform.rotation) * missileTransform.rotation : Quaternion.identity;
            Vector3 nose = shooter.ReferenceTransform.up, top = -shooter.ReferenceTransform.forward;
            return (shooter.CoM - 3f * top + 1f * nose, Quaternion.LookRotation(nose, top) * Quaternion.Inverse(local));
        }

        static MissileLauncher SpawnMissile(Vessel shooter, string partName)
        {
            var info = PartLoader.getPartInfoByName(partName);
            if (info == null || shooter.rootPart == null) return null;
            var (position, rotation) = LauncherPose(info.partPrefab, shooter);
            var part = ModuleMissileRearm.CreatePart(ModuleMissileRearm.PartSnapshot(info.partPrefab), position, rotation, shooter.rootPart);
            return part != null ? part.FindModuleImplementing<MissileLauncher>() : null;
        }

        /// <summary>
        /// The spawned missile is a vessel of its own that floats free while its seeker locks (and its rigidbody can start at rest), so put
        /// it back on the shooter, moving with it, right before launch.
        /// </summary>
        static void PutOnLauncher(MissileLauncher ml, Vessel shooter)
        {
            if (ml == null || ml.vessel == null || shooter == null || ml.part.partInfo == null) return;
            var (position, rotation) = LauncherPose(ml.part.partInfo.partPrefab, shooter);
            ml.vessel.IgnoreGForces(10);
            ml.vessel.SetRotation(rotation);
            ml.vessel.SetPosition(position);
            ml.vessel.SetVelocity(shooter.Velocity());
            if (ml.part.rb != null) ml.part.rb.angularVelocity = Vector3.zero;
        }

        static void LogTrial(Trial trial, string result, string reason, float lockTime, Vessel shooter, Vessel target)
        {
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDArmory.MissileRange]: MissileRangeTrial {0} shooter={1} target={2} result={3} reason={4} lockTime={5:F1} actualRange={6:F0}",
                trial.Describe(), shooter != null ? shooter.vesselName.Replace(' ', '_') : "?", target != null ? target.vesselName.Replace(' ', '_') : "?",
                result, string.IsNullOrEmpty(reason) ? "none" : reason.Replace(' ', '_'), lockTime,
                shooter != null && target != null ? (target.CoM - shooter.CoM).magnitude : -1f));
        }
    }
}
