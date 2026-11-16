using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.Settings;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Radar
{
    public partial class VesselRadarData : MonoBehaviour
    {
        private List<ModuleRadar> availableRadars;
        private List<ModuleIRST> availableIRSTs;
        private List<ModuleRadar> externalRadars;
        public List<VesselRadarData> externalVRDs;
        private float _maxRadarRange = 0;
        internal bool resizingWindow = false;

        public Rect RADARresizeRect = new Rect(
            BDArmorySetup.WindowRectRadar.width - 17 * BDArmorySettings.RADAR_WINDOW_SCALE,
            BDArmorySetup.WindowRectRadar.height - 17 * BDArmorySettings.RADAR_WINDOW_SCALE,
            16 * BDArmorySettings.RADAR_WINDOW_SCALE,
            16 * BDArmorySettings.RADAR_WINDOW_SCALE);

        private int rCount = 0;

        public int radarCount
        {
            get { return rCount; }
        }

        private int iCount = 0;

        public int irstCount
        {
            get { return iCount; }
        }

        public bool guiEnabled
        {
            get { return drawGUI; }
        }

        private bool drawGUI;

        public MissileFire weaponManager; // This is set and updated externally by ModuleIRST and ModuleRadar, but otherwise does not update dynamically.
        public bool canReceiveRadarData;

        //GUI
        public bool linkWindowOpen;
        private float numberOfAvailableLinks;
        public Rect linkWindowRect = new Rect(0, 0, 0, 0);
        private float linkRectWidth = 200;
        private float linkRectEntryHeight = 26;

        public static bool radarRectInitialized;
        internal static float RadarScreenSize = 360;
        internal static Rect RadarDisplayRect;

        internal static float BorderSize = 10;
        internal static float HeaderSize = 15;
        internal static float ControlsWidth = 125;
        internal static float Gap = 2;

        private Vector2 pingSize = new Vector2(16, 8);

        private GUIStyle radarTopStyle;

        private bool noData;

        private float guiInputTime;
        private float guiInputCooldown = 0.2f;

        //range increments
        //TODO:  Determine how to dynamically generate this list from the radar being used.
        public float[] baseIncrements = new float[] { 500, 2500, 5000, 10000, 20000, 40000, 100000, 250000, 500000, 750000, 1000000, 2000000 };

        public float[] rIncrements = new float[] { 500, 2500, 5000, 10000, 20000, 40000, 100000, 250000, 500000, 750000, 1000000, 2000000 };
        private int rangeIndex = 0;

        //lock cursor
        private bool showSelector;
        private Vector2 selectorPos = Vector2.zero;

        //data link
        public List<VesselRadarData> availableExternalVRDs;

        private Transform referenceTransform;

        // referenceTransform's position etc.
        Vector3 currPosition;
        Vector3 currForward;
        Vector3 currUp;
        Vector3 currRight;

        public MissileBase LastMissile;

        //bool boresightScan = false;

        //TargetSignatureData[] contacts = new TargetSignatureData[30];
        public List<RadarDisplayData> displayedTargets;
        public List<IRSTDisplayData> displayedIRTargets;
        public bool locked;
        public int activeLockedTargetIndex;
        public List<int> lockedTargetIndexes;

        public int numLockedTargets
        {
            get { return lockedTargetIndexes.Count; }
        }

        public bool hasLoadedExternalVRDs = false;

        private float lockedTargetsUpdateTime = -1f;

        private List<TargetSignatureData> lockedTargetList;

        public List<TargetSignatureData> GetLockedTargets()
        {
            if (lockedTargetsUpdateTime < Time.time)
            {
                lockedTargetList.Clear();
                for (int i = 0; i < lockedTargetIndexes.Count; i++)
                {
                    lockedTargetList.Add(displayedTargets[lockedTargetIndexes[i]].targetData);
                }

                lockedTargetsUpdateTime = Time.time;
            }
            
            return lockedTargetList;
        }

        public RadarDisplayData lockedTargetData
        {
            get { return displayedTargets[lockedTargetIndexes[activeLockedTargetIndex]]; }
        }

        public TargetSignatureData activeIRTarget(Vessel desiredTarget, MissileFire mf, bool ranging = false)
        {
            TargetSignatureData data;
            float targetMagnitude = 0;
            int brightestTarget = 0;
            for (int i = 0; i < displayedIRTargets.Count; i++)
            {
                IRSTDisplayData IRSTdata = displayedIRTargets[i];
                if (desiredTarget != null)
                {
                    if (IRSTdata.vessel == desiredTarget)
                    {
                        // If we need ranging data and no ranging data...
                        if (ranging && !IRSTdata.detectedByIRST.irstRanging) return TargetSignatureData.noTarget;

                        data = IRSTdata.targetData;
                        return data;
                    }
                }
                else
                {
                    if (IRSTdata.targetData.Team == mf.Team) continue;
                    if (ranging && !IRSTdata.detectedByIRST.irstRanging) continue;
                    if (IRSTdata.magnitude > targetMagnitude)
                    {
                        targetMagnitude = IRSTdata.magnitude;
                        brightestTarget = i;
                    }
                }
            }
            if (targetMagnitude > 0)
            {
                data = displayedIRTargets[brightestTarget].targetData;
                return data;
            }
            else
            {
                data = TargetSignatureData.noTarget;
                return data;
            }
        }

        // Technically not the *most* efficient way of doing this, having each of the functions
        // that use this perform the search and return the targetData directly would be reduce
        // the amount of copies (these are structs) but this is more readable and maintainable
        public int detectedRadarTargetIndex(Vessel desiredTarget, MissileFire mf) //passive sonar torpedoes, but could also be useful for LOAL missiles fired at detected but not locked targets, etc.
        {
            float targetMagnitude = 0;
            int brightestTarget = 0;
            for (int i = 0; i < displayedTargets.Count; i++)
            {
                if (desiredTarget != null)
                {
                    if (displayedTargets[i].vessel == desiredTarget)
                        return i;
                }
                else
                {
                    TargetSignatureData tData = displayedTargets[i].targetData;
                    if (tData.Team == mf.Team) continue;
                    if (tData.signalStrength > targetMagnitude)
                    {
                        targetMagnitude = tData.signalStrength;
                        brightestTarget = i;
                    }

                }
            }
            if (targetMagnitude > 0)
                return brightestTarget;
            else
                return -1;
        }

        public TargetSignatureData detectedRadarTarget(Vessel desiredTarget, MissileFire mf) //passive sonar torpedoes, but could also be useful for LOAL missiles fired at detected but not locked targets, etc.
        {
            int temp = detectedRadarTargetIndex(desiredTarget, mf);
            if (temp >= 0)
                return displayedTargets[temp].targetData;
            else
                return TargetSignatureData.noTarget;
        }

        public (TargetSignatureData, bool) detectedRadarTargetLock(Vessel desiredTarget, MissileFire mf) //passive sonar torpedoes, but could also be useful for LOAL missiles fired at detected but not locked targets, etc.
        {
            int temp = detectedRadarTargetIndex(desiredTarget, mf);
            if (temp >= 0)
            {
                // This reduces the copies produced of this struct
                RadarDisplayData t = displayedTargets[temp];
                return (t.targetData, t.locked);
            }
            else
                return (TargetSignatureData.noTarget, false);
        }

        // Used for INS stuff
        public (TargetSignatureData, bool) detectedRadarTargetGetRadar(Vessel desiredTarget, MissileFire mf) //passive sonar torpedoes, but could also be useful for LOAL missiles fired at detected but not locked targets, etc.
        {
            int temp = detectedRadarTargetIndex(desiredTarget, mf);
            if (temp >= 0)
            {
                RadarDisplayData t = displayedTargets[temp];
                TargetSignatureData tempData = t.targetData;
                if (!t.locked)
                {
                    tempData.lockedByRadar = t.detectedByRadar;
                }
                return (tempData, t.locked);
            }
            else
                return (TargetSignatureData.noTarget, false);
        }

        public TargetSignatureData detectedRadarTarget() //passive sonar torpedoes, but could also be useful for LOAL missiles fired at detected but not locked targets ,etc.
        {
            TargetSignatureData data;
            for (int i = 0; i < displayedTargets.Count; i++)
            {
                RadarDisplayData t  = displayedTargets[i];
                if (t.vessel == weaponManager.currentTarget)
                {
                    data = t.targetData;
                    return data;
                }
            }
            data = TargetSignatureData.noTarget;
            return data;
        }

        //turret slaving
        public bool slaveTurrets;

        private Vessel myVessel;

        public Vessel vessel
        {
            get { return myVessel; }
        }

        public void AddRadar(ModuleRadar mr)
        {
            if (availableRadars.Contains(mr))
            {
                return;
            }

            availableRadars.Add(mr);
            rCount = availableRadars.Count;
            //UpdateDataLinkCapability();
            linkCapabilityDirty = true;
            rangeCapabilityDirty = true;
        }

        public void RemoveRadar(ModuleRadar mr)
        {
            if (availableRadars.Remove(mr))
            {
                rCount = availableRadars.Count;
                RemoveDataFromRadar(mr);
                //UpdateDataLinkCapability();
                linkCapabilityDirty = true;
                rangeCapabilityDirty = true;
            }
        }

        public void AddIRST(ModuleIRST mi)
        {
            if (availableIRSTs.Contains(mi))
            {
                return;
            }

            availableIRSTs.Add(mi);
            iCount = availableIRSTs.Count;
            rangeCapabilityDirty = true;
        }

        public void RemoveIRST(ModuleIRST mi)
        {
            availableIRSTs.Remove(mi);
            iCount = availableIRSTs.Count;
            RemoveDataFromIRST(mi);
            rangeCapabilityDirty = true;
        }

        public bool linkCapabilityDirty;
        public bool rangeCapabilityDirty;
        public bool radarsReady;
        public bool queueLinks = false;

        private void Awake()
        {
            availableRadars = new List<ModuleRadar>();
            availableIRSTs = new List<ModuleIRST>();
            externalRadars = new List<ModuleRadar>();
            myVessel = GetComponent<Vessel>();
            lockedTargetIndexes = new List<int>();
            lockedTargetList = new List<TargetSignatureData>();
            availableExternalVRDs = new List<VesselRadarData>();

            radarTopStyle = new GUIStyle
            {
                normal = { textColor = ScopeArt.Phosphor(0.65f) },
                alignment = TextAnchor.UpperCenter,
                fontSize = 12
            };

            displayedTargets = new List<RadarDisplayData>();
            displayedIRTargets = new List<IRSTDisplayData>();
            externalVRDs = new List<VesselRadarData>();
            waitingForVessels = new List<string>();

            RadarDisplayRect = new Rect(BorderSize / 2, BorderSize / 2 + HeaderSize,
              RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE,
              RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE);

            if (!radarRectInitialized)
            {
                float width = RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE + BorderSize + ControlsWidth + Gap * 3;
                float height = RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE + BorderSize + HeaderSize;
                BDArmorySetup.WindowRectRadar = new Rect(BDArmorySetup.WindowRectRadar.x, BDArmorySetup.WindowRectRadar.y, width, height);
                radarRectInitialized = true;
            }
        }

        private void Start()
        {
            referenceTransform = (new GameObject()).transform;

            rangeIndex = rIncrements.Length - 6;

            // Default to 2 slots for jammers, this will dynamically resize itself
            // as needed.
            jammedPositions = new Vector2[2 * 4];
            jammedPositionsSize = 2;

            //determine configured physics ranges and add a radar range level for the highest range
            if (vessel.vesselRanges.flying.load > rIncrements[rIncrements.Length - 1])
            {
                rIncrements = new[] { 500, 2500, 5000, 10000, 20000, 40000, 100000, 250000, 500000, 750000, 1000000, vessel.vesselRanges.flying.load };
                rangeIndex--;
            }

            UpdateLockedTargets();
            using (var mf = VesselModuleRegistry.GetMissileFires(vessel).GetEnumerator())
                while (mf.MoveNext())
                {
                    if (mf.Current == null) continue;
                    mf.Current.vesselRadarData = this;
                }
            GameEvents.onVesselDestroy.Add(OnVesselDestroyed);
            GameEvents.onVesselCreate.Add(OnVesselDestroyed);
            MissileFire.OnChangeTeam += OnChangeTeam;
            GameEvents.onGameStateSave.Add(OnGameStateSave);
            GameEvents.onPartDestroyed.Add(PartDestroyed);

            if (!weaponManager)
            {
                weaponManager = vessel.ActiveController().WM;
            }
            StartCoroutine(StartupRoutine());
        }

        private IEnumerator StartupRoutine()
        {
            yield return new WaitWhile(() => !FlightGlobals.ready || (vessel is not null && (vessel.packed || !vessel.loaded)));
            yield return new WaitForFixedUpdate();
            radarsReady = true;
        }

        private void OnGameStateSave(ConfigNode n)
        {
            SaveExternalVRDVessels();
        }

        private void SaveExternalVRDVessels()
        {
            string linkedVesselID = "";

            List<VesselRadarData>.Enumerator v = externalVRDs.GetEnumerator();
            while (v.MoveNext())
            {
                if (v.Current == null) continue;
                linkedVesselID += v.Current.vessel.id + ",";
            }
            v.Dispose();

            List<string>.Enumerator id = waitingForVessels.GetEnumerator();
            while (id.MoveNext())
            {
                if (id.Current == null) continue;
                linkedVesselID += id.Current + ",";
            }
            id.Dispose();

            List<ModuleRadar>.Enumerator radar = availableRadars.GetEnumerator();
            while (radar.MoveNext())
            {
                if (radar.Current == null) continue;
                if (radar.Current.vessel != vessel) continue;
                radar.Current.linkedVesselID = linkedVesselID;
                return;
            }
            radar.Dispose();
        }

        private void OnDestroy()
        {
            GameEvents.onVesselDestroy.Remove(OnVesselDestroyed);
            GameEvents.onVesselCreate.Remove(OnVesselDestroyed);
            MissileFire.OnChangeTeam -= OnChangeTeam;
            GameEvents.onGameStateSave.Remove(OnGameStateSave);
            GameEvents.onPartDestroyed.Remove(PartDestroyed);

            referenceTransform = null;

            if (weaponManager)
            {
                if (slaveTurrets)
                {
                    weaponManager.slavingTurrets = false;
                    weaponManager.slavedPosition = Vector3.zero;
                    weaponManager.slavedTarget = TargetSignatureData.noTarget;
                }
            }
        }

        private void OnChangeTeam(MissileFire wm, BDTeam team)
        {
            if (!weaponManager || !wm) return;

            if (team != weaponManager.Team)
            {
                if (wm.vesselRadarData)
                {
                    UnlinkVRD(wm.vesselRadarData);
                }
            }
            else if (wm.vessel == vessel)
            {
                UnlinkAllExternalRadars();
            }

            RemoveDisconnectedRadars();
        }

        private void UpdateRangeCapability()
        {
            _maxRadarRange = 0;
            if (availableRadars.Count > 0)
            {
                _maxRadarRange = Mathf.Max(_maxRadarRange, MaxRadarRange());
            }
            else if (availableIRSTs.Count > 0)
            {
                _maxRadarRange = Mathf.Max(_maxRadarRange, MaxIRSTRange());
            }
            // Now rebuild range display array
            List<float> newArray = new List<float>();
            for (int x = 0; x < baseIncrements.Length; x++)
            {
                newArray.Add(baseIncrements[x]);
                if (_maxRadarRange <= baseIncrements[x])
                {
                    break;
                }
            }
            if (newArray.Count > 0)
            {
                rIncrements = newArray.ToArray();
                rangeIndex = Mathf.Clamp(rangeIndex, 0, rIncrements.Length - 1);
            }
        }

        public float MaxRadarRange()
        {
            float overallMaxRange = 0f;
            List<ModuleRadar>.Enumerator rad = availableRadars.GetEnumerator();
            while (rad.MoveNext())
            {
                if (rad.Current == null) continue;
                float maxRange = rad.Current.radarDetectionCurve.maxTime * 1000;
                if ((rad.Current.vessel != vessel && !externalRadars.Contains(rad.Current)) || !(maxRange > 0)) continue;
                if (maxRange > overallMaxRange) overallMaxRange = maxRange;
            }
            rad.Dispose();
            return overallMaxRange;
        }

        public float MaxIRSTRange()
        {
            float overallMaxRange = 0f;
            List<ModuleIRST>.Enumerator irst = availableIRSTs.GetEnumerator();
            while (irst.MoveNext())
            {
                if (irst.Current == null) continue;
                float maxRange = irst.Current.DetectionCurve.maxTime * 1000;
                if (irst.Current.vessel != vessel || !(maxRange > 0)) continue;
                if (maxRange > overallMaxRange) overallMaxRange = maxRange;
            }
            irst.Dispose();
            return overallMaxRange;
        }

        private void UpdateDataLinkCapability()
        {
            canReceiveRadarData = false;
            noData = true;
            List<ModuleRadar>.Enumerator rad = availableRadars.GetEnumerator();
            while (rad.MoveNext())
            {
                if (rad.Current == null) continue;
                if (rad.Current.vessel == vessel && rad.Current.canReceiveRadarData)
                {
                    canReceiveRadarData = true;
                }

                if (rad.Current.canScan)
                {
                    noData = false;
                }
            }
            rad.Dispose();

            if (!canReceiveRadarData)
            {
                UnlinkAllExternalRadars();
            }

            List<ModuleRadar>.Enumerator mr = availableRadars.GetEnumerator();
            while (mr.MoveNext())
            {
                if (mr.Current == null) continue;
                if (mr.Current.canScan)
                {
                    noData = false;
                }
            }
            mr.Dispose();
        }

        private void UpdateReferenceTransform()
        {
            if (!referenceTransform) return;
            // Previously the following line had !vessel.Landed which is a bit weird, the b-scope display
            // has no specific provisions for landed vessels, and thus the display would be completely
            // wrong for landed vessels
            if (radarCount == 1 && !availableRadars[0].omnidirectional)// && !vessel.Landed)
            {
                referenceTransform.position = vessel.CoM;
                // Rotate reference transform such that we're pointed forwards along the radar's forward
                // direction but rotated to negate any pitch, to create a stabilized perspective for radar displays
                referenceTransform.rotation =
                    Quaternion.LookRotation(availableRadars[0].currForward.ProjectOnPlanePreNormalized(vessel.upAxis).normalized, vessel.upAxis);
            }
            else
            {
                referenceTransform.position = vessel.CoM;
                referenceTransform.rotation = Quaternion.LookRotation(vessel.LandedOrSplashed ?
                    VectorUtils.GetNorthVector(vessel.CoM, vessel.mainBody) :
                    vessel.transform.up.ProjectOnPlanePreNormalized(vessel.upAxis), vessel.upAxis);
            }
            currPosition = vessel.CoM;
            currForward = referenceTransform.forward;
            currUp = referenceTransform.up;
            currRight = referenceTransform.right;
        }

        private void PartDestroyed(Part p)
        {
            RemoveDisconnectedRadars();
            UpdateLockedTargets();
            RefreshAvailableLinks();
        }

        private void OnVesselDestroyed(Vessel v)
        {
            RemoveDisconnectedRadars();
            UpdateLockedTargets();
            RefreshAvailableLinks();
        }

        private void RemoveDisconnectedRadars()
        {
            availableRadars.RemoveAll(r => r == null);
            List<ModuleRadar> radarsToRemove = new List<ModuleRadar>();
            List<ModuleRadar>.Enumerator radar = availableRadars.GetEnumerator();
            while (radar.MoveNext())
            {
                if (radar.Current == null) continue;
                if (!radar.Current.radarEnabled || (radar.Current.vessel != vessel && !externalRadars.Contains(radar.Current)))
                {
                    radarsToRemove.Add(radar.Current);
                }
                else if (!radar.Current.WeaponManager || (weaponManager && radar.Current.WeaponManager.Team != weaponManager.Team))
                {
                    radarsToRemove.Add(radar.Current);
                }
            }
            radar.Dispose();

            List<ModuleRadar>.Enumerator rrad = radarsToRemove.GetEnumerator();
            while (rrad.MoveNext())
            {
                if (rrad.Current == null) continue;
                rrad.Current.EnsureVesselRadarData();
                RemoveRadar(rrad.Current);
            }
            rrad.Dispose();
            rCount = availableRadars.Count;

            availableIRSTs.RemoveAll(r => r == null);
            List<ModuleIRST> IRSTsToRemove = new List<ModuleIRST>();
            List<ModuleIRST>.Enumerator irst = availableIRSTs.GetEnumerator();
            while (irst.MoveNext())
            {
                if (irst.Current == null) continue;
                if (!irst.Current.irstEnabled || irst.Current.vessel != vessel)
                {
                    IRSTsToRemove.Add(irst.Current);
                }
                else
                {
                    var irstWM = irst.Current.WeaponManager;
                    if (!irstWM || (weaponManager && irstWM.Team != weaponManager.Team))
                    {
                        IRSTsToRemove.Add(irst.Current);
                    }
                }
            }
            irst.Dispose();

            List<ModuleIRST>.Enumerator rirs = IRSTsToRemove.GetEnumerator();
            while (rirs.MoveNext())
            {
                if (rirs.Current == null) continue;
                RemoveIRST(rirs.Current);
            }
            rirs.Dispose();
            iCount = availableIRSTs.Count;
        }

        public void UpdateLockedTargets()
        {
            locked = false;

            lockedTargetIndexes.Clear(); // = new List<int>();

            for (int i = 0; i < displayedTargets.Count; i++)
            {
                RadarDisplayData t = displayedTargets[i];
                if (!t.vessel || !t.locked) continue;
                locked = true;
                lockedTargetIndexes.Add(i);
            }

            // Redo lockedTargetList
            lockedTargetsUpdateTime = -1f;

            activeLockedTargetIndex = locked
                ? Mathf.Clamp(activeLockedTargetIndex, 0, lockedTargetIndexes.Count - 1)
                : 0;
        }

        private bool UpdateSlaveData()
        {
            if (!weaponManager) //don't turn on auto-turret slaving when in manual control, let players use the button for that
            {
                return false;
            }
            if (!slaveTurrets || !locked)
            {
                weaponManager.slavedTarget = TargetSignatureData.noTarget;
                weaponManager.slavingTurrets = false;
                return false;
            }
            weaponManager.slavingTurrets = true;
            TargetSignatureData lockedTarget = lockedTargetData.targetData;
            ModuleRadar detectedRadar = lockedTargetData.detectedByRadar;
            weaponManager.slavedPosition = lockedTarget.predictedPositionWithChaffFactor(detectedRadar.radarChaffClutterFactor, detectedRadar._radarChaffNotchVFac, detectedRadar._radarChaffNotchRFac);
            weaponManager.slavedVelocity = lockedTarget.velocity;
            weaponManager.slavedAcceleration = lockedTarget.acceleration;
            weaponManager.slavedTarget = lockedTarget;
            return true;
            //This is only slaving turrets if there's a radar lock on the WM's guardTarget
            //no radar-guided gunnery for scan radars?
            //what about multiple turret multitarget tracking?
        }

        private void Update()
        {
            if (radarCount + irstCount > 0)
            {
                UpdateInputs();
            }
        }

        private void LateUpdate()
        {
            drawGUI = (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && !vessel.packed && rCount + iCount > 0 &&
                       vessel.isActiveVessel && BDArmorySetup.GAME_UI_ENABLED && !MapView.MapIsEnabled);
            if (drawGUI)
                UpdateGUIData();
            else
                RefreshDisplayWithoutGUI();
        }

        void FixedUpdate()
        {
            if (!vessel)
            {
                Destroy(this);
                return;
            }

            if (radarCount + irstCount > 0)
            {
                //vesselReferenceTransform.parent = linkedRadars[0].transform;
                UpdateReferenceTransform();

                CleanDisplayedContacts();

                if (!UpdateSlaveData() && slaveTurrets)
                {
                    UnslaveTurrets();
                }
            }
            else
            {
                if (slaveTurrets)
                {
                    UnslaveTurrets();
                }
            }

            if (linkCapabilityDirty)
            {
                UpdateDataLinkCapability();
                linkCapabilityDirty = false;
            }

            if (rangeCapabilityDirty)
            {
                UpdateRangeCapability();
                rangeCapabilityDirty = false;
            }

            if (queueLinks && canReceiveRadarData)
                LinkAllRadars();

            if (externalLockCapabilityDirty)
                CountExternalRadarMaxLocks();

            if (!vessel.loaded && (radarCount + irstCount == 0))
            {
                Destroy(this);
            }
        }

        public bool autoCycleLockOnFire = true;

        public void CycleActiveLock()
        {
            if (!locked) return;
            activeLockedTargetIndex++;
            if (activeLockedTargetIndex >= lockedTargetIndexes.Count)
            {
                activeLockedTargetIndex = 0;
            }

            lockedTargetData.detectedByRadar.SetActiveLock(lockedTargetData.targetData);

            //UpdateLockedTargets();
        }

        public void SetMaxRange()
        {
            rangeIndex = rIncrements.Length - 1;
            pingPositionsDirty = true;
            UpdateRWRRange();
        }

        public void IncreaseRange()
        {
            int origIndex = rangeIndex;
            rangeIndex = Mathf.Clamp(rangeIndex + 1, 0, rIncrements.Length - 1);
            if (origIndex == rangeIndex) return;
            pingPositionsDirty = true;
            UpdateRWRRange();
        }

        public void DecreaseRange()
        {
            int origIndex = rangeIndex;
            rangeIndex = Mathf.Clamp(rangeIndex - 1, 0, rIncrements.Length - 1);
            if (origIndex == rangeIndex) return;
            pingPositionsDirty = true;
            UpdateRWRRange();
        }

        /// <summary>
        /// Update the radar range also on the rwr display
        /// </summary>
        private void UpdateRWRRange()
        {
            using (var rwr = VesselModuleRegistry.GetModules<RadarWarningReceiver>(vessel).GetEnumerator())
                while (rwr.MoveNext())
                {
                    if (rwr.Current == null) continue;
                    rwr.Current.rwrDisplayRange = rIncrements[rangeIndex];
                }
        }

        private bool TryLockTarget(RadarDisplayData radarTarget, bool priorityLock = false)
        {
            if (radarTarget.locked) return false;

            ModuleRadar lockingRadar = null;
            //first try using the last radar to detect that target
            bool acquiredLock = false;
            if (radarTarget.detectedByRadar)
            {
                if (CheckRadarForLock(radarTarget.detectedByRadar, radarTarget, priorityLock))
                {
                    lockingRadar = radarTarget.detectedByRadar;
                    acquiredLock = lockingRadar.TryLockTarget(radarTarget.targetData.predictedPosition, radarTarget.vessel);
                }
            }
            if (!acquiredLock) //locks exceeded/target off scope, test if remaining radars have available locks & coveravge
            {
                using (List<ModuleRadar>.Enumerator radar = availableRadars.GetEnumerator())
                    while (radar.MoveNext())
                    {
                        if (radar.Current == null) continue;
                        // If the radar is external
                        if (!CheckRadarForLock(radar.Current, radarTarget, priorityLock)) continue;
                        lockingRadar = radar.Current;
                        if (lockingRadar.TryLockTarget(radarTarget.targetData.predictedPosition, radarTarget.vessel))
                        {
                            acquiredLock = true;
                            break;
                        }
                    }
            }
            if (lockingRadar != null)
            {
                return acquiredLock;
            }
            //UpdateLockedTargets();
            StartCoroutine(UpdateLocksAfterFrame());
            return false;
        }

        private IEnumerator UpdateLocksAfterFrame()
        {
            yield return new WaitForFixedUpdate();
            UpdateLockedTargets();
        }

        public void TryLockTarget(Vector3 worldPosition, bool priorityLock = false)
        {
            List<RadarDisplayData>.Enumerator displayData = displayedTargets.GetEnumerator();
            while (displayData.MoveNext())
            {
                if (!(Vector3.SqrMagnitude(worldPosition - displayData.Current.targetData.predictedPosition) <
                      40 * 40)) continue;
                TryLockTarget(displayData.Current, priorityLock);
                return;
            }
            displayData.Dispose();
            return;
        }

        public bool TryLockTarget(Vessel v, bool priorityLock = false)
        {
            if (v == null || v.packed) return false;

            using (List<RadarDisplayData>.Enumerator displayData = displayedTargets.GetEnumerator())
                while (displayData.MoveNext())
                {
                    if (v == displayData.Current.vessel)
                    {
                        return TryLockTarget(displayData.Current);
                    }
                }

            RadarDisplayData newData = new RadarDisplayData
            {
                vessel = v,
                detectedByRadar = null,
                targetData = new TargetSignatureData(v, 999)
            };

            return TryLockTarget(newData);

            //return false;
        }

        private bool CheckRadarForLock(ModuleRadar radar, RadarDisplayData radarTarget, bool priorityLock)
        {
            // Technically all instances of this are now gated by a null check so this is no longer necessary
            //if (!radar) return false;

            if (!radar.canLock) return false;

            bool guardModeActive = weaponManager && weaponManager.guardMode;

            if (!guardModeActive && (radar.locked && (radar.currentLocks == radar.maxLocks))) return false;

            // Ensure the radar's referenceTransform and related vectors are all updated...
            radar.UpdateReferenceTransform();

            TargetSignatureData tData = radarTarget.targetData;
            Vector3 relativePos = tData.predictedPosition - radar.currPosition;
            // Convert from m to km for the radar FloatCurves
            float dist = relativePos.magnitude * 0.001f;

            return
            (
                RadarUtils.RadarCanDetect(radar, tData.signalStrength, dist)
                && tData.signalStrength >= radar.radarLockTrackCurve.Evaluate(dist)
                && (radar.CheckFOV(tData.predictedPosition)
                && (!guardModeActive || // If not in Guard Mode
                    !radar.locked || // Or the radar isn't locked
                    (priorityLock && !radar.lockedTarget.targetInfo.isMissile) || // Or we're a priority lock
                    weaponManager.GetMissilesAway(radar.lockedTarget.targetInfo).numSARH == 0 || // Or we're not guiding a missile
                    VectorUtils.Angle(relativePos, radar.lockedTarget.position - radar.currPosition) < radar.multiLockFOV * 0.5f)) // Or we're within the multiLockFOV
            );
        }

        private void DisableAllRadars()
        {
            //rCount = 0;
            UnlinkAllExternalRadars();

            var radars = VesselModuleRegistry.GetModules<ModuleRadar>(vessel);
            if (radars != null)
            {
                using (var radar = radars.GetEnumerator())
                    while (radar.MoveNext())
                    {
                        if (radar.Current == null) continue;
                        radar.Current.DisableRadar();
                    }
            }
            var irsts = VesselModuleRegistry.GetModules<ModuleIRST>(vessel);
            if (irsts != null)
            {
                using (var irst = irsts.GetEnumerator())
                    while (irst.MoveNext())
                    {
                        if (irst.Current == null) continue;
                        irst.Current.DisableIRST();
                    }
            }
        }

        public float GetCrankFOV()
        {
            // Get max FOV of radars onboard vessel, or the minimum FOV radars with target locks

            float fov = 0f;
            var radars = VesselModuleRegistry.GetModules<ModuleRadar>(vessel);
            if (radars != null)
            {
                using (var radar = radars.GetEnumerator())
                    while (radar.MoveNext())
                    {
                        if (radar.Current == null) continue;
                        if (radar.Current.omnidirectional) return 360f;
                        // TODO: Account for radar orientation, as it is right now we just take the minimum azimuth limit!
                        fov = Mathf.Max(fov, radar.Current.radarMinMaxAzLimits[0]);
                    }
            }
            for (int i = 0; i < lockedTargetIndexes.Count; i++)
            {
                // TODO: Account for radar orientation, as it is right now we just take the minimum azimuth limit!
                fov = Mathf.Min(fov, displayedTargets[lockedTargetIndexes[i]].detectedByRadar.radarMinMaxAzLimits[0]);
            }

            return fov;
        }
        /// <summary>
        /// Slew any Targeting Cameras to the radarlocked position
        /// </summary>
        public void SlaveTurrets()
        {
            var targetingCameras = VesselModuleRegistry.GetModules<ModuleTargetingCamera>(vessel);
            if (targetingCameras != null)
            {
                using (var mtc = targetingCameras.GetEnumerator())
                    while (mtc.MoveNext())
                    {
                        if (mtc.Current == null) continue;
                        mtc.Current.slaveTurrets = false;
                    }
                slaveTurrets = true;
            }
        }

        public void UnslaveTurrets()
        {
            var targetingCameras = VesselModuleRegistry.GetModules<ModuleTargetingCamera>(vessel);
            if (targetingCameras != null)
            {
                using (var mtc = targetingCameras.GetEnumerator())
                    while (mtc.MoveNext())
                    {
                        if (mtc.Current == null) continue;
                        mtc.Current.slaveTurrets = false;
                    }
            }

            slaveTurrets = false;

            if (weaponManager)
            {
                weaponManager.slavingTurrets = false;
            }
            weaponManager.slavedPosition = Vector3.zero;
            weaponManager.slavedTarget = TargetSignatureData.noTarget; //reset and null these so hitting the slave target button on a weapon later doesn't lock it to a legacy position/target
        }

        private void OnGUI()
        {
            if (!drawGUI) return;

            for (int i = 0; i < lockedTargetIndexes.Count; i++)
            {
                if (BDArmorySettings.DEBUG_TELEMETRY || BDArmorySettings.DEBUG_RADAR)
                {
                    string label = string.Empty;
                    if (i == activeLockedTargetIndex)
                    {
                        label += "Active: ";
                    }
                    if (!displayedTargets[lockedTargetIndexes[i]].vessel)
                    {
                        label += "data with no vessel";
                    }
                    else
                    {
                        label += displayedTargets[lockedTargetIndexes[i]].vessel.vesselName;
                    }
                    GUI.Label(new Rect(20, 120 + (i * 16), 800, 26), label);
                }

                TargetSignatureData lockedTarget = displayedTargets[lockedTargetIndexes[i]].targetData;
                if (i == activeLockedTargetIndex)
                {
                    if (weaponManager && weaponManager.Team.IsFriendly(lockedTarget.Team))
                    {
                        GUIUtils.DrawTextureOnWorldPos(lockedTarget.predictedPosition,
                            BDArmorySetup.Instance.crossedGreenSquare, new Vector2(20, 20), 0);
                    }
                    else
                    {
                        GUIUtils.DrawTextureOnWorldPos(lockedTarget.predictedPosition,
                            BDArmorySetup.Instance.openGreenSquare, new Vector2(20, 20), 0);
                    }
                }
                else
                {
                    GUIUtils.DrawTextureOnWorldPos(lockedTarget.predictedPosition,
                        BDArmorySetup.Instance.greenDiamondTexture, new Vector2(17, 17), 0);
                }
            }

            if (resizingWindow && Event.current.type == EventType.MouseUp) { resizingWindow = false; }
            const string windowTitle = "Radar";
            if (BDArmorySettings.UI_SCALE_ACTUAL != 1) GUIUtility.ScaleAroundPivot(BDArmorySettings.UI_SCALE_ACTUAL * Vector2.one, BDArmorySetup.WindowRectRadar.position);
            BDArmorySetup.WindowRectRadar = GUI.Window(524141, BDArmorySetup.WindowRectRadar, WindowRadar, windowTitle, BDArmorySetup.BDGuiSkin.window);
            GUIUtils.UseMouseEventInRect(BDArmorySetup.WindowRectRadar);

            if (linkWindowOpen && canReceiveRadarData)
            {
                linkWindowRect = new Rect(BDArmorySetup.WindowRectRadar.x - linkRectWidth, BDArmorySetup.WindowRectRadar.y + 16, linkRectWidth,
                    16 + (numberOfAvailableLinks * linkRectEntryHeight));
                LinkRadarWindow();

                GUIUtils.UseMouseEventInRect(linkWindowRect);
            }
        }

        //GUI
        //=============================================
        private void WindowRadar(int windowID)
        {
            if (GUI.Button(new Rect(BDArmorySetup.WindowRectRadar.width - 18, 2, 16, 16), " X", BDArmorySetup.CloseButtonStyleDark)) //this won't actually close radar GUI, just turn all radars off. This intentional?
            {
                DisableAllRadars();
                BDArmorySetup.SaveConfig();
                return;
            }
            if (!referenceTransform) return;

            if (availableRadars.Count + availableIRSTs.Count == 0) return;
            //==============================
            GUI.BeginGroup(RadarDisplayRect);

            //bool omnidirectionalDisplay = (radarCount == 1 && linkedRadars[0].omnidirectional);
            //bool linked = (radarCount > 1);
            Rect scanRect = new Rect(0, 0, RadarDisplayRect.width, RadarDisplayRect.height);

            //Vector3 refForward = referenceTransform.forward;
            //Vector3 refUp = referenceTransform.up;
            //Vector3 refRight = referenceTransform.right;
            //Vector3 refPos = referenceTransform.position;

            // TODO: Overall many of these things could be cached, primarily the FoV lines, especially if we're gonna accurately represent
            // them in 3D space. A lot of these calculations do not need to be repeated unless the radars are moving around on the vessel
            // (which is entirely plausible, given some people like sticking them on gimbals, so maybe we check if the radar's relative
            // orientation has changed?). At the very least the FoV vectors (we'd only a single vector from one corner, and then reflect /
            // rotate that over to the other side, relative to the offset centerline) could be calculated as local vectors relative to the
            // radar transform, though we'd have to then account for the offset for any radar with an asymmetric scan-zone, though that 
            // would be easy enough with a local quaternion.
            // Actually we calculate the radar's `Quaternion.LookRotation` for the ReferenceTransform anyways, so it's probably best to
            // just have vectors to 2 opposing corners of the FoV pyramid, save the quaternion, and then rotate them using it.

            var canvas = ImguiScopeCanvas.Instance;
            float scale = BDArmorySettings.RADAR_WINDOW_SCALE;
            var display = new ScopeRect(0, 0, scanRect.width, scanRect.height);
            if (guiDispOmni)
            {
                ScopeComposer.Background(canvas, ScopeImage.OmniScope, display);
                if (vessel.LandedOrSplashed) GUI.Label(scanRect, "N", radarTopStyle);

                // TODO: the field of view lines don't take the radar's orientation in 3D space or asymmetric fields of view into account.
                for (int i = 0; i < guiRCount; i++)
                {
                    if (!float.IsNaN(radarCurrAngleArr[i])) ScopeComposer.Sweep(canvas, display, radarCurrAngleArr[i], false, guiFillScanR);
                    if (float.IsNaN(radarFOVAngleArr[i])) continue;
                    ScopeComposer.FieldOfViewEdge(canvas, display, dAngle + radarFOVAngleArr[i] + radarAngleArr[i], false);
                    ScopeComposer.FieldOfViewEdge(canvas, display, dAngle - radarFOVAngleArr[i] + radarAngleArr[i], false);
                }
                for (int i = guiRCount; i < guiSCount; i++)
                {
                    ScopeComposer.Sweep(canvas, display, radarCurrAngleArr[i], true, guiFillScanI);
                    if (float.IsNaN(radarFOVAngleArr[i])) continue;
                    ScopeComposer.FieldOfViewEdge(canvas, display, dAngle + radarFOVAngleArr[i] + radarAngleArr[i], true);
                    ScopeComposer.FieldOfViewEdge(canvas, display, dAngle - radarFOVAngleArr[i] + radarAngleArr[i], true);
                }
                ScopeComposer.OwnShip(canvas, display, dAngle, scale);
            }
            else
            {
                ScopeComposer.Background(canvas, ScopeImage.SectorScope, display);
                for (int i = 0; i < guiRCount; i++)
                {
                    var antenna = new ScopePoint(scanPosArr[i].x, scanPosArr[i].y);
                    if (float.IsNaN(radarCurrAngleArr[i])) ScopeComposer.ScanMarker(canvas, display, antenna, null, null, false, scale);
                    else ScopeComposer.ScanMarker(canvas, display, antenna, new ScopePoint(leftPosArr[i].x, leftPosArr[i].y), new ScopePoint(rightPosArr[i].x, rightPosArr[i].y), false, scale);
                }
                for (int i = guiRCount; i < guiSCount; i++)
                    ScopeComposer.ScanMarker(canvas, display, new ScopePoint(scanPosArr[i].x, scanPosArr[i].y), null, null, true, scale);
            }

            if (!vessel.Landed) ScopeComposer.Horizon(canvas, display, rollAngle);

            if (noData)
            {
                if (iCount > 0) DrawDisplayedIRContacts();
                else ScopeComposer.NoData(canvas, display, scale);
            }
            else
            {
                DrawDisplayedContacts();
                if (iCount > 0) DrawDisplayedIRContacts();
            }
            pingPositionsDirty = false;

            if (showSelector) ScopeComposer.Cursor(canvas, new ScopePoint(selectorPos.x, selectorPos.y), scale);

            if (LastMissile && LastMissile.TargetAcquired)
                ScopeComposer.MissileData(canvas, display, LastMissile.GetShortName(), "T-" + LastMissile.TimeToImpact.ToString("0"),
                    LastMissile.ActiveRadar && Mathf.Round(Time.time * 3) % 2 == 0 ? "ACTIVE" : null, scale);

            ScopeComposer.Mode(canvas, display, RadarMode(), slaveTurrets ? "TURRETS" : null, scale);
            if (dispRange) DisplayRange(canvas, display, scale); //don't change dist for non-range capable IRSTs

            GUI.EndGroup();

            // Show Control Button group
            DisplayRadarControls();

            // Resizing code block.
            RADARresizeRect = new Rect(BDArmorySetup.WindowRectRadar.width - 18, BDArmorySetup.WindowRectRadar.height - 19, 16, 16);
            GUI.DrawTexture(RADARresizeRect, GUIUtils.resizeTexture, ScaleMode.StretchToFill, true);
            if (Event.current.type == EventType.MouseDown && RADARresizeRect.Contains(Event.current.mousePosition))
            {
                resizingWindow = true;
            }
            else GUIUtils.DragWindow();

            if (Event.current.type == EventType.Repaint && resizingWindow)
            {
                if (Mouse.delta.x != 0 || Mouse.delta.y != 0)
                {
                    float diff = (Mathf.Abs(Mouse.delta.x) > Mathf.Abs(Mouse.delta.y) ? Mouse.delta.x : Mouse.delta.y) / BDArmorySettings.UI_SCALE_ACTUAL;
                    BDArmorySettings.RADAR_WINDOW_SCALE = Mathf.Clamp(BDArmorySettings.RADAR_WINDOW_SCALE + diff / RadarScreenSize, BDArmorySettings.RADAR_WINDOW_SCALE_MIN, BDArmorySettings.RADAR_WINDOW_SCALE_MAX);
                    BDArmorySetup.ResizeRadarWindow(BDArmorySettings.RADAR_WINDOW_SCALE);
                }
            }
            // End Resizing code.

            GUIUtils.RepositionWindow(ref BDArmorySetup.WindowRectRadar);
        }

        // UPDATE GUI DATA
        float dAngle;
        float[] radarAngleArr;
        float[] radarCurrAngleArr;
        float[] radarFOVAngleArr;
        float rollAngle;
        float directionalFieldOfView;
        int guiRCount = -1; // Saves the number of radars in the GUI data arrays, that way the
        // cutoff between radars and IRSTs is known, This is used because the GUI data arrays
        // don't match in length with rCount and iCount, having removed external radars and
        // null radars.
        int guiSCount = -1; // Total number of sensors in the arrays! This is specifically used
        // to avoid downsizing the arrays
        int arrSize = -1;
        Vector2[] scanPosArr;
        Vector2[] leftPosArr;
        Vector2[] rightPosArr;
        bool guiDispOmni = false; // This saves whether or not to display in omni, this is useful
        // to know to determine whether or not we need to recalculate the ping positions
        bool guiFillScanR = false; // Used for scanRect conditional
        bool guiFillScanI = false; // Used for scanRect conditional
        bool dispRange = false;

        private void UpdateGUIData()
        {
            int currIndex = 0;

            dispRange = availableRadars.Count > 0;

            int totCount = rCount + iCount; // Upper bound. Subtracting the linked radars undercounts when a linked radar has since been switched off or unlinked, and the loops below then run off the arrays.

            // If our radarData arrays are smaller than the total count of on-board sensors
            // then we re-size the arrays.
            // TODO: Maybe this could be something pre-counted in OnStart() to reduce
            // the number of times the arrays have to be re-sized, mostly for human players
            // since Guard Mode will enable all radars/IRSTs in one shot.
            if (arrSize < totCount)
            {
                radarAngleArr = new float[totCount];
                radarCurrAngleArr = new float[totCount];
                radarFOVAngleArr = new float[totCount];
                scanPosArr = new Vector2[totCount];
                leftPosArr = new Vector2[totCount];
                rightPosArr = new Vector2[totCount];
                arrSize = totCount;
            }

            // If we've flipped from an omni-display to a b-scope or from a b-scope to an omni
            // we need to recalculate all the ping positions!
            if (guiDispOmni != omniDisplay)
                pingPositionsDirty = true;

            if (omniDisplay)
            {
                guiDispOmni = true;

                //my ship direction icon
                Vector3 projectedVesselFwd = vessel.ReferenceTransform.up.ProjectOnPlanePreNormalized(currUp).normalized;
                Vector3 left = Vector3.Cross(currUp, projectedVesselFwd);
                dAngle = VectorUtils.AnglePreNormalized(projectedVesselFwd, referenceTransform.forward);
                if (referenceTransform.InverseTransformVector(vessel.ReferenceTransform.up).x < 0)
                {
                    dAngle = -dAngle;
                }

                Vector3 north;
                if (!vessel.Landed)
                    north = VectorUtils.GetNorthVector(currPosition, vessel.mainBody);
                else
                    north = Vector3.zero;

                //if (BDArmorySettings.DEBUG_RADAR)
                //    Debug.Log($"[BDArmory.UpdateRadarGUI]: Vessel: {vessel.vesselName}, with UUID: {vessel.id} beginning omni radar GUI update.");

                for (int i = 0; i < rCount; i++)
                {
                    if (availableRadars[i] == null || availableRadars[i].gameObject == null) continue;

                    //if (BDArmorySettings.DEBUG_RADAR)
                    //    Debug.Log($"[BDArmory.UpdateRadarGUI][Omni]: Vessel: {vessel.id} radar: {availableRadars[i].name} on vessel: {availableRadars[i].vessel.id} being processed. Skip cond: {!availableRadars[i].canScan || availableRadars[i].vessel != vessel}, index: {i}/{rCount}, currIndex: {currIndex}, arrSize: {arrSize}");

                    if (!availableRadars[i].canScan || availableRadars[i].vessel != vessel) continue;

                    float currentAngle = availableRadars[i].currentAngle;

                    availableRadars[i].UpdateDisplayTransform();
                    float radarAngle = VectorUtils.GetAngleOnPlane(availableRadars[i].currDisplayForward, projectedVesselFwd, left);

                    // TODO: This does not account for the true 3D orientation of the radar! Technically, we could just
                    // say it is only meant to represent the current status of the sweep, but even then we'd still have
                    // to account for the true 3D FoV limits of the radar.
                    radarAngleArr[currIndex] = radarAngle;

                    if (!availableRadars[i].locked || availableRadars[i].canTrackWhileScan)
                    {
                        if (!availableRadars[i].omnidirectional)
                        {
                            currentAngle += radarAngle + dAngle;
                        }
                        else if (!vessel.Landed)
                        {
                            float angleFromNorth = VectorUtils.GetAngleOnPlane(projectedVesselFwd, north,
                                Vector3.Cross(north, vessel.upAxis));
                            currentAngle += angleFromNorth;
                        }

                        radarCurrAngleArr[currIndex] = currentAngle;

                        guiFillScanR = availableRadars[i].omnidirectional && radarCount == 1;
                    }

                    // TODO: FIX THIS, currently doesn't take radar orientation in 3D space into account
                    // also doesn't take into account asymmetric FOVs!
                    //if linked and directional, draw FOV lines
                    if (availableRadars[i].omnidirectional)
                    {
                        radarFOVAngleArr[currIndex] = float.NaN;
                        currIndex++;
                        continue;
                    }

                    radarFOVAngleArr[currIndex] = availableRadars[i].radarAzFOV * 0.5f;

                    currIndex++;
                }

                guiRCount = currIndex;

                for (int i = 0; i < iCount; i++)
                {
                    if (availableIRSTs[i] == null || availableIRSTs[i].gameObject == null) continue;
                    if (!availableIRSTs[i].canScan || availableIRSTs[i].vessel != vessel) continue;

                    float currentAngle = availableIRSTs[i].currentAngle;

                    float radarAngle = VectorUtils.GetAngleOnPlane(availableIRSTs[i].irstForward, projectedVesselFwd, left);

                    if (!availableIRSTs[i].omnidirectional)
                    {
                        currentAngle += radarAngle + dAngle;
                    }
                    else if (!vessel.Landed)
                    {
                        float angleFromNorth = VectorUtils.GetAngleOnPlane(projectedVesselFwd, north,
                            Vector3.Cross(north, vessel.upAxis));
                        currentAngle += angleFromNorth;
                    }

                    radarAngleArr[currIndex] = radarAngle;
                    radarCurrAngleArr[currIndex] = currentAngle;
                    guiFillScanI = availableIRSTs[i].omnidirectional && irstCount == 1;

                    if (!dispRange && availableIRSTs[i].irstRanging)
                        dispRange = true;

                    //if linked and directional, draw FOV lines
                    if (availableIRSTs[i].omnidirectional)
                    {
                        radarFOVAngleArr[currIndex] = float.NaN;
                        currIndex++;
                        continue;
                    }
                    radarFOVAngleArr[currIndex] = availableIRSTs[i].directionalFieldOfView * 0.5f;
                    currIndex++;
                }

                guiSCount = currIndex;
            }
            else
            {
                guiDispOmni = false;

                directionalFieldOfView = (availableRadars.Count > 0) ? (availableRadars[0].radarMinMaxAzLimits[1]) : 0.5f * availableIRSTs[0].directionalFieldOfView;
                Rect scanRect = new Rect(0, 0, RadarDisplayRect.width, RadarDisplayRect.height);

                //if (BDArmorySettings.DEBUG_RADAR)
                //    Debug.Log($"[BDArmory.UpdateRadarGUI]: Vessel: {vessel.vesselName}, with UUID: {vessel.id} beginning non-omni radar GUI update.");

                for (int i = 0; i < rCount; i++)
                {
                    if (availableRadars[i] == null || availableRadars[i].gameObject == null) continue;

                    //if (BDArmorySettings.DEBUG_RADAR)
                    //    Debug.Log($"[BDArmory.UpdateRadarGUI][Non-Omni]: Vessel: {vessel.id} radar: {availableRadars[i].name} on vessel: {availableRadars[i].vessel.id} being processed. Skip cond: {!availableRadars[i].canScan || availableRadars[i].vessel != vessel}, index: {i}/{rCount}, currIndex: {currIndex}, arrSize: {arrSize}");

                    if (!availableRadars[i].canScan || availableRadars[i].vessel != vessel) continue;
                    bool islocked = availableRadars[i].locked;
                    //float lockScanAngle = linkedRadars[i].lockScanAngle;
                    float currentAngle = availableRadars[i].currentAngle;
                    float indicatorAngle = currentAngle; //locked ? lockScanAngle : currentAngle;
                    scanPosArr[currIndex] =
                        RadarUtils.WorldToRadarRadial(
                            currPosition +
                            (Quaternion.AngleAxis(indicatorAngle, currUp) * currForward),
                            referenceTransform, scanRect, 5000, directionalFieldOfView, true);

                    if (!islocked || !availableRadars[i].canTrackWhileScan)
                    {
                        radarCurrAngleArr[currIndex] = float.NaN;
                        currIndex++;
                        continue;
                    }
                    radarCurrAngleArr[currIndex] = 0f;
                    leftPosArr[currIndex] =
                        RadarUtils.WorldToRadarRadial(
                            currPosition +
                            (Quaternion.AngleAxis(availableRadars[i].leftLimit, currUp) *
                             currForward), referenceTransform, scanRect, 5000,
                            directionalFieldOfView, true);
                    rightPosArr[currIndex] =
                        RadarUtils.WorldToRadarRadial(
                            currPosition +
                            (Quaternion.AngleAxis(availableRadars[i].rightLimit, currUp) *
                             currForward), referenceTransform, scanRect, 5000,
                            directionalFieldOfView, true);

                    currIndex++;
                }

                guiRCount = currIndex;

                for (int i = 0; i < iCount; i++)
                {
                    if (availableIRSTs[i] == null || availableIRSTs[i].gameObject == null) continue;
                    if (!availableIRSTs[i].canScan || availableIRSTs[i].vessel != vessel) continue;
                    float currentAngle = availableIRSTs[i].currentAngle;
                    float indicatorAngle = currentAngle; //locked ? lockScanAngle : currentAngle;
                    scanPosArr[currIndex] =
                        RadarUtils.WorldToRadarRadial(
                            currPosition +
                            (Quaternion.AngleAxis(indicatorAngle, currUp) * currForward),
                            referenceTransform, scanRect, 5000, directionalFieldOfView, true);

                    if (!dispRange && availableIRSTs[i].irstRanging)
                        dispRange = true;

                    currIndex++;
                }

                guiSCount = currIndex;
            }

            //roll indicator
            if (!vessel.Landed)
            {
                Vector3 localUp = vessel.ReferenceTransform.InverseTransformDirection(vessel.upAxis);
                localUp = localUp.ProjectOnPlanePreNormalized(Vector3.up).normalized;
                rollAngle = -VectorUtils.GetAngleOnPlane(localUp, -Vector3.forward, Vector3.right);
            }

            if (!noData)// && iCount == 0)
                UpdateDisplayedContacts();
        }

        /// <summary>The mode legend: SCAN or BORE while searching, LOCK n/m with locks held; IRST in front when there is no radar.</summary>
        private string RadarMode()
        {
            bool boresight = availableRadars.Count > 0 ? availableRadars[0].boresightScan : availableIRSTs.Count > 0 && availableIRSTs[0].boresightScan;
            string mode = locked && lockedTargetIndexes.Count > 0 ? $"LOCK {activeLockedTargetIndex + 1}/{lockedTargetIndexes.Count}" : boresight ? "BORE" : "SCAN";
            return availableRadars.Count == 0 && availableIRSTs.Count > 0 ? "IRST " + mode : mode;
        }

        private void DisplayRange(IScopeCanvas canvas, ScopeRect display, float scale)
        {
            // Range Display and control
            if (rangeIndex >= rIncrements.Length) DecreaseRange();
            ScopeComposer.RangeLayout(display, scale, out var minus, out var label, out var plus);
            if (GUI.Button(new Rect(minus.x, minus.y, minus.w, minus.h), "-", BDArmorySetup.BDGuiSkin.button))
            {
                DecreaseRange();
            }
            ScopeComposer.RangeReadout(canvas, label, rIncrements[rangeIndex] / 1000, scale);
            if (GUI.Button(new Rect(plus.x, plus.y, plus.w, plus.h), "+", BDArmorySetup.BDGuiSkin.button))
            {
                IncreaseRange();
            }
        }
        static GUIStyle buttonOnStyle;
        /// <summary>A button shown on (Data Link open, turrets slaved): the skin's toggled look.</summary>
        static GUIStyle ButtonOnStyle
        {
            get
            {
                var button = BDArmorySetup.BDGuiSkin.button;
                if (buttonOnStyle == null || buttonOnStyle.normal.background != button.onNormal.background)
                    buttonOnStyle = new GUIStyle(button) { normal = button.onNormal, hover = button.onHover, active = button.onActive };
                return buttonOnStyle;
            }
        }

        private void DisplayRadarControls()
        {
            float buttonHeight = 25;
            float line = HeaderSize + BorderSize / 2;
            float startX = BorderSize + RadarDisplayRect.width;

            //Set up button positions depending on window scale.
            Rect dataLinkRect = new Rect(startX, line, ControlsWidth, buttonHeight);
            line += buttonHeight + Gap;
            Rect lockModeCycleRect = new Rect(startX, line, ControlsWidth, buttonHeight);
            line += buttonHeight + Gap;
            Rect slaveRect = new Rect(startX, line, ControlsWidth, buttonHeight);
            line += buttonHeight + Gap;
            Rect unlockRect = new Rect(startX, line, ControlsWidth, buttonHeight);
            line += buttonHeight + Gap;
            Rect unlockAllRect = new Rect(startX, line, ControlsWidth, buttonHeight);

            if (canReceiveRadarData)
            {
                if (GUI.Button(dataLinkRect, "Data Link", linkWindowOpen ? ButtonOnStyle : BDArmorySetup.BDGuiSkin.button))
                {
                    if (linkWindowOpen)
                    {
                        CloseLinkRadarWindow();
                    }
                    else
                    {
                        OpenLinkRadarWindow();
                    }
                }
            }
            else
            {
                Color oCol = GUI.color;
                GUI.color = new Color(1, 1, 1, 0.35f);
                GUI.Box(dataLinkRect, "Link N/A", BDArmorySetup.BDGuiSkin.button);
                GUI.color = oCol;
            }

            if (locked)
            {
                if (GUI.Button(lockModeCycleRect, "Cycle Lock", BDArmorySetup.BDGuiSkin.button))
                {
                    CycleActiveLock();
                }
            }
            else if (!omniDisplay) //SCAN MODE SELECTOR
            {
                if (!locked)
                {
                    string boresightToggle = (availableRadars.Count > 0 ? availableRadars[0].boresightScan : availableIRSTs[0].boresightScan) ? "Scan" : "Boresight";
                    if (GUI.Button(lockModeCycleRect, boresightToggle, BDArmorySetup.BDGuiSkin.button))
                    {
                        if (availableRadars.Count > 0) availableRadars[0].boresightScan = !availableRadars[0].boresightScan;
                        if (availableIRSTs.Count > 0) availableIRSTs[0].boresightScan = !availableIRSTs[0].boresightScan;
                    }
                }
            }

            //slave button
            if (GUI.Button(slaveRect, slaveTurrets ? "Unslave Turrets" : "Slave Turrets",
                slaveTurrets ? ButtonOnStyle : BDArmorySetup.BDGuiSkin.button))
            {
                if (slaveTurrets)
                {
                    UnslaveTurrets();
                }
                else
                {
                    SlaveTurrets();
                }
            }

            //unlocking
            if (locked)
            {
                if (GUI.Button(unlockRect, "Unlock", BDArmorySetup.BDGuiSkin.button))
                {
                    UnlockCurrentTarget();
                }

                if (GUI.Button(unlockAllRect, "Unlock All", BDArmorySetup.BDGuiSkin.button))
                {
                    UnlockAllTargets();
                }
            }
        }

        private void LinkRadarWindow()
        {
            GUI.Box(linkWindowRect, string.Empty, GUI.skin.window);

            numberOfAvailableLinks = 0;

            GUI.BeginGroup(linkWindowRect);

            if (GUI.Button(new Rect(8, 8, 100, linkRectEntryHeight), "Refresh", GUI.skin.button))
            {
                RefreshAvailableLinks();
            }
            numberOfAvailableLinks += 1.25f;

            List<VesselRadarData>.Enumerator v = availableExternalVRDs.GetEnumerator();
            while (v.MoveNext())
            {
                if (v.Current == null) continue;
                if (!v.Current.vessel || !v.Current.vessel.loaded) continue;
                bool linked = externalVRDs.Contains(v.Current);
                GUIStyle style = linked ? BDArmorySetup.SelectedButtonStyle : GUI.skin.button;
                if (
                    GUI.Button(
                        new Rect(8, 8 + (linkRectEntryHeight * numberOfAvailableLinks), linkRectWidth - 16,
                            linkRectEntryHeight), v.Current.vessel.vesselName, style))
                {
                    if (linked)
                    {
                        //UnlinkRadar(v);
                        UnlinkVRD(v.Current);
                    }
                    else
                    {
                        //LinkToRadar(v);
                        LinkVRD(v.Current);
                    }
                }
                numberOfAvailableLinks++;
            }
            v.Dispose();

            GUI.EndGroup();
        }

        public void LinkAllRadars()
        {
            RefreshAvailableLinks();
            List<VesselRadarData>.Enumerator v = availableExternalVRDs.GetEnumerator();
            while (v.MoveNext())
            {
                if (v.Current == null) continue;
                if (!v.Current.vessel || !v.Current.vessel.loaded) continue;
                if (!externalVRDs.Contains(v.Current))
                    LinkVRD(v.Current);
            }
            v.Dispose();
            queueLinks = false;
        }

        public void RemoveDataFromRadar(ModuleRadar radar)
        {
            displayedTargets.RemoveAll(t => t.detectedByRadar == radar);
            UpdateLockedTargets();
        }
        public void RemoveDataFromIRST(ModuleIRST irst)
        {
            displayedIRTargets.RemoveAll(t => t.detectedByIRST == irst);
        }
        public void UnlinkVRD(VesselRadarData vrd)
        {
            if (BDArmorySettings.DEBUG_RADAR) Debug.Log("[BDArmory.VesselRadarData]: Unlinking VRD: " + vrd.vessel.vesselName);
            externalVRDs.Remove(vrd);

            List<ModuleRadar> radarsToUnlink = new List<ModuleRadar>();

            List<ModuleRadar>.Enumerator mra = availableRadars.GetEnumerator();
            while (mra.MoveNext())
            {
                if (mra.Current == null) continue;
                if (mra.Current.vesselRadarData == vrd)
                {
                    radarsToUnlink.Add(mra.Current);
                }
            }
            mra.Dispose();

            List<ModuleRadar>.Enumerator mr = radarsToUnlink.GetEnumerator();
            while (mr.MoveNext())
            {
                if (mr.Current == null) continue;
                if (BDArmorySettings.DEBUG_RADAR) Debug.Log("[BDArmory.VesselRadarData]:  - Unlinking radar: " + mr.Current.radarName);
                UnlinkRadar(mr.Current);
            }
            mr.Dispose();

            SaveExternalVRDVessels();
        }

        private void UnlinkRadar(ModuleRadar mr)
        {
            if (mr && mr.vessel)
            {
                RemoveRadar(mr);
                externalRadars.Remove(mr);
                mr.RemoveExternalVRD(this);

                bool noMoreExternalRadar = true;
                List<ModuleRadar>.Enumerator rad = externalRadars.GetEnumerator();
                while (rad.MoveNext())
                {
                    if (rad.Current == null) continue;
                    if (rad.Current.vessel != mr.vessel) continue;
                    noMoreExternalRadar = false;
                    break;
                }
                rad.Dispose();

                if (noMoreExternalRadar)
                {
                    externalVRDs.Remove(mr.vesselRadarData);
                }
            }
            else
            {
                externalRadars.RemoveAll(r => r == null);
            }

            externalLockCapabilityDirty = true;
        }

        private void RemoveEmptyVRDs()
        {
            externalVRDs.RemoveAll(vrd => vrd == null);
            List<VesselRadarData> vrdsToRemove = new List<VesselRadarData>();
            List<VesselRadarData>.Enumerator vrda = externalVRDs.GetEnumerator();
            while (vrda.MoveNext())
            {
                if (vrda.Current == null) continue;
                if (vrda.Current.rCount == 0)
                {
                    vrdsToRemove.Add(vrda.Current);
                }
            }
            vrda.Dispose();

            List<VesselRadarData>.Enumerator vrdr = vrdsToRemove.GetEnumerator();
            while (vrdr.MoveNext())
            {
                if (vrdr.Current == null) continue;
                externalVRDs.Remove(vrdr.Current);
            }
            vrdr.Dispose();
            externalLockCapabilityDirty = true;
        }

        public void UnlinkDisabledRadar(ModuleRadar mr)
        {
            RemoveRadar(mr);
            externalRadars.Remove(mr);
            externalLockCapabilityDirty = true;
            SaveExternalVRDVessels();
        }

        public void BeginWaitForUnloadedLinkedRadar(ModuleRadar mr, string vesselID)
        {
            UnlinkDisabledRadar(mr);

            if (waitingForVessels.Contains(vesselID))
            {
                return;
            }

            waitingForVessels.Add(vesselID);
            SaveExternalVRDVessels();
            StartCoroutine(RecoverUnloadedLinkedVesselRoutine(vesselID));
        }

        private List<string> waitingForVessels;

        private IEnumerator RecoverUnloadedLinkedVesselRoutine(string vesselID)
        {
            while (true)
            {
                using (var v = BDATargetManager.LoadedVessels.GetEnumerator())
                    while (v.MoveNext())
                    {
                        if (v.Current == null || !v.Current.loaded || v.Current == vessel || VesselModuleRegistry.IgnoredVesselTypes.Contains(v.Current.vesselType)) continue;
                        if (v.Current.id.ToString() != vesselID) continue;
                        VesselRadarData vrd = v.Current.gameObject.GetComponent<VesselRadarData>();
                        if (!vrd) continue;
                        waitingForVessels.Remove(vesselID);
                        StartCoroutine(LinkVRDWhenReady(vrd));
                        yield break;
                    }

                yield return new WaitForSecondsFixed(0.5f);
            }
        }

        private IEnumerator LinkVRDWhenReady(VesselRadarData vrd)
        {
            yield return new WaitWhileFixed(() => !vrd.radarsReady || (vrd.vessel is not null && (vrd.vessel.packed || !vrd.vessel.loaded)) || vrd.radarCount < 1);
            LinkVRD(vrd);
            if (BDArmorySettings.DEBUG_RADAR) Debug.Log("[BDArmory.VesselRadarData]: Radar data link recovered: Local - " + vessel.vesselName + ", External - " +
                       vrd.vessel.vesselName);
        }

        public void UnlinkAllExternalRadars()
        {
            externalRadars.RemoveAll(r => r == null);
            List<ModuleRadar>.Enumerator eRad = externalRadars.GetEnumerator();
            while (eRad.MoveNext())
            {
                if (eRad.Current == null) continue;
                eRad.Current.RemoveExternalVRD(this);
            }
            eRad.Dispose();
            externalRadars.Clear();

            externalVRDs.Clear();

            availableRadars.RemoveAll(r => r == null);
            availableRadars.RemoveAll(r => r.vessel != vessel);
            rCount = availableRadars.Count;
            availableIRSTs.RemoveAll(r => r == null);
            availableIRSTs.RemoveAll(r => r.vessel != vessel);
            iCount = availableIRSTs.Count;
            MaxRadarLocksExternal = 0;
            RefreshAvailableLinks();
        }

        bool externalLockCapabilityDirty = false;
        public int MaxRadarLocksExternal
        {
            get 
            {
                if (externalLockCapabilityDirty)
                    CountExternalRadarMaxLocks();
                return field;
            }
            private set;
        }
            = 0;

        private void CountExternalRadarMaxLocks()
        {
            int tempMaxRadarLocksExternal = 0;
            foreach (ModuleRadar radar in externalRadars)
            {
                if (radar == null) continue;
                tempMaxRadarLocksExternal += radar.maxLocks;
            }
            MaxRadarLocksExternal = tempMaxRadarLocksExternal;
            externalLockCapabilityDirty = false;
        }

        private void OpenLinkRadarWindow()
        {
            RefreshAvailableLinks();
            linkWindowOpen = true;
        }

        private void CloseLinkRadarWindow()
        {
            linkWindowOpen = false;
        }

        public void RefreshAvailableLinks()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || weaponManager == null || FlightGlobals.fetch == null || FlightGlobals.Vessels == null || !FlightGlobals.ready)
            {
                return;
            }

            availableExternalVRDs = new List<VesselRadarData>();
            using (var v = FlightGlobals.Vessels.GetEnumerator())
                while (v.MoveNext())
                {
                    if (v.Current == null || !v.Current.loaded || v.Current == vessel) continue;
                    if (VesselModuleRegistry.IgnoredVesselTypes.Contains(v.Current.vesselType)) continue;

                    BDTeam team = null;
                    var mf = v.Current.ActiveController().WM;
                    if (mf != null) team = mf.Team;
                    if (team != weaponManager.Team) continue;
                    VesselRadarData vrd = v.Current.gameObject.GetComponent<VesselRadarData>();
                    if (vrd && vrd.radarCount > 0)
                    {
                        availableExternalVRDs.Add(vrd);
                    }
                }
        }

        public void LinkVRD(VesselRadarData vrd)
        {
            if (!externalVRDs.Contains(vrd))
            {
                externalVRDs.Add(vrd);
            }

            List<ModuleRadar>.Enumerator mr = vrd.availableRadars.GetEnumerator();
            Vessel vrdVessel = vrd.vessel;
            while (mr.MoveNext())
            {
                if (mr.Current == null && mr.Current.vessel != vrdVessel) continue; // Reject null and external radars
                LinkToRadar(mr.Current);
            }
            mr.Dispose();
            externalLockCapabilityDirty = true;
            SaveExternalVRDVessels();
            StartCoroutine(UpdateLocksAfterFrame());
        }

        public void LinkToRadar(ModuleRadar mr)
        {
            if (!mr)
            {
                return;
            }

            if (externalRadars.Contains(mr))
            {
                return;
            }

            externalRadars.Add(mr);
            AddRadar(mr);

            mr.AddExternalVRD(this);
        }

        public void AddRadarContact(ModuleRadar radar, TargetSignatureData contactData, bool _locked, bool receivedData = false)
        {
            bool addContact = true;

            RadarDisplayData rData = new RadarDisplayData();
            rData.vessel = contactData.vessel;

            if (rData.vessel == vessel) return;

            if (!receivedData) //don't prevent VRD from e.g. getting datalinked sonar data from an ally boat despite being airborne
            {
                if (!rData.vessel.LandedOrSplashed && radar.sonarMode != ModuleRadar.SonarModes.None) addContact = false; //Sonar should not detect Aircraft
                if (rData.vessel.Splashed && radar.sonarMode != ModuleRadar.SonarModes.None && vessel.Splashed) addContact = true; //Sonar only detects underwater vessels // Sonar should only work when in the water
            }

            if (addContact == false) return;

            rData.signalPersistTime = radar.signalPersistTime;
            rData.detectedByRadar = radar;
            rData.locked = _locked;
            contactData.lockedByRadar = radar;
            rData.targetData = contactData;
            rData.pingPosition = UpdatedPingPosition(contactData.position, directionalFieldOfView);
            rData.velAngle = VectorUtils.GetAngleOnPlane(contactData.velocity, currForward, currRight);

            if (_locked)
            {
                radar.UpdateLockedTargetInfo(contactData);
            }

            // Are we receiving data about a target potentially locked by another radar?
            bool dontOverwrite = false;

            bool updateLock = true;

            int replaceIndex = -1;
            for (int i = 0; i < displayedTargets.Count; i++)
            {
                RadarDisplayData t = displayedTargets[i];
                if (t.vessel == rData.vessel)
                {
                    // If the target we're looking for is already locked...
                    if (t.locked)
                    {
                        // If we're locked on to a target via a different radar, don't overwrite the
                        // locked data if our own data is not locked!
                        if (!_locked)
                        {
                            dontOverwrite = true;
                            break;
                        }

                        // Otherwise, we can overwrite the data, but don't update locks
                        updateLock = false;
                    }
                    replaceIndex = i;
                    break;
                }
            }

            if (replaceIndex >= 0)
            {
                // If it is an existing target, replace the data
                displayedTargets[replaceIndex] = rData;
                // And if we should update our locks, update them
                if (updateLock)
                    UpdateLockedTargets();
                return;
            }
            else if (dontOverwrite)
            {
                //UpdateLockedTargets();
                return;
            }
            else
            {
                // We're adding new data
                displayedTargets.Add(rData);
                UpdateLockedTargets();
                return;
            }
        }

        public void AddIRSTContact(ModuleIRST irst, TargetSignatureData contactData, float magnitude)
        {
            IRSTDisplayData rData = new IRSTDisplayData();
            rData.vessel = contactData.vessel;

            if (rData.vessel == vessel) return;

            rData.signalPersistTime = irst.signalPersistTime;
            rData.detectedByIRST = irst;
            rData.magnitude = magnitude;
            rData.targetData = contactData;
            rData.pingPosition = UpdatedPingPosition(contactData.position, irst);

            int replaceIndex = -1;
            for (int i = 0; i < displayedIRTargets.Count; i++)
            {
                IRSTDisplayData t = displayedIRTargets[i];
                if (t.vessel == rData.vessel)
                {
                    // If contact exists and we're a ranging IRST, skip
                    if (!irst.irstRanging) return;

                    // If contact exists and it's already detected by ranging IRST, skip
                    if (t.detectedByIRST && t.detectedByIRST.irstRanging) return;

                    replaceIndex = i;
                    break;
                }
            }

            if (replaceIndex >= 0)
            {
                // If it is an existing target, replace the data
                displayedIRTargets[replaceIndex] = rData;
                return;
            }
            else
            {
                // We're adding new data
                displayedIRTargets.Add(rData);
                return;
            }
        }

        public void TargetNext()
        {
            // activeLockedTargetIndex is the index to the list of locked targets.
            // It contains the index of the displayedTargets.  We are really concerned with the displayed targets here,
            // but we need to keep the locked targets index list current.
            int displayedTargetIndex;
            if (!locked)
            {
                // No locked targets, get the first target in the list of displayed targets.
                if (displayedTargets.Count == 0) return;
                displayedTargetIndex = 0;
                TryLockTarget(displayedTargets[displayedTargetIndex]);
                //lockedTargetIndexes.Add(displayedTargetIndex);
                //UpdateLockedTargets();
                return;
            }
            // We have locked target(s)  Lets see if we can select the next one in the list (if it exists)
            displayedTargetIndex = lockedTargetIndexes[activeLockedTargetIndex];
            // Lets store the displayed target that is active
            ModuleRadar rad = displayedTargets[displayedTargetIndex].detectedByRadar;
            if (lockedTargetIndexes.Count > 1)
            {
                // We have more than one locked target.  Switch to the next locked target.
                if (activeLockedTargetIndex < lockedTargetIndexes.Count - 1)
                    activeLockedTargetIndex++;
                else
                {
                    activeLockedTargetIndex = 0;
                }
                //UpdateLockedTargets();
            }
            else
            {
                // If we have only one target we are done.
                if (displayedTargets.Count <= 1) return;
                // We have more targets to work with so attempt lock on the next available displayed target.
                if (displayedTargetIndex < displayedTargets.Count - 1)
                {
                    displayedTargetIndex++;
                }
                else
                {
                    displayedTargetIndex = 0;
                }

                TryLockTarget(displayedTargets[displayedTargetIndex]);
                if (!displayedTargets[displayedTargetIndex].detectedByRadar) return;
                // We have a good lock.  Lets update the indexes and locks
                lockedTargetIndexes.Add(displayedTargetIndex);
                rad.UnlockTargetAt(rad.currentLockIndex);
                //UpdateLockedTargets();
            }
        }

        public void TargetPrev()
        {
            // activeLockedTargetIndex is the index to the list of locked targets.
            // It contains the index of the displayedTargets.  We are really concerned with the displayed targets here,
            // but we need to keep the locked targets index list current.
            int displayedTargetIndex;
            if (!locked)
            {
                // No locked targets, get the last target in the list of displayed targets.
                if (displayedTargets.Count == 0) return;
                displayedTargetIndex = displayedTargets.Count - 1;
                TryLockTarget(displayedTargets[displayedTargetIndex]);
                //lockedTargetIndexes.Add(displayedTargetIndex);
                //UpdateLockedTargets();
                return;
            }
            // We have locked target(s)  Lets see if we can select the previous one in the list (if it exists)
            displayedTargetIndex = lockedTargetIndexes[activeLockedTargetIndex];
            // Lets store the displayed target that is ative
            ModuleRadar rad = displayedTargets[displayedTargetIndex].detectedByRadar;
            if (lockedTargetIndexes.Count > 1)
            {
                // We have more than one locked target.  switch to the previous locked target.
                if (activeLockedTargetIndex > 0)
                    activeLockedTargetIndex--;
                else
                {
                    activeLockedTargetIndex = lockedTargetIndexes.Count - 1;
                }
                //UpdateLockedTargets();
            }
            else
            {
                // If we have only one target we are done.
                if (displayedTargets.Count <= 1) return;
                // We have more targets to work with so attempt lock on the pevious available displayed target.
                if (displayedTargetIndex > 0)
                {
                    displayedTargetIndex--;
                }
                else
                {
                    displayedTargetIndex = displayedTargets.Count - 1;
                }

                TryLockTarget(displayedTargets[displayedTargetIndex]);
                if (!displayedTargets[displayedTargetIndex].detectedByRadar) return;
                // We got a good lock.  Lets update the indexes and locks
                //lockedTargetIndexes.Add(displayedTargetIndex);
                rad.UnlockTargetAt(rad.currentLockIndex);
                //UpdateLockedTargets();
            }
        }

        public bool SwitchActiveLockedTarget(Vessel vessel) // FIXME This needs to take into account the maxLocks field.
        {
            for (int i = 0; i < lockedTargetIndexes.Count; i++)
            {
                if (displayedTargets[lockedTargetIndexes[i]].vessel == vessel)
                {
                    activeLockedTargetIndex = i;
                    return true;
                }
            }
            return false;
        }

        // NOTE: Both this method and RemoveVesselFromLockedTargets could be improved by accounting for other
        // radars locking on to the target in question, as well as improved AddRadarContact behavior, primarily
        // by adding a counter to RadarDisplayTarget to account for the number of radars locked on, and to have
        // detectedByRadar be filled by the most capable radar of the ones reporting a lock-on. Granted, currently
        // such multi-radar locks aren't a thing, and as such this hasn't been implemented.
        public void UnlockAllTargetsOfRadar(ModuleRadar radar)
        {
            //radar.UnlockTarget();
            //displayedTargets.RemoveAll(t => t.detectedByRadar == radar); -> THIS IS PRETTY EXPENSIVE, INVOLVES A LOT OF COPYING
            // Instead, just set locked to false, which has the added benefit of not deleting the contact...
            for (int i = 0; i < displayedTargets.Count; i++)
            {
                // Get local copy (since we're gonna be using it for a comparison anyways, which will create a local copy)
                RadarDisplayData t = displayedTargets[i];
                if (t.detectedByRadar == radar)
                {
                    // Set locked to false
                    t.locked = false;
                    displayedTargets[i] = t;
                }
            }
            UpdateLockedTargets();
        }

        public void RemoveVesselFromTargets(Vessel _vessel)
        {
            // WARNING - THIS DOES A LOT OF MOVING THINGS AROUND, ONLY USE IF NECESSARY!
            displayedTargets.RemoveAll(t => t.vessel == _vessel);
            UpdateLockedTargets();
        }

        public void RemoveVesselFromLockedTargets(Vessel _vessel)
        {
            if (_vessel == null)
            {
                displayedTargets.RemoveAll(t => t.vessel == null);
                UpdateLockedTargets();
                return;
            }

            for (int i = 0; i < displayedTargets.Count; i++)
            {
                // Get local copy (since we're gonna be using it for a comparison anyways, which will create a local copy)
                RadarDisplayData t = displayedTargets[i];
                if (t.vessel == _vessel)
                {
                    // Set locked to false
                    t.locked = false;
                    displayedTargets[i] = t;
                    // There should only be a single instance of this vessel in `displayedTargets`
                    break;
                }
            }
            UpdateLockedTargets();
        }

        public void UnlockAllTargets(bool unlockDatalinkedRadars = true)
        {
            List<ModuleRadar>.Enumerator radar = weaponManager.radars.GetEnumerator();
            while (radar.MoveNext())
            {
                if (radar.Current == null) continue;
                if (radar.Current.vessel != vessel) continue;
                if (!unlockDatalinkedRadars && radar.Current.linkedVRDs > 0) continue;
                radar.Current.UnlockAllTargets();
            }
            radar.Dispose();
        }

        public void UnlockCurrentTarget()
        {
            if (!locked) return;

            ModuleRadar rad = displayedTargets[lockedTargetIndexes[activeLockedTargetIndex]].detectedByRadar;
            rad.UnlockTargetAt(rad.currentLockIndex);
        }

        /// <summary>
        /// Unlocks the target vessel. This variant is less efficient than the index variant, however it is
        /// generally more useful as it will search through displayedTargets and find the vessel. Useful in
        /// instances where the index of the target in lockedTargetIndexes is not known, or where the index
        /// may change due to changes in the locks.
        /// </summary>
        /// <param name="vessel">Vessel to unlock.</param>
        public void UnlockSelectedTarget(Vessel vessel)
        {
            if (!locked) return;
            var vesselIndex = displayedTargets.FindIndex(t => t.vessel == vessel);
            if (vesselIndex != -1)
            {
                ModuleRadar rad = displayedTargets[vesselIndex].detectedByRadar;
                rad.UnlockTargetVessel(vessel);
            }
        }

        /// <summary>
        /// Unlocks the target at lockedTargetIndexes[index]. NOTE! Since lockedTargetIndexes WILL change when
        /// a target is locked/unlocked, this should ONLY be called in instances when you are only unlocking
        /// a single target. When unlocking multiple target, use the vessel variant instead. Note this function
        /// is entirely unprotected, it is the user's responsibility to ensure index is valid for
        /// lockedTargetIndexes.
        /// </summary>
        /// <param name="index">Index of target in lockedTargetIndexes.</param>
        public void UnlockSelectedTarget(int index)
        {
            if (!locked) return;
            ModuleRadar rad = displayedTargets[lockedTargetIndexes[index]].detectedByRadar;
            rad.UnlockTargetVessel(displayedTargets[lockedTargetIndexes[index]].vessel);
        }

        private void CleanDisplayedContacts()
        {
            int count = displayedTargets.Count;
            displayedTargets.RemoveAll(t => t.vessel == null || t.targetData.age > t.signalPersistTime * 2);
            displayedIRTargets.RemoveAll(t => t.vessel == null || t.targetData.age > t.signalPersistTime * 2);
            if (count != displayedTargets.Count)
            {
                UpdateLockedTargets();
            }
        }

        private Vector2 UpdatedPingPosition(Vector3 worldPosition, ModuleRadar radar)
        {
            return UpdatedPingPosition(worldPosition, radar.radarMinMaxAzLimits[1]);
        }

        private Vector2 UpdatedPingPosition(Vector3 worldPosition, float directionalFieldOfView)
        {
            if (rangeIndex < 0 || rangeIndex > rIncrements.Length - 1) rangeIndex = rIncrements.Length - 1;
            if (omniDisplay)
            {
                return RadarUtils.WorldToRadar(worldPosition, referenceTransform, RadarDisplayRect, rIncrements[rangeIndex]);
            }
            else
            {
                return RadarUtils.WorldToRadarRadial(worldPosition, referenceTransform, RadarDisplayRect,
                    rIncrements[rangeIndex], directionalFieldOfView);
            }
        }

        private Vector2 UpdatedPingPosition(Vector3 worldPosition, ModuleIRST irst)
        {
            if (rangeIndex < 0 || rangeIndex > rIncrements.Length - 1) rangeIndex = rIncrements.Length - 1;
            if (omniDisplay)
            {
                return RadarUtils.WorldToRadar(worldPosition, referenceTransform, RadarDisplayRect, rIncrements[rangeIndex]);
            }
            else
            {
                return RadarUtils.WorldToRadarRadial(worldPosition, referenceTransform, RadarDisplayRect,
                    rIncrements[rangeIndex], irst.directionalFieldOfView / 2);
            }
        }

        private bool pingPositionsDirty = true;
        public MissileLaunchParams guiDLZData;
        public bool guiDrawDLZData;
        float guiDistToTarget;
        // Size of 4 * jammedPositionsSize (because we display 4 jammed positions)
        internal Vector2[] jammedPositions;
        int jammedPositionsSize;

        private void UpdateDisplayedContacts()
        {
            BDAStealth.JamPatches.DisplayPrefix(this);
            try
            {
                UpdateDisplayedContactsStock();
                BDAStealth.JamPatches.DisplayPostfix(this);
            }
            finally { BDAStealth.JamPatches.DisplayFinalizer(); }
        }

        private void UpdateDisplayedContactsStock()
        {
            Vector2 displayCenter = guiDispOmni ? new Vector2(RadarDisplayRect.x * 0.5f, RadarDisplayRect.y * 0.5f) : new Vector2(RadarDisplayRect.x * 0.5f, RadarDisplayRect.y);
            int currJammedIndex = 0;
            guiDrawDLZData = false;

            int lTarInd = 0;
            if (locked)
                lTarInd = lockedTargetIndexes[activeLockedTargetIndex];

            for (int i = 0; i < displayedTargets.Count; i++)
            {
                RadarDisplayData t = displayedTargets[i];
                if (t.locked && locked)
                {
                    TargetSignatureData lockedTarget = t.targetData;
                    /*RadarDisplayData newData = new RadarDisplayData();
                    newData.detectedByRadar = displayedTargets[i].detectedByRadar;
                    newData.locked = displayedTargets[i].locked;
                    if (guiDispOmni)
                        newData.pingPosition = RadarUtils.WorldToRadar(lockedTarget.position, referenceTransform, RadarDisplayRect,
                            rIncrements[rangeIndex]);
                    else
                        newData.pingPosition = RadarUtils.WorldToRadarRadial(lockedTarget.position, referenceTransform,
                            RadarDisplayRect, rIncrements[rangeIndex],
                            directionalFieldOfView);
                    newData.signalPersistTime = displayedTargets[i].signalPersistTime;
                    newData.targetData = displayedTargets[i].targetData;
                    newData.vessel = displayedTargets[i].vessel;
                    float vAngle = VectorUtils.GetAngleOnPlane(lockedTarget.velocity, currForward, currRight);
                    newData.velAngle = vAngle;*/

                    if (guiDispOmni)
                        t.pingPosition = RadarUtils.WorldToRadar(lockedTarget.position, referenceTransform, RadarDisplayRect,
                            rIncrements[rangeIndex]);
                    else
                        t.pingPosition = RadarUtils.WorldToRadarRadial(lockedTarget.position, referenceTransform,
                            RadarDisplayRect, rIncrements[rangeIndex],
                            directionalFieldOfView);
                    t.velAngle = VectorUtils.GetAngleOnPlane(lockedTarget.velocity, currForward, currRight);

                    displayedTargets[i] = t;

                    if (i == lTarInd && weaponManager && weaponManager.selectedWeapon != null)
                    {
                        if (weaponManager.selectedWeapon.GetWeaponClass() == WeaponClasses.Missile || weaponManager.selectedWeapon.GetWeaponClass() == WeaponClasses.SLW)
                        {
                            MissileBase currMissile = weaponManager.CurrentMissile;
                            if (currMissile && (currMissile.TargetingMode == MissileBase.TargetingModes.Radar || currMissile.TargetingMode == MissileBase.TargetingModes.Heat || currMissile.TargetingMode == MissileBase.TargetingModes.Inertial || currMissile.TargetingMode == MissileBase.TargetingModes.Gps))
                            {
                                guiDLZData = MissileLaunchParams.GetDynamicLaunchParams(currMissile, lockedTarget.velocity, lockedTarget.predictedPosition);
                                guiDistToTarget = Vector3.Distance(lockedTarget.predictedPosition, currPosition);
                                guiDrawDLZData = true;
                            }
                        }
                    }
                }
                else
                {
                    //jamming
                    // NEW: evaluation via radarutils!

                    TargetSignatureData tData = t.targetData;

                    Vector2 tempPos;
                    if (pingPositionsDirty)
                    {
                        if (guiDispOmni)
                            tempPos = RadarUtils.WorldToRadar(tData.position, referenceTransform, RadarDisplayRect,
                                rIncrements[rangeIndex]);
                        else
                            tempPos = RadarUtils.WorldToRadarRadial(tData.position, referenceTransform,
                                RadarDisplayRect, rIncrements[rangeIndex],
                                directionalFieldOfView);
                    }
                    else
                        tempPos = t.pingPosition;

                    int tempJammedIndex = -1;
                    // TODO: This should probably go to AddRadarContact, but that would involve a more complex
                    // pool-type system for jammed positions instead of this simplistic array system, unless we
                    // specifically wanna keep these moving jammed positions
                    if (tData.vesselJammer)
                    {
                        float distanceToTarget = (t.detectedByRadar.currPosition - tData.position).sqrMagnitude;
                        float jamDistance = RadarUtils.GetVesselECMJammingDistance(tData.vessel);
                        if (distanceToTarget < jamDistance * jamDistance)
                        {
                            Vector2 tempRadarPos;
                            Vector2 dir2D;

                            if (t.detectedByRadar.vessel != vessel)
                            {
                                if (guiDispOmni)
                                    tempRadarPos = RadarUtils.WorldToRadar(t.detectedByRadar.currPosition, referenceTransform, RadarDisplayRect,
                                        rIncrements[rangeIndex]);
                                else
                                    tempRadarPos = RadarUtils.WorldToRadarRadial(t.detectedByRadar.currPosition, referenceTransform,
                                        RadarDisplayRect, rIncrements[rangeIndex],
                                        directionalFieldOfView);
                            }
                            else
                                tempRadarPos = displayCenter;

                            dir2D = (tempPos - tempRadarPos).normalized;

                            if (currJammedIndex > jammedPositionsSize - 1)
                            {
                                // Use the same strat as lists do and resize to twice the size if needed.
                                jammedPositionsSize *= 2;
                                // 4 times the size because we have 4 positions to store
                                System.Array.Resize(ref jammedPositions, jammedPositionsSize * 4);
                            }

                            float minR = 100f / rIncrements[rangeIndex];
                            dir2D = dir2D * rIncrements[rangeIndex];
                            float bearingVariation =
                                        Mathf.Clamp(
                                            1024e6f /    // 32000 * 32000
                                            distanceToTarget, 0,
                                            80);
                            for (int j = 0; j < 4; j++)
                                jammedPositions[currJammedIndex + j] = tempRadarPos +
                                    VectorUtils.Rotate2DVec2(dir2D * UnityEngine.Random.Range(minR, 1), UnityEngine.Random.Range(-bearingVariation, bearingVariation));
                            tempJammedIndex = currJammedIndex;
                            currJammedIndex++;
                        }
                    }

                    // Update if pingPositionsDirty *or* we need to update the jammed index
                    if (pingPositionsDirty || tempJammedIndex > 0)
                    {
                        //displayedTargets[i].pingPosition = UpdatedPingPosition(displayedTargets[i].targetData.position, displayedTargets[i].detectedByRadar);
                        /*RadarDisplayData newData = new RadarDisplayData();
                        newData.detectedByRadar = displayedTargets[i].detectedByRadar;
                        newData.locked = displayedTargets[i].locked;
                        newData.pingPosition = tempPos;
                        newData.signalPersistTime = displayedTargets[i].signalPersistTime;
                        newData.targetData = displayedTargets[i].targetData;
                        newData.velAngle = displayedTargets[i].velAngle;
                        newData.vessel = displayedTargets[i].vessel;
                        newData.jammedIndex = tempJammedIndex;*/
                        t.pingPosition = tempPos;
                        t.jammedIndex = tempJammedIndex;
                        displayedTargets[i] = t;
                    }
                }
            }
        }

        const float recognitionTime = 4f;
        Vessel recognising;
        float recognisingSince;

        /// <summary>
        /// Non-cooperative target recognition: the bugged target's type once it has been held for recognitionTime (switching the lock to
        /// another craft starts over); null until then.
        /// </summary>
        string RecognisedType(Vessel target)
        {
            if (target == null) return null;
            if (target != recognising) { recognising = target; recognisingSince = Time.time; }
            if (Time.time - recognisingSince < recognitionTime) return null;
            var theirs = VesselModuleRegistry.GetMissileFire(target, true);
            return ScopeComposer.CraftType(target.vesselName, theirs != null && theirs.Team != null ? theirs.Team.Name : null);
        }

        private void DrawDisplayedContacts()
        {
            var canvas = ImguiScopeCanvas.Instance;
            float scale = BDArmorySettings.RADAR_WINDOW_SCALE;
            var display = new ScopeRect(0, 0, RadarDisplayRect.width, RadarDisplayRect.height);
            bool lockDirty = false;
            Vector2 Centerpoint = new Vector2(display.w / 2, display.h / 2);

            for (int i = 0; i < displayedTargets.Count; i++)
            {
                RadarDisplayData t = displayedTargets[i];
                if (t.locked && locked)
                {
                    TargetSignatureData lockedTarget = t.targetData;
                    Vector2 pingPosition = t.pingPosition;
                    bool active = i == lockedTargetIndexes[activeLockedTargetIndex];
                    var hit = ScopeComposer.Lock(canvas, new ScopePoint(pingPosition.x, pingPosition.y), t.velAngle, active, (lockedTarget.altitude / 1000).ToString("0"), scale);
                    Rect pingRect = new Rect(hit.x, hit.y, hit.w, hit.h);

                    if (BDArmorySettings.DEBUG_RADAR)
                    {
                        GUI.Label(new Rect(pingPosition.x + (pingSize.x / 2), pingPosition.y, 100, 24),
                            lockedTarget.signalStrength.ToString("0.0"));
                    }

                    if (GUI.Button(pingRect, GUIContent.none, GUIStyle.none) &&
                        Time.time - guiInputTime > guiInputCooldown)
                    {
                        guiInputTime = Time.time;
                        if (active)
                        {
                            t.detectedByRadar.UnlockTargetVessel(t.vessel);
                            lockDirty = true;
                        }
                        else
                        {
                            for (int x = 0; x < lockedTargetIndexes.Count; x++)
                            {
                                if (i == lockedTargetIndexes[x])
                                {
                                    activeLockedTargetIndex = x;
                                    break;
                                }
                            }

                            t.detectedByRadar.SetActiveLock(t.targetData);
                        }
                    }

                    // The bugged target's numbers and the launch zone against it.
                    if (!lockDirty && i == lockedTargetIndexes[activeLockedTargetIndex])
                    {
                        Vector3 toTarget = lockedTarget.position - vessel.CoM;
                        float closure = Vector3.Dot(vessel.Velocity() - lockedTarget.velocity, toTarget.normalized);
                        ScopeComposer.LockData(canvas, display, toTarget.magnitude / 1000f, closure, lockedTarget.altitude / 1000f, RecognisedType(t.vessel), scale);
                        if (guiDrawDLZData)
                            ScopeComposer.LaunchZone(canvas, display, rIncrements[rangeIndex], guiDLZData.maxLaunchRange, guiDLZData.rangeTr, guiDLZData.minLaunchRange,
                                guiDistToTarget, omniDisplay ? (float?)null : pingPosition.y, scale);
                    }
                }
                else
                {
                    TargetSignatureData tData = t.targetData;
                    float minusAlpha = (Mathf.Clamp01((Time.time - tData.timeAcquired) / t.signalPersistTime) * 2) - 1;
                    float fade = Mathf.Clamp01(1f - minusAlpha);

                    //jamming
                    // NEW: evaluation via radarutils!
                    int currJammedIndex = t.jammedIndex;
                    bool jammed = currJammedIndex > 0;
                    // Identification: our side's craft answer as friends, the other sides' as hostile, anything without a side is unknown.
                    var iff = tData.Team == null ? Iff.Unknown : weaponManager && weaponManager.Team.IsFriendly(tData.Team) ? Iff.Friend : Iff.Hostile;
                    var p = new ScopePoint(t.pingPosition.x, t.pingPosition.y);
                    ScopeRect hit;
                    if (tData.targetInfo && tData.targetInfo.isMissile)
                    {
                        hit = ScopeComposer.Contact(canvas, ContactKind.Missile, iff, p, 0f, fade, null, scale);
                    }
                    else if (!jammed && t.detectedByRadar.showDirectionWhileScan && tData.velocity.sqrMagnitude > 100f) // Tracks, with their heading.
                    {
                        hit = ScopeComposer.Contact(canvas, ContactKind.Track, iff, p, t.velAngle, fade, (tData.altitude / 1000).ToString("0"), scale);
                    }
                    else // Bare returns; a jammer's four false targets.
                    {
                        hit = default;
                        int drawCount = jammed ? 4 : 1;
                        for (int d = 0; d < drawCount; d++)
                        {
                            if (jammed)
                            {
                                Vector2 currJammedPos = jammedPositions[currJammedIndex + d];
                                p = new ScopePoint(currJammedPos.x, currJammedPos.y);
                            }
                            float across = 0f; // The omni display lays a brick across its bearing.
                            if (omniDisplay)
                            {
                                across = Vector2.Angle(Vector3.up, Centerpoint - new Vector2(p.x, p.y));
                                if (p.x < Centerpoint.x) across = -across;
                            }
                            hit = ScopeComposer.Contact(canvas, jammed ? ContactKind.Jammed : ContactKind.Raw, jammed ? Iff.Hostile : iff, p, across, fade, null, scale);
                        }
                    }

                    if (GUI.Button(new Rect(hit.x, hit.y, hit.w, hit.h), GUIContent.none, GUIStyle.none) &&
                        Time.time - guiInputTime > guiInputCooldown)
                    {
                        guiInputTime = Time.time;
                        TryLockTarget(t, true);
                    }

                    if (BDArmorySettings.DEBUG_RADAR)
                    {
                        GUI.Label(new Rect(p.x + (pingSize.x / 2), p.y, 100, 24),
                            tData.signalStrength.ToString("0.0"));
                    }
                }
            }
        }

        private void DrawDisplayedIRContacts()
        {
            var canvas = ImguiScopeCanvas.Instance;
            float scale = BDArmorySettings.RADAR_WINDOW_SCALE;
            var display = new ScopeRect(0, 0, RadarDisplayRect.width, RadarDisplayRect.height);
            Vector2 Centerpoint = new Vector2(display.w / 2, display.h / 2);
            for (int i = 0; i < displayedIRTargets.Count; i++)
            {
                bool hasRadarContact = false;
                IRSTDisplayData t = displayedIRTargets[i];
                TargetSignatureData tData = t.targetData;
                if (t.detectedByIRST.irstRanging)
                {
                    TargetInfo tInfo = tData.targetInfo;
                    if (tInfo && displayedTargets.Count > 0) //if Radar enabled, don't display targets that have already been displayed
                    {
                        for (int r = 0; r < displayedTargets.Count; r++)
                        {
                            if (tInfo == displayedTargets[r].targetData.targetInfo)
                            {
                                hasRadarContact = true;
                                break;
                            }
                        }
                    }
                }
                if (!hasRadarContact) //have !radar contacts be displayed on the rim, since IRSt doesn't do ranging.
                {
                    float minusAlpha = (Mathf.Clamp01((Time.time - tData.timeAcquired) / t.signalPersistTime) * 2f) - 1f;

                    if (pingPositionsDirty)
                    {
                        t.pingPosition = UpdatedPingPosition(tData.position, t.detectedByIRST);
                        displayedIRTargets[i] = t;
                    }
                    Vector2 pingPosition = t.pingPosition;

                    float bearing = 0f;
                    if (omniDisplay)
                    {
                        bearing = Vector2.Angle(Vector3.up, Centerpoint - pingPosition);
                        if (pingPosition.x < Centerpoint.x) bearing = -bearing;
                    }
                    bool missile = (tData.targetInfo && tData.targetInfo.isMissile) || tData.Team == null;
                    ScopeComposer.InfraredContact(canvas, display, new ScopePoint(pingPosition.x, pingPosition.y), omniDisplay, bearing, t.magnitude, missile, Mathf.Clamp01(1f - minusAlpha), scale);

                    if (BDArmorySettings.DEBUG_RADAR)
                    {
                        GUI.Label(new Rect(pingPosition.x + (pingSize.x * 0.5f), pingPosition.y, 100, 24),
                            displayedIRTargets[i].magnitude.ToString("0.0"));
                    }
                }
            }
        }
        public bool omniDisplay
        {
            get { return (radarCount > 1 || (radarCount == 1 && availableRadars[0].omnidirectional) || irstCount > 1 || (irstCount == 1 && !availableIRSTs[0].irstRanging)); }
        }

        private void UpdateInputs()
        {
            if (!vessel.isActiveVessel)
            {
                return;
            }

            if (BDInputUtils.GetKey(BDInputSettingsFields.RADAR_SLEW_RIGHT))
            {
                ShowSelector();
                SlewSelector(Vector2.right);
            }
            else if (BDInputUtils.GetKey(BDInputSettingsFields.RADAR_SLEW_LEFT))
            {
                ShowSelector();
                SlewSelector(-Vector2.right);
            }

            if (BDInputUtils.GetKey(BDInputSettingsFields.RADAR_SLEW_UP))
            {
                ShowSelector();
                SlewSelector(-Vector2.up);
            }
            else if (BDInputUtils.GetKey(BDInputSettingsFields.RADAR_SLEW_DOWN))
            {
                ShowSelector();
                SlewSelector(Vector2.up);
            }
            if (radarCount > 0)
            {
                if (BDInputUtils.GetKeyDown(BDInputSettingsFields.RADAR_LOCK))
                {
                    if (showSelector)
                    {
                        TryLockViaSelector();
                    }
                    ShowSelector();
                }

                if (BDInputUtils.GetKeyDown(BDInputSettingsFields.RADAR_CYCLE_LOCK))
                {
                    if (locked)
                    {
                        CycleActiveLock();
                    }
                }

                if (BDInputUtils.GetKeyDown(BDInputSettingsFields.RADAR_TARGET_NEXT))
                {
                    TargetNext();
                }
                else if (BDInputUtils.GetKeyDown(BDInputSettingsFields.RADAR_TARGET_PREV))
                {
                    TargetPrev();
                }
            }
            if (BDInputUtils.GetKeyDown(BDInputSettingsFields.RADAR_SCAN_MODE))
            {
                if (!locked && radarCount + irstCount > 0 && !omniDisplay)
                {
                    availableRadars[0].boresightScan = !availableRadars[0].boresightScan;
                    availableIRSTs[0].boresightScan = !availableIRSTs[0].boresightScan;
                }
            }
            if (BDInputUtils.GetKeyDown(BDInputSettingsFields.RADAR_RANGE_UP))
            {
                IncreaseRange();
            }
            else if (BDInputUtils.GetKeyDown(BDInputSettingsFields.RADAR_RANGE_DN))
            {
                DecreaseRange();
            }
            if (BDInputUtils.GetKeyDown(BDInputSettingsFields.RADAR_TURRETS))
            {
                if (slaveTurrets)
                {
                    UnslaveTurrets();
                }
                else
                {
                    SlaveTurrets();
                }
            }
        }

        private void TryLockViaSelector()
        {
            bool found = false;
            Vector3 closestPos = Vector3.zero;
            float closestSqrMag = float.MaxValue;
            for (int i = 0; i < displayedTargets.Count; i++)
            {
                RadarDisplayData t = displayedTargets[i];
                float sqrMag = (t.pingPosition - selectorPos).sqrMagnitude;
                if (sqrMag < closestSqrMag)
                {
                    if (sqrMag < 400f) // 20 * 20)
                    {
                        closestPos = t.targetData.predictedPosition;
                        found = true;
                    }
                }
            }

            if (found)
            {
                TryLockTarget(closestPos, true);
            }
            else if (closestSqrMag > 1600f) // (40 * 40))
            {
                UnlockCurrentTarget();
            }
        }

        private void SlewSelector(Vector2 direction)
        {
            float rate = 150;
            selectorPos += direction * rate * Time.deltaTime;

            if (!omniDisplay)
            {
                if (selectorPos.y > RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE * 0.975f)
                {
                    if (rangeIndex > 0)
                    {
                        DecreaseRange();
                        selectorPos.y = RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE * 0.75f;
                    }
                }
                else if (selectorPos.y < RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE * 0.025f)
                {
                    if (rangeIndex < rIncrements.Length - 1)
                    {
                        IncreaseRange();
                        selectorPos.y = RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE * 0.25f;
                    }
                }
            }

            selectorPos.y = Mathf.Clamp(selectorPos.y, 10, RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE - 10);
            selectorPos.x = Mathf.Clamp(selectorPos.x, 10, RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE - 10);
        }

        private void ShowSelector()
        {
            if (!showSelector)
            {
                showSelector = true;
                selectorPos = new Vector2(RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE / 2, RadarScreenSize * BDArmorySettings.RADAR_WINDOW_SCALE / 2);
            }
        }
    }
}
