namespace BDArmory.Radar
{
    /// <summary>Control surface used by IVA displays (MOARdV Avionics Systems' BDArmory bindings in the M2X cockpits).</summary>
    public partial class ModuleRadar
    {
        // Display offsets owned by the IVA display; not used by BDArmory itself.
        public double xOffset;
        public double yOffset;
        public double zOffset;

        public void EnableBoreSight() => boresightScan = true;
        public void DisableBoreSight() => boresightScan = false;
    }
}
