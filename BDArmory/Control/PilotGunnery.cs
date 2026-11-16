using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Utils;
using BDArmory.Weapons;

namespace BDArmory.Control
{
    /// <summary>
    /// Close-in gunnery record (steering metrics only): within gunDiagRange of the target, how the time splits between the weapons the
    /// weapon manager has selected, how far the gun's lead is off the nose, why an AI-fired gun did not fire, and a line a second.
    /// </summary>
    public partial class BDModulePilotAI
    {
        // Comparison in one fight: GameData/BDArmory/PluginData/test_gunnery.on holds a word; only pilots of craft whose name contains it
        // fly with the gun employment range, the flight-path lift measure, the clean-flight drag fit and the planner's speed floor.
        static readonly string gunneryTestCraft = ReadTestWord("test_gunnery.on");
        static string ReadTestWord(string file)
        {
            string path = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/" + file);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
        }
        bool NewGunnery => string.IsNullOrEmpty(gunneryTestCraft) || (vessel != null && vessel.vesselName.Contains(gunneryTestCraft));

        float gunEmploymentRange = -1f, gunEmploymentRangeTime = -10f;
        /// <summary>
        /// Range (m) out to which the pilot flies for gun shots: the weapon manager's gun range, but no further than the craft's air-to-air
        /// guns engage, aim (maxEffectiveDistance) or reach in about 1.5 s of bullet flight, where a target's turn still can't take it
        /// out of the stream. The weapon manager's own gun range (often set to many km) still governs weapon selection.
        /// </summary>
        float GunEmploymentRange(MissileFire weaponManager)
        {
            if (weaponManager == null) return 1000f;
            if (!NewGunnery) return weaponManager.gunRange;
            if (Time.time - gunEmploymentRangeTime < 1f && gunEmploymentRangeTime <= Time.time) return gunEmploymentRange;
            gunEmploymentRangeTime = Time.time;
            float best = 0f;
            foreach (var gun in VesselModuleRegistry.GetModules<ModuleWeapon>(vessel))
            {
                if (gun == null || !gun.engageAir || gun.GetWeaponClass() != WeaponClasses.Gun) continue;
                float reach = Mathf.Min(gun.engageRangeMax, gun.maxEffectiveDistance, 1.5f * Mathf.Max(gun.bulletVelocity, 300f));
                if (reach > best) best = reach;
            }
            gunEmploymentRange = Mathf.Clamp(best > 0f ? Mathf.Min(best, weaponManager.gunRange) : weaponManager.gunRange, 300f, 2500f);
            return gunEmploymentRange;
        }

        const float gunDiagRange = 2500f;
        int gunDiagTicks, gunDiagGunTicks, gunDiagMissileTicks, gunDiagNoWeaponTicks, gunDiagFireTicks, gunDiagAim2, gunDiagAim5, gunDiagAim10, gunDiagAimingTicks;
        readonly Dictionary<string, int> gunDiagFail = new Dictionary<string, int>();
        float gunDiagNextLine;

        void UpdateGunDiagnostics()
        {
            var weaponManager = WeaponManager;
            if (weaponManager == null || targetVessel == null || targetVessel.LandedOrSplashed || vesselTransform == null || vessel == null || !vessel.loaded) return;
            Vector3 toTarget = targetVessel.CoM - vessel.CoM;
            float distance = toTarget.magnitude;
            if (distance > gunDiagRange) return;
            ++gunDiagTicks;
            var missile = weaponManager.CurrentMissile;
            var gun = weaponManager.currentGun;
            float aim = -1f;
            string fail = "";
            if (missile != null) ++gunDiagMissileTicks;
            else if (gun != null)
            {
                ++gunDiagGunTicks;
                aim = VectorUtils.Angle(vesselTransform.up, targetVessel.CoM - gun.GetLeadOffset() - vessel.CoM);
                if (aim < 2f) ++gunDiagAim2;
                if (aim < 5f) ++gunDiagAim5;
                if (aim < 10f) ++gunDiagAim10;
                if (steerMode == SteerModes.Aiming) ++gunDiagAimingTicks;
                if (gun.autoFire) ++gunDiagFireTicks;
                else
                {
                    fail = string.IsNullOrEmpty(gun.autoFireFailReason) ? "none" : gun.autoFireFailReason;
                    gunDiagFail.TryGetValue(fail, out int n);
                    gunDiagFail[fail] = n + 1;
                }
            }
            else ++gunDiagNoWeaponTicks;
            if (Time.time >= gunDiagNextLine)
            {
                gunDiagNextLine = Time.time + 1f;
                float closure = -Vector3.Dot(targetVessel.Velocity() - vessel.Velocity(), toTarget) / Mathf.Max(distance, 1f);
                float targetAspect = VectorUtils.Angle(targetVessel.srf_vel_direction, -toTarget); // 0: it is pointing at us.
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[BDArmory.BDModulePilotAI]: GunDiag {0} target={1} dist={2:F0} closure={3:F0} weapon={4} aim={5:F1} autoFire={6} fail={7} steer={8} status={9} speed={10:F0} alt={11:F0} offNose={12:F0} targetAspect={13:F0} shot={14:F2}/{15:F2} gunRange={16:F0}",
                    vessel.vesselName, targetVessel.vesselName, distance, closure,
                    missile != null ? missile.GetShortName() : gun != null ? "gun" : "none", aim, gun != null && missile == null && gun.autoFire, fail,
                    steerMode, currentStatus, vessel.srfSpeed, vessel.radarAltitude, VectorUtils.Angle(vesselTransform.up, toTarget), targetAspect,
                    ShotEstimate, ShotThreshold, GunEmploymentRange(weaponManager)));
            }
        }

        string GunMetrics()
        {
            var fails = new System.Text.StringBuilder();
            foreach (var kv in gunDiagFail) fails.Append(kv.Key.Replace(' ', '_')).Append(':').Append((kv.Value * Time.fixedDeltaTime).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            float dt = Time.fixedDeltaTime;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "altitudeClimb={10:F1}s {11} close={0:F1}s closeGun={1:F1}s closeMissile={2:F1}s closeNone={3:F1}s gunFire={4:F1}s gunAim2={5:F1}s gunAim5={6:F1}s gunAim10={7:F1}s gunAiming={8:F1}s gunFail={9}",
                gunDiagTicks * dt, gunDiagGunTicks * dt, gunDiagMissileTicks * dt, gunDiagNoWeaponTicks * dt, gunDiagFireTicks * dt, gunDiagAim2 * dt, gunDiagAim5 * dt, gunDiagAim10 * dt,
                gunDiagAimingTicks * dt, fails.Length > 0 ? fails.ToString() : "none", altitudeClimbTicks * dt, HeightMetrics());
        }
    }
}
