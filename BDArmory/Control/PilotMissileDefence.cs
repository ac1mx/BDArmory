using UnityEngine;

using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Kinematic evasion answers from the missile flight replica: whether the incoming missile, with its own motor, drag, agility and
    /// give-up speed, flown from its state now with proportional navigation, still reaches us if we turn back in, crank or turn cold.
    /// Notching is left to the existing estimate: it defeats the seeker, not the missile's energy.
    /// </summary>
    public partial class BDModulePilotAI
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Missile Flyout Evasion", // Kinematic evasion judged by flying the incoming missile (needs Advanced Tactics).
            groupName = "pilotAI_EvadeExtend", groupDisplayName = "#LOC_BDArmory_AI_EvadeExtend", groupStartCollapsed = true),
            UI_Toggle(enabledText = "#LOC_BDArmory_Enabled", disabledText = "#LOC_BDArmory_Disabled", scene = UI_Scene.All),]
        public bool missileFlyoutEvasion = true;

        const float flyoutRefreshInterval = 0.3f; // s per evasion option and incoming missile.
        Vessel flyoutMissile;
        readonly float[] flyoutMissSqr = new float[6], flyoutTime = new float[6], flyoutAt = { -1f, -1f, -1f, -1f, -1f, -1f };
        int flyoutEvaluations;

        /// <summary>
        /// Squared closest approach of the incoming missile if we leave in the given direction at the given speed and keep speeding up
        /// along it, and the time to it. False when the incoming missile isn't one the replica covers (modular missiles).
        /// </summary>
        bool FlyoutMissSqr(KinematicEvasionStates state, Vector3 direction, float speed, float acceleration, out float missSqr, out float time)
        {
            missSqr = 0f; time = 0f;
            var weaponManager = WeaponManager;
            var missileVessel = weaponManager != null ? weaponManager.incomingMissileVessel : null;
            if (missileVessel == null || VesselModuleRegistry.GetMissileBase(missileVessel) is not MissileLauncher ml || !ml.HasFired) return false;
            int i = (int)state;
            if (missileVessel != flyoutMissile)
            {
                flyoutMissile = missileVessel;
                for (int k = 0; k < flyoutAt.Length; ++k) flyoutAt[k] = -1f;
            }
            if (flyoutAt[i] < 0f || Time.time - flyoutAt[i] > flyoutRefreshInterval)
            {
                var result = ShotModel.Intercept(ml, vessel.CoM, speed * direction, Mathf.Max(acceleration, 0f) * direction, Mathf.Max(maxSpeed, speed));
                flyoutMissSqr[i] = result.reached ? 0f : result.missDistance * result.missDistance;
                flyoutTime[i] = result.time;
                flyoutAt[i] = Time.time;
                ++flyoutEvaluations;
            }
            missSqr = flyoutMissSqr[i];
            time = flyoutTime[i];
            return true;
        }
    }
}
