using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

using BDArmory.Extensions;
using BDArmory.Settings;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.GPU
{
    /// <summary>
    /// Long-lived trails drawn on the GPU (shader BDArmory/GPU/Trail) in one procedural draw: smoke behind burning missile motors,
    /// condensation trails behind air-breathing engines where the air is cold enough, and wingtip vortices on hard-pulling aircraft
    /// in dense air. Each trail is a ring of points in its own block of one buffer; only new points are uploaded. Points are kept
    /// relative to the main body on the CPU, so floating-origin shifts and the frame velocity never move a trail.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class Trails : MonoBehaviour
    {
        public static bool Active => instance != null && GpuAssets.Trail != null && SystemInfo.supportsComputeShaders;
        const int PointsPerSlot = 1024, MaxSlots = 160, TrailLayer = 15;
        // K: no contrail above the warm limit; between it and the cold limit they get thicker and last longer, from short trails that
        // dissipate within seconds to persistent ones.
        const float ContrailWarmLimit = 275f, ContrailFullCold = 240f, ShortContrailLife = 20f;

        [StructLayout(LayoutKind.Sequential)]
        struct TrailPoint
        {
            public Vector3 offset; public float birth;
            public float width, alpha, along, pad;
        }
        const int PointStride = 32;

        [StructLayout(LayoutKind.Sequential)]
        struct SlotData
        {
            public Vector3 anchor; public float head;
            public Vector3 live; public float count;
            public float spread, lifetime, fadeIn, liveAlpha;
            public Color color;
            public float liveWidth, liveAlong, seed, pad;
        }
        const int SlotStride = 80;

        enum Kind { MissileSmoke, Contrail, Wingtip }

        class Style
        {
            public float width, spread, lifetime, fadeIn, minSpacing, maxInterval;
            public Color color;
        }
        static readonly Style[] styles =
        {
            new Style { width = 1.1f, spread = 1.4f, lifetime = 90f, fadeIn = 0f, minSpacing = 4f, maxInterval = 0.2f, color = new Color(0.80f, 0.80f, 0.78f, 1f) },
            new Style { width = 1.3f, spread = 1.3f, lifetime = 150f, fadeIn = 0.3f, minSpacing = 6f, maxInterval = 0.25f, color = new Color(0.97f, 0.98f, 1f, 1f) },
            new Style { width = 0.35f, spread = 0.55f, lifetime = 2.5f, fadeIn = 0.04f, minSpacing = 2f, maxInterval = 0.04f, color = new Color(0.95f, 0.96f, 1f, 1f) },
        };

        class Slot
        {
            public bool inUse, attached;
            public Kind kind;
            public CelestialBody body;
            public Vector3d anchor, last; // Relative to the body.
            public int head = -1, count, dirtyFrom = -1, dirtyCount;
            public float lastBirth, along, lastWidth, lastAlpha, seed;
            public Vector3d live;
            public float liveAlpha;
            public float lifetime;
        }

        /// <summary>One place a trail comes from; slot is the trail it is laying now (-1: none).</summary>
        class Emitter
        {
            public Kind kind;
            public Part part;
            public Transform transform;
            public Vector3 local; // In transform's space.
            public Vector3 exhaust; // Local direction the exhaust leaves in (engines).
            public ModuleEngines engine;
            public MissileLauncher missile;
            public int slot = -1;
            public bool seen;
        }

        class VesselTrails
        {
            public int partCount = -1;
            public readonly List<Emitter> emitters = new List<Emitter>();
        }

        static Trails instance;
        static readonly int pointsID = Shader.PropertyToID("_Points"), slotsID = Shader.PropertyToID("_Slots"), pointsPerSlotID = Shader.PropertyToID("_PointsPerSlot"),
            timeID = Shader.PropertyToID("_TrailTime"), sunDirID = Shader.PropertyToID("_TrailSunDir"), sunColorID = Shader.PropertyToID("_TrailSunColor"), ambientID = Shader.PropertyToID("_TrailAmbient");
        readonly TrailPoint[] points = new TrailPoint[PointsPerSlot * MaxSlots];
        readonly SlotData[] slotData = new SlotData[MaxSlots];
        readonly Slot[] slots = new Slot[MaxSlots];
        readonly Dictionary<Vessel, VesselTrails> vessels = new Dictionary<Vessel, VesselTrails>();
        readonly List<Vessel> gone = new List<Vessel>();
        ComputeBuffer pointBuffer, slotBuffer;
        Material material;
        int slotsUsed;
        public static int TrailCount { get; private set; }

        void Awake()
        {
            instance = this;
            for (int i = 0; i < MaxSlots; ++i) slots[i] = new Slot();
        }

        void Start()
        {
            if (GpuAssets.Trail == null || !SystemInfo.supportsComputeShaders) { Debug.Log("[BDArmory.Trails]: Trail shader not available, no trails."); return; }
            material = new Material(GpuAssets.Trail) { renderQueue = 3000 };
            pointBuffer = new ComputeBuffer(points.Length, PointStride);
            slotBuffer = new ComputeBuffer(MaxSlots, SlotStride);
            pointBuffer.SetData(points);
            slotBuffer.SetData(slotData);
            material.SetBuffer(pointsID, pointBuffer);
            material.SetBuffer(slotsID, slotBuffer);
            material.SetInt(pointsPerSlotID, PointsPerSlot);
            var body = FlightGlobals.GetHomeBody();
            if (body != null && body.atmosphere) Debug.Log($"[BDArmory.Trails]: {body.bodyName} contrails form above {ContrailLevel(body, ContrailWarmLimit):F0} m, full above {ContrailLevel(body, ContrailFullCold):F0} m.");
        }

        /// <summary>Lowest altitude at which the body's standard air is colder than the given temperature.</summary>
        static double ContrailLevel(CelestialBody body, float temperature)
        {
            for (double altitude = 0; altitude < body.atmosphereDepth; altitude += 100)
                if (body.GetTemperature(altitude) < temperature) return altitude;
            return double.NaN;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            pointBuffer?.Release();
            slotBuffer?.Release();
            if (material != null) Destroy(material);
        }

        void LateUpdate()
        {
            if (material == null || !Active) return;
            bool any = BDArmorySettings.PERFORMANCE_OPTIONS && (BDArmorySettings.TRAILS_MISSILE || BDArmorySettings.TRAILS_CONTRAIL || BDArmorySettings.TRAILS_WINGTIP);
            float now = Time.time;
            if (!any) DetachAll();
            else if (Time.timeScale > 0f) UpdateSources(now);
            Upload(now);
            if (slotsUsed == 0) return;
            UpdateLighting();
            material.SetFloat(timeID, now);
            var camera = FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null ? FlightCamera.fetch.mainCamera.transform.position : Vector3.zero;
            Graphics.DrawProcedural(material, new Bounds(camera, Vector3.one * 4e6f), MeshTopology.Triangles, PointsPerSlot * 6, slotsUsed, null, null, ShadowCastingMode.Off, false, TrailLayer);
        }

        #region Sources
        void UpdateSources(float now)
        {
            foreach (var entry in vessels) foreach (var e in entry.Value.emitters) e.seen = false;
            var loaded = FlightGlobals.VesselsLoaded;
            for (int i = 0; i < loaded.Count; ++i)
            {
                var v = loaded[i];
                if (v == null || v.packed || v.mainBody == null || !v.mainBody.atmosphere || v.LandedOrSplashed) continue;
                if (!vessels.TryGetValue(v, out var trails)) vessels[v] = trails = new VesselTrails();
                if (trails.partCount != v.parts.Count) Rebuild(v, trails);
                foreach (var e in trails.emitters)
                {
                    e.seen = true;
                    UpdateEmitter(v, e, now);
                }
            }
            gone.Clear();
            foreach (var entry in vessels)
            {
                bool any = false;
                foreach (var e in entry.Value.emitters) { if (e.seen) any = true; else Detach(e); }
                if (!any && (entry.Key == null || !entry.Key.loaded)) gone.Add(entry.Key);
            }
            foreach (var v in gone) vessels.Remove(v);
        }

        void Rebuild(Vessel v, VesselTrails trails)
        {
            foreach (var e in trails.emitters) Detach(e);
            trails.emitters.Clear();
            trails.partCount = v.parts.Count;
            if (v.IsMissile())
            {
                var missile = VesselModuleRegistry.GetMissileBase(v) as MissileLauncher;
                if (missile != null && missile.part != null)
                {
                    float back = 1.5f;
                    var collider = missile.part.collider;
                    if (collider != null) back = Mathf.Clamp(Vector3.Dot(collider.bounds.extents, new Vector3(1f, 1f, 1f)) * 0.6f, 0.5f, 4f);
                    var reference = missile.MissileReferenceTransform != null ? missile.MissileReferenceTransform : missile.part.transform;
                    trails.emitters.Add(new Emitter { kind = Kind.MissileSmoke, part = missile.part, transform = reference, local = new Vector3(0f, 0f, -back), missile = missile });
                }
                return;
            }
            foreach (var part in v.parts)
            {
                if (part == null) continue;
                foreach (var module in part.Modules)
                {
                    if (!(module is ModuleEngines engine) || !BreathesAir(engine) || engine.thrustTransforms == null || engine.thrustTransforms.Count == 0) continue;
                    if (engine.thrustTransforms.Count <= 2)
                        foreach (var t in engine.thrustTransforms)
                        {
                            if (t == null) continue;
                            trails.emitters.Add(new Emitter { kind = Kind.Contrail, part = part, transform = t, local = Vector3.forward * 1.5f, engine = engine });
                        }
                    else
                    {
                        // Many nozzles: one trail from their middle, on the first one's frame.
                        var first = engine.thrustTransforms[0];
                        Vector3 middle = Vector3.zero;
                        foreach (var t in engine.thrustTransforms) middle += first.InverseTransformPoint(t.position);
                        middle /= engine.thrustTransforms.Count;
                        trails.emitters.Add(new Emitter { kind = Kind.Contrail, part = part, transform = first, local = middle + Vector3.forward * 1.5f, engine = engine });
                    }
                }
            }
            AddWingtips(v, trails);
        }

        static bool BreathesAir(ModuleEngines engine)
        {
            if (engine.propellants == null) return false;
            foreach (var propellant in engine.propellants) if (propellant.name == "IntakeAir" || propellant.name == "IntakeAtm") return true;
            return false;
        }

        /// <summary>The outermost points of the craft's lifting surfaces either side of the control point, if it has wings worth the name.</summary>
        static void AddWingtips(Vessel v, VesselTrails trails)
        {
            var reference = v.ReferenceTransform;
            if (reference == null || !v.IsControllable) return;
            Part leftPart = null, rightPart = null;
            Vector3 left = Vector3.zero, right = Vector3.zero;
            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (var part in v.parts)
            {
                if (part == null || !part.Modules.Contains<ModuleLiftingSurface>()) continue;
                foreach (var filter in part.FindModelComponents<MeshFilter>())
                {
                    var mesh = filter.sharedMesh;
                    if (mesh == null) continue;
                    // The corners of the mesh's own bounds (no vertex read-back: rebuilt whenever the craft loses or drops a part).
                    var t = filter.transform;
                    var b = mesh.bounds;
                    for (int c = 0; c < 8; ++c)
                        Consider(t.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1))), part);
                }
            }
            void Consider(Vector3 world, Part part)
            {
                float x = reference.InverseTransformPoint(world).x;
                if (x < minX) { minX = x; left = world; leftPart = part; }
                if (x > maxX) { maxX = x; right = world; rightPart = part; }
            }
            if (leftPart == null || rightPart == null || maxX - minX < 3f) return; // Fins, not wings.
            trails.emitters.Add(new Emitter { kind = Kind.Wingtip, part = leftPart, transform = leftPart.transform, local = leftPart.transform.InverseTransformPoint(left) });
            trails.emitters.Add(new Emitter { kind = Kind.Wingtip, part = rightPart, transform = rightPart.transform, local = rightPart.transform.InverseTransformPoint(right) });
        }

        void UpdateEmitter(Vessel v, Emitter e, float now)
        {
            if (e.part == null || e.transform == null || e.part.vessel != v) { Detach(e); return; }
            float strength = Strength(v, e, out float widthScale, out float lifetime);
            if (strength < 0.02f) { Detach(e); return; }
            Vector3 world = e.transform.TransformPoint(e.local);
            Emit(e, v.mainBody, world, (float)v.srfSpeed, strength, widthScale, lifetime, now);
        }

        /// <summary>How thick the trail is right now (0: none), and a width factor.</summary>
        static float Strength(Vessel v, Emitter e, out float widthScale, out float lifetime)
        {
            widthScale = 1f;
            lifetime = styles[(int)e.kind].lifetime;
            switch (e.kind)
            {
                case Kind.MissileSmoke:
                    if (!BDArmorySettings.TRAILS_MISSILE || e.missile == null || !e.missile.HasFired || e.missile.ThrustNow <= 0f) return 0f;
                    return e.missile.MissileState == MissileBase.MissileStates.Boost ? 0.85f : 0.5f;
                case Kind.Contrail:
                    {
                        var engine = e.engine;
                        if (!BDArmorySettings.TRAILS_CONTRAIL || engine == null || !engine.EngineIgnited || engine.maxThrust <= 0f) return 0f;
                        float thrust = engine.finalThrust / engine.maxThrust;
                        if (thrust < 0.03f) return 0f;
                        float cold = Mathf.Clamp01((ContrailWarmLimit - (float)v.atmosphericTemperature) / (ContrailWarmLimit - ContrailFullCold));
                        widthScale = Mathf.Clamp(Mathf.Sqrt(engine.maxThrust / 100f), 0.5f, 2f); // Bigger engines, more water vapour.
                        lifetime = Mathf.Lerp(ShortContrailLife, lifetime, cold);
                        return 0.6f * Mathf.Sqrt(cold) * Mathf.Lerp(0.6f, 1f, thrust);
                    }
                case Kind.Wingtip:
                    {
                        if (!BDArmorySettings.TRAILS_WINGTIP) return 0f;
                        float load = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4f, 7.5f, (float)v.geeForce));
                        float humid = Mathf.Clamp01(((float)v.atmDensity - 0.15f) / 0.5f);
                        float fast = Mathf.Clamp01(((float)v.srfSpeed - 80f) / 60f);
                        return 0.55f * load * humid * fast;
                    }
            }
            return 0f;
        }
        #endregion

        #region Trails
        void Emit(Emitter e, CelestialBody body, Vector3 world, float speed, float strength, float widthScale, float lifetime, float now)
        {
            var style = styles[(int)e.kind];
            Vector3d rel = (Vector3d)world - body.position;
            Slot slot = e.slot >= 0 ? slots[e.slot] : null;
            if (slot == null || slot.body != body)
            {
                Detach(e);
                e.slot = Allocate(now);
                if (e.slot < 0) return;
                slot = slots[e.slot];
                slot.inUse = slot.attached = true;
                slot.kind = e.kind;
                slot.body = body;
                slot.anchor = rel;
                slot.head = -1; slot.count = 0; slot.along = 0f;
                slot.dirtyFrom = -1; slot.dirtyCount = 0;
                slot.seed = Random.Range(0f, 1000f);
                slot.lifetime = lifetime;
                AddPoint(e.slot, slot, rel, now, style.width * widthScale, strength);
            }
            slot.lifetime = Mathf.Max(slot.lifetime, lifetime); // Colder air further along keeps the whole trail longer.
            float spacing = Mathf.Max(style.minSpacing, speed * slot.lifetime / (PointsPerSlot - 16));
            double moved = (rel - slot.last).magnitude;
            if (moved >= spacing || moved > 0.5 && now - slot.lastBirth >= style.maxInterval)
            {
                slot.along += (float)moved;
                AddPoint(e.slot, slot, rel, now, style.width * widthScale, strength);
            }
            slot.live = rel;
            slot.liveAlpha = strength;
            slot.lastWidth = style.width * widthScale;
        }

        void AddPoint(int index, Slot slot, Vector3d rel, float now, float width, float alpha)
        {
            slot.head = (slot.head + 1) % PointsPerSlot;
            slot.count = Mathf.Min(slot.count + 1, PointsPerSlot);
            points[index * PointsPerSlot + slot.head] = new TrailPoint { offset = (Vector3)(rel - slot.anchor), birth = now, width = width, alpha = alpha, along = slot.along };
            if (slot.dirtyFrom < 0) slot.dirtyFrom = slot.head;
            slot.dirtyCount = Mathf.Min(slot.dirtyCount + 1, PointsPerSlot);
            slot.last = rel;
            slot.lastBirth = now;
            slot.lastWidth = width;
            slot.lastAlpha = alpha;
        }

        /// <summary>A free trail, else the one that will fade first among those no longer being laid.</summary>
        int Allocate(float now)
        {
            int oldest = -1;
            float oldestEnd = float.MaxValue;
            for (int i = 0; i < MaxSlots; ++i)
            {
                var slot = slots[i];
                if (!slot.inUse) return i;
                if (slot.attached) continue;
                float end = slot.lastBirth + slot.lifetime;
                if (end < oldestEnd) { oldestEnd = end; oldest = i; }
            }
            return oldest;
        }

        void Detach(Emitter e)
        {
            if (e.slot < 0) return;
            var slot = slots[e.slot];
            slot.attached = false;
            slot.live = slot.last; // The trail ends at its last point.
            slot.liveAlpha = slot.lastAlpha;
            e.slot = -1;
        }

        void DetachAll()
        {
            foreach (var entry in vessels) foreach (var e in entry.Value.emitters) Detach(e);
        }

        void Upload(float now)
        {
            slotsUsed = 0;
            int live = 0;
            for (int i = 0; i < MaxSlots; ++i)
            {
                var slot = slots[i];
                if (slot.inUse && !slot.attached && now - slot.lastBirth > slot.lifetime + 0.5f) slot.inUse = false;
                if (!slot.inUse || slot.body == null)
                {
                    slotData[i].count = 0f;
                    continue;
                }
                ++live;
                slotsUsed = i + 1;
                if (slot.dirtyCount > 0)
                {
                    int start = slot.dirtyFrom, n = slot.dirtyCount, first = Mathf.Min(n, PointsPerSlot - start);
                    pointBuffer.SetData(points, i * PointsPerSlot + start, i * PointsPerSlot + start, first);
                    if (n > first) pointBuffer.SetData(points, i * PointsPerSlot, i * PointsPerSlot, n - first);
                    slot.dirtyFrom = -1; slot.dirtyCount = 0;
                }
                var style = styles[(int)slot.kind];
                Vector3d bodyPosition = slot.body.position;
                slotData[i] = new SlotData
                {
                    anchor = (Vector3)(bodyPosition + slot.anchor), head = slot.head,
                    live = (Vector3)(bodyPosition + slot.live), count = slot.count,
                    spread = style.spread, lifetime = slot.lifetime, fadeIn = style.fadeIn, liveAlpha = slot.liveAlpha,
                    color = style.color,
                    liveWidth = slot.lastWidth, liveAlong = slot.along + (float)(slot.live - slot.last).magnitude, seed = slot.seed,
                };
            }
            TrailCount = live;
            if (slotsUsed > 0) slotBuffer.SetData(slotData, 0, 0, slotsUsed);
        }

        void UpdateLighting()
        {
            var camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vector3 position = camera != null ? camera.transform.position : Vector3.zero;
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            var body = FlightGlobals.currentMainBody;
            Vector3 sunDirection = Vector3.up;
            float elevation = 1f;
            if (sun != null)
            {
                sunDirection = ((Vector3)(sun.position - (Vector3d)position)).normalized;
                if (body != null) elevation = Vector3.Dot(sunDirection, ((Vector3)((Vector3d)position - body.position)).normalized);
            }
            float daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.1f, elevation));
            float low = 1f - Mathf.Clamp01(elevation / 0.35f); // Low sun: warmer light.
            Color sunColor = Color.Lerp(new Color(1f, 0.97f, 0.92f), new Color(1f, 0.62f, 0.38f), low * daylight);
            Color ambient = Color.Lerp(new Color(0.05f, 0.06f, 0.09f), new Color(0.36f, 0.40f, 0.48f), daylight);
            material.SetVector(sunDirID, new Vector4(sunDirection.x, sunDirection.y, sunDirection.z, daylight));
            material.SetColor(sunColorID, sunColor * 0.85f);
            material.SetColor(ambientID, ambient);
        }
        #endregion
    }
}
