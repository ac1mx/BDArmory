using System;

namespace BDArmory.Control
{
    /// <summary>
    /// Rate controller for one rotation axis that identifies the craft's response online and normalises its gains by it.
    ///
    /// Model (all rates in rad/s in the stick-positive sense, q in kPa, v in m/s):
    ///     dr/dt = B * u + q * m * (b * delta - a * r / v - c * x + d)
    /// where u is the stick input (lagged by the thrust vectoring response), delta is the control surface deflection in stick units, m is the lift Mach factor,
    /// x is the aerodynamic angle that gives the axis its static stability (sin AoA for pitch, sin sideslip for yaw, 0 for roll)
    /// and B is the angular acceleration available from sources that don't depend on the airflow (vectored thrust, reaction wheels),
    /// which is known from the craft rather than identified.
    ///
    /// This class has no Unity dependencies so that it can be exercised outside the game.
    /// </summary>
    public sealed class AxisRateController
    {
        #region Tuning
        /// <summary>Closed-loop bandwidth of the angle loop (rad/s).</summary>
        public float bandwidth = 5f;
        /// <summary>Damping ratio of the angle loop.</summary>
        public float dampingRatio = 0.9f;
        /// <summary>Fraction of the identified control authority assumed to be available for braking the rotation.</summary>
        public float brakingAuthority = 0.6f;
        /// <summary>Fraction of the identified static stability moment that is cancelled by feedback.</summary>
        public float stabilityCancellation = 0.7f;
        /// <summary>Integral gain on the rate error (stick units per rad).</summary>
        public float integralGain = 1.5f;
        /// <summary>Limit of the integral term (stick units).</summary>
        public float integralLimit = 0.5f;
        /// <summary>Time constant of the assumed control surface response (s), used when the surfaces' travel rate isn't known.</summary>
        public float actuatorTime = 0.1f;
        /// <summary>Travel rate of the control surfaces (stick units per second), 0 if unknown.</summary>
        public float actuatorRate = 0f;
        /// <summary>Response rate of the vectored thrust (per second, exponential approach), 0 if it follows the stick immediately.</summary>
        public float constResponseRate = 0f;
        #endregion

        #region Identified model
        const int N = 4;
        readonly double[] theta = new double[N]; // b, a, c, d
        readonly double[] P = new double[N * N];
        readonly double[] phi = new double[N];
        readonly double[] K = new double[N];
        readonly double[] phiF = new double[N];
        readonly double[] thetaRecent = new double[N]; // Snapshots of the model for judging it on data it hasn't been fitted to.
        readonly double[] thetaHeld = new double[N];
        double yF, accelF, lastRate, delta, input, errEMA, sigEMA, sigRef, confidence;
        bool initialised;
        int samples;

        const double forgetting = 0.9995; // About 40s of memory at 50Hz: with the Mach factor and thrust response modelled, the parameters are properties of the craft, not of the moment.
        const double filterTime = 0.08; // Low-pass applied identically to both sides of the regression.
        const double bMax = 50;
        const double dampingScale = 100; // Brings the damping regressor (rate / speed) to the same order as the others.
        const float minDynPres = 0.5f; // kPa
        const float weightDynPres = 5f; // kPa, below this the samples are progressively down-weighted.
        const float minSpeed = 20f; // m/s
        const double minAuthority = 0.05; // rad/s² at full stick, below this the axis is considered uncontrollable.
        const int snapshotSamples = 100;
        const double confidenceRiseRate = 0.25; // per second

        /// <summary>Identified aerodynamic control authority (rad/s² per kPa at full deflection).</summary>
        public float Authority => (float)theta[0];
        public float Damping => (float)(theta[1] * dampingScale);
        public float Stability => (float)theta[2];
        public float Bias => (float)theta[3];
        /// <summary>0—1: how much the identified model explains the observed angular acceleration.</summary>
        public float Confidence => (float)confidence;
        #endregion

        float integral;
        public float Integral => integral;
        public float RateCommand { get; private set; }

        public AxisRateController(float initialAuthority = 0.05f)
        {
            Reset(initialAuthority);
        }

        public void Reset(float initialAuthority = 0.05f)
        {
            Array.Clear(theta, 0, N);
            Array.Clear(thetaRecent, 0, N);
            Array.Clear(thetaHeld, 0, N);
            Array.Clear(P, 0, N * N);
            Array.Clear(phiF, 0, N);
            theta[0] = Math.Max(0, initialAuthority);
            for (int i = 0; i < N; ++i) P[i * N + i] = 10;
            yF = accelF = lastRate = delta = input = errEMA = sigEMA = sigRef = confidence = 0;
            initialised = false;
            samples = 0;
            integral = 0;
            leadTime = lastSwingRate = lastLeadError = swingTargetMotion = 0;
            leadErrorKnown = false;
        }

        /// <summary>
        /// Update the model with the response to the previously applied input. Call once per physics frame before Control.
        /// </summary>
        /// <param name="rate">Body rate about the axis (rad/s, stick-positive sense, not relative to any target).</param>
        /// <param name="x">Aerodynamic angle (sine) giving static stability on this axis.</param>
        /// <param name="machFactor">Lift multiplier for the current Mach number.</param>
        /// <param name="constAuthority">Angular acceleration at full stick from vectored thrust and reaction wheels (rad/s²).</param>
        public void Identify(float rate, float x, float dynPres, float machFactor, float speed, float constAuthority, float dt)
        {
            if (dt <= 0f) return;
            if (!initialised || dynPres < minDynPres || speed < minSpeed)
            {
                lastRate = rate;
                initialised = dynPres >= minDynPres && speed >= minSpeed;
                Array.Clear(phiF, 0, N);
                yF = accelF = 0;
                confidence = Math.Max(0, confidence - confidenceRiseRate * dt); // The model isn't being checked here.
                return;
            }
            // Regress the part of the angular acceleration that the airflow is responsible for, per unit dynamic pressure.
            double accel = (rate - lastRate) / dt;
            double aeroPres = dynPres * machFactor; // Every aerodynamic moment comes from lift, which falls with Mach number.
            double scale = 1.0 / Math.Max(aeroPres, weightDynPres);
            double y = (accel - constAuthority * input) * scale;
            double qs = aeroPres * scale;
            phi[0] = qs * delta;
            phi[1] = -qs * dampingScale * lastRate / speed;
            phi[2] = -qs * x;
            phi[3] = qs;
            lastRate = rate;

            double w = dt / (filterTime + dt);
            yF += w * (y - yF);
            accelF += w * (accel - accelF);
            for (int i = 0; i < N; ++i) phiF[i] += w * (phi[i] - phiF[i]);

            // Recursive least squares with forgetting.
            double denom = forgetting;
            for (int i = 0; i < N; ++i)
            {
                double s = 0;
                for (int j = 0; j < N; ++j) s += P[i * N + j] * phiF[j];
                K[i] = s;
                denom += phiF[i] * s;
            }
            double pred = 0;
            for (int i = 0; i < N; ++i) pred += theta[i] * phiF[i];
            double err = yF - pred;
            if (denom > 1e-12 && !double.IsNaN(err) && !double.IsInfinity(err))
            {
                for (int i = 0; i < N; ++i) theta[i] += K[i] / denom * err;
                for (int i = 0; i < N; ++i)
                    for (int j = 0; j < N; ++j)
                        P[i * N + j] = (P[i * N + j] - K[i] * K[j] / denom) / forgetting;
                // Keep the covariance bounded when there's no excitation.
                double trace = 0;
                for (int i = 0; i < N; ++i) trace += P[i * N + i];
                if (trace > 1e4)
                {
                    double shrink = 1e4 / trace;
                    for (int i = 0; i < N * N; ++i) P[i] *= shrink;
                }
            }
            theta[0] = Math.Min(bMax, Math.Max(0, theta[0]));
            theta[1] = Math.Max(0, theta[1]);

            // Fit quality: fraction of the craft's (filtered) angular acceleration explained by a copy of the model taken some seconds of
            // manoeuvring ago, so that a model that only fits by continually re-adapting scores badly.
            // Only frames where the craft is actually being manoeuvred say anything about the fit; steady flight is just noise.
            double signal = accelF * accelF;
            sigRef = Math.Max(sigRef * 0.9998, signal);
            if (signal > 0.02 * sigRef)
            {
                double heldPred = 0;
                for (int i = 0; i < N; ++i) heldPred += thetaHeld[i] * phiF[i];
                double heldErr = (yF - heldPred) / scale;
                const double emaRate = 0.01;
                errEMA += emaRate * (heldErr * heldErr - errEMA);
                sigEMA += emaRate * (signal - sigEMA);
                if (++samples % snapshotSamples == 0)
                {
                    Array.Copy(thetaRecent, thetaHeld, N);
                    Array.Copy(theta, thetaRecent, N);
                }
            }
            double fit = samples >= 3 * snapshotSamples && sigEMA > 1e-12 ? 1 - errEMA / sigEMA : 0;
            double target = Math.Max(0, Math.Min(1, (fit - 0.5) / 0.3));
            confidence = Math.Min(target, confidence + confidenceRiseRate * dt); // Trust is lost immediately and regained slowly.
        }

        #region Braking lead
        // Slow control surfaces and the craft's own rate response mean that a rotation keeps going for a while after the stick reverses.
        // A PD law that brakes too late for this overshoots, and with a saturated stick it then swings back and forth indefinitely.
        // The time by which braking has to be brought forward is learnt from the outcome of each swing: how far past (or short of) the
        // target the rotation stopped.
        double leadTime, lastSwingRate, lastLeadError, swingTargetMotion;
        bool leadErrorKnown;
        const double maxLeadTime = 1.5, leadGain = 0.1, leadReference = 20 * Math.PI / 180, leadDeadband = 5 * Math.PI / 180, shortfallWeight = 0.3, minSwingRate = 0.3, minCountedSwingRate = 1, maxSwingTargetMotion = 15 * Math.PI / 180;

        /// <summary>Learnt time by which braking is brought forward (s).</summary>
        public float LeadTime => (float)leadTime;

        /// <summary>
        /// Angle by which the error should be reduced so that braking starts early enough (rad, signed like the rate).
        /// </summary>
        /// <param name="rate">Rate about the axis relative to the target (rad/s).</param>
        /// <param name="inherentLead">Lead the control law already has (s), i.e., its damping gain over its proportional gain.</param>
        public float LeadAngle(float rate, float inherentLead)
        {
            double along = Math.Max(0, delta * Math.Sign(rate)); // Deflection still driving the rotation, which has to come off first.
            double travelTime = actuatorRate > 0f ? 0.5 * along / actuatorRate : 0;
            return (float)(rate * Math.Max(0, travelTime + leadTime - inherentLead));
        }

        /// <summary>
        /// Learn from where the rotation stops. Call once per physics frame with the error to the target.
        /// </summary>
        /// <param name="error">Angle to the target about the axis (rad, positive = positive stick).</param>
        /// <param name="rate">Rate about the axis relative to the target (rad/s).</param>
        /// <param name="dt">Physics time step (s).</param>
        public void LearnLead(float error, float rate, float dt)
        {
            // Change in the error that the craft's own rotation doesn't account for is the target moving: a swing during which the
            // target moved says nothing about how well the rotation was braked.
            if (leadErrorKnown)
            {
                double change = error - lastLeadError;
                change -= 2 * Math.PI * Math.Round(change / (2 * Math.PI));
                swingTargetMotion += Math.Abs(change + rate * dt);
            }
            lastLeadError = error;
            leadErrorKnown = true;
            if (Math.Abs(rate) >= minSwingRate)
            {
                if (lastSwingRate == 0 || Math.Sign(rate) != Math.Sign(lastSwingRate) || Math.Abs(rate) > Math.Abs(lastSwingRate)) lastSwingRate = rate;
                return;
            }
            double targetMotion = swingTargetMotion;
            swingTargetMotion = 0;
            if (lastSwingRate == 0) return;
            // The rotation has stopped: positive overshoot means the target is now behind it.
            double overshoot = -error * Math.Sign(lastSwingRate);
            bool countedSwing = Math.Abs(lastSwingRate) >= minCountedSwingRate;
            lastSwingRate = 0;
            if (!countedSwing || targetMotion > maxSwingTargetMotion || Math.Abs(overshoot) < leadDeadband) return;
            double adjustment = overshoot > 0 ? Math.Min(1, overshoot / leadReference) : shortfallWeight * Math.Max(-1, overshoot / leadReference);
            leadTime = Math.Max(0, Math.Min(maxLeadTime, leadTime + leadGain * adjustment));
        }
        #endregion

        /// <summary>
        /// Compute the stick input for the axis.
        /// </summary>
        /// <param name="error">Angle to turn through about the axis (rad, positive = positive stick).</param>
        /// <param name="rate">Body rate about the axis relative to the target direction's rate (rad/s).</param>
        /// <param name="bodyRate">Body rate about the axis (rad/s).</param>
        /// <param name="x">Aerodynamic angle (sine) giving static stability on this axis.</param>
        /// <param name="limit">Maximum magnitude of the stick input.</param>
        /// <returns>False if the model can't be used in the current conditions (input is then 0).</returns>
        public bool Control(float error, float rate, float bodyRate, float x, float dynPres, float machFactor, float speed, float constAuthority, float limit, float dt, out float stick)
        {
            stick = 0f;
            double aeroPres = dynPres * machFactor;
            double b = constAuthority + theta[0] * aeroPres; // rad/s² per unit stick
            if (speed < minSpeed || b * limit < minAuthority) { integral = 0; return false; }
            double braking = Math.Max(1e-3, b * limit * brakingAuthority);
            double k = bandwidth / (2 * dampingRatio);
            double absErr = Math.Abs(error);
            double linearRange = braking / (k * k);
            double rateCmd = absErr <= linearRange ? k * error : Math.Sign(error) * Math.Sqrt(2 * braking * (absErr - 0.5 * linearRange));
            RateCommand = (float)rateCmd;

            double rateError = rateCmd - rate;
            double accelCmd = 2 * dampingRatio * bandwidth * rateError;
            double targetBodyRate = bodyRate + rateError; // The body rate we're asking for.
            double u = (accelCmd + aeroPres * (theta[1] * dampingScale * targetBodyRate / speed + stabilityCancellation * theta[2] * x - theta[3])) / b;

            double total = u + integral;
            bool saturatedHigh = total > limit, saturatedLow = total < -limit;
            if (!(saturatedHigh && rateError > 0) && !(saturatedLow && rateError < 0))
                integral = (float)Math.Max(-integralLimit, Math.Min(integralLimit, integral + integralGain * rateError * dt));
            total = Math.Max(-limit, Math.Min(limit, u + integral));
            if (double.IsNaN(total)) { integral = 0; return false; }
            stick = (float)total;
            return true;
        }

        /// <summary>
        /// Record the input that was actually sent to the craft this frame (after any later limiting).
        /// </summary>
        public void Applied(float input, float dt)
        {
            if (actuatorRate > 0f)
                delta += Math.Max(-actuatorRate * dt, Math.Min(actuatorRate * dt, input - delta));
            else
            {
                double w = dt / (Math.Max(1e-3f, actuatorTime) + dt);
                delta += w * (input - delta);
            }
            this.input = constResponseRate > 0f ? this.input + Math.Min(1.0, constResponseRate * dt) * (input - this.input) : input;
        }
    }

    /// <summary>
    /// Keeps one axis' control loop from hunting. A stick that swings back and forth faster than about 2 Hz drives the airframe beyond the
    /// bandwidth it can follow: with the lag of the surfaces, thrust vectoring and structure that is a self-sustained oscillation of the loop,
    /// not tracking. The loop gain at which that starts depends on the flight condition (control power, damping and lag all change with
    /// dynamic pressure, and differently on craft with thrust vectoring), so the gain limit is learned per dynamic-pressure band: the
    /// controller's whole output is scaled down in the band where the stick hunts and raised back slowly while it stays calm there.
    /// </summary>
    public sealed class LoopGainGovernor
    {
        static readonly float[] knots = { 5f, 15f, 27.5f, 42.5f, 60f, 82.5f, 112.5f, 150f }; // kPa, centres of the dynamic-pressure bands.
        readonly float[] gain = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        const float hysteresis = 0.03f; // Stick travel back from an extreme that counts as a reversal.
        const float minSwing = 0.08f; // Peak-to-peak stick swing that counts as part of an oscillation.
        const float memory = 2f; // s, time constant of the reversal rate.
        const float huntingRate = 4f, calmRate = 2f; // Reversals per second: 4/s is a 2 Hz oscillation.
        const float cutRate = 1.5f, recoverRate = 0.02f; // Per second: how fast a band's gain falls while hunting and rises again while calm.
        const float minGain = 0.15f;
        // The loop can also flip the stick every physics tick, with too small a swing for the reversal count: the F-15Z2 at q 180 kPa
        // alternates ±0.03 at 25 Hz (its damping gain times its control power is more than one 0.02 s tick can follow). Tick-to-tick
        // reversals of the stick's change: 12/s flags 70–80% of its flight above 120 kPa and 2% below 30 kPa.
        const float flipFloor = 0.02f, flipMemory = 0.5f, flipHuntingRate = 12f, flipCalmRate = 6f;
        float extreme, previousExtreme, lastStick, lastStep;
        int direction;
        bool started;

        /// <summary>Stick reversals per second over the last couple of seconds.</summary>
        public float ReversalRate { get; private set; }
        /// <summary>Tick-to-tick stick flips per second over the last half second.</summary>
        public float FlipRate { get; private set; }
        public float LowestGain { get { float g = 1f; for (int i = 0; i < gain.Length; ++i) g = Math.Min(g, gain[i]); return g; } }

        /// <summary>Gain to apply to the axis' controller output at this dynamic pressure (kPa).</summary>
        public float Gain(float dynPres)
        {
            Locate(dynPres, out int i, out float w);
            return gain[i] + w * (gain[i + 1] - gain[i]);
        }

        static void Locate(float dynPres, out int i, out float w)
        {
            if (!(dynPres > knots[0])) { i = 0; w = 0f; return; } // Also catches NaN.
            for (i = 0; i < knots.Length - 2 && dynPres > knots[i + 1]; ++i) { }
            w = Math.Min(1f, (dynPres - knots[i]) / (knots[i + 1] - knots[i]));
        }

        /// <summary>Feed the stick actually applied this tick.</summary>
        public void Observe(float stick, float dynPres, float dt)
        {
            if (!started) { extreme = previousExtreme = lastStick = stick; started = true; }
            ReversalRate *= (float)Math.Exp(-dt / memory);
            FlipRate *= (float)Math.Exp(-dt / flipMemory);
            float step = stick - lastStick;
            lastStick = stick;
            if (Math.Abs(step) > flipFloor)
            {
                if (step * lastStep < 0f) FlipRate += 1f / flipMemory;
                lastStep = step;
            }
            if (direction == 0) // Not moving yet.
            {
                if (Math.Abs(stick - extreme) > hysteresis) { direction = stick > extreme ? 1 : -1; previousExtreme = extreme; extreme = stick; }
            }
            else if (direction > 0 ? stick > extreme : stick < extreme) extreme = stick; // Still moving the same way.
            else if (Math.Abs(stick - extreme) > hysteresis) // Turned back.
            {
                if (Math.Abs(extreme - previousExtreme) >= minSwing) ReversalRate += 1f / memory;
                direction = -direction;
                previousExtreme = extreme;
                extreme = stick;
            }

            float factor, excess = Math.Max(ReversalRate / huntingRate, FlipRate / flipHuntingRate) - 1f;
            if (excess > 0f) factor = (float)Math.Exp(-cutRate * Math.Min(excess, 2f) * dt);
            else if (ReversalRate < calmRate && FlipRate < flipCalmRate) factor = (float)Math.Exp(recoverRate * dt);
            else return;
            Locate(dynPres, out int i, out float w);
            gain[i] = Math.Max(minGain, Math.Min(1f, gain[i] * (float)Math.Pow(factor, 1f - w)));
            gain[i + 1] = Math.Max(minGain, Math.Min(1f, gain[i + 1] * (float)Math.Pow(factor, w)));
        }
    }

    /// <summary>
    /// Three-axis attitude controller built from identified per-axis models.
    /// </summary>
    public sealed class PilotControlCore
    {
        public readonly AxisRateController pitch = new AxisRateController { bandwidth = 5f };
        public readonly AxisRateController yaw = new AxisRateController { bandwidth = 4f };
        public readonly AxisRateController roll = new AxisRateController { bandwidth = 7f, integralGain = 0.5f };
        // The craft's learned gain limits: kept across resets of the identified models.
        public readonly LoopGainGovernor pitchGovernor = new LoopGainGovernor(), yawGovernor = new LoopGainGovernor(), rollGovernor = new LoopGainGovernor();

        /// <summary>Lowest per-axis model confidence, i.e., how far the model-based output can be trusted.</summary>
        public float Confidence => Math.Min(pitch.Confidence, Math.Min(yaw.Confidence, roll.Confidence));

        public void Reset()
        {
            pitch.Reset();
            yaw.Reset();
            roll.Reset();
        }

        public void Identify(float pitchRate, float yawRate, float rollRate, float sinAoA, float sinSideslip, float dynPres, float machFactor, float speed,
            float pitchConstAuthority, float yawConstAuthority, float rollConstAuthority, float dt)
        {
            pitch.Identify(pitchRate, sinAoA, dynPres, machFactor, speed, pitchConstAuthority, dt);
            yaw.Identify(yawRate, sinSideslip, dynPres, machFactor, speed, yawConstAuthority, dt);
            roll.Identify(rollRate, 0f, dynPres, machFactor, speed, rollConstAuthority, dt);
        }

        public void Applied(float pitchInput, float yawInput, float rollInput, float dt)
        {
            pitch.Applied(pitchInput, dt);
            yaw.Applied(yawInput, dt);
            roll.Applied(rollInput, dt);
        }
    }
}
