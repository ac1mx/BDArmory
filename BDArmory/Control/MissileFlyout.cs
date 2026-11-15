using System;

using BDArmory.Guidances;

namespace BDArmory.Control
{
    /// <summary>The parts of a MissileLauncher's config that decide how it flies.</summary>
    public struct MissileSpec
    {
        public float mass, thrust, boostTime, cruiseThrust, cruiseTime, dropTime, liftArea, dragArea, maxAoA, maxTorque, blastRadius;
        public float liftMultiplier, dragMultiplier;
        public float responseTime; // s: time constant of the turning acceleration following the guidance command (0: at once).
        public GuidanceLaw law;    // Homing law (Config and ProNav: proportional navigation).
        public float lawRange;     // m: the law is flown inside this range of the target, proportional navigation before (0: all the way).
        public float targetMax;    // m/s²: the turn the law assumes the target capable of.
        public MotorMode motor;    // How the sustainer is spent.
        public float minThrottle;  // Throttle motors: lowest throttle.
        public float sustainUsed;  // s of sustainer already burnt at full thrust (a missile in flight).
        public bool sustainLit;    // Pulse motors: the second pulse is already burning (a missile in flight).
    }

    /// <summary>
    /// Fast replica of BDArmory's missile flight model (MissileLauncher thrust phases, MissileGuidance.DoAeroForces lift and drag curves and
    /// torque-limited angle of attack, MissileLauncher.CheckMiss's kinematic give-up), flying proportional navigation as a point mass in a
    /// local flat frame (x, y level, z up, metres, relative to the launch point). Plain arithmetic, no engine types, so that the bench can
    /// run the same code. Used to predict whether a shot can reach before it's taken.
    /// </summary>
    public static class MissileFlyout
    {
        public struct Result
        {
            public bool reached;        // Came within the blast radius.
            public float time;          // Time to the closest approach (s).
            public float missDistance;  // Closest approach (m).
            public float speed;         // Missile speed at the closest approach (m/s).
            public float closing;       // Closing speed at the closest approach (m/s).
            public float lateralG;      // Turning acceleration the missile could still pull at the closest approach (g).
            public bool gaveUp;         // Stopped as too slow to steer (CheckMiss) before reaching.
        }

        // MissileGuidance.DefaultLiftCurve and DefaultDragCurve: per key (angle of attack °, value, in tangent, out tangent). Flat arrays:
        // the flyout evaluates these a dozen times a step, and Mono indexes rectangular arrays through a slow path.
        static readonly float[] liftKeys = { 0, 0, 0.04375f, 0.04375f, 8, 0.35f, 0.04801136f, 0.04801136f, 30, 1.5f, 0, 0, 65, 0.6f, 0, 0, 90, 0.7f, 0, 0 };
        static readonly float[] dragKeys = { 0, 0.00215f, 0, 0, 5, 0.00285f, 0.0002775f, 0.0002775f, 30, 0.01f, 0.0002142857f, 0.01115385f, 55, 0.3f, 0.008434067f, 0.008434067f, 90, 0.5f, 0.005714285f, 0.005714285f };

        /// <summary>Cubic Hermite through the keys, as Unity's AnimationCurve (FloatCurve) evaluates them.</summary>
        static float Curve(float[] keys, float x)
        {
            int last = keys.Length - 4;
            if (x <= keys[0]) return keys[1];
            if (x >= keys[last]) return keys[last + 1];
            for (int i = 0; i < last; i += 4)
            {
                float t0 = keys[i], t1 = keys[i + 4];
                if (x > t1) continue;
                float dt = t1 - t0, s = (x - t0) / dt, s2 = s * s, s3 = s2 * s;
                return (2 * s3 - 3 * s2 + 1) * keys[i + 1] + (s3 - 2 * s2 + s) * dt * keys[i + 3] + (-2 * s3 + 3 * s2) * keys[i + 5] + (s3 - s2) * dt * keys[i + 6];
            }
            return keys[last + 1];
        }
        public static float Lift(float aoa) => Math.Max(Curve(liftKeys, aoa), 0f);
        public static float Drag(float aoa) => Math.Max(Curve(dragKeys, aoa), 0f);

        static float ThrustAt(in MissileSpec m, float t)
        {
            if (t < m.dropTime) return 0f;
            t -= m.dropTime;
            if (t < m.boostTime) return m.thrust;
            if (t < m.boostTime + m.cruiseTime) return m.cruiseThrust;
            return 0f;
        }

        /// <summary>Largest angle of attack at which the aerodynamic torque (centre of lift 1 m behind the CoM) stays within 90% of maxTorque.</summary>
        static float TorqueAoALimit(in MissileSpec m, float q)
        {
            float limit = 0.9f * m.maxTorque, aoa = 0f;
            float liftSk = m.liftArea * m.liftMultiplier, dragSk = m.dragArea * m.dragMultiplier;
            while (aoa < m.maxAoA)
            {
                float a = aoa + 1f;
                if (q * TorqueFactor(a, liftSk, dragSk) > limit) break;
                aoa = a;
            }
            return Math.Max(aoa, 1f);
        }

        /// <summary>Aerodynamic torque per unit dynamic pressure at an angle of attack (the term TorqueAoALimit compares).</summary>
        static float TorqueFactor(float a, float liftSk, float dragSk)
        {
            float r = a * (float)(Math.PI / 180.0);
            return liftSk * Lift(a) * (float)Math.Cos(r) + dragSk * Drag(a) * (float)Math.Sin(r);
        }

        /// <summary>
        /// TorqueAoALimit for one missile at any dynamic pressure, from a table built once: the running maximum of the torque factor over
        /// the whole-degree angles it steps through. The first angle whose torque passes the limit is the first where that maximum does, and
        /// the maximum grows with the angle, so a binary search finds the same angle the step-by-step climb would.
        /// </summary>
        sealed class TorqueLimitTable
        {
            float[] runningMax = new float[0];
            int count;
            float limit, maxAoA;

            public void Build(in MissileSpec m)
            {
                limit = 0.9f * m.maxTorque; maxAoA = m.maxAoA;
                float liftSk = m.liftArea * m.liftMultiplier, dragSk = m.dragArea * m.dragMultiplier;
                count = 0;
                float best = float.NegativeInfinity;
                for (float aoa = 0f; aoa < maxAoA; aoa += 1f)
                {
                    if (count == runningMax.Length) Array.Resize(ref runningMax, Math.Max(16, 2 * count));
                    best = Math.Max(best, TorqueFactor(aoa + 1f, liftSk, dragSk));
                    runningMax[count++] = best;
                }
            }

            public float Limit(float q)
            {
                int lo = 0, hi = count; // First index whose running maximum passes the limit, count if none does.
                while (lo < hi) { int mid = (lo + hi) >> 1; if (q * runningMax[mid] > limit) hi = mid; else lo = mid + 1; }
                return Math.Max(lo, 1f); // Index i is the angle i + 1; the climb stops one degree short of it.
            }
        }
        [ThreadStatic] static TorqueLimitTable torqueLimits;

        /// <summary>
        /// The angle of attack up to the cap that turns hardest: lift peaks at 30° and falls beyond it, while the thrust's share across the
        /// flight path keeps growing, so past 30° only a few angles need checking.
        /// </summary>
        static float BestTurningAoA(float cap, float q, float liftSk, float thrust)
        {
            if (cap <= 30f) return cap;
            float best = 30f, bestLateral = q * liftSk * Lift(30f) + thrust * (float)Math.Sin(30f * Math.PI / 180.0);
            for (float a = 40f; a < cap + 9.99f; a += 10f)
            {
                float angle = Math.Min(a, cap);
                float lateral = q * liftSk * Lift(angle) + thrust * (float)Math.Sin(angle * Math.PI / 180.0);
                if (lateral > bestLateral) { best = angle; bestLateral = lateral; }
            }
            return best;
        }

        /// <summary>The smallest angle of attack (°, up to the cap) that turns at the given acceleration (m/s²) at a dynamic pressure and thrust.</summary>
        public static float AoAForLateral(in MissileSpec m, float q, float thrust, float lateral, float cap)
        {
            const double deg = Math.PI / 180.0;
            float liftSk = m.liftArea * m.liftMultiplier, mass = Math.Max(m.mass, 1e-4f);
            float Lateral(float a) => (q * liftSk * Lift(a) + thrust * (float)Math.Sin(a * deg)) / mass;
            if (lateral <= 1e-3f) return 0f;
            if (Lateral(cap) <= lateral) return cap;
            float lo = 0f, hi = cap;
            for (int i = 0; i < 12; ++i) { float mid = 0.5f * (lo + hi); if (Lateral(mid) < lateral) lo = mid; else hi = mid; }
            return hi;
        }

        /// <summary>The angle of attack (°) at which the missile turns hardest at a dynamic pressure and thrust, within its limits.</summary>
        public static float BestAoA(in MissileSpec m, float q, float thrust) => BestTurningAoA(Math.Min(m.maxAoA, TorqueAoALimit(m, q)), q, m.liftArea * m.liftMultiplier, thrust);

        /// <summary>Turning acceleration (g) at the best angle of attack within the missile's limits, at a speed and air density.</summary>
        public static float TurningG(in MissileSpec m, float speed, float density, float gravity, float thrust = 0f)
        {
            float q = 0.5f * density * speed * speed;
            float liftSk = m.liftArea * m.liftMultiplier;
            float aoa = BestTurningAoA(Math.Min(m.maxAoA, TorqueAoALimit(m, q)), q, liftSk, thrust);
            return (q * liftSk * Lift(aoa) + thrust * (float)Math.Sin(aoa * Math.PI / 180.0)) / Math.Max(m.mass, 1e-4f) / gravity;
        }

        /// <summary>MissileLauncher.GetKinematicSpeed: below this, after the motor stops, the missile gives up.</summary>
        public static float KinematicSpeed(in MissileSpec m, float density, float gravity, float speedOfSound)
        {
            float denominator = 0.5f * density * m.liftArea * m.liftMultiplier * Lift(m.maxAoA);
            if (denominator <= 0) return 0.5f * speedOfSound;
            return Math.Min((float)Math.Sqrt(4f * m.mass * gravity / denominator), 0.5f * speedOfSound);
        }

        /// <summary>
        /// Fly a shot. Positions are relative to the launch point in the local frame; the target keeps its velocity plus a constant
        /// acceleration (capped to its maximum speed) for the whole flight. density(altitude) gives the air density at a height above the
        /// launch point's sea level reference, launchAltitude is the launch point's own altitude. elapsed is the time since launch for a
        /// missile already flying (its motor phases follow it); maxTime and the result's time count from now. With evadeG the target also
        /// turns level at that many g once the missile is within evadeTimeToGo seconds of it, reversing every evadePeriod seconds (0: one
        /// way; negative g: the other way first).
        /// </summary>
        public static Result Fly(in MissileSpec m, double vx, double vy, double vz,
            double tx, double ty, double tz, double tvx, double tvy, double tvz, double tax, double tay, double taz, double targetMaxSpeed,
            double launchAltitude, Func<double, float> density, float gravity, float speedOfSound, float dt = 0.1f, float maxTime = 120f, float navigation = 3f, float elapsed = 0f,
            float evadeG = 0f, float evadeTimeToGo = 0f, float evadePeriod = 0f)
        {
            double px = 0, py = 0, pz = 0;
            const double deg = Math.PI / 180.0;
            var result = new Result { missDistance = float.MaxValue };
            float t = 0f;
            float liftSk = m.liftArea * m.liftMultiplier, dragSk = m.dragArea * m.dragMultiplier;
            float mass = Math.Max(m.mass, 1e-4f);
            double lagGain = m.responseTime > 0 ? Math.Min(1.0, dt / m.responseTime) : 1.0;
            double lx0 = 0, ly0 = 0, lz0 = 0; // Turning acceleration being flown (m/s²), following the command with the response lag.
            var torqueLimit = torqueLimits ?? (torqueLimits = new TorqueLimitTable());
            torqueLimit.Build(m);
            bool managedMotor = m.motor != MotorMode.Sustain, lit = m.sustainLit;
            float evadeStart = -1f;
            float sustainUsed = m.sustainUsed;
            bool law = m.law > GuidanceLaw.ProNav;
            var state = new GuidanceLaws.State { lag = m.responseTime > 0 ? m.responseTime : 0.05f, targetMax = m.targetMax > 0 ? m.targetMax : 80f, navigation = navigation };
            while (t < maxTime)
            {
                double rx = tx - px, ry = ty - py, rz = tz - pz;
                double r2 = rx * rx + ry * ry + rz * rz, r = Math.Sqrt(r2);
                double rvx = tvx - vx, rvy = tvy - vy, rvz = tvz - vz;
                double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                double closing = -(rx * rvx + ry * rvy + rz * rvz) / Math.Max(r, 1e-6);
                double rv2 = rvx * rvx + rvy * rvy + rvz * rvz;
                double s = rv2 > 0 ? Math.Min(Math.Max(-(rx * rvx + ry * rvy + rz * rvz) / rv2, 0), dt) : 0; // Closest approach within this step.
                double cx = rx + rvx * s, cy = ry + rvy * s, cz = rz + rvz * s;
                double cpa = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                float rho = density(launchAltitude + pz);
                float q = 0.5f * rho * (float)(speed * speed);
                float thrust;
                bool burntOut;
                if (!managedMotor) { thrust = ThrustAt(m, elapsed + t); burntOut = thrust == 0f && elapsed + t > m.dropTime + m.boostTime; }
                else
                {
                    float sinceDrop = elapsed + t - m.dropTime;
                    if (sinceDrop < 0f) thrust = 0f;
                    else if (sinceDrop < m.boostTime) thrust = m.thrust;
                    else
                    {
                        float throttle = GuidanceLaws.SustainThrottle(m.motor, m.minThrottle, m.cruiseTime - sustainUsed, closing > 1 ? r / closing : -1, speed,
                            Math.Sqrt(tvx * tvx + tvy * tvy + tvz * tvz), ref lit);
                        thrust = throttle * m.cruiseThrust;
                        sustainUsed += throttle * dt;
                    }
                    burntOut = sinceDrop >= m.boostTime && sustainUsed >= m.cruiseTime;
                }
                float aoaCap = q > 1f ? BestTurningAoA(Math.Min(m.maxAoA, torqueLimit.Limit(q)), q, liftSk, thrust) : 0f;
                if (cpa < result.missDistance)
                {
                    result.missDistance = (float)cpa;
                    result.time = t + (float)s;
                    result.speed = (float)speed;
                    result.closing = (float)closing;
                    result.lateralG = (q * liftSk * Lift(aoaCap) + thrust * (float)Math.Sin(aoaCap * deg)) / mass / gravity;
                }
                if (result.missDistance <= 0.5f * m.blastRadius) break;
                if (t > 3f && closing < 0 && r > result.missDistance + 50) break; // Past the target.
                if (launchAltitude + pz < 0) break; // Into the ground/sea.
                if (burntOut && speed < KinematicSpeed(m, rho, gravity, speedOfSound)) { result.gaveUp = true; break; }

                double ux = vx / speed, uy = vy / speed, uz = vz / speed;
                double ax = 0, ay = 0, az = 0;
                float Lateral(float a) => (q * liftSk * Lift(a) + thrust * (float)Math.Sin(a * deg)) / mass;
                double reach = q > 1f ? Lateral(aoaCap) : 0.0;
                if (law && elapsed + t >= m.dropTime && r > 1 && (m.lawRange <= 0f || r < m.lawRange))
                {
                    state.rx = rx; state.ry = ry; state.rz = rz; state.vx = rvx; state.vy = rvy; state.vz = rvz;
                    state.tax = tax; state.tay = tay; state.taz = taz; state.max = lx0; state.may = ly0; state.maz = lz0;
                    state.ux = ux; state.uy = uy; state.uz = uz; state.missileMax = (float)reach;
                    if (!GuidanceLaws.Command(m.law, state, out ax, out ay, out az, out _)) { ax = ay = 0; az = 0; }
                    az += gravity;
                    double along0 = ax * ux + ay * uy + az * uz;
                    ax -= along0 * ux; ay -= along0 * uy; az -= along0 * uz;
                }
                else if (elapsed + t >= m.dropTime && r > 1)
                {
                    // Proportional navigation: navigation × closing speed × line-of-sight rate, across the velocity, plus gravity compensation.
                    double wx = (ry * rvz - rz * rvy) / r2, wy = (rz * rvx - rx * rvz) / r2, wz = (rx * rvy - ry * rvx) / r2;
                    double lx = rx / r, ly = ry / r, lz = rz / r;
                    ax = navigation * closing * (wy * lz - wz * ly);
                    ay = navigation * closing * (wz * lx - wx * lz);
                    az = navigation * closing * (wx * ly - wy * lx) + gravity;
                    double along0 = ax * ux + ay * uy + az * uz;
                    ax -= along0 * ux; ay -= along0 * uy; az -= along0 * uz;
                }
                double command = Math.Sqrt(ax * ax + ay * ay + az * az);
                // The command within what the missile can pull, then the response lag.
                if (command > reach && command > 1e-6) { double k = reach / command; ax *= k; ay *= k; az *= k; }
                lx0 += (ax - lx0) * lagGain; ly0 += (ay - ly0) * lagGain; lz0 += (az - lz0) * lagGain;
                double alongLag = lx0 * ux + ly0 * uy + lz0 * uz; // Keep it across the (turning) flight path.
                lx0 -= alongLag * ux; ly0 -= alongLag * uy; lz0 -= alongLag * uz;
                double lateral = Math.Sqrt(lx0 * lx0 + ly0 * ly0 + lz0 * lz0);
                float aoa = 0f;
                if (lateral > 1e-3 && q > 1f)
                {
                    if (Lateral(aoaCap) <= lateral) aoa = aoaCap;
                    else
                    {
                        float lo = 0f, hi = aoaCap;
                        for (int i = 0; i < 12; ++i) { float mid = 0.5f * (lo + hi); if (Lateral(mid) < lateral) lo = mid; else hi = mid; }
                        aoa = hi;
                    }
                }
                double along = (thrust * Math.Cos(aoa * deg) - q * dragSk * Drag(aoa)) / mass;
                vx += (along * ux + lx0) * dt;
                vy += (along * uy + ly0) * dt;
                vz += (along * uz + lz0 - gravity) * dt;
                px += vx * dt; py += vy * dt; pz += vz * dt;
                tvx += tax * dt; tvy += tay * dt; tvz += taz * dt;
                if (evadeG != 0f && closing > 1 && r / closing < evadeTimeToGo)
                {
                    if (evadeStart < 0f) evadeStart = t;
                    double level = Math.Sqrt(tvx * tvx + tvy * tvy);
                    if (level > 1)
                    {
                        double side = evadePeriod > 0f && ((int)((t - evadeStart) / evadePeriod) & 1) == 1 ? -1 : 1;
                        double turn = side * evadeG * gravity * dt / level, cos = Math.Cos(turn), sin = Math.Sin(turn);
                        double nx = tvx * cos - tvy * sin, ny = tvx * sin + tvy * cos;
                        tax = (nx - tvx) / dt; tay = (ny - tvy) / dt; taz = 0; // What a seeker measuring the target would see.
                        tvx = nx; tvy = ny;
                    }
                }
                double targetSpeed = Math.Sqrt(tvx * tvx + tvy * tvy + tvz * tvz);
                if (targetSpeed > targetMaxSpeed && targetSpeed > 0) { double k = targetMaxSpeed / targetSpeed; tvx *= k; tvy *= k; tvz *= k; }
                tx += tvx * dt; ty += tvy * dt; tz += tvz * dt;
                t += dt;
            }
            result.reached = result.missDistance <= m.blastRadius;
            return result;
        }
    }
}
