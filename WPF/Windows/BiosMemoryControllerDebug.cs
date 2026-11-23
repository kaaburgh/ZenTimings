using System.Collections.Generic;

namespace ZenTimings.Common
{
    // The legacy AMD_ACPI / Get APCB Config dump is only initialized for DDR4/LPDDR4.
    // This is independent of the DDR5 AmdSetupRPL / AodSetupRpl setting-origin reader.
    internal static class BiosMemoryControllerDebug
    {
        internal static IEnumerable<string> Dump(byte[] table, bool isDdr4OrLpddr4)
        {
            if (!isDdr4OrLpddr4)
            {
                yield return "Not applicable: legacy AMD_ACPI APCB dump is only read for DDR4/LPDDR4.";
                yield break;
            }

            if (table == null || table.Length == 0)
            {
                yield return "Not available: no legacy AMD_ACPI APCB table was loaded.";
                yield break;
            }

            for (int i = 0; i < table.Length; i++)
                yield return $"Index {i:D3}: {table[i]:X2} ({table[i]})";
        }
    }
}
