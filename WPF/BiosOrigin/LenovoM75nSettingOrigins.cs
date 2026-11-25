using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZenTimings.BiosOrigin
{
    // Raven/Picasso DDR4 CBS IFR, identical in M33KT24A, M33KT32A and M33KT33A.
    // Only the saved CBS menu is classified, never the effective AGESA value.
    public static class LenovoM75nSettingOrigins
    {
        public const string Resolver = "lenovo-m75n-raven-ddr4-v1";
        public const string LayoutId = "lenovo-m75n-m33";
        private const string Evidence = "AMD CBS (AmdSetup), saved menu state only; firmware application unverified.";
        public static bool IsRelease(string version) =>
            string.Equals(version?.Trim(), "M33KT24A", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(version?.Trim(), "M33KT32A", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(version?.Trim(), "M33KT33A", StringComparison.OrdinalIgnoreCase);

        // Exact OneOf ranges (all contiguous except tCWL and speed).
        private static readonly (string row, int offset, int min, int max)[] Direct = {
            ("tCL",0xA1,8,33), ("tRCDRD",0xA2,8,27), ("tRCDWR",0xA3,8,27),
            ("tRP",0xA4,8,27), ("tRAS",0xA5,21,58), ("tRRDS",0xA8,4,12),
            ("tRRDL",0xA9,4,12), ("tWTRS",0xAC,2,14), ("tWTRL",0xAD,2,14),
            ("tRTP",0xC3,5,14), ("tCKE",0x159,1,31), ("tRDRDSC",0x144,1,11),
            ("tRDRDSD",0x150,1,15), ("tRDRDDD",0x152,1,15),
            ("tWRWRSC",0x15A,1,15), ("tWRWRSD",0x151,1,15), ("tWRWRDD",0x153,1,15),
            ("tRDWR",0x156,1,31), ("tWRRD",0x143,1,15)
        };
        private static readonly (string row, int ctrl, int offset, int width, int min, int max)[] Gated = {
            ("tRC",0xA6,0xA7,1,29,135), ("tFAW",0xAA,0xAB,1,6,54),
            ("tWR",0xAE,0xAF,1,10,81), ("tRDRDSCL",0xB3,0xB4,1,1,15),
            ("tWRWRSCL",0xB5,0xB6,1,1,63), ("tRFC",0xB7,0xB8,2,60,990),
            ("tRFC2",0xBA,0xBB,2,60,990), ("tRFC4",0xBD,0xBE,2,60,990)
        };
        private static readonly Dictionary<int,int> ClockMHz = new Dictionary<int,int> {
            {4,333},{6,400},{10,533},{20,667},{24,800},{25,1050},{26,1066},{28,933},
            {32,1067},{36,1200},{40,1333},{41,1367},{42,1400},{43,1433},{44,1467},
            {45,1500},{46,1533},{47,1567},{48,1600},{49,1633},{50,1667},{51,1700},
            {52,1733},{53,1767},{54,1800},{55,1833},{56,1867},{57,1900},{58,1933},
            {59,1967},{60,2000},{61,2033},{62,2067},{63,2100}
        };

        public static IEnumerable<string> DescribeRawOffsets(byte[] bytes)
        {
            yield return "  Lenovo M33 DDR4 reference offsets (UNVERIFIED for unknown firmware)";
            if (bytes == null || bytes.Length != 0x53F)
            {
                yield return "  Reference omitted: unexpected AmdSetup size.";
                yield break;
            }
            yield return "  AmdSetup header u32: 0x" + ReadU32(bytes, 0).ToString("X8") +
                " / 0x" + ReadU32(bytes, 4).ToString("X8");
            yield return "  Overclock[0x9F]=" + bytes[0x9F] + "; clock code[0xA0]=" + bytes[0xA0];
            foreach (var f in Direct)
                yield return "  " + f.row + "[0x" + f.offset.ToString("X") + "]=" + bytes[f.offset];
            foreach (var f in Gated)
                yield return "  " + f.row + " control[0x" + f.ctrl.ToString("X") + "]=" + bytes[f.ctrl] +
                    " value[0x" + f.offset.ToString("X") + "]=" +
                    (bytes[f.offset] | (f.width == 2 ? bytes[f.offset+1] << 8 : 0));
        }

        private static uint ReadU32(byte[] bytes, int offset) =>
            (uint)bytes[offset] | ((uint)bytes[offset + 1] << 8) |
            ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 3] << 24);

        private static SettingOriginRow Unknown(string row, string detail) =>
            new SettingOriginRow { Row = row, Source = "?", Details = detail + " " + Evidence };
        private static SettingOriginRow Manual(string row, int value, string kind = "timing", string label = null) =>
            new SettingOriginRow { Row = row, Source = "CBS", Kind = kind, StoredNumber = value,
                Stored = label ?? value.ToString(CultureInfo.InvariantCulture), Details = Evidence };
        private static SettingOriginReport Reject(SettingOriginReport r, string reason)
        {
            r.IsMatched = false; r.Plausibility = "failed: " + reason;
            r.Status = "Lenovo M75n rejected: " + reason; return r;
        }
        public static SettingOriginReport FromBlobs(byte[] amd, byte[] setup,
            BiosOriginLayoutCatalog.BiosOriginCatalogMatch firmware)
        {
            var r = new SettingOriginReport { LayoutId = LayoutId };
            if (firmware == null || firmware.Resolver != Resolver || firmware.LayoutId != LayoutId ||
                !BiosOriginLayoutCatalog.MayDisplayOrigins(firmware.Status))
                return Reject(r, "No matching trusted catalog release.");
            string error = firmware.CheckFingerprint(new Dictionary<string, byte[]> {
                {"AmdSetup",amd}, {"Setup",setup} });
            if (error != null) return Reject(r, error);
            // M33KT24A AFU dump contains three linked/mirrored AmdSetup records
            // with the same first two u32 words. Do not assume these words for 1.32/1.33.
            if (firmware.ReleaseVersion == "M33KT24A" &&
                (ReadU32(amd, 0) != 0x420C3F32u || ReadU32(amd, 4) != 0x00000164u))
                return Reject(r, "M33KT24A AmdSetup header differs from AFU NVRAM evidence.");
            int gate = amd[0x9F];
            if (gate != 1 && gate != 255) return Reject(r, "Overclock gate outside IFR options.");
            bool active = gate == 1;
            foreach (var f in Direct)
            {
                int v = amd[f.offset];
                if (active && v != 255 && (v < f.min || v > f.max))
                    return Reject(r, f.row + " outside IFR options.");
                r.Add(active && v != 255 ? Manual(f.row,v) :
                    Unknown(f.row, "CBS Auto or Overclock disabled; other firmware paths unverified."));
            }
            foreach (var f in Gated)
            {
                int c = amd[f.ctrl], v = amd[f.offset] | (f.width == 2 ? amd[f.offset+1] << 8 : 0);
                if (active && (c > 1 || (c == 1 && (v < f.min || v > f.max))))
                    return Reject(r, f.row + " outside IFR control/range.");
                r.Add(active && c == 1 ? Manual(f.row,v) :
                    Unknown(f.row, "CBS Auto or Overclock disabled; other firmware paths unverified."));
            }
            int cwl = amd[0xC2];
            if (active && cwl != 255 && Array.IndexOf(new[]{9,10,11,12,14,16,18,20},cwl) < 0)
                return Reject(r, "tCWL outside IFR options.");
            r.Add(active && cwl != 255 ? Manual("tCWL",cwl) : Unknown("tCWL","CBS Auto or gate inactive."));
            int code = amd[0xA0], mhz = 0;
            if (active && code != 255 && !ClockMHz.TryGetValue(code,out mhz))
                return Reject(r, "Memory Clock Speed outside IFR options.");
            // Labels are nominal rounded MHz (and some legacy encodings are odd).
            // Do not use them for numerical mismatch checks until confirmed on hardware.
            r.Add(active && code != 255 ? Manual("Speed",mhz*2,"enum",(mhz*2)+" MT/s (nominal)") :
                Unknown("Speed","CBS Auto or gate inactive."));
            r.Add(active && code != 255 ? Manual("MCLK",mhz,"enum",mhz+" MHz (nominal)") :
                Unknown("MCLK","CBS Auto or gate inactive."));
            foreach (var f in new[]{(row:"Cmd2T",offset:0x14E),(row:"GDM",offset:0x155),(row:"PwrDwn",offset:0x14C)})
            {
                int v=amd[f.offset];
                if (v != 255 && v > 1) return Reject(r,f.row+" outside IFR options.");
                r.Add(v == 255 ? Unknown(f.row,"CBS Auto; other firmware paths unverified.") :
                    Manual(f.row,v,"enum",f.row=="Cmd2T" ? (v==0?"1T":"2T") : (v==0?"Disabled":"Enabled")));
            }
            r.Add(new SettingOriginRow { Row = "tRFC (ns)", Source = r.Rows["tRFC"].Source,
                Details = "Derived from running tRFC/MCLK. " + Evidence });
            r.IsMatched=true; r.Plausibility="passed";
            r.Status="matched (Lenovo M75n "+firmware.ReleaseVersion+" DDR4 IFR-derived; hardware validation pending)";
            return r;
        }
    }
}
