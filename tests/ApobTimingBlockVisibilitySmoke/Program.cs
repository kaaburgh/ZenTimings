using System;
using System.Collections.Generic;
using System.Linq;
using ZenTimings.Windows;

internal static class Program
{
    private sealed class Block
    {
        internal int Channel;
        internal int PState;
        internal int MemClk;
        internal bool IsActive;
    }

    private static int assertions;

    private static void Check(bool condition, string scenario)
    {
        assertions++;
        if (!condition)
            throw new InvalidOperationException("FAIL: " + scenario);
    }

    private static void Main()
    {
        // Hardware regression: BD790i X3D APOB v24 presents four blocks,
        // only Ch A and Ch C at the active 3000 MHz MCLK.
        var original = new List<Block>
        {
            new Block { Channel = 0, MemClk = 3000, IsActive = true },
            new Block { Channel = 1, MemClk = 1600, IsActive = false },
            new Block { Channel = 2, MemClk = 3000, IsActive = true },
            new Block { Channel = 3, MemClk = 1600, IsActive = false }
        };
        var visible = ApobTimingBlockVisibility.SelectVisible(
            original, b => b.IsActive, activeClockKnown: true, showInactive: false);
        Check(visible.Count == 2, "only two active blocks remain");
        Check(visible.Select(b => b.Channel).SequenceEqual(new[] { 0, 2 }),
            "channel identity and order remain A/C, not renamed A/B");
        Check(visible.All(b => b.IsActive && b.MemClk == 3000),
            "inactive 1600 MHz P-state records are not presented as running channels");
        Check(original.Count == 4 && original[1].MemClk == 1600 && original[3].MemClk == 1600,
            "filtering never alters the original APOB records");

        var all = ApobTimingBlockVisibility.SelectVisible(
            original, b => b.IsActive, activeClockKnown: true, showInactive: true);
        Check(all.Select(b => b.Channel).SequenceEqual(new[] { 0, 1, 2, 3 }),
            "show-inactive diagnostics restores all blocks in original order");

        var unknownClock = ApobTimingBlockVisibility.SelectVisible(
            original, b => b.IsActive, activeClockKnown: false, showInactive: false);
        Check(unknownClock.Count == 4,
            "without reliable active-clock metadata, do not hide any timing blocks");

        var allInactive = new List<Block>
        {
            new Block { Channel = 0, MemClk = 1600, IsActive = false },
            new Block { Channel = 1, MemClk = 1600, IsActive = false }
        };
        Check(ApobTimingBlockVisibility.SelectVisible(
            allInactive, b => b.IsActive, activeClockKnown: true, showInactive: false).Count == 2,
            "if no active block was identified, retain all diagnostic information");

        var allActive = new List<Block>
        {
            new Block { Channel = 0, MemClk = 3000, IsActive = true },
            new Block { Channel = 1, MemClk = 3000, IsActive = true }
        };
        Check(ApobTimingBlockVisibility.SelectVisible(
            allActive, b => b.IsActive, activeClockKnown: true, showInactive: false).Count == 2,
            "all-active configuration remains unchanged");

        Check(ApobTimingBlockVisibility.SelectVisible(
            new List<Block>(), b => b.IsActive, true, false).Count == 0,
            "empty APOB timing block list stays empty");
        Check(ApobTimingBlockVisibility.SelectVisible<Block>(
            null, b => b.IsActive, true, false).Count == 0,
            "unavailable APOB timing block list stays empty");

        Console.WriteLine("OK: " + assertions + " APOB timing visibility assertions");
    }
}
