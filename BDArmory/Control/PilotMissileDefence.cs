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

        #region Drag and terrain masking
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Terrain Masking", // Running from a missile or an opponent's shot, look for ground to put between us and its radar (needs the Engagement Planner).
            groupName = "pilotAI_EvadeExtend", groupDisplayName = "#LOC_BDArmory_AI_EvadeExtend", groupStartCollapsed = true),
            UI_Toggle(enabledText = "#LOC_BDArmory_Enabled", disabledText = "#LOC_BDArmory_Disabled", scene = UI_Scene.All),]
        public bool terrainMasking = false; // Off until it has been flown over land.

        const float dragDiveAngle = 15f, dragMinTime = 6f, dragMinHeight = 1500f;
        const float maskRange = 3000f, maskInterval = 1f, maskHeight = 150f;
        bool runDragging, runMasked;
        float maskTime = -100f;
        Vector3 maskDirection;
        bool maskFound;

        /// <summary>
        /// Running from a missile: put ground between us and the radar guiding it if there is some within reach (terrain masking), else
        /// run downhill into thicker air while there is height and time for it, where the missile loses speed faster than we do (drag).
        /// </summary>
        Vector3 DragOrMask(Vector3 away)
        {
            runDragging = runMasked = false;
            var weaponManager = WeaponManager;
            if (!PlanActive(weaponManager) || !NewAwareness || weaponManager.incomingMissileVessel == null) return away;
            if (TerrainMask(GuidingSensorPosition(weaponManager.incomingMissileVessel), away, out Vector3 masked)) { runMasked = true; return masked; }
            if (weaponManager.incomingMissileTime < dragMinTime || vessel.radarAltitude - minAltitude < dragMinHeight) return away;
            runDragging = true;
            return Vector3.RotateTowards(away, -upDirection, dragDiveAngle * Mathf.Deg2Rad, 0f);
        }

        /// <summary>Where the radar guiding a missile is: the launching craft's until the missile's own seeker is on, the missile after.</summary>
        static Vector3 GuidingSensorPosition(Vessel missileVessel)
        {
            var missile = VesselModuleRegistry.GetMissileBase(missileVessel);
            if (missile != null && missile.TargetingMode == MissileBase.TargetingModes.Radar && !missile.ActiveRadar && missile.SourceVessel != null) return missile.SourceVessel.CoM;
            return missileVessel.CoM;
        }

        /// <summary>
        /// A direction to run in, within 60° of away, that puts terrain between us and a sensor: the point a few km out, just above the
        /// ground, whose line to the sensor the ground cuts. Looked for once a second, over land only.
        /// </summary>
        bool TerrainMask(Vector3 sensor, Vector3 away, out Vector3 direction)
        {
            if (!terrainMasking) { direction = away; return false; }
            if (Time.time - maskTime >= maskInterval || Time.time < maskTime)
            {
                maskTime = Time.time;
                maskFound = false;
                float ground = (float)(vessel.altitude - vessel.radarAltitude);
                if (ground > 1f || !vessel.mainBody.ocean) // Over land.
                {
                    for (int i = 0; i < 5 && !maskFound; ++i)
                    {
                        float bearing = i == 0 ? 0f : (i % 2 == 1 ? 1f : -1f) * 30f * ((i + 1) / 2);
                        Vector3 heading = Quaternion.AngleAxis(bearing, upDirection) * away;
                        Vector3 point = vessel.CoM + maskRange * heading;
                        point -= (BodyUtils.GetRadarAltitudeAtPos(point) - Mathf.Max(minAltitude, maskHeight)) * upDirection; // Down to just above the ground there.
                        Vector3 line = sensor - point;
                        for (int k = 1; k <= 8 && !maskFound; ++k)
                            if (BodyUtils.GetRadarAltitudeAtPos(point + line * (k / 9f)) < 0f) { maskFound = true; maskDirection = (point - vessel.CoM).normalized; }
                    }
                }
            }
            direction = maskDirection;
            return maskFound;
        }
        #endregion

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
