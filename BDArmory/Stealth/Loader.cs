using System;
using System.Linq;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>Loads the radar and stealth rules at startup and writes the sensor report and install audit at the main menu.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Loader : MonoBehaviour
    {
        static bool reported;

        void Awake()
        {
            Settings.Load();
            Debug.Log($"[BDAStealth] Loaded (built into BDArmory). enabled={Settings.Enabled} preset={Settings.Preset} n={Settings.RangeExponent} refRcs={Settings.ReferenceRcs} burnThrough={Settings.BurnThroughKm}km hold={Settings.TrackHold} limit={Settings.StealthLimit}");
            GameEvents.onLevelWasLoaded.Add(OnLevel);
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>One line per sensor class at the main menu, so the log shows the rules working on this install's real parts.</summary>
        void OnLevel(GameScenes s)
        {
            if (s != GameScenes.MAINMENU || reported) return;
            reported = true;
            try
            {
                var all = Sensors.All(null);
                int radars = all.Count(x => !x.IsSeeker), seekers = all.Count(x => x.IsSeeker), scaled = all.Count(x => x.Scale != 1f);
                Debug.Log($"[BDAStealth] Sensors: {radars} radars, {seekers} radar seekers ({scaled} km-authored seeker curves rescaled)");
                foreach (Sensor x in new[] { all.FirstOrDefault(r => r.Name.Contains("APG-63")), all.FirstOrDefault(r => r.IsSeeker && r.Scale != 1f), all.FirstOrDefault(r => r.IsSeeker && r.Scale == 1f) })
                {
                    if (x == null) continue;
                    string row(float rcs) { Ranges g = Analysis.Evaluate(x, Target.Plain(rcs, 300f)); return $"{rcs} m²: seen {Analysis.Km(g.Detect)}/stock {Analysis.Km(g.StockDetect)}, lock {Analysis.Km(g.Lock)}/stock {Analysis.Km(g.StockLock)}, vs ECM {Analysis.Km(g.LockEcm)}/stock {Analysis.Km(g.StockLockEcm)}"; }
                    Debug.Log($"[BDAStealth] {x.Name}: {row(10f)} | {row(1f)} | {row(0.1f)}");
                }
            }
            catch (Exception e) { Debug.LogError("[BDAStealth] Sensor report failed: " + e); }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Audit.Write();
                Debug.Log($"[BDAStealth] Install audit took {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e) { Debug.LogError("[BDAStealth] Install audit failed: " + e); }
        }
    }
}
