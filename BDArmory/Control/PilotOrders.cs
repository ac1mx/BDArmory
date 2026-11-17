using System.Collections.Generic;
using UnityEngine;

using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Flight orders, given by a scenario (Testing/Scenario) and flown whenever the pilot has nothing to fight; without orders nothing
    /// here runs. A lead flies its route (two points make a racetrack, a CAP) at cruise speed; a wingman holds a slot on its lead (combat
    /// spread) and takes over the route if the lead is lost. Beyond that:
    ///  - cues: an estimated position of a hostile from outside the craft's own sensors, either a surveillance radar's track passed by
    ///    datalink (SurveillanceRadar, GCI) or our own passive fix on a hostile radar (below). A cue inside commitRange commits the pilot:
    ///    it flies at the cue (offset by flankAngle for a flanker) at commit speed until its own sensors give it a target;
    ///  - passive ranging: the warning receiver measures an emitter's bearing well but its range poorly. Bearings taken from points
    ///    spread across the line of sight fix the range: with baseline B (our own track across the line of sight over the last minute,
    ///    and the wingman's spread when it hears the same emitter) the range error is about R²σ/B. While the fix is poor the lead flies a
    ///    ranging leg (offset from the bearing) and the wingman opens the spread, both of which grow B;
    ///  - emission control: radars off until a cue or target is inside radarOnRange, we're spiked or a missile is coming; no
    ///    afterburner while flying orders (commit included);
    ///  - winchester: a fighter with nothing left to shoot recovers home;
    ///  - high-value asset: a craft with egressRange (an AWACS) runs from any hostile known to its side inside that range; a hunter
    ///    (huntHighValue, a missile truck) takes the other side's asset as its cue and target ahead of fighters (SectionTargetBonus).
    /// Each change of state is logged as an "Orders" line.
    /// </summary>
    public partial class BDModulePilotAI
    {
        public class FlightOrders
        {
            public string flight = "";
            public BDModulePilotAI lead;            // Null: fly the route; else hold the slot on this lead.
            public float slotRight = 3000f, slotBack = 500f, slotUp = 0f; // Slot on the lead (m): right of its track, behind, above.
            public float rangingSpread = 12000f;    // Slot spread while the section ranges an emitter passively.
            public readonly List<Vector3d> route = new List<Vector3d>(); // Geo (lat, lon, altitude ASL), flown in a loop.
            public float cruiseSpeed = 250f, commitSpeed = 350f;
            public Vector3d home;                   // Geo point to recover to.
            public bool haveHome;
            public bool emissionControl;
            public float radarOnRange = 100000f;
            public bool passiveRanging;
            public float commitRange = 150000f;
            public float flankAngle;                // Commit offset from the cue's bearing (deg, positive right).
            public float egressRange;               // High-value asset: run from hostiles inside this range (0: no).
            public bool huntHighValue;              // Prefer the other side's high-value assets (an AWACS) for cues and targets.
        }

        /// <summary>A high-value airborne asset: a surveillance radar carrier, or a craft whose orders run from hostiles (an AWACS).</summary>
        public static bool IsHighValueAsset(Vessel v)
        {
            if (v == null || !v.loaded) return false;
            if (v.gameObject.GetComponent<SurveillanceRadar>() != null) return true;
            var controller = v.ActiveController();
            return controller != null && controller.AI is BDModulePilotAI pilot && pilot.orders != null && pilot.orders.egressRange > 0f;
        }

        public FlightOrders orders;

        /// <summary>An estimated position of a hostile from outside our own sensors.</summary>
        public class Cue
        {
            public Vessel vessel;       // The craft it is (for the log only: the pilot flies to the estimate).
            public Vector3d geo;        // Estimated position.
            public float error;         // Estimated position error (m).
            public float time = -100f;
            public string source;
        }
        readonly Cue externalCue = new Cue(), passiveCue = new Cue();
        const float cueMemory = 90f, ordersInterval = 1f, bearingSigma = 1.5f * Mathf.Deg2Rad, trackHistory = 60f;
        float ordersTime = -10f;
        bool emissionsLifted, committed, egressing;
        int routeIndex;
        string ordersState = "";
        readonly Queue<(float time, Vector3d geo)> track = new Queue<(float, Vector3d)>();
        readonly Dictionary<Vessel, float> passiveFixLogged = new Dictionary<Vessel, float>();
        public bool OrdersCommitted => committed;
        /// <summary>Emission control holds: the weapon manager must not switch a radar on.</summary>
        public bool EmissionsHeld => orders != null && orders.emissionControl && !emissionsLifted;
        public bool OrdersRecovering => ordersState == "Recovering";

        /// <summary>A surveillance radar's (or another off-board sensor's) estimate of a hostile, passed by datalink.</summary>
        public void ReceiveCue(Vessel target, Vector3 estimatedPosition, float error, string source)
        {
            if (orders == null || target == null) return;
            var body = vessel.mainBody;
            Vector3d geo = VectorUtils.WorldPositionToGeoCoords(estimatedPosition, body);
            // Keep the nearer of two cues unless the one we hold is stale; a hunter keeps a high-value asset's over any other.
            if (Time.time - externalCue.time < cueMemory && externalCue.vessel != target && externalCue.vessel != null)
            {
                bool heldValue = orders.huntHighValue && IsHighValueAsset(externalCue.vessel), newValue = orders.huntHighValue && IsHighValueAsset(target);
                float held = (VectorUtils.GetWorldSurfacePostion(externalCue.geo, body) - vessel.CoM).sqrMagnitude;
                if (heldValue && !newValue) return;
                if (heldValue == newValue && (estimatedPosition - vessel.CoM).sqrMagnitude > held) return;
            }
            externalCue.vessel = target; externalCue.geo = geo; externalCue.error = error; externalCue.time = Time.time; externalCue.source = source;
        }

        void LogOrders(string message) => Debug.Log($"[BDArmory.BDModulePilotAI]: Orders {vessel.vesselName} ({orders.flight}): {message}");

        void SetOrdersState(string state, string detail = null)
        {
            if (state == ordersState) return;
            ordersState = state;
            LogOrders(detail != null ? $"{state} - {detail}" : state);
        }

        /// <summary>Once a second: passive fixes and emission control (called every tick).</summary>
        void UpdateOrders()
        {
            if (orders == null || vessel.LandedOrSplashed || Time.time - ordersTime < ordersInterval) return;
            ordersTime = Time.time;
            var weaponManager = WeaponManager;
            if (weaponManager == null) return;
            var body = vessel.mainBody;
            track.Enqueue((Time.time, VectorUtils.WorldPositionToGeoCoords(vessel.CoM, body)));
            while (track.Count > 0 && Time.time - track.Peek().time > trackHistory) track.Dequeue();

            if (orders.passiveRanging) UpdatePassiveFix(weaponManager);

            // Emission control.
            if (orders.emissionControl && !emissionsLifted)
            {
                string reason = null;
                var cue = BestCue(out float cueRange);
                if (cue != null && cueRange < orders.radarOnRange) reason = FormattableString($"{cue.source} cue on {cue.vessel?.vesselName} inside {orders.radarOnRange / 1000f:0} km (estimate {cueRange / 1000f:0} km, true {(cue.vessel != null ? (cue.vessel.CoM - vessel.CoM).magnitude / 1000f : -1f):0} km)");
                else if (targetVessel != null && (targetVessel.CoM - vessel.CoM).magnitude < orders.radarOnRange) reason = $"target {targetVessel.vesselName} at {(targetVessel.CoM - vessel.CoM).magnitude / 1000f:0} km";
                else if (weaponManager.rwr != null && weaponManager.rwr.LockCount() > 0) reason = "spiked";
                else if (weaponManager.missileIsIncoming) reason = "missile warning";
                if (reason != null)
                {
                    emissionsLifted = true;
                    LogOrders("radar on: " + reason);
                    foreach (var radar in weaponManager.radars)
                        if (radar != null && !radar.radarEnabled && radar.sonarMode == ModuleRadar.SonarModes.None) radar.EnableRadar();
                    weaponManager._radarsEnabled = true;
                }
                else
                {
                    foreach (var radar in weaponManager.radars)
                        if (radar != null && radar.radarEnabled && radar.sonarMode == ModuleRadar.SonarModes.None) radar.DisableRadar();
                    weaponManager._radarsEnabled = false;
                }
            }
        }

        static string FormattableString(System.FormattableString s) => System.FormattableString.Invariant(s);

        /// <summary>
        /// Our passive fix on the nearest hostile radar our receiver hears: its true position off by the measurement error (bearing
        /// error across the line of sight, R²σ/B along it, at most the receiver's own range estimate error).
        /// </summary>
        void UpdatePassiveFix(MissileFire weaponManager)
        {
            var rwr = weaponManager.rwr;
            if (rwr == null || !rwr.rwrEnabled) return;
            var body = vessel.mainBody;
            Vessel wingman = WingmanForOrders();
            var wingmanWM = wingman != null ? wingman.ActiveController().WM : null;
            var wingmanRwr = wingmanWM != null ? wingmanWM.rwr : null;
            RadarWarningReceiver.Emitter best = null;
            float bestRange = float.MaxValue;
            foreach (var e in rwr.Emitters)
            {
                if (e.vessel == null || e.friendly || e.missile || Time.time - e.lastHeard > 10f) continue;
                float range = (e.position - vessel.CoM).magnitude;
                if (range < bestRange) { best = e; bestRange = range; }
            }
            if (best == null) return;
            Vector3 los = (best.position - vessel.CoM) / Mathf.Max(bestRange, 1f);
            // Baseline across the line of sight: our own track over the last minute and the wingman's spread if it hears it too.
            Vector3 oldest = track.Count > 0 ? (Vector3)VectorUtils.GetWorldSurfacePostion(track.Peek().geo, body) : vessel.CoM;
            float ownBaseline = (vessel.CoM - oldest).ProjectOnPlanePreNormalized(los).magnitude;
            float wingBaseline = wingmanRwr != null && wingmanRwr.rwrEnabled && wingmanRwr.EmitterOf(best.vessel) != null ? (wingman.CoM - vessel.CoM).ProjectOnPlanePreNormalized(los).magnitude : 0f;
            float baseline = Mathf.Sqrt(ownBaseline * ownBaseline + wingBaseline * wingBaseline);
            float along = Mathf.Min(0.3f * bestRange, bestRange * bestRange * bearingSigma / Mathf.Max(baseline, 1f));
            float across = bestRange * bearingSigma;
            // Errors that wander slowly with time, different for each observer and emitter.
            int seed = (best.vessel.id.GetHashCode() ^ vessel.id.GetHashCode()) & 0x3FF;
            float n1 = 2f * Mathf.PerlinNoise(seed * 0.37f, Time.time / 40f) - 1f, n2 = 2f * Mathf.PerlinNoise(seed * 0.71f + 17f, Time.time / 40f) - 1f;
            Vector3 across1 = Vector3.Cross(los, upDirection).normalized;
            Vector3 estimate = best.position + n1 * along * los + n2 * across * across1;
            float error = Mathf.Sqrt(along * along + across * across);
            passiveCue.vessel = best.vessel; passiveCue.geo = VectorUtils.WorldPositionToGeoCoords(estimate, body); passiveCue.error = error; passiveCue.time = Time.time;
            passiveCue.source = wingBaseline > 0f ? "two-ship ESM" : "ESM";
            // Log the fix as it firms up: first heard, then each halving of the relative error.
            float relative = error / Mathf.Max(bestRange, 1f);
            if (!passiveFixLogged.TryGetValue(best.vessel, out float logged) || relative < 0.5f * logged)
            {
                passiveFixLogged[best.vessel] = relative;
                LogOrders(FormattableString($"{passiveCue.source} fix on {best.vessel.vesselName}: estimate {(estimate - vessel.CoM).magnitude / 1000f:0} km ± {error / 1000f:0.0} km, true {bestRange / 1000f:0} km, baseline {baseline / 1000f:0.0} km"));
            }
        }

        Vessel WingmanForOrders()
        {
            if (orders.lead != null && orders.lead.vessel != null && orders.lead.vessel.loaded) return orders.lead.vessel;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || other == vessel || !other.loaded) continue;
                var ai = other.ActiveController().AI as BDModulePilotAI;
                if (ai != null && ai.orders != null && ai.orders.lead == this && ai.orders.flight == orders.flight) return other; // Not another flight following us.
            }
            return null;
        }

        /// <summary>The cue to act on: the nearer of a fresh external cue and our passive fix, else the lead's (a hunter takes a high-value asset's first).</summary>
        Cue BestCue(out float range)
        {
            range = float.MaxValue;
            Cue best = null;
            bool bestValue = false;
            var body = vessel.mainBody;
            foreach (var cue in new[] { externalCue, passiveCue, orders.lead != null && orders.lead.vessel != null ? orders.lead.externalCue : null, orders.lead != null && orders.lead.vessel != null ? orders.lead.passiveCue : null })
            {
                if (cue == null || cue.vessel == null || Time.time - cue.time > cueMemory) continue;
                float r = (VectorUtils.GetWorldSurfacePostion(cue.geo, body) - vessel.CoM).ProjectOnPlanePreNormalized(upDirection).magnitude;
                bool value = orders.huntHighValue && IsHighValueAsset(cue.vessel);
                if ((value && !bestValue) || (value == bestValue && r < range)) { range = r; best = cue; bestValue = value; }
            }
            return best;
        }

        /// <summary>
        /// Throttle for orders flying: dry power at cruise, the afterburner only when asked for (a commit well above cruise speed, or
        /// catching up the slot). A craft whose dry power can't hold the ordered cruise is mis-tuned, not helped by burning.
        /// </summary>
        void CruiseThrottle(float speed, bool afterburner = false, bool brakes = false)
        {
            AdjustThrottle(speed, brakes, afterburner);
            if (!afterburner) speedController.forceAfterburnerIfMaxThrottle = false; // Not below the AB override speed either.
        }
        float OrdersAltitude()
        {
            if (orders.route.Count > 0) return (float)orders.route[Mathf.Clamp(routeIndex, 0, orders.route.Count - 1)].z;
            return defaultAltitude;
        }

        /// <summary>
        /// Fly towards a position at an altitude (ASL), without the combat corrections (their climb limit learns lift from hard
        /// manoeuvring, which cruise flight never does, and held every climb back). The aim is a point 4 km out along the horizontal
        /// direction to the position, turned at most 25° off the nose at a time (a cruise turn, not a hard pull), raised or lowered to a
        /// flight path angle of 1° per 250 m of height error, at most 8.5°. Aiming at the position itself would dive: on Kerbin a point
        /// at our height 200 km away is 9° below the horizontal. A slow integral of the flight path error is added, so a craft whose
        /// nose sits above its flight path (a heavy transport needs 6° to hold height) still gets the path it is asked for. Terrain
        /// avoidance still runs before this.
        /// </summary>
        void FlyOrdersTo(FlightCtrlState s, Vector3 position, float altitude, Vector3 threat = default)
        {
            const float lookAhead = 4000f, maxPath = 8.5f, metresPerDegree = 250f, integralRate = 0.15f, maxIntegral = 10f;
            var body = vessel.mainBody;
            Vector3 heading = vessel.srf_vel_direction.ProjectOnPlanePreNormalized(upDirection).normalized;
            Vector3 horizontal = (position - vessel.CoM).ProjectOnPlanePreNormalized(upDirection);
            if (horizontal.sqrMagnitude < 1f) horizontal = heading;
            if (heading != Vector3.zero) horizontal = Vector3.RotateTowards(heading, horizontal.normalized, 25f * Mathf.Deg2Rad, 0f);
            // Keep the low-signature nose cone on a known threat: no further off its bearing than the airframe's return allows.
            if (threat != Vector3.zero) horizontal = Vector3.RotateTowards(threat, horizontal.normalized, Mathf.Max(offAspectLimit - 10f, 10f) * Mathf.Deg2Rad, 0f);
            horizontal = horizontal.normalized;
            var geo = VectorUtils.WorldPositionToGeoCoords(vessel.CoM + lookAhead * horizontal, body);
            float floor = Mathf.Max((float)body.TerrainAltitude(geo.x, geo.y), 0f) + minAltitude;
            ordersTargetAltitude = Mathf.Max(altitude, floor);
            float path = Mathf.Clamp((ordersTargetAltitude - (float)vessel.altitude) / metresPerDegree, -maxPath, maxPath);
            float flightPath = Mathf.Asin(Mathf.Clamp(Vector3.Dot(vessel.srf_vel_direction, upDirection), -1f, 1f)) * Mathf.Rad2Deg;
            if (Time.time - ordersPathTime > 1f) ordersPathIntegral = 0f; // Not flown on orders a moment ago: start afresh.
            ordersPathTime = Time.time;
            float integrate = integralRate * (path - flightPath) * Time.fixedDeltaTime;
            if (integrate > 0f && (float)vessel.srfSpeed < 0.85f * orders.cruiseSpeed) integrate = -integralRate * Time.fixedDeltaTime; // Slow: speed first, let the nose back down.
            ordersPathIntegral = Mathf.Clamp(ordersPathIntegral + integrate, -maxIntegral, maxIntegral);
            ordersCommandedClimb = Mathf.Clamp(path + ordersPathIntegral, -maxPath - maxIntegral, maxPath + maxIntegral);
            float angle = ordersCommandedClimb * Mathf.Deg2Rad;
            // The throttle is the orders' own (CruiseThrottle): FlyToPosition would otherwise reset it, afterburner allowed, every frame.
            FlyToPosition(s, vessel.CoM + lookAhead * (Mathf.Cos(angle) * horizontal + Mathf.Sin(angle) * upDirection), true, false);
        }
        float ordersPathIntegral, ordersPathTime = -10f, slotSpread, slotSpreadTime = -10f;
        /// <summary>Flown with nothing to fight: egress, commit on a cue, hold the slot or fly the route.</summary>
        bool FlyOrders(FlightCtrlState s)
        {
            if (orders == null) return false;
            var body = vessel.mainBody;
            var weaponManager = WeaponManager;

            // High-value asset: run from the nearest hostile our side knows inside the egress range.
            if (orders.egressRange > 0f && weaponManager != null)
            {
                Vessel threat = null;
                float nearest = orders.egressRange;
                foreach (var info in BDATargetManager.TargetList(weaponManager.Team))
                {
                    if (info == null || info.Vessel == null || info.isMissile || info.Vessel.LandedOrSplashed) continue;
                    float d = (info.Vessel.CoM - vessel.CoM).magnitude;
                    if (d < nearest) { nearest = d; threat = info.Vessel; }
                }
                var seen = BestCue(out float seenRange); // Our own surveillance track.
                if (seen != null && seenRange < nearest) { nearest = seenRange; threat = seen.vessel; }
                if (threat != null)
                {
                    if (!egressing) { egressing = true; LogOrders($"egress from {threat.vesselName} at {nearest / 1000f:0} km"); }
                    SetStatus("Egress");
                    Vector3 away = (vessel.CoM - threat.CoM).ProjectOnPlanePreNormalized(upDirection).normalized;
                    CruiseThrottle(orders.commitSpeed); // Its dash speed: an AWACS airframe isn't flown past its limit however hard it runs.
                    FlyOrdersTo(s, vessel.CoM + 20000f * away, OrdersAltitude());
                    return true;
                }
                if (egressing) { egressing = false; LogOrders("egress over, back on station"); }
            }

            // Winchester: a fighter with nothing left to shoot recovers.
            if (orders.haveHome && orders.egressRange <= 0f && weaponManager != null && !weaponManager.HasWeaponsAndAmmo())
            {
                SetOrdersState("Recovering", "winchester, heading home");
                SetStatus("Winchester");
                CruiseThrottle(orders.cruiseSpeed);
                FlyOrdersTo(s, VectorUtils.GetWorldSurfacePostion(orders.home, body), OrdersAltitude());
                return true;
            }

            var cue = BestCue(out float cueRange);
            bool isLead = orders.lead == null || orders.lead.vessel == null || !orders.lead.vessel.loaded || orders.lead.OrdersRecovering;
            if (cue != null && cueRange < (committed ? 1.2f : 1f) * orders.commitRange && (isLead || orders.flankAngle != 0f)) // Committed stays committed while the estimate wanders.
            {
                if (!committed) { committed = true; LogOrders(FormattableString($"commit on {cue.source} cue: {cue.vessel?.vesselName} estimated {cueRange / 1000f:0} km ± {cue.error / 1000f:0} km")); }
                // Fly at the cue; a flanker swings out by its flank angle, which closes as the range comes down. A poor passive fix gets a
                // ranging leg: 35° off the bearing builds the baseline that fixes the range.
                Vector3 cuePosition = VectorUtils.GetWorldSurfacePostion(cue.geo, body);
                Vector3 toCue = (cuePosition - vessel.CoM).ProjectOnPlanePreNormalized(upDirection);
                float offset = orders.flankAngle * Mathf.Clamp01((cueRange - 20000f) / 60000f);
                bool ranging = cue == passiveCue && cue.error > (ordersState == "Ranging" ? 0.12f : 0.18f) * cueRange;
                if (ranging && offset == 0f) offset = Mathf.Min(35f, offAspectLimit); // No wider than the airframe's return allows.
                Vector3 direction = Quaternion.AngleAxis(offset, upDirection) * toCue.normalized;
                SetOrdersState(ranging ? "Ranging" : orders.flankAngle != 0f ? "Flanking" : "Committed", cue.vessel != null ? cue.vessel.vesselName : null);
                SetStatus(ranging ? "ESM ranging" : "Committed");
                // Emission control covers the infrared too: an afterburner plume is seen by infrared search far beyond radar range.
                CruiseThrottle(orders.commitSpeed, !orders.emissionControl && orders.commitSpeed > orders.cruiseSpeed + 50f);
                FlyOrdersTo(s, vessel.CoM + 20000f * direction, OrdersAltitude());
                return true;
            }
            if (committed && (cue == null || cueRange > 1.2f * orders.commitRange)) { committed = false; LogOrders("commit dropped, no cue"); }

            if (!isLead)
            {
                FlySlot(s, orders.lead);
                return true;
            }
            if (orders.route.Count == 0) return false;
            Vector3 point = VectorUtils.GetWorldSurfacePostion(orders.route[routeIndex % orders.route.Count], body);
            if ((point - vessel.CoM).ProjectOnPlanePreNormalized(upDirection).sqrMagnitude < 3000f * 3000f) routeIndex = (routeIndex + 1) % orders.route.Count;
            SetOrdersState("On station");
            SetStatus("CAP");
            CruiseThrottle(orders.cruiseSpeed);
            FlyOrdersTo(s, VectorUtils.GetWorldSurfacePostion(orders.route[routeIndex % orders.route.Count], body), OrdersAltitude());
            return true;
        }

        /// <summary>The slot on the lead (abeam on its right or left, stepped back and up, wider while the lead ranges passively) and the speed that closes on it.</summary>
        Vector3 SlotPoint(BDModulePilotAI lead, out Vector3 forward, out float speed)
        {
            Vessel leadVessel = lead.vessel;
            Vector3 leadVelocity = leadVessel.Velocity();
            forward = leadVelocity.ProjectOnPlanePreNormalized(upDirection).normalized;
            if (forward == Vector3.zero) forward = vesselTransform.up;
            Vector3 right = Vector3.Cross(upDirection, forward).normalized; // Unity's frame is left-handed: up × forward is right.
            // The spread opens and closes slowly (40 m/s across the track): sliding out sideways would show the threat our beam.
            float wanted = lead.ordersState == "Ranging" ? Mathf.Sign(orders.slotRight) * orders.rangingSpread : orders.slotRight;
            if (Time.time - slotSpreadTime > 1f) slotSpread = orders.slotRight;
            slotSpread = Mathf.MoveTowards(slotSpread, wanted, 40f * (Time.time - slotSpreadTime > 1f ? 0f : Time.time - slotSpreadTime));
            slotSpreadTime = Time.time;
            Vector3 slot = leadVessel.CoM + slotSpread * right - orders.slotBack * forward + orders.slotUp * upDirection;
            float along = Vector3.Dot(slot - vessel.CoM, forward);
            speed = Mathf.Clamp((float)leadVessel.srfSpeed + Mathf.Clamp(along / 20f, -60f, 80f), Mathf.Max(minSpeed + 20f, 0.8f * orders.cruiseSpeed), maxSpeed);
            return slot;
        }

        /// <summary>
        /// Missile truck discipline: a hunter following its escorts' lead keeps its slot behind them while its target is a fighter still
        /// beyond its missiles' reach (an AWACS: 1.3 times it), instead of running ahead of the escorts at it (where it is the biggest
        /// return in the sky). Inside reach, or defending, it fights as usual. Sets the fly-to point and the throttle.
        /// </summary>
        bool TruckHoldsSlot(Vessel target, ref Vector3 flyTo)
        {
            if (orders == null || !orders.huntHighValue || orders.lead == null || orders.lead.vessel == null || !orders.lead.vessel.loaded || orders.lead.vessel.LandedOrSplashed || orders.lead.OrdersRecovering) return false;
            var weaponManager = WeaponManager;
            if (target == null || weaponManager == null || weaponManager.missileIsIncoming || IsEvading) return false;
            float reach = 0f;
            foreach (var missile in VesselModuleRegistry.GetModules<MissileBase>(vessel))
                if (missile != null && !missile.HasFired && missile.engageAir)
                    reach = Mathf.Max(reach, MissileLaunchParams.GetDynamicLaunchParams(missile, target.Velocity(), target.CoM).maxLaunchRange);
            if ((target.CoM - vessel.CoM).magnitude < (IsHighValueAsset(target) ? 1.3f : 1.05f) * reach) return false; // An AWACS is run in on a little earlier.
            Vector3 slot = SlotPoint(orders.lead, out Vector3 forward, out float speed);
            flyTo = slot + 2500f * forward;
            CruiseThrottle(speed, false, true);
            ++truckSlotTicks;
            return true;
        }
        int truckSlotTicks, truckReserveHolds;
        const int truckReserve = 2;
        const float truckHighValueThreshold = 0.35f; // The reserved rounds go at an AWACS only with a real chance: it runs, and they are the last.

        /// <summary>
        /// A missile truck keeps its last truckReserve rounds of its longest-reaching type for the other side's AWACS while one is alive:
        /// a fighter gets them only in self-defence (inside 40 km with its nose on us).
        /// </summary>
        bool TruckReserves(Vessel target, MissileBase missile)
        {
            if (orders == null || !orders.huntHighValue || target == null || IsHighValueAsset(target) || WeaponManager == null) return false;
            float longest = 0f;
            foreach (var other in VesselModuleRegistry.GetModules<MissileBase>(vessel))
                if (other != null && !other.HasFired && other.engageAir) longest = Mathf.Max(longest, other.GetEngagementRangeMax());
            if (missile.GetEngagementRangeMax() < longest) return false; // Shorter rounds are for fighters.
            int rounds = 0;
            foreach (var other in VesselModuleRegistry.GetModules<MissileBase>(vessel))
                if (other != null && !other.HasFired && other.engageAir && other.GetEngagementRangeMax() >= longest) rounds += RoundsAboard(other);
            if (rounds > truckReserve) return false;
            bool assetAlive = false;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || !other.loaded || other.LandedOrSplashed || !IsHighValueAsset(other)) continue;
                var otherWM = other.ActiveController().WM;
                if (otherWM != null && WeaponManager.Team.IsEnemy(otherWM.Team)) { assetAlive = true; break; }
            }
            if (!assetAlive) return false;
            if ((target.CoM - vessel.CoM).magnitude < 40000f && !TurnedAway(target, 45f)) return false; // Self-defence.
            ++truckReserveHolds;
            return true;
        }

        /// <summary>Hold the slot on the lead.</summary>
        void FlySlot(FlightCtrlState s, BDModulePilotAI lead)
        {
            Vessel leadVessel = lead.vessel;
            Vector3 slot = SlotPoint(lead, out Vector3 forward, out float speed);
            SetOrdersState("In slot", leadVessel.vesselName);
            SetStatus("Formation");
            CruiseThrottle(speed, false, true); // Dry: an afterburner plume is seen by infrared search far beyond radar range.
            // Under emission control with the lead committed, holding the slot never swings our beam onto the threat.
            Vector3 threat = Vector3.zero;
            if (orders.emissionControl && lead.committed)
            {
                var cue = lead.BestCue(out _);
                if (cue != null) threat = (VectorUtils.GetWorldSurfacePostion(cue.geo, vessel.mainBody) - vessel.CoM).ProjectOnPlanePreNormalized(upDirection).normalized;
            }
            FlyOrdersTo(s, slot + 2500f * forward, (float)FlightGlobals.getAltitudeAtPos(leadVessel.CoM) + orders.slotUp, threat);
        }

        float ordersCommandedClimb, ordersTargetAltitude;
        string OrdersMetrics() => orders == null ? "orders=none" : FormattableString($"orders={orders.flight} state={ordersState.Replace(' ', '_')} emcon={(orders.emissionControl ? (emissionsLifted ? "lifted" : "silent") : "off")} committed={committed} ordersAlt={ordersTargetAltitude:0} cmdClimb={ordersCommandedClimb:0.0} truckSlot={truckSlotTicks * Time.fixedDeltaTime:0.0}s truckReserveHolds={truckReserveHolds}");
    }
}
