using System;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>Target description for range predictions.</summary>
    internal struct Target
    {
        public float BaseRcs;        // rendered RCS, m^2 (includes coatings; after ground clutter if applicable)
        public float Coating;        // area-weighted skin reflectivity used for the stealth limit (1 = none)
        public float Parts;          // combined rcsReduction of stealth parts under the current stacking rule
        public float StockParts;     // combined factor under stock stacking (product x count)
        public float Override;       // rcsOverride, <=0 none
        public float Jam;            // noise jammer strength when ECM on
        public float LockBreak;      // lockbreaker strength when ECM on
        public float StockBaseRcs;   // rendered RCS without the per-part reflectivity limit (stock comparison); <=0 = same as BaseRcs

        public static Target Plain(float rcs, float lockBreak = 0f) =>
            new Target { BaseRcs = rcs, Coating = 1f, Parts = 1f, StockParts = 1f, Override = -1f, LockBreak = lockBreak };

        public float Base => Override > 0f ? Override : BaseRcs;
        /// <summary>RCS-reduction factor radars see under the current rules.</summary>
        public float Reduction => StealthMath.LimitedReduction(Parts, Coating, Settings.StealthLimit);
        /// <summary>RCS radars detect under the current rules (ECM off).</summary>
        public float Effective => Settings.Enabled && Settings.ApplyRcsReduction ? Base * Reduction : Base;
    }

    internal struct Ranges
    {
        public float Detect, Lock, DetectEcm, LockEcm, Hold;     // km
        public float StockDetect, StockLock, StockLockEcm;       // km
    }

    internal static class Analysis
    {
        static float Req(FloatCurve det, float detMax, FloatCurve lck, float r) => r >= detMax ? float.PositiveInfinity : Mathf.Max(lck.Evaluate(r), det.Evaluate(r));

        public static Ranges Evaluate(Sensor s, Target t)
        {
            var o = new Ranges();
            float baseSig = t.Base;
            bool hasDet = s.Det != null && s.CanScan && Curves.Range(s.Det, out _, out _);
            bool hasLck = s.Lock != null && s.CanLock && Curves.Range(s.Lock, out _, out _);
            float dMin = 0f, dMax = 0f, lMin = 0f, lMax = 0f;
            if (hasDet) { Curves.Range(s.Det, out dMin, out dMax); dMin *= s.Scale; dMax *= s.Scale; }
            if (hasLck)
            {
                Curves.Range(s.Lock, out lMin, out lMax); lMin *= s.Scale; lMax *= s.Scale;
                if (s.LockMax > 0f) { lMin = 0f; lMax = s.LockMax; }
            }
            float k = s.ToKm;

            // ---- current rules ----
            if (Settings.Enabled)
            {
                FloatCurve det = hasDet ? Curves.Effective(s.Det, s.Convert, s.Scale) : null;
                FloatCurve lck = hasLck ? Curves.Effective(s.Lock, s.Convert, s.Scale, s.IsSeeker ? s.LockMax : -1f, lockCurve: true) : null;
                float reduced = t.Effective;
                float rcsJs = baseSig * t.Reduction;
                float detectEcm = reduced + (Settings.JammerReveals ? t.Jam / 100f : 0f);
                if (hasDet)
                {
                    o.Detect = k * StealthMath.MaxRange(det.Evaluate, dMin, dMax, _ => reduced);
                    o.DetectEcm = k * StealthMath.MaxRange(det.Evaluate, dMin, dMax, _ => detectEcm);
                }
                if (hasLck)
                {
                    Func<float, float> req = r => hasDet ? Req(det, dMax, lck, r) : lck.Evaluate(r);
                    float hold = Settings.HoldBoost;
                    o.Lock = k * StealthMath.MaxRange(req, lMin, lMax, _ => reduced);
                    o.Hold = k * StealthMath.MaxRange(req, lMin, lMax, _ => reduced * hold);
                    float hoj = s.IsSeeker && Settings.HomeOnJam ? t.Jam / 100f : 0f;
                    o.LockEcm = k * StealthMath.MaxRange(req, lMin, lMax, r => reduced * StealthMath.JamFactor(t.LockBreak, rcsJs, r * k, Settings.BurnThroughKm) + hoj);
                }
            }

            // ---- stock BDArmory 1.13 rules ----
            // Detection reads the base RCS (reduction parts are reset away); lockbreak uses the reduced value and clamps at 0.
            if (t.StockBaseRcs > 0f && t.Override <= 0f) baseSig = t.StockBaseRcs;
            float sReduced = baseSig * t.StockParts;
            float sModified = sReduced + t.Jam / 100f;
            float sLbf = sReduced <= 0f ? 0f : Mathf.Max(Mathf.Clamp01(sReduced / sModified) * (1f - t.LockBreak / sReduced / 100f), 0f);
            FloatCurve sDet = hasDet ? Curves.Original(s.Det) : null, sLck = hasLck ? Curves.Original(s.Lock) : null;
            float sdMin = dMin / s.Scale, sdMax = dMax / s.Scale;
            if (hasDet) o.StockDetect = k * StealthMath.MaxRange(sDet.Evaluate, sdMin, sdMax, _ => baseSig);
            if (hasLck)
            {
                float slMin = s.LockMax > 0f ? 0f : lMin / s.Scale, slMax = s.LockMax > 0f ? s.LockMax : lMax / s.Scale;
                Func<float, float> sReq = r => hasDet ? Req(sDet, sdMax, sLck, r) : sLck.Evaluate(r);
                o.StockLock = k * StealthMath.MaxRange(sReq, slMin, slMax, _ => baseSig);
                o.StockLockEcm = sLbf <= 0f ? 0f : k * StealthMath.MaxRange(sReq, slMin, slMax, _ => baseSig * sLbf);
            }
            if (!Settings.Enabled) { o.Detect = o.DetectEcm = o.StockDetect; o.Lock = o.Hold = o.StockLock; o.LockEcm = o.StockLockEcm; }
            return o;
        }

        public static string Km(float km) => km <= 0f ? "never" : km >= 100f ? $"{km:0} km" : km >= 10f ? $"{km:0.0} km" : $"{km:0.0#} km";
        public static string Rcs(float m2) => m2 >= 10f ? $"{m2:0} m²" : m2 >= 1f ? $"{m2:0.0} m²" : $"{m2:0.###} m²";
    }
}
