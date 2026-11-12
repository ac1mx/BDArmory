using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>
    /// Converts radar / missile-seeker threshold curves in place (flight only) and keeps the original keys,
    /// so settings changes rebuild from the original and disabling restores it exactly.
    /// Part prefabs are never mutated; previews use converted copies.
    /// </summary>
    internal static class Curves
    {
        class State
        {
            public AnimationCurve Original;   // the AnimationCurve object BDA loaded
            public FloatCurve OriginalEval;   // evaluator over a copy of the original keys
            public float TMin, TMax;
            public float[] OrigTimes;
            public AnimationCurve Installed;  // our converted curve while applied
            public int Version = -1;
            public float AppliedScale = -1f, AppliedMax = -2f;
            public FloatCurve Preview;        // converted copy for UI
            public int PreviewVersion = -1;
            public float PreviewScale = -1f, PreviewMax = -2f;
        }

        static readonly ConditionalWeakTable<FloatCurve, State> states = new ConditionalWeakTable<FloatCurve, State>();
        static readonly List<float> tBuf = new List<float>(96), vBuf = new List<float>(96), timesBuf = new List<float>(16);

        static State Get(FloatCurve c)
        {
            if (c == null || c.Curve == null || c.Curve.length == 0) return null;
            if (states.TryGetValue(c, out State s))
            {
                if (c.Curve == s.Installed || c.Curve == s.Original) return s;
                states.Remove(c);   // BDA reloaded the curve (new AnimationCurve object): recapture
            }
            Keyframe[] keys = c.Curve.keys;
            s = new State { Original = c.Curve, OriginalEval = new FloatCurve(keys), OrigTimes = new float[keys.Length] };
            s.TMin = float.MaxValue; s.TMax = float.MinValue;
            for (int i = 0; i < keys.Length; i++)
            {
                s.OrigTimes[i] = keys[i].time;
                s.TMin = Mathf.Min(s.TMin, keys[i].time);
                s.TMax = Mathf.Max(s.TMax, keys[i].time);
            }
            states.Add(c, s);
            return s;
        }

        /// <summary>
        /// scale: multiply the curve's range axis (1000 converts a km-authored seeker curve to the metres BDA evaluates).
        /// maxRange: the sensor's real limit (seekers: activeRadarRange); the falloff is anchored there, not at a curve end it never uses.
        /// </summary>
        static AnimationCurve Build(State s, float scale, bool convert, float maxRange, bool lockCurve)
        {
            FloatCurve o = s.OriginalEval;
            timesBuf.Clear();
            foreach (float t in s.OrigTimes) timesBuf.Add(t * scale);
            float tMax = s.TMax * scale;
            if (maxRange > 0f && maxRange > s.TMin * scale) tMax = Mathf.Min(tMax, maxRange);
            bool ok = convert && StealthMath.BuildCurve(t => o.Evaluate(t / scale), s.TMin * scale, tMax, timesBuf,
                                                        Settings.RangeExponent, Settings.ReferenceRcs, tBuf, vBuf, lockCurve && Settings.NoAssuredLock);
            if (!ok)
            {
                if (scale == 1f) return null;   // nothing to change
                tBuf.Clear(); vBuf.Clear();
                foreach (Keyframe k in s.Original.keys) { tBuf.Add(k.time * scale); vBuf.Add(k.value); }
            }
            int n = tBuf.Count;
            var keys = new Keyframe[n];
            for (int i = 0; i < n; i++)
            {
                float inT = i > 0 ? (vBuf[i] - vBuf[i - 1]) / (tBuf[i] - tBuf[i - 1]) : 0f;
                float outT = i < n - 1 ? (vBuf[i + 1] - vBuf[i]) / (tBuf[i + 1] - tBuf[i]) : 0f;
                keys[i] = new Keyframe(tBuf[i], vBuf[i], inT, outT);
            }
            return new AnimationCurve(keys);
        }

        /// <summary>Apply current settings to a live curve (flight only). convert=false restores the original (sonar).</summary>
        public static void Ensure(FloatCurve c, bool convert = true, float scale = 1f, float maxRange = -1f, bool lockCurve = false)
        {
            if (c == null || !HighLogic.LoadedSceneIsFlight) return;
            // Called on every radar scan and seeker update: when nothing changed, stay in managed code (Get touches the native curve).
            if (states.TryGetValue(c, out State cur) && cur.Version == Settings.Version && cur.AppliedScale == scale && cur.AppliedMax == maxRange
                && ReferenceEquals(c.Curve, cur.Installed ?? cur.Original)) return;
            State s = Get(c);
            if (s == null || (s.Version == Settings.Version && s.AppliedScale == scale && s.AppliedMax == maxRange)) return;
            s.Version = Settings.Version; s.AppliedScale = scale; s.AppliedMax = maxRange;
            AnimationCurve conv = Settings.Enabled ? Build(s, scale, convert, maxRange, lockCurve) : null;
            if (conv == null) { c.Curve = s.Original; s.Installed = null; }
            else { c.Curve = conv; s.Installed = conv; }
        }

        /// <summary>Curve to evaluate for display under the current settings (never mutates the source).</summary>
        public static FloatCurve Effective(FloatCurve c, bool convert = true, float scale = 1f, float maxRange = -1f, bool lockCurve = false)
        {
            State s = Get(c);
            if (s == null) return c;
            if (!Settings.Enabled) return s.OriginalEval;
            if (s.PreviewVersion != Settings.Version || s.PreviewScale != scale || s.PreviewMax != maxRange)
            {
                s.PreviewVersion = Settings.Version; s.PreviewScale = scale; s.PreviewMax = maxRange;
                AnimationCurve conv = Build(s, scale, convert, maxRange, lockCurve);
                s.Preview = conv == null ? s.OriginalEval : new FloatCurve(conv.keys);
            }
            return s.Preview;
        }

        public static bool Range(FloatCurve c, out float tMin, out float tMax)
        {
            State s = Get(c);
            tMin = s?.TMin ?? 0f; tMax = s?.TMax ?? 0f;
            return s != null && tMax > tMin;
        }

        /// <summary>The curve exactly as the part config defined it.</summary>
        public static FloatCurve Original(FloatCurve c) => Get(c)?.OriginalEval ?? c;
    }
}
