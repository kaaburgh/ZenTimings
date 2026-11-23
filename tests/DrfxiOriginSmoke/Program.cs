using System;
using System.Linq;
using ZenTimings.BiosOrigin;
using ZenTimings.Common;

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

        // Phase 2: select a versioned catalog candidate and validate its live
        // variable fingerprints independently before running the DRFXI resolver.
        var catalog115 = BiosOriginLayoutCatalog.Select("DRFXI", "1.15");
        Check(catalog115 != null && catalog115.LayoutId == "drfxi-dragonrange-v1"
            && catalog115.Status == "verified", "DRFXI 1.15 verified catalog entry");
        var catalog117 = BiosOriginLayoutCatalog.Select("DRFXI", "BIOS 1.17");
        Check(catalog117 != null && catalog117.Status == "derived",
            "DRFXI 1.17 still only derived, not hardware verified");
        Check(BiosOriginLayoutCatalog.Select("NotDRFXI", "1.15") == null,
            "board token never matches a substring");
        Check(BiosOriginLayoutCatalog.Select("DRFXI", "1.15.9") == null,
            "version prefix is not enough to match a BIOS release");
        Check(BiosOriginLayoutCatalog.Select("DRFXI", "1.19") == null,
            "unsupported firmware never chooses a layout");
        Check(BiosOriginLayoutCatalog.Select("Other board", "1.15") == null,
            "unsupported board never chooses a layout");
        Check(catalog115.Resolver == "drfxi-dragonrange-v1"
            && catalog115.Variables.Count == 2, "catalog specifies trusted resolver and varstores");
        Check(catalog115.Variables.All(v => v.Guid.StartsWith("{") && v.Guid.EndsWith("}"))
            && catalog115.Variables.Any(v => v.Guid.Equals(
                "{3a997502-647a-4c82-998e-52ef9486a247}", StringComparison.OrdinalIgnoreCase)),
            "catalog GUIDs are canonical Win32 UEFI API strings with braces");
        Check(BiosOriginLayoutCatalog.MayDisplayOrigins("verified")
            && BiosOriginLayoutCatalog.MayDisplayOrigins("derived"),
            "only deterministic catalog evidence tiers are eligible for display");
        Check(!BiosOriginLayoutCatalog.MayDisplayOrigins("ai-proposed")
            && !BiosOriginLayoutCatalog.MayDisplayOrigins(null),
            "AI-proposed or unknown catalog evidence never silently enables badges");

        // MSI MAG B650 TOMAHAWK WIFI 7D75v1E is a recognized candidate only.
        // Its vendor/CBS/AOD semantics remain untrusted, so neither live nor offline
        // origin display may run a resolver. The opt-in debug capture uses this list.
        var msiCandidate = BiosOriginLayoutCatalog.Select(
            "MAG B650 TOMAHAWK WIFI (MS-7D75)", "E7D75AMS.1E0");
        Check(msiCandidate != null && msiCandidate.LayoutId == "msi-mag-b650-tomahawk-wifi-7d75v1e",
            "MSI board and exact BIOS image ID select the candidate layout");
        Check(msiCandidate.Status == "ai-proposed" && !BiosOriginLayoutCatalog.MayDisplayOrigins(msiCandidate.Status),
            "MSI image remains fail-closed until maintainer promotion");
        Check(msiCandidate.Resolver == "msi-mag-b650-tomahawk-wifi-7d75v1e-pending"
            && msiCandidate.Variables.Count == 6,
            "MSI candidate captures Setup, MSI OC, CBS RPL/PHX and AOD RPL/PHX variables");
        Check(msiCandidate.Variables.Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(
            new[] { "AmdSetupPHX", "AmdSetupRPL", "AodSetupPhx", "AodSetupRpl", "MsiOcInfoFlag", "Setup" }
                .OrderBy(n => n, StringComparer.Ordinal)),
            "MSI candidate variable names are complete and deterministic");
        Check(msiCandidate.Variables.All(v => v.Guid.StartsWith("{") && v.Guid.EndsWith("}")),
            "MSI candidate GUIDs are canonical Win32 UEFI API strings");
        Check(BiosOriginLayoutCatalog.Select("NOTMS-7D75", "E7D75AMS.1E0") == null,
            "MSI board token never matches a larger unrelated identifier");
        Check(BiosOriginLayoutCatalog.Select("MS-7D75", "E7D75AMS.1E01") == null,
            "MSI BIOS version matching rejects a longer version prefix");
        var msiReplay = DrfxiSettingOrigins.FromDebugReport("MS-7D75", "E7D75AMS.1E0", "no blobs");
        Check(!msiReplay.IsMatched && msiReplay.LayoutId == msiCandidate.LayoutId
            && msiReplay.Status.Contains("AI-proposed"),
            "offline replay identifies MSI candidate but refuses untrusted origin display");
        var fingerprintBlobs = new System.Collections.Generic.Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase) { { "AmdSetupRPL", cbs }, { "AodSetupRpl", aod } };
        Check(catalog115.CheckFingerprint(fingerprintBlobs) == null,
            "correct variable sizes and CBS header pass catalog fingerprint");
        byte[] brokenAmd = (byte[])cbs.Clone();
        brokenAmd[8] ^= 0x01;
        fingerprintBlobs["AmdSetupRPL"] = brokenAmd;
        Check(catalog115.CheckFingerprint(fingerprintBlobs)?.Contains("0x8") == true,
            "changed generation rejects catalog fingerprint");
        fingerprintBlobs["AmdSetupRPL"] = new byte[20];
        Check(catalog115.CheckFingerprint(fingerprintBlobs)?.Contains("size") == true,
            "incorrect varstore size rejects catalog fingerprint");
        fingerprintBlobs["AmdSetupRPL"] = cbs;
        fingerprintBlobs.Remove("AodSetupRpl");
        Check(catalog115.CheckFingerprint(fingerprintBlobs)?.Contains("missing") == true,
            "missing varstore rejects catalog fingerprint");
        fingerprintBlobs["AmdSetupRPL"] = cbs;
        fingerprintBlobs["AodSetupRpl"] = aod;

        // Synthesized full 1.15 owner configuration, documented in the AFU /O readback.
        // Not a raw NVRAM fixture: those bytes were not committed to the research repository.
        aod[0x3C] = 1;
        aod[0x3A] = 0xFF; // AodSmm default; != 1 writes Memory Target Speed.
        Set16(aod, 0x3D, 6000);
        aod[0x3F] = 1;
        Set16(aod, 0x40, 36);
        aod[0x42] = 1;
        Set16(aod, 0x43, 40);
        var manualTimings = new[]
        {
            new { Row = "tCL", Ctrl = 0x3F, Val = 36 },
            new { Row = "tRCDWR", Ctrl = 0x42, Val = 40 },
            new { Row = "tRCDRD", Ctrl = 0x42, Val = 40 },
            new { Row = "tRP", Ctrl = 0x45, Val = 40 },
            new { Row = "tRAS", Ctrl = 0x48, Val = 64 },
            new { Row = "tRC", Ctrl = 0x4B, Val = 104 },
            new { Row = "tWR", Ctrl = 0x4E, Val = 48 },
            new { Row = "tRFC", Ctrl = 0x51, Val = 480 },
            new { Row = "tRTP", Ctrl = 0x5A, Val = 16 },
            new { Row = "tRRDL", Ctrl = 0x5D, Val = 12 },
            new { Row = "tRRDS", Ctrl = 0x60, Val = 8 },
            new { Row = "tFAW", Ctrl = 0x63, Val = 32 },
            new { Row = "tWTRL", Ctrl = 0x66, Val = 24 },
            new { Row = "tWTRS", Ctrl = 0x69, Val = 7 }
        };
        foreach (var t in manualTimings)
        {
            aod[t.Ctrl] = 1;
            Set16(aod, t.Ctrl + 1, t.Val);
        }
        aod[0x1A9] = 0;  // Platform PMIC Control disabled on the owner's board.
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
        Check(r.Find("MCLK").Source == "AOD" && r.Find("MCLK").StoredNumber == 6000,
            "MCLK inherits AOD speed origin, 6000 MT/s stored");
        // AOD 0x3A selects two mutually exclusive firmware paths: default 0xFF
        // writes the Memory Target Speed token; == 1 writes alternate profile tokens.
        aod[0x3A] = 1;
        var profilePath = DrfxiSettingOrigins.FromBlobs(cbs, aod);
        Check(profilePath.IsMatched && profilePath.Find("Speed").Source == "Auto",
            "0x3A == 1 suppresses AOD Memory Target Speed");
        Check(profilePath.Find("MCLK").Source == "Auto",
            "0x3A == 1 must not report manual MCLK from AOD speed");
        Check(profilePath.Find("tCL").Source == "AOD" && profilePath.Find("FCLK").Source == "AOD",
            "alternate speed path does not disable timing or independent FCLK");
        aod[0x3A] = 0;
        Check(DrfxiSettingOrigins.FromBlobs(cbs, aod).Find("Speed").Source == "AOD",
            "0x3A == 0 also permits the AOD speed token");
        aod[0x3A] = 0xFF;
        Set16(aod, 0x3D, 6100);
        Check(!DrfxiSettingOrigins.FromBlobs(cbs, aod).IsMatched,
            "0x3A == 0xFF validates AOD speed range/step");
        aod[0x3A] = 1;
        Check(DrfxiSettingOrigins.FromBlobs(cbs, aod).IsMatched,
            "0x3A == 1 ignores unrelated target-speed value");
        aod[0x3A] = 0xFF;
        Set16(aod, 0x3D, 6000);

        foreach (var t in manualTimings)
            Check(r.Find(t.Row).Source == "AOD" && r.Find(t.Row).StoredNumber == t.Val,
                "full owner AOD timing " + t.Row);
        foreach (string row in new[] { "tRFC2", "tRFCsb", "tRDRDSCL", "tWRWRSCL",
            "tRDRDSC", "tRDWR", "tWRRD", "ProcOdt", "ProcCaDs", "ProcDqDs",
            "RttNomWr", "RttNomRd", "RttWr", "RttPark", "RttParkDqs", "PwrDwn" })
            Check(r.Find(row).Source == "Auto", "owner Auto setting " + row);
        Check(aod[0x1A9] == 0 && r.Find("CPU VDDIO").Source == "AOD",
            "Platform PMIC disabled while VDDIO Ctrl is Manual");
        Check(r.Find("MCLK").DiffersFrom("3000.00", 3000) == false, "MT/s to MCLK conversion");
        Check(r.Find("tCL").Source == "AOD" && r.Find("tCL").DiffersFrom("36") == false, "manual tCL");
        Check(r.Find("tRCDRD").Source == "AOD" && r.Find("tRCDWR").StoredNumber == 40, "shared TRCD setting");
        Check(r.Find("tRFC").DiffersFrom("480") == false, "manual tRFC");
        Check(r.Find("FCLK").StoredNumber == 2000 && r.Find("FCLK").DiffersFrom("2000.00") == false,
            "AOD FCLK MHz");
        Check(r.Find("UCLK").DiffersFrom("3000.00", 3000) == false, "AOD UCLK 1:1");
        Check(r.Find("MEM VDD").DiffersFrom("1.10") == true, "stored 1200 mV vs AOD region 1.10 V");
        Check(r.Find("CPU VDDIO").DiffersFrom("0.0000V") == null,
            "missing voltage sensor must not produce a false mismatch");
        Check(!r.Find("CPU VDDIO").Tooltip("0.0000V").Contains("Running: 0.0000V"),
            "tooltip must not treat absent voltage as a measurement");
        Check(r.Find("CPU VDDIO").Tooltip("0.0000V").Contains("No valid voltage reading"),
            "voltage tooltip reports missing sensor");
        Check(r.Find("CPU VDDIO").Details.Contains("extended CMOS"), "APU VDDIO hand-off note");
        Check(r.Find("FCLK").Details.Contains("independent setting"), "FCLK gate independence");
        Check(!r.Find("FCLK").Details.Contains("timing gate:"), "no fictitious FCLK timing gate");
        Check(r.Find("MEM VDD").Details.Contains("VDDIO Ctrl"), "voltage gate source");
        Check(!r.Find("tRFC (ns)").Tooltip("160.00").Contains("Saved: "),
            "derived tRFC ns has no empty saved-value line");
        Check(r.Find("MEM VDDQ").DiffersFrom("1.20") == false, "matching 1.20 V");
        Check(r.Find("MEM VDD").Tooltip("1.10").Contains("Running: 1.10"), "tooltip records discrepancy");
        Check(r.Find("tRFC (ns)").DiffersFrom("160.00") == null, "derived timing cannot be compared directly");
        // All DDR5 panel variants use these bound value paths, despite differently rendered labels.
        var bindings = new[]
        {
            new { Path = "MemoryFrequencyString", Row = "Speed" },
            new { Path = "Timings.CL", Row = "tCL" },
            new { Path = "Timings.PowerDown", Row = "PwrDwn" },
            new { Path = "PowerTable.UCLK", Row = "UCLK" },
            new { Path = "ApuVddio", Row = "CPU VDDIO" },
            new { Path = "Vmisc", Row = "VDD MISC" },
            new { Path = "SwaAdcV", Row = "MEM VDD" },
            new { Path = "Timings.RFCns", Row = "tRFC (ns)" },
            new { Path = "Timings.RefreshMode", Row = "Refresh" },
            new { Path = "ApobData.RttWr", Row = "RttWr" }
        };
        foreach (var b in bindings)
            Check(SettingOriginReport.RowForBindingPath(b.Path) == b.Row,
                "canonical mapping " + b.Path);
        Check(r.FindByBindingPath("Timings.PowerDown", "PowerDown").Source == "Auto",
            "legacy panel PowerDown row has badge");
        Check(r.FindByBindingPath("ApuVddio", "VDDIO (AOD)").Source == "AOD",
            "dynamic VDDIO source label has badge");
        Check(r.FindByBindingPath("Vmisc", "MISC (SVI3)").Source == "Auto",
            "dynamic VDD MISC source label has badge");
        Check(SettingOriginReport.RowForBindingPath("Unknown.Sensor") == null,
            "unknown binding does not assign a false origin");

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
        cbs = (byte[])savedCbs.Clone();
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
        Check(replay.Status.Contains("catalog status=verified"),
            "offline replay discloses catalog evidence tier");
        Check(replay.Find("Speed").Details.Contains("Layout catalog:")
            && replay.Find("Speed").Details.Contains("Verified against"),
            "origin tooltip discloses verified catalog provenance");
        var unverifiedReplay = DrfxiSettingOrigins.FromDebugReport("DRFXI", "1.17", text);
        Check(unverifiedReplay.IsMatched && unverifiedReplay.Status.Contains("catalog status=derived"),
            "BIOS 1.17 retains derived evidence status until hardware validation");
        Check(unverifiedReplay.Find("Speed").Details.Contains("no hardware readout verified"),
            "derived origin tooltip never claims physical verification");
        Check(!DrfxiSettingOrigins.FromDebugReport("NOTDRFXI", "1.15", text).IsMatched,
            "offline report cannot bypass board catalog gate");
        Check(!DrfxiSettingOrigins.FromDebugReport("DRFXI", "1.19", text).IsMatched,
            "offline report cannot bypass firmware version gate");
        byte[] invalidHeader = (byte[])savedCbs.Clone();
        invalidHeader[8] ^= 1;
        string invalidReport = "AmdSetupRPL.base64: " + Convert.ToBase64String(invalidHeader) + "\n"
            + "AodSetupRpl.base64: " + Convert.ToBase64String(savedAod) + "\n";
        var wrongFingerprint = DrfxiSettingOrigins.FromDebugReport("DRFXI", "1.15", invalidReport);
        Check(!wrongFingerprint.IsMatched && wrongFingerprint.Plausibility.StartsWith("failed:"),
            "offline report never accepts mismatched catalog fingerprint");

        // The DDR5 debug report previously dereferenced BMC.Table even though BMC was null.
        Check(BiosMemoryControllerDebug.Dump(null, false).Single().StartsWith("Not applicable:"),
            "DDR5 without BMC is not an error");
        Check(BiosMemoryControllerDebug.Dump(new byte[] { 0x2A }, false).Single().StartsWith("Not applicable:"),
            "unsupported memory type never emits a legacy APCB table");
        Check(BiosMemoryControllerDebug.Dump(null, true).Single().StartsWith("Not available:"),
            "DDR4 with missing table is reported without an exception");
        Check(BiosMemoryControllerDebug.Dump(new byte[0], true).Single().StartsWith("Not available:"),
            "DDR4 with empty table is reported without an exception");
        Check(BiosMemoryControllerDebug.Dump(new byte[] { 0x00, 0x7F, 0xFF }, true)
                .SequenceEqual(new[] { "Index 000: 00 (0)", "Index 001: 7F (127)", "Index 002: FF (255)" }),
            "loaded DDR4 APCB bytes preserve the original dump format");

        Console.WriteLine("OK: " + tests + " DRFXI setting-origin and BIOS debug assertions");
    }
}
