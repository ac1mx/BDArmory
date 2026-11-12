using System;
using System.Collections.Generic;
using System.Numerics;
using BDAStealth;

/// <summary>
/// Engagement model for specific craft: sensor ranges from the part configs' curves, and a 2-D fly-out of one missile against
/// one jet running BDArmory's AI countermeasure program, using the plugin's own decoy rules (StealthMath).
/// Inputs are measured (in-game RCS reports, part configs, craft files); the fly-out itself is a model, not a game run.
/// </summary>
static class CraftSim
{
    // ---- rules (Balanced preset) ----
    const float N = 2f, RefRcs = 10f, BurnKm = 10f, FlareChance = 0.25f, ChaffChance = 0.2f, CmFloor = 0.15f;
    const float FlareFactor = 1.2f;                 // FLARE_FACTOR, BDArmory settings.cfg

    // ---- craft (rcs_report_*.txt, Balanced rules; craft files for pods and AI countermeasure timings) ----
    class Craft
    {
        public string Name;
        public float Overall, Nose, Beam, Tail;     // m², as radars see it
        public float EcmReduction = 1f, Noise, LockBreak;
        public int FlarePods = 2, ChaffPods = 2;
        public bool HasEcm => Noise > 0f || LockBreak > 0f;
    }

    static readonly Craft[] crafts =
    {
        new Craft { Name = "F-22A L", Overall = 2.6f, Nose = 0.784f, Beam = 3.5f, Tail = 1.1f },
        new Craft { Name = "YF-23", Overall = 0.573f, Nose = 0.13f, Beam = 0.589f, Tail = 0.929f, EcmReduction = 0.284f, Noise = 1152f, LockBreak = 1040f },
        new Craft { Name = "Su-47", Overall = 32f, Nose = 7.5f, Beam = 40f, Tail = 8.7f },
    };

    // ---- sensors (KerbalField part configs; curve keys in km) ----
    static readonly float[] aesaDet = { 0, 0, 10, 0, 15, 0, 20, 0.225f, 40, 1.353f, 80, 2.525f, 160, 5 };
    static readonly float[] aesaLock = { 0, 0, 10, 0.075f, 15, 0.1725f, 20, 0.3375f, 40, 1.95f, 80, 3.75f, 160, 7.5f };
    static readonly float[] amraam = { 0, 0, 10, 0, 15, 0, 20, 0.325f, 40, 1.953f, 80, 3.125f, 160, 5 };
    const float AmraamSeekerKm = 80f;

    static Func<float, float> Curve(float[] kv, float max, bool lockCurve, out float tMax)
    {
        var t = new List<float>(); var v = new List<float>(); var times = new List<float>();
        for (int i = 0; i < kv.Length; i += 2) { t.Add(kv[i]); v.Add(kv[i + 1]); times.Add(kv[i]); }
        Func<float, float> orig = x => StealthMath.EvalLinear(t, v, x);
        tMax = Math.Min(t[t.Count - 1], max);
        var ot = new List<float>(); var ov = new List<float>();
        if (!StealthMath.BuildCurve(orig, 0f, tMax, times, N, RefRcs, ot, ov, lockCurve)) return orig;
        return x => StealthMath.EvalLinear(ot, ov, x);
    }

    static string Km(float km) => km <= 0f ? "never" : km >= 100f ? $"{km:0} km" : $"{km:0.0} km";

    static void Ranges()
    {
        var det = Curve(aesaDet, float.MaxValue, false, out float dMax);
        var lck = Curve(aesaLock, float.MaxValue, true, out float lMax);
        var skr = Curve(amraam, AmraamSeekerKm, true, out float sMax);
        Func<float, float> radarReq = r => r >= dMax ? float.PositiveInfinity : Math.Max(det(r), lck(r));

        Console.WriteLine("\n=== SENSOR RANGES (AESA radar, AIM-120D seeker) ===");
        Console.WriteLine($"{"target",-18}{"aspect",-7}{"AESA sees",11}{"AESA locks",12}{"AIM-120D seeker",17}");
        foreach (Craft c in crafts)
            foreach (bool ecm in c.HasEcm ? new[] { false, true } : new[] { false })
                foreach (var (label, rcs) in new[] { ("nose", c.Nose), ("beam", c.Beam), ("tail", c.Tail) })
                {
                    float reduced = rcs * (ecm ? c.EcmReduction : 1f);
                    float noise = ecm ? c.Noise / 100f : 0f, lb = ecm ? c.LockBreak : 0f;
                    float seen = StealthMath.MaxRange(det, 0f, dMax, _ => reduced + noise);
                    float locked = StealthMath.MaxRange(radarReq, 0f, lMax, r => reduced * StealthMath.JamFactor(lb, reduced, r, BurnKm));
                    float seeker = StealthMath.MaxRange(skr, 0f, sMax, r => reduced * StealthMath.JamFactor(lb, reduced, r, BurnKm) + noise);   // + home-on-jam
                    Console.WriteLine($"{c.Name + (ecm ? " ECM on" : ""),-18}{label,-7}{Km(seen),11}{Km(locked),12}{Km(seeker),17}");
                }
    }

    // ---- fly-out ----

    enum Aspect { Tail, Beam, HeadOn }

    static readonly Random rng = new Random(2026);
    static float U(float a, float b) => a + (b - a) * (float)rng.NextDouble();
    static Vector2 Rot(Vector2 v, float rad) { float c = MathF.Cos(rad), s = MathF.Sin(rad); return new Vector2(c * v.X - s * v.Y, s * v.X + c * v.Y); }
    static float AngleDeg(Vector2 a, Vector2 b) => MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(b)), -1f, 1f)) * 180f / MathF.PI;

    static Vector2 Steer(Vector2 heading, Vector2 want, float maxRad)
    {
        float cross = heading.X * want.Y - heading.Y * want.X, dot = Vector2.Dot(heading, want);
        float ang = MathF.Atan2(cross, dot);
        return Vector2.Normalize(Rot(heading, Math.Clamp(ang, -maxRad, maxRad)));
    }

    /// <summary>Skin RCS the missile sees (ChaffPatches.SkinRcs). cosNose: 1 = looking at the nose, 0 = beam, -1 = tail.</summary>
    static float AspectRcs(Craft c, bool ecm, float cosNose)
    {
        float c2 = cosNose * cosNose;
        return (c.Beam * (1f - c2) + (cosNose > 0f ? c.Nose : c.Tail) * c2) * (ecm ? c.EcmReduction : 1f);
    }

    const float Dt = 0.02f, JetSpeed = 250f, HitRadius = 20f, CmThreshold = 5f, AirDensity = 0.7f;

    class Flare { public Vector2 Pos, Vel; public float Thermal, Min, Lit; public bool Rolled; }

    /// <summary>One missile against one jet. ir: AIM-9X (heat) else AIM-120D (active radar). Returns true if the missile got within HitRadius.</summary>
    static bool FlyOut(bool ir, Aspect aspect, float turnG, Craft craft, bool ecm, bool countermeasures)
    {
        float vm = ir ? 780f : 1250f;                                   // optimumAirspeed
        float turn = (ir ? 85f : 65f) * MathF.PI / 180f * Dt;           // maxTurnRateDPS
        Vector2 jh = aspect == Aspect.Tail ? Vector2.UnitX : aspect == Aspect.HeadOn ? -Vector2.UnitX : Vector2.UnitY;
        float closing = aspect == Aspect.Tail ? vm - JetSpeed : aspect == Aspect.HeadOn ? vm + JetSpeed : vm;
        Vector2 m = Vector2.Zero, mh = Vector2.UnitX, j = new Vector2(closing * 7f, 0f);
        float jetTurn = turnG * 9.81f / JetSpeed * Dt * (rng.Next(2) == 0 ? 1f : -1f);

        // heat seeker state
        const float Heat = 1500f, HeatThreshold = 45f, ScanRadius = 40f;   // lockedSensorFOV 80 / 2
        var flares = new List<Flare>();
        Flare held = null; bool lost = false; Vector2 pred = j, predVel = jh * JetSpeed;
        float prior = 0f, fatigue = 0f, fatigueTime = 0f;
        // radar seeker state
        float seducedUntil = -1f; Vector2 bloom = Vector2.Zero;
        // AI countermeasure program
        int count = ir ? 3 : 2; float interval = ir ? 0.2f : 0.5f, wait = ir ? 0.7f : 0.6f;
        float nextRelease = -1f; int inBurst = 0;

        float min = float.MaxValue;
        bool armed = false;
        for (float t = 0f; t < 30f; t += Dt)
        {
            Vector2 jv = jh * JetSpeed, los = j - m;
            float dist = los.Length();
            float rate = Vector2.Dot(mh * vm - jv, los / dist);
            // countermeasures start when the threat's closing time drops under cmThreshold
            if (countermeasures && nextRelease < 0f && rate > 1f && dist / rate <= CmThreshold) nextRelease = t;
            if (countermeasures && nextRelease >= 0f && t >= nextRelease)
            {
                inBurst++;
                nextRelease = t + (inBurst < count ? interval : interval + wait);
                if (inBurst >= count) inBurst = 0;
                if (ir)
                    for (int p = 0; p < craft.FlarePods; p++)
                    {
                        float lo = U(0.65f, 0.8f), heat = Heat * U(lo, FlareFactor - lo + 0.8f);
                        flares.Add(new Flare { Pos = j, Vel = jv + Rot(jh, MathF.PI / 2f) * U(-30f, 30f), Thermal = heat, Min = heat * 0.34f, Lit = t });
                    }
                else
                {
                    float beam = 1f - Math.Clamp(MathF.Abs(Vector2.Dot(jv, los / dist)) / JetSpeed, 0f, 1f);
                    float chance = StealthMath.ChaffSeduction(ChaffChance, 1f, CmFloor, beam, AspectRcs(craft, ecm, Vector2.Dot(jh, -los / dist)));
                    for (int p = 0; p < craft.ChaffPods; p++)
                        if (rng.NextDouble() < chance) { seducedUntil = t + U(1.5f, 3.5f); bloom = j; break; }
                }
            }

            Vector2 aim, aimVel;
            if (ir)
            {
                // seen heat: full from behind, about a third head-on (engine occluded by the airframe)
                float cosTail = Vector2.Dot(jh, los / dist);
                float jetSeen = Heat * (0.35f + 0.65f * (1f + cosTail) / 2f) * 1.96e6f / Math.Max(dist * dist, 9e4f);
                Vector2 ray = held != null ? held.Pos - m : lost ? pred - m : los;
                if (held == null && !lost) prior = jetSeen;
                Flare pick = null; float pickHeat = 0f;
                foreach (Flare f in flares)
                {
                    if (t - f.Lit > 5f) continue;
                    Vector2 to = f.Pos - m;
                    if (!(AngleDeg(to, ray) < ScanRadius)) continue;
                    float seen = f.Thermal * 1.96e6f / Math.Clamp(to.LengthSquared(), 9e4f, 3.6e7f);
                    bool captured = f == held;
                    if (!captured && !f.Rolled && prior > 0f)
                    {
                        f.Rolled = true;
                        float fat = StealthMath.FlareFatigue(fatigue, t - fatigueTime);
                        captured = rng.NextDouble() < StealthMath.FlareCapture(seen / prior, FlareChance, 1f, CmFloor) * StealthMath.FlareFatigueFactor(fat);
                        if (!captured) { fatigue = fat + 1f; fatigueTime = t; }
                    }
                    if (captured && seen > pickHeat) { pick = f; pickHeat = seen; }
                }
                if (pick != null && pickHeat > HeatThreshold) { held = pick; lost = false; prior = pickHeat; }
                else
                {
                    if (held != null) { lost = true; pred = held.Pos; predVel = held.Vel; held = null; }   // flare burnt out or left the view
                    if (lost)
                    {
                        pred += predVel * Dt;
                        if (AngleDeg(los, pred - m) < ScanRadius && jetSeen > HeatThreshold) lost = false;   // jet back in view: reacquired
                    }
                }
                aim = held != null ? held.Pos : lost ? pred : j;
                aimVel = held != null ? held.Vel : lost ? predVel : jv;
                foreach (Flare f in flares)
                {
                    float sp = f.Vel.Length();
                    if (sp > 0.01f) f.Vel -= f.Vel / sp * MathF.Min(sp, 0.004f * AirDensity * sp * sp * Dt);
                    f.Pos += f.Vel * Dt;
                    f.Thermal = MathF.Max(f.Min, f.Thermal - (f.Thermal - f.Min) / 5f * Dt);
                }
            }
            else
            {
                bool seduced = t < seducedUntil;
                aim = seduced ? bloom : j;
                aimVel = seduced ? Vector2.Zero : jv;
            }

            // lead pursuit with a turn-rate limit
            float tgo = (aim - m).Length() / vm;
            Vector2 want = aim + aimVel * tgo - m;
            if (want.LengthSquared() > 1e-6f) mh = Steer(mh, Vector2.Normalize(want), turn);

            // closest approach to the jet within this step
            Vector2 rel = j - m, relVel = jv - mh * vm;
            float ts = Math.Clamp(-Vector2.Dot(rel, relVel) / Math.Max(relVel.LengthSquared(), 1e-6f), 0f, Dt);
            min = MathF.Min(min, (rel + relVel * ts).Length());
            if (min < HitRadius) return true;

            // MissileLauncher.CheckMiss: armed within 400 m of the aim point; once that point is behind the nose the missile
            // has missed (guidance off, self-destruct), whether it was chasing the jet, a flare or a chaff bloom.
            if ((aim - m).LengthSquared() < 160000f) armed = true;
            if (armed && Vector2.Dot(aim - m, mh) < 0f) return false;

            m += mh * vm * Dt;
            jh = Vector2.Normalize(Rot(jh, jetTurn));
            j += jh * JetSpeed * Dt;
            if (t > 12f && dist > 6000f && rate < 0f) break;   // flown past and opening
        }
        return false;
    }

    static float Pk(bool ir, Aspect a, float g, Craft c, bool ecm, bool cm, int runs = 20000)
    {
        int hits = 0;
        for (int i = 0; i < runs; i++) if (FlyOut(ir, a, g, c, ecm, cm)) hits++;
        return hits / (float)runs;
    }

    public static void Run()
    {
        Ranges();

        Console.WriteLine("\n=== AIM-9X vs a jet with 2 flare pods, AI flare program (hit chance) ===");
        Console.WriteLine($"{"aspect",-9}{"jet",-13}{"no flares",11}{"flares",9}");
        foreach (Aspect a in new[] { Aspect.Tail, Aspect.Beam, Aspect.HeadOn })
            foreach (float g in new[] { 0f, 6f })
                Console.WriteLine($"{a,-9}{(g > 0f ? $"{g:0} g turn" : "straight"),-13}{Pk(true, a, g, crafts[0], false, false):P0}{"",6}{Pk(true, a, g, crafts[0], false, true),6:P0}");

        Console.WriteLine("\n=== AIM-120D (active) vs a jet with 2 chaff pods, AI chaff program (hit chance) ===");
        Console.WriteLine($"{"target",-18}{"aspect",-9}{"jet",-13}{"no chaff",10}{"chaff",8}{"per-bundle",12}");
        foreach (Craft c in crafts)
            foreach (bool ecm in c.HasEcm ? new[] { false, true } : new[] { false })
                foreach (Aspect a in new[] { Aspect.Tail, Aspect.Beam, Aspect.HeadOn })
                    foreach (float g in new[] { 0f, 6f })
                    {
                        float per = StealthMath.ChaffSeduction(ChaffChance, 1f, CmFloor, a == Aspect.Beam ? 1f : 0f, AspectRcs(c, ecm, a == Aspect.Beam ? 0f : a == Aspect.HeadOn ? 1f : -1f));
                        Console.WriteLine($"{c.Name + (ecm ? " ECM on" : ""),-18}{a,-9}{(g > 0f ? $"{g:0} g turn" : "straight"),-13}{Pk(false, a, g, c, ecm, false),8:P0}{Pk(false, a, g, c, ecm, true),9:P0}{per,11:P0}");
                    }
    }
}
