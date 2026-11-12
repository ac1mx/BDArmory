using System.Collections.Generic;
using BDArmory.CounterMeasure;
using BDArmory.Radar;
using BDArmory.UI;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>Makes BDArmory's editor RCS window report ranges under the active rules, with ECM and stock comparisons.</summary>
    internal static class EditorPatches
    {
        // Craft ECM summary, refreshed on each snapshot.
        static readonly List<float> factors = new List<float>();
        static float jam, lockBreak, rcsOverride, stockReduction = 1f;

        /// <summary>Scan the editor craft's ECM / stealth parts (all treated as switched on).</summary>
        public static void ScanCraft(IEnumerable<Part> parts)
        {
            factors.Clear(); jam = 0f; lockBreak = 0f; rcsOverride = -1f;
            float jw = 1f, lw = 1f;
            if (parts == null) return;
            foreach (Part p in parts)
            {
                if (p == null) continue;
                foreach (ModuleECMJammer j in p.FindModulesImplementing<ModuleECMJammer>())
                {
                    if (j.signalSpam) { jam += jw * j.jammerStrength; jw *= 0.75f; }
                    if (j.lockBreaker) { lockBreak += lw * j.lockBreakerStrength; lw *= 0.65f; }
                    if (j.rcsReduction) { factors.Add(j.rcsReductionFactor); rcsOverride = Mathf.Max(rcsOverride, j.rcsOverride); }
                }
            }
            stockReduction = StealthMath.CombineReduction(new List<float>(factors), false, 0f);
        }

        /// <summary>The scanned craft as a range-analysis target; baseRcs is the rendered RCS (coatings included).</summary>
        public static Target CraftTarget(float baseRcs, float gcf = 1f) => new Target
        {
            BaseRcs = baseRcs * gcf,
            Override = rcsOverride > 0f ? rcsOverride * gcf : -1f,
            Coating = Patches.EditorCoating,
            Parts = Patches.CombineParts(new List<float>(factors)),
            StockParts = stockReduction,
            Jam = jam,
            LockBreak = lockBreak,
        };

        public static bool CraftHasEcm => jam > 0f || lockBreak > 0f;
        public static float CraftRcs => RadarUtils.rcsTotal;

        internal static void SnapshotPostfix(BDAEditorAnalysisWindow window)
        {
            ScanCraft(EditorLogic.fetch?.ship?.Parts);
            Target t = CraftTarget(1f);
            window.rcsReductionFactor = Settings.Enabled && Settings.ApplyRcsReduction ? t.Reduction : (Settings.Enabled ? 1f : stockReduction);
            lastVersion = -1;
        }

        static int lastIndex = -2, lastVersion = -1; static bool lastLanded; static float lastRcs = -1f;
        static string line3 = "", line4 = "", det = "", lck = "";

        internal static void WindowPostfix(BDAEditorAnalysisWindow window)
        {
            if (window.windowRect.height < 540f) window.windowRect.height = 540f;

            int idx = window.previous_index;
            ModuleRadar[] radars = window.radars;
            if (radars == null || idx < 0 || idx >= radars.Length) return;
            bool landed = window.bLandedSplashed;
            float rcs = RadarUtils.rcsTotal;
            if (idx != lastIndex || landed != lastLanded || rcs != lastRcs || Settings.Version != lastVersion)
            {
                lastIndex = idx; lastLanded = landed; lastRcs = rcs; lastVersion = Settings.Version;
                ModuleRadar radar = radars[idx];
                Sensor sensor = Sensors.Of(radar);
                float gcf = landed ? radar.radarGroundClutterFactor : 1f;
                Target t = CraftTarget(rcs, gcf);
                Ranges g = Analysis.Evaluate(sensor, t);
                bool ecm = CraftHasEcm;
                det = radar.canScan ? $"Detection: {Analysis.Km(g.Detect)}" + (ecm && jam > 0f ? $"   ECM on: {Analysis.Km(g.DetectEcm)}" : "") : "Detection: this radar cannot scan.";
                lck = radar.canLock ? $"Lock: {Analysis.Km(g.Lock)}   held to {Analysis.Km(g.Hold)}" + (ecm ? $"   ECM on: {Analysis.Km(g.LockEcm)}" : "") : "Lock: this radar cannot lock.";
                line3 = $"Stock BDArmory: detect {Analysis.Km(g.StockDetect)} · lock {Analysis.Km(g.StockLock)}" + (ecm ? $" · ECM on: {Analysis.Km(g.StockLockEcm)}" : "");
                Ranges refR = Analysis.Evaluate(sensor, Target.Plain(Settings.ReferenceRcs));
                float shown = t.Effective;
                string limit = Settings.Enabled && t.Coating * t.Parts < Settings.StealthLimit * 1.001f ? " (at stealth limit)" : "";
                line4 = refR.Detect > 0f && radar.canScan
                    ? $"Seen at {100f * g.Detect / refR.Detect:0}% of a {Settings.ReferenceRcs:0.#} m² craft's range. Effective RCS {Analysis.Rcs(shown)}{limit}."
                    : $"Effective RCS {Analysis.Rcs(shown)}{limit}.";
            }
            window.text_detection = det;
            window.text_locktrack = lck;
            GUIStyle label = BDArmorySetup.BDGuiSkin.label;
            GUI.Label(new Rect(10f, 475f, 640f, 20f), line3, label);
            GUI.Label(new Rect(10f, 495f, 640f, 20f), line4, label);
            GUI.Label(new Rect(10f, 515f, 640f, 20f), Settings.Enabled ? $"Rules: {Settings.Preset} (toolbar: Radar & Stealth)" : "Rules: stock BDArmory (stealth rules disabled)", label);
        }
    }
}
