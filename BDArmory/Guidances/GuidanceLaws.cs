using System;

namespace BDArmory.Guidances
{
    /// <summary>Homing law a missile flies once it is guiding on its target (MissileBase.guidanceLaw).</summary>
    public enum GuidanceLaw
    {
        Config = 0,   // The missile config's own homingType.
        ProNav,       // Proportional navigation.
        Optimal,      // Optimal guidance: zero-effort miss with the target's turn and the missile's own steering lag.
        Game,         // Differential game (DGL/1): assumes the target turns as hard as it can the worst way.
        SlidingMode   // Sliding mode: proportional navigation plus a switching term sized to the target's turn, without measuring it.
    }

    /// <summary>How a missile spends its sustainer (MissileBase.motorMode).</summary>
    public enum MotorMode
    {
        Sustain = 0,  // Burns straight after the booster (stock).
        Pulse,        // A second pulse, held until it burns out about at the intercept (or the missile gets slow).
        Throttle      // Throttled (ducted rocket): the same total impulse, spread over the rest of the flight.
    }

    /// <summary>
    /// Homing laws and motor management as plain arithmetic, shared by the missiles (MissileLauncher), the flight replica the pilots
    /// plan with (MissileFlyout) and the bench. Vectors are in any one inertial frame, metres and seconds.
    /// </summary>
    public static class GuidanceLaws
    {
        public struct State
        {
            public double rx, ry, rz;      // Target position less missile position.
            public double vx, vy, vz;      // Target velocity less missile velocity.
            public double tax, tay, taz;   // Target acceleration as measured (Optimal only).
            public double max, may, maz;   // Turning acceleration the missile is flying now (its steering lag's state).
            public double ux, uy, uz;      // Missile velocity direction.
            public float lag;              // s: time constant of the missile's turn following its command.
            public float missileMax;       // m/s²: turning acceleration the missile can pull now.
            public float targetMax;        // m/s²: turning acceleration the target is assumed capable of.
            public float navigation;       // Proportional navigation gain.
        }

        public const float SlidingBoundary = 0.01f; // rad/s of line-of-sight rate over which the switching term comes in (no chatter inside).
        public const float GameZoneShare = 0.5f;    // The share of the capture zone inside which the game law eases off from full turn.

        /// <summary>Time to the closest approach on the present velocities, or -1 when opening.</summary>
        public static double TimeToGo(in State s)
        {
            double v2 = s.vx * s.vx + s.vy * s.vy + s.vz * s.vz, d = -(s.rx * s.vx + s.ry * s.vy + s.rz * s.vz);
            return v2 > 1e-6 && d > 0 ? d / v2 : -1;
        }

        /// <summary>Optimal guidance gain against the time to go in units of the steering lag: 3 far out, rising as the lag starts to matter.</summary>
        public static double OptimalGain(double x)
        {
            if (x > 50) return 3;
            double e = Math.Exp(-x);
            double denominator = 2 * x * x * x + 3 + 6 * x - 6 * x * x - 12 * x * e - 3 * e * e;
            if (denominator < 1e-9) return 15;
            return Math.Min(Math.Max(6 * x * x * (e - 1 + x) / denominator, 3), 15);
        }

        /// <summary>
        /// The turning acceleration a law commands (across the missile's velocity, within what it can pull). False when the target is
        /// opening and the law has nothing to say (fly pursuit).
        /// </summary>
        public static bool Command(GuidanceLaw law, in State s, out double ax, out double ay, out double az, out double timeToGo)
        {
            ax = ay = az = 0;
            timeToGo = TimeToGo(s);
            if (timeToGo <= 0) return false;
            double t = Math.Max(timeToGo, 0.05), t2 = t * t;
            // Zero-effort miss: where the target ends up relative to us if neither turns any more.
            double zx = s.rx + s.vx * t, zy = s.ry + s.vy * t, zz = s.rz + s.vz * t;
            double lag = Math.Max(s.lag, 1e-3), x = t / lag;
            double e = Math.Exp(-x), lagMiss = lag * lag * (e + x - 1); // What the turn we're already flying still takes off the miss.
            double n = s.navigation > 0 ? s.navigation : 3;
            switch (law)
            {
                case GuidanceLaw.Optimal:
                    {
                        double r = Math.Sqrt(s.rx * s.rx + s.ry * s.ry + s.rz * s.rz);
                        double lx = s.rx / r, ly = s.ry / r, lz = s.rz / r;
                        double along = s.tax * lx + s.tay * ly + s.taz * lz; // The target's turn across the line of sight.
                        zx += 0.5 * (s.tax - along * lx) * t2 - s.max * lagMiss;
                        zy += 0.5 * (s.tay - along * ly) * t2 - s.may * lagMiss;
                        zz += 0.5 * (s.taz - along * lz) * t2 - s.maz * lagMiss;
                        double gain = OptimalGain(x) / t2;
                        ax = gain * zx; ay = gain * zy; az = gain * zz;
                        break;
                    }
                case GuidanceLaw.Game:
                    {
                        zx -= s.max * lagMiss; zy -= s.may * lagMiss; zz -= s.maz * lagMiss;
                        double miss = Math.Sqrt(zx * zx + zy * zy + zz * zz);
                        if (miss < 1e-6) break;
                        // The miss we can still take out less the miss the target can still put in: inside it the intercept is assured
                        // whatever the target does, so ease off in proportion; outside it, turn as hard as we can.
                        double targetMax = Math.Min(s.targetMax, 0.6 * s.missileMax);
                        double zone = s.missileMax * lag * lag * (0.5 * x * x - x + 1 - e) - 0.5 * targetMax * t2;
                        double share = zone > 1 ? Math.Min(miss / (GameZoneShare * zone), 1) : Math.Min(miss, 1);
                        double k = s.missileMax * share / miss;
                        ax = k * zx; ay = k * zy; az = k * zz;
                        break;
                    }
                case GuidanceLaw.SlidingMode:
                    {
                        double miss = Math.Sqrt(zx * zx + zy * zy + zz * zz);
                        if (miss < 1e-6) break;
                        double r = Math.Sqrt(s.rx * s.rx + s.ry * s.ry + s.rz * s.rz);
                        double losRate = miss / (Math.Max(r, 1) * t);
                        double k = n / t2 + s.targetMax * Math.Min(losRate / SlidingBoundary, 1) / miss;
                        ax = k * zx; ay = k * zy; az = k * zz;
                        break;
                    }
                default:
                    ax = n * zx / t2; ay = n * zy / t2; az = n * zz / t2;
                    break;
            }
            double alongVelocity = ax * s.ux + ay * s.uy + az * s.uz;
            ax -= alongVelocity * s.ux; ay -= alongVelocity * s.uy; az -= alongVelocity * s.uz;
            double a = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (a > s.missileMax && a > 1e-9) { double k = s.missileMax / a; ax *= k; ay *= k; az *= k; }
            return true;
        }

        public const float PulseLead = 1.1f;      // A held pulse is lit when the time to go is within this many burn times.
        public const float SlowSpeedFactor = 1.5f; // ... or when the missile is slower than this many times the target's speed,
        public const float SlowSpeedFloor = 450f;  // or than this (m/s).

        /// <summary>
        /// Sustainer throttle (0—1) for this tick. sustainLeft is the burn left at full thrust (s); timeToGo &lt; 0 means unknown (burn).
        /// lit says whether the sustainer has been started, and is set when this starts it.
        /// </summary>
        public static float SustainThrottle(MotorMode mode, float minThrottle, float sustainLeft, double timeToGo, double speed, double targetSpeed, ref bool lit)
        {
            if (sustainLeft <= 0f) return 0f;
            bool slow = speed < Math.Max(SlowSpeedFloor, SlowSpeedFactor * targetSpeed);
            switch (mode)
            {
                case MotorMode.Pulse:
                    if (!lit && (timeToGo < 0 || timeToGo <= PulseLead * sustainLeft || slow)) lit = true;
                    return lit ? 1f : 0f;
                case MotorMode.Throttle:
                    lit = true;
                    if (timeToGo < 0 || slow) return 1f;
                    return (float)Math.Min(Math.Max(sustainLeft / Math.Max(timeToGo, 0.1), Math.Max(minThrottle, 0.05f)), 1.0);
                default:
                    lit = true;
                    return 1f;
            }
        }
    }
}
