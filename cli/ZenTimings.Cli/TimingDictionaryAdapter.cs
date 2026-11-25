// The pinned ZenStates-Core dictionaries only need this nested struct.
// Compile the original dictionaries unmodified, without the Windows hardware stack.
namespace ZenStates.Core.Hardware.DRAM
{
    internal static class MemoryConfig
    {
        public struct TimingDef
        {
            public string Name;
            public int HiBit;
            public int LoBit;
        }
    }
}
