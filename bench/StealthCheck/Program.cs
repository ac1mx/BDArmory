using System;
using System.Collections.Generic;
using System.Linq;
using BDAStealth;

// Checks the detection math against real radar curves from this install (linear interpolation of config keys).
static class Program
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static Func<float, float> Lin(params float[] kv)
    {
        var t = new List<float>(); var v = new List<float>();
        for (int i = 0; i < kv.Length; i += 2) { t.Add(kv[i]); v.Add(kv[i + 1]); }
        return x => StealthMath.EvalLinear(t, v, x);
    }

    class Radar { public string Name; public Func<float, float> Det, Lock; public float DMax, LMax; public float[] DT, LT; }

    static Radar Make(string name, float[] det, float[] lck)
    {
        return new Radar { Name = name, Det = Lin(det), Lock = lck == null ? null : Lin(lck), DMax = det[det.Length - 2], LMax = lck == null ? 0 : lck[lck.Length - 2],
            DT = det.Where((_, i) => i % 2 == 0).ToArray(), LT = lck?.Where((_, i) => i % 2 == 0).ToArray() };
    }

    static Func<float, float> Convert(Func<float, float> orig, float max, float[] times, float n, float refRcs)
    {
        var t = new List<float>(); var v = new List<float>();
        if (!StealthMath.BuildCurve(orig, 0f, max, times, n, refRcs, t, v)) return orig;
        return x => StealthMath.EvalLinear(t, v, x);
    }

    static float Range(Func<float, float> req, float max, float sig) => StealthMath.MaxRange(req, 0f, max, _ => sig);
    static float LockRange(Func<float, float> det, float dMax, Func<float, float> lck, float lMax, Func<float, float> sigAt)
        => StealthMath.MaxRange(r => r >= dMax ? float.PositiveInfinity : Math.Max(det(r), lck(r)), 0f, lMax, sigAt);

    /// <summary>Release times of a BDArmory countermeasure routine: count releases interval apart, then wait, repeated for duration seconds.</summary>
    static float[] Program_Times(int count, float interval, float wait, float duration)
    {
        var times = new List<float>();
        for (float start = 0f; start < duration; start += count * interval + wait)
            for (int i = 0; i < count && start + i * interval < duration; i++) times.Add(start + i * interval);
        return times.ToArray();
    }

    static void Main()
    {
        var radars = new[]
        {
            Make("BDA AN/APG-63V2 radome", new float[] { 0, 0, 5, 0, 10, 5, 20, 15, 35, 25 }, new float[] { 0, 0, 5, 5, 10, 7, 20, 20, 35, 35 }),
            Make("BDA AWACS", new float[] { 0, 0, 10, 0, 11, 1, 20, 5, 40, 15, 80, 30 }, null),
            Make("StockArmory KAPG-77", new float[] { 0, 0, 10, 0, 17, 0.001f, 27, 0.01f, 55, 0.1f, 125, 1 }, new float[] { 0, 0, 10, 0, 16, 0.001f, 25, 0.01f, 52, 0.1f, 121, 1 }),
        };
        const float n = 3f, refRcs = 10f, burn = 10f, L = 300f;
        float[] rcsList = { 30, 10, 3, 1, 0.3f, 0.1f, 0.03f };

        foreach (var R in radars)
        {
            var det = Convert(R.Det, R.DMax, R.DT, n, refRcs);
            var lck = R.Lock == null ? null : Convert(R.Lock, R.LMax, R.LT, n, refRcs);
            Console.WriteLine($"\n== {R.Name}  (n={n}, ref={refRcs} m², burn-through={burn} km, ECM lockbreak={L})");
            Console.WriteLine($"{"RCS m²",8} | {"detect stock",12} {"new",7} | {"lock stock",10} {"new",7} | {"lock+ECM stock",14} {"new",7}");
            float prevNew = float.MaxValue;
            foreach (float s in rcsList)
            {
                float dS = Range(R.Det, R.DMax, s), dN = Range(det, R.DMax, s);
                string l = "", le = "";
                if (lck != null)
                {
                    float lS = LockRange(R.Det, R.DMax, R.Lock, R.LMax, _ => s), lN = LockRange(det, R.DMax, lck, R.LMax, _ => s);
                    float lbf = Math.Max(1f - L / s / 100f, 0f);   // stock lockbreak factor (no noise jamming)
                    float leS = lbf <= 0 ? 0 : LockRange(R.Det, R.DMax, R.Lock, R.LMax, _ => s * lbf);
                    float leN = LockRange(det, R.DMax, lck, R.LMax, r => s * StealthMath.JamFactor(L, s, r, burn));
                    l = $"{lS,10:0.0} {lN,7:0.0}"; le = $"{(leS <= 0 ? "never" : leS.ToString("0.0")),14} {leN,7:0.0}";
                    if (s < 3f) Check(leN > 0f, $"{R.Name}: {s} m² + {L} ECM is lockable somewhere (stock: {(leS <= 0 ? "never" : leS.ToString("0.0"))})");
                }
                Console.WriteLine($"{s,8:0.###} | {dS,12:0.0} {dN,7:0.0} | {l} | {le}");
                if (s >= refRcs) Check(Math.Abs(dN - dS) <= R.DMax * 0.01f, $"{R.Name}: {s} m² keeps stock detection range ({dS:0.0} vs {dN:0.0})");
                Check(dN >= dS - R.DMax * 0.005f, $"{R.Name}: {s} m² never detected later than stock");
                Check(dN <= prevNew + 1e-3f, $"{R.Name}: detection range monotone in RCS");
                prevNew = dN;
            }
            // 10x cut below the reference follows 0.1^(1/n) where the law governs.
            float d1 = Range(det, R.DMax, 1f), d01 = Range(det, R.DMax, 0.1f);
            float expect = (float)Math.Pow(0.1, 1.0 / n);
            if (d1 > 0 && d01 > 0 && d1 < R.DMax * 0.99f)
                Check(Math.Abs(d01 / d1 - expect) < 0.03f || d01 / d1 > expect, $"{R.Name}: 1→0.1 m² range ratio {d01 / d1:0.00} (law {expect:0.00}, never steeper)");
        }

        Console.WriteLine();
        Check(StealthMath.JamFactor(300, 1, 0.5f, burn) > 0.9f, "ECM burns through at 0.5 km (factor > 0.9)");
        Check(StealthMath.JamFactor(300, 1, 1000f, burn) > 0f, "ECM factor never reaches 0");
        var two075 = StealthMath.CombineReduction(new List<float> { 0.75f, 0.75f }, true, 0.02f);
        var two075s = StealthMath.CombineReduction(new List<float> { 0.75f, 0.75f }, false, 0f);
        Check(two075 < 0.75f && two075s > 1f, $"two 0.75 parts: new {two075:0.000} (< single), stock {two075s:0.000} (> 1, RCS increase)");
        var deep = StealthMath.CombineReduction(new List<float> { 0.05f, 0.05f, 0.05f }, true, 0.02f);
        Check(Math.Abs(deep - 0.02f) < 1e-6, $"stacking floored at 0.02 ({deep:0.0000})");
        Check(StealthMath.StandoffModifier(1f, 300f, soft: true) > 0f && StealthMath.StandoffModifier(1f, 300f, soft: false) == 0f,
              $"escort jamming 300 vs 1 m²: soft {StealthMath.StandoffModifier(1f, 300f, true):0.00}, stock 0");
        // ---- full stealth stack: 10 m² airframe, fully RAM-coated (0.1), PSG pod (0.05) + stealth cockpit (0.2), 300 lockbreaker ----
        {
            const float S = 0.05f;
            var R = radars[0];
            float coat = StealthMath.CoatingFactor(new[] { 4f, 6f }, new[] { 0.1f, 0.1f }, S);
            float rendered = 10f * coat;
            float partsNew = StealthMath.CombineReduction(new List<float> { 0.05f, 0.2f }, true, 0f);
            float partsStock = StealthMath.CombineReduction(new List<float> { 0.05f, 0.2f }, false, 0f);
            float rcsr = StealthMath.LimitedReduction(partsNew, coat, S);
            float eff = rendered * rcsr;
            Check(Math.Abs(eff - 10f * S) < 1e-4f, $"full stack: effective RCS {eff:0.###} m² = stealth limit ×{S} of 10 m² (coating ×{coat:0.00}, parts ×{partsNew:0.000} → ×{rcsr:0.00})");
            float sReduced = rendered * partsStock, sLbf = Math.Max(1f - L / sReduced / 100f, 0f);
            var det = Convert(R.Det, R.DMax, R.DT, n, refRcs); var lck = Convert(R.Lock, R.LMax, R.LT, n, refRcs);
            float lockNew = LockRange(det, R.DMax, lck, R.LMax, r => eff * StealthMath.JamFactor(L, eff, r, burn));
            float lockStock = sLbf <= 0 ? 0 : LockRange(R.Det, R.DMax, R.Lock, R.LMax, _ => rendered * sLbf);
            float plain = LockRange(det, R.DMax, lck, R.LMax, _ => 10f);
            Console.WriteLine($"   radome vs full stack: stock seen {Range(R.Det, R.DMax, rendered):0.0} km, locked {(lockStock <= 0 ? "never" : lockStock.ToString("0.0"))}; new seen {Range(det, R.DMax, eff):0.0} km, locked {lockNew:0.0} km (plain 10 m²: {plain:0.0} km)");
            Check(lockStock <= 0f && lockNew > 0f && lockNew < plain, "full stack: stock unlockable; new lockable but well inside a plain craft's lock range");
        }

        // ---- noise-jam radius ----
        Check(StealthMath.StockJamRadius(1200f, 0.5f) > 25000f && StealthMath.BoundedJamRadius(1200f, 0.5f, 10f) <= 3000f,
              $"1200 jammer on 0.5 m²: stock radius {StealthMath.StockJamRadius(1200f, 0.5f) / 1000f:0.0} km, bounded {StealthMath.BoundedJamRadius(1200f, 0.5f, 10f) / 1000f:0.00} km");
        Check(Math.Abs(StealthMath.BoundedJamRadius(1200f, 10f, 10f) - StealthMath.StockJamRadius(1200f, 10f)) < 1f,
              $"1200 jammer on 10 m²: bounded {StealthMath.BoundedJamRadius(1200f, 10f, 10f) / 1000f:0.00} km ≈ stock {StealthMath.StockJamRadius(1200f, 10f) / 1000f:0.00} km");

        // ---- seeker unit fix: HQ-9 curve keys in km, activeRadarRange 250000 m, BDA evaluates at range in metres ----
        {
            var hq9 = Lin(0, 0, 10, 0, 15, 0, 20, 0.325f, 40, 1.953f, 80, 3.125f, 160, 5);
            float[] keys = { 0, 10, 15, 20, 40, 80, 160 };
            const float range = 250000f;
            float stock1 = StealthMath.MaxRange(hq9, 0f, range, _ => 1f), stock10 = StealthMath.MaxRange(hq9, 0f, range, _ => 10f);
            Func<float, float> scaled = r => hq9(r / 1000f);
            var t = new List<float>(); var v = new List<float>();
            StealthMath.BuildCurve(scaled, 0f, 160000f, keys.Select(k => k * 1000f).ToList(), n, refRcs, t, v);
            Func<float, float> fixedC = r => StealthMath.EvalLinear(t, v, r);
            float new1 = StealthMath.MaxRange(fixedC, 0f, range, _ => 1f), new10 = StealthMath.MaxRange(fixedC, 0f, range, _ => 10f), new01 = StealthMath.MaxRange(fixedC, 0f, range, _ => 0.1f);
            Console.WriteLine($"   HQ-9 seeker: stock 1 m² {stock1 / 1000f:0.0} km, 10 m² {stock10 / 1000f:0} km; fixed 0.1 m² {new01 / 1000f:0.0} km, 1 m² {new1 / 1000f:0.0} km, 10 m² {new10 / 1000f:0} km");
            Check(stock1 < 1f && new1 > 10000f && new01 > 0f && new01 < new1, "HQ-9 seeker: stock blind to 1 m² beyond 160 m; fixed locks 1 m² at tens of km and stealth sooner");
        }

        // ---- no guaranteed-lock zone: AIM-120D curve (km keys ×1000, seeker 80 km) needs 0 m² inside 15 km ----
        {
            var aim = Lin(0, 0, 10000, 0, 15000, 0, 20000, 0.325f, 40000, 1.953f, 80000, 3.125f, 160000, 5);
            float[] keys = { 0, 10000, 15000, 20000, 40000, 80000 };
            var tz = new List<float>(); var vz = new List<float>(); var tk = new List<float>(); var vk = new List<float>();
            StealthMath.BuildCurve(aim, 0f, 80000f, keys, 2f, refRcs, tk, vk, noAssuredZone: false);
            StealthMath.BuildCurve(aim, 0f, 80000f, keys, 2f, refRcs, tz, vz, noAssuredZone: true);
            Func<float, float> keep = r => StealthMath.EvalLinear(tk, vk, r), fix = r => StealthMath.EvalLinear(tz, vz, r);
            Func<float, float> jammed = r => 0.1f * StealthMath.JamFactor(300f, 0.1f, r / 1000f, burn);
            float a = StealthMath.MaxRange(keep, 0f, 80000f, jammed) / 1000f, b = StealthMath.MaxRange(fix, 0f, 80000f, jammed) / 1000f;
            float c = StealthMath.MaxRange(fix, 0f, 80000f, _ => 10f) / 1000f;
            Console.WriteLine($"   AIM-120D vs 0.1 m² + 300 ECM: with assured zone {a:0.0} km, without {b:0.0} km; 10 m² still {c:0} km");
            Check(a >= 14f && b > 0f && b < a && c >= 79f, "lock curve without the guaranteed zone: ECM and stealth matter inside 15 km, still burns through, big targets unchanged");
        }

        // ---- graded flares ----
        {
            float p07 = StealthMath.FlareChance(0.7f, 0.25f), p1 = StealthMath.FlareChance(1f, 0.25f), p15 = StealthMath.FlareChance(1.5f, 0.25f), p3 = StealthMath.FlareChance(3f, 0.25f);
            Console.WriteLine($"   flare chance @0.25: 0.5× {StealthMath.FlareChance(0.5f, 0.25f):P0}, 0.7× {p07:P0}, 1× {p1:P0}, 1.5× {p15:P0}, 3× {p3:P0}");
            Check(Math.Abs(p1 - 0.25f) < 1e-3f && p07 > 0f && p07 < p1 && p1 < p15 && p15 < p3 && p3 < 1f,
                  "flare chance is graded: cooler flares sometimes work, hotter ones sometimes fail (stock: 0% below 1×, ~100% above)");
        }

        // ---- flare capture: seeker sensitivity scales the chance, floor keeps it above zero ----
        {
            float full = StealthMath.FlareCapture(1f, 0.25f, 1f, 0.15f), half = StealthMath.FlareCapture(1f, 0.25f, 0.5f, 0.15f);
            float immune = StealthMath.FlareCapture(1f, 0.25f, 0f, 0.15f), hot = StealthMath.FlareCapture(50f, 0.25f, 3f, 0.15f);
            Console.WriteLine($"   flare capture @0.25, equal heat: sensitivity 1 {full:P1}, 0.5 {half:P1}, 0 (floored 0.15) {immune:P1}; 50× heat, sensitivity 3 {hot:P0}");
            Check(Math.Abs(full - 0.25f) < 1e-3f && Math.Abs(half - 0.125f) < 1e-3f && Math.Abs(immune - 0.0375f) < 1e-3f,
                  "flare capture scales with seeker sensitivity; a flare-immune seeker is floored, not immune");
            Check(Math.Abs(hot - StealthMath.MaxDecoyChance) < 1e-6f && StealthMath.FlareCapture(1f, 0.25f, 0f, 0f) == 0f,
                  "flare capture is capped below certainty and is 0 with the floor off");
            float front = StealthMath.FlareCapture(8f, 0.25f, 1f, 0.15f), atCap = StealthMath.FlareChance(StealthMath.FlareRatioCap, 0.25f);
            Check(Math.Abs(front - atCap) < 1e-6f && front < 0.5f, $"a flare 8× hotter than a head-on jet decoys {front:P0}, same as {StealthMath.FlareRatioCap}× (uncapped {StealthMath.FlareChance(8f, 0.25f):P0})");
        }

        // ---- chaff seduction ----
        {
            float beam1 = StealthMath.ChaffSeduction(0.3f, 1f, 0.15f, 1f, 1f), head1 = StealthMath.ChaffSeduction(0.3f, 1f, 0.15f, 0f, 1f);
            float beam10 = StealthMath.ChaffSeduction(0.3f, 1f, 0.15f, 1f, 10f), beam01 = StealthMath.ChaffSeduction(0.3f, 1f, 0.15f, 1f, 0.1f);
            float immune = StealthMath.ChaffSeduction(0.3f, 0f, 0.15f, 1f, 1f);
            float crank1 = StealthMath.ChaffSeduction(0.3f, 1f, 0.15f, StealthMath.DopplerOverlap(300f * MathF.Sin(MathF.PI / 4f)), 1f); // 300 m/s, 45° off the line of sight
            float notch1 = StealthMath.ChaffSeduction(0.3f, 1f, 0.15f, StealthMath.DopplerOverlap(300f * MathF.Sin(5f * MathF.PI / 180f)), 1f); // within 5° of the beam
            Console.WriteLine($"   chaff seduction @0.3 per bundle: 1 m² beaming {beam1:P1}, 5° off the beam {notch1:P1}, cranking 45° {crank1:P1}, head-on {head1:P1}; 10 m² beaming {beam10:P1}; 0.1 m² beaming {beam01:P1}; chaff-immune missile {immune:P1}");
            Check(Math.Abs(beam1 - 0.25f) < 1e-3f && head1 < 0.01f && crank1 < 0.01f && notch1 > 0.15f, "chaff works in the notch (1 m²: 25% beaming, >15% within 5°) and hardly at all outside the velocity gate (<1% cranking or head-on)");
            Check(beam01 > beam1 && beam1 > beam10 && beam10 > 0f && beam01 < 0.3f + 1e-6f, "chaff hides small targets better than large ones, never above the base chance");
            Check(immune > 0f && immune < beam1 && StealthMath.ChaffSeduction(0.3f, 0f, 0f, 1f, 1f) == 0f, "chaff-immune missiles are floored, not immune; 0 with the floor off");
        }

        // ---- smart seekers: the same release in the same geometry always ends the same way ----
        {
            const float gate = 50f, floor = 0.15f;
            float beam2 = StealthMath.ChaffPull(1, 1f, floor, 0f, gate, 2f), beam30 = StealthMath.ChaffPull(1, 1f, floor, 0f, gate, 30f);
            float crank = StealthMath.ChaffPull(4, 1f, floor, 300f * MathF.Sin(MathF.PI / 4f), gate, 2f), edge = StealthMath.ChaffPull(1, 1f, floor, 62.5f, gate, 2f);
            Console.WriteLine($"   smart chaff pull per bundle: 2 m² in the gate {beam2:F2}, 30 m² in the gate {beam30:F2}, 2 m² at 62 m/s along the line {edge:F2}; four bundles cranking 45° {crank:F2}");
            Check(beam2 >= 1f && 3 * beam30 < 1f && 4 * beam30 >= 1f && StealthMath.ChaffBundlesNeeded(0f, beam30) == 4 && StealthMath.ChaffBundlesNeeded(0f, crank) == 0,
                  "one bundle takes the track of a 2 m² craft in the gate; a 30 m² craft needs four together; none does outside the gate");
            Check(StealthMath.FlareReleased(2f, 1f, 1.5f, false, 30f, 2f) && !StealthMath.FlareReleased(1.2f, 1f, 1.5f, false, 1f, 2f) && StealthMath.FlareReleased(1.2f, 1f, 1.5f, true, 7f, 2f) && !StealthMath.FlareReleased(1.2f, 1f, 1.5f, true, 9f, 2f),
                  "a seeker on a flare goes back to a craft that outshines it, or a close one within four track gates of the flare");
            Check(crank == 0f && edge > 0f && edge < beam2, "chaff outside the velocity gate has no pull at all, and fades across the gate's edge");
            Check(StealthMath.ChaffHold(1f, false) == 1.5f && StealthMath.ChaffHold(5f, false) == 3.5f && StealthMath.ChaffHold(5f, true) < 1f, "a bloom holds the track 1.5-3.5 s by its margin, under a second with a data link re-cue");
            float dim = StealthMath.FlarePullRate(0.9f, 1.5f, 1f, floor), bright = StealthMath.FlarePullRate(3f, 1.5f, 1f, floor), old = StealthMath.FlarePullRate(0.9f, 0.7f, 1f, floor);
            Console.WriteLine($"   smart flare pull per second: 0.9× vs rejection 1.5 {dim:F2}, 3× vs 1.5 {bright:F2}, 0.9× vs 0.7 {old:F2}");
            Check(dim == 0f && bright > 0f && old > 0f && old < bright && StealthMath.FlarePullRate(50f, 1.5f, 1f, floor) == bright,
                  "a flare dimmer than the seeker rejects never pulls it; brighter ones pull faster, up to twice the rejection");
            Check(Math.Abs(1f / bright - StealthMath.FlareDwell) < 1e-4f, "a flare twice what the seeker rejects takes the track after FlareDwell in the gate");
            Check(StealthMath.PullAfter(1f, StealthMath.PullDecay) < 0.37f && StealthMath.PullAfter(1f, 0f) == 1f, "gathered pull fades between releases");
        }

        // ---- skin reflectivity: hull material under armour ----
        {
            const float foam = 0.01f, rac = 0.1f, steel = 1f, glass = 1.2f;
            Check(Math.Abs(StealthMath.SkinReflectivity(1f, rac, 10f) - rac) < 1e-6f && Math.Abs(StealthMath.SkinReflectivity(1f, rac, 5f) - 0.55f) < 1e-6f && Math.Abs(StealthMath.SkinReflectivity(1f, glass, 10f) - glass) < 1e-6f,
                  "plain hull: skin reflectivity equals stock (coating 0.1 at 10 mm, 0.55 at 5 mm, S-glass 1.2)");
            Check(StealthMath.SkinReflectivity(foam, steel, 0f) == foam && StealthMath.SkinReflectivity(foam, steel, 10f) == steel,
                  "foam hull: bare 0.01; under 10 mm of steel the plate is what the radar sees (1.0)");
            float thin = StealthMath.SkinReflectivity(foam, steel, 1f);
            Check(Math.Abs(thin - 0.109f) < 1e-3f, $"foam hull under 1 mm of steel: {thin:0.000} (stock: 1.0)");
            float both = StealthMath.SkinReflectivity(foam, rac, 10f), comp = StealthMath.SkinReflectivity(0.8f, rac, 10f);
            Check(Math.Abs(both - 0.001f) < 1e-6f && Math.Abs(comp - 0.08f) < 1e-6f, $"absorbent coating stacks with the hull: foam {both:0.000}, composites {comp:0.00} (stock: 0.1 for both)");
        }

        // ---- noise burn-through (radar display strobes, missile datalink) ----
        {
            float yf23 = StealthMath.NoiseBurnThroughKm(1152f, 0.162f, 10f), pod = StealthMath.NoiseBurnThroughKm(700f, 10f, 10f);
            Console.WriteLine($"   noise burn-through: YF-23 ECM on (0.162 m², noise 1152) {yf23:0.00} km; 10 m² craft with a 700 pod {pod:0.0} km");
            Check(Math.Abs(yf23 - 1.186f) < 0.01f && Math.Abs(pod - 11.95f) < 0.05f && float.IsPositiveInfinity(StealthMath.NoiseBurnThroughKm(0f, 1f, 10f)),
                  "range is denied beyond burn-through; no noise jammer, no denial");
        }

        // ---- whole engagements: BDArmory's default AI countermeasure programs against one missile (model, not a game run) ----
        // AI defaults (MissileFire): countermeasures start 5 s before impact; flares 3 × 0.2 s then 0.7 s wait; chaff 2 × 0.5 s then 0.6 s wait.
        {
            var rng = new Random(12345);
            const int runs = 200000;
            const float flareFactor = 1.2f;   // FLARE_FACTOR in this install's BDArmory settings.cfg
            float[] flareTimes = Program_Times(3, 0.2f, 0.7f, 5f), chaffTimes = Program_Times(2, 0.5f, 0.6f, 5f);

            // Flare heat relative to the jet's full heat, as CMFlare.SetThermal draws it; seen = share of that heat the seeker sees on the jet.
            float FlareDefeat(float chance, float sensitivity, float seen, float[] times)
            {
                int defeated = 0;
                for (int n = 0; n < runs; n++)
                {
                    float fatigue = 0f, last = 0f;
                    foreach (float t in times)
                    {
                        float lo = 0.65f + 0.15f * (float)rng.NextDouble();
                        float heat = lo + (flareFactor - lo + 0.8f - lo) * (float)rng.NextDouble();
                        float f = StealthMath.FlareFatigue(fatigue, t - last);
                        float p = StealthMath.FlareCapture(heat / seen, chance, sensitivity, 0.15f) * StealthMath.FlareFatigueFactor(f);
                        if (rng.NextDouble() < p) { defeated++; break; }   // narrow seeker: following a flare for its 5 s burn is a miss
                        fatigue = f + 1f; last = t;
                    }
                }
                return defeated / (float)runs;
            }

            // A seduction that is still running in the last 0.75 s leaves no time to steer back.
            float ChaffDefeat(float chance, float sensitivity, float beam, float rcs)
            {
                float p = StealthMath.ChaffSeduction(chance, sensitivity, 0.15f, beam, rcs);
                int defeated = 0;
                for (int n = 0; n < runs; n++)
                {
                    float until = -1f;
                    foreach (float t in chaffTimes) if (rng.NextDouble() < p) until = t + 1.5f + 2f * (float)rng.NextDouble();
                    if (until > 5f - 0.75f) defeated++;
                }
                return defeated / (float)runs;
            }

            float tail = FlareDefeat(0.25f, 1f, 1f, flareTimes), head = FlareDefeat(0.25f, 1f, 0.3f, flareTimes), irccm = FlareDefeat(0.25f, 0.5f, 1f, flareTimes);
            float one = FlareDefeat(0.25f, 1f, 1f, new[] { 0f }), spaced = FlareDefeat(0.25f, 1f, 1f, new[] { 0f, 2.5f, 5f });
            float immune = FlareDefeat(0.25f, 0f, 1f, flareTimes);
            Console.WriteLine($"   IR missile vs AI flares ({flareTimes.Length} flares, chance 25%): tail {tail:P0}, head-on {head:P0}, sensitivity 0.5 tail {irccm:P0}, immune (floored) tail {immune:P0}; one flare {one:P0}, three spaced 2.5 s {spaced:P0}");
            Check(tail > 0.3f && tail < 0.5f, "AI flare program defeats 30–50% of tail-aspect IR missiles (no fatigue: ~97%)");
            Check(head > tail && head < 0.7f, "head-on flares work better than tail-on but stay below 70%");
            Check(irccm > 0.12f && irccm < tail * 0.7f && immune > 0.02f && immune < irccm, "flare-resistant seekers are decoyed less, never zero");
            Check(spaced > 3f * 0.6f * one && spaced < tail * 1.25f, "three well-spaced flares are worth nearly three separate tries");

            float beam1 = ChaffDefeat(0.2f, 1f, 1f, 1f), head1 = ChaffDefeat(0.2f, 1f, 0f, 1f), beam10 = ChaffDefeat(0.2f, 1f, 1f, 10f), beam01 = ChaffDefeat(0.2f, 1f, 1f, 0.1f);
            Console.WriteLine($"   radar missile vs AI chaff ({chaffTimes.Length} bundles, chance 20%):1 m² beaming {beam1:P0}, head-on {head1:P0}; 10 m² beaming {beam10:P0}; 0.1 m² beaming {beam01:P0}");
            Check(beam1 > 0.35f && beam1 < 0.65f && head1 < 0.03f, "chaff in the notch defeats about half the radar missiles; outside the velocity gate (head-on) under 3%");
            Check(beam10 < beam1 * 0.7f && beam01 > beam1, "chaff protects a 10 m² craft much less and a stealth craft more");
        }

        // ---- aspect interpolation on real rendered data (path given as first argument) ----
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && System.IO.File.Exists(args[1]))
            foreach (string craft in new[] { "F-22A L", "YF-23" })
            {
                float[,] m = Aspect.Parse(args[1], craft);
                float overall = Aspect.Overall(m);
                double[] dirs = StealthMath.AspectDirs(m);
                Console.WriteLine($"\n== {craft}: overall {overall:0.###} m² (75th pct), {m.GetLength(0)} samples");
                foreach (float el in new[] { 0f, 2f })
                {
                    var st = Aspect.Sweep(az => Aspect.Stock(m, overall, 0f, az, el));
                    Console.WriteLine($"   level turn el={el}: stock interp max step {st.maxStep * 100:0}% per 0.5°, {st.bigSteps} jumps >25%");
                    foreach (float sigma in new[] { 6f, 8f, 10f })
                    {
                        var sm = Aspect.Sweep(az => StealthMath.SmoothAspect(m, dirs, overall, 0f, az, el, sigma));
                        Console.WriteLine($"                     smooth σ={sigma}°: max step {sm.maxStep * 100:0}% per 0.5°, {sm.bigSteps} jumps >25%");
                    }
                }
                foreach (float w in new[] { 0.45f, 0.25f, 0.2f })
                {
                    string row(float az, float el) => $"{Aspect.Stock(m, overall, w, az, el):0.##}/{StealthMath.SmoothAspect(m, dirs, overall, w, az, el, 8f):0.##}";
                    Console.WriteLine($"   weight {w}: nose {row(0, 0)}  nose+30 {row(30, 0)}  beam {row(90, 0)}  tail {row(180, 0)}  (stock/smooth m²)");
                }
                // ---- aspect grid: accuracy against the direct kernel, and cost per evaluation ----
                {
                    var grid = new StealthMath.AspectGrid(m, 8f);
                    var r = new Random(7);
                    const int evals = 200000;
                    var az = new float[evals]; var el = new float[evals];
                    for (int i = 0; i < evals; i++) { az[i] = (float)(r.NextDouble() * 360 - 180); el[i] = (float)(r.NextDouble() * 120 - 60); }
                    double worst = 0, sumErr = 0;
                    for (int i = 0; i < evals; i++)
                    {
                        float direct = StealthMath.SmoothAspect(m, dirs, overall, 0.2f, az[i], el[i], 8f), fast = grid.Eval(overall, 0.2f, az[i], el[i]);
                        double err = Math.Abs(fast - direct) / direct;
                        worst = Math.Max(worst, err); sumErr += err;
                    }
                    float sink = 0f;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int rep = 0; rep < 5; rep++) for (int i = 0; i < evals; i++) sink += StealthMath.SmoothAspect(m, dirs, overall, 0.2f, az[i], el[i], 8f);
                    double nsDirect = sw.Elapsed.TotalMilliseconds * 1e6 / (5.0 * evals);
                    sw.Restart();
                    for (int rep = 0; rep < 5; rep++) for (int i = 0; i < evals; i++) sink += Aspect.Stock(m, overall, 0.2f, Math.Abs(az[i]), el[i]);
                    double nsStock = sw.Elapsed.TotalMilliseconds * 1e6 / (5.0 * evals);
                    sw.Restart();
                    for (int rep = 0; rep < 5; rep++) for (int i = 0; i < evals; i++) sink += grid.Eval(overall, 0.2f, az[i], el[i]);
                    double nsGrid = sw.Elapsed.TotalMilliseconds * 1e6 / (5.0 * evals);
                    var fresh = new StealthMath.AspectGrid(m, 8f);
                    sw.Restart();
                    for (int i = 0; i < evals; i++) sink += fresh.Eval(overall, 0.2f, az[i], el[i]);
                    double nsCold = sw.Elapsed.TotalMilliseconds * 1e6 / evals;
                    Console.WriteLine($"   aspect RCS per evaluation: stock {nsStock:0} ns, kernel {nsDirect:0} ns, grid {nsGrid:0} ns (first {evals} calls on a fresh grid incl. filling it: {nsCold:0} ns); grid error mean {sumErr / evals:P2}, worst {worst:P1}  [{sink:0}]");
                    Check(worst < 0.03 && sumErr / evals < 0.003, $"{craft}: aspect grid matches the direct kernel (worst {worst:P1}, mean {sumErr / evals:P2})");
                    Check(nsGrid < nsDirect / 5.0, $"{craft}: aspect grid is at least 5× cheaper than the direct kernel ({nsDirect:0} → {nsGrid:0} ns)");
                }
                var smooth8 = Aspect.Sweep(az => StealthMath.SmoothAspect(m, dirs, overall, 0f, az, 0f, 8f));
                var stock0 = Aspect.Sweep(az => Aspect.Stock(m, overall, 0f, az, 0f));
                Check(smooth8.maxStep < stock0.maxStep && smooth8.bigSteps < stock0.bigSteps, $"{craft}: smooth aspect has smaller jumps than stock in a level turn");
            }

        if (Array.IndexOf(args, "craft") >= 0) CraftSim.Run();

        Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
