using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZenTimings.BiosOrigin
{
    // IFR-derived saved menu state, not effective AGESA settings.
    public static class AsrockSteelLegendSettingOrigins
    {
        public const string Resolver = "asrock-x670e-steel-legend-v1";
        public const string LayoutId = "asrock-x670e-steel-legend";
        // Index follows the 25 physical AMD timing controls. The ASRock 1.11
        // Setup fields are consecutive u16; 1.21 shifts by 9 bytes and inserts tREFI.
        private static readonly string[] Rows = {
            "tCL", "tRCDRD", "tRP", "tRAS", "tRC", "tWR", "tRFC", "tRFC2", "tRFCsb",
            "tRTP", "tRRDL", "tRRDS", "tFAW", "tWTRL", "tWTRS", "tRDRDSCL", "tRDRDSC",
            "tRDRDSD", "tRDRDDD", "tWRWRSCL", "tWRWRSC", "tWRWRSD", "tWRWRDD", "tWRRD", "tRDWR"
        };
        // Native Setup help ranges (not the wider storage range).
        private static readonly int[] NativeMin = {22,8,8,30,32,48,50,50,50,5,4,1,20,8,1,1,1,1,1,1,1,1,1,1,1};
        private static readonly int[] NativeMax = {64,62,62,126,255,96,2047,2047,2047,31,32,20,80,48,16,15,15,15,15,63,15,15,15,15,31};
        private static readonly int[] AmdMin = {22,8,8,30,32,48,50,50,50,5,4,4,20,8,2,1,1,1,1,1,1,1,1,1,1};
        private static readonly int[] AmdMax = {64,62,62,126,255,96,4095,4095,2047,31,32,20,80,48,16,15,15,15,15,63,15,15,15,15,31};
        private static int U16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
        private static bool GateValid(int b) => b == 1 || b == 255;
        private static bool InRange(int v, int min, int max, int step = 1) =>
            v >= min && v <= max && (v - min) % step == 0;
        private static int NativeOffset(int i, bool newer) =>
            0x163 + 2 * i + (newer ? (i >= 6 ? 11 : 9) : 0);
        private static int Min(int i, bool newer) =>
            newer && i == 3 ? 24 : newer && i == 5 ? 42 : newer && i == 12 ? 12 : NativeMin[i];

        public static SettingOriginReport FromBlobs(byte[] amd, byte[] aod, byte[] setup,
            BiosOriginLayoutCatalog.BiosOriginCatalogMatch firmware)
        {
            var report = new SettingOriginReport { LayoutId = LayoutId, IsMatched = false,
                AmdSetup = amd == null ? null : (byte[])amd.Clone(),
                AodSetup = aod == null ? null : (byte[])aod.Clone(),
                Setup = setup == null ? null : (byte[])setup.Clone() };
            if (firmware == null || firmware.Resolver != Resolver || firmware.LayoutId != LayoutId ||
                !BiosOriginLayoutCatalog.MayDisplayOrigins(firmware.Status))
                return Reject(report, "No matching ASRock catalog release.");
            var blobs = new Dictionary<string, byte[]> { { "AmdSetupRPL", amd },
                { "AodSetupRpl", aod }, { "Setup", setup } };
            string fingerprint = firmware.CheckFingerprint(blobs);
            if (fingerprint != null) return Reject(report, fingerprint);
            bool newer = firmware.ReleaseVersion == "1.21";
            if (!GateValid(amd[0x116]) || (aod != null && !GateValid(aod[0x3C])))
                return Reject(report, "AMD memory timing gate outside IFR options.");
            bool cbsGate = amd[0x116] == 1, aodGate = aod != null && aod[0x3C] == 1;
            int profileOffset = newer ? 0x1CE : 0x1B6;
            int profile = setup == null ? -1 : setup[profileOffset];
            int performance = setup == null ? -1 : setup[profileOffset + 1];
            if (setup != null && ((profile > 7 && profile != 255) ||
                (performance != 1 && performance != 2 && performance != 255)))
                return Reject(report, "ASRock profile/performance selector outside IFR options.");
            bool profileAuto = setup != null && profile == (newer ? 0 : 255) && performance == 255;
            string context = "ASRock " + firmware.ReleaseVersion + " profile=" + profile +
                ", performance=" + performance + " (-1 means unreadable). Saved menu state only; precedence and effective AGESA application unverified.";
            for (int i = 0; i < Rows.Length; i++)
            {
                int co = 0x119 + 3 * i, ao = 0x3F + 3 * i, so = NativeOffset(i, newer);
                int cc = amd[co], ac = aod == null ? -1 : aod[ao];
                int cv = U16(amd, co + 1), av = aod == null ? 0 : U16(aod, ao + 1);
                int sv = setup == null ? 0 : U16(setup, so);
                if (cc > 1 || ac > 1) return Reject(report, Rows[i] + " control outside IFR options.");
                bool cm = cbsGate && cc == 1, am = aodGate && ac == 1;
                if ((cm && !InRange(cv, AmdMin[i], AmdMax[i])) ||
                    (am && !InRange(av, AmdMin[i], AmdMax[i], i == 0 ? 2 : i == 5 ? 6 : 1)) ||
                    (sv != 0 && !InRange(sv, Min(i, newer), NativeMax[i], i == 5 ? 6 : 1)))
                    return Reject(report, Rows[i] + " value outside IFR/help range.");
                string raw = "Setup[0x" + so.ToString("X") + "]=" + (setup == null ? "unreadable" : sv.ToString()) +
                    "; CBS manual=" + cm + " value=" + cv + "; AOD manual=" + am + " value=" +
                    (aod == null ? "unreadable" : av.ToString()) + ". " + context;
                report.Add(Choose(Rows[i], "timing", sv, cm, cv, am, av, setup != null && aod != null && profileAuto, raw));
            }
            // Native tRCD is explicitly RD; do not assign its value to the WR row.
            report.Add(Unknown("tRCDWR", "Separate native WR mapping is not established. " + context));
            int speedOffset = newer ? 0x16A : 0x161;
            int speed = setup == null ? 0 : U16(setup, speedOffset);
            if (speed != 0 && !(speed >= 3200 && speed <= 16000 && speed % 200 == 0))
                return Reject(report, "ASRock DRAM Frequency outside IFR options.");
            int cbsSpeed = U16(amd, 0x117);
            if (cbsGate && !(cbsSpeed == 2000 || cbsSpeed == 2400 ||
                (cbsSpeed >= 3200 && cbsSpeed <= 11200 && cbsSpeed % 200 == 0)))
                return Reject(report, "CBS target speed outside IFR options.");
            // AOD speed has an unresolved hidden selector (0x3A).
            var speedRow = Choose("Speed", "speed", speed, cbsGate, cbsSpeed, aodGate, 0,
                setup != null && aod != null && profileAuto && !aodGate,
                "ASRock DRAM Frequency[0x" + speedOffset.ToString("X") + "]=" + speed +
                "; CBS target=" + cbsSpeed + "; AOD selector semantics unresolved. " + context);
            report.Add(speedRow);
            report.Add(new SettingOriginRow { Row = "MCLK", Source = speedRow.Source, Stored = speedRow.Stored,
                StoredNumber = speedRow.StoredNumber, Kind = "mclk", Details = "Half the saved DDR data rate. " + speedRow.Details });
            if (newer)
            {
                int refresh = setup == null ? 0 : U16(setup, 0x178);
                if (refresh != 0 && refresh < 50) return Reject(report, "Native tREFI below help minimum.");
                report.Add(Choose("tREFI", "timing", refresh, cbsGate, U16(amd, 0x2E1), false, 0,
                    setup != null && aod != null && profileAuto && !cbsGate && !aodGate,
                    "Native tREFI[0x178]=" + refresh + "; CBS[0x2E1]=" + U16(amd, 0x2E1) +
                    "; CBS default/sentinel semantics unresolved. " + context));
            }
            else report.Add(Unknown("tREFI", "No tREFI field in the analyzed 1.11 timing forms. " + context));
            var rfc = report.Rows["tRFC"];
            report.Add(new SettingOriginRow { Row = "tRFC (ns)", Source = rfc.Source,
                Details = "Derived from running tRFC/MCLK. " + rfc.Details });
            report.IsMatched = true;
            report.Plausibility = "passed";
            report.Status = "matched (ASRock " + firmware.ReleaseVersion + " IFR-derived; hardware validation pending)";
            return report;
        }

        // Explicit opt-in diagnostics only; offsets are never used to infer badges here.
        public static IEnumerable<string> DescribeRawSetupOffsets(byte[] bytes,
            BiosOriginLayoutCatalog.BiosOriginCatalogMatch firmware)
        {
            if (bytes == null || firmware == null || firmware.Resolver != Resolver) yield break;
            bool newer = firmware.ReleaseVersion == "1.21";
            if (bytes.Length != firmware.ExpectedSize("Setup"))
            {
                yield return "  ASRock reference offsets omitted: unexpected Setup size.";
                yield break;
            }
            int p = newer ? 0x1CE : 0x1B6, s = newer ? 0x16A : 0x161;
            yield return "  ASRock profile[0x" + p.ToString("X") + "]=" + bytes[p] +
                "; performance[0x" + (p + 1).ToString("X") + "]=" + bytes[p + 1];
            yield return "  ASRock frequency[0x" + s.ToString("X") + "]=" + U16(bytes, s);
            for (int i = 0; i < Rows.Length; i++)
            {
                int offset = NativeOffset(i, newer);
                yield return "  ASRock " + Rows[i] + " [0x" + offset.ToString("X") + "]=" + U16(bytes, offset);
            }
            if (newer) yield return "  ASRock tREFI[0x178]=" + U16(bytes, 0x178);
        }

        private static SettingOriginReport Reject(SettingOriginReport report, string reason)
        {
            report.IsMatched = false;
            report.Plausibility = "failed: " + reason;
            report.Status = "ASRock layout rejected: " + reason;
            return report;
        }

        private static SettingOriginRow Unknown(string row, string details) =>
            new SettingOriginRow { Row = row, Source = "?", Details = details };

        private static SettingOriginRow Choose(string row, string kind, int native, bool cbsManual,
            int cbs, bool aodManual, int aod, bool allSourcesKnown, string details)
        {
            int count = (native != 0 ? 1 : 0) + (cbsManual ? 1 : 0) + (aodManual ? 1 : 0);
            // Do not infer Auto from zero/defaults, missing variables or profiles.
            // Even equal non-zero values in multiple menus do not establish precedence.
            if (!allSourcesKnown || count != 1)
                return Unknown(row, "No unique unambiguous saved manual source. " + details);
            string source = native != 0 ? "Setup" : cbsManual ? "CBS" : "AOD";
            int value = native != 0 ? native : cbsManual ? cbs : aod;
            return new SettingOriginRow { Row = row, Kind = kind, Source = source, StoredNumber = value,
                Stored = value.ToString(CultureInfo.InvariantCulture), Details = details };
        }
    }
}
