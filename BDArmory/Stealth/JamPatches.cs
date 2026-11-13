using System;
using System.Collections.Generic;
using BDArmory.Control;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>On-screen notes for the player's own craft (shooter or target).</summary>
    internal static class Notify
    {
        public static bool Involves(Vessel a, Vessel b)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            return active != null && (active == a || active == b);
        }

        public static void Post(string text)
        {
            if (Settings.Messages) ScreenMessages.PostScreenMessage(text, 4f, ScreenMessageStyle.UPPER_CENTER);
        }
    }

    /// <summary>
    /// Noise jammers and the things that read them: the AI's reactive ECM, the radar display, missile datalink and lock attempts.
    /// A noise jammer denies range, not bearing: beyond its burn-through range a radar shows it as a strobe and cannot feed a
    /// missile its position; inside it the skin echo is back. Stock applies both effects only inside the jam radius.
    /// </summary>
    internal static class JamPatches
    {
        static Vessel displayVessel;      // vessel whose radar display is being updated
        static MissileBase linkMissile;   // missile whose mid-course update is being evaluated
        static readonly List<float> onFactors = new List<float>(8), nowFactors = new List<float>(8);
        static float nextLockNote;

        // ---- AI reactive ECM ----

        /// <summary>
        /// The AI switches every jammer on when a radar missile is inbound. Against a missile already on its own seeker that
        /// is only right if it lowers the seeker's signal: a noise jammer feeds home-on-jam, so here the AI leaves ECM off
        /// (or drops the ECM it started) when jamming would make the craft the stronger target.
        /// </summary>
        internal static bool FireEcmPrefix(MissileFire __instance)
        {
            if (!Settings.Enabled || !Settings.HomeOnJam) return true;
            try
            {
                Vessel v = __instance.vessel, threat = __instance.incomingMissileVessel;
                if (v == null || threat == null) return true;
                TargetInfo threatInfo = threat.gameObject.GetComponent<TargetInfo>();
                MissileBase mb = threatInfo != null ? threatInfo.MissileBaseModule : null;
                if (mb == null || !Sensors.HasRadarSeeker(mb) || Sensors.IsTorpedo(mb)) return true;
                float range = Vector3.Distance(threat.CoM, v.CoM);
                if (!mb.ActiveRadar && range > mb.activeRadarRange) return true;   // still riding the launcher's radar: lockbreak helps
                TargetInfo ti = v.gameObject.GetComponent<TargetInfo>();
                if (ti == null || !(ti.radarBaseSignature > 0f)) return true;

                bool aiJamming = __instance.isECMJamming;
                Patches.EcmTotals(v, _ => true, out float onNoise, out float onLock, onFactors);
                Patches.EcmTotals(v, j => j.jammerEnabled && (!aiJamming || j.manuallyEnabled), out float nowNoise, out float nowLock, nowFactors);
                float km = range * 0.001f;
                float on = Patches.SeekerSignal(v, ti.radarBaseSignature, onNoise, onLock, onFactors, km);
                float now = Patches.SeekerSignal(v, ti.radarBaseSignature, nowNoise, nowLock, nowFactors, km);
                if (on <= now) return true;
                if (aiJamming) __instance.StopECM();
                return false;
            }
            catch (Exception e) { Debug.LogWarning("[BDAStealth] ECM decision: " + e.Message); return true; }
        }

        // ---- range denial ----

        /// <summary>True when the sensor is outside the range at which v's skin echo beats its own noise jamming.</summary>
        public static bool RangeDenied(Vessel v, Vector3 sensor)
        {
            TargetInfo ti = v.gameObject.GetComponent<TargetInfo>();
            if (ti == null || !Patches.TryEcm(ti, out EcmData e) || e.Jam <= 0f) return false;
            float km = StealthMath.NoiseBurnThroughKm(e.Jam, ti.radarRCSReducedSignature, Settings.BurnThroughKm);
            return Vector3.Distance(v.CoM, sensor) > km * 1000f;
        }

        internal static void DisplayPrefix(VesselRadarData __instance) => displayVessel = __instance.vessel;
        internal static void DisplayFinalizer() => displayVessel = null;
        internal static void LinkPrefix(MissileBase __instance) => linkMissile = __instance;
        internal static void LinkFinalizer() => linkMissile = null;

        /// <summary>
        /// BDArmory compares this distance with the sensor's range to the jammer: the radar display draws strobes when it is
        /// larger, missile datalink updates stop when it is larger. Return "everything" or "nothing" from the range-denial rule.
        /// </summary>
        internal static void JamDistancePostfix(Vessel v, ref float __result)
        {
            if (!Settings.Enabled || !Settings.JamDeniesRange || v == null) return;
            Vector3 sensor;
            if (linkMissile != null)
            {
                Vessel source = linkMissile.SourceVessel;
                sensor = source != null ? source.CoM : linkMissile.vessel.CoM;
            }
            else if (displayVessel != null) sensor = displayVessel.CoM;
            else return;
            __result = RangeDenied(v, sensor) ? 1e9f : 0f;
        }

        static readonly Vector2[] strobeBuf = new Vector2[4];

        /// <summary>
        /// BDArmory numbers jammed contacts from 0 but draws strobes only for an index above 0, so the first (usually the only)
        /// jammer on the display never shows them; and a contact that stops being jammed keeps its strobes until the display is
        /// next marked dirty. After its update, give the first jammed contact a non-zero slot and clear every unjammed one.
        /// </summary>
        internal static void DisplayPostfix(VesselRadarData __instance)
        {
            if (!Settings.Enabled || !Settings.JamDeniesRange || displayVessel == null) return;
            List<RadarDisplayData> shown = __instance.displayedTargets;
            Vector2[] strobes = __instance.jammedPositions;
            if (shown == null || strobes == null) return;
            int first = -1, jammed = 0;
            Vector3 sensor = displayVessel.CoM;
            for (int i = 0; i < shown.Count; i++)
            {
                RadarDisplayData d = shown[i];
                bool denied = d.vessel != null && d.targetData.vesselJammer && RangeDenied(d.vessel, sensor);   // BDArmory's test, with our distance
                if (denied) { if (first < 0) first = i; jammed++; }
                else if (d.jammedIndex != -1) { d.jammedIndex = -1; shown[i] = d; }
            }
            if (first < 0) return;
            int slot = jammed;   // BDArmory used slots 0 .. jammed-1
            if (strobes.Length < slot + 4) { Array.Resize(ref strobes, slot + 4); __instance.jammedPositions = strobes; }
            Array.Copy(strobes, 0, strobeBuf, 0, 4);
            Array.Copy(strobeBuf, 0, strobes, slot, 4);
            RadarDisplayData f = shown[first];
            f.jammedIndex = slot;
            shown[first] = f;
        }

        // ---- lock attempts ----

        /// <summary>BDArmory lets a lock attempt on a jamming contact start and then fail without a word.</summary>
        internal static void TryLockPostfix(ModuleRadar __instance, Vessel targetVessel, bool __result)
        {
            if (__result || !Settings.Enabled || !Settings.Messages || targetVessel == null || Time.time < nextLockNote) return;
            try
            {
                if (__instance.vessel == null || !__instance.vessel.isActiveVessel) return;
                TargetInfo ti = targetVessel.gameObject.GetComponent<TargetInfo>();
                if (ti == null || !Patches.TryEcm(ti, out EcmData e) || e.LockBreak <= 0f) return;
                Target t = Target.Plain(ti.radarBaseSignature * Patches.Exposure(targetVessel));
                t.Coating = Patches.Coating(targetVessel); t.Parts = e.PartsCombined(); t.Jam = e.Jam; t.LockBreak = e.LockBreak;
                float burnThrough = Analysis.Evaluate(Sensors.Of(__instance), t).LockEcm;
                if (Vector3.Distance(targetVessel.CoM, __instance.vessel.CoM) * 0.001f <= burnThrough) return;   // inside burn-through: ECM is not why it failed
                nextLockNote = Time.time + 3f;
                Notify.Post($"NO LOCK · {targetVessel.vesselName} ECM · burn-through {Analysis.Km(burnThrough)}");
            }
            catch (Exception ex) { Debug.LogWarning("[BDAStealth] lock note: " + ex.Message); }
        }
    }
}
