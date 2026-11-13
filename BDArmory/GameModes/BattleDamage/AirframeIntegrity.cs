using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Damage;
using BDArmory.Settings;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.GameModes
{
    /// <summary>
    /// Airframe integrity (battle damage): a craft that has lost much of its structure to weapons flies worse as a whole, since its
    /// hydraulics, fuel feeds and wiring run through the whole airframe, instead of flying on at full performance with whatever parts
    /// are left. Integrity is the share of the airframe's starting hit points still in place: parts shot off or destroyed count as lost,
    /// weapons fired and parts dropped through decouplers don't. As it falls, every control surface loses deflection and actuator speed
    /// and every engine loses thrust, on top of whatever BDArmory's per-part battle damage has already done to them.
    /// </summary>
    public class AirframeIntegrity : VesselModule
    {
        // Full performance down to fullAbove, falling linearly to the floors at floorAt and below.
        // From the jnt1 F-15Z2 fights: one AMRAAM hit 12–20 m away leaves 0.63–0.77 (median 0.69) of the hit points, one at 25–29 m about
        // 0.9. The close hit is a mission kill (0.69 -> control 0.44, thrust 0.63), the graze a scratch.
        public const float fullAbove = 0.95f, floorAt = 0.6f, minControl = 0.25f, minThrust = 0.5f;
        const float interval = 0.5f, settleTime = 1f; // s

        public float Integrity { get; private set; } = 1f;
        public float ControlShare => appliedControl;
        public float ThrustShare => appliedThrust;

        readonly Dictionary<Part, float> tracked = new Dictionary<Part, float>(); // Part → hit points it started with.
        float baselineTotal, lostTotal, nextUpdate = -1f, appliedControl = 1f, appliedThrust = 1f, loggedIntegrity = 1f;
        int decoupledCount;
        bool started;

        public static float ControlFactor(float integrity) => Mathf.Lerp(minControl, 1f, Mathf.InverseLerp(floorAt, fullAbove, integrity));
        public static float ThrustFactor(float integrity) => Mathf.Lerp(minThrust, 1f, Mathf.InverseLerp(floorAt, fullAbove, integrity));

        /// <summary>The airframe integrity of a vessel (1 when unknown).</summary>
        public static float Of(Vessel v)
        {
            if (v == null) return 1f;
            var module = v.GetComponent<AirframeIntegrity>();
            return module != null ? module.Integrity : 1f;
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || !vessel.loaded || vessel.packed) return;
            if (nextUpdate < 0f) { nextUpdate = Time.time + settleTime; return; } // Let the hit points settle after loading.
            if (Time.time < nextUpdate) return;
            nextUpdate = Time.time + interval;
            Measure(); // Measured either way: the pilot AI judges its own damage from it.
            if (!BDArmorySettings.BATTLEDAMAGE || !BDArmorySettings.BD_AIRFRAME_INTEGRITY || BDArmorySettings.PAINTBALL_MODE) Apply(1f, 1f);
            else Apply(ControlFactor(Integrity), ThrustFactor(Integrity));
        }

        void Measure()
        {
            float current = 0f;
            int decoupled = 0;
            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                var decoupler = part.FindModuleImplementing<ModuleDecouplerBase>();
                if (decoupler != null && decoupler.isDecoupled) ++decoupled;
                var hp = part.FindModuleImplementing<HitpointTracker>();
                if (hp == null) continue;
                if (!tracked.TryGetValue(part, out float max))
                {
                    if (started || part.FindModuleImplementing<MissileBase>() != null) continue; // Only the parts it started with.
                    max = hp.GetMaxHitpoints();
                    if (max <= 0f) continue; // Armour panels have no hit points.
                    tracked[part] = max;
                    baselineTotal += max;
                }
                current += Mathf.Clamp(hp.Hitpoints, 0f, max);
            }
            started = true;
            bool decouplerFired = decoupled > decoupledCount;
            decoupledCount = decoupled;

            // Parts that have left: destroyed or shot off is lost structure; released on purpose isn't part of the airframe any more.
            List<Part> gone = null;
            foreach (var kvp in tracked)
                if (kvp.Key == null || kvp.Key.vessel != vessel) (gone ??= new List<Part>()).Add(kvp.Key);
            if (gone != null)
                foreach (var part in gone)
                {
                    float max = tracked[part];
                    tracked.Remove(part);
                    if (part != null && (decouplerFired || Released(part))) baselineTotal -= max;
                    else lostTotal += max;
                }
            Integrity = baselineTotal > 0f ? Mathf.Clamp01(current / baselineTotal) : 1f;
            if (BDModulePilotAI.steeringMetricsEnabled && loggedIntegrity - Integrity >= 0.05f)
            {
                loggedIntegrity = Integrity;
                Debug.Log($"[BDArmory.AirframeIntegrity]: {vessel.vesselName} integrity {Integrity:F2} (lost parts {lostTotal:F0} of {baselineTotal:F0} hp): control {ControlFactor(Integrity):F2}, thrust {ThrustFactor(Integrity):F2}");
            }
        }

        /// <summary>
        /// A part now on another vessel that left on purpose: it went with a fired decoupler. (Missiles aren't tracked at all; a wing shot
        /// off with a missile still on it is lost structure.)
        /// </summary>
        static bool Released(Part part)
        {
            var other = part.vessel;
            if (other == null) return false;
            foreach (var p in other.parts)
            {
                if (p == null) continue;
                var decoupler = p.FindModuleImplementing<ModuleDecouplerBase>();
                if (decoupler != null && decoupler.isDecoupled) return true;
            }
            return false;
        }

        /// <summary>Scale the control surfaces and engines from the shares applied last time to the new ones (so other changes to them stay).</summary>
        void Apply(float control, float thrust)
        {
            if (Mathf.Abs(control - appliedControl) > 0.005f && appliedControl > 0f)
            {
                float k = control / appliedControl;
                foreach (var surface in VesselModuleRegistry.GetModules<ModuleControlSurface>(vessel))
                {
                    if (surface == null || surface is ModuleAeroSurface) continue; // Airbrakes aren't on the hydraulics we model.
                    surface.authorityLimiter *= k;
                    surface.actuatorSpeed *= k;
                }
                appliedControl = control;
            }
            if (Mathf.Abs(thrust - appliedThrust) > 0.005f && appliedThrust > 0f)
            {
                float k = thrust / appliedThrust;
                foreach (var engine in VesselModuleRegistry.GetModules<ModuleEngines>(vessel))
                    if (engine != null) engine.thrustPercentage = Mathf.Clamp(engine.thrustPercentage * k, 0f, 100f);
                appliedThrust = thrust;
            }
        }
    }
}
