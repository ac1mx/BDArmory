using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BDArmory.Control;
using BDArmory.Guidances;

// Missile config bench on the game's own flight replica (BDArmory/Control/MissileFlyout.cs).
//   tables <missiles.json> [atmosphere.txt]                       reach of each missile against straight-flying and running targets
//   features <missiles.json> <shots.csv> <out.csv> [atmosphere.txt]   the shot model's flight-prediction features for logged shots
//   balance <missiles.json> <targets.txt> <patch.cfg> [atmosphere.txt]  fit missile motors to reach targets, writing a ModuleManager patch
// missiles.json comes from bench/missiles/extract_missiles.py (ModuleManager.ConfigCache); atmosphere.txt has "altitude density" lines
// (written by the missile range at start-up); without it an exponential stand-in is used.
static class Program
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const float Gravity = 9.81f, SpeedOfSound = 300f;
    static float ResponseTime = 0.4f; // --lag: the replica's steering response time (s), as ShotModel uses.
    static float[] densityTable; const float DensityStep = 200f;

    static float Density(double altitude)
    {
        if (densityTable == null) return (float)(1.225 * Math.Exp(-Math.Max(altitude, 0) / 5600.0));
        float index = Math.Clamp((float)altitude / DensityStep, 0f, densityTable.Length - 1.001f);
        int i = (int)index;
        return densityTable[i] + (densityTable[i + 1] - densityTable[i]) * (index - i);
    }

    static void LoadAtmosphere(string path)
    {
        if (path == null || !File.Exists(path)) { Console.WriteLine("(no atmosphere table: exponential stand-in)"); return; }
        var rows = File.ReadAllLines(path).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out _))
            .Select(p => (alt: double.Parse(p[0], Inv), rho: float.Parse(p[1], Inv))).OrderBy(r => r.alt).ToList();
        densityTable = new float[(int)(rows.Last().alt / DensityStep) + 2];
        for (int i = 0; i < densityTable.Length; ++i)
        {
            double a = i * DensityStep;
            var lo = rows.LastOrDefault(r => r.alt <= a); var hi = rows.FirstOrDefault(r => r.alt >= a);
            densityTable[i] = hi.alt == lo.alt ? lo.rho : lo.rho + (hi.rho - lo.rho) * (float)((a - lo.alt) / (hi.alt - lo.alt));
        }
    }

    static float F(JsonElement e, string key, float fallback)
    {
        if (!e.TryGetProperty(key, out var v)) return fallback;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return float.TryParse(s.Split(',')[0].Trim(), NumberStyles.Float, Inv, out float f) ? f : fallback;
    }

    static Dictionary<string, (MissileSpec spec, float maxOffBoresight, float activeRadarRange, string targeting)> LoadMissiles(string path)
    {
        var result = new Dictionary<string, (MissileSpec, float, float, string)>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var part in doc.RootElement.EnumerateObject())
        {
            var ml = part.Value.GetProperty("MissileLauncher");
            var wh = part.Value.GetProperty("warhead");
            float lift = F(ml, "liftArea", 0.015f), drag = F(ml, "dragArea", -1f);
            float blast = F(wh, "blastRadius", -1f);
            if (blast <= 0) { float tnt = F(wh, "tntMass", 10f); blast = 3.5f * MathF.Cbrt(Math.Max(tnt, 0.1f)) * 1.5f; } // Rough stand-in when only the charge is given.
            float detonation = F(ml, "DetonationDistance", -1f);
            if (detonation > 0) blast = Math.Max(blast, detonation);
            var spec = new MissileSpec
            {
                mass = (float)part.Value.GetProperty("mass").GetDouble(), thrust = F(ml, "thrust", 30), boostTime = F(ml, "boostTime", 2.2f),
                cruiseThrust = F(ml, "cruiseThrust", 3), cruiseTime = F(ml, "cruiseTime", 45), dropTime = F(ml, "dropTime", 0.5f),
                liftArea = lift, dragArea = drag > 0 ? drag : lift, maxAoA = F(ml, "maxAoA", 35), maxTorque = F(ml, "maxTorque", 90), blastRadius = blast,
                liftMultiplier = 0.25f, dragMultiplier = 6f, responseTime = ResponseTime
            };
            string targeting = ml.TryGetProperty("targetingType", out var tt) ? tt.GetString().ToLowerInvariant() : "none";
            result[part.Name] = (spec, F(ml, "maxOffBoresight", 180), F(ml, "activeRadarRange", 6000), targeting);
        }
        return result;
    }

    static MissileFlyout.Result Shot(in MissileSpec m, double altitude, double launchSpeed, double range, double targetAltitude, double targetSpeed, double aspectDeg, bool escape)
    {
        double a = aspectDeg * Math.PI / 180.0;
        double tvx = escape ? targetSpeed : -targetSpeed * Math.Cos(a), tvy = escape ? 0 : targetSpeed * Math.Sin(a);
        return MissileFlyout.Fly(m, launchSpeed, 0, 0, range, 0, targetAltitude - altitude, tvx, tvy, 0, escape ? 10 : 0, 0, 0, escape ? 1.25 * targetSpeed : targetSpeed,
            altitude, Density, Gravity, SpeedOfSound);
    }

    /// <summary>Longest launch distance that still reaches (bisection; assumes reach is lost once and for all beyond some distance).</summary>
    static double Reach(in MissileSpec m, double altitude, double launchSpeed, double targetSpeed, double aspectDeg, bool escape)
    {
        double lo = 1000, hi = 250000;
        if (!Shot(m, altitude, launchSpeed, lo, altitude, targetSpeed, aspectDeg, escape).reached) return 0;
        while (hi - lo > 200)
        {
            double mid = 0.5 * (lo + hi);
            if (Shot(m, altitude, launchSpeed, mid, altitude, targetSpeed, aspectDeg, escape).reached) lo = mid; else hi = mid;
        }
        return lo;
    }

    static void Tables(string missilesPath)
    {
        var missiles = LoadMissiles(missilesPath);
        Console.WriteLine("Reach in km against a 280 m/s target flying straight (hot: at the shooter, cold: away) or turning away at launch and running (run),");
        Console.WriteLine("launched level at 300 m/s; v@cut: speed when the motor stops (level, 6 km); g: turning g at 600 m/s, 6 km; motor: total impulse / mass.");
        Console.WriteLine($"{"missile",-14}{"seeker",-9}{"motor",7}{"v@cut",7}{"hot3",7}{"cold3",7}{"run3",7}{"hot9",7}{"cold9",7}{"run9",7}{"hot14",7}{"run14",7}{"g",6}");
        foreach (var (name, (m, _, _, targeting)) in missiles.OrderBy(k => k.Value.targeting).ThenBy(k => k.Key))
        {
            double motor = (m.thrust * m.boostTime + m.cruiseThrust * m.cruiseTime) / m.mass;
            var cut = MissileFlyout.Fly(m, 300, 0, 0, 1e7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6000, Density, Gravity, SpeedOfSound, maxTime: m.dropTime + m.boostTime + m.cruiseTime);
            double g = MissileFlyout.TurningG(m, 600f, Density(6000), Gravity);
            Console.WriteLine($"{name,-14}{targeting,-9}{motor,7:F0}{cut.speed,7:F0}" +
                $"{Reach(m, 3000, 300, 280, 0, false) / 1000,7:F1}{Reach(m, 3000, 300, 280, 180, false) / 1000,7:F1}{Reach(m, 3000, 300, 280, 0, true) / 1000,7:F1}" +
                $"{Reach(m, 9000, 300, 280, 0, false) / 1000,7:F1}{Reach(m, 9000, 300, 280, 180, false) / 1000,7:F1}{Reach(m, 9000, 300, 280, 0, true) / 1000,7:F1}" +
                $"{Reach(m, 14000, 300, 280, 0, false) / 1000,7:F1}{Reach(m, 14000, 300, 280, 0, true) / 1000,7:F1}{Math.Min(g, 999),6:F0}");
        }
    }

    static void Features(string missilesPath, string shotsCsv, string outCsv)
    {
        var missiles = LoadMissiles(missilesPath);
        var lines = File.ReadAllLines(shotsCsv);
        var header = lines[0].Split(',').Select(h => h.Trim('"')).ToList();
        using var output = new StreamWriter(outCsv);
        output.WriteLine("part,hit,targetMode,range,aspect,offBoresight,launchAlt,flyReaches,flyTime,flyMiss,flySpeed,flyG,escReaches,escMiss,energyMargin,offBoresightFraction,launcherGuided,heat");
        foreach (var row in lines.Skip(1))
        {
            var cells = SplitCsv(row);
            string Get(string k) { int i = header.IndexOf(k); return i >= 0 && i < cells.Count ? cells[i] : ""; }
            if (!missiles.TryGetValue(Get("part"), out var entry)) continue;
            var (m, maxOffBoresight, activeRadarRange, targeting) = entry;
            double[] V(string k) { var p = Get(k).Split(','); return p.Length == 3 ? p.Select(x => double.Parse(x, Inv)).ToArray() : null; }
            var rel = V("rel"); var vel = V("vel"); var tv = V("targetVel");
            if (rel == null || vel == null || tv == null) continue;
            double altitude = double.Parse(Get("launchAltMSL"), Inv), range = Math.Sqrt(rel[0] * rel[0] + rel[1] * rel[1] + rel[2] * rel[2]);
            if (Math.Sqrt(vel[0] * vel[0] + vel[1] * vel[1] + vel[2] * vel[2]) < 1) continue; // Launched at rest (harness bug in the first runs).
            double targetSpeed = Math.Max(Math.Sqrt(tv[0] * tv[0] + tv[1] * tv[1] + tv[2] * tv[2]), 50);
            var straight = MissileFlyout.Fly(m, vel[0], vel[1], vel[2], rel[0], rel[1], rel[2], tv[0], tv[1], tv[2], 0, 0, 0, targetSpeed, altitude, Density, Gravity, SpeedOfSound);
            double h = Math.Sqrt(rel[0] * rel[0] + rel[1] * rel[1]); double ax = rel[0] / h, ay = rel[1] / h;
            var escape = MissileFlyout.Fly(m, vel[0], vel[1], vel[2], rel[0], rel[1], rel[2], targetSpeed * ax, targetSpeed * ay, 0, 10 * ax, 10 * ay, 0, 1.25 * targetSpeed, altitude, Density, Gravity, SpeedOfSound);
            float kinematic = MissileFlyout.KinematicSpeed(m, Density(altitude), Gravity, SpeedOfSound);
            double margin = Math.Clamp(Math.Log(Math.Max(straight.speed, 1) / Math.Max(kinematic, 50)), -1, 2);
            double offB = Math.Clamp(double.Parse(Get("offBoresight"), Inv) / Math.Clamp(maxOffBoresight, 1, 180), 0, 2);
            double guided = targeting is "radar" or "inertial" ? Math.Clamp(1 - activeRadarRange / range, 0, 1) : 0;
            output.WriteLine(string.Join(",", Get("part"), Get("hit"), Get("targetMode"), range.ToString("F0", Inv), Get("aspect"), Get("offBoresight"), Get("launchAlt"),
                straight.reached, straight.time.ToString("F1", Inv), straight.missDistance.ToString("F0", Inv), straight.speed.ToString("F0", Inv), straight.lateralG.ToString("F1", Inv),
                escape.reached, escape.missDistance.ToString("F0", Inv), margin.ToString("F2", Inv), offB.ToString("F2", Inv), guided.ToString("F2", Inv), targeting == "heat"));
        }
    }

    /// <summary>
    /// Time the flyouts the engagement planner and shot model run: every logged MissileShot's launch state flown straight and against
    /// a turning, running target, repeated. Prints µs per flyout and a checksum of every result field, so a faster MissileFlyout can be
    /// checked to fly exactly the same.
    /// </summary>
    static void Timing(string missilesPath, string logPath, int repeats)
    {
        var missiles = LoadMissiles(missilesPath);
        var field = new System.Text.RegularExpressions.Regex(@"(\w+)=(\S+)");
        var shots = new List<(MissileSpec m, double[] rel, double[] vel, double[] tv, double alt)>();
        foreach (var line in File.ReadLines(logPath))
        {
            int at = line.IndexOf("MissileShot ", StringComparison.Ordinal);
            if (at < 0 || !line.Contains("flyReaches=")) continue;
            var d = field.Matches(line.Substring(at)).ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value);
            if (!d.TryGetValue("part", out var part) || !missiles.TryGetValue(part, out var entry)) continue;
            double[] V(string k) { if (!d.TryGetValue(k, out var s)) return null; var p = s.Split(','); return p.Length == 3 ? p.Select(x => double.Parse(x, Inv)).ToArray() : null; }
            var rel = V("rel"); var vel = V("vel"); var tv = V("targetVel");
            if (rel == null || vel == null || tv == null || Math.Sqrt(vel[0] * vel[0] + vel[1] * vel[1] + vel[2] * vel[2]) < 1) continue;
            shots.Add((entry.spec, rel, vel, tv, double.Parse(d["launchAltMSL"], Inv)));
        }
        double checksum = 0; int flyouts = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int r = 0; r < repeats; ++r)
            foreach (var (m, rel, vel, tv, alt) in shots)
            {
                double targetSpeed = Math.Max(Math.Sqrt(tv[0] * tv[0] + tv[1] * tv[1] + tv[2] * tv[2]), 50);
                double h = Math.Max(Math.Sqrt(rel[0] * rel[0] + rel[1] * rel[1]), 1); double ax = rel[0] / h, ay = rel[1] / h;
                var straight = MissileFlyout.Fly(m, vel[0], vel[1], vel[2], rel[0], rel[1], rel[2], tv[0], tv[1], tv[2], 0, 0, 0, targetSpeed, alt, Density, Gravity, SpeedOfSound);
                var escape = MissileFlyout.Fly(m, vel[0], vel[1], vel[2], rel[0], rel[1], rel[2], targetSpeed * ax, targetSpeed * ay, 0, 10 * ax, 10 * ay, 0, 1.25 * targetSpeed, alt, Density, Gravity, SpeedOfSound);
                flyouts += 2;
                if (r == 0)
                    foreach (var f in new[] { straight, escape })
                        checksum = checksum * 1.000001 + f.time + f.missDistance + f.speed + f.closing + f.lateralG + (f.reached ? 1 : 0) + (f.gaveUp ? 2 : 0);
            }
        watch.Stop();
        Console.WriteLine($"{shots.Count} shots, {flyouts} flyouts, {watch.Elapsed.TotalMilliseconds * 1000 / flyouts:F1} us per flyout, checksum {checksum:R}");
    }

    record Metrics(double Hot9, double Run9, double Hot3, double SpeedAtCut, double G);

    static Metrics Measure(in MissileSpec m)
    {
        var cut = MissileFlyout.Fly(m, 300, 0, 0, 1e7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6000, Density, Gravity, SpeedOfSound, maxTime: m.dropTime + m.boostTime + m.cruiseTime);
        return new Metrics(Reach(m, 9000, 300, 280, 0, false) / 1000, Reach(m, 9000, 300, 280, 0, true) / 1000, Reach(m, 3000, 300, 280, 0, false) / 1000,
            cut.speed, MissileFlyout.TurningG(m, 600f, Density(6000), Gravity));
    }

    /// <summary>
    /// Fit each listed missile's motor (boost and sustain thrust and time) and drag to its targets: no-escape reach at 9 km (target turns
    /// away at launch and runs), head-on reach at 9 and 3 km, and a band for the speed when the motor stops. A small pull towards the current
    /// values keeps each missile's character where the targets leave room. Targets file lines: name hot9 run9 hot3 speedMin speedMax (km, m/s),
    /// optionally activeRange (km): where a radar missile's own seeker takes over from the launcher's radar (written to the patch, not fitted).
    /// </summary>
    static void Balance(string missilesPath, string targetsPath, string patchPath)
    {
        var missiles = LoadMissiles(missilesPath);
        var targets = File.ReadAllLines(targetsPath).Select(l => l.Split('#')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 6)
            .ToDictionary(p => p[0], p => p.Skip(1).Take(6).Select(x => double.Parse(x, Inv)).ToArray());
        var results = new Dictionary<string, (MissileSpec spec, Metrics before, Metrics after)>();
        var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) };
        System.Threading.Tasks.Parallel.ForEach(targets, options, entry =>
        {
            if (!missiles.TryGetValue(entry.Key, out var info)) { Console.WriteLine($"{entry.Key}: not in {missilesPath}"); return; }
            var m0 = info.spec; var t = entry.Value;
            // Parameters: log boost acceleration, boost time, log sustain acceleration (fraction of boost), sustain time, log drag area scale.
            double[] x0 = { Math.Log(m0.thrust / m0.mass), m0.boostTime, Math.Log(Math.Clamp(m0.cruiseThrust / Math.Max(m0.thrust, 1e-3f), 0.02f, 1f)), Math.Min(m0.cruiseTime, 90), 0 };
            MissileSpec Apply(double[] x)
            {
                var m = m0;
                m.thrust = (float)(Math.Exp(x[0]) * m0.mass); m.boostTime = (float)x[1];
                m.cruiseThrust = (float)(m.thrust * Math.Exp(x[2])); m.cruiseTime = (float)x[3];
                m.dragArea = (float)(m0.dragArea * Math.Exp(x[4]));
                return m;
            }
            double Loss(double[] x, out Metrics r)
            {
                r = null;
                if (x[1] < 1 || x[1] > 10 || x[3] < 0 || x[3] > 90 || x[2] > 0 || x[2] < Math.Log(0.02) || Math.Abs(x[4]) > Math.Log(5)) return double.MaxValue;
                r = Measure(Apply(x));
                double Sq(double v, double target) => Math.Pow(Math.Log(Math.Max(v, 0.1) / target), 2);
                double loss = 2 * Sq(r.Run9, t[1]) + 0.7 * Sq(r.Hot9, t[0]) + 0.5 * Sq(r.Hot3, t[2]);
                if (r.SpeedAtCut < t[3]) loss += 2 * Sq(r.SpeedAtCut, t[3]);
                if (r.SpeedAtCut > t[4]) loss += 2 * Sq(r.SpeedAtCut, t[4]);
                for (int i = 0; i < 5; ++i) loss += 0.01 * Math.Pow((x[i] - x0[i]) / (i == 1 || i == 3 ? Math.Max(x0[i], 2) : 1), 2);
                return loss;
            }
            var before = Measure(m0);
            double[] best = (double[])x0.Clone(); best[3] = Math.Max(best[3], 1);
            double bestLoss = Loss(best, out var bestMetrics);
            double[] steps = { 0.4, 1.0, 0.5, 8, 0.4 };
            for (int round = 0; round < 7; ++round)
            {
                bool improved = true;
                while (improved)
                {
                    improved = false;
                    for (int i = 0; i < 5; ++i)
                        foreach (int sign in new[] { 1, -1 })
                        {
                            var trial = (double[])best.Clone(); trial[i] += sign * steps[i];
                            double loss = Loss(trial, out var metrics);
                            if (loss < bestLoss) { best = trial; bestLoss = loss; bestMetrics = metrics; improved = true; }
                        }
                }
                for (int i = 0; i < 5; ++i) steps[i] *= 0.5;
            }
            lock (results) results[entry.Key] = (Apply(best), before, bestMetrics);
        });

        Console.WriteLine($"{"missile",-14}{"",8}{"thrust",8}{"boost",7}{"cruise",8}{"cTime",7}{"drag",8}{"hot9",7}{"run9",7}{"hot3",7}{"v@cut",7}{"g",6}  targets");
        var patch = new System.Text.StringBuilder(
            "// Air-to-air missiles carried by the Modern Warfare 1 craft: motor, drag and launch range balanced into short, medium and long range classes\n" +
            "// on BDArmory's own missile flight model and Kerbin's atmosphere as KSPTweaks stretches it (atmosphereStretch = 2.0, atmosphereShape = 0.8).\n" +
            "// Generated by BDArmory-src bench/MissileBench (balance, targets in bench/missiles/balance_targets.txt); regenerate rather than hand-edit.\n");
        foreach (var name in targets.Keys.Where(results.ContainsKey))
        {
            var (spec, before, after) = results[name]; var m0 = missiles[name].spec; var t = targets[name];
            Console.WriteLine($"{name,-14}{"before",8}{m0.thrust,8:F1}{m0.boostTime,7:F1}{m0.cruiseThrust,8:F1}{m0.cruiseTime,7:F0}{m0.dragArea,8:F4}{before.Hot9,7:F1}{before.Run9,7:F1}{before.Hot3,7:F1}{before.SpeedAtCut,7:F0}{before.G,6:F0}");
            Console.WriteLine($"{"",-14}{"after",8}{spec.thrust,8:F1}{spec.boostTime,7:F1}{spec.cruiseThrust,8:F1}{spec.cruiseTime,7:F0}{spec.dragArea,8:F4}{after.Hot9,7:F1}{after.Run9,7:F1}{after.Hot3,7:F1}{after.SpeedAtCut,7:F0}{after.G,6:F0}  {string.Join(" ", t)}");
            // The launch zone adds closing speed × flight time to the static range, so 0.6 of the head-on reach lines it up with the reach head-on.
            double maxStatic = Math.Round(0.6 * after.Hot9 * 2) / 2 * 1000;
            // KSP turns underscores in part names into dots, so the config node can have either: match one character there.
            patch.Append(Inv, $"@PART[{name.Replace('.', '?')}]:FINAL\n{{\n\t@MODULE[MissileLauncher]\n\t{{\n\t\t%thrust = {spec.thrust:0.#}\n\t\t%boostTime = {spec.boostTime:0.#}\n\t\t%cruiseThrust = {spec.cruiseThrust:0.#}\n\t\t%cruiseTime = {spec.cruiseTime:0.#}\n\t\t%dragArea = {spec.dragArea:0.#####}\n\t\t%maxStaticLaunchRange = {maxStatic:0}\n");
            if (t.Length > 5 && t[5] > 0) patch.Append(Inv, $"\t\t%activeRadarRange = {t[5] * 1000:0}\n");
            patch.Append("\t}\n}\n");
        }
        if (patchPath != null) File.WriteAllText(patchPath, patch.ToString());
    }

    /// <summary>
    /// Homing laws against a target that breaks or weaves late, and motor modes for reach and arrival speed: each missile at shares of
    /// its head-on reach, from ahead, abeam and behind.
    /// </summary>
    static void Laws(string missilesPath, string[] names)
    {
        var missiles = LoadMissiles(missilesPath);
        var laws = new[] { GuidanceLaw.ProNav, GuidanceLaw.Optimal, GuidanceLaw.Game, GuidanceLaw.SlidingMode };
        const double altitude = 6000, launchSpeed = 300, targetSpeed = 280;
        Console.WriteLine("Hits out of the evasion cases (9 g break at 6/3/1.5 s to go either way, 7 g weave 4 s/2 s from 10 s) x aspects (0/60/120/180) x range (30/55/80% of head-on reach); mean miss (m) of the misses.");
        Console.WriteLine($"{"missile",-14}" + string.Concat(laws.Select(l => $"{l,16}")));
        foreach (var name in names)
        {
            if (!missiles.TryGetValue(name, out var entry)) { Console.WriteLine($"{name}: not in {missilesPath}"); continue; }
            var m = entry.spec;
            double reach = Reach(m, altitude, launchSpeed, targetSpeed, 0, false);
            string row = $"{name,-14}";
            foreach (var law in laws)
            {
                var spec = m; spec.law = law; spec.targetMax = 90f;
                int hits = 0, n = 0; double miss = 0;
                foreach (double share in new[] { 0.3, 0.55, 0.8 })
                    foreach (double aspect in new[] { 0.0, 60, 120, 180 })
                        foreach (var (g, tgo, period) in new[] { (9f, 6f, 0f), (-9f, 6f, 0f), (9f, 3f, 0f), (-9f, 3f, 0f), (9f, 1.5f, 0f), (-9f, 1.5f, 0f), (7f, 10f, 4f), (-7f, 10f, 4f), (7f, 10f, 2f), (-7f, 10f, 2f) })
                        {
                            double a = aspect * Math.PI / 180.0, range = share * reach * (aspect > 90 ? 0.5 : 1);
                            var result = MissileFlyout.Fly(spec, launchSpeed, 0, 0, range, 0, 0, -targetSpeed * Math.Cos(a), targetSpeed * Math.Sin(a), 0, 0, 0, 0, targetSpeed,
                                altitude, Density, Gravity, SpeedOfSound, evadeG: g, evadeTimeToGo: tgo, evadePeriod: period);
                            ++n;
                            if (result.reached) ++hits; else miss += result.missDistance;
                        }
                row += $"{hits,7}/{n,-3}{(n > hits ? miss / (n - hits) : 0),5:F0}";
            }
            Console.WriteLine(row);
        }
        Console.WriteLine();
        Console.WriteLine("Motor modes: reach (km) hot / running target at 6 km, and arrival speed (m/s) and time (s) on a hot shot at 60% of the sustain reach.");
        Console.WriteLine($"{"missile",-14}{"mode",-10}{"hot",7}{"run",7}{"v@60%",8}{"t@60%",7}{"run9",7}{"run14",7}");
        foreach (var name in names)
        {
            if (!missiles.TryGetValue(name, out var entry)) continue;
            double baseReach = Reach(entry.spec, altitude, launchSpeed, targetSpeed, 0, false);
            foreach (var (mode, minThrottle) in new[] { (MotorMode.Sustain, 0f), (MotorMode.Pulse, 0f), (MotorMode.Throttle, 0.3f), (MotorMode.Throttle, 0.5f) })
            {
                var spec = entry.spec; spec.motor = mode; spec.minThrottle = minThrottle;
                var at60 = Shot(spec, altitude, launchSpeed, 0.6 * baseReach, altitude, targetSpeed, 0, false);
                Console.WriteLine($"{name,-14}{mode + (minThrottle > 0 ? " " + minThrottle.ToString(Inv) : ""),-13}{Reach(spec, altitude, launchSpeed, targetSpeed, 0, false) / 1000,5:F1}{Reach(spec, altitude, launchSpeed, targetSpeed, 0, true) / 1000,7:F1}" +
                    $"{at60.speed,8:F0}{at60.time,7:F1}{Reach(spec, 9000, launchSpeed, targetSpeed, 0, true) / 1000,7:F1}{Reach(spec, 14000, launchSpeed, targetSpeed, 0, true) / 1000,7:F1}");
            }
        }
    }

    static List<string> SplitCsv(string line)
    {
        var cells = new List<string>(); var cur = new System.Text.StringBuilder(); bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells;
    }

    static int Main(string[] args)
    {
        int lag = Array.IndexOf(args, "--lag");
        if (lag >= 0 && lag + 1 < args.Length)
        {
            ResponseTime = float.Parse(args[lag + 1], Inv);
            args = args.Take(lag).Concat(args.Skip(lag + 2)).ToArray();
        }
        if (args.Length >= 2 && args[0] == "tables") { LoadAtmosphere(args.Length > 2 ? args[2] : null); Tables(args[1]); return 0; }
        if (args.Length >= 4 && args[0] == "features") { LoadAtmosphere(args.Length > 4 ? args[4] : null); Features(args[1], args[2], args[3]); return 0; }
        if (args.Length >= 4 && args[0] == "balance") { LoadAtmosphere(args.Length > 4 ? args[4] : null); Balance(args[1], args[2], args[3]); return 0; }
        if (args.Length >= 3 && args[0] == "laws") { LoadAtmosphere(args[2]); Laws(args[1], args.Skip(3).ToArray()); return 0; }
        if (args.Length >= 3 && args[0] == "timing") { LoadAtmosphere(args.Length > 4 ? args[4] : null); Timing(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 5); return 0; }
        Console.WriteLine("usage: tables <missiles.json> [atmosphere.txt] | features <missiles.json> <shots.csv> <out.csv> [atmosphere.txt]" +
            " | balance <missiles.json> <targets.txt> <patch.cfg> [atmosphere.txt] | timing <missiles.json> <KSP.log> [repeats] [atmosphere.txt]");
        return 1;
    }
}
