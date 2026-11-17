using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.Extensions;
using BDArmory.GameModes;
using BDArmory.ModIntegration;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.VesselSpawning;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Testing
{
    /// <summary>
    /// Scripted air scenario. With GameData/BDArmory/PluginData/scenario.cfg present, the flight scene is cleared and the flights it
    /// describes are spawned on their stations, each member with its team, flight orders (BDModulePilotAI.FlightOrders: route, slot,
    /// emission control, passive ranging, commit criteria, home) and sensor fit (LPI radar, a surveillance radar). Events then
    /// run on a timeline or on what happens (first track, first shot, first kill): spawn a flight, push a flight to a point, show a
    /// message. Every sensor first (radar, warning receiver, surveillance track), launch and kill is logged as a "[BDArmory.Scenario]:
    /// T+..." line and shown on screen; the scenario is over when one side has no fighters left, every fighter left is recovering, or the
    /// duration runs out.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class Scenario : VesselSpawnerBase
    {
        static readonly string ConfigPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/scenario.cfg");

        class Member
        {
            public string craft, shipName;
            public float slotRight = 3000f, slotBack = 600f, slotUp;
            public Vessel vessel;
            public BDModulePilotAI pilot;
            public bool dead;
            public string name;
        }

        class Flight
        {
            public string callsign, team, spawnOn = "start", description = "", follow = ""; // follow: its lead holds a slot on that flight's lead.
            public Vector2d start, station, home;
            public bool haveStart, haveHome;
            public float heading = float.NaN, axis, leg = 40000f, altitude = 8000f;
            public float cruiseSpeed = 250f, commitSpeed = 350f, commitRange = 150000f, radarOnRange = 100000f;
            public float flankAngle, egressRange, lpi, surveillanceRange, rangingSpread = 12000f;
            public string surveillanceBand = "vhf"; // vhf (sees by size) or microwave (sees the craft's return at aspect, tighter tracks).
            public bool emissionControl, passiveRanging, huntHighValue, spawned, up; // up: its craft are flying (spawned is set when spawning starts).
            public readonly List<Member> members = new List<Member>();
            public bool IsAsset => egressRange > 0f;
        }

        class ScenarioEvent
        {
            public float at = -1f;
            public string on, spawn, push, message;
            public Vector2d to;
            public bool haveTo, fired;
        }

        readonly List<Flight> flights = new List<Flight>();
        readonly List<ScenarioEvent> events = new List<ScenarioEvent>();
        string scenarioName = "Scenario";
        Vector2d centre;
        float preRange = 300000f, duration = 1800f, sunElevation = float.NaN, sitrepInterval = 60f;
        bool startFlightsUp, infiniteOrdnance;
        bool? savedInfiniteOrdnance;
        float startTime = -1f;
        bool over;
        int shots, kills;
        readonly HashSet<string> firsts = new HashSet<string>();
        readonly HashSet<int> missilesSeen = new HashSet<int>(), missilesExploded = new HashSet<int>();
        readonly Dictionary<int, float> missileFirstSeen = new Dictionary<int, float>();
        readonly List<Launch> launches = new List<Launch>();
        class Launch { public MissileBase missile; public Vessel target; public string from, name; public float time, lastDistance = float.MaxValue, lastSeen; }
        
        float T => startTime < 0f ? 0f : Time.time - startTime;

        void Note(string message, bool toScreen = true)
        {
            Debug.Log($"[BDArmory.Scenario]: T+{T:0}s {message}");
            if (toScreen) ScreenMessages.PostScreenMessage(message, 6f, ScreenMessageStyle.UPPER_CENTER);
        }

        #region Config
        static Vector2d ParseGeo(string value)
        {
            var parts = value.Split(',');
            return new Vector2d(double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture), double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));
        }
        static void Read(ConfigNode node, string key, ref float value) { if (node.HasValue(key)) value = float.Parse(node.GetValue(key), CultureInfo.InvariantCulture); }
        static void Read(ConfigNode node, string key, ref bool value) { if (node.HasValue(key)) value = bool.Parse(node.GetValue(key)); }
        static void Read(ConfigNode node, string key, ref string value) { if (node.HasValue(key)) value = node.GetValue(key); }

        bool Load()
        {
            var node = ConfigNode.Load(ConfigPath)?.GetNode("SCENARIO");
            if (node == null) { Debug.LogError("[BDArmory.Scenario]: scenario.cfg has no SCENARIO node"); return false; }
            Read(node, "name", ref scenarioName);
            centre = node.HasValue("centre") ? ParseGeo(node.GetValue("centre")) : new Vector2d(0, -60);
            float preKm = preRange / 1000f; Read(node, "preRange", ref preKm); preRange = 1000f * preKm;
            Read(node, "duration", ref duration);
            Read(node, "sunElevation", ref sunElevation);
            Read(node, "sitrepInterval", ref sitrepInterval);
            if (node.HasValue("infiniteOrdnance")) bool.TryParse(node.GetValue("infiniteOrdnance"), out infiniteOrdnance);
            foreach (var f in node.GetNodes("FLIGHT"))
            {
                var flight = new Flight();
                Read(f, "callsign", ref flight.callsign); Read(f, "team", ref flight.team); Read(f, "spawn", ref flight.spawnOn); Read(f, "description", ref flight.description);
                flight.station = ParseGeo(f.GetValue("station"));
                if (f.HasValue("start")) { flight.start = ParseGeo(f.GetValue("start")); flight.haveStart = true; }
                if (f.HasValue("home")) { flight.home = ParseGeo(f.GetValue("home")); flight.haveHome = true; }
                Read(f, "heading", ref flight.heading); Read(f, "axis", ref flight.axis); Read(f, "leg", ref flight.leg); Read(f, "altitude", ref flight.altitude);
                Read(f, "cruiseSpeed", ref flight.cruiseSpeed); Read(f, "commitSpeed", ref flight.commitSpeed); Read(f, "commitRange", ref flight.commitRange);
                Read(f, "radarOnRange", ref flight.radarOnRange); Read(f, "flankAngle", ref flight.flankAngle);
                Read(f, "egressRange", ref flight.egressRange); Read(f, "lpiRadar", ref flight.lpi); Read(f, "surveillanceRange", ref flight.surveillanceRange);
                Read(f, "rangingSpread", ref flight.rangingSpread); Read(f, "emissionControl", ref flight.emissionControl); Read(f, "passiveRanging", ref flight.passiveRanging);
                Read(f, "huntHighValue", ref flight.huntHighValue); Read(f, "surveillanceBand", ref flight.surveillanceBand); Read(f, "follow", ref flight.follow);
                foreach (var m in f.GetNodes("MEMBER"))
                {
                    var member = new Member();
                    Read(m, "craft", ref member.craft); Read(m, "slotRight", ref member.slotRight); Read(m, "slotBack", ref member.slotBack); Read(m, "slotUp", ref member.slotUp);
                    string path = Path.Combine(AutoSpawnPath, member.craft);
                    if (!File.Exists(path)) { Debug.LogError($"[BDArmory.Scenario]: craft {path} not found"); return false; }
                    member.shipName = ConfigNode.Load(path)?.GetValue("ship") ?? Path.GetFileNameWithoutExtension(path);
                    flight.members.Add(member);
                }
                flights.Add(flight);
            }
            foreach (var e in node.GetNodes("EVENT"))
            {
                var ev = new ScenarioEvent();
                Read(e, "at", ref ev.at); Read(e, "on", ref ev.on); Read(e, "spawn", ref ev.spawn); Read(e, "push", ref ev.push); Read(e, "message", ref ev.message);
                if (e.HasValue("to")) { ev.to = ParseGeo(e.GetValue("to")); ev.haveTo = true; }
                events.Add(ev);
            }
            return flights.Count > 0;
        }
        #endregion

        protected override void Awake()
        {
            base.Awake();
            if (!File.Exists(ConfigPath)) { Destroy(this); return; }
        }

        void OnDestroy()
        {
            if (savedInfiniteOrdnance.HasValue) BDArmorySettings.INFINITE_ORDINANCE = savedInfiniteOrdnance.Value;
        }

        void Start()
        {
            if (!File.Exists(ConfigPath)) return;
            if (!Load()) { Destroy(this); return; }
            SurveillanceRadar.FlightsTracking.Clear();
            StartCoroutine(Run());
        }

        public override IEnumerator Spawn(SpawnConfig spawnConfig) { yield break; }

        IEnumerator Run()
        {
            yield return new WaitUntil(() => FlightGlobals.ready && BDArmorySetup.Instance != null && BDACompetitionMode.Instance != null && SpawnUtilsInstance.Instance != null);
            float wait = Time.realtimeSinceStartup + 6f;
            yield return new WaitUntil(() => Time.realtimeSinceStartup > wait);
            Debug.Log($"[BDArmory.Scenario]: {scenarioName}: {flights.Count} flights, {events.Count} events, PRE {preRange / 1000f:0} km (was {PhysicsRangeExtender.GetPRERange() / 1000f:0} km)");
            PhysicsRangeExtender.SetPRERange((int)preRange);
            // The load-out is part of the scenario: launchers that reload from stowed rounds run dry (restored when the scene ends).
            savedInfiniteOrdnance = BDArmorySettings.INFINITE_ORDINANCE;
            BDArmorySettings.INFINITE_ORDINANCE = infiniteOrdnance;

            var spawnConfig = new SpawnConfig(FlightGlobals.currentMainBody.flightGlobalsIndex, centre.x, centre.y, 8000, killEverythingFirst: true, assignTeams: false);
            PreSpawnInitialisation(spawnConfig);
            vesselsSpawning = true;
            yield return AcquireSpawnPoint(spawnConfig, preRange / 2.5f, true);
            if (spawnFailureReason != SpawnFailureReason.None) { Debug.LogError($"[BDArmory.Scenario]: spawn point failed: {spawnFailureReason}"); vesselsSpawning = false; yield break; }
            if (!float.IsNaN(sunElevation)) yield return SetSun();

            foreach (var flight in flights)
            {
                double terrain = FlightGlobals.currentMainBody.TerrainAltitude(flight.station.x, flight.station.y);
                if (terrain > 0) Debug.LogWarning($"[BDArmory.Scenario]: {flight.callsign}'s station is over land ({terrain:0} m)");
            }
            foreach (var flight in flights.Where(f => f.spawnOn == "start")) yield return SpawnFlight(flight);
            // Weapons free only once every start flight is on its side: a craft spawns on its craft file's team until configured.
            startFlightsUp = true;
            foreach (var member in flights.Where(f => !f.IsAsset).SelectMany(f => f.members).Where(m => Alive(m) && m.pilot != null))
            {
                var weaponManager = member.vessel.ActiveController().WM;
                if (weaponManager != null && !weaponManager.guardMode) weaponManager.ToggleGuardMode();
            }
            vesselsSpawning = false;
            startTime = Time.time;
            var first = flights.FirstOrDefault(f => f.spawned && f.members.Any(m => m.vessel != null))?.members.First(m => m.vessel != null).vessel;
            if (first != null) yield return LoadedVesselSwitcher.Instance.SwitchToVesselWhenPossible(first);
            Note($"{scenarioName}: {string.Join(", ", flights.Where(f => f.spawned).Select(f => $"{f.callsign} ({f.team}, {f.members.Count})"))} on station");

            float nextSitrep = Time.time + sitrepInterval;
            while (!over)
            {
                float next = Time.time + 1f;
                yield return new WaitUntil(() => Time.time >= next);
                if (Time.time >= nextSitrep) { nextSitrep += sitrepInterval; Sitrep(); }
                Monitor();
                RunEvents();
                CheckOver();
            }
        }

        #region Spawning
        Vector3d World(Vector2d geo, double altitude = 0) => FlightGlobals.currentMainBody.GetWorldSurfacePosition(geo.x, geo.y, altitude);
        Vector2d Geo(Vector3d world) { var g = VectorUtils.WorldPositionToGeoCoords(world, FlightGlobals.currentMainBody); return new Vector2d(g.x, g.y); }
        (Vector3 north, Vector3 east, Vector3 up) Frame(Vector2d geo)
        {
            Vector3d p = World(geo);
            Vector3 up = (p - FlightGlobals.currentMainBody.position).normalized;
            Vector3 east = ((Vector3)(World(new Vector2d(geo.x, geo.y + 0.01)) - p)).ProjectOnPlanePreNormalized(up).normalized;
            Vector3 north = ((Vector3)(World(new Vector2d(geo.x + 0.01, geo.y)) - p)).ProjectOnPlanePreNormalized(up).normalized;
            return (north, east, up);
        }
        Vector3 Horizontal(Vector2d geo, float heading) { var (north, east, _) = Frame(geo); return Mathf.Cos(heading * Mathf.Deg2Rad) * north + Mathf.Sin(heading * Mathf.Deg2Rad) * east; }
        Vector2d Offset(Vector2d geo, float heading, float distance) => Geo(World(geo) + (Vector3d)(distance * Horizontal(geo, heading)));

        IEnumerator SpawnFlight(Flight flight)
        {
            if (flight.spawned) yield break;
            flight.spawned = true;
            Vector2d start = flight.haveStart ? flight.start : flight.station;
            float heading = float.IsNaN(flight.heading) ? flight.axis : flight.heading;
            Vector3 forward = Horizontal(start, heading), right = Horizontal(start, heading + 90f);
            var configs = new List<VesselSpawnConfig>();
            foreach (var member in flight.members)
            {
                bool lead = member == flight.members[0];
                Vector3d position = World(start) + (Vector3d)(lead ? Vector3.zero : member.slotRight * right - member.slotBack * forward);
                configs.Add(new VesselSpawnConfig(Path.Combine(AutoSpawnPath, member.craft), position, forward, flight.altitude + (lead ? 0f : member.slotUp), 0f, true, false));
            }
            yield return SpawnVessels(configs);
            if (spawnFailureReason != SpawnFailureReason.None) { Debug.LogError($"[BDArmory.Scenario]: {flight.callsign} failed to spawn: {spawnFailureReason}"); spawnFailureReason = SpawnFailureReason.None; flight.up = true; foreach (var m in flight.members) m.dead = true; yield break; }
            var spawnConfig = new SpawnConfig(FlightGlobals.currentMainBody.flightGlobalsIndex, start.x, start.y, flight.altitude, killEverythingFirst: false, assignTeams: false);
            yield return PostSpawnMainSequence(spawnConfig, true, true, true);
            if (spawnFailureReason != SpawnFailureReason.None) { Debug.LogError($"[BDArmory.Scenario]: {flight.callsign} post-spawn failed: {spawnFailureReason}"); spawnFailureReason = SpawnFailureReason.None; flight.up = true; foreach (var m in flight.members) m.dead = true; yield break; }
            var vessels = spawnedVessels.Values.ToList();
            foreach (var member in flight.members)
            {
                member.vessel = vessels.FirstOrDefault(v => v != null && v.vesselName.StartsWith(member.shipName) && !flights.SelectMany(f => f.members).Any(m => m != member && m.vessel == v));
                if (member.vessel == null) { Debug.LogError($"[BDArmory.Scenario]: {member.shipName} not found after spawning"); member.dead = true; continue; }
                member.name = member.vessel.vesselName;
            }
            yield return new WaitForFixedUpdate();
            foreach (var member in flight.members.Where(m => m.vessel != null)) Configure(flight, member, start, heading);
            flight.up = true;
            Note($"{flight.callsign} flight up: {string.Join(", ", flight.members.Where(m => m.vessel != null).Select(m => m.name))}{(flight.description.Length > 0 ? " - " + flight.description : "")}");
        }

        void Configure(Flight flight, Member member, Vector2d start, float heading)
        {
            var v = member.vessel;
            var weaponManager = v.ActiveController().WM;
            var pilot = v.ActiveController().AI as BDModulePilotAI;
            if (weaponManager == null || pilot == null) { Debug.LogError($"[BDArmory.Scenario]: {member.name} has no weapon manager or pilot"); return; }
            member.pilot = pilot;
            weaponManager.SetTeam(BDTeam.Get(flight.team));
            weaponManager.engageMissile = true; weaponManager.ToggleEngageMissile(); // Fighters shoot at aircraft, not at the missiles coming at them (the toggle sets every weapon).
            bool lead = member == flight.members[0];
            // A following flight's lead holds its slot on the followed flight's lead (a missile truck trailing its escorts).
            var followed = lead && flight.follow.Length > 0 ? flights.FirstOrDefault(f => f.callsign == flight.follow)?.members.FirstOrDefault(m => m.pilot != null)?.pilot : null;
            float altitude = flight.altitude + (lead ? 0f : member.slotUp);
            pilot.defaultAltitude = altitude;
            if (pilot.minAltitude > 0.6f * altitude) pilot.minAltitude = Mathf.Max(80f, 0.4f * altitude);
            pilot.maxAltitude = Mathf.Max(pilot.maxAltitude, altitude + 2000f);
            var orders = new BDModulePilotAI.FlightOrders
            {
                flight = flight.callsign,
                lead = lead ? followed : flight.members[0].pilot,
                slotRight = member.slotRight, slotBack = member.slotBack, slotUp = member.slotUp, rangingSpread = flight.rangingSpread,
                cruiseSpeed = flight.cruiseSpeed, commitSpeed = flight.commitSpeed, commitRange = flight.commitRange, radarOnRange = flight.radarOnRange,
                emissionControl = flight.emissionControl, passiveRanging = flight.passiveRanging, flankAngle = flight.flankAngle, egressRange = flight.egressRange,
                huntHighValue = flight.huntHighValue,
            };
            var body = FlightGlobals.currentMainBody;
            foreach (var point in new[] { Offset(flight.station, flight.axis, 0.5f * flight.leg), Offset(flight.station, flight.axis, -0.5f * flight.leg) })
                orders.route.Add(new Vector3d(point.x, point.y, flight.altitude));
            if (flight.haveHome) { orders.home = new Vector3d(flight.home.x, flight.home.y, 0); orders.haveHome = true; }
            pilot.orders = orders;
            if (flight.lpi > 0f) foreach (var radar in weaponManager.radars) if (radar != null) radar.rwrDetectability = flight.lpi;
            if (flight.surveillanceRange > 0f && lead)
            {
                var surveillance = v.gameObject.AddComponent<SurveillanceRadar>();
                surveillance.microwave = flight.surveillanceBand == "microwave";
                surveillance.radarName = $"{flight.callsign} {(surveillance.microwave ? "S-band" : "VHF")}";
                surveillance.flight = flight.callsign;
                surveillance.referenceRange = flight.surveillanceRange;
            }
            // On station, flying: level, along the flight's heading, at cruise speed.
            Vector3 nose = Horizontal(Geo(v.CoM), heading), up = (v.CoM - body.position).normalized;
            Place(v, v.CoM, nose, up, flight.cruiseSpeed * nose);
            pilot.ActivatePilot();
            // An unarmed asset in guard mode takes a target and rams it; without it the pilot flies its orders (egress included).
            if (startFlightsUp && !flight.IsAsset && !weaponManager.guardMode) weaponManager.ToggleGuardMode();
            if (flight.emissionControl) foreach (var radar in weaponManager.radars) if (radar != null && radar.radarEnabled) radar.DisableRadar();
            Debug.Log(FormattableString.Invariant($"[BDArmory.Scenario]: {member.name}: team {flight.team}, {(lead ? "lead" : $"slot {member.slotRight:0}/{member.slotBack:0}/{member.slotUp:0} m")}, alt {altitude:0} m, cruise {flight.cruiseSpeed:0} m/s, emcon {flight.emissionControl}, passive {flight.passiveRanging}, lpi {flight.lpi:0.00}, radars {weaponManager.radars.Count}, rwr {(weaponManager.rwr != null ? "yes" : "no")}, size {v.vesselSize}, low-band RCS {SurveillanceRadar.LowBandRcs(v):0.0} m²"));
        }

        /// <summary>Put a vessel at a position with its nose along a direction, wings level, flying at a velocity (as MissileRange does).</summary>
        static void Place(Vessel v, Vector3d centreOfMass, Vector3 nose, Vector3 up, Vector3 velocity)
        {
            Quaternion rootRotation0 = v.transform.rotation, referenceRotation0 = v.ReferenceTransform.rotation;
            Vector3 belly = -Vector3.ProjectOnPlane(up, nose).normalized;
            Quaternion referenceRotation = Quaternion.LookRotation(belly, nose);
            Quaternion rootRotation = referenceRotation * Quaternion.Inverse(referenceRotation0) * rootRotation0;
            Vector3 comOffset = rootRotation * Quaternion.Inverse(rootRotation0) * (v.CoM - v.transform.position);
            v.IgnoreGForces(240);
            v.IgnoreSpeed(240);
            v.SetRotation(rootRotation);
            v.SetPosition(centreOfMass - (Vector3d)comOffset);
            v.SetVelocity(velocity);
            foreach (var part in v.parts) if (part.rb != null) part.rb.angularVelocity = Vector3.zero;
        }

        /// <summary>Set the clock so that the sun stands at sunElevation above the eastern horizon at the centre (morning).</summary>
        IEnumerator SetSun()
        {
            var body = FlightGlobals.currentMainBody;
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || body == sun) yield break;
            double day = body.solarDayLength > 0 ? body.solarDayLength : body.rotationPeriod;
            double before = SunAngle(body, sun);
            double delta = (((sunElevation - before) % 360.0) + 360.0) % 360.0;
            Planetarium.SetUniversalTime(Planetarium.GetUniversalTime() + delta / 360.0 * day);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            Debug.Log(FormattableString.Invariant($"[BDArmory.Scenario]: sun moved from {before:0.0}° to {SunAngle(body, sun):0.0}° above the eastern horizon (target {sunElevation:0}°, clock +{delta / 360.0 * day:0} s)"));
        }

        double SunAngle(CelestialBody body, CelestialBody sun)
        {
            var (_, east, up) = Frame(centre);
            Vector3 toSun = ((Vector3)(sun.position - World(centre))).normalized;
            return Math.Atan2(Vector3.Dot(toSun, up), Vector3.Dot(toSun, east)) * Mathf.Rad2Deg;
        }
        #endregion

        #region Monitoring
        bool Alive(Member m)
        {
            if (m.dead || m.vessel == null) return false;
            var v = m.vessel;
            return v.loaded && !v.LandedOrSplashed && !CleanDamage.IsShotDown(v) && v.ActiveController().AI != null && v.ActiveController().WM != null;
        }

        string NameOf(Vessel v) => v == null ? "?" : v.vesselName;

        void Monitor()
        {
            var all = flights.SelectMany(f => f.members.Select(m => (f, m))).Where(x => x.m.vessel != null || x.m.dead).ToList();
            // Launches (each followed in flight to name the missile that brings a craft down). A missile is reported a second after it is
            // first seen: on its launch frame its target can still be one its launcher's radar held before (a reloaded copy's).
            if (BDATargetManager.FiredMissiles != null)
                foreach (var weapon in BDATargetManager.FiredMissiles)
                {
                    if (weapon is not MissileBase missile || missile == null || !missile.HasFired) continue;
                    int id = missile.GetInstanceID();
                    if (missilesSeen.Contains(id)) continue;
                    if (!missileFirstSeen.TryGetValue(id, out float seenAt)) { missileFirstSeen[id] = Time.time; continue; }
                    if (Time.time - seenAt < 1f) continue;
                    missilesSeen.Add(id); missileFirstSeen.Remove(id);
                    var source = missile.SourceVessel;
                    var target = missile.targetVessel != null ? missile.targetVessel.Vessel : null;
                    if (source == null || !all.Any(x => x.m.vessel == source)) continue;
                    ++shots;
                    float range = target != null ? (target.CoM - source.CoM).magnitude : -1f;
                    string kind = missile.TargetingMode == MissileBase.TargetingModes.Radar ? "Fox 3" : missile.TargetingMode == MissileBase.TargetingModes.Heat ? "Fox 2" : "Launch";
                    Note(FormattableString.Invariant($"{kind}: {NameOf(source)} {missile.GetShortName()} at {NameOf(target)}, {range / 1000f:0.0} km"));
                    launches.Add(new Launch { missile = missile, target = target, from = NameOf(source), name = missile.GetShortName(), time = seenAt, lastSeen = Time.time });
                    if (firsts.Add("firstShot")) Fire("firstShot");
                }

            foreach (var launch in launches) // How close each missile still flying has got to its target.
                if (launch.missile != null && launch.missile.vessel != null && launch.target != null && !launch.missile.HasExploded)
                {
                    launch.lastDistance = (launch.missile.vessel.CoM - launch.target.CoM).magnitude;
                    launch.lastSeen = Time.time;
                }

            foreach (var (flight, member) in all)
            {
                if (member.dead) continue;
                if (!Alive(member))
                {
                    member.dead = true;
                    ++kills;
                    var v = member.vessel;
                    string cause = v == null || !v.loaded ? "lost" : v.LandedOrSplashed ? "in the sea" : CleanDamage.IsShotDown(v) ? "shot down" : "destroyed";
                    // The missile that had closed to within 3 km of it when last seen (at most 3 s ago), else none known.
                    var shot = launches.Where(x => x.target == v && Time.time - x.lastSeen < 3f && x.lastDistance < 3000f).OrderBy(x => x.lastDistance).FirstOrDefault();
                    string by = shot != null ? FormattableString.Invariant($" by {shot.from}'s {shot.name} ({Time.time - shot.time:0} s flight)") : "";
                    Note($"Splash {member.name} ({flight.callsign}, {cause}){by}");
                    if (firsts.Add("firstKill")) Fire("firstKill");
                    continue;
                }
                var wm = member.vessel.ActiveController().WM;
                // Warning receiver: first hostile emitter heard by this craft.
                if (wm.rwr != null)
                    foreach (var e in wm.rwr.Emitters)
                        if (e.vessel != null && !e.friendly && !e.missile && firsts.Add($"rwr|{member.name}|{e.vessel.vesselName}"))
                            Note(FormattableString.Invariant($"{member.name} RWR: {e.vessel.vesselName} ({e.radar ?? e.type.ToString()}) at {(e.position - member.vessel.CoM).magnitude / 1000f:0} km"), false);
            }
            // Each side's first picture of each enemy craft: radar (or IRST) track, or a contact (visual, a lock giving itself away).
            foreach (var team in flights.Select(f => f.team).Distinct())
            {
                var bdTeam = BDTeam.Get(team);
                foreach (var info in BDATargetManager.TargetList(bdTeam))
                {
                    if (info == null || info.Vessel == null || info.isMissile) continue;
                    bool track = info.detected.TryGetValue(bdTeam, out bool d) && d;
                    if (!firsts.Add($"pic|{team}|{info.Vessel.vesselName}|{track}")) continue;
                    var nearest = all.Where(x => x.f.team == team && Alive(x.m)).Select(x => x.m.vessel).OrderBy(v => (v.CoM - info.Vessel.CoM).sqrMagnitude).FirstOrDefault();
                    if (nearest == null) continue;
                    string by = "";
                    if (info.lastReport.TryGetValue(bdTeam, out var report) && report.reporter != null)
                    {
                        // The target as the reporter saw it: its signature towards the reporter and how far off its nose the reporter was.
                        Vector3 toReporter = report.reporter.CoM - info.Vessel.CoM;
                        float aspectRcs = RadarUtils.GetVesselRadarSignatureAtAspect(info, report.reporter.CoM, toReporter.magnitude);
                        float offNose = Vector3.Angle(info.Vessel.ReferenceTransform.up, toReporter);
                        by = FormattableString.Invariant($" (reported by {report.reporter.vesselName}{(report.sensor != null ? " " + report.sensor : "")} at {toReporter.magnitude / 1000f:0} km, RCS {info.radarBaseSignature:0.00} m², {aspectRcs:0.00} m² towards it, {offNose:0}° off its nose)");
                    }
                    Note(FormattableString.Invariant($"{team} {(track ? "tracks" : "has a contact on")} {info.Vessel.vesselName}, {(nearest.CoM - info.Vessel.CoM).magnitude / 1000f:0} km from {nearest.vesselName}{by}"), track);
                    if (firsts.Add("firstTrack")) Fire("firstTrack");
                }
            }
            foreach (var tracking in SurveillanceRadar.FlightsTracking) // "surveillanceTrack:<callsign>" for one flight's radar, "surveillanceTrack" for any.
                if (firsts.Add("surveillanceTrack:" + tracking)) { Fire("surveillanceTrack:" + tracking); if (firsts.Add("surveillanceTrack")) Fire("surveillanceTrack"); }
        }

        /// <summary>Every sitrepInterval (a minute by default): where every craft still flying is, what it is doing and on what power.</summary>
        void Sitrep()
        {
            foreach (var flight in flights)
                foreach (var member in flight.members.Where(Alive))
                {
                    var v = member.vessel;
                    var pilot = member.pilot;
                    bool afterburner = v.FindPartModulesImplementing<MultiModeEngine>().Any(e => !e.runningPrimary);
                    Debug.Log(FormattableString.Invariant($"[BDArmory.Scenario]: T+{T:0}s Sitrep {member.name}: {v.latitude:0.00}, {v.longitude:0.00}, {v.altitude:0} m, {v.srfSpeed:0} m/s (M{v.mach:0.00}), throttle {v.ctrlState.mainThrottle * 100f:0}% {(afterburner ? "wet" : "dry")}, {(pilot != null ? pilot.currentStatus : "?")}, missiles {MissilesLeft(v)}"));
                }
        }

        /// <summary>Rounds still aboard: each missile on a rail, plus any stowed rounds its launcher reloads from.</summary>
        static int MissilesLeft(Vessel v) => VesselModuleRegistry.GetModules<MissileBase>(v).Where(m => m != null && !m.HasFired)
            .Sum(m => m.reloadableRail != null ? Math.Max(m.reloadableRail.ammoCount, 0) : 1);

        void Fire(string trigger)
        {
            foreach (var e in events) if (!e.fired && e.on == trigger) Execute(e);
        }

        void RunEvents()
        {
            foreach (var e in events) if (!e.fired && e.at >= 0f && T >= e.at) Execute(e);
        }

        void Execute(ScenarioEvent e)
        {
            e.fired = true;
            if (!string.IsNullOrEmpty(e.message)) Note(e.message);
            if (!string.IsNullOrEmpty(e.spawn))
            {
                var flight = flights.FirstOrDefault(f => f.callsign == e.spawn);
                if (flight != null) StartCoroutine(SpawnFlight(flight));
            }
            if (!string.IsNullOrEmpty(e.push) && e.haveTo)
            {
                var flight = flights.FirstOrDefault(f => f.callsign == e.push);
                if (flight == null) return;
                foreach (var member in flight.members.Where(m => m.pilot != null && m.pilot.orders != null))
                {
                    member.pilot.orders.route.Clear();
                    member.pilot.orders.route.Add(new Vector3d(e.to.x, e.to.y, flight.altitude));
                }
                Debug.Log(FormattableString.Invariant($"[BDArmory.Scenario]: {flight.callsign} pushes to {e.to.x:0.00}, {e.to.y:0.00}"));
            }
        }

        void CheckOver()
        {
            string reason = null;
            var teams = flights.Select(f => f.team).Distinct().ToList();
            foreach (var team in teams)
            {
                var fighters = flights.Where(f => f.team == team && !f.IsAsset).ToList();
                bool pending = fighters.Any(f => !f.up);
                if (!pending && fighters.SelectMany(f => f.members).All(m => !Alive(m))) reason = $"{team} has no fighters left";
            }
            var aliveFighters = flights.Where(f => !f.IsAsset && f.spawned).SelectMany(f => f.members).Where(Alive).ToList();
            if (reason == null && aliveFighters.Count > 0 && flights.All(f => f.spawned) && aliveFighters.All(m => m.pilot != null && m.pilot.orders != null && !m.vessel.ActiveController().WM.HasWeaponsAndAmmo()))
                reason = "every fighter left is recovering";
            if (reason == null && T > duration) reason = "time";
            if (reason == null) return;
            over = true;
            Note($"Scenario over ({reason}): {shots} shots, {kills} lost");
            foreach (var flight in flights)
                foreach (var member in flight.members)
                    Debug.Log($"[BDArmory.Scenario]: Result {flight.callsign} {member.name ?? member.shipName}: {(!flight.spawned ? "never launched" : Alive(member) ? (member.pilot != null && member.pilot.OrdersRecovering ? "alive, recovering" : "alive") : "lost")}");
        }
        #endregion
    }
}
