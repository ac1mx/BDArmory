using System;
using System.IO;
using UnityEngine;

namespace BDAStealth
{
    public enum Preset { Custom, Classic, Balanced, Realistic, StealthFavoured }

    internal static class Settings
    {
        static readonly string Dir = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "BDAStealth", "PluginData");
        static readonly string FilePath = Path.Combine(Dir, "BDAStealth.cfg");

        public static bool Enabled = true;
        public static float RangeExponent = 2f;     // required RCS ~ range^n below the reference RCS
        public static float ReferenceRcs = 10f;     // m^2; targets this size or bigger keep their original detection ranges
        public static float BurnThroughKm = 10f;    // lockbreaker reference range (J/S grows with range^2)
        public static float TrackHold = 1.15f;      // existing locks persist out to ~this x lock range
        public static float StealthLimit = 0.03f;   // coatings x stealth parts can't cut RCS below this fraction
        public static bool DiminishingStacking = true;
        public static bool ApplyRcsReduction = true;  // stealth parts actually reduce detection (stock: only feeds lockbreak)
        public static bool JammerReveals = true;      // active noise jammers add jammerStrength/100 m^2 to detection RCS
        public static bool SoftStandoff = true;       // escort jamming S/(S+J) instead of S-J clamped at 0
        public static bool BoundJamRadius = true;     // noise-jam radius no longer grows as RCS shrinks
        public static bool FixSeekerUnits = true;     // seeker curves written in km (BDA evaluates in m) are rescaled
        public static bool SmoothAspect = true;       // aspect RCS interpolated smoothly (stock jumps between samples)
        public static bool NoAssuredLock = true;      // lock curves needing 0 m² up close (lock anything, ECM included) use the falloff
        public static float CmFloor = 0.15f;          // no missile is fully immune to flares/chaff (min flare/chaff effectiveness)
        public static bool GradedFlares = true;       // each flare gets one chance roll per IR missile (stock: hotter = always decoys)
        public static float FlareChance = 0.25f;      // chance a flare as hot as the target (as the seeker sees it) decoys a missile
        public static bool ChaffSeduction = true;     // a chaff bundle can pull a radar missile onto the bloom (stock: aim jitter only)
        public static float ChaffChance = 0.2f;       // per bundle, beaming 0 m² target, chaffEffectivity 1
        public static bool HomeOnJam = true;          // active radar seekers home on noise jammers (+jammerStrength/100 m² lock signal)
        public static bool SkinStacking = true;       // absorbent armour stacks with the hull material; thin armour fades to the hull, not to bare metal (stock: any armour replaces the hull)
        public static bool JamDeniesRange = true;     // noise jammers show as strobes and block missile datalink until burn-through (stock: only inside the jam radius)
        public static bool BattleDamage = true;       // lost hitpoints wear coatings and stealth parts, lost parts leave breaches, open bays expose their missiles
        public static bool Messages = true;           // on-screen notes when a decoy or ECM defeats a missile or lock
        public static Preset Preset = Preset.Balanced;

        /// <summary>Bumped on every change; curves and ECM states rebuild lazily when it differs.</summary>
        public static int Version = 1;

        public static float HoldBoost => Mathf.Pow(Mathf.Max(TrackHold, 1f), RangeExponent);

        public static void Apply(Preset p)
        {
            Preset = p;
            if (p == Preset.Classic) { Enabled = false; Changed(); return; }
            Enabled = true; ReferenceRcs = 10f;
            DiminishingStacking = ApplyRcsReduction = JammerReveals = SoftStandoff = BoundJamRadius = FixSeekerUnits = SmoothAspect = GradedFlares = NoAssuredLock = ChaffSeduction = HomeOnJam = SkinStacking = JamDeniesRange = BattleDamage = true; CmFloor = 0.15f;
            switch (p)
            {
                case Preset.Balanced: RangeExponent = 2f; BurnThroughKm = 10f; TrackHold = 1.15f; StealthLimit = 0.03f; FlareChance = 0.25f; ChaffChance = 0.2f; break;
                case Preset.Realistic: RangeExponent = 4f; BurnThroughKm = 8f; TrackHold = 1.1f; StealthLimit = 0.02f; FlareChance = 0.2f; ChaffChance = 0.15f; break;
                case Preset.StealthFavoured: RangeExponent = 1.5f; BurnThroughKm = 15f; TrackHold = 1.1f; StealthLimit = 0.015f; FlareChance = 0.3f; ChaffChance = 0.3f; break;
            }
            Changed();
        }

        public static void Changed() { Version++; dirty = true; }

        static bool dirty;
        public static void SaveIfDirty() { if (dirty) Save(); }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) { Save(); return; }
                ConfigNode n = ConfigNode.Load(FilePath)?.GetNode("BDASTEALTH");
                if (n == null) return;
                n.TryGetValue("enabled", ref Enabled);
                n.TryGetValue("rangeExponent", ref RangeExponent);
                n.TryGetValue("referenceRcs", ref ReferenceRcs);
                n.TryGetValue("burnThroughKm", ref BurnThroughKm);
                n.TryGetValue("trackHold", ref TrackHold);
                n.TryGetValue("stealthLimit", ref StealthLimit);
                n.TryGetValue("diminishingStacking", ref DiminishingStacking);
                n.TryGetValue("applyRcsReduction", ref ApplyRcsReduction);
                n.TryGetValue("jammerReveals", ref JammerReveals);
                n.TryGetValue("softStandoff", ref SoftStandoff);
                n.TryGetValue("boundJamRadius", ref BoundJamRadius);
                n.TryGetValue("fixSeekerUnits", ref FixSeekerUnits);
                n.TryGetValue("smoothAspect", ref SmoothAspect);
                n.TryGetValue("gradedFlares", ref GradedFlares);
                n.TryGetValue("noAssuredLock", ref NoAssuredLock);
                n.TryGetValue("cmFloor", ref CmFloor);
                n.TryGetValue("flareChance", ref FlareChance);
                n.TryGetValue("chaffSeduction", ref ChaffSeduction);
                n.TryGetValue("chaffChance", ref ChaffChance);
                n.TryGetValue("homeOnJam", ref HomeOnJam);
                n.TryGetValue("skinStacking", ref SkinStacking);
                n.TryGetValue("jamDeniesRange", ref JamDeniesRange);
                n.TryGetValue("battleDamage", ref BattleDamage);
                n.TryGetValue("messages", ref Messages);
                string p = n.GetValue("preset");
                if (p != null && Enum.TryParse(p, out Preset parsed)) Preset = parsed;
                // A named preset means its current definition, so retuned presets reach installs whose file stores older numbers.
                if (Preset != Preset.Custom) { Apply(Preset); dirty = false; }
                Clamp();
                Version++;
            }
            catch (Exception e) { Debug.LogError("[BDAStealth] Failed to load settings: " + e); }
        }

        public static void Clamp()
        {
            RangeExponent = Mathf.Clamp(RangeExponent, 1f, 4f);
            ReferenceRcs = Mathf.Clamp(ReferenceRcs, 0.1f, 100f);
            BurnThroughKm = Mathf.Clamp(BurnThroughKm, 1f, 50f);
            TrackHold = Mathf.Clamp(TrackHold, 1f, 1.5f);
            StealthLimit = Mathf.Clamp(StealthLimit, 0.005f, 1f);
            FlareChance = Mathf.Clamp(FlareChance, 0.02f, 0.9f);
            CmFloor = Mathf.Clamp(CmFloor, 0f, 1f);
            ChaffChance = Mathf.Clamp(ChaffChance, 0.05f, 0.9f);
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var root = new ConfigNode();
                ConfigNode n = root.AddNode("BDASTEALTH");
                n.AddValue("preset", Preset.ToString());
                n.AddValue("enabled", Enabled);
                n.AddValue("rangeExponent", RangeExponent);
                n.AddValue("referenceRcs", ReferenceRcs);
                n.AddValue("burnThroughKm", BurnThroughKm);
                n.AddValue("trackHold", TrackHold);
                n.AddValue("stealthLimit", StealthLimit);
                n.AddValue("diminishingStacking", DiminishingStacking);
                n.AddValue("applyRcsReduction", ApplyRcsReduction);
                n.AddValue("jammerReveals", JammerReveals);
                n.AddValue("softStandoff", SoftStandoff);
                n.AddValue("boundJamRadius", BoundJamRadius);
                n.AddValue("fixSeekerUnits", FixSeekerUnits);
                n.AddValue("smoothAspect", SmoothAspect);
                n.AddValue("gradedFlares", GradedFlares);
                n.AddValue("noAssuredLock", NoAssuredLock);
                n.AddValue("cmFloor", CmFloor);
                n.AddValue("flareChance", FlareChance);
                n.AddValue("chaffSeduction", ChaffSeduction);
                n.AddValue("chaffChance", ChaffChance);
                n.AddValue("homeOnJam", HomeOnJam);
                n.AddValue("skinStacking", SkinStacking);
                n.AddValue("jamDeniesRange", JamDeniesRange);
                n.AddValue("battleDamage", BattleDamage);
                n.AddValue("messages", Messages);
                root.Save(FilePath, "BDAStealth settings - edit in game via the Radar & Stealth toolbar button");
                dirty = false;
            }
            catch (Exception e) { Debug.LogError("[BDAStealth] Failed to save settings: " + e); }
        }
    }
}
