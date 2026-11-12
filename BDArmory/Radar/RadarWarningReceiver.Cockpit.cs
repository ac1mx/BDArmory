using UnityEngine;

using BDArmory.Settings;

namespace BDArmory.Radar
{
    /// <summary>Read/control surface used by IVA displays (MOARdV Avionics Systems' BDArmory bindings in the M2X cockpits).</summary>
    public partial class RadarWarningReceiver
    {
        // Set when the matching warning sound starts; IVA displays read and clear them.
        public bool playLaunch;
        public bool playLock;
        public bool playPing;

        void Update()
        {
            // Counted here rather than in OnGUI, so the repeat delay also runs out while the RWR window is hidden.
            if (audioSourceRepeatDelay > 0) audioSourceRepeatDelay -= Time.deltaTime;
        }

        public void ToggleRWR()
        {
            if (!rwrEnabled)
            {
                EnableRWR();
                displayRWR = true;
            }
            else
            {
                DisableRWR();
                displayRWR = false;
            }
        }

        public double GetRWRSize() => RwrSize * BDArmorySettings.RWR_WINDOW_SCALE;

        public bool GetRWRPingExists(int i) => rwrEnabled && pingsData != null && i >= 0 && i < pingsData.Length && pingsData[i].exists;

        public double GetRWRPingType(int i) => GetRWRPingExists(i) ? (double)pingsData[i].signalType : -1;
    }
}
