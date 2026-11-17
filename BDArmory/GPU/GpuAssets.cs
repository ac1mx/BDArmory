using System.IO;
using UnityEngine;

namespace BDArmory.GPU
{
    /// <summary>
    /// Loads GameData/BDArmory/AssetBundles/bdarmorygpu_windows.bundle (built from BDArmory-src/Unity): the compute shaders and
    /// shaders that do work on the GPU which used to be done per object on the main thread. Every user of these falls back to its
    /// CPU path when an asset is missing.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class GpuAssets : MonoBehaviour
    {
        const string BundleName = "bdarmorygpu_windows.bundle";
        public static bool Loaded { get; private set; }
        public static Shader Tracer;
        public static Shader Decal;
        public static Shader Thermal;
        public static Shader RadarScope;
        public static Shader RcsInstanced;
        public static Shader Trail;

        void Awake()
        {
            if (Loaded) return;
            Loaded = true;
            if (Application.platform != RuntimePlatform.WindowsPlayer) return; // The bundle is built for Direct3D 11 only.
            string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BDArmory", "AssetBundles", BundleName);
            if (!File.Exists(path)) { Debug.Log($"[BDArmory.GpuAssets]: {BundleName} not found, using the CPU paths."); return; }
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) { Debug.LogWarning($"[BDArmory.GpuAssets]: Failed to load {BundleName}."); return; }
            foreach (var compute in bundle.LoadAllAssets<ComputeShader>())
            {
                Debug.Log($"[BDArmory.GpuAssets]: Compute shader {compute.name} loaded.");
                if (compute.name == "RcsReduce") RcsGpuReduction.SetShader(compute);
            }
            foreach (var shader in bundle.LoadAllAssets<Shader>())
            {
                Debug.Log($"[BDArmory.GpuAssets]: Shader {shader.name} loaded, supported: {shader.isSupported}.");
                if (!shader.isSupported) continue;
                switch (shader.name)
                {
                    case "BDArmory/GPU/Tracer": Tracer = shader; break;
                    case "BDArmory/GPU/Decal": Decal = shader; break;
                    case "BDArmory/GPU/Thermal": Thermal = shader; break;
                    case "BDArmory/GPU/RadarScope": RadarScope = shader; break;
                    case "BDArmory/GPU/RcsInstanced": RcsInstanced = shader; break;
                    case "BDArmory/GPU/Trail": Trail = shader; break;
                }
            }
            bundle.Unload(false);
        }
    }
}
