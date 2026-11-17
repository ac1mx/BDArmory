using System.IO;
using UnityEngine;

using BDArmory.Radar;
using BDArmory.UI;
using BDArmory.Utils;

namespace BDArmory.Testing
{
    /// <summary>
    /// Test runs only: with GameData/BDArmory/PluginData/ui_capture.on present, the weapon manager, AI and radar warning receiver
    /// windows are kept open for the active vessel and a screenshot is written to GameData/BDArmory/Logs/ui_NNN.png every interval
    /// (seconds, the file's content; 20 by default), so that the in-game interface can be looked at after an unattended run.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class UiCapture : MonoBehaviour
    {
        static readonly string flag = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/ui_capture.on");
        float interval = 20f, next;
        int count;

        void Start()
        {
            if (!File.Exists(flag)) { Destroy(this); return; }
            if (float.TryParse(File.ReadAllText(flag).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds) && seconds >= 2f) interval = seconds;
            next = Time.realtimeSinceStartup + 60f;
        }

        bool capturing;

        void Update()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || capturing || Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + interval;
            string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/Logs", $"ui_{++count:000}.png");
            StartCoroutine(Capture(path, vessel));
        }

        /// <summary>
        /// Opens the windows for the shot only (a frame to lay out, the frame drawn), then puts them back as the player had them, so
        /// that they can still be closed while a run is watched.
        /// </summary>
        System.Collections.IEnumerator Capture(string path, Vessel vessel)
        {
            capturing = true;
            bool toolbar = BDArmorySetup.windowBDAToolBarEnabled, ai = BDArmoryAIGUI.windowBDAAIGUIEnabled, settings = BDArmorySetup.windowSettingsEnabled;
            bool switcher = BDArmorySetup.showVesselSwitcherGUI;
            var rwrs = new System.Collections.Generic.List<(RadarWarningReceiver rwr, bool shown)>();
            foreach (var rwr in VesselModuleRegistry.GetModules<RadarWarningReceiver>(vessel)) if (rwr != null) { rwrs.Add((rwr, rwr.displayRWR)); rwr.displayRWR = true; }
            BDArmorySetup.windowBDAToolBarEnabled = true;
            BDArmoryAIGUI.windowBDAAIGUIEnabled = true;
            bool extra = count % 3 == 0; // Every third shot also shows the settings window and the vessel switcher (skin coverage).
            BDArmorySetup.windowSettingsEnabled = extra || settings;
            if (extra && LoadedVesselSwitcher.Instance != null && !switcher) LoadedVesselSwitcher.Instance.SetVisible(true);
            yield return null;
            yield return new WaitForEndOfFrame(); // After the interface has been drawn.
            string vesselName = vessel != null ? vessel.vesselName : "";
            int width = Screen.width, height = Screen.height;
            var shot = new Texture2D(width, height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            byte[] rgb = shot.GetRawTextureData(); // Bottom row first, as a BMP stores it.
            Destroy(shot);
            path = Path.ChangeExtension(path, ".bmp"); // Written on a worker thread: no encoding on the main thread (run_fight converts to PNG).
            System.Threading.ThreadPool.QueueUserWorkItem(_ => WriteBmp(path, rgb, width, height));
            Debug.Log($"[BDArmory.UiCapture]: {path} ({vesselName})");
            BDArmorySetup.windowBDAToolBarEnabled = toolbar;
            BDArmoryAIGUI.windowBDAAIGUIEnabled = ai;
            BDArmorySetup.windowSettingsEnabled = settings;
            if (LoadedVesselSwitcher.Instance != null && BDArmorySetup.showVesselSwitcherGUI != switcher) LoadedVesselSwitcher.Instance.SetVisible(switcher);
            foreach (var (rwr, shown) in rwrs) if (rwr != null) rwr.displayRWR = shown;
            capturing = false;
        }

        internal static void WriteBmp(string path, byte[] rgb, int width, int height)
        {
            int stride = (width * 3 + 3) & ~3, size = 54 + stride * height;
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write((byte)'B'); writer.Write((byte)'M'); writer.Write(size); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((short)1); writer.Write((short)24); writer.Write(0); writer.Write(stride * height);
            writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0);
            var row = new byte[stride];
            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x) { int i = (y * width + x) * 3; row[x * 3] = rgb[i + 2]; row[x * 3 + 1] = rgb[i + 1]; row[x * 3 + 2] = rgb[i]; }
                writer.Write(row);
            }
        }
    }

    /// <summary>
    /// Test runs only: with GameData/BDArmory/PluginData/part_photo.on present (content: part names, comma-separated), each of those
    /// parts still carried by a loaded craft is rendered (at most every 30 s per name) from three sides and with its craft, written to
    /// GameData/BDArmory/Logs/ui_part_*.bmp, so new part models can be looked at after an unattended run.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class PartPhoto : MonoBehaviour
    {
        static readonly string flag = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/part_photo.on");
        string[] names;
        float next;
        int count, turn;
        bool busy;

        void Start()
        {
            if (!File.Exists(flag)) { Destroy(this); return; }
            names = File.ReadAllText(flag).Split(new[] { ',', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < names.Length; ++i) names[i] = names[i].Trim();
            next = Time.realtimeSinceStartup + 45f;
        }

        // Every 2 s look for a listed part still on a craft; photograph each part name at most once per 30 s, round robin.
        float nextLook;
        readonly System.Collections.Generic.Dictionary<string, float> lastShot = new System.Collections.Generic.Dictionary<string, float>();

        void Update()
        {
            if (busy || names == null || names.Length == 0 || Time.realtimeSinceStartup < next || Time.realtimeSinceStartup < nextLook) return;
            nextLook = Time.realtimeSinceStartup + 2f;
            for (int k = 0; k < names.Length; ++k)
            {
                string name = names[(turn + k) % names.Length];
                if (lastShot.TryGetValue(name, out float t) && Time.realtimeSinceStartup - t < 30f) continue;
                foreach (var vessel in FlightGlobals.Vessels)
                {
                    if (vessel == null || !vessel.loaded || vessel.packed) continue;
                    foreach (var part in vessel.parts)
                    {
                        if (part == null || part.partInfo == null || part.partInfo.name != name) continue;
                        var missile = part.FindModuleImplementing<Weapons.Missiles.MissileBase>();
                        if (missile != null && missile.HasFired) continue;
                        lastShot[name] = Time.realtimeSinceStartup;
                        turn = (turn + k + 1) % names.Length;
                        StartCoroutine(Photograph(vessel, part));
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Renders the part through a camera of its own (the flight camera is left alone: camera mods override its target), from three
        /// sides close up and once from further out with the craft carrying it.
        /// </summary>
        System.Collections.IEnumerator Photograph(Vessel vessel, Part part)
        {
            busy = true;
            yield return new WaitForEndOfFrame();
            var main = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (part == null || vessel == null || main == null) { busy = false; yield break; }
            const int width = 1600, height = 1000;
            var go = new GameObject("PartPhotoCamera");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.45f, 0.6f, 0.8f);
            cam.nearClipPlane = 0.05f; cam.farClipPlane = 2000f; cam.fieldOfView = 45f;
            var rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            var bounds = new Bounds(part.transform.position, Vector3.zero);
            foreach (var r in part.GetComponentsInChildren<Renderer>()) if (r != null && r.enabled) bounds.Encapsulate(r.bounds);
            Vector3 center = bounds.center;
            float size = Mathf.Max(bounds.size.magnitude, 0.5f);
            var reference = part.FindModelTransform("missileTransform") ?? part.transform;
            Vector3 f = reference.forward, u = reference.up, r3 = Vector3.Cross(u, f);
            Vector3 up = (vessel.CoM - vessel.mainBody.position).normalized;
            float d = 1.05f * size + 0.5f;
            var views = new[] {
                ("side", center + (r3 - 0.25f * u + 0.05f * f).normalized * d),
                ("front", center + (0.6f * f + 0.5f * r3 - 0.6f * u).normalized * d),
                ("rear", center + (-0.7f * f - 0.4f * r3 - 0.5f * u).normalized * d),
                ("craft", center + (-0.8f * u + 0.5f * r3 - 0.3f * f).normalized * Mathf.Max(14f, 3f * d)) };
            foreach (var (view, position) in views)
            {
                cam.transform.position = position;
                cam.transform.LookAt(center, up);
                cam.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var shot = new Texture2D(width, height, TextureFormat.RGB24, false);
                shot.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                RenderTexture.active = previous;
                byte[] rgb = shot.GetRawTextureData();
                Destroy(shot);
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/Logs", $"ui_part_{++count:000}_{part.partInfo.name}_{view}.bmp");
                System.Threading.ThreadPool.QueueUserWorkItem(_ => UiCapture.WriteBmp(path, rgb, width, height));
                Debug.Log($"[BDArmory.PartPhoto]: {path} ({vessel.vesselName}, {part.partInfo.name}, {view})");
            }
            cam.targetTexture = null;
            rt.Release(); Destroy(rt); Destroy(go);
            busy = false;
        }
    }
}
