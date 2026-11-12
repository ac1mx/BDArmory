// Offline comparison of the pilot AI's legacy PID steering law against the model-normalised controller on a simplified single-axis aircraft.
// The aircraft here is a stand-in (short-period pitch dynamics with a rate-limited elevator), not KSP's aerodynamics:
// use it to check the control law's behaviour across the flight envelope, not to predict in-game numbers.
using System;
using BDArmory.Control;

static class Program
{
    const float dt = 0.02f;

    sealed class Plant
    {
        // dr/dt = q*(B*delta - A*r/v - C*sin(alpha)); dalpha/dt = r - L*q*alpha/v
        public double B = 0.15, A = 40, C = 0.6, L = 14.4;
        public double actuatorRate = 3, actuatorLag = 0.05; // stick units/s, s
        public double noise = 0.004; // rad/s on the measured rate
        public double r, alpha, delta, nose;
        readonly Random rng;
        public Plant(int seed) { rng = new Random(seed); }
        public void Step(double u, double q, double v)
        {
            double want = delta + (u - delta) * dt / (actuatorLag + dt);
            delta += Math.Max(-actuatorRate * dt, Math.Min(actuatorRate * dt, want - delta));
            double rDot = q * (B * delta - A * r / v - C * Math.Sin(alpha));
            r += rDot * dt;
            alpha += (r - L * q * alpha / v) * dt;
            nose += r * dt;
        }
        public double MeasuredRate()
        {
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
            return r + noise * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }

    sealed class LegacyPID
    {
        public float kp = 14f, ki = 0.4f, kd = 5f;
        float integral;
        public float Update(float errorDeg, float rate, float q, float v)
        {
            float e = Math.Max(-25f, Math.Min(25f, errorDeg)); // Aiming mode clamp.
            float p = 0.015f * kp * e;
            integral = Math.Max(-1f, Math.Min(1f, integral + e * dt));
            float d = kd * rate;
            float dyn = Math.Max(0f, Math.Min(1.2f, 16f * v / q));
            return Math.Max(-1f, Math.Min(1f, (p + ki * integral - d) * dyn));
        }
    }

    struct Result { public double settle, overshoot, trackRms; public float confidence; }

    static double Density(double alt) => 1.225 * Math.Exp(-alt / 5600.0); // Kerbin-like scale height.

    static Result Run(bool adaptive, double v, double alt, float kp, float ki, float kd, int seed)
    {
        double q = 0.5 * Density(alt) * v * v / 1000.0;
        var plant = new Plant(seed);
        var legacy = new LegacyPID { kp = kp, ki = ki, kd = kd };
        var axis = new AxisRateController { bandwidth = 5f, actuatorRate = (float)plant.actuatorRate };
        var rng = new Random(seed + 1);
        double target = 0, targetRate = 0;
        float lastU = 0;
        var result = new Result { settle = double.NaN };

        double t = 0;
        double phaseStart = 0;
        int phase = 0; // 0: warm-up manoeuvres, 1: 30° step, 2: tracking
        double maxOver = 0, sumSq = 0; int nTrack = 0; double lastOutside = 0;
        double nextJump = 0;
        while (true)
        {
            if (phase == 0)
            {
                if (t >= nextJump) { target = plant.nose + (rng.NextDouble() * 2 - 1) * 0.6; nextJump = t + 1.5 + rng.NextDouble() * 2; }
                if (t > 25) { phase = 1; phaseStart = t; target = plant.nose + 30 * Math.PI / 180; targetRate = 0; lastOutside = t; }
            }
            else if (phase == 1)
            {
                double err = target - plant.nose;
                if (Math.Abs(err) > Math.PI / 180) lastOutside = t;
                maxOver = Math.Max(maxOver, -err);
                if (t - phaseStart > 12) { result.settle = lastOutside - phaseStart; result.overshoot = maxOver * 180 / Math.PI; phase = 2; phaseStart = t; }
            }
            else
            {
                double tp = t - phaseStart;
                targetRate = 0.15 + 0.1 * Math.Sin(1.5 * tp); // rad/s
                target += targetRate * dt;
                if (tp > 5) { double e = (target - plant.nose) * 180 / Math.PI; sumSq += e * e; ++nTrack; }
                if (tp > 20) break;
            }

            float measured = (float)plant.MeasuredRate();
            float errRad = (float)(target - plant.nose);
            float relRate = measured - (float)targetRate;
            float qf = (float)q, vf = (float)v;
            float sinAoA = (float)Math.Sin(plant.alpha);

            axis.Identify(measured, sinAoA, qf, 1f, vf, 0f, dt);
            float uLegacy = legacy.Update(errRad * 180f / (float)Math.PI, relRate, qf, vf);
            float u = uLegacy;
            if (adaptive && axis.Control(errRad, relRate, measured, sinAoA, qf, 1f, vf, 0f, 1f, dt, out float uModel))
                u = uLegacy + (uModel - uLegacy) * axis.Confidence;
            axis.Applied(u, dt);
            lastU = u;
            plant.Step(u, q, v);
            t += dt;
            if (double.IsNaN(plant.nose) || Math.Abs(plant.r) > 50) { result.settle = double.NaN; result.trackRms = double.NaN; return result; }
        }
        result.trackRms = Math.Sqrt(sumSq / Math.Max(1, nTrack));
        result.confidence = axis.Confidence;
        return result;
    }

    // Dogfight-like run: speed and throttle change continuously and part of the control authority comes from
    // thrust vectoring and reaction wheels, i.e. does not scale with dynamic pressure.
    static void RunVarying(bool adaptive, float kp, float ki, float kd, int seed)
    {
        const double alt = 3000, wheel = 0.15, tvc = 1.2; // rad/s² per unit stick
        var plant = new Plant(seed);
        var legacy = new LegacyPID { kp = kp, ki = ki, kd = kd };
        var axis = new AxisRateController { bandwidth = 5f, actuatorRate = (float)plant.actuatorRate };
        var rng = new Random(seed + 1);
        double target = 0, nextJump = 0, sumSq = 0, stick = 0, authMin = double.MaxValue, authMax = 0, confSum = 0;
        int n = 0, onTarget = 0;
        float lastU = 0;
        bool trace = Environment.GetEnvironmentVariable("TRACE") == (adaptive ? "adaptive" : "legacy");
        for (double t = 0; t < 180; t += dt)
        {
            double v = 200 + 110 * Math.Sin(2 * Math.PI * t / 25);
            double throttle = 0.65 - 0.35 * Math.Sin(2 * Math.PI * t / 25 + 0.8) ;
            double q = 0.5 * Density(alt) * v * v / 1000.0;
            double constAuthority = wheel + tvc * throttle;
            if (t >= nextJump) { target = plant.nose + (rng.NextDouble() * 2 - 1) * 0.5; nextJump = t + 2 + rng.NextDouble() * 3; }

            float measured = (float)plant.MeasuredRate();
            float errRad = (float)(target - plant.nose);
            float qf = (float)q, vf = (float)v;
            float sinAoA = (float)Math.Sin(plant.alpha);
            axis.Identify(measured, sinAoA, qf, 1f, vf, (float)constAuthority, dt);
            float uLegacy = legacy.Update(errRad * 180f / (float)Math.PI, measured, qf, vf);
            float u = uLegacy;
            if (adaptive && axis.Control(errRad, measured, measured, sinAoA, qf, 1f, vf, (float)constAuthority, 1f, dt, out float uModel))
                u = uLegacy + (uModel - uLegacy) * axis.Confidence;
            axis.Applied(u, dt);
            plant.r += constAuthority * u * dt; // Vectored thrust and wheels act without actuator lag.
            plant.Step(u, q, v);
            if (t > 30)
            {
                double e = errRad * 180 / Math.PI;
                sumSq += e * e; ++n; if (Math.Abs(e) < 1) ++onTarget;
                stick += Math.Abs(u - lastU);
                authMin = Math.Min(authMin, axis.Authority); authMax = Math.Max(authMax, axis.Authority);
                confSum += axis.Confidence;
            }
            lastU = u;
            if (trace && (int)(t / dt) % 100 == 0) Console.WriteLine($"  t {t,5:0} v {v,4:0} q {q,5:0.0} thr {throttle:0.00} b {axis.Authority:0.000} a {axis.Damping,6:0.0} c {axis.Stability,6:0.00} d {axis.Bias,7:0.000} conf {axis.Confidence:0.00} alpha {plant.alpha * 57.3,6:0.0} delta {plant.delta,5:0.00}");
            if (double.IsNaN(plant.nose) || Math.Abs(plant.r) > 50) { Console.WriteLine($"{(adaptive ? "adaptive" : "legacy  ")}: diverged at {t:0.0}s"); return; }
        }
        Console.WriteLine($"{(adaptive ? "adaptive" : "legacy  ")}: errRMS {Math.Sqrt(sumSq / n),5:0.00}°  onTarget {100.0 * onTarget / n,4:0.0}%  stickChange {stick / n:0.0000}/frame  meanConf {confSum / n:0.00}  authority {authMin:0.000}–{authMax:0.000}");
    }

    // Cost of the model-based path for one craft per physics tick (three axes: identify, control, record input).
    static void MeasureCost()
    {
        var core = new PilotControlCore();
        var rng = new Random(3);
        const int ticks = 2_000_000;
        float sum = 0;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < ticks; ++i)
        {
            float r = (float)(rng.NextDouble() - 0.5), e = (float)(rng.NextDouble() - 0.5) * 0.4f;
            core.Identify(r, 0.5f * r, -r, 0.1f * r, 0.05f * r, 20f, 0.9f, 220f, 1f, 0.4f, 0.2f, dt);
            core.pitch.Control(e, r, r, 0.1f * r, 20f, 0.9f, 220f, 1f, 1f, dt, out float p);
            core.yaw.Control(e, 0.5f * r, 0.5f * r, 0.05f * r, 20f, 0.9f, 220f, 0.4f, 1f, dt, out float y);
            core.roll.Control(e, -r, -r, 0f, 20f, 0.9f, 220f, 0.2f, 1f, dt, out float ro);
            core.Applied(p, y, ro, dt);
            sum += p + y + ro;
        }
        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Console.WriteLine($"model path cost: {sw.Elapsed.TotalMilliseconds * 1000 / ticks:0.000} µs per craft per physics tick, {alloc / (double)ticks:0.00} bytes allocated per tick (checksum {sum:G3})");
    }

    // Approximate stock wing lift coefficient against sin(angle of attack): rises to a peak at 30° and falls to zero at 90°.
    static double StockLift(double sin)
    {
        double[] xs = { 0, 0.258819, 0.5, 0.7071068, 1 }, ys = { 0, 0.5114774, 0.9026583, 0.5926583, 0 };
        for (int i = 1; i < xs.Length; ++i)
            if (sin <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (sin - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return 0;
    }

    // Replays a flight recorded in the game (GameData/BDArmory/Logs/flightdata-*.csv) through the identification, and fits the
    // model structure by batch least squares for a range of actuator assumptions to show which one the recorded craft follows.
    static void Replay(string path)
    {
        var lines = System.IO.File.ReadAllLines(path);
        var header = lines[0].Split(',');
        int Col(string name) => Array.IndexOf(header, name);
        var rows = new System.Collections.Generic.List<double[]>();
        for (int i = 1; i < lines.Length; ++i)
        {
            var parts = lines[i].Split(',');
            if (parts.Length != header.Length) continue;
            var row = new double[parts.Length];
            bool ok = true;
            for (int j = 0; j < parts.Length && ok; ++j) ok = double.TryParse(parts[j], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out row[j]);
            if (ok) rows.Add(row);
        }
        Console.WriteLine($"{System.IO.Path.GetFileName(path)}: {rows.Count} ticks ({rows.Count * 0.02:0}s)");
        int cDt = Col("dt"), cQ = Col("q"), cV = Col("speed"), cM = Col("machFactor");
        var axes = new[] {
            (name: "pitch", rate: Col("pitchRate"), u: Col("pitch"), x: Col("sinAoA"), auth: Col("constPitch")),
            (name: "yaw", rate: Col("yawRate"), u: Col("yaw"), x: Col("sinSideslip"), auth: Col("constYaw")),
            (name: "roll", rate: Col("rollRate"), u: Col("roll"), x: -1, auth: Col("constRoll")) };
        // The in-flight identification on the same data, with the actuator rates the game reported for this craft.
        {
            int qc = Col("q"), vc = Col("speed"), mc = Col("machFactor"), dc = Col("dt");
            var online = new[] {
                (name: "pitch", rate: Col("pitchRate"), u: Col("pitch"), x: Col("sinAoA"), auth: Col("constPitch"), axis: new AxisRateController { actuatorRate = 1.33f, constResponseRate = 5f }),
                (name: "yaw", rate: Col("yawRate"), u: Col("yaw"), x: Col("sinSideslip"), auth: Col("constYaw"), axis: new AxisRateController { actuatorRate = 2f, constResponseRate = 3f }),
                (name: "roll", rate: Col("rollRate"), u: Col("roll"), x: -1, auth: Col("constRoll"), axis: new AxisRateController { actuatorRate = 1.67f, constResponseRate = 5f }) };
            foreach (var ax in online)
            {
                var authority = new System.Collections.Generic.List<float>();
                double confSum = 0; int trusted = 0, counted = 0;
                foreach (var r in rows)
                {
                    float dtk = (float)r[dc];
                    ax.axis.Identify((float)r[ax.rate], ax.x >= 0 ? (float)r[ax.x] : 0f, (float)r[qc], (float)r[mc], (float)r[vc], (float)r[ax.auth], dtk);
                    ax.axis.Applied((float)r[ax.u], dtk);
                    if (++counted > 1500) { confSum += ax.axis.Confidence; if (ax.axis.Confidence > 0.8f) ++trusted; authority.Add(ax.axis.Authority); }
                }
                authority.Sort();
                int m = authority.Count;
                if (m > 0) Console.WriteLine($"online {ax.name,-5}: mean confidence {confSum / m:0.00}, trusted {100.0 * trusted / m:0}% of the time, aero authority p10/p50/p90 {authority[m / 10]:0.000}/{authority[m / 2]:0.000}/{authority[9 * m / 10]:0.000} (after the first 30s)");
            }
        }
        int variant = int.TryParse(Environment.GetEnvironmentVariable("VARIANT"), out int parsed) ? parsed : 0;
        Console.WriteLine($"model variant {variant}");
        double[] surfaceRates = { 0, 1, 1.5, 2, 3, 5 }; // stick units per second, 0 = immediate
        double[] constRates = { 0, 2, 3, 5, 8 }; // per second, 0 = immediate
        foreach (var ax in axes)
        {
            Console.WriteLine($"{ax.name}: batch fit of dr/dt = k*B*uc + q*(b*m*delta - a*r/v - c*x + d), by surface travel rate (rows) and thrust response rate (columns): R² / k / b");
            Console.Write("        ");
            foreach (var cr in constRates) Console.Write($"{cr,20:0.0}");
            Console.WriteLine();
            foreach (var sr in surfaceRates)
            {
                Console.Write($"{sr,8:0.0}");
                foreach (var cr in constRates)
                {
                    const int n = 5;
                    var A = new double[n, n + 1];
                    double delta = 0, uc = 0, lastRate = double.NaN, accelF = 0, sst = 0;
                    var phi = new double[n]; var phiF = new double[n];
                    var samples = new System.Collections.Generic.List<(double y, double[] p)>();
                    foreach (var r in rows)
                    {
                        double dtk = r[cDt], q = r[cQ], v = r[cV];
                        if (dtk <= 0 || q < 0.5 || v < 20 || double.IsNaN(lastRate)) { lastRate = r[ax.rate]; delta = uc = r[ax.u]; continue; }
                        double accel = (r[ax.rate] - lastRate) / dtk;
                        // Variant 0: only the control term follows the lift Mach factor. 1: all aerodynamic terms do. 2: as 1, with the stability
                        // term following the wing lift curve (which peaks at 30° and falls off) instead of being linear in sin(AoA).
                        double ma = variant >= 1 ? r[cM] : 1, xs = ax.x >= 0 ? r[ax.x] : 0;
                        if (variant >= 2) xs = Math.Sign(xs) * StockLift(Math.Abs(xs));
                        phi[0] = r[ax.auth] * uc; phi[1] = q * r[cM] * delta; phi[2] = -q * ma * lastRate / v; phi[3] = -q * ma * xs; phi[4] = q * ma;
                        double w = dtk / (0.08 + dtk);
                        accelF += w * (accel - accelF);
                        for (int i = 0; i < n; ++i) phiF[i] += w * (phi[i] - phiF[i]);
                        samples.Add((accelF, (double[])phiF.Clone()));
                        lastRate = r[ax.rate];
                        double u = r[ax.u];
                        delta = sr > 0 ? delta + Math.Max(-sr * dtk, Math.Min(sr * dtk, u - delta)) : u;
                        uc = cr > 0 ? uc + Math.Min(1.0, cr * dtk) * (u - uc) : u;
                    }
                    foreach (var (y, p) in samples)
                    {
                        sst += y * y;
                        for (int i = 0; i < n; ++i) { for (int j = 0; j < n; ++j) A[i, j] += p[i] * p[j]; A[i, n] += p[i] * y; }
                    }
                    for (int i = 0; i < n; ++i) A[i, i] += 1e-9;
                    for (int i = 0; i < n; ++i) // Gaussian elimination with partial pivoting.
                    {
                        int piv = i;
                        for (int k = i + 1; k < n; ++k) if (Math.Abs(A[k, i]) > Math.Abs(A[piv, i])) piv = k;
                        for (int j = 0; j <= n; ++j) (A[i, j], A[piv, j]) = (A[piv, j], A[i, j]);
                        for (int k = 0; k < n; ++k)
                        {
                            if (k == i || A[i, i] == 0) continue;
                            double f = A[k, i] / A[i, i];
                            for (int j = i; j <= n; ++j) A[k, j] -= f * A[i, j];
                        }
                    }
                    var th = new double[n];
                    for (int i = 0; i < n; ++i) th[i] = A[i, i] != 0 ? A[i, n] / A[i, i] : 0;
                    double sse = 0;
                    foreach (var (y, p) in samples) { double e = y; for (int i = 0; i < n; ++i) e -= th[i] * p[i]; sse += e * e; }
                    Console.Write($"{$"{1 - sse / sst:0.00} / {th[0]:0.00} / {th[1]:0.000}",20}");
                }
                Console.WriteLine();
            }
        }
    }

    // Roll axis with slow control surfaces: dr/dt = a*delta - (a/terminalRate)*r, the stock roll PD law with and without the stopping-angle lead.
    // Defaults are the F-22A L from a recorded duel (steer mult 21.3, damping 7.06, roll surfaces 1.67 travels/s, ~6 rad/s² at 200 m/s).
    static void RunRoll(bool lead, double authorityPerPres, double helix, double actuatorRate, out double reversals, out double rms, out double settle, out double peakRate, out double leadTime)
    {
        const double v = 200, aeroPres = 11, steerMult = 21.29, steerDamping = 7.06, duration = 120;
        double a = authorityPerPres * aeroPres, damping = a / (helix * v);
        var axis = new AxisRateController { actuatorRate = (float)actuatorRate };
        var rng = new Random(3);
        double r = 0, delta = 0, angle = 0, target = 0, nextStep = 0, stepTime = 0, sumSq = 0, settleSum = 0, lastOutside = 0;
        int n = 0, flips = 0, last = 0, steps = 0;
        peakRate = 0;
        for (double t = 0; t < duration; t += dt)
        {
            if (t >= nextStep)
            {
                if (steps > 0) settleSum += lastOutside - stepTime;
                target = angle + (rng.Next(2) == 0 ? -1 : 1) * (60 + 110 * rng.NextDouble()) * Math.PI / 180;
                nextStep = t + 6; stepTime = lastOutside = t; ++steps;
            }
            axis.Identify((float)r, 0f, (float)(aeroPres), 1f, (float)v, 0f, dt);
            double errDeg = (target - angle) * 180 / Math.PI;
            if (lead)
            {
                axis.LearnLead((float)(target - angle), (float)r, dt);
                errDeg -= axis.LeadAngle((float)r, (float)(0.1 * steerDamping / (0.0015 * steerMult * 180 / Math.PI))) * 180 / Math.PI;
            }
            double u = Math.Max(-1, Math.Min(1, 1.2 * (0.0015 * steerMult * errDeg - 0.1 * steerDamping * r)));
            axis.Applied((float)u, dt);
            delta += Math.Max(-actuatorRate * dt, Math.Min(actuatorRate * dt, u - delta));
            r += (a * delta - damping * r) * dt;
            angle += r * dt;
            peakRate = Math.Max(peakRate, Math.Abs(r));
            int s = r > 0.4 ? 1 : r < -0.4 ? -1 : 0;
            if (s != 0) { if (last != 0 && s != last) ++flips; last = s; }
            if (Math.Abs(target - angle) > 10 * Math.PI / 180) lastOutside = t;
            if (t - stepTime > 2.5) { double e = (target - angle) * 180 / Math.PI; sumSq += e * e; ++n; }
        }
        leadTime = axis.LeadTime;
        reversals = flips / duration; rms = Math.Sqrt(sumSq / Math.Max(1, n)); settle = settleSum / Math.Max(1, steps - 1);
    }

    static void RollBench()
    {
        Console.WriteLine("roll steps of 60-170° every 6 s; reversals = roll rate sign changes per second, rms = error from 2.5 s after each step, settle = time to stay within 10°");
        Console.WriteLine("authority/kPa helix  surf/s | stock: reversals  rms   settle  peak | lead: reversals  rms   settle  peak  learnt");
        foreach (var (b, h, ar) in new[] { (0.55, 0.03, 1.67), (0.3, 0.03, 1.67), (1.1, 0.03, 1.67), (0.64, 0.02, 1.67), (0.55, 0.05, 1.67), (0.55, 0.03, 1.0), (0.55, 0.03, 3.0), (0.55, 0.03, 8.0) })
        {
            RunRoll(false, b, h, ar, out double f0, out double e0, out double s0, out double p0, out _);
            RunRoll(true, b, h, ar, out double f1, out double e1, out double s1, out double p1, out double lt);
            Console.WriteLine($"{b,10:0.00} {h,7:0.000} {ar,6:0.00} | {f0,14:0.00}/s {e0,5:0.0}° {s0,5:0.00}s {p0,5:0.0} | {f1,13:0.00}/s {e1,5:0.0}° {s1,5:0.00}s {p1,5:0.0} {lt,5:0.00}s");
        }
    }

    static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "roll") { RollBench(); return; }
        if (args.Length == 1 && args[0] == "cost") { MeasureCost(); return; }
        if (args.Length == 2 && args[0] == "replay") { Replay(args[1]); return; }
        float kp = 14f, ki = 0.4f, kd = 5f; // BDArmory defaults
        if (args.Length >= 3) { kp = float.Parse(args[0]); ki = float.Parse(args[1]); kd = float.Parse(args[2]); }
        Console.WriteLine("varying speed and throttle, thrust vectoring (true aerodynamic authority 0.15 per kPa):");
        RunVarying(false, kp, ki, kd, 1);
        RunVarying(true, kp, ki, kd, 1);
        Console.WriteLine($"legacy gains: P {kp} I {ki} D {kd}");
        Console.WriteLine("speed  alt   q(kPa) | legacy: settle  over   track | adaptive: settle  over   track  conf");
        double[] speeds = { 80, 150, 250, 400, 700, 1000 };
        double[] alts = { 500, 5000, 12000 };
        foreach (var alt in alts)
            foreach (var v in speeds)
            {
                double q = 0.5 * Density(alt) * v * v / 1000.0;
                var a = Run(false, v, alt, kp, ki, kd, 1);
                var b = Run(true, v, alt, kp, ki, kd, 1);
                Console.WriteLine($"{v,5:0} {alt,5:0} {q,7:0.0} | {a.settle,14:0.00}s {a.overshoot,5:0.0}° {a.trackRms,6:0.00}° | {b.settle,16:0.00}s {b.overshoot,5:0.0}° {b.trackRms,6:0.00}° {b.confidence,5:0.00}");
            }
    }
}
