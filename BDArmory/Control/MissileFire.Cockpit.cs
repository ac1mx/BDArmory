using BDArmory.UI;
using BDArmory.Weapons;

namespace BDArmory.Control
{
    /// <summary>Control surface used by IVA displays (MOARdV Avionics Systems' BDArmory bindings in the M2X cockpits).</summary>
    public partial class MissileFire
    {
        public void SelectGPSTargetIndex(int index)
        {
            var targets = BDATargetManager.GPSTargetList(Team);
            if (index < 0 || index >= targets.Count) return;
            designatedGPSCoordsIndex = index;
            designatedGPSInfo = targets[index];
        }

        public void JettisonWeapon() => AGJettisonWeapon(null);

        public void SetArm(bool state)
        {
            isArmed = state;
            audioSource.PlayOneShot(isArmed ? armOnSound : armOffSound);
        }

        public void FireChaffManually(int amount)
        {
            if (!isChaffing) StartCoroutine(ChaffRoutine(amount, chaffInterval));
        }

        public void FireFlaresManually(int amount)
        {
            if (!isFlaring) StartCoroutine(FlareRoutine(amount, cmInterval));
        }

        /// <summary>Index in weaponArray of the first gun with this short name, or 0 (no weapon) if there is none.</summary>
        public int GetGunIndexByShortName(string shortName)
        {
            for (int i = 0; i < weaponArray.Length; i++)
                if (weaponArray[i] != null && weaponArray[i].GetWeaponClass() == WeaponClasses.Gun && weaponArray[i].GetShortName() == shortName)
                    return i;
            return 0;
        }
    }
}
