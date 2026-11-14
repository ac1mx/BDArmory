using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

using BDArmory.Bullets;
using BDArmory.Targeting;

namespace BDArmory.GPU
{
    /// <summary>
    /// Draws every bullet's tracer and smoke trail with one procedural draw per texture (shader BDArmory/GPU/Tracer), instead of
    /// two LineRenderers per bullet, each with its own material, its own draw call and its geometry and width rebuilt on the
    /// main thread for every camera. The bullets' LineRenderers stay as they were but don't render (forceRenderingOff).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TracerRenderer : MonoBehaviour
    {
        /// <summary>Off: the bullets' LineRenderers draw as before (also a BDAPerf flag: arm).</summary>
        public static bool Enabled = true;
        public static bool Active => Enabled && instance != null && GpuAssets.Tracer != null && SystemInfo.supportsComputeShaders;
        const int TracerLayer = 15; // The bullets' layer.

        [StructLayout(LayoutKind.Sequential)]
        struct Segment
        {
            public Vector3 p0; public float w0;
            public Vector3 p1; public float w1;
            public Vector3 anchor; public float pad;
            public Color tint;
            public Vector4 uv;
        }
        const int SegmentStride = 80;

        class Group
        {
            public Segment[] segments = new Segment[256];
            public int count;
            public ComputeBuffer buffer;
            public Material material;
            public void Add(in Segment s)
            {
                if (count == segments.Length) System.Array.Resize(ref segments, count * 2);
                segments[count++] = s;
            }
        }

        static TracerRenderer instance;
        static readonly int segmentsID = Shader.PropertyToID("_Segments"), camScaleID = Shader.PropertyToID("_BDTracerCamScale");
        readonly Dictionary<Texture, Group> tracerGroups = new Dictionary<Texture, Group>();
        readonly Dictionary<Texture, Group> smokeGroups = new Dictionary<Texture, Group>();
        readonly Vector3[] smokePoints = new Vector3[5];
        readonly float[] smokeLengths = new float[5];

        void Awake()
        {
            instance = this;
            Camera.onPreRender += OnCameraPreRender;
        }

        void OnDestroy()
        {
            Camera.onPreRender -= OnCameraPreRender;
            if (instance == this) instance = null;
            Release(tracerGroups);
            Release(smokeGroups);
        }

        static void Release(Dictionary<Texture, Group> groups)
        {
            foreach (var group in groups.Values)
            {
                group.buffer?.Release();
                if (group.material != null) Destroy(group.material);
            }
            groups.Clear();
        }

        static void OnCameraPreRender(Camera camera)
        {
            Shader.SetGlobalFloat(camScaleID, TargetingCamera.IsTGPCamera(camera) ? 4f : 1f);
        }

        void LateUpdate()
        {
            if (!Active) return;
            var bullets = PooledBulletManager.ActiveBullets;
            if (bullets == null) return;
            foreach (var group in tracerGroups.Values) group.count = 0;
            foreach (var group in smokeGroups.Values) group.count = 0;
            foreach (var bullet in bullets)
            {
                if (bullet == null || !bullet.gameObject.activeInHierarchy || !bullet.gpuTracer) continue;
                AddBullet(bullet);
            }
            Draw(tracerGroups, 3001);
            Draw(smokeGroups, 3000);
        }

        void AddBullet(PooledBullet bullet)
        {
            Vector3 anchor = bullet.currentPosition;
            if (bullet.tracerTexture != null)
            {
                // Stretched along the two points, start width at the head.
                var line = bullet.LinePositions;
                float scale = bullet.randomWidthScale;
                GetGroup(tracerGroups, bullet.tracerTexture).Add(new Segment
                {
                    p0 = line[0], w0 = bullet.tracerStartWidth * scale,
                    p1 = line[1], w1 = bullet.tracerEndWidth * scale,
                    anchor = anchor,
                    tint = bullet.TracerTint,
                    uv = new Vector4(0f, 1f, 0f, 0f),
                });
            }
            if (bullet.SmokeTrailVisible && bullet.smokeTexture != null)
            {
                // Tiled along its length in world units (texture scale 0.1, scrolling offset), width interpolated along the length.
                var points = bullet.SmokePositions;
                float total = 0f;
                smokeLengths[0] = 0f;
                for (int i = 0; i < points.Length; ++i)
                {
                    smokePoints[i] = points[i];
                    if (i > 0) smokeLengths[i] = total += (points[i] - points[i - 1]).magnitude;
                }
                if (total <= 0f) return;
                float startWidth = bullet.tracerStartWidth / 4f, endWidth = bullet.tracerEndWidth / 4f, offset = bullet.SmokeTextureOffset;
                var group = GetGroup(smokeGroups, bullet.smokeTexture);
                Color tint = bullet.SmokeTint;
                for (int i = 0; i < points.Length - 1; ++i)
                {
                    if (smokeLengths[i + 1] - smokeLengths[i] <= 1e-4f) continue;
                    group.Add(new Segment
                    {
                        p0 = smokePoints[i], w0 = Mathf.Lerp(startWidth, endWidth, smokeLengths[i] / total),
                        p1 = smokePoints[i + 1], w1 = Mathf.Lerp(startWidth, endWidth, smokeLengths[i + 1] / total),
                        anchor = anchor,
                        tint = tint,
                        uv = new Vector4(smokeLengths[i] * 0.1f + offset, smokeLengths[i + 1] * 0.1f + offset, 0f, 0f),
                    });
                }
            }
        }

        /// <summary>The group's segments' extent, padded for the quads' camera-scaled width.</summary>
        static Bounds Bounds(Group group)
        {
            Vector3 min = group.segments[0].p0, max = min;
            float width = 0f;
            for (int i = 0; i < group.count; ++i)
            {
                ref var s = ref group.segments[i];
                min = Vector3.Min(min, Vector3.Min(s.p0, s.p1));
                max = Vector3.Max(max, Vector3.Max(s.p0, s.p1));
                width = Mathf.Max(width, Mathf.Max(s.w0, s.w1));
            }
            var bounds = new Bounds((min + max) * 0.5f, max - min);
            bounds.Expand(width * 4f * 3000f / 50f * 3f); // Shader width factor at most (fov / 60) * 4 * 3000 / 50.
            return bounds;
        }

        static Group GetGroup(Dictionary<Texture, Group> groups, Texture texture)
        {
            if (!groups.TryGetValue(texture, out var group))
            {
                group = new Group { material = new Material(GpuAssets.Tracer) { mainTexture = texture } };
                groups[texture] = group;
            }
            return group;
        }

        static void Draw(Dictionary<Texture, Group> groups, int renderQueue)
        {
            foreach (var group in groups.Values)
            {
                if (group.count == 0) continue;
                if (group.buffer == null || group.buffer.count < group.count)
                {
                    group.buffer?.Release();
                    group.buffer = new ComputeBuffer(group.segments.Length, SegmentStride);
                    group.material.SetBuffer(segmentsID, group.buffer);
                }
                group.buffer.SetData(group.segments, 0, 0, group.count);
                group.material.renderQueue = renderQueue;
                Graphics.DrawProcedural(group.material, Bounds(group), MeshTopology.Triangles, group.count * 6, 1, null, null, ShadowCastingMode.Off, false, TracerLayer);
            }
        }
    }
}
