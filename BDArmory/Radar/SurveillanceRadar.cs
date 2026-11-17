using System.Collections.Generic;
using UnityEngine;

using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Radar
{
    /// <summary>
    /// A long-wave (VHF) surveillance radar on a large aircraft or a ground site, added by a scenario. At those wavelengths an
    /// airframe's shaping and coatings do little: its return depends on its size (BDAStealth's conventional rate, 0.154 m² per m² of
    /// the craft's mean face), so a stealth fighter is seen nearly as far as a conventional one of its size. The price is accuracy:
    /// tracks are good to a few kilometres, refreshed once per antenna turn, and too coarse to guide a missile or take a radar lock.
    /// What it is good for is control: once per turn every pilot on its side flying flight orders gets a cue on the nearest track by
    /// datalink and can be vectored onto it (GCI); nothing enters the side's target picture, so no one can engage on it. The radar
    /// horizon applies: Kerbin is small, so a ground site sees fighters at altitude only out to about 110 km
    /// and an airborne one at 10 km to about 210 km.
    /// A microwave radar (an E-3-class S-band set, microwave = true) instead sees the craft's own return towards it, shaping and
    /// coatings included, with tighter tracks.
    /// </summary>
    public class SurveillanceRadar : MonoBehaviour
    {
        public string radarName = "Surveillance";
        public string flight = "";              // Callsign of the carrier's flight (for scenario triggers).
        public bool microwave;
        public float referenceRange = 250000f; // m against a 5 m² return (low-band, or microwave at aspect).
        public float period = 10f;              // s per antenna turn.
        public float bearingSigma = 1.2f * Mathf.Deg2Rad, rangeSigma = 1500f;
        public float maxRange = 400000f;

        Vessel vessel;
        float nextSweep;
        readonly HashSet<Vessel> seen = new HashSet<Vessel>();
        readonly List<(Vessel vessel, Vector3 estimate, float error)> tracks = new List<(Vessel, Vector3, float)>();
        public int Sweeps { get; private set; }
        public static readonly HashSet<string> FlightsTracking = new HashSet<string>(); // Flights whose radar has a first track (scenario triggers).

        void Start()
        {
            vessel = GetComponent<Vessel>();
            nextSweep = Time.time + period;
            if (microwave) { bearingSigma = 0.3f * Mathf.Deg2Rad; rangeSigma = 300f; }
            Debug.Log($"[BDArmory.SurveillanceRadar]: {radarName} on {(vessel != null ? vessel.vesselName : "?")}, {(microwave ? "microwave" : "low band")}, {referenceRange / 1000f:0} km against 5 m², {period:0} s turn");
        }

        /// <summary>Low-band radar cross-section (m²) of a craft: by size, not by shaping.</summary>
        public static float LowBandRcs(Vessel v)
        {
            Vector3 size = v.vesselSize;
            float meanFace = (size.x * size.y + size.y * size.z + size.x * size.z) / 3f;
            return 0.154f * meanFace;
        }

        void Update()
        {
            if (vessel == null) { Destroy(this); return; }
            if (!vessel.loaded || Time.time < nextSweep) return;
            nextSweep = Time.time + period;
            var weaponManager = vessel.ActiveController().WM;
            if (weaponManager == null || weaponManager.Team == null) return;
            ++Sweeps;
            tracks.Clear();
            var body = vessel.mainBody;
            int hostiles = 0, outOfReach = 0, belowHorizon = 0;
            float nearest = float.MaxValue;
            foreach (var other in BDATargetManager.LoadedVessels)
            {
                if (other == null || other == vessel || !other.loaded || other.LandedOrSplashed || other.IsMissile()) continue;
                var otherWM = other.ActiveController().WM;
                if (otherWM == null || !weaponManager.Team.IsEnemy(otherWM.Team)) continue;
                ++hostiles;
                Vector3 offset = other.CoM - vessel.CoM;
                float range = offset.magnitude;
                nearest = Mathf.Min(nearest, range);
                var info = microwave ? other.gameObject.GetComponent<Targeting.TargetInfo>() : null;
                float rcs = info != null ? RadarUtils.GetVesselRadarSignatureAtAspect(info, vessel.CoM, range) : LowBandRcs(other);
                float reach = Mathf.Min(maxRange, referenceRange * Mathf.Pow(Mathf.Max(rcs, 0.01f) / 5f, 0.25f));
                if (range > reach) { ++outOfReach; continue; }
                if (VisualDetection.BlockedByBody(vessel.CoM, other.CoM, body)) { ++belowHorizon; continue; }
                // Coarse: bearing and range errors, fixed for a sweep.
                Vector3 los = offset / range, up = (vessel.CoM - body.position).normalized;
                Vector3 across = Vector3.Cross(los, up).normalized;
                float alongError = rangeSigma * Gaussian(), acrossError = range * bearingSigma * Gaussian();
                Vector3 estimate = other.CoM + alongError * los + acrossError * across;
                float error = Mathf.Sqrt(rangeSigma * rangeSigma + (range * bearingSigma) * (range * bearingSigma));
                tracks.Add((other, estimate, error));
                if (seen.Add(other))
                {
                    FlightsTracking.Add(flight);
                    Debug.Log(System.FormattableString.Invariant($"[BDArmory.SurveillanceRadar]: {radarName} ({vessel.vesselName}) first track on {other.vesselName} at {range / 1000f:0} km ({(microwave ? "aspect" : "low-band")} RCS {rcs:0.0} m², reach {reach / 1000f:0} km)"));
                }
            }
            if (Sweeps % 6 == 0)
                Debug.Log(System.FormattableString.Invariant($"[BDArmory.SurveillanceRadar]: {radarName} sweep {Sweeps}: {tracks.Count} tracks of {hostiles} hostile craft loaded (out of reach {outOfReach}, below the horizon {belowHorizon}), nearest {(hostiles > 0 ? nearest / 1000f : -1f):0} km, own altitude {vessel.altitude:0} m"));
            if (tracks.Count == 0) return;
            // Vector every pilot on our side flying orders onto the track nearest it (a hunter onto the nearest high-value asset).
            foreach (var friend in BDATargetManager.LoadedVessels)
            {
                if (friend == null || !friend.loaded) continue; // Including the craft carrying it, which runs from what it sees.
                var controller = friend.ActiveController();
                if (controller.WM == null || controller.WM.Team != weaponManager.Team || controller.AI is not BDModulePilotAI pilot || pilot.orders == null) continue;
                int best = -1;
                float bestDistance = float.MaxValue;
                bool bestValue = false;
                for (int i = 0; i < tracks.Count; ++i)
                {
                    float d = (tracks[i].estimate - friend.CoM).sqrMagnitude;
                    bool value = pilot.orders.huntHighValue && BDModulePilotAI.IsHighValueAsset(tracks[i].vessel);
                    if ((value && !bestValue) || (value == bestValue && d < bestDistance)) { bestDistance = d; best = i; bestValue = value; }
                }
                if (best >= 0) pilot.ReceiveCue(tracks[best].vessel, tracks[best].estimate, tracks[best].error, "GCI");
            }
        }

        static float Gaussian()
        {
            float u1 = Mathf.Max(Random.value, 1e-6f), u2 = Random.value;
            return Mathf.Clamp(Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2), -3f, 3f);
        }
    }
}
