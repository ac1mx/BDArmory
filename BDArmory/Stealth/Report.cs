using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BDArmory.Control;
using BDArmory.CounterMeasure;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.Targeting;
using BDArmory.UI;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>
    /// Text report of every loaded combat craft: rendered RCS, aspect RCS, coatings, stealth parts, ECM, and ranges against
    /// key sensors under stock and current rules. Written from the window, or automatically when PluginData/probe.cfg exists.
    /// </summary>
    internal static class Report
    {
        static readonly string Dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BDAStealth", "PluginData");

        static readonly (string name, float az, float el)[] aspects =
        {
            ("nose", 0f, 0f), ("nose+30", 30f, 0f), ("beam", 90f, 0f), ("tail", 180f, 0f), ("above", 0f, 90f), ("below", 0f, -90f),
        };

        /// <summary>Craft as a target with ECM off or with every ECM part switched on.</summary>
        static Target Of(Vessel v, TargetInfo ti, float rcs, bool ecmOn)
        {
            var t = Target.Plain(rcs);
            t.Coating = Patches.Coating(v);
            if (!ecmOn) return t;
            var factors = new List<float>();
            float jw = 1f, lw = 1f;
            foreach (Part p in v.parts)
                foreach (ModuleECMJammer j in p.FindModulesImplementing<ModuleECMJammer>())
                {
                    if (j.signalSpam) { t.Jam += jw * j.jammerStrength; jw *= 0.75f; }
                    if (j.lockBreaker) { t.LockBreak += lw * j.lockBreakerStrength; lw *= 0.65f; }
                    if (j.rcsReduction) factors.Add(j.rcsReductionFactor);
                }
            t.StockParts = StealthMath.CombineReduction(new List<float>(factors), false, 0f);
            t.Parts = Patches.CombineParts(factors);
            return t;
        }

        static string Rng(Ranges g, bool seeker) => seeker
            ? $"lock {Analysis.Km(g.Lock),8} (stock {Analysis.Km(g.StockLock),8})"
            : $"seen {Analysis.Km(g.Detect),8} (stock {Analysis.Km(g.StockDetect),8})  lock {Analysis.Km(g.Lock),8} (stock {Analysis.Km(g.StockLock),8})";

        static string RngEcm(Ranges g, bool seeker) => seeker
            ? $"lock {Analysis.Km(g.LockEcm),8} (stock {Analysis.Km(g.StockLockEcm),8})"
            : $"seen {Analysis.Km(g.DetectEcm),8} (stock {Analysis.Km(g.StockDetect),8})  lock {Analysis.Km(g.LockEcm),8} (stock {Analysis.Km(g.StockLockEcm),8})";

        public static string Write()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"BDAStealth RCS report {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Rules: {(Settings.Enabled ? Settings.Preset.ToString() : "stock")}  n={Settings.RangeExponent} ref={Settings.ReferenceRcs} m² burn-through={Settings.BurnThroughKm} km hold=×{Settings.TrackHold} limit=×{Settings.StealthLimit}");
            sb.AppendLine($"BDA: ASPECTED_RCS={BDArmorySettings.ASPECTED_RCS} weight={BDArmorySettings.ASPECTED_RCS_OVERALL_RCS_WEIGHT} NOTCHING={BDArmorySettings.RADAR_NOTCHING} SCR={BDArmorySettings.RADAR_NOTCHING_SCR_FACTOR} CHAFF={BDArmorySettings.CHAFF_FACTOR}");
            sb.AppendLine("Ranges: head-on air-to-air, no ground clutter or notching. 'ECM on' = every ECM part on the craft switched on.");

            var all = Sensors.All(null);
            var sensors = new List<Sensor>();
            foreach (string key in new[] { "AESA Radar", "AIM-120D", "EU METEOR", "MBDA Meteor", "APG-63V1", "KAPG-77v1 Radar" })
            {
                Sensor s = all.FirstOrDefault(x => x.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                if (s != null && !sensors.Contains(s)) sensors.Add(s);
            }
            sb.AppendLine("Sensors: " + string.Join(", ", sensors.Select(s => s.Name + (s.IsSeeker ? $" (seeker {s.LockMax / 1000f:0} km, curve ×{s.Scale:0})" : ""))));

            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || !v.loaded || v.vesselType == VesselType.Debris) continue;
                MissileFire wm = v.ActiveController()?.WM;
                if (wm == null) continue;
                // Stock render first (no per-part reflectivity limit), then the current-rules render that stays live.
                var one = new List<Vessel> { v };
                float stockBase; float[,] stockM;
                try { Patches.RenderStock = true; RadarUtils.ForceUpdateRadarCrossSections(one, allowDeferredRead: false); }
                finally { Patches.RenderStock = false; }
                TargetInfo ti = RadarUtils.GetVesselRadarSignature(v);
                if (ti == null) continue;
                stockBase = ti.radarBaseSignature; stockM = (float[,])ti.radarSignatureMatrix?.Clone();
                RadarUtils.ForceUpdateRadarCrossSections(one, allowDeferredRead: false);
                ti = RadarUtils.GetVesselRadarSignature(v);
                float baseRcs = ti.radarBaseSignature;
                sb.AppendLine();
                sb.AppendLine($"=== {v.vesselName}  ({v.parts.Count} parts)");
                Target off = Of(v, ti, baseRcs, false), on = Of(v, ti, baseRcs, true);
                sb.AppendLine($"Rendered all-aspect RCS {Analysis.Rcs(baseRcs)} (stock render {Analysis.Rcs(stockBase)}; BDA 75th percentile; skin reflectivity ×{off.Coating:0.00} after limit)");
                sb.AppendLine($"ECM on: stealth parts ×{on.Parts:0.####} (stock stacking ×{on.StockParts:0.##########}) → effective ×{on.Reduction:0.###} = {Analysis.Rcs(on.Effective)}; noise {on.Jam:0} (+{on.Jam / 100f:0.#} m² when revealing), lockbreak {on.LockBreak:0}");

                float[,] m = ti.radarSignatureMatrix;
                if (m != null)
                {
                    var vals = new List<float>();
                    for (int i = 0; i < m.GetLength(0); i++) vals.Add(m[i, 2]);
                    vals.Sort();
                    sb.AppendLine($"Aspect samples ({vals.Count}): min {Analysis.Rcs(vals[0])}, median {Analysis.Rcs(vals[vals.Count / 2])}, max {Analysis.Rcs(vals[vals.Count - 1])}");
                    var raw = new StringBuilder("Aspect matrix (az,el,m² new,m² stock):");
                    for (int i = 0; i < m.GetLength(0); i++) raw.Append($" {m[i, 0]:0.##},{m[i, 1]:0.##},{m[i, 2]:0.####},{(stockM != null && i < stockM.GetLength(0) ? stockM[i, 2] : -1f):0.####}");
                    sb.AppendLine(raw.ToString());
                }
                var aspectRcs = new List<(string, float, float)>();
                if (m != null && BDArmorySettings.ASPECTED_RCS)
                    foreach (var a in aspects)
                        aspectRcs.Add((a.name, RadarUtils.RCSMatrixEval(m, baseRcs, a.az, a.el), stockM != null ? RadarUtils.RCSMatrixEval(stockM, stockBase, a.az, a.el) : stockBase));
                else aspectRcs.Add(("all", baseRcs, stockBase));
                sb.AppendLine("Aspect RCS as radars see it (blended with overall), new / stock render: " + string.Join(", ", aspectRcs.Select(x => $"{x.Item1} {Analysis.Rcs(x.Item2)} / {Analysis.Rcs(x.Item3)}")));

                foreach (Sensor s in sensors)
                {
                    sb.AppendLine($"  vs {s.Name}:");
                    foreach (var (name, rcs, stockRcs) in aspectRcs)
                    {
                        if (name == "above" || name == "below" || name == "nose+30") continue;
                        Target ta = Of(v, ti, rcs, false), tb = Of(v, ti, rcs, true);
                        ta.StockBaseRcs = tb.StockBaseRcs = stockRcs;
                        Ranges a = Analysis.Evaluate(s, ta);
                        Ranges b = Analysis.Evaluate(s, tb);
                        sb.AppendLine($"    {name,-5} ECM off: {Rng(a, s.IsSeeker)}");
                        sb.AppendLine($"    {name,-5} ECM on : {RngEcm(b, s.IsSeeker)}");
                    }
                }
            }
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, $"rcs_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[BDAStealth] RCS report written: " + path);
            return path;
        }
    }

    /// <summary>Automatic report for unattended runs: only active when PluginData/probe.cfg exists.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ReportProbe : MonoBehaviour
    {
        float readyAt = -1f; bool done, quit;
        int minVessels = 2; float delay = 20f;

        void Start()
        {
            string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BDAStealth", "PluginData", "probe.cfg");
            ConfigNode n = File.Exists(path) ? ConfigNode.Load(path)?.GetNode("PROBE") : null;
            if (n == null) { Destroy(this); return; }
            n.TryGetValue("quit", ref quit); n.TryGetValue("minVessels", ref minVessels); n.TryGetValue("delay", ref delay);
        }

        void Update()
        {
            if (done) return;
            int count = 0;
            foreach (Vessel v in FlightGlobals.Vessels) if (v != null && v.loaded && v.ActiveController()?.WM != null) count++;
            if (count < minVessels) return;
            if (readyAt < 0f) readyAt = Time.realtimeSinceStartup + delay;
            if (Time.realtimeSinceStartup < readyAt) return;
            done = true;
            try { Report.Write(); } catch (Exception e) { Debug.LogError("[BDAStealth] Report failed: " + e); }
            if (quit) Application.Quit();
        }
    }
}
