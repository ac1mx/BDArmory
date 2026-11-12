using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.CounterMeasure;
using BDArmory.Damage;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>Per-target ECM data captured from VesselECMJInfo, re-applied on every signature read.</summary>
    internal class EcmData
    {
        public float[] Factors = Array.Empty<float>();   // rcsReductionFactor of each active rcsReduction device
        public float Jam;                               // summed noise jammer strength (signalSpam)
        public float LockBreak;                         // summed lockbreaker strength
        public float StockLockbreak = 1f;
        public float Parts = 1f;
        public int Version = -1;
        public bool Overridden;
        public Patches.Skin Skin;                       // the craft's skin record, resolved once

        public float PartsCombined()
        {
            if (Version != Settings.Version)
            {
                Version = Settings.Version;
                Parts = Patches.CombineParts(new List<float>(Factors));
            }
            return Parts;
        }
    }

    /// <summary>Which sensor is currently evaluating lock signatures (set around BDA's scan functions).</summary>
    internal static class Ctx
    {
        static readonly Vector3[] origins = new Vector3[8];
        static readonly bool[] holds = new bool[8], seekers = new bool[8];
        static readonly Vessel[] tracked = new Vessel[8];
        static int depth;
        public static bool Active => depth > 0 && depth <= origins.Length;
        public static Vector3 Origin => origins[depth - 1];
        /// <summary>The sensor is a missile's own radar seeker.</summary>
        public static bool Seeker => seekers[depth - 1];
        /// <summary>The sensor already tracks v (track is held past the acquisition range). No tracked vessel = whichever it finds.</summary>
        public static bool Holds(Vessel v) => holds[depth - 1] && (tracked[depth - 1] == null || tracked[depth - 1] == v);
        public static void Push(Vector3 origin, bool hold, Vessel trackedVessel = null, bool seeker = false)
        {
            if (depth < origins.Length) { origins[depth] = origin; holds[depth] = hold; tracked[depth] = trackedVessel; seekers[depth] = seeker; }
            depth++;
        }
        public static void Pop()
        {
            if (depth <= 0) return;
            depth--;
            if (depth < tracked.Length) tracked[depth] = null;
        }
    }

    internal static class Patches
    {
        static readonly ConditionalWeakTable<TargetInfo, EcmData> ecm = new ConditionalWeakTable<TargetInfo, EcmData>();
        /// <summary>What the last RCS render found on a craft's skin.</summary>
        internal sealed class Skin
        {
            public float Coating = 1f;   // area-weighted reflectivity
            public int Hidden;           // parts whose stealth hull sits under reflective armour
        }
        static readonly ConditionalWeakTable<Vessel, Skin> skins = new ConditionalWeakTable<Vessel, Skin>();

        /// <summary>Coating factor of the craft last rendered in the editor.</summary>
        public static float EditorCoating = 1f;
        public static int EditorRenders, EditorHidden;
        /// <summary>Set by the report to render one stock RCS snapshot (no per-part reflectivity limit).</summary>
        public static bool RenderStock;

        /// <summary>Live ECM data for a target, if any radar has read its signature.</summary>
        public static bool TryEcm(TargetInfo ti, out EcmData e) => ecm.TryGetValue(ti, out e);

        // ---- shared rules ----

        /// <summary>Stealth-part stacking under the current rules (no limit applied; see StealthMath.LimitedReduction).</summary>
        public static float CombineParts(List<float> factors)
        {
            if (factors.Count == 0) return 1f;
            return StealthMath.CombineReduction(factors, !Settings.Enabled ? false : Settings.DiminishingStacking, 0f);
        }

        public static float Coating(Vessel v) => v != null && skins.TryGetValue(v, out Skin s) ? s.Coating : 1f;
        public static int HiddenHullParts(Vessel v) => v != null && skins.TryGetValue(v, out Skin s) ? s.Hidden : 0;

        /// <summary>
        /// Noise, lockbreak and stealth factors of the jammers on a craft that pass the filter, stacked in BDArmory's order
        /// (VesselECMJInfo.UpdateJammerStrength: each further noise source counts ×0.75, each further lockbreaker ×0.65).
        /// </summary>
        public static void EcmTotals(Vessel v, Func<ModuleECMJammer, bool> include, out float noise, out float lockBreak, List<float> factors)
        {
            noise = 0f; lockBreak = 0f; factors.Clear();
            float nw = 1f, lw = 1f;
            foreach (ModuleECMJammer j in BDArmory.Utils.VesselModuleRegistry.GetModules<ModuleECMJammer>(v))
            {
                if (j == null || j.isMissileECM || !include(j)) continue;
                if (j.signalSpam) { noise += nw * j.jammerStrength; nw *= 0.75f; }
                if (j.lockBreaker) { lockBreak += lw * j.lockBreakerStrength; lw *= 0.65f; }
                if (j.rcsReduction) factors.Add(j.rcsReductionFactor);
            }
        }

        /// <summary>Signal a missile's own radar seeker gets from a craft at rangeKm, for a given ECM state (see SignaturePostfix).</summary>
        public static float SeekerSignal(Vessel v, float baseRcs, float noise, float lockBreak, List<float> factors, float rangeKm)
        {
            float rcsr = StealthMath.LimitedReduction(CombineParts(factors), Coating(v), Settings.StealthLimit);
            float reduced = Settings.ApplyRcsReduction ? baseRcs * rcsr : baseRcs;
            return reduced * StealthMath.JamFactor(lockBreak, baseRcs * rcsr, rangeKm, Settings.BurnThroughKm) + (Settings.HomeOnJam ? noise / 100f : 0f);
        }

        // ---- curve conversion (lazy, flight only) ----

        static void EnsureRadar(ModuleRadar radar)
        {
            if (radar == null) return;
            bool convert = !Sensors.IsSonar(radar);
            Curves.Ensure(radar.radarDetectionCurve, convert);
            Curves.Ensure(radar.radarLockTrackCurve, convert, lockCurve: true);
        }

        internal static void EnsureRadarPrefix(ModuleRadar radar) => EnsureRadar(radar);

        internal static void ScanLockPrefix(ModuleRadar radar)
        {
            EnsureRadar(radar);
            Ctx.Push(radar != null ? radar.currPosition : Vector3.zero, false);
        }

        internal static void LockTrackPrefix(Ray ray, ModuleRadar radar, bool locked, Vessel lockedVessel)
        {
            EnsureRadar(radar);
            Ctx.Push(ray.origin, locked, lockedVessel);
        }

        internal static void MissileLockPrefix(Ray ray, MissileBase missile)
        {
            bool hold = false, radarSeeker = false;
            Vessel trackedVessel = null;
            if (missile != null)
            {
                FloatCurve c = missile.activeRadarLockTrackCurve;
                SeekerCache sc = seekerCache.GetValue(missile, _ => new SeekerCache());
                if (sc.Version != Settings.Version)
                {
                    sc.Version = Settings.Version;
                    sc.Radar = !Sensors.IsTorpedo(missile);
                    sc.Scale = Sensors.SeekerScale(missile, c);
                }
                radarSeeker = sc.Radar;
                Curves.Ensure(c, radarSeeker, sc.Scale, missile.activeRadarRange, lockCurve: true);
                // Seeker already tracking: keep that target a little longer, like a radar lock. Other vessels in view get no boost.
                trackedVessel = missile.radarTarget.exists ? missile.radarTarget.vessel : null;
                hold = trackedVessel != null;
            }
            Ctx.Push(ray.origin, hold, trackedVessel, radarSeeker);
        }

        /// <summary>Per-missile seeker facts that only change with the settings (each is a virtual call or a native curve read).</summary>
        sealed class SeekerCache { public int Version = -1; public float Scale = 1f; public bool Radar = true; }
        static readonly ConditionalWeakTable<MissileBase, SeekerCache> seekerCache = new ConditionalWeakTable<MissileBase, SeekerCache>();

        internal static void PopFinalizer() => Ctx.Pop();

        // ---- coatings: clamp each part's reflectivity to the stealth limit while BDA renders the RCS ----

        static readonly List<HitpointTracker> renderParts = new List<HitpointTracker>(64);
        static readonly List<float> renderRefl = new List<float>(64), areaBuf = new List<float>(64), reflBuf = new List<float>(64);

        internal static void RenderPrefix(Vessel v)
        {
            renderParts.Clear(); renderRefl.Clear(); areaBuf.Clear(); reflBuf.Clear();
            if (v?.parts == null) return;
            bool clamp = Settings.Enabled && !RenderStock;
            float limit = clamp ? Settings.StealthLimit : 0f;
            int hidden = 0;
            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                HitpointTracker hp = p.GetComponent<HitpointTracker>();
                if (hp == null) continue;
                float stock = hp.radarReflectivity, r = stock;
                if (hp.ArmorTypeNum > 1f && hp.Armor > 0f)
                {
                    float hull = hp.hullRadarReturnFactor, armour = hp.armorRadarReturnFactor;
                    if (clamp && Settings.SkinStacking) r = StealthMath.SkinReflectivity(hull, armour, hp.Armor);
                    if (hull < 1f && armour >= 1f && r > hull + 0.05f) hidden++;
                }
                areaBuf.Add(hp.armorVolume); reflBuf.Add(r);
                if (clamp && r < limit) r = limit;
                if (r != stock)
                {
                    renderParts.Add(hp); renderRefl.Add(stock);
                    hp.radarReflectivity = r;
                }
            }
            float c = StealthMath.CoatingFactor(areaBuf, reflBuf, limit);
            if (RenderStock) return;   // report-only stock render: don't overwrite the live skin data
            if (HighLogic.LoadedSceneIsEditor) { EditorCoating = c; EditorHidden = hidden; EditorRenders++; }
            else { Skin s = skins.GetValue(v, _ => new Skin()); s.Coating = c; s.Hidden = hidden; }
        }

        internal static void RenderFinalizer()
        {
            for (int i = 0; i < renderParts.Count; i++) if (renderParts[i] != null) renderParts[i].radarReflectivity = renderRefl[i];
            renderParts.Clear(); renderRefl.Clear();
        }

        // ---- signatures ----

        static EcmData Data(TargetInfo ti) => ecm.GetValue(ti, _ => new EcmData());

        /// <summary>
        /// Stock resets reduced/modified signatures to the base RCS on every read, so RCS-reduction parts never reduced detection
        /// while still driving the lockbreak factor to 0. Here the same numbers drive detection and locking.
        /// </summary>
        internal static void SignaturePostfix(Vessel v, TargetInfo __result)
        {
            TargetInfo ti = __result;
            if (ti == null) return;
            EcmData e = Data(ti);
            float baseSig = ti.radarBaseSignature;
            if (!Settings.Enabled)
            {
                if (e.Overridden)
                {
                    ti.radarLockbreakFactor = e.StockLockbreak;
                    ti.radarJammingDistance = e.Jam > 0f ? StealthMath.StockJamRadius(e.Jam, baseSig) : 0f;
                    e.Overridden = false;
                }
                return;
            }
            e.Overridden = true;
            if (e.Skin == null)
            {
                Vessel owner = v != null ? v : ti.Vessel;
                if (owner != null) e.Skin = skins.GetValue(owner, _ => new Skin());
            }
            float rcsr = StealthMath.LimitedReduction(e.PartsCombined(), e.Skin != null ? e.Skin.Coating : 1f, Settings.StealthLimit);
            float reduced = Settings.ApplyRcsReduction ? baseSig * rcsr : baseSig;
            float detect = reduced + (Settings.JammerReveals ? e.Jam / 100f : 0f);
            ti.radarRCSReducedSignature = reduced;
            ti.radarModifiedSignature = detect;
            if (e.Jam > 0f) ti.radarJammingDistance = Settings.BoundJamRadius ? StealthMath.BoundedJamRadius(e.Jam, baseSig, Settings.ReferenceRcs) : StealthMath.StockJamRadius(e.Jam, baseSig);

            float rangeKm = Ctx.Active && v != null ? Vector3.Distance(v.CoM, Ctx.Origin) * 0.001f : Settings.BurnThroughKm;
            float hold = Ctx.Active && Ctx.Holds(v) ? Settings.HoldBoost : 1f;
            float jam = StealthMath.JamFactor(e.LockBreak, baseSig * rcsr, rangeKm, Settings.BurnThroughKm);
            float lockSignal = reduced * jam * hold;
            // Home-on-jam: a missile's own seeker can track the noise a jammer radiates.
            if (Settings.HomeOnJam && Ctx.Active && Ctx.Seeker) lockSignal += e.Jam / 100f;
            // BDA multiplies the detection signature by this factor in lock paths.
            ti.radarLockbreakFactor = detect > 0f ? lockSignal / detect : 0f;
        }

        static readonly List<float> factorBuf = new List<float>(8);

        internal static void JammerPostfix(VesselECMJInfo __instance)
        {
            TargetInfo ti = __instance.ti;
            if (ti == null) return;
            EcmData e = Data(ti);
            e.StockLockbreak = ti.radarLockbreakFactor;
            factorBuf.Clear();
            List<ModuleECMJammer> list = __instance.jammers;
            if (list != null) foreach (ModuleECMJammer j in list) if (j != null && j.rcsReduction) factorBuf.Add(j.rcsReductionFactor);
            e.Factors = factorBuf.ToArray();
            e.Jam = __instance.jammerStrength;
            e.LockBreak = __instance.lockBreakStrength;
            e.Version = -1;
            if (!Settings.Enabled) { e.Overridden = false; return; }
            __instance.rcsr = StealthMath.LimitedReduction(e.PartsCombined(), Coating(__instance.vessel), Settings.StealthLimit);
            SignaturePostfix(__instance.vessel, ti);
        }

        struct Escort { public Vessel Vessel; public VesselECMJInfo Info; public MissileFire Wm; }
        static readonly List<Escort> escorts = new List<Escort>(8);
        static float escortsTime = -1f;

        /// <summary>Stock: signature - J/100, clamped at 0 (escort jammers make stealth targets unlockable). Soft: S/(S+J).</summary>
        internal static bool StandoffPrefix(Vessel v, BDTeam team, Vector3 position, Vessel targetV, float signature, ref float __result)
        {
            if (!Settings.Enabled || !Settings.SoftStandoff) return true;
            if (targetV == null || targetV.ActiveController().WM == null || signature == 0f) { __result = 1f; return false; }
            // BDArmory asks this once per target per radar scan and walks every loaded vessel each time. The set of craft that
            // are jamming at all only changes when a jammer is switched, so collect it once per time step.
            if (Time.time != escortsTime)
            {
                escortsTime = Time.time;
                escorts.Clear();
                foreach (Vessel other in BDATargetManager.LoadedVessels)
                {
                    if (other == null || !other.loaded || other.vesselType == VesselType.Debris) continue;
                    VesselECMJInfo info = other.gameObject.GetComponent<VesselECMJInfo>();
                    if (!info || info.lockBreakStrength <= 0f) continue;
                    MissileFire wm = other.ActiveController().WM;
                    if (wm) escorts.Add(new Escort { Vessel = other, Info = info, Wm = wm });
                }
            }
            float sum = 0f;
            if (team != null && escorts.Count > 0)
            {
                Vector3 toTarget = targetV.CoM - position;
                Vector3 targetDir = toTarget.normalized;
                float targetSqr = toTarget.sqrMagnitude;
                for (int i = 0; i < escorts.Count; i++)
                {
                    Escort e = escorts[i];
                    if (e.Vessel == null || e.Vessel == v || e.Vessel == targetV || !e.Wm || team.IsFriendly(e.Wm.Team)) continue;
                    Vector3 toJammer = e.Vessel.CoM - position;
                    float cos = Vector3.Dot(targetDir, toJammer.normalized);
                    float w = Mathf.Clamp01(cos * cos * cos) * Mathf.Clamp(1f - Mathf.Log10(toJammer.sqrMagnitude / targetSqr), 0f, 3f);
                    sum += w * e.Info.lockBreakStrength;
                }
            }
            __result = StealthMath.StandoffModifier(signature, sum, soft: true);
            return false;
        }

        // ---- part info (editor tooltips; computed with the rules active at game start) ----

        static readonly float[] infoRcs = { 10f, 1f, 0.1f };

        static void AppendRanges(StringBuilder sb, Sensor s)
        {
            if (!Settings.Enabled) return;
            sb.AppendLine();
            sb.AppendLine("<b><color=#99ccff>BDAStealth ranges</color></b>");
            foreach (float rcs in infoRcs)
            {
                Ranges g = Analysis.Evaluate(s, Target.Plain(rcs, 300f));
                var parts = new List<string>();
                if (s.CanScan && s.Det != null) parts.Add("seen " + Analysis.Km(g.Detect));
                if (s.CanLock) parts.Add("lock " + Analysis.Km(g.Lock) + ", vs ECM " + Analysis.Km(g.LockEcm));
                sb.AppendLine($"- {Analysis.Rcs(rcs)}: {string.Join(" · ", parts)}");
            }
            sb.AppendLine("<i>ECM = 300 lockbreaker. Live numbers: Radar & Stealth window.</i>");
        }

        internal static void RadarInfoPostfix(ModuleRadar __instance, ref string __result)
        {
            try
            {
                if (Sensors.IsSonar(__instance) || !(__instance.canScan || __instance.canLock)) return;
                var sb = new StringBuilder(__result); AppendRanges(sb, Sensors.Of(__instance)); __result = sb.ToString();
            }
            catch (Exception e) { Debug.LogWarning("[BDAStealth] radar info: " + e.Message); }
        }

        internal static void MissileInfoPostfix(MissileLauncher __instance, ref string __result)
        {
            try
            {
                if (!Sensors.HasRadarSeeker(__instance) || Sensors.IsTorpedo(__instance)) return;
                var sb = new StringBuilder(__result); AppendRanges(sb, Sensors.Of(__instance)); __result = sb.ToString();
            }
            catch (Exception e) { Debug.LogWarning("[BDAStealth] missile info: " + e.Message); }
        }

        internal static void JammerInfoPostfix(ModuleECMJammer __instance, ref string __result)
        {
            if (!Settings.Enabled) return;
            var sb = new StringBuilder(__result);
            sb.AppendLine();
            sb.AppendLine("<b><color=#99ccff>BDAStealth</color></b>");
            if (__instance.rcsReduction)
                sb.AppendLine($"- Alone: detection range ×{Mathf.Pow(Mathf.Max(__instance.rcsReductionFactor, Settings.StealthLimit), 1f / Settings.RangeExponent):0.00}. Coatings + stealth parts stop at ×{Settings.StealthLimit:0.###} RCS.");
            if (__instance.lockBreaker && __instance.lockBreakerStrength > 0f)
            {
                float L = __instance.lockBreakerStrength, b = Settings.BurnThroughKm;
                sb.AppendLine($"- Lockbreak on a 1 m² craft: lock signal ×{StealthMath.JamFactor(L, 1f, b * 0.3f, b):0.00} at {b * 0.3f:0.#} km, ×{StealthMath.JamFactor(L, 1f, b, b):0.00} at {b:0} km, ×{StealthMath.JamFactor(L, 1f, b * 3f, b):0.00} at {b * 3f:0} km.");
            }
            if (__instance.signalSpam && __instance.jammerStrength > 0f)
                sb.AppendLine($"- Noise: +{__instance.jammerStrength / 100f:0.#} m² to detection{(Settings.HomeOnJam ? " and to missile seeker lock" : "")} while on; jams radars within ~{StealthMath.BoundedJamRadius(__instance.jammerStrength, 1f, Settings.ReferenceRcs) / 1000f:0.#} km.");
            __result = sb.ToString();
        }
    }
}
