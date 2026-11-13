using System;
using System.Collections.Generic;
using BDArmory.CounterMeasure;
using BDArmory.Radar;
using BDArmory.Targeting;
using BDArmory.Weapons.Missiles;
using KSP.UI.Screens;
using UnityEngine;

namespace BDAStealth
{
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class StealthWindow : MonoBehaviour
    {
        static Rect rect = new Rect(260f, 120f, 620f, 10f);
        static bool show, advanced;
        static Texture2D icon;
        ApplicationLauncherButton button;
        readonly int id = "BDAStealth.Window".GetHashCode();

        static readonly float[] previewRcs = { 30f, 10f, 3f, 1f, 0.3f, 0.1f };
        const float PreviewLockBreak = 300f;   // a stock ECM pod (ECMJ-131) lockbreaker
        List<Sensor> sensors = new List<Sensor>();
        int sensorIdx, scannedRenders = -1;
        float nextSave, nextRefresh;

        void Start()
        {
            if (!(HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor || HighLogic.LoadedScene == GameScenes.SPACECENTER)) { Destroy(this); return; }
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            if (ApplicationLauncher.Ready) AddButton();
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            Settings.SaveIfDirty();
        }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            button = ApplicationLauncher.Instance.AddModApplication(() => { show = true; RefreshSensors(); }, () => { show = false; Settings.SaveIfDirty(); }, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPACECENTER, Icon());
            if (show) button.SetTrue(false);
        }

        static Texture2D Icon()
        {
            if (icon != null) return icon;
            const int n = 38;
            icon = new Texture2D(n, n, TextureFormat.ARGB32, false);
            var c = new Vector2(19f, 6f);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    Vector2 d = new Vector2(x, y) - c;
                    float r = d.magnitude, a = Mathf.Abs(Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg);
                    bool arc = a < 50f && (Mathf.Abs(r - 10f) < 1.3f || Mathf.Abs(r - 18f) < 1.3f || Mathf.Abs(r - 26f) < 1.3f);
                    bool dot = r < 3f;
                    float fade = Mathf.Clamp01(1.2f - r / 30f);
                    icon.SetPixel(x, y, arc || dot ? new Color(0.6f + 0.4f * fade, 0.9f, 1f, 0.4f + 0.6f * fade) : Color.clear);
                }
            icon.Apply();
            return icon;
        }

        void RefreshSensors()
        {
            string current = sensors.Count > 0 && sensorIdx < sensors.Count ? sensors[sensorIdx].Name : null;
            var own = new List<Sensor>();
            List<Part> craft = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel?.parts : HighLogic.LoadedSceneIsEditor ? EditorLogic.fetch?.ship?.Parts : null;
            if (craft != null)
                foreach (Part p in craft)
                {
                    foreach (ModuleRadar r in p.FindModulesImplementing<ModuleRadar>())
                        if ((r.canScan || r.canLock) && !Sensors.IsSonar(r)) own.Add(Sensors.Of(r));
                    foreach (MissileBase m in p.FindModulesImplementing<MissileBase>())
                        if (Sensors.HasRadarSeeker(m) && !Sensors.IsTorpedo(m)) own.Add(Sensors.Of(m));
                }
            sensors = Sensors.All(own);
            ownCount = own.Count;
            sensorIdx = 0;
            if (current != null) { int i = sensors.FindIndex(s => s.Name == current); if (i >= 0) sensorIdx = i; }
        }

        int ownCount;

        void Update()
        {
            if (Time.unscaledTime > nextSave) { nextSave = Time.unscaledTime + 2f; Settings.SaveIfDirty(); }
            if (show && (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor) && Time.unscaledTime > nextRefresh) { nextRefresh = Time.unscaledTime + 5f; RefreshSensors(); }
        }

        void OnGUI()
        {
            if (!show) return;
            rect.height = 10f;   // let GUILayout shrink the window when sections collapse
            rect = GUILayout.Window(id, rect, Draw, "Radar & Stealth", GUILayout.Width(620f));
        }

        GUIStyle wrap, bold, cell, cellBold, on, dim;

        void Styles()
        {
            if (wrap != null) return;
            wrap = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 };
            dim = new GUIStyle(wrap); dim.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
            bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            cell = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontSize = 12 };
            cellBold = new GUIStyle(cell) { fontStyle = FontStyle.Bold };
            on = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            on.normal.textColor = on.hover.textColor = new Color(0.55f, 1f, 0.55f);
        }

        void Draw(int windowId)
        {
            Styles();
            if (GUI.Button(new Rect(rect.width - 22f, 3f, 18f, 18f), "×")) { show = false; button?.SetFalse(false); Settings.SaveIfDirty(); }

            GUILayout.BeginHorizontal();
            PresetButton(Preset.Classic, "Stock BDA");
            PresetButton(Preset.Balanced, "Balanced");
            PresetButton(Preset.Realistic, "Realistic");
            PresetButton(Preset.StealthFavoured, "Stealth+");
            GUILayout.EndHorizontal();
            GUILayout.Label(PresetBlurb(), dim);

            if (Settings.Enabled)
            {
                GUILayout.Space(4f);
                Slider("Stealth limit", ref Settings.StealthLimit, 0.005f, 0.25f, 0.005f, $"×{Settings.StealthLimit:0.###}",
                    $"Coatings + stealth parts together cut RCS at most {1f / Settings.StealthLimit:0}× (detection range ×{Mathf.Pow(Settings.StealthLimit, 1f / Settings.RangeExponent):0.00}).");
                Slider("Range falloff", ref Settings.RangeExponent, 1f, 4f, 0.25f, $"{Settings.RangeExponent:0.##}",
                    $"10× less RCS → {Mathf.Pow(0.1f, 1f / Settings.RangeExponent):0.00}× detection and lock range. 4 = radar equation.");
                Slider("ECM burn-through", ref Settings.BurnThroughKm, 2f, 40f, 1f, $"{Settings.BurnThroughKm:0} km",
                    $"Lockbreakers weaken up close. 300 ECM on 1 m²: lock signal ×{StealthMath.JamFactor(300f, 1f, Settings.BurnThroughKm * 0.3f, Settings.BurnThroughKm):0.00} at {Settings.BurnThroughKm * 0.3f:0.#} km, ×{StealthMath.JamFactor(300f, 1f, Settings.BurnThroughKm * 2f, Settings.BurnThroughKm):0.00} at {Settings.BurnThroughKm * 2f:0} km.");
                Slider("Track hold", ref Settings.TrackHold, 1f, 1.5f, 0.05f, $"×{Settings.TrackHold:0.00}",
                    "Radar locks and missile seekers keep a target out to about this multiple of their lock range.");
                Slider("Reference RCS", ref Settings.ReferenceRcs, 1f, 50f, 1f, $"{Settings.ReferenceRcs:0} m²",
                    "Craft this size or bigger keep stock detection ranges.");

                if (Settings.GradedFlares)
                {
                    float c = Settings.FlareChance;
                    string pct(float r) => $"{100f * StealthMath.FlareCapture(r, c, 1f, 0f):0}%";
                    float salvo = 1f;
                    for (int k = 0; k < 12; k++) salvo *= 1f - c * StealthMath.FlareFatigueFactor(k);
                    Slider("Flare decoy chance", ref Settings.FlareChance, 0.05f, 0.6f, 0.01f, $"{100f * c:0}%",
                        $"First flare, per IR seeker: as hot as the jet {pct(1f)}, 0.7× {pct(0.7f)}, hotter {pct(StealthMath.FlareRatioCap)}. Each rejected flare halves the next; 12 at once: {100f * (1f - salvo):0}%. × the missile's flare sensitivity.");
                }
                if (Settings.ChaffSeduction)
                {
                    float c = Settings.ChaffChance;
                    string pct(float beam, float rcs) => $"{100f * StealthMath.ChaffSeduction(c, 1f, Settings.CmFloor, beam, rcs):0}%";
                    Slider("Chaff decoy chance", ref Settings.ChaffChance, 0.05f, 0.9f, 0.01f, $"{100f * c:0}%",
                        $"Per bundle, per radar missile. 1 m²: beaming {pct(1f, 1f)}, head-on {pct(0f, 1f)}. 10 m²: beaming {pct(1f, 10f)}. 0.1 m²: beaming {pct(1f, 0.1f)}. × the missile's chaff sensitivity.");
                }

                advanced = GUILayout.Toggle(advanced, advanced ? " ▼ Fixes to stock behaviour" : " ▶ Fixes to stock behaviour", bold);
                if (advanced)
                {
                    Toggle(ref Settings.ApplyRcsReduction, "Stealth parts reduce detection", "Stock only feeds them into lockbreak.");
                    Toggle(ref Settings.DiminishingStacking, "Diminishing stealth stacking", "Stock: product × count (two ×0.75 give ×1.125).");
                    Toggle(ref Settings.JammerReveals, "Noise jammers reveal the jammer", "+jammerStrength/100 m² to detection.");
                    Toggle(ref Settings.BoundJamRadius, "Bounded noise-jam radius", "Stock radius grows as RCS shrinks (30 km at 0.5 m²).");
                    Toggle(ref Settings.SoftStandoff, "Soft escort jamming", "Stock subtracts and can reach zero.");
                    Toggle(ref Settings.FixSeekerUnits, "Seeker km/m unit fix", "Seeker curves written in km are rescaled.");
                    Toggle(ref Settings.SmoothAspect, "Smooth aspect RCS", "Stock jumps up to 4× per half-degree of turn.");
                    Toggle(ref Settings.GradedFlares, "Graded flare decoys", "Stock: a flare hotter than the jet always wins, a cooler one never.");
                    Toggle(ref Settings.ChaffSeduction, "Chaff decoys radar missiles", "Stock: aim jitter only.");
                    Toggle(ref Settings.HomeOnJam, "Seekers home on noise jammers", "+jammerStrength/100 m² to missile seeker lock.");
                    Toggle(ref Settings.JamDeniesRange, "Noise jamming denies range", "Strobe on radar and no missile datalink until burn-through.");
                    Toggle(ref Settings.SkinStacking, "Coatings stack with hull material", "Stock: any armour replaces the hull's radar factor.");
                    Toggle(ref Settings.BattleDamage, "Damage and open bays cost stealth", "Lost hitpoints wear coatings and stealth parts; open bays expose their missiles.");
                    Toggle(ref Settings.NoAssuredLock, "No guaranteed-lock zones", "41 radar/seeker configs lock anything inside 2–20 km, ECM included.");
                    bool floor = Settings.CmFloor > 0f;
                    Toggle(ref floor, "No countermeasure-immune missiles", "Flare/chaff effectiveness at least 15%.");
                    if (floor != Settings.CmFloor > 0f) { Settings.CmFloor = floor ? 0.15f : 0f; Custom(); }
                }
                bool notes = GUILayout.Toggle(Settings.Messages, " On-screen decoy and ECM messages");
                if (notes != Settings.Messages) { Settings.Messages = notes; Settings.Changed(); }   // not part of a preset
            }

            GUILayout.Space(6f);
            DrawSensorPicker();
            DrawCraft();
            DrawPreview();
            GUI.DragWindow();
        }

        void PresetButton(Preset p, string label)
        {
            if (GUILayout.Button(label, Settings.Preset == p ? on : GUI.skin.button)) Settings.Apply(p);
        }

        static string PresetBlurb()
        {
            switch (Settings.Preset)
            {
                case Preset.Classic: return "Stock BDArmory, unchanged.";
                case Preset.Balanced: return "A stealth jet is seen and locked at roughly a third to half the range of a normal one; never invisible, and ECM burns through up close. Craft of 10 m² and up keep stock ranges. Flares decoy by chance, not all-or-nothing.";
                case Preset.Realistic: return "Radar equation (range⁴). Stealth helps less, ECM burns through sooner.";
                case Preset.StealthFavoured: return "Strong stealth. Still lockable up close.";
                default: return "Custom settings.";
            }
        }

        void Custom() { Settings.Preset = Preset.Custom; Settings.Changed(); }

        void Slider(string name, ref float value, float min, float max, float step, string shown, string effect)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, GUILayout.Width(120f));
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(320f), GUILayout.Height(20f));
            v = Mathf.Round(v / step) * step;
            GUILayout.Label(shown, cellBold, GUILayout.Width(70f));
            GUILayout.EndHorizontal();
            GUILayout.Label(effect, dim);
            if (Mathf.Abs(v - value) > 1e-5f) { value = Mathf.Clamp(v, min, max); Custom(); }
        }

        void Toggle(ref bool value, string name, string note)
        {
            GUILayout.BeginHorizontal();
            bool v = GUILayout.Toggle(value, " " + name, GUILayout.Width(250f));
            GUILayout.Label(note, dim);
            GUILayout.EndHorizontal();
            if (v != value) { value = v; Custom(); }
        }

        Sensor Current => sensors.Count == 0 ? null : sensors[Mathf.Clamp(sensorIdx, 0, sensors.Count - 1)];

        void DrawSensorPicker()
        {
            if (sensors.Count == 0) RefreshSensors();
            if (sensors.Count == 0) { GUILayout.Label("No radar or radar-seeker parts loaded."); return; }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Against", bold, GUILayout.Width(60f));
            if (GUILayout.Button("◀", GUILayout.Width(28f))) sensorIdx = (sensorIdx + sensors.Count - 1) % sensors.Count;
            Sensor s = Current;
            string tag = sensorIdx < ownCount ? "  (this craft)" : "";
            GUILayout.Label($"{s.Name}{tag}", bold, GUILayout.ExpandWidth(true));
            GUILayout.Label($"{sensorIdx + 1}/{sensors.Count}", cell, GUILayout.Width(60f));
            if (GUILayout.Button("▶", GUILayout.Width(28f))) sensorIdx = (sensorIdx + 1) % sensors.Count;
            GUILayout.EndHorizontal();
        }

        // ---- the player's craft ----

        bool CraftTarget(out Target t, out string source)
        {
            t = default; source = null;
            if (HighLogic.LoadedSceneIsFlight)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                TargetInfo ti = v != null ? v.gameObject.GetComponent<TargetInfo>() : null;
                if (ti == null || ti.radarBaseSignature < 0f) { source = "Not measured yet: happens the first time any radar scans this craft."; return false; }
                t = Target.Plain(ti.radarBaseSignature * Patches.Exposure(v));
                t.Coating = Patches.Coating(v);
                VesselECMJInfo info = v.gameObject.GetComponent<VesselECMJInfo>();
                if (info != null) { t.Jam = info.jammerStrength; t.LockBreak = info.lockBreakStrength; }
                if (Patches.TryEcm(ti, out EcmData e)) t.Parts = e.PartsCombined();
                source = "active ECM";
                return true;
            }
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (Patches.EditorRenders == 0) { source = "Not measured yet."; return false; }
                if (scannedRenders != Patches.EditorRenders) { scannedRenders = Patches.EditorRenders; EditorPatches.ScanCraft(EditorLogic.fetch?.ship?.Parts); }
                t = EditorPatches.CraftTarget(EditorPatches.CraftRcs);
                source = "all ECM on";
                return true;
            }
            return false;
        }

        void DrawCraft()
        {
            if (!(HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor)) return;
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Your craft", bold);
            if (HighLogic.LoadedSceneIsEditor && GUILayout.Button("Measure", GUILayout.Width(80f))) MeasureEditorCraft();
            if (HighLogic.LoadedSceneIsFlight && GUILayout.Button("Write RCS report", GUILayout.Width(130f)))
                try { ScreenMessages.PostScreenMessage("RCS report: " + Report.Write(), 8f, ScreenMessageStyle.UPPER_CENTER); } catch (Exception e) { Debug.LogError("[BDAStealth] Report failed: " + e); }
            GUILayout.EndHorizontal();
            if (!CraftTarget(out Target t, out string source)) { GUILayout.Label(source, dim); return; }
            Sensor s = Current;
            Ranges g = s != null ? Analysis.Evaluate(s, t) : default;
            float eff = t.Effective;
            string limit = Settings.Enabled && t.Coating * t.Parts < Settings.StealthLimit * 1.001f ? "  — at the stealth limit" : "";
            GUILayout.Label($"RCS {Analysis.Rcs(t.Base)} rendered (coatings ×{t.Coating:0.00}) · stealth parts ×{t.Parts:0.00} → {Analysis.Rcs(eff)} effective{limit}", wrap);
            int hidden = HighLogic.LoadedSceneIsEditor ? Patches.EditorHidden : Patches.HiddenHullParts(FlightGlobals.ActiveVessel);
            if (hidden > 0) GUILayout.Label($"Stealth hull under reflective armour: {hidden} parts", wrap);
            if (s == null) return;
            bool ecm = t.Jam > 0f || t.LockBreak > 0f;
            var line = new List<string>();
            if (s.CanScan && s.Det != null) line.Add($"seen {Analysis.Km(g.Detect)}" + (t.Jam > 0f ? $" ({Analysis.Km(g.DetectEcm)} jamming)" : ""));
            if (s.CanLock) line.Add($"locked {Analysis.Km(g.Lock)}" + (ecm ? $" ({Analysis.Km(g.LockEcm)} with {source})" : ""));
            if (line.Count > 0) GUILayout.Label("vs " + s.Name + ": " + string.Join(" · ", line), wrap);
            if (Settings.Enabled)
                GUILayout.Label($"Stock BDArmory: seen {Analysis.Km(g.StockDetect)} · locked {Analysis.Km(g.StockLock)}" + (ecm ? $" ({Analysis.Km(g.StockLockEcm)} with ECM)" : ""), dim);
        }

        static void MeasureEditorCraft()
        {
            try
            {
                if (EditorLogic.RootPart == null) return;
                RadarUtils.RenderVesselRadarSnapshot(new Vessel { parts = EditorLogic.fetch.ship.Parts, vesselType = VesselType.Plane }, EditorLogic.RootPart.transform, null, inEditorZoom: true);
            }
            catch (Exception e) { Debug.LogWarning("[BDAStealth] Editor measure failed: " + e.Message); }
        }

        // ---- generic table ----

        void DrawPreview()
        {
            Sensor s = Current;
            if (s == null) return;
            GUILayout.Space(4f);
            GUILayout.Label("Typical targets", bold);
            GUILayout.BeginHorizontal();
            Head("RCS", 70f); Head("Seen", 80f); Head("Locked", 80f); Head($"Locked vs {PreviewLockBreak:0} ECM", 140f); Head("Stock seen", 90f); Head("Stock vs ECM", 100f);
            GUILayout.EndHorizontal();
            bool scan = s.CanScan && s.Det != null;
            foreach (float rcs in previewRcs)
            {
                Ranges p = Analysis.Evaluate(s, Target.Plain(rcs));
                Ranges g = Analysis.Evaluate(s, Target.Plain(rcs, PreviewLockBreak));
                GUILayout.BeginHorizontal();
                GUILayout.Label(Analysis.Rcs(rcs), cellBold, GUILayout.Width(70f));
                GUILayout.Label(scan ? Analysis.Km(p.Detect) : "–", cell, GUILayout.Width(80f));
                GUILayout.Label(s.CanLock ? Analysis.Km(p.Lock) : "–", cell, GUILayout.Width(80f));
                GUILayout.Label(s.CanLock ? Analysis.Km(g.LockEcm) : "–", cell, GUILayout.Width(140f));
                GUILayout.Label(scan ? Analysis.Km(p.StockDetect) : "–", cell, GUILayout.Width(90f));
                GUILayout.Label(s.CanLock ? Analysis.Km(g.StockLockEcm) : "–", cell, GUILayout.Width(100f));
                GUILayout.EndHorizontal();
            }
            GUILayout.Label(s.IsSeeker ? "Seeker: lock only, limited by the missile's active radar range." : "RCS here is after stealth. IRST and IR seekers ignore RCS, so they counter stealth.", dim);
        }

        void Head(string s, float w) => GUILayout.Label(s, cellBold, GUILayout.Width(w));
    }
}
