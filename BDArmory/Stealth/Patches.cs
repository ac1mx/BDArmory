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
using BDArmory.WeaponMounts;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>Per-target ECM data captured from VesselECMJInfo, re-applied on every signature read.</summary>
    internal class EcmData
    {
        public float[] Factors = Array.Empty<float>();   // rcsReductionFactor of each active rcsReduction device
        public ModuleECMJammer[] Devices = Array.Empty<ModuleECMJammer>();   // the devices, same order (their damage wears the factor)
        public float Jam;                               // summed noise jammer strength (signalSpam)
        public float LockBreak;                         // summed lockbreaker strength
        public float StockLockbreak = 1f;
        public float Parts = 1f;
        public int Version = -1;
        float devicesChecked = float.NegativeInfinity;
        public bool Overridden;
        public Patches.Skin Skin;                       // the craft's skin record, resolved once

        static readonly List<float> combineBuf = new List<float>(8);

        public float PartsCombined()
        {
            bool wear = Settings.BattleDamage && Devices.Length > 0 && HighLogic.LoadedSceneIsFlight;
            if (Version != Settings.Version || (wear && Time.time - devicesChecked >= Patches.ExposureInterval))
            {
                Version = Settings.Version;
                devicesChecked = Time.time;
                combineBuf.Clear();
                for (int i = 0; i < Factors.Length; i++)
                    combineBuf.Add(wear && i < Devices.Length ? Patches.Worn(Factors[i], Patches.DeviceDamage(Devices[i])) : Factors[i]);
                Parts = Patches.CombineParts(combineBuf);
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
            // Battle damage and open bays since the last render (see Exposure).
            public readonly List<SkinPart> Parts = new List<SkinPart>();
            public float Rendered;                   // sum of area x reflectivity the last render saw
            public float RenderedRcs;                // the RCS that render gave (m²)
            public float RenderedSkinArea;           // skin area (bay contents not included) the render saw
            public float ConventionalRcs;            // RCS a conventional airframe of this size would render (m²)
            public float Breach, BreachArea;         // skin left bare where parts were lost (kept across renders)
            public float Factor = 1f;                // RCS now / RCS rendered
            public float Checked = float.NegativeInfinity, Logged = 1f;
        }

        /// <summary>One part as the last render saw it: its weight (area), full hitpoints, and the bay that hides it or that it is the doors of.</summary>
        internal struct SkinPart
        {
            public HitpointTracker Hp;
            public float Area, MaxHp;
            public Bay Bay;
            public bool Inside;   // carried inside the bay (its missiles, a deployable rail): seen only as far as the bay is open
        }

        /// <summary>A weapons bay: cargo bay doors or a deployable rail, and how far it is open (0 shut, 1 open).</summary>
        internal sealed class Bay
        {
            public ModuleAnimateGeneric Doors; public float Closed;
            public BDDeployableRail Rail;
            public float Open => Rail != null ? Rail.DeployFraction : Doors != null ? Mathf.Clamp01(Mathf.Abs(Doors.animTime - Closed)) : 0f;
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
                if (j.rcsReduction) factors.Add(Settings.BattleDamage && HighLogic.LoadedSceneIsFlight ? Worn(j.rcsReductionFactor, DeviceDamage(j)) : j.rcsReductionFactor);
            }
        }

        /// <summary>Signal a missile's own radar seeker gets from a craft at rangeKm, for a given ECM state (see SignaturePostfix).</summary>
        public static float SeekerSignal(Vessel v, float baseRcs, float noise, float lockBreak, List<float> factors, float rangeKm)
        {
            baseRcs *= Exposure(v);
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
            // In flight the parts the render sees are recorded, so damage and open bays can be measured against it (see Exposure).
            Skin live = clamp && Settings.BattleDamage && HighLogic.LoadedSceneIsFlight ? skins.GetValue(v, _ => new Skin()) : null;
            if (live != null) live.Parts.Clear();
            float rendered = 0f, skinArea = 0f;
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
                if (live != null)
                {
                    SkinPart sp = new SkinPart { Hp = hp, Area = hp.armorVolume > 0f ? hp.armorVolume : 1f, MaxHp = hp.GetMaxHitpoints() };
                    sp.Bay = BayOf(p, out sp.Inside);
                    live.Parts.Add(sp);
                    rendered += sp.Area * Mathf.Max(r, limit) * (sp.Inside ? sp.Bay.Open : 1f);
                    if (!sp.Inside) skinArea += sp.Area;
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
            else
            {
                Skin s = skins.GetValue(v, _ => new Skin()); s.Coating = c; s.Hidden = hidden;
                if (live != null)
                {
                    live.Rendered = rendered;
                    live.RenderedSkinArea = skinArea;
                    Vector3 size = v.vesselSize; // width, height, length
                    live.ConventionalRcs = ConventionalRcsPerArea * (size.x * size.y + size.y * size.z + size.x * size.z) / 3f;
                    live.Checked = float.NegativeInfinity;   // breaches from before this render still count
                }
            }
        }

        // ---- battle damage: a damaged stealth airframe and open weapons bays reflect more ----

        /// <summary>Seconds between re-measurements of a craft's skin.</summary>
        public const float ExposureInterval = 0.5f;
        /// <summary>
        /// Rendered RCS (m²) of a conventional fighter per m² of its mean face area (the average of its bounding box's front, side
        /// and top): the Su-47 renders 34.0 m² with a 220.5 m² mean face. Damaged skin and breaches reflect like such an airframe of
        /// the craft's size, so a stealth craft loses its shaping and coating where it is hit (F-22A L 1.7 m² intact, 24 m² for a
        /// conventional airframe its size; YF-23 0.68 m² / 35 m²).
        /// </summary>
        const float ConventionalRcsPerArea = 0.154f;
        /// <summary>A lost part leaves a bare breach of this fraction of its area: the torn joint, not the whole panel.</summary>
        const float BreachFraction = 0.25f;

        /// <summary>Reflectivity r worn toward bare metal (1) by a fraction w (damaged coating).</summary>
        public static float Worn(float r, float w) => r < 1f ? r + (1f - r) * Mathf.Clamp01(w) : r;

        /// <summary>Fraction of a part's hitpoints (armour integrity for an armour panel) it has lost.</summary>
        static float Damage(HitpointTracker hp, float maxHp)
        {
            float intact = hp.ArmorPanel ? hp.ArmorRemaining / 100f : maxHp > 0f ? hp.Hitpoints / maxHp : 1f;
            return Mathf.Clamp01(1f - intact);
        }


        /// <summary>Fraction of its hitpoints an RCS-reduction device has lost (a destroyed one counts as fully worn).</summary>
        public static float DeviceDamage(ModuleECMJammer j)
        {
            if (j == null || j.part == null) return 1f;
            HitpointTracker hp = j.part.GetComponent<HitpointTracker>();
            return hp == null ? 0f : Damage(hp, hp.GetMaxHitpoints());
        }

        /// <summary>
        /// The weapons bay a part belongs to: bay doors (part of the skin, inside = false), a deployable rail (stowed inside the
        /// airframe), or a missile carried on either (inside).
        /// </summary>
        static Bay BayOf(Part p, out bool inside)
        {
            Bay own = BayModule(p);
            inside = own != null && own.Rail != null;
            if (own != null || p.parent == null || p.FindModuleImplementing<MissileBase>() == null) return own;
            inside = true;
            return BayModule(p.parent);
        }

        static Bay BayModule(Part p)
        {
            BDDeployableRail rail = p.FindModuleImplementing<BDDeployableRail>();
            if (rail != null) return new Bay { Rail = rail };
            ModuleCargoBay cargo = p.FindModuleImplementing<ModuleCargoBay>();
            if (cargo != null && cargo.DeployModuleIndex >= 0 && cargo.DeployModuleIndex < p.Modules.Count && p.Modules[cargo.DeployModuleIndex] is ModuleAnimateGeneric doors)
                return new Bay { Doors = doors, Closed = cargo.closedPosition };
            return null;
        }

        /// <summary>
        /// RCS multiplier for what has happened to a craft since its last render, 1 = as rendered. Each part's lost hitpoints are
        /// that share of its skin damaged: bare metal that reflects like a conventional airframe of the craft's size, so it loses
        /// both coating and shaping; a lost part leaves a smaller breach that does the same. An open bay shows its contents. A
        /// conventional craft never goes above its render, so this only takes stealth away.
        /// </summary>
        public static float Exposure(Vessel v) => v != null && skins.TryGetValue(v, out Skin s) ? Exposure(v, s) : 1f;

        static float Exposure(Vessel v, Skin s)
        {
            if (!Settings.Enabled || !Settings.BattleDamage || !HighLogic.LoadedSceneIsFlight) return 1f;
            if (Time.time - s.Checked >= ExposureInterval) Measure(v, s);
            return s.Factor;
        }

        static readonly System.Diagnostics.Stopwatch exposureWatch = new System.Diagnostics.Stopwatch();
        static int exposureMeasures;

        static void Measure(Vessel v, Skin s)
        {
            s.Checked = Time.time;
            if (!(s.Rendered > 0f)) { s.Factor = 1f; return; }
            exposureWatch.Start();
            float limit = Settings.StealthLimit;
            float intact = 0f, bare = s.Breach;              // skin still as rendered (area x reflectivity) / skin area left bare
            float skinRefl = s.Breach, skinArea = s.BreachArea;
            for (int i = s.Parts.Count - 1; i >= 0; i--)
            {
                SkinPart sp = s.Parts[i];
                HitpointTracker hp = sp.Hp;
                if (hp == null || hp.part == null || hp.part.vessel != v)
                {
                    // Gone. Bay contents and ordnance that left (a launched missile) take nothing with them. A destroyed or torn-off
                    // part of the airframe leaves a bare breach where it was attached.
                    if (!sp.Inside && (hp == null || hp.part == null || hp.part.FindModuleImplementing<MissileBase>() == null))
                    {
                        float breach = BreachFraction * sp.Area;
                        s.Breach += breach; s.BreachArea += breach;
                        bare += breach; skinRefl += breach; skinArea += breach;
                    }
                    s.Parts[i] = s.Parts[s.Parts.Count - 1];
                    s.Parts.RemoveAt(s.Parts.Count - 1);
                    continue;
                }
                float r = hp.radarReflectivity;
                if (hp.ArmorTypeNum > 1f && hp.Armor > 0f && Settings.SkinStacking) r = StealthMath.SkinReflectivity(hp.hullRadarReturnFactor, hp.armorRadarReturnFactor, hp.Armor);
                r = Mathf.Max(r, limit);
                // Missiles and rails hanging out of an open bay: seen as far as it is open, at the rendered rate (a render with the
                // F-22A L's rails out gives 2.58 m² against 1.71 m² stowed, which this reproduces).
                if (sp.Inside) { intact += sp.Area * r * sp.Bay.Open; continue; }
                float d = Damage(hp, sp.MaxHp);
                intact += (1f - d) * sp.Area * r;
                bare += d * sp.Area;
                skinRefl += sp.Area * Worn(r, d); skinArea += sp.Area;
            }
            // Intact skin keeps the craft's rendered rate; bare skin reflects like a conventional airframe of the craft's size (never
            // less than the rendered rate of uncoated skin), and the whole never exceeds that conventional airframe.
            float conventional = Mathf.Max(s.ConventionalRcs, s.RenderedRcs);
            float barePerArea = s.RenderedRcs > 0f && s.RenderedSkinArea > 0f ? Mathf.Max(conventional / (s.RenderedRcs * s.RenderedSkinArea), 1f / s.Rendered) : 1f / s.Rendered;
            s.Factor = intact / s.Rendered + bare * barePerArea;
            if (s.RenderedRcs > 0f) s.Factor = Mathf.Min(s.Factor, conventional / s.RenderedRcs);
            if (skinArea > 0f) s.Coating = skinRefl / skinArea;
            exposureWatch.Stop();
            ++exposureMeasures;
            if (Mathf.Abs(s.Factor / s.Logged - 1f) > 0.1f)
            {
                s.Logged = s.Factor;
                Debug.Log($"[BDAStealth] Exposure: {v.vesselName} RCS x{s.Factor:0.00} of its render, {s.Factor * s.RenderedRcs:0.00} m² (bare/open skin {bare:0.0} of {s.RenderedSkinArea:0.0}, conventional {s.ConventionalRcs:0.0} m², breaches {s.BreachArea:0.0}); {exposureMeasures} measures, {exposureWatch.Elapsed.TotalMilliseconds / exposureMeasures * 1000:0} us each");
            }
        }

        internal static void RenderFinalizer(Vessel v, TargetInfo ti)
        {
            for (int i = 0; i < renderParts.Count; i++) if (renderParts[i] != null) renderParts[i].radarReflectivity = renderRefl[i];
            renderParts.Clear(); renderRefl.Clear();
            RecordRender(v, ti);
        }

        /// <summary>Remember a finished render's signature for the damage model (called later for a GPU read-back).</summary>
        internal static void RecordRender(Vessel v, TargetInfo ti)
        {
            if (RenderStock || ti == null || v == null || !HighLogic.LoadedSceneIsFlight || !skins.TryGetValue(v, out Skin s) || s.Parts.Count == 0) return;
            s.RenderedRcs = ti.radarBaseSignature;
            Vector3 size = v.vesselSize;
            Debug.Log($"[BDAStealth] Render: {v.vesselName} RCS {s.RenderedRcs:0.000} m², skin {s.Rendered:0.0} of area {s.RenderedSkinArea:0.0}, size {size.x:0.0}x{size.y:0.0}x{size.z:0.0} m, mean face {(size.x * size.y + size.y * size.z + size.x * size.z) / 3f:0.0} m², conventional {s.ConventionalRcs:0.0} m²");
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
            Vessel owner = v != null ? v : ti.Vessel;
            if (e.Skin == null && owner != null) e.Skin = skins.GetValue(owner, _ => new Skin());
            if (e.Skin != null && owner != null) baseSig *= Exposure(owner, e.Skin);
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
        static readonly List<ModuleECMJammer> deviceBuf = new List<ModuleECMJammer>(8);

        internal static void JammerPostfix(VesselECMJInfo __instance)
        {
            TargetInfo ti = __instance.ti;
            if (ti == null) return;
            EcmData e = Data(ti);
            e.StockLockbreak = ti.radarLockbreakFactor;
            factorBuf.Clear();
            List<ModuleECMJammer> list = __instance.jammers;
            deviceBuf.Clear();
            if (list != null) foreach (ModuleECMJammer j in list) if (j != null && j.rcsReduction) { factorBuf.Add(j.rcsReductionFactor); deviceBuf.Add(j); }
            e.Factors = factorBuf.ToArray();
            e.Devices = deviceBuf.ToArray();
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
