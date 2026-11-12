using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Settings;

namespace BDArmory.Radar
{
    /// <summary>
    /// Read/control surface used by IVA displays (MOARdV Avionics Systems' BDArmory bindings in the M2X cockpits).
    /// Signatures match the M2X "BDArmory Plus" build those bindings were compiled against.
    /// </summary>
    public partial class VesselRadarData
    {
        // DLZ bar geometry of the active locked target, in radar display pixels (set while drawing, or by RefreshDisplayWithoutGUI).
        public float rangeToPixels;
        public float targetDistY;
        public int iconType;

        public bool GetShowSelector => showSelector;
        public bool GetIsLocked => locked;
        public float GetSelectorX => selectorPos.x;
        public float GetSelectorY => selectorPos.y;

        public float GetRangeValue() => rangeIndex < rIncrements.Length ? rIncrements[rangeIndex] : 0f;
        public int GetrIncrementsCount() => rIncrements.Length;
        public int GetRangeIndex() => rangeIndex;

        public void SetRange(int index)
        {
            int clamped = Mathf.Clamp(index, 0, rIncrements.Length - 1);
            if (clamped == rangeIndex) return;
            rangeIndex = clamped;
            pingPositionsDirty = true;
        }

        public void SetSelectorX(float value) { showSelector = true; selectorPos.x = value; }
        public void SetSelectorY(float value) { showSelector = true; selectorPos.y = value; }
        public void TryLock() => TryLockViaSelector();

        public float GetRadarSize() => RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE;
        public float GetRadarHeight() => RadarDisplayRect.height;
        public float GetRadarWidth() => RadarDisplayRect.width;
        public float GetDisplayedTargetsCount() => displayedTargets.Count;
        public float GetDisplayedIRSTTargetsCount() => displayedIRTargets.Count;

        bool HasTarget(int i) => i >= 0 && i < displayedTargets.Count;
        bool HasIRTarget(int i) => i >= 0 && i < displayedIRTargets.Count;

        public double GetPingPositionX(int i) => HasTarget(i) ? displayedTargets[i].pingPosition.x : 0;
        public double GetPingPositionY(int i) => HasTarget(i) ? displayedTargets[i].pingPosition.y : 0;
        public double GetIRSTPingPositionX(int i) => HasIRTarget(i) ? displayedIRTargets[i].pingPosition.x : 0;
        public double GetIRSTPingPositionY(int i) => HasIRTarget(i) ? displayedIRTargets[i].pingPosition.y : 0;
        public double GetPingPositionLongitude(int i) => HasTarget(i) && displayedTargets[i].targetData.vessel ? displayedTargets[i].targetData.vessel.longitude : 0;
        public double GetPingPositionLatitude(int i) => HasTarget(i) && displayedTargets[i].targetData.vessel ? displayedTargets[i].targetData.vessel.latitude : 0;
        public double GetPingAltitude(int i) => HasTarget(i) ? displayedTargets[i].targetData.altitude : 0;
        public bool GetPingLocked(int i) => HasTarget(i) && displayedTargets[i].locked;
        public double GetIRSTPingMagnitude(int i) => HasIRTarget(i) ? displayedIRTargets[i].magnitude : 0;
        public string GetPingVesselId(int i) => HasTarget(i) && displayedTargets[i].vessel ? displayedTargets[i].vessel.id.ToString() : "";
        public double GetTimeAcquired(int i) => HasTarget(i) ? displayedTargets[i].targetData.timeAcquired : 0;
        public double GetSignalPersistTime(int i) => HasTarget(i) ? displayedTargets[i].signalPersistTime : 0;
        // Squared distance, as in the M2X build.
        public double GetPingDistance(int i) => HasTarget(i) ? (vessel.transform.position - displayedTargets[i].targetData.position).sqrMagnitude : 0;

        public bool GetPingIsInTeam(int i)
        {
            if (!HasTarget(i)) return false;
            var data = displayedTargets[i].targetData;
            return !((data.targetInfo && data.targetInfo.isMissile) || data.Team == null);
        }

        static double VelocityRotation(Vector3 velocity, Transform reference)
        {
            if (!reference) return 0;
            float angle = Vector3.Angle(velocity.ProjectOnPlanePreNormalized(reference.up), reference.forward);
            return reference.InverseTransformVector(velocity).x < 0 ? -angle : angle;
        }
        public double GetPingRotation(int i) => HasTarget(i) ? VelocityRotation(displayedTargets[i].targetData.velocity, referenceTransform) : 0;
        public double GetIRSTPingRotation(int i) => HasIRTarget(i) ? VelocityRotation(displayedIRTargets[i].targetData.velocity, referenceTransform) : 0;

        static double MinusAlpha(float timeAcquired, float persist) => persist > 0 ? Mathf.Clamp01((Time.time - timeAcquired) / persist) * 2f - 1f : 1;
        public double GetMinusAlpha(int i) => HasTarget(i) ? MinusAlpha(displayedTargets[i].targetData.timeAcquired, displayedTargets[i].signalPersistTime) : 0;
        public double GetIRSTMinusAlpha(int i) => HasIRTarget(i) ? MinusAlpha(displayedIRTargets[i].targetData.timeAcquired, displayedIRTargets[i].signalPersistTime) : 0;

        public int GetRadarCount() => availableRadars.Count;
        bool HasRadar(int i) => i >= 0 && i < availableRadars.Count && availableRadars[i];
        public bool GetRadarActive(int i) => HasRadar(i) && availableRadars[i].radarEnabled;
        public double GetRadarCurrentAngle(int i) => HasRadar(i) ? availableRadars[i].currentAngle : 0;
        public double GetRadarMaxAngle(int i) => HasRadar(i) ? availableRadars[i].radarAzFOV : 0;
        public bool GetRadarBoresight(int i) => HasRadar(i) && availableRadars[i].boresightScan;

        public bool SetRadarActive(int i, bool value)
        {
            if (!HasRadar(i)) return false;
            if (value) availableRadars[i].EnableRadar(); else availableRadars[i].DisableRadar();
            return value;
        }
        public bool ToggleRadarActive(int i) => HasRadar(i) && SetRadarActive(i, !GetRadarActive(i));
        public bool SetRadarBoresight(int i, bool value)
        {
            if (!HasRadar(i)) return false;
            availableRadars[i].boresightScan = value;
            return value;
        }
        public bool ToggleRadarBoresight(int i) => HasRadar(i) && SetRadarBoresight(i, !availableRadars[i].boresightScan);

        public string GetLastMissileName() => LastMissile && LastMissile.TargetAcquired ? LastMissile.GetShortName() : "";
        // Blinks while the last missile's own seeker is active, as on the M2X display.
        public bool GetLastMissileActive() => LastMissile && LastMissile.TargetAcquired && LastMissile.ActiveRadar && Mathf.Round(Time.time * 3f) % 2f == 0f;
        public double GetLastMissileTTI() => LastMissile && LastMissile.TargetAcquired ? LastMissile.TimeToImpact : 0;

        /// <summary>Icon class for display index i: 0 missile/unknown, 1 friendly, 2 hostile, 3 locked, 4 active lock, 5 jammed or no heading info; -1 no target.</summary>
        public double GetIconType(int i)
        {
            if (!HasTarget(i)) return -1;
            var t = displayedTargets[i];
            var data = t.targetData;
            bool activeLock = t.locked && locked && lockedTargetIndexes.Count > 0 && i == lockedTargetIndexes[activeLockedTargetIndex];
            bool otherLock = t.locked && locked && !activeLock;
            bool jammed = false;
            if (data.vesselJammer && data.vessel)
            {
                float jamDist = RadarUtils.GetVesselECMJammingDistance(data.vessel);
                jammed = jamDist * jamDist > (vessel.transform.position - data.position).sqrMagnitude;
            }
            if ((data.targetInfo && data.targetInfo.isMissile) || data.Team == null) iconType = 0;
            else if (!jammed && t.detectedByRadar && t.detectedByRadar.showDirectionWhileScan && data.velocity.sqrMagnitude > 50f)
                iconType = activeLock ? 4 : otherLock ? 3 : (weaponManager && weaponManager.Team.IsFriendly(data.Team)) ? 1 : 2;
            else iconType = 5;
            return iconType;
        }

        public void UnlinkAllVRD(int i)
        {
            if (!weaponManager || i < 0 || i >= weaponManager.radars.Count || !weaponManager.radars[i]) return;
            var source = weaponManager.radars[i].vesselRadarData;
            externalVRDs.Clear();
            externalRadars.Clear();
            var toUnlink = new List<ModuleRadar>();
            foreach (var radar in availableRadars)
                if (radar && radar.vesselRadarData == source) toUnlink.Add(radar);
            foreach (var radar in toUnlink)
            {
                if (BDArmorySettings.DEBUG_RADAR) Debug.Log($"[BDArmory.VesselRadarData]:  - Unlinking radar: {radar.radarName}");
                UnlinkRadar(radar);
            }
            SaveExternalVRDVessels();
        }

        /// <summary>
        /// Keeps contact positions and the DLZ geometry current while the BDA radar window is not drawn (UI hidden),
        /// so IVA displays still work. Active vessel only.
        /// </summary>
        void RefreshDisplayWithoutGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || vessel == null || vessel.packed || !vessel.isActiveVessel || rCount + iCount == 0 || !referenceTransform) return;
            UpdateGUIData();
            if (!pingPositionsDirty && !locked) return;
            for (int i = 0; i < displayedTargets.Count; i++)
            {
                var t = displayedTargets[i];
                t.pingPosition = UpdatedPingPosition(t.locked && locked ? t.targetData.predictedPosition : t.targetData.position, directionalFieldOfView);
                displayedTargets[i] = t;
            }
            for (int i = 0; i < displayedIRTargets.Count; i++)
            {
                var t = displayedIRTargets[i];
                if (t.detectedByIRST) t.pingPosition = UpdatedPingPosition(t.targetData.position, t.detectedByIRST);
                displayedIRTargets[i] = t;
            }
            pingPositionsDirty = false;
            if (locked && guiDrawDLZData && lockedTargetIndexes.Count > 0 && rangeIndex < rIncrements.Length)
            {
                rangeToPixels = 1 / rIncrements[rangeIndex] * RadarDisplayRect.height;
                float iconSize = 16 * BDArmorySettings.RADAR_WINDOW_SCALE;
                targetDistY = omniDisplay
                    ? RadarDisplayRect.height - guiDistToTarget * rangeToPixels - iconSize / 2
                    : displayedTargets[lockedTargetIndexes[activeLockedTargetIndex]].pingPosition.y - iconSize / 2;
            }
        }
    }
}
