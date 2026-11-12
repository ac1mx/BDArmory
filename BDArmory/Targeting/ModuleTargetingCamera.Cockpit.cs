using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Settings;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Targeting
{
    /// <summary>
    /// Read/control surface used by IVA displays (MOARdV Avionics Systems' BDArmory bindings in the M2X cockpits).
    /// The values are kept current for the active camera whether or not the BDA camera window is drawn.
    /// </summary>
    public partial class ModuleTargetingCamera
    {
        public float angleToTarget;
        public float targetRange;
        public string geoLabel;
        public MissileLaunchParams dlz;
        // DLZ bar geometry in camera image pixels, laid out as in the BDA camera window.
        public float rangeToPixels;
        public float targetDistY;
        public float dlzRectY;
        public float dlzRectHeight;

        public float GetTGPSize() => camImageSize * BDArmorySettings.TARGET_WINDOW_SCALE;
        public float GetRangeToPixels() => rangeToPixels;

        public void ToggleRadar() => radarLock = !radarLock;

        public void ToggleSlave()
        {
            if (slaveTurrets) UnslaveTurrets();
            else SlaveTurrets();
        }

        void LateUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !cameraEnabled || activeCam != this || vessel == null || !vessel.isActiveVessel || delayedEnabling || !FlightGlobals.ready || FlightGlobals.currentMainBody == null) return;
            UpdateCockpitData();
        }

        void UpdateCockpitData()
        {
            float imageSize = camImageSize * BDArmorySettings.TARGET_WINDOW_SCALE;
            Rect imageRect = new Rect(2, 20, imageSize, imageSize);
            targetRange = Vector3.Distance(groundTargetPosition, transform.position);
            geoLabel = BodyUtils.FormattedGeoPos(bodyRelativeGTP, false);

            Vector3 upDirection = (transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
            Vector3 hForward = vessel.ReferenceTransform.up.ProjectOnPlanePreNormalized(upDirection);
            angleToTarget = VectorUtils.GetAngleOnPlane(targetPointPosition - transform.position, hForward, Vector3.Cross(upDirection, hForward));

            var wm = WeaponManager;
            if (wm && wm.selectedWeapon != null && wm.selectedWeapon.GetWeaponClass() == WeaponClasses.Missile)
            {
                MissileBase missile = wm.CurrentMissile;
                if (missile && (missile.TargetingMode == MissileBase.TargetingModes.Gps || missile.TargetingMode == MissileBase.TargetingModes.Laser))
                {
                    dlz = MissileLaunchParams.GetDynamicLaunchParams(missile, Vector3.zero, groundTargetPosition);
                    dlzRectY = imageRect.y + imageRect.height / 4;
                    dlzRectHeight = imageRect.height / 2;
                    float scaleDistance = Mathf.Max(Mathf.Max(8000f, missile.maxStaticLaunchRange * 2), targetRange);
                    rangeToPixels = 1f / scaleDistance * dlzRectHeight;
                    targetDistY = dlzRectY + dlzRectHeight - targetRange * rangeToPixels;
                }
            }
        }
    }
}
