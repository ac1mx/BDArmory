using System;
using UnityEngine;

using BDArmory.CounterMeasure;
using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Weapons.Missiles;
using BDAStealth;

namespace BDArmory.Control
{
    /// <summary>
    /// On-screen cues for the craft being flown or watched, from the same decoy and shot models the AI pilots decide with: against the
    /// missile that arrives first, its time to go and what would defeat it now (beam, chaff, flares, or hold them); for the selected
    /// air-to-air missile, its chance against the current target. Shown with BDAStealth's on-screen messages.
    /// </summary>
    public partial class MissileFire
    {
        const float cueInterval = 0.25f, heatWarningRange = 4000f, maxCueTime = 60f;
        float cueTime = -1f;
        string threatCue, shotCue, rwrCue, lastCueAction;
        static readonly Color rwrCueColor = new Color(1f, 0.85f, 0.3f);
        int cueCount;
        static GUIStyle cueStyle;
        static readonly Color threatCueColor = new Color(1f, 0.45f, 0.3f), shotCueColor = new Color(0.55f, 1f, 0.55f);

        void DrawCues()
        {
            if (!BDAStealth.Settings.Enabled || !BDAStealth.Settings.Messages) return;
            if (Time.time - cueTime >= cueInterval || Time.time < cueTime)
            {
                cueTime = Time.time;
                threatCue = ThreatCue(out string action);
                rwrCue = RwrCue();
                if (++cueCount % 2 == 0) shotCue = ShotCue(); // The estimate may fly the missile through the shot model: twice a second.
                if (BDModulePilotAI.steeringMetricsEnabled && action != lastCueAction)
                {
                    lastCueAction = action;
                    if (action != null) Debug.Log($"[BDArmory.MissileFire]: Cue vessel={vessel.vesselName} {threatCue}{(shotCue != null ? " | " + shotCue : "")}");
                }
            }
            if (threatCue == null && shotCue == null && rwrCue == null) return;
            cueStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold };
            float y = Screen.height * 0.22f;
            if (threatCue != null) { DrawCue(y, threatCue, threatCueColor); y += 28f; }
            if (rwrCue != null) { DrawCue(y, rwrCue, rwrCueColor); y += 28f; }
            if (shotCue != null) DrawCue(y, shotCue, shotCueColor);
        }

        static void DrawCue(float y, string text, Color color)
        {
            cueStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(1f, y + 1f, Screen.width, 28f), text, cueStyle);
            cueStyle.normal.textColor = color;
            GUI.Label(new Rect(0f, y, Screen.width, 28f), text, cueStyle);
        }

        /// <summary>
        /// The missile guiding on this craft that arrives first, among those it has warning of: radar missiles with the radar warning
        /// receiver on, heat seekers once they are close enough to see.
        /// </summary>
        MissileBase FirstWarnedMissile(out float timeToGo, out float distance)
        {
            MissileBase first = null;
            timeToGo = float.MaxValue; distance = 0f;
            if (BDATargetManager.FiredMissiles == null) return null;
            bool radarWarning = rwr != null && rwr.rwrEnabled;
            foreach (var weapon in BDATargetManager.FiredMissiles)
            {
                if (weapon is not MissileBase m || m.vessel == null || !m.HasFired || m.HasExploded || m.HasMissed || m.targetVessel == null || m.targetVessel.Vessel != vessel) continue;
                if (m.TargetingMode != MissileBase.TargetingModes.Radar && m.TargetingMode != MissileBase.TargetingModes.Heat) continue;
                Vector3 toUs = vessel.CoM - m.vessel.CoM;
                float range = toUs.magnitude;
                if (m.TargetingMode == MissileBase.TargetingModes.Radar ? !radarWarning : range > heatWarningRange) continue;
                float closing = Vector3.Dot(m.vessel.Velocity() - vessel.Velocity(), toUs / Mathf.Max(range, 1f));
                if (closing <= 0f || range / closing >= Mathf.Min(timeToGo, maxCueTime)) continue;
                first = m; timeToGo = range / closing; distance = range;
            }
            return first;
        }

        string ThreatCue(out string action)
        {
            action = null;
            var seeker = FirstWarnedMissile(out float time, out float distance);
            if (seeker == null) return null;
            bool radar = seeker.TargetingMode == MissileBase.TargetingModes.Radar;
            string head = FormattableString.Invariant($"{(radar ? "RADAR" : "HEAT")} {time:0.0} s");
            action = radar ? ChaffCue(seeker, time, distance) : FlareCue(seeker, time, distance);
            return action != null ? head + " · " + action : head;
        }

        /// <summary>The warning receiver's top threat once it is more than a search sweep: who holds a lock on us or has launched, where, and about how far.</summary>
        string RwrCue()
        {
            if (rwr == null || !rwr.rwrEnabled) return null;
            var top = rwr.TopThreat();
            if (top == null || top.State < BDArmory.Radar.RadarWarningReceiver.EmitterStates.Lock) return null;
            Vector3 up = vessel.up, heading = Vector3.ProjectOnPlane(vessel.Velocity(), up), toIt = Vector3.ProjectOnPlane(top.position - vessel.CoM, up);
            int clock = Mathf.RoundToInt(Vector3.SignedAngle(heading, toIt, up) / 30f);
            clock = ((clock % 12) + 12) % 12;
            int locks = rwr.LockCount();
            return FormattableString.Invariant($"{(top.State == BDArmory.Radar.RadarWarningReceiver.EmitterStates.Launch ? "LAUNCH" : "SPIKE")} · {top.vessel.vesselName} · {(clock == 0 ? 12 : clock)} o'clock · ~{rwr.EstimatedRange(top) / 1000f:0} km{(locks > 1 ? " · +" + (locks - 1) : "")}");
        }

        string ChaffCue(MissileBase seeker, float time, float distance)
        {
            int stock = Math.Min(CountermeasureStock(CMDropper.CountermeasureTypes.Chaff), maxRelease);
            if (seeker.HomingOnJam) return "JAMMER OFF"; // It is following our jamming, and chaff won't pull it while it does.
            if (stock <= 0) return null;
            if (!seeker.smartSeeker)
            {
                float p = ChaffPatches.BundleChance(seeker, vessel, out float beaming, out bool seduced);
                if (p < 0f || seduced) return seduced ? "DECOYED" : null;
                return p >= 0.5f * beaming ? "CHAFF" : "BEAM " + BeamSide(seeker);
            }
            if (ChaffPatches.Resolved(seeker, vessel)) return null;
            int needed = ChaffPatches.SmartBundlesNeeded(seeker, vessel, out bool recued);
            if (needed > 0 && needed <= stock)
            {
                // The same release window the AI pilots use (PlanChaff): late enough that the bloom outlasts the rest of the flight.
                float resolveTime = distance > 0f ? time * seeker.resolveRange / distance : 0f;
                return time <= resolveTime + (recued ? 1f : 4f) ? "CHAFF ×" + needed : "HOLD CHAFF";
            }
            int beamNeeded = ChaffPatches.SmartBundlesNeeded(seeker, vessel, out _, true);
            return beamNeeded > 0 && beamNeeded <= stock ? "BEAM " + BeamSide(seeker) : null;
        }

        string FlareCue(MissileBase seeker, float time, float distance)
        {
            if (CountermeasureStock(CMDropper.CountermeasureTypes.Flare) <= 0) return null;
            float p = HeatPatches.FlareChance(seeker, out _, out bool captured);
            if (captured) return "DECOYED";
            if (!(p > 0f)) return null;
            if (!seeker.smartSeeker) return "FLARE";
            if (!HeatPatches.SmartFlareWorth(seeker, vessel, out _)) return null;
            // The same release window the AI pilots use (PlanFlares).
            float resolveTime = distance > 0f ? time * seeker.resolveRange / distance : 0f;
            return time <= resolveTime + StealthMath.FlareMemory + StealthMath.FlareLead ? "FLARE" : "HOLD FLARE";
        }

        /// <summary>The shorter turn that puts the missile on the beam: away from it while it is ahead of the wing line, towards it once behind.</summary>
        string BeamSide(MissileBase seeker)
        {
            Vector3 up = vessel.up, heading = Vector3.ProjectOnPlane(vessel.Velocity(), up), toMissile = Vector3.ProjectOnPlane(seeker.vessel.CoM - vessel.CoM, up);
            bool onRight = Vector3.Dot(Vector3.Cross(up, heading), toMissile) > 0f, ahead = Vector3.Dot(heading, toMissile) > 0f;
            return onRight == ahead ? "LEFT" : "RIGHT";
        }

        string ShotCue()
        {
            var missile = CurrentMissile;
            if (missile == null || missile.HasFired || !missile.engageAir) return null;
            Vessel target = currentTarget != null ? currentTarget.Vessel
                : vesselRadarData != null && vesselRadarData.locked ? vesselRadarData.lockedTargetData.vessel
                : missile.TargetingMode == MissileBase.TargetingModes.Heat && missile.heatTarget.exists ? missile.heatTarget.vessel : null;
            if (target == null || target.LandedOrSplashed || target.IsMissile()) return null;
            float p = ShotModel.Estimate(missile, vessel, target);
            return p >= 0f ? FormattableString.Invariant($"{missile.GetShortName()} · {100f * p:0}%") : null;
        }
    }
}
