namespace ZenStates.Core
{
    // Only used by the linked upstream SPD report formatter. The CLI never
    // enables serial-number output or exposes the upstream raw report.
    internal sealed class CoreOptions
    {
        internal static CoreOptions Current { get; } = new CoreOptions();
        internal bool PrintSerialNumbers => false;
    }
}
