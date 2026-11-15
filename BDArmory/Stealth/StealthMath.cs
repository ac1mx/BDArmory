using System;
using System.Collections.Generic;

namespace BDAStealth
{
    /// <summary>Pure detection math (no Unity types) so it can be checked outside the game.</summary>
    public static class StealthMath
    {
        public const int CurveSamples = 64;

        /// <summary>
        /// Largest range r in [tMin, tMax] where the original threshold curve is still at or below sRef,
        /// i.e. the range at which a target of RCS sRef is detected under the original curve.
        /// </summary>
        public static float AnchorRange(Func<float, float> orig, float tMin, float tMax, float sRef)
        {
            if (orig(tMax) <= sRef) return tMax;
            const int steps = 512;
            float step = (tMax - tMin) / steps;
            for (int i = steps - 1; i >= 0; i--)
            {
                float r = tMin + i * step;
                if (orig(r) <= sRef)
                {
                    float lo = r, hi = r + step;   // orig(lo) <= sRef < orig(hi)
                    for (int k = 0; k < 24; k++) { float m = 0.5f * (lo + hi); if (orig(m) <= sRef) lo = m; else hi = m; }
                    return lo;
                }
            }
            return tMin;
        }

        /// <summary>
        /// Rebuild a "minimum RCS needed at range r" curve:
        ///   required(r) = min( original(r), sRef * (r / rRef)^n )
        /// rRef is where the original curve detects sRef (or the curve's max range if it never needs that much).
        /// Targets at or above sRef keep their original detection range; smaller targets lose range smoothly
        /// (RCS x0.1 -> range x0.1^(1/n)) instead of falling off a cliff. Returns false if the curve can't be anchored.
        /// </summary>
        public static bool BuildCurve(Func<float, float> orig, float tMin, float tMax, IList<float> origTimes,
                                      float exponent, float refRcs, List<float> outT, List<float> outV, bool noAssuredZone = false)
        {
            outT.Clear(); outV.Clear();
            if (!(tMax > tMin) || exponent <= 0f || refRcs <= 0f) return false;
            float sMax = orig(tMax);
            if (!(sMax > 0f) || float.IsInfinity(sMax)) return false;
            float sRef = Math.Min(refRcs, sMax);
            float rRef = AnchorRange(orig, tMin, tMax, sRef);
            if (!(rRef > 0f)) return false;

            var times = new List<float>(CurveSamples + 1 + (origTimes?.Count ?? 0));
            for (int i = 0; i <= CurveSamples; i++) times.Add(tMin + (tMax - tMin) * i / CurveSamples);
            if (origTimes != null) foreach (float t in origTimes) if (t > tMin && t < tMax) times.Add(t);
            times.Add(rRef);
            times.Sort();
            float eps = (tMax - tMin) * 1e-5f;
            foreach (float t in times)
            {
                if (outT.Count > 0 && t - outT[outT.Count - 1] <= eps) continue;
                float law = t <= 0f ? 0f : sRef * (float)Math.Pow(t / rRef, exponent);
                float o = orig(t);
                outT.Add(t);
                // Lock curves: where the config needs 0 m² (locks anything, ECM included) use the falloff instead.
                outV.Add(noAssuredZone && o <= 0f ? law : Math.Min(o, law));
            }
            // Pin the last key to tMax exactly so min/max range is unchanged.
            outT[outT.Count - 1] = tMax;
            return outT.Count >= 2;
        }

        /// <summary>Piecewise-linear evaluation of sampled keys (matches the linear tangents written in game).</summary>
        public static float EvalLinear(IList<float> t, IList<float> v, float x)
        {
            if (x <= t[0]) return v[0];
            int n = t.Count;
            if (x >= t[n - 1]) return v[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (t[m] <= x) lo = m; else hi = m; }
            float f = (x - t[lo]) / (t[hi] - t[lo]);
            return v[lo] + f * (v[hi] - v[lo]);
        }

        /// <summary>
        /// Self-screening lockbreak: jammer power falls with r^2, the radar echo with r^4, so jam-to-signal grows with r^2.
        ///   J/S(r) = (lockBreak/100) / rcs * (r / burnThrough)^2,   factor = 1 / (1 + J/S)
        /// Never reaches zero, so every jammed target can be locked close enough ("burn-through").
        /// </summary>
        public static float JamFactor(float lockBreak, float rcs, float rangeKm, float burnThroughKm)
        {
            if (lockBreak <= 0f) return 1f;
            if (rcs <= 0f) return 0f;
            float x = rangeKm / Math.Max(burnThroughKm, 0.01f);
            return 1f / (1f + lockBreak / 100f / rcs * x * x);
        }

        /// <summary>
        /// Combine RCS-reduction factors. Stock: product * count (two 0.75 parts give 1.125, i.e. MORE RCS).
        /// Diminishing: strongest first, each further part counts half as much in log space; floored.
        /// </summary>
        public static float CombineReduction(List<float> factors, bool diminishing, float floor)
        {
            if (factors.Count == 0) return 1f;
            if (!diminishing)
            {
                float p = 1f; foreach (float f in factors) p *= f;
                return Math.Max(p * factors.Count, 0f);
            }
            factors.Sort();
            double r = 1.0, w = 1.0;
            foreach (float f in factors) { r *= Math.Pow(Math.Max(f, 0f), w); w *= 0.5; }
            return Math.Max((float)r, floor);
        }

        /// <summary>
        /// Area-weighted radar reflectivity of a craft's skin, each part clamped to the stealth limit.
        /// 1 = uncoated, 0.1 = fully covered in radar absorbent coating.
        /// </summary>
        public static float CoatingFactor(IList<float> areas, IList<float> reflectivity, float limit)
        {
            double a = 0, ar = 0;
            for (int i = 0; i < areas.Count; i++)
            {
                double w = areas[i] > 0f ? areas[i] : 1.0;
                a += w; ar += w * Math.Max(reflectivity[i], limit);
            }
            return a > 0 ? (float)(ar / a) : 1f;
        }

        /// <summary>
        /// Stealth parts on top of coatings: coating x parts never goes below the stealth limit.
        /// </summary>
        public static float LimitedReduction(float partsCombined, float coating, float limit)
        {
            float floor = Math.Min(1f, limit / Math.Max(coating, limit));
            return Math.Max(partsCombined, floor);
        }

        /// <summary>Stock noise-jam radius (m): (J/RCS/100 + 1) * J, which explodes as RCS shrinks.</summary>
        public static float StockJamRadius(float jam, float baseRcs) => baseRcs > 0f ? (jam / baseRcs / 100f + 1f) * jam : jam;

/// <summary>Bounded noise-jam radius (m): the stock formula with RCS counted as at least refRcs, so stealth no longer inflates it.</summary>
        public static float BoundedJamRadius(float jam, float rcs, float refRcs)
        {
            if (jam <= 0f) return 0f;
            return StockJamRadius(jam, Math.Max(rcs, refRcs));
        }

        /// <summary>
        /// Chance that one flare decoys one IR seeker, rolled once when the flare first enters the seeker's view.
        /// ratio = flare heat / target heat as the seeker sees it (after its biases and flare resistance).
        /// p(1) = chanceAtEqualHeat; hotter flares more likely, cooler less, smoothly (logistic in log ratio).
        /// Stock instead decoys deterministically whenever the flare starts hotter than the target.
        /// </summary>
        public static float FlareChance(float ratio, float chanceAtEqualHeat, float slope = 0.25f)
        {
            if (!(ratio > 0f)) return 0f;
            double p1 = Math.Min(Math.Max(chanceAtEqualHeat, 0.001), 0.999);
            double c = slope * Math.Log((1 - p1) / p1);
            return (float)(1.0 / (1.0 + Math.Exp(-(Math.Log(ratio) - c) / slope)));
        }

        /// <summary>
        /// Range (km) inside which a radar sees a noise jammer's skin echo above its noise: jam-to-signal = 1 in JamFactor's law.
        /// Beyond it the radar has a bearing (the jamming strobe) but no range.
        /// </summary>
        public static float NoiseBurnThroughKm(float noise, float rcs, float burnThroughKm)
        {
            if (noise <= 0f) return float.PositiveInfinity;
            if (rcs <= 0f) return 0f;
            return burnThroughKm * (float)Math.Sqrt(rcs * 100.0 / noise);
        }

        /// <summary>
        /// Radar reflectivity of one part's skin from its hull material and armour (armourMm of it).
        /// Stock: any armour replaces the hull's factor, and armour thinner than 10 mm fades toward bare metal (1).
        /// Here thin armour fades toward the hull underneath, and absorbent armour (factor below 1) multiplies with the hull;
        /// 10 mm or more of reflective armour still hides a stealth hull, since that plate is what the radar sees.
        /// With a plain hull (factor 1) the result equals stock.
        /// </summary>
        public static float SkinReflectivity(float hull, float armour, float armourMm)
        {
            if (!(armourMm > 0f)) return hull;
            float t = Math.Min(armourMm, 10f) / 10f;
            float full = armour < 1f ? armour * Math.Min(hull, 1f) : armour;
            return Math.Min(Math.Max(hull + (full - hull) * t, 0f), 2f);
        }

        public const float MaxDecoyChance = 0.9f;

        /// <summary>
        /// Chance that one flare captures one IR seeker: the heat-ratio chance scaled by the seeker's flare sensitivity
        /// (flareEffectivity, never below floor), so a resistant seeker is decoyed less often instead of never.
        /// </summary>
        public static float FlareCapture(float ratio, float chanceAtEqualHeat, float effectivity, float floor)
        {
            float e = Math.Max(effectivity, floor);
            if (!(e > 0f)) return 0f;
            return Math.Min(MaxDecoyChance, FlareChance(Math.Min(ratio, FlareRatioCap), chanceAtEqualHeat) * e);
        }

        /// <summary>
        /// Heat ratio beyond which a hotter flare is no more convincing. Seen from the front a jet shows a fraction of the
        /// heat its flares do, so without this every head-on flare would be a near-certain decoy.
        /// </summary>
        public const float FlareRatioCap = 1.15f;

        /// <summary>Seconds for a seeker's flare fatigue to fall by 1/e.</summary>
        public const float FlareFatigueDecay = 5f;

        /// <summary>
        /// Flare fatigue: every flare a seeker has just rejected halves the chance of the next one, and the effect fades over
        /// a few seconds. A salvo is then worth little more than its first few flares (a stream of 25% flares tops out near
        /// 42%, not 97%), and spacing flares out beats dumping them.
        /// </summary>
        public static float FlareFatigue(float fatigue, float secondsSince) =>
            fatigue <= 0f ? 0f : fatigue * (float)Math.Exp(-Math.Max(secondsSince, 0f) / FlareFatigueDecay);

        public static float FlareFatigueFactor(float fatigue) => (float)Math.Pow(0.5, Math.Max(fatigue, 0f));

        /// <summary>Radar cross-section of one chaff bloom, m^2.</summary>
        public const float ChaffCloudRcs = 5f;

        /// <summary>Scale (m/s) of a pulse-doppler seeker's velocity gate: chaff at rest in the air competes with a target only within it.</summary>
        public const float DopplerGate = 50f;
        /// <summary>Share of a bundle's effect left when the chaff sits outside the velocity gate (range-gate and leading-edge effects).</summary>
        public const float OffGateChaff = 0.03f;

        /// <summary>How far chaff at rest falls inside the velocity gate on a target with this speed along the line of sight (0–1).</summary>
        public static float DopplerOverlap(float radialSpeed) => (float)Math.Exp(-(radialSpeed / DopplerGate) * (radialSpeed / DopplerGate));

        /// <summary>
        /// Chance that one chaff bundle pulls a radar track onto the chaff bloom.
        /// beam: doppler overlap (DopplerOverlap): 0 = target moving along the line of sight (bloom outside the velocity gate), 1 = in the notch.
        /// Small targets hide behind the bloom better: weight cloud / (cloud + target RCS).
        /// </summary>
        public static float ChaffSeduction(float baseChance, float effectivity, float floor, float beam, float targetRcs)
        {
            float e = Math.Max(effectivity, floor);
            if (!(baseChance > 0f) || !(e > 0f)) return 0f;
            float b = OffGateChaff + (1f - OffGateChaff) * Math.Min(Math.Max(beam, 0f), 1f);
            float size = ChaffCloudRcs / (ChaffCloudRcs + Math.Max(targetRcs, 0f));
            return Math.Min(MaxDecoyChance, baseChance * e * b * size);
        }

        #region Smart seekers (MissileBase.smartSeeker): decoys take a track by what is in play, not by a roll
        /// <summary>Seconds for the pull of chaff or flares on a smart seeker's track to fall by 1/e.</summary>
        public const float PullDecay = 1f;

        /// <summary>
        /// Whether a smart seeker following a flare goes back to the craft, which it still has in view: once the craft looks brighter than
        /// the flare by what the seeker rejects (the flare cooling, the craft closing), or once the craft is inside the range at which
        /// the seeker tells its shape and within a few track gates of where the seeker is looking; or, having followed the flare for
        /// FlareMemory seconds, once the flare has drifted out of the track gate around the craft: a flare slows down and falls away,
        /// the craft keeps to the path the seeker remembers.
        /// </summary>
        public static bool FlareReleased(float craftHeat, float flareHeat, float rejection, bool resolved, float craftOffFlareDeg, float trackGate, float heldSeconds = 0f) =>
            craftHeat >= Math.Max(rejection, 1f) * flareHeat || (resolved && craftOffFlareDeg < 4f * trackGate) || (heldSeconds >= FlareMemory && craftOffFlareDeg > trackGate);

        /// <summary>Seconds a smart heat seeker follows a flare before it compares the flare's motion with the path its target was on.</summary>
        public const float FlareMemory = 1.25f;

        /// <summary>Seconds allowed for a flare stream to take a smart heat seeker's track (dwell in its track gate) before the hold has to start.</summary>
        public const float FlareLead = 0.4f;

        /// <summary>
        /// How far chaff at rest falls inside a smart seeker's velocity gate on a craft with this speed along the line of sight:
        /// 1 inside the gate, falling to 0 at one and a half times its width.
        /// </summary>
        public static float GateOverlap(float radialSpeed, float gate)
        {
            if (!(gate > 0f)) return 0f;
            return Math.Min(Math.Max((1.5f * gate - Math.Abs(radialSpeed)) / (0.5f * gate), 0f), 1f);
        }

        /// <summary>
        /// Pull of fresh chaff bundles on a smart seeker's track of a craft, inside the velocity gate only: by the bloom's size over the
        /// craft's radar signature at the aspect in view, eased (power 0.75) so that a small craft hides behind one bundle and a large one
        /// needs several in quick succession rather than more than it can release. The track goes to the bloom once the pull gathered reaches 1.
        /// </summary>
        public static float ChaffPull(int bundles, float effectivity, float floor, float radialSpeed, float gate, float targetRcs)
        {
            float e = Math.Max(effectivity, floor);
            if (bundles <= 0 || !(e > 0f)) return 0f;
            return bundles * e * GateOverlap(radialSpeed, gate) * (float)Math.Pow(ChaffCloudRcs / Math.Max(targetRcs, 0.01f), 0.75);
        }

        /// <summary>Bundles, released together, that bring a track's gathered pull to 1 (0: none can, the craft is outside the velocity gate).</summary>
        public static int ChaffBundlesNeeded(float gathered, float pullPerBundle)
        {
            if (gathered >= 1f) return 1;
            return pullPerBundle > 0f ? (int)Math.Ceiling((1f - gathered) / pullPerBundle) : 0;
        }

        /// <summary>Seconds a smart seeker stays on a chaff bloom that took its track with this much pull (re-cued at once by a data link).</summary>
        public static float ChaffHold(float pull, bool recued) => recued ? 0.6f : 1.5f + Math.Min(Math.Max(pull - 1f, 0f), 2f);

        /// <summary>
        /// Rate (per second) at which a flare inside a smart seeker's track gate pulls the track: how far its brightness, over the tracked
        /// craft's, exceeds what the seeker rejects. The track goes to the flare once the pull gathered reaches 1.
        /// </summary>
        public static float FlarePullRate(float ratio, float rejection, float effectivity, float floor)
        {
            float e = Math.Max(effectivity, floor);
            if (!(ratio > 0f) || !(e > 0f)) return 0f;
            return Math.Min(Math.Max(ratio * e / Math.Max(rejection, 0.05f) - 1f, 0f), 1f) / FlareDwell;
        }

        /// <summary>Seconds a flare at least twice as bright as the seeker rejects has to stay in the track gate to take the track; dimmer ones need longer.</summary>
        public const float FlareDwell = 0.3f;

        /// <summary>Pull left after a time without anything adding to it.</summary>
        public static float PullAfter(float pull, float seconds) => pull <= 0f ? 0f : pull * (float)Math.Exp(-Math.Max(seconds, 0f) / PullDecay);
        #endregion

        /// <summary>Unit direction for an (azimuth, elevation) aspect sample, same convention as BDA's RCS renders.</summary>
        public static void AspectDir(float azDeg, float elDeg, out double x, out double y, out double z)
        {
            double az = azDeg * Math.PI / 180.0, el = elDeg * Math.PI / 180.0;
            x = -Math.Cos(el) * Math.Sin(az); y = Math.Cos(el) * Math.Cos(az); z = -Math.Sin(el);
        }

        /// <summary>
        /// Smooth aspect RCS: Gaussian-weighted average of the rendered samples by angular distance on the sphere
        /// (sigma degrees), so RCS changes continuously as the target turns instead of jumping between samples.
        /// samples: [n,3] = az, el, rcs (az 0..180, mirrored left/right). dirs: precomputed unit vectors (n x 3).
        /// Returns the blend (1-weight)*aspect + weight*overall, like BDA.
        /// </summary>
        static readonly double KernelFloor = Math.Exp(-4.5);   // Gaussian weight at 3 sigma

        public static float SmoothAspect(float[,] samples, double[] dirs, float overall, float weight, float azDeg, float elDeg, float sigmaDeg)
        {
            azDeg = Math.Abs(azDeg);
            if (azDeg > 180f) azDeg = 360f - azDeg;
            if (elDeg > 90f) elDeg = 180f - elDeg; else if (elDeg < -90f) elDeg = -180f - elDeg;
            AspectDir(azDeg, elDeg, out double qx, out double qy, out double qz);
            int n = samples.GetLength(0);
            double cosCut = Math.Cos(3.0 * sigmaDeg * Math.PI / 180.0), inv2s2 = 1.0 / (2.0 * sigmaDeg * sigmaDeg);
            double wSum = 0, vSum = 0, best = -2; float bestV = overall;
            for (int i = 0; i < n; i++)
            {
                double dy = qy * dirs[3 * i + 1] + qz * dirs[3 * i + 2];
                bool onAxis = Math.Abs(dirs[3 * i]) < 1e-6;   // az 0 / 180 samples have no mirror image
                for (int m = 0; m < (onAxis ? 1 : 2); m++)
                {
                    double c = (m == 0 ? qx : -qx) * dirs[3 * i] + dy;   // m=1: the sample's left/right mirror
                    if (c > best) { best = c; bestV = samples[i, 2]; }
                    if (c < cosCut) continue;
                    double ang = Math.Acos(Math.Min(1.0, c)) * 180.0 / Math.PI;
                    // Shifted so the weight reaches zero at the 3-sigma cut: a sample entering or leaving the kernel no longer
                    // makes the result jump (it did, by up to ~10%, where only one or two sparse high-elevation samples are in range).
                    double w = Math.Exp(-ang * ang * inv2s2) - KernelFloor;
                    if (w <= 0.0) continue;
                    wSum += w; vSum += w * samples[i, 2];
                }
            }
            float aspect = wSum > 1e-9 ? (float)(vSum / wSum) : bestV;
            return (1f - weight) * aspect + weight * overall;
        }

        /// <summary>
        /// SmoothAspect is asked once per target per radar scan and walks every rendered sample (107 per craft, most of them
        /// twice for the left/right mirror). The smoothed field varies slowly (kernel sigma 8°), so this caches it on a 0.5° grid (0.5 MB per craft)
        /// over the half-sphere the samples cover and answers by bilinear lookup. Cells are filled the first time they are
        /// touched, so a fresh RCS render costs nothing up front.
        /// </summary>
        public sealed class AspectGrid
        {
            // Between sparse high-elevation samples the kernel moves from one sample's value to the next within a few degrees;
            // at 0.5° the lookup stays within 0.6% of the kernel on real craft (check program).
            const float Step = 0.5f;
            const int NAz = 361, NEl = 361;   // az 0..180, el -90..90
            readonly float[,] samples;
            readonly double[] dirs;
            readonly float sigma;
            readonly float[] cells = new float[NAz * NEl];

            public AspectGrid(float[,] samples, float sigmaDeg)
            {
                this.samples = samples; sigma = sigmaDeg; dirs = AspectDirs(samples);
                for (int i = 0; i < cells.Length; i++) cells[i] = float.NaN;
            }

            float Cell(int ia, int ie)
            {
                int k = ie * NAz + ia;
                float c = cells[k];
                if (float.IsNaN(c)) cells[k] = c = SmoothAspect(samples, dirs, 0f, 0f, ia * Step, ie * Step - 90f, sigma);
                return c;
            }

            /// <summary>Same result as SmoothAspect(samples, dirs, overall, weight, az, el, sigma) to within the grid's interpolation error.</summary>
            public float Eval(float overall, float weight, float azDeg, float elDeg)
            {
                azDeg = Math.Abs(azDeg);
                if (azDeg > 180f) azDeg = 360f - azDeg;
                if (elDeg > 90f) elDeg = 180f - elDeg; else if (elDeg < -90f) elDeg = -180f - elDeg;
                float fa = Math.Min(Math.Max(azDeg, 0f), 180f) / Step, fe = (Math.Min(Math.Max(elDeg, -90f), 90f) + 90f) / Step;
                int ia = Math.Min((int)fa, NAz - 2), ie = Math.Min((int)fe, NEl - 2);
                float ta = fa - ia, te = fe - ie;
                float lo = Cell(ia, ie) + (Cell(ia + 1, ie) - Cell(ia, ie)) * ta;
                float hi = Cell(ia, ie + 1) + (Cell(ia + 1, ie + 1) - Cell(ia, ie + 1)) * ta;
                return (1f - weight) * (lo + (hi - lo) * te) + weight * overall;
            }
        }

        public static double[] AspectDirs(float[,] samples)
        {
            int n = samples.GetLength(0);
            var d = new double[3 * n];
            for (int i = 0; i < n; i++) { AspectDir(samples[i, 0], samples[i, 1], out d[3 * i], out d[3 * i + 1], out d[3 * i + 2]); }
            return d;
        }

        /// <summary>Stock standoff jamming: subtract and clamp (stealth targets go to zero). Soft: S / (S + J).</summary>
        public static float StandoffModifier(float signature, float weightedLockBreak, bool soft)
        {
            if (signature <= 0f) return 1f;
            float j = weightedLockBreak / 100f;
            if (soft) return signature / (signature + j);
            return Math.Max(signature - j, 0f) / signature;
        }

        /// <summary>Largest range in (tMin, tMax) where signalAt(r) >= required(r); 0 if none. Scans outside-in like BDA's editor.</summary>
        public static float MaxRange(Func<float, float> required, float tMin, float tMax, Func<float, float> signalAt)
        {
            const int steps = 400;
            float step = (tMax - tMin) / steps;
            for (int i = steps; i >= 0; i--)
            {
                float r = tMin + i * step;
                if (r <= 0f) break;
                if (signalAt(r) >= required(r))
                {
                    if (i == steps) return tMax;
                    float lo = r, hi = r + step;
                    for (int k = 0; k < 20; k++) { float m = 0.5f * (lo + hi); if (signalAt(m) >= required(m)) lo = m; else hi = m; }
                    return lo;
                }
            }
            return 0f;
        }
    }
}
