using ZenStates.Core.Hardware.DRAM.DDR5.Spd;

namespace ZenStates.Core.Hardware.DRAM.DDR5.Pmic
{
    // The upstream SPD data model also contains a live PMIC object and its
    // report formatter references the hardware factory. Neither is populated
    // or used by the offline decoder. No PMIC operations exist in this adapter.
    public abstract class Ddr5Pmic { }
    internal static class Ddr5PmicFactory
    {
        internal static bool IsSupported(Ddr5PmicType type) => false;
    }
}
