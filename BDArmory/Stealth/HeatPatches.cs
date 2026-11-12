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
            public float Fatigue, FatigueTime;   // recently rejected flares (see StealthMath.FlareFatigue)
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
                            Debug.Log($"[BDAStealth] Flare: {ctxMissile.vesselName} decoyed (chance {100f * chance:0}%, flare {thermal:0} vs tracked {prior:0}, sensitivity ×{ctxFlareResistance:0.##}, fatigue {fatigue:0.0})");
                            if (ctxSeeker != null && ctxSeeker.HasFired && Notify.Involves(ctxSeeker.SourceVessel, heatTarget.vessel))
                                Notify.Post(heatTarget.vessel != null ? $"{ctxSeeker.shortName} · flare · {heatTarget.vessel.vesselName}" : $"{ctxSeeker.shortName} · flare");
                        }
                        else { st.Fatigue = fatigue + 1f; st.FatigueTime = now; }
                    }
                }
                if (captured && thermal > pickHeat) { pick = flare; pickHeat = thermal; }
            }
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
