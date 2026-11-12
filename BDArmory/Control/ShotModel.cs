using System;
using System.Globalization;
using System.IO;
using UnityEngine;

using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Estimate of how likely a missile launched now is to hit, as a logistic function of the launch geometry.
    /// The weights are fitted to shots logged in this game (see MissileBase's shot record); a fit saved as
    /// GameData/BDArmory/PluginData/shot_model.txt (seven numbers on one line) replaces the built-in one.
    /// </summary>
    public static class ShotModel
    {
        // bias, range per 10 km, target coming at us, target running away, degrees off the missile's nose per 45, heat seeker, launched below 500 m.
        // Fitted to 108 shots from three four-way fights; refit with more as they're logged.
        static float[] weights = { -0.679f, -0.540f, -0.004f, -0.355f, 0.006f, 1.646f, -0.240f };
        static bool loaded;
        const float hotAspect = 60f, coldAspect = 135f, lowAltitude = 500f;

        static void Load()
        {
            loaded = true;
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/BDArmory/PluginData/shot_model.txt");
                if (!File.Exists(path)) return;
                var fields = File.ReadAllText(path).Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != weights.Length) { Debug.LogWarning($"[BDArmory.ShotModel]: {path} has {fields.Length} values, expected {weights.Length}; using the built-in model."); return; }
                var read = new float[weights.Length];
                for (int i = 0; i < read.Length; ++i) read[i] = float.Parse(fields[i], CultureInfo.InvariantCulture);
                weights = read;
            }
            catch (Exception e) { Debug.LogWarning($"[BDArmory.ShotModel]: Couldn't read the shot model, using the built-in one: {e.Message}"); }
        }

        /// <summary>
        /// Chance (0—1) that the missile hits the target if launched from the vessel now.
        /// </summary>
        public static float Estimate(MissileBase missile, Vessel from, Vessel target)
        {
            if (!loaded) Load();
            Vector3 toTarget = target.CoM - from.CoM;
            float range = toTarget.magnitude;
            if (range < 1f) return 1f;
            Vector3 line = toTarget / range;
            float aspect = VectorUtils.Angle(target.srf_vel_direction, -line); // 0 = coming at us, 180 = running away.
            float offNose = Mathf.Min(VectorUtils.Angle(missile.GetForwardTransform(), line), 90f);
            float z = weights[0]
                + weights[1] * range / 10000f
                + (aspect < hotAspect ? weights[2] : 0f)
                + (aspect > coldAspect ? weights[3] : 0f)
                + weights[4] * offNose / 45f
                + (missile.TargetingMode == MissileBase.TargetingModes.Heat ? weights[5] : 0f)
                + (from.radarAltitude < lowAltitude ? weights[6] : 0f);
            return 1f / (1f + Mathf.Exp(-z));
        }
    }
}
