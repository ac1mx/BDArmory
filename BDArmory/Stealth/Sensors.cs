using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BDArmory.Radar;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDAStealth
{
    /// <summary>A radar or an active radar seeker, as the range analysis sees it. Ranges are in curve units; ToKm converts.</summary>
    internal sealed class Sensor
    {
        public string Name;
        public FloatCurve Det, Lock;     // Det null for seekers
        public bool CanScan, CanLock, Convert = true;
        public MissileBase Missile;      // seekers: scale follows the live unit-fix setting
        float scale = 1f;
        public float Scale { get => Missile != null ? Sensors.SeekerScale(Missile, Lock) : scale; set => scale = value; }  // applied to the curve's range axis
        public float ToKm = 1f;          // curve units -> km
        public float LockMax = -1f;      // hard lock range in curve units (seekers: activeRadarRange); -1 = curve max
        public bool IsSeeker;
    }

    internal static class Sensors
    {
        static string Loc(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '#') return s;
            try { return KSP.Localization.Localizer.Format(s); } catch { return s; }
        }

        public static bool IsSonar(ModuleRadar r) => r.sonarMode != ModuleRadar.SonarModes.None;
        public static bool IsTorpedo(MissileBase m) => m.GetWeaponClass() == WeaponClasses.SLW;

        public static bool HasRadarSeeker(MissileBase m) =>
            m.activeRadarRange > 0f && (m.TargetingMode == MissileBase.TargetingModes.Radar || m.TargetingModeTerminal == MissileBase.TargetingModes.Radar);

        /// <summary>Prefab missiles only know their targeting mode after ParseModes (normally run in OnStart/GetInfo).</summary>
        public static void Parse(MissileLauncher m) { try { m.ParseModes(); } catch { } }

        static readonly ConditionalWeakTable<MissileBase, FloatCurve> defaults = new ConditionalWeakTable<MissileBase, FloatCurve>();

        /// <summary>The seeker curve BDA will use: config curve, or the 5 m² @ activeRadarRange default added at OnStart.</summary>
        public static FloatCurve SeekerCurve(MissileBase m)
        {
            FloatCurve c = m.activeRadarLockTrackCurve;
            if (c != null && c.Curve != null && c.Curve.length > 0) return c;
            return defaults.GetValue(m, k => { var f = new FloatCurve(); f.Add(0f, 0f); f.Add(k.activeRadarRange, 5f); return f; });
        }

        /// <summary>
        /// BDA evaluates seeker curves at the range in metres, but some configs write keys in km (max key 100 with a 100000 m seeker),
        /// which makes the seeker range-blind. Those curves are rescaled x1000.
        /// </summary>
        public static float SeekerScale(MissileBase m, FloatCurve c)
        {
            if (!Settings.Enabled || !Settings.FixSeekerUnits || m.activeRadarRange <= 0f) return 1f;
            if (!Curves.Range(c, out _, out float tMax) || tMax <= 0f) return 1f;
            return tMax * 100f < m.activeRadarRange ? 1000f : 1f;
        }

        public static Sensor Of(ModuleRadar r)
        {
            return new Sensor
            {
                Name = Loc(!string.IsNullOrEmpty(r.radarName) ? r.radarName : r.part?.partInfo?.title ?? r.part?.name ?? "radar"),
                Det = r.radarDetectionCurve, Lock = r.radarLockTrackCurve, CanScan = r.canScan, CanLock = r.canLock, Convert = !IsSonar(r),
            };
        }

        public static Sensor Of(MissileBase m)
        {
            FloatCurve c = SeekerCurve(m);
            string title = m.part?.partInfo?.title;
            return new Sensor
            {
                Name = Loc(string.IsNullOrEmpty(title) ? m.shortName : title) + " seeker",
                Lock = c, CanLock = true, Convert = !IsTorpedo(m), Missile = m, ToKm = 0.001f, LockMax = m.activeRadarRange, IsSeeker = true,
            };
        }

        /// <summary>All radar and radar-seeker part prefabs, sorted by name, plus the given live sensors first.</summary>
        public static List<Sensor> All(IEnumerable<Sensor> first)
        {
            var list = new List<Sensor>();
            var seen = new HashSet<string>();
            if (first != null) foreach (Sensor s in first) if (seen.Add("own:" + s.Name)) list.Add(s);
            var rest = new List<Sensor>();
            if (PartLoader.LoadedPartsList != null)
                foreach (AvailablePart ap in PartLoader.LoadedPartsList)
                {
                    if (ap?.partPrefab == null) continue;
                    foreach (ModuleRadar r in ap.partPrefab.FindModulesImplementing<ModuleRadar>())
                        if ((r.canScan || r.canLock) && !IsSonar(r)) { Sensor s = Of(r); if (seen.Add(s.Name)) rest.Add(s); }
                    foreach (MissileLauncher m in ap.partPrefab.FindModulesImplementing<MissileLauncher>())
                    {
                        Parse(m);
                        if (HasRadarSeeker(m) && !IsTorpedo(m)) { Sensor s = Of(m); if (seen.Add(s.Name)) rest.Add(s); }
                    }
                }
            rest.Sort((a, b) => a.IsSeeker != b.IsSeeker ? (a.IsSeeker ? 1 : -1) : string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
            list.AddRange(rest);
            return list;
        }
    }
}
