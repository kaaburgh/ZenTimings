using System;
using System.Linq;
using ZenTimings.BiosOrigin;

internal static class Program
{
    private static int tests;

    private static void Check(bool condition, string message)
    {
        tests++;
        if (!condition) throw new Exception("FAILED: " + message);
    }

    private static void Set16(byte[] blob, int offset, int value)
    {
        blob[offset] = (byte)value;
        blob[offset + 1] = (byte)(value >> 8);
    }

    private static void Set32(byte[] blob, int offset, uint value)
    {
        blob[offset] = (byte)value;
        blob[offset + 1] = (byte)(value >> 8);
        blob[offset + 2] = (byte)(value >> 16);
        blob[offset + 3] = (byte)(value >> 24);
    }

    private static void Defaults(out byte[] cbs, out byte[] aod)
    {
        cbs = new byte[0x6D5];
        aod = new byte[0x1AC];
        Set32(cbs, 0x00, 0xFC4053AA);
        Set32(cbs, 0x04, 0x2D8);
        Set32(cbs, 0x08, 0x4B9D5809);
        cbs[0x116] = 255;
        aod[0x3C] = 255;

        // Twenty-five physical timing controls (26 UI rows because TRCD has RD + WR).
        for (int i = 0x3F; i <= 0x87; i += 3) aod[i] = 0;
        for (int i = 0x119; i <= 0x161; i += 3) cbs[i] = 0;
        for (int i = 0x8A; i <= 0x93; i++) aod[i] = 255;
        foreach (int i in new[] { 0x165, 0x16D, 0x16C, 0x16E, 0x167, 0x168, 0x169,
                                  0x16A, 0x16B, 0x16F, 0x260 }) cbs[i] = 255;
        Set16(aod, 0x94, 0xFFFF);
        aod[0x96] = 255;
        aod[0x192] = 0;
        aod[0x19B] = 0;
        aod[0x97] = 0;
    }

    private static void Main()
    {
        byte[] cbs, aod;
        Defaults(out cbs, out aod);
        var r = DrfxiSettingOrigins.FromBlobs(cbs, aod);
        Check(r.IsMatched, "valid default variables match: " + r.Status);
        Check(r.LayoutId == "drfxi-dragonrange-v1", "versioned layout id");
        Check(r.Find("tCL").Source == "Auto", "automatic timing");
        Check(r.Find("tCWL").Source == "AGESA", "timing without BIOS menu field");
        Check(r.Find("BGS").Source == "?", "BGS mapping unknown");
        Check(r.Find("UCLK").Source == "Auto", "UCLK defaults to Auto");

        // The 1.15 owner readback's representative AOD values.
        aod[0x3C] = 1;
        aod[0x3A] = 1;
        Set16(aod, 0x3D, 6000);
        aod[0x3F] = 1;
        Set16(aod, 0x40, 36);
        aod[0x42] = 1;
        Set16(aod, 0x43, 40);
        aod[0x51] = 1;
        Set16(aod, 0x52, 480);
        Set16(aod, 0x94, 2000);
        aod[0x96] = 1;
        aod[0x192] = 1;
        Set16(aod, 0x193, 1200);
        Set16(aod, 0x195, 1200);
        Set16(aod, 0x197, 1200);

        r = DrfxiSettingOrigins.FromBlobs(cbs, aod);
        Check(r.IsMatched, "owner-like AOD config matches: " + r.Status);
        Check(r.Find("Speed").Source == "AOD" && r.Find("Speed").StoredNumber == 6000, "AOD 6000 MT/s");
        Check(r.Find("MCLK").DiffersFrom("3000.00", 3000) == false, "MT/s to MCLK conversion");
        Check(r.Find("tCL").Source == "AOD" && r.Find("tCL").DiffersFrom("36") == false, "manual tCL");
        Check(r.Find("tRCDRD").Source == "AOD" && r.Find("tRCDWR").StoredNumber == 40, "shared TRCD setting");
        Check(r.Find("tRFC").DiffersFrom("480") == false, "manual tRFC");
        Check(r.Find("FCLK").StoredNumber == 2000 && r.Find("FCLK").DiffersFrom("2000.00") == false,
            "AOD FCLK MHz");
        Check(r.Find("UCLK").DiffersFrom("3000.00", 3000) == false, "AOD UCLK 1:1");
        Check(r.Find("MEM VDD").DiffersFrom("1.10") == true, "stored 1200 mV vs AOD region 1.10 V");
        Check(r.Find("MEM VDDQ").DiffersFrom("1.20") == false, "matching 1.20 V");
        Check(r.Find("MEM VDD").Tooltip("1.10").Contains("Running: 1.10"), "tooltip records discrepancy");
        Check(r.Find("tRFC (ns)").DiffersFrom("160.00") == null, "derived timing cannot be compared directly");

        // Both menus active: AOD takes priority but the tooltip must disclose CBS.
        cbs[0x116] = 1;
        cbs[0x119] = 1;
        Set16(cbs, 0x11A, 40);
        Set16(cbs, 0x117, 5600);
        r = DrfxiSettingOrigins.FromBlobs(cbs, aod);
        Check(r.IsMatched && r.Find("tCL").OtherMenuManual, "both menus Manual");
        Check(r.Find("tCL").Source == "AOD", "inferred AOD precedence");
        Check(r.Find("tCL").Tooltip("36").Contains("priority") || r.Find("tCL").Tooltip("36").Contains("precedence"),
            "precedence warning");

        aod[0x3C] = 255;
        r = DrfxiSettingOrigins.FromBlobs(cbs, aod);
        Check(r.Find("tCL").Source == "CBS", "disabled AOD timing gate ignores stored Manual");
        Check(r.Find("Speed").Source == "CBS", "CBS fallback for speed");
        Check(r.Find("FCLK").Source == "AOD", "FCLK independent of AOD timing gate");

        // Fail closed for bad fingerprints, control bytes, enumerations and manual value ranges.
        byte[] savedCbs = (byte[])cbs.Clone(), savedAod = (byte[])aod.Clone();
        cbs[0] ^= 1;
        Check(!DrfxiSettingOrigins.FromBlobs(cbs, aod).IsMatched, "bad CBS signature");
        cbs = savedCbs;
        aod[0x3F] = 2;
        Check(!DrfxiSettingOrigins.FromBlobs(cbs, aod).IsMatched, "bad control byte");
        aod = (byte[])savedAod.Clone();
        cbs[0x260] = 254;
        Check(!DrfxiSettingOrigins.FromBlobs(cbs, aod).IsMatched, "undefined CBS FCLK option");
        cbs = (byte[])savedCbs.Clone();
        aod[0x192] = 1;
        Set16(aod, 0x193, 1700);
        Check(!DrfxiSettingOrigins.FromBlobs(cbs, aod).IsMatched, "out-of-range voltage");
        Check(!DrfxiSettingOrigins.FromBlobs(new byte[20], new byte[20]).IsMatched, "wrong varstore sizes");

        // The parser does not silently read this computer when a debug report has no blobs.
        Check(!DrfxiSettingOrigins.FromDebugReport("DRFXI", "1.15", "no blobs").IsMatched,
            "offline replay requires explicit raw evidence");
        string text = "AmdSetupRPL.base64: " + Convert.ToBase64String(savedCbs) + "\n"
            + "AodSetupRpl.base64: " + Convert.ToBase64String(savedAod) + "\n";
        var replay = DrfxiSettingOrigins.FromDebugReport("DRFXI", "1.15", text);
        Console.WriteLine("Offline replay: " + replay.Status);
        Check(replay.IsMatched,
            "offline debug report reproduces origin data");

        Console.WriteLine("OK: " + tests + " DRFXI setting-origin assertions");
    }
}
