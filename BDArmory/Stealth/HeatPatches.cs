using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BDArmory.CounterMeasure;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>
    /// Graded flare decoys and smooth aspect RCS.
    /// Stock IR seekers switch to whichever source is closest to the heat they were tracking, so a flare that starts hotter
    /// than the jet always steals the lock as it cools through the jet's heat, and a cooler one never can: missiles either
    /// always or never hit. Here each flare gets one chance roll per seeker. A flare that wins captures the seeker, which
    /// follows it until it burns out or leaves the field of view and then looks for a target again; flares that lose are
    /// ignored by that seeker.
    /// </summary>
    internal static class HeatPatches
    {
        static Vessel ctxMissile;
        static MissileBase ctxSeeker;
        static float ctxFlareResistance = 1f;
        sealed class SeekerState
        {
            public readonly Dictionary<int, float> Rolled = new Dictionary<int, float>();   // flare instance id -> time of its roll
            public CMFlare Captured;
            public float CapturedAt;
            public float Fatigue, FatigueTime;   // recently rejected flares (see StealthMath.FlareFatigue)
            public float Pull, PullTime;         // smart seekers: pull gathered from flares in the track gate (StealthMath.FlarePullRate)
        }
        static readonly ConditionalWeakTable<Vessel, SeekerState> seekerStates = new ConditionalWeakTable<Vessel, SeekerState>();
        /// <summary>Flares are pooled: an object is reused no sooner than 16 s after it was lit, and burns for 5 s. A roll older than this belongs to an earlier flare.</summary>
        const float RollExpiry = 10f;
        static CMFlare pendingFlare;
        static float pendingThermal;
        public static int FlareCaptures;
        static readonly ConditionalWeakTable<float[,], StealthMath.AspectGrid> aspectGrids = new ConditionalWeakTable<float[,], StealthMath.AspectGrid>();
        public const float AspectSigma = 8f;

        static MissileBase flooredMissile;
        static float flooredOriginal;

        /// <summary>
        /// Chance that a flare from the seeker's target decoys an unfatigued seeker, at equal heat after its flare sensitivity; the fatigue
        /// of the flares it has just rejected; captured when it is already following a flare. Negative when graded flares are off.
        /// </summary>
        internal static float FlareChance(MissileBase seeker, out float fatigue, out bool captured)
        {
            captured = false; fatigue = 0f;
            if (!Settings.Enabled || !Settings.GradedFlares || seeker == null || seeker.vessel == null) return -1f;
            if (seekerStates.TryGetValue(seeker.vessel, out SeekerState st))
            {
                captured = st.Captured != null;
                fatigue = seeker.smartSeeker ? 0f : StealthMath.FlareFatigue(st.Fatigue, Time.time - st.FatigueTime);
            }
            if (seeker.smartSeeker)
            {
                // No roll to quote: flares are worth releasing while the seeker is too far out to tell the craft's shape, and nothing inside that.
                Vessel tracked = seeker.targetVessel != null ? seeker.targetVessel.Vessel : null;
                bool resolved = tracked != null && seeker.resolveRange > 0f && (tracked.CoM - seeker.vessel.CoM).sqrMagnitude < seeker.resolveRange * seeker.resolveRange;
                return resolved ? 0f : 0.5f;
            }
            return StealthMath.FlareCapture(1f, Settings.FlareChance, seeker.flareEffectivity, Settings.CmFloor);
        }

        /// <summary>
        /// Smart seekers: whether flares released by craft now can take the seeker's track (the brightest a flare ignites, over the heat
        /// the seeker sees of the craft from where it is, passes what the seeker rejects, and the seeker is not close enough to tell the
        /// craft's shape), and how long such a flare has to stay in the track gate.
        /// </summary>
        internal static bool SmartFlareWorth(MissileBase seeker, Vessel craft, out float dwell)
        {
            dwell = float.PositiveInfinity;
            if (seeker == null || seeker.vessel == null || craft == null) return false;
            if (seeker.resolveRange > 0f && (craft.CoM - seeker.vessel.CoM).sqrMagnitude < seeker.resolveRange * seeker.resolveRange) return false;
            float own = BDATargetManager.GetVesselHeatSignature(craft, Vector3.zero).Item1; // What its flares are lit to match (CMFlare.SetThermal).
            float seen = BDArmorySettings.ASPECTED_IR_SEEKERS ? BDATargetManager.GetVesselHeatSignature(craft, seeker.vessel.CoM, seeker.frontAspectHeatModifier).Item1 : own;
            if (!(own > 0f) || !(seen > 0f)) return false;
            float brightest = Mathf.Max(BDArmorySettings.FLARE_FACTOR, 0.8f) * own / seen; // The top of the range flares ignite in.
            float rate = StealthMath.FlarePullRate(brightest, seeker.flareRejection, seeker.flareEffectivity, Settings.CmFloor);
            if (rate > 0f) { dwell = 1f / rate; return true; }
            // Flares lit now keep the heat the craft has now; the craft sheds its engines' share once the pilot pulls them back, which he
            // does while flares are going out (BDModulePilotAI evasion). Worth it if they outshine what is left: the skin.
            float skin = 0f;
            foreach (var part in craft.Parts) if (part != null) skin = Mathf.Max(skin, (float)part.skinTemperature);
            rate = StealthMath.FlarePullRate(brightest * own / Mathf.Clamp(skin, 1f, own), seeker.flareRejection, seeker.flareEffectivity, Settings.CmFloor);
            if (!(rate > 0f)) return false;
            dwell = 1f / rate + EngineCoolTime;
            return true;
        }
        const float EngineCoolTime = 1f; // Allowance (s) for the engines to spool down before the craft is at its skin heat.

        internal static void HeatPrefix(Vessel missileVessel)
        {
            ctxMissile = missileVessel;
            pendingFlare = null;
            MissileBase mb = missileVessel != null ? missileVessel.GetComponent<MissileBase>() : null;
            ctxSeeker = mb;
            ctxFlareResistance = mb != null ? mb.flareEffectivity : 1f;
            // No flare-immune seekers: floor flareEffectivity for this update (BDA also multiplies the chosen flare by it).
            if (mb != null && Settings.Enabled && mb.flareEffectivity < Settings.CmFloor)
            {
                flooredMissile = mb; flooredOriginal = mb.flareEffectivity;
                mb.flareEffectivity = ctxFlareResistance = Settings.CmFloor;
            }
        }

        /// <summary>The seeker follows the flare that captured it, whatever stock picked among the vessels in view.</summary>
        internal static void HeatPostfix(float highpassThreshold, ref TargetSignatureData __result)
        {
            if (pendingFlare != null && pendingThermal > highpassThreshold) __result = new TargetSignatureData(pendingFlare, pendingThermal);
        }

        internal static void HeatFinalizer()
        {
            ctxMissile = null;
            ctxSeeker = null;
            pendingFlare = null;
            if (flooredMissile != null) { flooredMissile.flareEffectivity = flooredOriginal; flooredMissile = null; }
        }

        /// <summary>No chaff-immune missiles: floor chaffEffectivity for the duration of one guidance call (restored in the finalizer).</summary>
        internal static void MissileChaffPrefix(MissileBase __instance, out float __state)
        {
            __state = -1f;
            if (Settings.Enabled && __instance.chaffEffectivity < Settings.CmFloor) { __state = __instance.chaffEffectivity; __instance.chaffEffectivity = Settings.CmFloor; }
        }

        internal static void MissileChaffFinalizer(MissileBase __instance, float __state)
        {
            if (__state >= 0f) __instance.chaffEffectivity = __state;
        }

        sealed class RadarChaff { public float Original; public int Version = -1; }
        static readonly ConditionalWeakTable<ModuleRadar, RadarChaff> radarChaff = new ConditionalWeakTable<ModuleRadar, RadarChaff>();

        /// <summary>No chaff-immune radars: floor radarChaffClutterFactor; the configured value is kept and restored when the floor is off.</summary>
        internal static void RadarChaffPrefix(ModuleRadar __instance)
        {
            ChaffPatches.RadarLocksUpdate(__instance);
            var c = radarChaff.GetValue(__instance, r => new RadarChaff { Original = r.radarChaffClutterFactor });
            if (c.Version == Settings.Version) return;
            c.Version = Settings.Version;
            __instance.radarChaffClutterFactor = Settings.Enabled ? Mathf.Max(c.Original, Settings.CmFloor) : c.Original;
        }

        internal static bool FlarePrefix(Ray ray, float scanRadius, FloatCurve lockedSensorFOVBias, FloatCurve lockedSensorVelocityBias, FloatCurve lockedSensorVelocityMagnitudeBias,
                                float lockedSensorMinAngularVelocity, TargetSignatureData heatTarget, Vector3 heatTargetAngularVelDir, float heatTargetAngularVelMag, ref TargetSignatureData __result)
        {
            if (!Settings.Enabled || !Settings.GradedFlares || ctxMissile == null) return true;
            SeekerState st = seekerStates.GetValue(ctxMissile, _ => new SeekerState());
            float prior = Mathf.Max(heatTarget.signalStrength, 1e-3f), now = Time.time, pickHeat = 0f;
            CMFlare held = st.Captured, pick = null;
            Vector3 down = -VectorUtils.GetUpDirection(ray.origin);
            // Smart seekers: only a flare inside the track gate around the craft competes, by how much brighter it looks than the seeker
            // rejects and for how long it stays there; inside the range at which the seeker tells the craft's shape, none does.
            bool smart = ctxSeeker != null && ctxSeeker.smartSeeker;
            Vector3 toTracked = heatTarget.position - ray.origin;
            bool resolved = smart && ctxSeeker.resolveRange > 0f && heatTarget.vessel != null && toTracked.sqrMagnitude < ctxSeeker.resolveRange * ctxSeeker.resolveRange;
            CMFlare brightest = null;
            float brightestRate = 0f, brightestHeat = 0f;
            // A smart seeker following a flare goes back to the craft it was launched at, if that is still in view, once the craft
            // outshines the flare or is close and near where the seeker is looking (StealthMath.FlareReleased): a flare buys the time
            // it takes to get away from it.
            if (smart && held != null && held && ctxSeeker.targetVessel != null && ctxSeeker.targetVessel.Vessel != null)
            {
                Vessel craft = ctxSeeker.targetVessel.Vessel;
                Vector3 toCraft = craft.CoM - ray.origin, toFlare = held.transform.position - ray.origin;
                if (VectorUtils.Angle(toCraft, ray.direction) < scanRadius)
                {
                    float craftHeat = BDATargetManager.GetVesselHeatSignature(craft, BDArmorySettings.ASPECTED_IR_SEEKERS ? ctxMissile.CoM : Vector3.zero, ctxSeeker.frontAspectHeatModifier).Item1
                        * 1960000f / Mathf.Max(toCraft.sqrMagnitude, 90000f) * Mathf.Clamp(VectorUtils.Angle(toCraft, down) / 90f, 0.5f, 1.5f);
                    float flareHeat = held.thermal * 1960000f / Mathf.Clamp(toFlare.sqrMagnitude, 90000f, 36000000f) * Mathf.Clamp(VectorUtils.Angle(toFlare, down) / 90f, 0.5f, 1.5f);
                    bool close = ctxSeeker.resolveRange > 0f && toCraft.sqrMagnitude < ctxSeeker.resolveRange * ctxSeeker.resolveRange;
                    if (StealthMath.FlareReleased(craftHeat, flareHeat * ctxFlareResistance, ctxSeeker.flareRejection, close, VectorUtils.Angle(toCraft, toFlare), ctxSeeker.trackGate, now - st.CapturedAt))
                    {
                        if (BDArmory.Control.BDModulePilotAI.steeringMetricsEnabled)
                        {
                            float off = VectorUtils.Angle(toCraft, toFlare);
                            string reason = craftHeat >= Mathf.Max(ctxSeeker.flareRejection, 1f) * flareHeat * ctxFlareResistance ? "heat" : close && off < 4f * ctxSeeker.trackGate ? "resolved" : "motion";
                            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[BDAStealth] FlareRelease missile={0} reason={1} held={2:F2} craftHeat={3:F0} flareHeat={4:F0} offDeg={5:F1} range={6:F0}",
                                ctxMissile.vesselName, reason, now - st.CapturedAt, craftHeat, flareHeat * ctxFlareResistance, off, toCraft.magnitude));
                        }
                        held = null;
                    }
                }
            }
            foreach (CMFlare flare in BDArmorySetup.Flares)
            {
                if (!flare) continue;
                Vector3 to = flare.transform.position - ray.origin;
                float angle = VectorUtils.Angle(to, ray.direction);
                if (!(angle < scanRadius)) continue;
                // Same seeker-side heat as stock GetFlareTarget.
                float thermal = flare.thermal;
                thermal *= BDATargetManager.GetSeekerBias(angle, Vector3.Cross(to, flare.velocity) / to.sqrMagnitude, heatTargetAngularVelDir, heatTargetAngularVelMag,
                                lockedSensorFOVBias, lockedSensorVelocityBias, lockedSensorVelocityMagnitudeBias, lockedSensorMinAngularVelocity);
                thermal *= 1960000f / Mathf.Clamp(to.sqrMagnitude, 90000f, 36000000f);
                thermal *= Mathf.Clamp(VectorUtils.Angle(to, down) / 90f, 0.5f, 1.5f);
                if (!(thermal > 0f)) continue;
                bool captured = flare == held;
                if (smart)
                {
                    if (captured) { if (thermal > pickHeat) { pick = flare; pickHeat = thermal; } continue; }
                    if (held != null || resolved || heatTarget.vessel == null || VectorUtils.Angle(to, toTracked) > ctxSeeker.trackGate) continue;
                    float rate = StealthMath.FlarePullRate(thermal / prior, ctxSeeker.flareRejection, ctxFlareResistance, Settings.CmFloor);
                    if (rate > brightestRate) { brightest = flare; brightestRate = rate; brightestHeat = thermal; }
                    continue;
                }
                if (!captured)
                {
                    int id = flare.GetInstanceID();
                    if (!st.Rolled.TryGetValue(id, out float rolledAt) || now - rolledAt > RollExpiry)
                    {
                        st.Rolled[id] = now;
                        float fatigue = StealthMath.FlareFatigue(st.Fatigue, now - st.FatigueTime);
                        float chance = StealthMath.FlareCapture(thermal / prior, Settings.FlareChance, ctxFlareResistance, Settings.CmFloor) * StealthMath.FlareFatigueFactor(fatigue);
                        // Dumb seekers only ever go for something hotter than what they track.
                        captured = !(BDArmorySettings.DUMB_IR_SEEKERS && thermal <= prior) && Random.value < chance;
                        if (captured)
                        {
                            FlareCaptures++;
                            if (ctxSeeker != null) ctxSeeker.RecordShotDecoy();
                            Debug.Log($"[BDAStealth] Flare: {ctxMissile.vesselName} decoyed (chance {100f * chance:0}%, flare {thermal:0} vs tracked {prior:0}, sensitivity ×{ctxFlareResistance:0.##}, fatigue {fatigue:0.0})");
                            if (ctxSeeker != null && ctxSeeker.HasFired && Notify.Involves(ctxSeeker.SourceVessel, heatTarget.vessel))
                                Notify.Post(heatTarget.vessel != null ? $"{ctxSeeker.shortName} · flare · {heatTarget.vessel.vesselName}" : $"{ctxSeeker.shortName} · flare");
                        }
                        else { st.Fatigue = fatigue + 1f; st.FatigueTime = now; }
                    }
                }
                if (captured && thermal > pickHeat) { pick = flare; pickHeat = thermal; }
            }
            if (smart && pick == null)
            {
                st.Pull = StealthMath.PullAfter(st.Pull, now - st.PullTime) + brightestRate * Time.fixedDeltaTime;
                st.PullTime = now;
                if (brightest != null && st.Pull >= 1f)
                {
                    pick = brightest; pickHeat = brightestHeat;
                    st.Pull = 0f;
                    FlareCaptures++;
                    ctxSeeker.RecordShotDecoy();
                    Debug.Log($"[BDAStealth] Flare: {ctxMissile.vesselName} decoyed (flare {brightestHeat:0} vs tracked {prior:0}, rejection ×{ctxSeeker.flareRejection:0.##}, sensitivity ×{ctxFlareResistance:0.##}, gate {ctxSeeker.trackGate:0.#}°, range {toTracked.magnitude:0} m)");
                    if (ctxSeeker.HasFired && Notify.Involves(ctxSeeker.SourceVessel, heatTarget.vessel))
                        Notify.Post($"{ctxSeeker.shortName} · flare · {heatTarget.vessel.vesselName}");
                }
            }
            if (smart && st.Captured != null && pick == null && held != null && BDArmory.Control.BDModulePilotAI.steeringMetricsEnabled)
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[BDAStealth] FlareRelease missile={0} reason={1} held={2:F2}", ctxMissile.vesselName, held ? "outOfView" : "burntOut", now - st.CapturedAt));
            if (pick != st.Captured) st.CapturedAt = now;
            st.Captured = pick;
            pendingFlare = pick; pendingThermal = pickHeat;
            // Stock sees no flare; HeatPostfix hands the seeker the capturing flare, so the outcome does not depend on
            // stock's closest-heat comparison (which a flare scaled by flareEffectivity < 1 can never win).
            __result = TargetSignatureData.noTarget;
            return false;
        }

        /// <summary>Smooth aspect RCS: kernel over neighbouring samples instead of 3-nearest-or-nearest.</summary>
        internal static bool AspectPrefix(float[,] rcsMatrix, float overallRCS, float azAngle, float elAngle, ref float __result)
        {
            if (!Settings.Enabled || !Settings.SmoothAspect || rcsMatrix == null || rcsMatrix.GetLength(0) < 3) return true;
            StealthMath.AspectGrid grid = aspectGrids.GetValue(rcsMatrix, m => new StealthMath.AspectGrid(m, AspectSigma));
            __result = grid.Eval(overallRCS, BDArmorySettings.ASPECTED_RCS_OVERALL_RCS_WEIGHT, azAngle, elAngle);
            return false;
        }
    }
}
