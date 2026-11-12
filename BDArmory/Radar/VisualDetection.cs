using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Radar
{
    /// <summary>
    /// How far away a pilot's eyes can pick out a given craft: by how large it appears from where it's seen, how well it's lit,
    /// whether it's in the sun's glare or against the ground, and whether it's trailing an exhaust plume.
    /// The weapon manager's visual range setting is the upper limit on the result.
    /// </summary>
    public static class VisualDetection
    {
        const float rangePerMetre = 1600f; // A fighter (apparent size 3m nose-on, 6m side-on, 9m in planform) is picked out at about 5, 10 and 14 km.
        const float sideFill = 0.4f, frontFill = 0.15f, planFill = 0.3f; // Fraction of the bounding box that an aircraft fills from each side.
        const float nightFactor = 0.25f, groundFactor = 0.75f, glareFactor = 0.35f, glareAngle = 15f;
        const float hazeRange = 40000f; // Limit of visibility inside an atmosphere.
        const float afterburnerPlume = 4f, boostPlume = 6f, cruisePlume = 3f, plumeNightFactor = 1.5f; // Apparent sizes (m).
        const float minMissileSize = 1f;
        const float lightsSize = 3f; // Apparent size (m) of a craft showing lights in the dark.
        const float opticsFactor = 1.5f, opticsNightFactor = 0.6f; // Looking through an active targeting pod.
        /// <summary>Visual range assumed for a fighter-sized target where no particular target is in question (m).</summary>
        public const float nominalRange = 10000f;

        /// <summary>What decided the visual range to a target.</summary>
        public enum Reason { Clear, BlindSpot, Terrain, Night, Glare, AgainstGround, Plume, Lights, Haze }
        const float terrainSampleSpacing = 2500f;
        const int maxTerrainSamples = 6;
        // What the airframe hides from the cockpit of a flying craft (cosines of the half-angles of the cones).
        static readonly float bellyBlindCos = Mathf.Cos(40f * Mathf.Deg2Rad), tailBlindCos = Mathf.Cos(20f * Mathf.Deg2Rad), lowTailBlindCos = Mathf.Cos(50f * Mathf.Deg2Rad);

        /// <summary>
        /// Apparent size (m) of a craft seen from the given position: the square root of the area it presents.
        /// </summary>
        public static float ApparentSize(Vessel target, Vector3 observerPosition)
        {
            Vector3 size = target.vesselSize; // Width, height, length.
            Transform reference = target.ReferenceTransform;
            if (reference == null) return Mathf.Sqrt(planFill * size.x * size.z);
            Vector3 direction = (observerPosition - target.CoM).normalized;
            float side = Mathf.Abs(Vector3.Dot(direction, reference.right));
            float front = Mathf.Abs(Vector3.Dot(direction, reference.up));
            float plan = Mathf.Abs(Vector3.Dot(direction, reference.forward));
            return Mathf.Sqrt(side * sideFill * size.y * size.z + front * frontFill * size.x * size.y + plan * planFill * size.x * size.z);
        }

        /// <summary>
        /// Range (m) at which the observer can see the target, before the weapon manager's limit and any cloaking are applied.
        /// </summary>
        /// <param name="plumeSize">Apparent size (m) of the target's exhaust plume, 0 for none.</param>
        /// <param name="lit">The target is showing lights.</param>
        /// <param name="optics">The observer is looking through a targeting pod.</param>
        public static float Range(Vessel observer, Vessel target, float apparentSize, float plumeSize, bool lit, bool optics, out Reason reason)
        {
            Vector3 toTarget = target.CoM - observer.CoM;
            Vector3 targetDirection = toTarget.normalized;
            Vector3 up = observer.upAxis;
            float daylight = 1f, glare = 1f;
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun != null && observer.mainBody != sun)
            {
                Vector3 sunDirection = (sun.transform.position - observer.CoM).normalized;
                daylight = Mathf.InverseLerp(-0.1f, 0.1f, Vector3.Dot(up, sunDirection));
                if (daylight > 0) glare = Mathf.Lerp(1f, Mathf.Lerp(glareFactor, 1f, VectorUtils.Angle(sunDirection, targetDirection) / glareAngle), daylight);
            }
            bool inAtmosphere = observer.atmDensity > 0;
            bool againstGround = inAtmosphere && Vector3.Dot(targetDirection, up) < -0.02f;
            float light = Mathf.Lerp(optics ? opticsNightFactor : nightFactor, 1f, daylight);
            float bodyRange = rangePerMetre * apparentSize * light * (againstGround ? groundFactor : 1f) * (optics ? opticsFactor : 1f);
            float glowSize = Mathf.Max(plumeSize, lit ? lightsSize * (1f - daylight) : 0f); // Lights only show in the dark.
            float glowRange = rangePerMetre * glowSize * Mathf.Lerp(plumeNightFactor, 1f, daylight);
            float range = Mathf.Max(bodyRange, glowRange) * glare;
            reason = glare < 0.7f ? Reason.Glare
                : glowRange > bodyRange ? (plumeSize >= glowSize ? Reason.Plume : Reason.Lights)
                : light < 0.6f ? Reason.Night
                : againstGround ? Reason.AgainstGround
                : Reason.Clear;
            if (inAtmosphere && range > hazeRange) { range = hazeRange; reason = Reason.Haze; }
            return range;
        }

        public static float VesselRange(Vessel observer, Vessel target, out Reason reason)
        {
            return Range(observer, target, ApparentSize(target, observer.CoM), IsAfterburning(target) ? afterburnerPlume : 0f, IsLit(target), HasOptics(observer), out reason);
        }

        public static float MissileRange(Vessel observer, MissileBase missile, out Reason reason)
        {
            float plume = missile.MissileState == MissileBase.MissileStates.Boost ? boostPlume : missile.MissileState == MissileBase.MissileStates.Cruise ? cruisePlume : 0f;
            return Range(observer, missile.vessel, Mathf.Max(minMissileSize, ApparentSize(missile.vessel, observer.CoM)), plume, false, HasOptics(observer), out reason);
        }

        static bool IsLit(Vessel vessel)
        {
            foreach (var light in VesselModuleRegistry.GetModules<ModuleLight>(vessel))
                if (light != null && light.isOn) return true;
            return false;
        }

        static bool HasOptics(Vessel vessel)
        {
            foreach (var camera in VesselModuleRegistry.GetModules<BDArmory.Targeting.ModuleTargetingCamera>(vessel))
                if (camera != null && camera.cameraEnabled) return true;
            return false;
        }

        /// <summary>
        /// Whether the observer's own airframe hides the position from its cockpit: under the belly, dead astern, and astern below the wings.
        /// Only craft flying nose-first have these blind spots: ships and ground vehicles have an all-round view, and so does anything
        /// hovering or controlled from a part that doesn't face the way it flies, where "belly" and "tail" can't be told from the control point.
        /// </summary>
        public static bool InBlindSpot(Vessel observer, Vector3 position)
        {
            if (!BDArmory.Settings.BDArmorySettings.VISUAL_BLIND_SPOTS || observer.LandedOrSplashed || observer.atmDensity <= 0) return false;
            Transform reference = observer.ReferenceTransform;
            if (reference == null) return false;
            if (observer.srfSpeed < 30 || Vector3.Dot(reference.up, observer.srf_vel_direction) < 0.5f) return false;
            Vector3 direction = (position - observer.CoM).normalized;
            float belly = Vector3.Dot(direction, reference.forward), tail = -Vector3.Dot(direction, reference.up);
            return belly > bellyBlindCos || tail > tailBlindCos || (belly > 0 && tail > lowTailBlindCos);
        }

        static bool IsAfterburning(Vessel vessel)
        {
            if (vessel.ctrlState == null || vessel.ctrlState.mainThrottle < 0.5f) return false;
            foreach (var engine in VesselModuleRegistry.GetModules<MultiModeEngine>(vessel))
            {
                if (engine != null && !engine.runningPrimary && engine.SecondaryEngine != null && engine.SecondaryEngine.EngineIgnited && !engine.SecondaryEngine.flameout) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether the body itself or its terrain lies between two points. Unlike a raycast, this doesn't depend on terrain colliders,
        /// which only exist close to the active vessel.
        /// </summary>
        public static bool BlockedByBody(Vector3 from, Vector3 to, CelestialBody body)
        {
            if (body == null) return false;
            Vector3d a = (Vector3d)from - body.position, b = (Vector3d)to - body.position, ab = b - a;
            double lengthSqr = ab.sqrMagnitude;
            if (lengthSqr < 1 || a.sqrMagnitude < body.Radius * body.Radius || b.sqrMagnitude < body.Radius * body.Radius) return false; // Below sea level is handled by the underwater rules.
            // Closest approach of the sight line to the body's centre.
            double t = Mathf.Clamp01((float)(-Vector3d.Dot(a, ab) / lengthSqr));
            double closest = (a + t * ab).magnitude - body.Radius;
            if (closest < 0) return true;
            if (body.pqsController == null) return false;
            double highestTerrain = body.pqsController.radiusMax - body.Radius;
            double length = System.Math.Sqrt(lengthSqr);
            // Height of the sight line's lowest point bounds what terrain could reach it.
            if (closest > highestTerrain) return false;
            int samples = Mathf.Clamp(Mathf.FloorToInt((float)length / terrainSampleSpacing), 1, maxTerrainSamples);
            for (int i = 1; i <= samples; ++i)
            {
                Vector3d point = a + ab * ((double)i / (samples + 1));
                double altitude = point.magnitude - body.Radius;
                if (altitude > highestTerrain) continue;
                Vector3d world = point + body.position;
                double terrain = body.TerrainAltitude(body.GetLatitude(world), body.GetLongitude(world), !body.ocean);
                if (altitude < terrain) return true;
            }
            return false;
        }

        public struct Sample
        {
            public float time, range;
            public bool blocked;
            public Reason reason;
        }

        /// <summary>
        /// Per-observer cache: these are asked for many times per second per target, but change slowly.
        /// </summary>
        public sealed class Cache
        {
            const float lifetime = 0.5f;
            readonly Dictionary<Vessel, Sample> samples = new Dictionary<Vessel, Sample>();
            public int blindSpotSamples, blockedSamples;

            public Sample Get(Vessel observer, Vessel target, MissileBase missile = null)
            {
                float now = Time.time;
                if (samples.TryGetValue(target, out Sample sample) && now - sample.time < lifetime && now >= sample.time) return sample;
                if (samples.Count > 128) samples.Clear(); // Destroyed vessels accumulate otherwise.
                Reason reason = Reason.BlindSpot;
                float range = InBlindSpot(observer, target.CoM) ? 0f : missile != null ? MissileRange(observer, missile, out reason) : VesselRange(observer, target, out reason);
                bool blocked = BlockedByBody(observer.CoM, target.CoM, observer.mainBody);
                sample = new Sample
                {
                    time = now - lifetime * 0.2f * Random.value, // Spread the refreshes of different targets over different frames.
                    range = range,
                    reason = blocked ? Reason.Terrain : reason,
                    blocked = blocked
                };
                if (sample.range <= 0f) ++blindSpotSamples;
                if (sample.blocked) ++blockedSamples;
                samples[target] = sample;
                return sample;
            }
        }
    }
}
