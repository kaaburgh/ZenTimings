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

    private static void RunMsiMagB650Tests()
    {
        var catalog = BiosOriginLayoutCatalog.Select(
            "MAG B650 TOMAHAWK WIFI (MS-7D75)", "1.E0");
        Check(catalog != null && catalog.LayoutId == "msi-mag-b650-tomahawk-7d75v1e"
            && catalog.Resolver == "msi-mag-b650-raphael-v1e" && catalog.Status == "derived",
            "MSI 7D75v1E selects its derived family-specific layout");
        // Two BIOS releases share every mapped timing offset but not the
        // fingerprint of MSI Setup (0x7C2 vs 0x7C4).
        var catalogE5 = BiosOriginLayoutCatalog.Select("MS-7D75", "1.E5");
        Check(catalogE5 != null && catalogE5.LayoutId == catalog.LayoutId
            && catalogE5.Resolver == catalog.Resolver && catalogE5.Status == "derived",
            "MSI 1E5 is a distinct derived release of the same mapping");
        Check(catalog.ExpectedSize("Setup") == 0x7C4
            && catalogE5.ExpectedSize("Setup") == 0x7C2
            && catalogE5.ExpectedSize("AmdSetupRPL") == 0x779
            && catalogE5.ExpectedSize("AodSetupRpl") == 0x320,
            "release-specific Setup size never changes shared AMD varstores");
        Check(BiosOriginLayoutCatalog.Select("MS-7D75", "E7D75AMS.1E5").ReleaseVersion == "1.E5"
            && BiosOriginLayoutCatalog.Select("MS-7D75", "7D75v1E5").ReleaseVersion == "1.E5"
            && BiosOriginLayoutCatalog.Select("MS-7D75", "E7D75AMS.1E0").ReleaseVersion == "1.E0",
            "SMBIOS and vendor-facing aliases resolve to their canonical releases");
        Check(BiosOriginLayoutCatalog.Select("MS-7D75", "1.E50") == null
            && BiosOriginLayoutCatalog.Select("MS-7D75", "1.E7") == null,
            "unsupported BIOS versions fail closed rather than prefix-match");
        Check(BiosOriginLayoutCatalog.DescribeSelection("MS-7D75", "1.R2").Contains("not registered")
            && BiosOriginLayoutCatalog.DescribeSelection("MS-7D75", "1.R2").Contains("1.E5"),
            "catalog diagnosis distinguishes known board from unknown BIOS release");
        Check(BiosOriginLayoutCatalog.DescribeSelection("NO-BOARD", "1.E0").Contains("No board token"),
            "catalog diagnosis distinguishes an entirely unknown board");
        Check(catalog.Variables.Count == 3
            && catalog.Variables.Single(v => v.Name == "AmdSetupRPL").Optional == false
            && catalog.Variables.Single(v => v.Name == "AodSetupRpl").Optional
            && catalog.Variables.Single(v => v.Name == "Setup").Optional,
            "MSI CBS is required while AOD and Setup are optional read-only diagnostics");

        byte[] amd = new byte[0x779];
        byte[] aod = new byte[0x320];
        byte[] setup = new byte[0x7C4];
        amd[0x116] = 255;
        aod[0x3C] = 255;
        for (int i = 0x3F; i <= 0x87; i += 3) aod[i] = 0;
        for (int i = 0x119; i <= 0x161; i += 3) amd[i] = 0;

        var blobs = new System.Collections.Generic.Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase)
        {
            { "AmdSetupRPL", amd }
        };
        Check(catalog.CheckFingerprint(blobs) == null,
            "MSI fingerprint accepts missing optional AOD and Setup variables");
        blobs["AodSetupRpl"] = aod;
        blobs["Setup"] = setup;
        Check(catalog.CheckFingerprint(blobs) == null,
            "MSI fingerprint accepts exact IFR varstore sizes");
        byte[] badHeader = (byte[])amd.Clone();
        badHeader[8] ^= 0x01;
        blobs["AmdSetupRPL"] = badHeader;
        Check(catalog.CheckFingerprint(blobs)?.Contains("0x8") == true
            && catalogE5.CheckFingerprint(blobs)?.Contains("0x8") == true,
            "both MSI releases check the identical three-word CBS NVAR default header");
        blobs["AmdSetupRPL"] = amd;
        byte[] setupE5 = new byte[0x7C2];
        blobs["Setup"] = setupE5;
        Check(catalogE5.CheckFingerprint(blobs) == null
            && catalog.CheckFingerprint(blobs)?.Contains("size") == true,
            "1E5 Setup accepted only by its own release fingerprint");
        blobs["Setup"] = setup;
        Check(catalog.CheckFingerprint(blobs) == null
            && catalogE5.CheckFingerprint(blobs)?.Contains("size") == true,
            "1E0 Setup accepted only by its own release fingerprint");

        var report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.IsMatched && report.LayoutId == "msi-mag-b650-tomahawk-7d75v1e",
            "MSI default synthetic variables pass IFR plausibility");
        Check(report.Find("tCL").Source == "?" && report.Find("Speed").Source == "?",
            "MSI defaults stay unknown until proprietary OC settings are mapped");
        Check(report.Find("tCL").Details.Contains("MSI")
            && report.Find("Speed").Details.Contains("0x51B"),
            "MSI unknown origins explain the independent vendor OC paths");
        Check(report.Setup != null && report.Setup.Length == 0x7C4,
            "MSI Setup varstore is retained for opt-in debug export");
        var e5Report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setupE5, catalogE5);
        Check(e5Report.IsMatched && e5Report.Setup.Length == 0x7C2
            && e5Report.Status.Contains("1.E5")
            && e5Report.Find("tCL").Source == "?",
            "MSI 1E5 resolves the same timing offsets with its own Setup size");
        Check(!MsiMagB650SettingOrigins.FromBlobs(amd, aod, setupE5, catalog).IsMatched
            && !MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalogE5).IsMatched,
            "wrong release fingerprint is rejected by resolver itself");

        amd[0x116] = 1;
        Set16(amd, 0x117, 6000);
        report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.Find("Speed").Source == "CBS"
            && report.Find("Speed").StoredNumber == 6000
            && report.Find("MCLK").DiffersFrom("3000.00", 3000) == false,
            "MSI CBS target speed and derived MCLK are decoded");
        aod[0x3C] = 1;
        report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.Find("Speed").Source == "?"
            && report.Find("Speed").Details.Contains("not interpreted"),
            "MSI AOD Speed path remains unknown until selector semantics are verified");
        aod[0x3C] = 255;

        amd[0x116] = 1;
        amd[0x119] = 1;
        Set16(amd, 0x11A, 30);
        report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.Find("tCL").Source == "CBS" && report.Find("tCL").StoredNumber == 30,
            "MSI CBS tCL IFR offsets and value are decoded");

        amd[0x119] = 0;
        aod[0x3C] = 1;
        aod[0x3F] = 1;
        Set16(aod, 0x40, 30);
        report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.Find("tCL").Source == "AOD" && report.Find("tCL").StoredNumber == 30,
            "MSI AOD tCL IFR offsets and value are decoded");

        amd[0x119] = 1;
        report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.Find("tCL").Source == "?",
            "MSI does not invent AOD-over-CBS precedence when both menus are Manual");

        amd[0x116] = 255;
        amd[0x119] = 0;
        aod[0x3C] = 255;
        aod[0x3F] = 0;
        setup[0x596] = 1;
        report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.Find("tCL").Source == "?"
            && report.Find("tCL").Details.Contains("EXPO")
            && report.Find("tCL").Details.Contains("0x596=1"),
            "MSI EXPO selector is disclosed as raw diagnostic, not falsely mapped to timing source");
        setup[0x596] = 0;
        setup[0x74] = 1;
        report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.Find("tCL").Source == "?"
            && report.Find("tCL").Details.Contains("0x74=1"),
            "top-level AMD EXPO selector is not omitted from MSI diagnostics");
        setup[0x74] = 0;
        Set16(setup, 0x597, 1);
        report = MsiMagB650SettingOrigins.FromBlobs(amd, aod, setup, catalog);
        Check(report.Find("tCL").Source == "?"
            && report.Find("tCL").Details.Contains("0x597=1"),
            "Memory Try It tuning source is diagnosed rather than classified as Auto");
        Set16(setup, 0x597, 0);

        report = MsiMagB650SettingOrigins.FromBlobs(amd, null, null, catalog);
        Check(report.IsMatched && report.Find("tCL").Source == "?"
            && report.Find("tCL").Details.Contains("not runtime-readable"),
            "missing optional MSI AOD/Setup variables fail closed for Auto classification");

        Check(!MsiMagB650SettingOrigins.FromBlobs(new byte[20], null, null, catalog).IsMatched,
            "MSI resolver rejects incorrect CBS varstore size");
        Check(!MsiMagB650SettingOrigins.FromBlobs(amd, aod, new byte[0x7C2], catalog).IsMatched,
            "1E5-sized Setup cannot be misidentified as 1E0");
        byte[] badGate = (byte[])amd.Clone();
        badGate[0x116] = 254;
        Check(!MsiMagB650SettingOrigins.FromBlobs(badGate, null, null, catalog).IsMatched,
            "MSI resolver rejects an undefined CBS timing gate");
        byte[] badSetup = new byte[0x7C4];
        badSetup[0x596] = 4;
        var badSetupReport = MsiMagB650SettingOrigins.FromBlobs(amd, null, badSetup, catalog);
        Check(!badSetupReport.IsMatched && badSetupReport.Setup.Length == 0x7C4,
            "MSI resolver rejects invalid EXPO selector but retains raw blob for opt-in diagnostics");
        byte[] badAxmp = new byte[0x7C4];
        badAxmp[0x73] = 3;
        Check(!MsiMagB650SettingOrigins.FromBlobs(amd, null, badAxmp, catalog).IsMatched,
            "MSI top-level A-XMP option 3 is invalid according to exact IFR options");
        badAxmp = new byte[0x7C4];
        badAxmp[0x595] = 3;
        Check(!MsiMagB650SettingOrigins.FromBlobs(amd, null, badAxmp, catalog).IsMatched,
            "MSI conditional A-XMP option 3 is invalid according to IFR");
        badAxmp[0x595] = 2;
        Check(MsiMagB650SettingOrigins.FromBlobs(amd, null, badAxmp, catalog).IsMatched,
            "MSI conditional A-XMP Profile 2 is valid");

        string debug = "AmdSetupRPL.base64: " + Convert.ToBase64String(amd) + "\n";
        var replay = DrfxiSettingOrigins.FromDebugReport(
            "MAG B650 TOMAHAWK WIFI (MS-7D75)", "1.E0", debug);
        Check(replay.IsMatched && replay.LayoutId == "msi-mag-b650-tomahawk-7d75v1e"
            && replay.Find("Speed").Source == "?",
            "MSI offline replay tolerates absent optional variables but remains fail-closed");
        string e5Debug = debug + "Setup.base64: " + Convert.ToBase64String(setupE5) + "\n";
        var e5Replay = DrfxiSettingOrigins.FromDebugReport(
            "MS-7D75", "E7D75AMS.1E5", e5Debug);
        var wrongReplay = DrfxiSettingOrigins.FromDebugReport(
            "MS-7D75", "E7D75AMS.1E0", e5Debug);
        Check(e5Replay.IsMatched && e5Replay.Status.Contains("1.E5")
            && e5Replay.Find("tCL").Source == "?",
            "MSI 1E5 replay uses selected fingerprint with the shared IFR mapping");
        Check(!wrongReplay.IsMatched && wrongReplay.Plausibility.StartsWith("failed:"),
            "MSI 1E5 Setup replayed under 1E0 must fail fingerprint validation");
    }

    private static void RunDiagnosticCaptureTests()
    {
        // Simulates a newer, unregistered MSI firmware. No Windows / UEFI API
        // is called, and the independent evidence path is not a badge resolver.
        byte[] amd = new byte[0x779];
        byte[] setup = new byte[0x7C2];
        byte[] pbs = new byte[] { 0x12, 0x34, 0x56 };
        var attempted = new System.Collections.Generic.List<string>();
        var lines = DrfxiSettingOrigins.FormatFirmwareDiagnostics(
            "MS-7D75", "1.R2", "Micro-Star", "AMI", "MS-7D75", "E7D75AMS.1R2",
            "ComboPI 1.3.x",
            (name, guid) =>
            {
                attempted.Add(name);
                if (name == "AmdSetupRPL")
                    return new FirmwareVariableProbeResult { Bytes = amd, Attributes = 7 };
                if (name == "Setup")
                    return new FirmwareVariableProbeResult { Bytes = setup, Attributes = 7 };
                if (name == "AMD_PBS_SETUP")
                    return new FirmwareVariableProbeResult { Bytes = pbs, Attributes = 7 };
                return new FirmwareVariableProbeResult { Win32Error = 203 };
            });
        string report = string.Join("\n", lines);
        Check(attempted.SequenceEqual(new[]
            { "AmdSetupRPL", "AodSetupRpl", "Setup", "AMD_PBS_SETUP" }),
            "unknown firmware captures all four variables without stopping on missing AOD");
        Check(report.Contains("not registered") && report.Contains("RAW EVIDENCE ONLY")
            && report.Contains("UNVERIFIED"),
            "unmatched catalog is explicitly marked as non-authoritative evidence");
        Check(report.Contains("Win32_BaseBoard.Product: MS-7D75")
            && report.Contains("Win32_BIOS.SMBIOSBIOSVersion: E7D75AMS.1R2")
            && report.Contains("AGESA: ComboPI 1.3.x"),
            "raw report includes exact WMI identity and AGESA context");
        Check(report.Contains("AmdSetupRPL.base64: " + Convert.ToBase64String(amd))
            && report.Contains("Setup.base64: " + Convert.ToBase64String(setup))
            && report.Contains("AMD_PBS_SETUP.base64: " + Convert.ToBase64String(pbs)),
            "unknown firmware exports each successfully read variable independently");
        Check(!report.Contains("AodSetupRpl.base64") && report.Contains("firmware variable is absent or is not runtime-visible (203)")
            && report.Contains("length: " + amd.Length) && report.Contains("SHA-256: "),
            "missing AOD records error while other variables include size and SHA-256");
        Check(report.Contains("CBS default header [+0,+4,+8]")
            && report.Contains("CBS tCL ctrl[0x119]")
            && report.Contains("MSI EXPO [0x74]")
            && report.Contains("MSI Memory Try It [0x597]"),
            "unverified probes cover CBS header, timing controls, AOD selectors and MSI EXPO");
        Check(!report.Contains("AOD tCL ctrl["),
            "no AOD probe is fabricated when its variable is absent");

        // Failure of one Win32 read must not prevent the following probes.
        attempted.Clear();
        var recovered = DrfxiSettingOrigins.FormatFirmwareDiagnostics(
            "MS-7D75", "1.R2", "MSI", "AMI", "MS-7D75", "1.R2", "AGESA",
            (name, guid) =>
            {
                attempted.Add(name);
                if (name == "AmdSetupRPL") throw new InvalidOperationException("injected fault");
                return new FirmwareVariableProbeResult { Bytes = new byte[4], Attributes = 7 };
            });
        Check(attempted.Count == 4 && string.Join("\n", recovered).Contains("injected fault")
            && string.Join("\n", recovered).Contains("AodSetupRpl.base64: "),
            "an exception in the first varstore read does not suppress later reads");

        byte[] tooShort = new byte[2];
        var raw = MsiMagB650SettingOrigins.DescribeRawReferenceOffsets("AmdSetupRPL", tooShort);
        Check(raw.Any(x => x.Contains("<out-of-range>")),
            "unverified probes safely handle short unknown firmware varstores");

        // A supported BIOS also goes through the same explicit diagnostic path;
        // no attempt to validate its layout or to create badges is made here.
        var known = DrfxiSettingOrigins.FormatFirmwareDiagnostics(
            "MS-7D75", "1.E5", "MSI", "AMI", "MS-7D75", "E7D75AMS.1E5", "AGESA",
            (name, guid) => new FirmwareVariableProbeResult { Win32Error = 203 });
        Check(string.Join("\n", known).Contains("Board and release matched") &&
            string.Join("\n", known).Contains("candidate only"),
            "known firmware capture remains diagnostic and cannot bypass fingerprint checks");
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

        RunMsiMagB650Tests();
        RunDiagnosticCaptureTests();

        Console.WriteLine("OK: " + tests + " BIOS setting-origin and BIOS debug assertions");
    }
}
