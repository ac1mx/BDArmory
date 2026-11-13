using System.Collections.Generic;
using UnityEngine;

namespace BDArmory.GPU
{
    /// <summary>
    /// White-hot thermal image for the targeting camera: the near and far cameras render with BDArmory/GPU/Thermal as a
    /// replacement shader. Each part's heat (skin temperature over ambient, running engines hot) reaches the shader through the
    /// part renderers' property blocks, refreshed a slice per frame and only where it changed.
    /// </summary>
    public static class ThermalView
    {
        public static bool Available => GpuAssets.Thermal != null;
        /// <summary>Off: part heat is no longer refreshed (diagnostics: cost of the refresh).</summary>
        public static bool HeatUpdates = true;
        const int RenderersPerFrame = 256;
        const float RescanInterval = 2f;
        const float MaxDistance = 30000f;
        static readonly int heatID = Shader.PropertyToID("_BDHeat"), sunID = Shader.PropertyToID("_BDThermalSun"), sceneID = Shader.PropertyToID("_BDThermalScene");
        static readonly List<Renderer> renderers = new List<Renderer>();
        static readonly List<Part> owners = new List<Part>();
        static readonly List<float> applied = new List<float>();
        static readonly Dictionary<Part, float> heatThisPass = new Dictionary<Part, float>();
        static MaterialPropertyBlock block;
        static float nextScan;
        static int cursor;

        /// <summary>Render the thermal image into the cameras' target. False if the shader isn't available.</summary>
        public static bool Render(Camera[] cameras, bool[] enabled, RenderTexture target)
        {
            if (!Available || cameras == null || cameras[0] == null) return false;
            Vector3 position = cameras[0].transform.position;
            if (HeatUpdates) UpdateHeat(position);

            Vector3 sunDirection = Vector3.up;
            float daylight = 0f;
            var sun = FlightGlobals.Bodies.Count > 0 ? FlightGlobals.Bodies[0] : null;
            var body = FlightGlobals.currentMainBody;
            if (sun != null)
            {
                sunDirection = ((Vector3)(sun.position - (Vector3d)position)).normalized;
                if (body != null) daylight = Mathf.Clamp01(Vector3.Dot(sunDirection, ((Vector3)((Vector3d)position - body.position)).normalized) * 4f + 0.5f);
            }
            Shader.SetGlobalVector(sunID, new Vector4(sunDirection.x, sunDirection.y, sunDirection.z, daylight));
            Shader.SetGlobalVector(sceneID, new Vector4(0.16f, 0.14f, 0.34f, 0.08f));

            // The sky is cold: start from black and leave the sky and galaxy cameras out.
            var active = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = active;
            // Own cameras copied from the targeting camera's: no image effects or other mods' per-camera passes (Scatterer's
            // atmosphere tinted the whole thermal image).
            if (cameras[1] != null && enabled[1]) Proxy(ref farProxy, cameras[1], "BDArmory Thermal Far").RenderWithShader(GpuAssets.Thermal, "RenderType");
            if (cameras[0] != null && enabled[0]) Proxy(ref nearProxy, cameras[0], "BDArmory Thermal Near").RenderWithShader(GpuAssets.Thermal, "RenderType");
            return true;
        }

        static Camera nearProxy, farProxy;

        static Camera Proxy(ref Camera proxy, Camera source, string name)
        {
            if (proxy == null)
            {
                proxy = new GameObject(name).AddComponent<Camera>();
                proxy.enabled = false;
            }
            proxy.CopyFrom(source); // Settings, target, culling mask and pose.
            proxy.clearFlags = CameraClearFlags.Depth;
            proxy.allowHDR = false;
            proxy.allowMSAA = false;
            proxy.depthTextureMode = DepthTextureMode.None;
            return proxy;
        }

        static void UpdateHeat(Vector3 position)
        {
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + RescanInterval;
                Rescan(position);
            }
            if (renderers.Count == 0) return;
            if (block == null) block = new MaterialPropertyBlock();
            heatThisPass.Clear();
            int count = Mathf.Min(RenderersPerFrame, renderers.Count);
            for (int n = 0; n < count; ++n)
            {
                if (cursor >= renderers.Count) cursor = 0;
                int i = cursor++;
                var r = renderers[i];
                var part = owners[i];
                if (r == null || part == null) continue;
                if (!heatThisPass.TryGetValue(part, out float heat)) heatThisPass[part] = heat = PartHeat(part);
                if (Mathf.Abs(heat - applied[i]) < 0.02f) continue;
                applied[i] = heat;
                r.GetPropertyBlock(block);
                block.SetFloat(heatID, heat + 1f); // Stored +1: renderers that aren't parts read 0, i.e. below zero in the shader.
                r.SetPropertyBlock(block);
            }
        }

        static void Rescan(Vector3 position)
        {
            renderers.Clear(); owners.Clear(); applied.Clear();
            float maxSqr = MaxDistance * MaxDistance;
            var vessels = FlightGlobals.VesselsLoaded;
            for (int v = 0; v < vessels.Count; ++v)
            {
                var vessel = vessels[v];
                if (vessel == null || vessel.parts == null || (vessel.CoM - position).sqrMagnitude > maxSqr) continue;
                for (int p = 0; p < vessel.parts.Count; ++p)
                {
                    var part = vessel.parts[p];
                    if (part == null || part.HighlightRenderer == null) continue;
                    for (int k = 0; k < part.HighlightRenderer.Count; ++k)
                    {
                        var r = part.HighlightRenderer[k];
                        if (r == null) continue;
                        renderers.Add(r); owners.Add(part); applied.Add(-10f);
                    }
                }
            }
            if (cursor >= renderers.Count) cursor = 0;
        }

        /// <summary>0 at ambient air temperature, 1 for a hot running engine.</summary>
        static float PartHeat(Part part)
        {
            float ambient = part.vessel != null ? (float)part.vessel.externalTemperature : 288f;
            float heat = Mathf.Clamp01(((float)part.skinTemperature - ambient) / 300f);
            for (int m = 0; m < part.Modules.Count; ++m)
                if (part.Modules[m] is ModuleEngines e && e.EngineIgnited && e.isOperational && e.maxThrust > 0f && e.finalThrust > 0f)
                    heat = Mathf.Max(heat, 0.55f + 0.45f * Mathf.Clamp01(e.finalThrust / e.maxThrust));
            return heat;
        }
    }
}
