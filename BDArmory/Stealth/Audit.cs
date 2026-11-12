using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BDArmory.CounterMeasure;
using BDArmory.Radar;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDAStealth
{
    /// <summary>
    /// Install-wide audit of every radar, radar seeker, IR missile, ECM part and countermeasure dispenser (post-ModuleManager
    /// prefabs), with stock vs current ranges and flags for configs that still behave all-or-nothing.
    /// </summary>
    internal static class Audit
    {
        static readonly float[] rcs = { 10f, 1f, 0.1f };

        /// <summary>Range below which a curve requires 0 m², i.e. locks/detects anything regardless of RCS or ECM.</summary>
        static float AssuredRange(FloatCurve c, float scale)
        {
            if (!Curves.Range(c, out float tMin, out float tMax)) return 0f;
            FloatCurve o = Curves.Original(c);
            float r = 0f;
            for (int i = 0; i <= 200; i++) { float t = tMin + (tMax - tMin) * i / 200f; if (o.Evaluate(t) <= 0f) r = t; else break; }
            return r * scale;
        }

        public static string Write()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"BDAStealth install audit {DateTime.Now:yyyy-MM-dd HH:mm:ss}  rules: {(Settings.Enabled ? Settings.Preset.ToString() : "stock")} n={Settings.RangeExponent} limit=×{Settings.StealthLimit} flareChance={Settings.FlareChance}");
            sb.AppendLine("All rules apply to every part below automatically; FLAGS mark configs that still give all-or-nothing results.");
            int flagged = 0;

            List<Sensor> all = Sensors.All(null);
            foreach (bool seekers in new[] { false, true })
            {
                sb.AppendLine();
                sb.AppendLine(seekers ? "=== RADAR SEEKERS (lock range; 300 ECM)  stock → now" : "=== RADARS (seen / lock; 300 ECM)  stock → now");
                foreach (Sensor s in all.Where(x => x.IsSeeker == seekers))
                {
                    var cells = new List<string>();
                    foreach (float r in rcs)
                    {
                        Ranges g = Analysis.Evaluate(s, Target.Plain(r)), e = Analysis.Evaluate(s, Target.Plain(r, 300f));
                        cells.Add(seekers
                            ? $"{r}m² {Analysis.Km(g.StockLock)}→{Analysis.Km(g.Lock)} (ECM {Analysis.Km(e.StockLockEcm)}→{Analysis.Km(e.LockEcm)})"
                            : $"{r}m² {Analysis.Km(g.StockDetect)}/{Analysis.Km(g.StockLock)}→{Analysis.Km(g.Detect)}/{Analysis.Km(g.Lock)} (ECM {Analysis.Km(e.StockLockEcm)}→{Analysis.Km(e.LockEcm)})");
                    }
                    var flags = new List<string>();
                    if (s.Scale != 1f) flags.Add("km-authored curve (stock range-blind) fixed");
                    if (s.Lock != null && s.CanLock) { float a = AssuredRange(s.Lock, s.Scale) * s.ToKm; if (a > 0.5f) flags.Add($"config locks ANY target inside {a:0.#} km, ECM included" + (Settings.Enabled && Settings.NoAssuredLock ? " -> replaced by falloff" : "")); }
                    if (s.IsSeeker && s.Lock != null && Curves.Range(s.Lock, out _, out float tm) && tm * s.Scale < s.LockMax * 0.5f) flags.Add($"curve ends at {tm * s.Scale * s.ToKm:0.#} km, seeker range {s.LockMax * s.ToKm:0} km");
                    if (flags.Count > 0) flagged++;
                    sb.AppendLine($"{s.Name}: {string.Join(" | ", cells)}{(flags.Count > 0 ? "   FLAGS: " + string.Join("; ", flags) : "")}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== IR MISSILES  (flareEffectivity: ×per-flare decoy chance; 1 = normal)");
            var seen = new HashSet<string>();
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
                foreach (MissileLauncher m in ap.partPrefab?.FindModulesImplementing<MissileLauncher>() ?? new List<MissileLauncher>())
                {
                    Sensors.Parse(m);
                    if (m.TargetingMode != MissileBase.TargetingModes.Heat && m.TargetingModeTerminal != MissileBase.TargetingModes.Heat) continue;
                    string name = ap.title + "";
                    if (!seen.Add(name)) continue;
                    float fe = Settings.Enabled ? Mathf.Max(m.flareEffectivity, Settings.CmFloor) : m.flareEffectivity;
                    float p1 = StealthMath.FlareCapture(1f, Settings.FlareChance, fe, 0f), p07 = StealthMath.FlareCapture(0.7f, Settings.FlareChance, fe, 0f);
                    var flags = new List<string>();
                    if (m.flareEffectivity <= 0.05f) flags.Add("flare-immune (flareEffectivity ~0)" + (Settings.Enabled && Settings.CmFloor > 0f ? $" -> floored to {Settings.CmFloor:0.##}" : ""));
                    else if (fe >= 3f) flags.Add("decoyed by almost any flare");
                    if (flags.Count > 0) flagged++;
                    sb.AppendLine($"{name}: flareEffectivity {fe:0.##}, heatThreshold {m.heatThreshold:0}, FOV {m.lockedSensorFOV:0}°, allAspect {m.allAspect} → per-flare decoy chance {100f * p07:0}% (0.7× heat) / {100f * p1:0}% (equal heat){(flags.Count > 0 ? "   FLAGS: " + string.Join("; ", flags) : "")}");
                }

            sb.AppendLine();
            sb.AppendLine("=== RADAR MISSILES vs CHAFF  (chaffEffectivity: ×position error from chaff and ×per-bundle decoy chance; 1 = normal)");
            if (Settings.Enabled && Settings.ChaffSeduction)
                sb.AppendLine($"per-bundle decoy chance at chaffEffectivity 1: 1 m² beaming {100f * StealthMath.ChaffSeduction(Settings.ChaffChance, 1f, 0f, 1f, 1f):0}% / head-on {100f * StealthMath.ChaffSeduction(Settings.ChaffChance, 1f, 0f, 0f, 1f):0}%, 10 m² beaming {100f * StealthMath.ChaffSeduction(Settings.ChaffChance, 1f, 0f, 1f, 10f):0}%, 0.1 m² beaming {100f * StealthMath.ChaffSeduction(Settings.ChaffChance, 1f, 0f, 1f, 0.1f):0}%");
            seen.Clear();
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
                foreach (MissileLauncher m in ap.partPrefab?.FindModulesImplementing<MissileLauncher>() ?? new List<MissileLauncher>())
                {
                    Sensors.Parse(m);
                    if (!Sensors.HasRadarSeeker(m) && m.TargetingMode != MissileBase.TargetingModes.Radar) continue;
                    if (!seen.Add(ap.title + "")) continue;
                    if (m.chaffEffectivity <= 0.05f || m.chaffEffectivity >= 3f) { flagged++; sb.AppendLine($"{ap.title}: chaffEffectivity {m.chaffEffectivity:0.##}   FLAGS: {(m.chaffEffectivity <= 0.05f ? "chaff-immune" + (Settings.Enabled && Settings.CmFloor > 0f ? $" -> floored to {Settings.CmFloor:0.##}" : "") : "chaff-dominated")}"); }
                }
            sb.AppendLine("(only outliers listed)");

            sb.AppendLine();
            sb.AppendLine("=== ECM PARTS  (lockbreak: lock signal ×(burn-through factor) on 1 m² at 5/10/30 km)");
            seen.Clear();
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
                foreach (ModuleECMJammer j in ap.partPrefab?.FindModulesImplementing<ModuleECMJammer>() ?? new List<ModuleECMJammer>())
                {
                    if (!seen.Add(ap.title + "")) continue;
                    var flags = new List<string>();
                    if (j.rcsOverride > 0f) flags.Add($"rcsOverride {j.rcsOverride} m² replaces the craft's RCS");
                    if (j.rcsReduction && j.rcsReductionFactor <= 0f) flags.Add("rcsReductionFactor 0 (stock: invisible; now limited)");
                    if (j.lockBreaker && j.lockBreakerStrength >= 1000f) flags.Add("very strong lockbreaker");
                    if (flags.Count > 0) flagged++;
                    string lb = j.lockBreaker ? $"lockbreak {j.lockBreakerStrength:0} (×{StealthMath.JamFactor(j.lockBreakerStrength, 1f, 5f, Settings.BurnThroughKm):0.00}/×{StealthMath.JamFactor(j.lockBreakerStrength, 1f, 10f, Settings.BurnThroughKm):0.00}/×{StealthMath.JamFactor(j.lockBreakerStrength, 1f, 30f, Settings.BurnThroughKm):0.00})" : "no lockbreak";
                    sb.AppendLine($"{ap.title}: noise {(j.signalSpam ? j.jammerStrength : 0f):0}, {lb}, RCS ×{(j.rcsReduction ? j.rcsReductionFactor : 1f):0.##}{(j.alwaysOn ? ", always on" : "")}{(flags.Count > 0 ? "   FLAGS: " + string.Join("; ", flags) : "")}");
                }

            sb.AppendLine();
            sb.AppendLine("=== COUNTERMEASURE DISPENSERS");
            seen.Clear();
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
                foreach (CMDropper d in ap.partPrefab?.FindModulesImplementing<CMDropper>() ?? new List<CMDropper>())
                    if (seen.Add(ap.title + "")) sb.AppendLine($"{ap.title}: {d.countermeasureType}");

            sb.Insert(0, $"FLAGGED: {flagged}\n");
            string dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BDAStealth", "PluginData");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "install_audit.txt");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[BDAStealth] Install audit written ({flagged} flagged): {path}");
            return path;
        }
    }
}
