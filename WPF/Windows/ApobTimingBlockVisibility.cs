using System;
using System.Collections.Generic;
using System.Linq;

namespace ZenTimings.Windows
{
    /// <summary>
    /// Chooses APOB timing blocks for presentation, without modifying the parsed
    /// training records. Inactive blocks may describe memory P-states that are
    /// not currently running; they must not be presented as active channels.
    /// </summary>
    public static class ApobTimingBlockVisibility
    {
        public static List<T> SelectVisible<T>(
            IList<T> blocks, Func<T, bool> isActive, bool activeClockKnown, bool showInactive)
        {
            if (blocks == null || blocks.Count == 0)
                return new List<T>();
            if (isActive == null)
                throw new ArgumentNullException(nameof(isActive));

            List<T> all = blocks.ToList();
            if (showInactive || !activeClockKnown)
                return all;

            List<T> active = all.Where(isActive).ToList();

            // If APOB cannot identify *any* active timing block, retaining all
            // records is safer than displaying an empty or misleading table.
            return active.Count > 0 ? active : all;
        }
    }
}
