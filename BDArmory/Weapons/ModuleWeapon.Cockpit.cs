using System.Collections.Generic;
using UnityEngine;

using BDArmory.Utils;

namespace BDArmory.Weapons
{
    /// <summary>
    /// Gun features from the M2X "BDArmory Plus" build: a selectable low rate of fire (parts with roundsPerMinuteLow),
    /// a barrel heat animation (hasHeatAnimation/heatAnimName), and the firing-solution flag read by IVA displays.
    /// </summary>
    public partial class ModuleWeapon
    {
        [KSPField]
        public float roundsPerMinuteLow = -1; // > 0 enables the High/Low rate-of-fire toggle.
        public float baseRPMLow = -1;

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = false, guiName = "Rate of Fire Mode"),
            UI_Toggle(enabledText = "High", disabledText = "Low", scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public bool rateOfFireMode = true;

        [KSPField]
        public bool hasHeatAnimation = false;
        [KSPField]
        public string heatAnimName = "heatAnim";
        AnimationState[] heatState = new AnimationState[0];

        /// <summary>True while the gun's aim point is within 1° of the computed lead.</summary>
        public bool solutionFound;

        /// <summary>Maximum rate of fire for the selected mode.</summary>
        float ActiveMaxRPM => rateOfFireMode || baseRPMLow <= 0 ? baseRPM : baseRPMLow;

        void SetupRateOfFireMode()
        {
            baseRPMLow = roundsPerMinuteLow > 0 ? Mathf.Min(roundsPerMinuteLow, baseRPM) : -1;
            bool available = baseRPMLow > 0;
            if (!available) rateOfFireMode = true;
            var field = Fields["rateOfFireMode"];
            field.guiActive = available;
            field.guiActiveEditor = available;
            if (!available) return;
            var onChange = (Callback<BaseField, object>)((_, __) => ApplyRateOfFireMode());
            if (field.uiControlFlight != null) field.uiControlFlight.onFieldChanged = onChange;
            if (field.uiControlEditor != null) field.uiControlEditor.onFieldChanged = onChange;
            ApplyRateOfFireMode();
        }

        void ApplyRateOfFireMode()
        {
            // Spooling guns pick the mode up in their spool-up; others fire at the mode's rate directly.
            if (SpoolUpTime > 0) return;
            if (!isChaingun) roundsPerMinute = ActiveMaxRPM;
            else roundsPerMinute = Mathf.Min(roundsPerMinute, ActiveMaxRPM);
        }

        void SetupHeatAnimation()
        {
            if (!hasHeatAnimation) return;
            var names = BDAcTools.ParseNames(heatAnimName);
            var states = new List<AnimationState>(names.Count);
            foreach (var name in names)
            {
                var state = GUIUtils.SetUpSingleAnimation(name, part);
                if (state == null) { Debug.LogWarning($"[BDArmory.ModuleWeapon]: {OriginalShortName} is missing heat anim {name}"); continue; }
                state.normalizedTime = 0;
                state.speed = 0;
                state.enabled = false;
                states.Add(state);
            }
            heatState = states.ToArray();
            if (heatState.Length == 0) hasHeatAnimation = false;
        }

        void UpdateHeatAnimation()
        {
            if (!hasHeatAnimation || maxHeat <= 0) return;
            // Reaches full glow at two thirds of the overheat limit, as in the M2X build.
            float t = Mathf.Clamp01(1.5f * heat / maxHeat);
            for (int i = 0; i < heatState.Length; i++)
            {
                heatState[i].enabled = true;
                heatState[i].normalizedTime = t;
            }
        }
    }
}
