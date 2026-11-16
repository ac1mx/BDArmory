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

        void Update()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            BDArmorySetup.windowBDAToolBarEnabled = true;
            BDArmoryAIGUI.windowBDAAIGUIEnabled = true;
            foreach (var rwr in VesselModuleRegistry.GetModules<RadarWarningReceiver>(vessel)) if (rwr != null) rwr.displayRWR = true;
            // Every third shot also shows the settings window and the vessel switcher (skin coverage), closed again for the others.
            bool extra = count % 3 == 2;
            BDArmorySetup.windowSettingsEnabled = extra;
            if (LoadedVesselSwitcher.Instance != null && BDArmorySetup.showVesselSwitcherGUI != extra) LoadedVesselSwitcher.Instance.SetVisible(extra);
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + interval;
            string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/Logs", $"ui_{++count:000}.png");
            StartCoroutine(Capture(path, vessel.vesselName));
        }

        System.Collections.IEnumerator Capture(string path, string vesselName)
        {
            yield return new WaitForEndOfFrame(); // After the interface has been drawn.
            int width = Screen.width, height = Screen.height;
            var shot = new Texture2D(width, height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            byte[] rgb = shot.GetRawTextureData(); // Bottom row first, as a BMP stores it.
            Destroy(shot);
            path = Path.ChangeExtension(path, ".bmp"); // Written on a worker thread: no encoding on the main thread (run_fight converts to PNG).
            System.Threading.ThreadPool.QueueUserWorkItem(_ => WriteBmp(path, rgb, width, height));
            Debug.Log($"[BDArmory.UiCapture]: {path} ({vesselName})");
        }

        static void WriteBmp(string path, byte[] rgb, int width, int height)
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
}
