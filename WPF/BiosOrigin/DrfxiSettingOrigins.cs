using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace ZenTimings.BiosOrigin
{
    // Read-only view of settings saved in UEFI NVRAM, NOT a claim about which values AGESA applied.
    // All byte offsets / allowed values come from the checked-in DRFXI IFR-derived JSON resource.
    public sealed class SettingOriginRow
    {
        public string Row { get; internal set; }
        public string Source { get; internal set; }
        public string Stored { get; internal set; }
        public double? StoredNumber { get; internal set; }
        public string Kind { get; internal set; }
        public string Details { get; internal set; }
        public bool OtherMenuManual { get; internal set; }

        public string Badge
        {
            get
            {
                switch (Source)
                {
                    case "AOD": return "M·AOD";
                    case "CBS": return "M·CBS";
                    case "Auto": return "A";
                    case "AGESA": return "·";
                    default: return "?";
                }
            }
        }

        // Returns null when the running reading cannot safely be compared with the stored setting.
        // In particular, hardware impedance encodings and AGESA-computed timings are NOT comparable.
        public bool? DiffersFrom(string displayedValue, double mclk = 0)
        {
            if ((Source != "AOD" && Source != "CBS") || !StoredNumber.HasValue)
                return null;

            double expected = StoredNumber.Value;
            double tolerance;
            switch (Kind)
            {
                case "timing": tolerance = 0.5; break;
                case "speed": tolerance = 2.0; break;
                case "mclk": expected /= 2.0; tolerance = 2.0; break;
                case "fclk": tolerance = 2.0; break;
                case "voltage": expected /= 1000.0; tolerance = 0.015; break;
                case "uclk":
                    if (mclk <= 0) return null;
                    expected = expected == 1 ? mclk : mclk / 2.0;
                    tolerance = 2.0;
                    break;
                default: return null;
            }

            // Values in the UI use either invariant decimal numbers or numbers plus units.
            if (string.IsNullOrWhiteSpace(displayedValue) || displayedValue.Contains("N/A"))
                return null;

            Match number = Regex.Match(displayedValue, @"[-+]?\d+(?:[.,]\d+)?");
            if (!number.Success)
                return null;

            double running;
            if (!double.TryParse(number.Value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out running))
                return null;
            // Zero or negative voltage means an absent sensor reading in MainViewModel,
            // not a physically measured 0 V. Keep UI and snapshot mismatch logic consistent.
            if (Kind == "voltage" && running <= 0)
                return null;
            return Math.Abs(expected - running) > tolerance;
        }

        public string Tooltip(string displayedValue, double mclk = 0, string runningSource = null)
        {
            string tooltip = Details ?? "";
            if (Source == "AOD" || Source == "CBS")
            {
                if (!string.IsNullOrWhiteSpace(Stored))
                    tooltip += "\nSaved: " + Stored;
                if (OtherMenuManual)
                    tooltip += "\nBoth AMD Overclocking and AMD CBS are Manual; AOD precedence is inferred, not verified in ABL.";
                bool? differs = DiffersFrom(displayedValue, mclk);
                if (differs.HasValue)
                {
                    tooltip += "\nRunning: " + displayedValue;
                    if (!string.IsNullOrEmpty(runningSource))
                        tooltip += " (" + runningSource + ")";
                    if (differs.Value)
                        tooltip += "\n≠ Saved and running values differ. A BIOS save may require reboot, or firmware may override/clamp the value.";
                }
                else
                {
                    tooltip += Kind == "voltage" && (string.IsNullOrWhiteSpace(displayedValue) ||
                        displayedValue.StartsWith("0.000", StringComparison.Ordinal))
                        ? "\nNo valid voltage reading available; comparison skipped."
                        : "\nSaved and running values cannot be reliably compared.";
                }
            }
            return tooltip;
        }
    }

    public sealed class SettingOriginReport
    {
        private readonly Dictionary<string, SettingOriginRow> rows =
            new Dictionary<string, SettingOriginRow>(StringComparer.OrdinalIgnoreCase);

        internal byte[] AmdSetup { get; set; }
        internal byte[] AodSetup { get; set; }
        public string LayoutId { get; internal set; }
        public string Status { get; internal set; }
        public string Plausibility { get; internal set; }
        public bool IsMatched { get; internal set; }
        public IReadOnlyDictionary<string, SettingOriginRow> Rows => rows;

        internal void Add(SettingOriginRow row) => rows[row.Row] = row;

        public SettingOriginRow Find(string label)
        {
            if (!IsMatched || string.IsNullOrWhiteSpace(label))
                return null;
            // Fallback for legacy programmatically populated, unbound rows only.
            if (label.StartsWith("UCLK", StringComparison.OrdinalIgnoreCase)) label = "UCLK";
            if (label.StartsWith("CPU VDDIO", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("VDDIO (", StringComparison.OrdinalIgnoreCase)) label = "CPU VDDIO";
            if (label.StartsWith("VDD MISC", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("MISC (", StringComparison.OrdinalIgnoreCase)) label = "VDD MISC";
            if (label == "PowerDown") label = "PwrDwn";
            SettingOriginRow row;
            return rows.TryGetValue(label, out row) ? row : null;
        }

        /// <summary>Canonical origin key for a UI value binding, independent of its dynamic label.</summary>
        public static string RowForBindingPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // The same paths are used by both legacy and current DDR5 panels.
            switch (path)
            {
                case "MemoryFrequencyString": return "Speed";
                case "PowerTable.MCLK": return "MCLK";
                case "PowerTable.FCLK": return "FCLK";
                case "PowerTable.UCLK": return "UCLK";
                case "SwaAdcV": return "MEM VDD";
                case "SwbAdcV": return "MEM VDDQ";
                case "ApuVddio": return "CPU VDDIO";
                case "VppAdcV": return "MEM VPP";
                case "Vmisc": return "VDD MISC";
                case "CcdlData": return "tCCDL";
                case "Timings.PowerDown": return "PwrDwn";
                case "Timings.RefreshMode": return "Refresh";
                case "Timings.RFCns": return "tRFC (ns)";
                case "Timings.BGSAlt": return "BGS Alt";
                case "Timings.BGS": return "BGS";
                case "Timings.GDM": return "GDM";
                case "Timings.Cmd2T": return "Cmd2T";
                case "Timings.Nitro": return "Nitro";
                case "ApobData.ProcOdt": return "ProcOdt";
                case "ApobData.ProcCaDs": return "ProcCaDs";
                case "ApobData.ProcDqDs": return "ProcDqDs";
                case "ApobData.DramDataDs": return "DramDqDs";
                case "ApobData.RttNomWr": return "RttNomWr";
                case "ApobData.RttNomRd": return "RttNomRd";
                case "ApobData.RttWr": return "RttWr";
                case "ApobData.RttPark": return "RttPark";
                case "ApobData.RttParkDqs": return "RttParkDqs";
            }
            if (path.StartsWith("Timings.", StringComparison.Ordinal))
                return "t" + path.Substring("Timings.".Length);
            // Unknown source fields must not be relabelled using an unrelated row caption.
            return null;
        }

        public SettingOriginRow FindByBindingPath(string bindingPath, string fallbackLabel)
        {
            string row = RowForBindingPath(bindingPath);
            if (row != null) return Find(row);
            return string.IsNullOrWhiteSpace(bindingPath) ? Find(fallbackLabel) : null;
        }
    }

    public static class DrfxiSettingOrigins
    {
        public const string LayoutResource = "ZenTimings.Resources.BiosLayouts.drfxi-dragonrange-v1.json";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFirmwareEnvironmentVariableExW")]
        private static extern uint GetFirmwareEnvironmentVariableEx(
            string name, string guid, byte[] buffer, uint size, out uint attributes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupPrivilegeValueW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TokenPrivileges
        {
            public int Count;
            public long Luid;
            public int Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
            ref TokenPrivileges privileges, int bufferLength, IntPtr previousState, IntPtr returnLength);

        private static int EnablePrivilege()
        {
            IntPtr token;
            // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token))
                return Marshal.GetLastWin32Error();
            try
            {
                long luid;
                if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out luid))
                    return Marshal.GetLastWin32Error();
                var privilege = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, ref privilege, 0, IntPtr.Zero, IntPtr.Zero))
                    return Marshal.GetLastWin32Error();
                // ERROR_NOT_ALL_ASSIGNED (1300) can accompany an otherwise successful API call.
                return Marshal.GetLastWin32Error();
            }
            finally { CloseHandle(token); }
        }

        private static byte[] ReadVariable(string name, string guid, out uint attributes, out int error)
        {
            var buffer = new byte[0x10000];
            uint length = GetFirmwareEnvironmentVariableEx(name, guid, buffer, (uint)buffer.Length, out attributes);
            if (length == 0)
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }
            error = 0;
            Array.Resize(ref buffer, (int)length);
            return buffer;
        }

        private static string ErrorMeaning(int error)
        {
            switch (error)
            {
                case 1: return "legacy BIOS or firmware API unavailable";
                case 203: return "firmware variable is absent or is not runtime-visible";
                case 1314: return "SeSystemEnvironmentPrivilege is missing";
                case 1300: return "SeSystemEnvironmentPrivilege is not assigned to this token";
                default: return "Win32 error " + error;
            }
        }

        public static SettingOriginReport ReadFromFirmware(string board, string biosVersion)
        {
            try
            {
                var candidate = BiosOriginLayoutCatalog.Select(board, biosVersion);
                if (candidate == null)
                    return Unavailable("No supported origin layout catalog entry for this board / BIOS.");
                if (candidate.Resolver != "drfxi-dragonrange-v1")
                    return Unavailable("Origin layout has no trusted resolver: " + candidate.LayoutId);
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                    return Unavailable("Windows UEFI API is not available on this OS.");

                int privilegeError = EnablePrivilege();
                if (privilegeError != 0)
                    return Unavailable("Cannot enable firmware-read privilege: " + ErrorMeaning(privilegeError));

                var blobs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var variable in candidate.Variables)
                {
                    uint attributes;
                    int error;
                    byte[] bytes = ReadVariable(variable.Name, variable.Guid, out attributes, out error);
                    if (bytes == null)
                        return Unavailable("UEFI read failed: " + variable.Name + ": " + ErrorMeaning(error));
                    blobs.Add(variable.Name, bytes);
                }
                return ResolveCatalogCandidate(candidate, blobs);
            }
            catch (Exception ex)
            {
                return Unavailable("UEFI origin catalog/read failed: " + ex.Message);
            }
        }

        public static SettingOriginReport FromDebugReport(string board, string biosVersion, string reportText)
        {
            try
            {
                var candidate = BiosOriginLayoutCatalog.Select(board, biosVersion);
                if (candidate == null)
                    return Unavailable("No supported origin layout for board / BIOS in debug report.");
                if (candidate.Resolver != "drfxi-dragonrange-v1")
                    return Unavailable("Origin layout has no trusted offline resolver: " + candidate.LayoutId);
                var blobs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var variable in candidate.Variables)
                {
                    byte[] bytes = DecodeReportBlob(reportText, variable.Name);
                    if (bytes == null)
                        return Unavailable("This debug report lacks BIOS setup variable " + variable.Name + ".");
                    blobs.Add(variable.Name, bytes);
                }
                return ResolveCatalogCandidate(candidate, blobs);
            }
            catch (Exception ex)
            {
                return Unavailable("Offline origin layout catalog failed: " + ex.Message);
            }
        }

        private static SettingOriginReport ResolveCatalogCandidate(
            BiosOriginLayoutCatalog.BiosOriginCatalogMatch candidate,
            IDictionary<string, byte[]> blobs)
        {
            string error = candidate.CheckFingerprint(blobs);
            if (error != null)
            {
                var rejected = Unavailable("Layout catalog fingerprint mismatch: " + error);
                rejected.LayoutId = candidate.LayoutId;
                rejected.Plausibility = "failed: " + error;
                return rejected;
            }

            // The catalog merely selects a candidate. The existing IFR-derived DRFXI
            // resolver remains responsible for *every* field's plausibility validation.
            SettingOriginReport resolved = FromBlobs(blobs["AmdSetupRPL"], blobs["AodSetupRpl"]);
            if (resolved.IsMatched)
                resolved.Status += "; catalog status=" + candidate.Status
                    + ", layout=" + candidate.LayoutId;
            return resolved;
        }

        private static byte[] DecodeReportBlob(string report, string name)
        {
            if (report == null) return null;
            Match m = Regex.Match(report, "^" + name + @"\.base64:\s*([A-Za-z0-9+/=]+)\s*$", RegexOptions.Multiline);
            try { return m.Success ? Convert.FromBase64String(m.Groups[1].Value) : null; }
            catch (FormatException) { return null; }
        }

        private static SettingOriginReport Unavailable(string status)
        {
            return new SettingOriginReport
            {
                LayoutId = null, IsMatched = false, Plausibility = "not checked", Status = status
            };
        }

        public static SettingOriginReport FromBlobs(byte[] amd, byte[] aod)
        {
            try
            {
                Layout layout = LoadLayout();
                string reason = CheckPlausibility(layout, amd, aod);
                if (reason != null)
                    return new SettingOriginReport
                    {
                        LayoutId = layout.Id, IsMatched = false, Plausibility = "failed: " + reason,
                        Status = "DRFXI layout rejected: " + reason
                    };

                var report = new SettingOriginReport
                {
                    LayoutId = layout.Id,
                    IsMatched = true,
                    Plausibility = "passed",
                    Status = "matched (saved setup settings; AOD-over-CBS priority is inferred)",
                    AmdSetup = (byte[])amd.Clone(), AodSetup = (byte[])aod.Clone()
                };

                bool aodGate = aod[0x3C] == 1, cbsGate = amd[0x116] == 1;
                foreach (TimingField t in layout.Timings)
                {
                    bool am = aodGate && aod[t.AodCtrl] == 1;
                    bool cm = cbsGate && amd[t.CbsCtrl] == 1;
                    report.Add(Choose(t.Row, "timing", am, cm, U16(aod, t.AodValue),
                        U16(amd, t.CbsValue), aodGate, cbsGate));
                }

                foreach (EnumField e in layout.Enums)
                {
                    int aodValue = GetNumber(aod, e.AodOffset, e.AodOptions);
                    int cbsValue = e.CbsOffset.HasValue ? GetNumber(amd, e.CbsOffset.Value, e.CbsOptions) : 255;
                    int aodAuto = e.Row == "FCLK" ? 65535 : 255;
                    bool am = aodValue != aodAuto;
                    bool cm = e.CbsOffset.HasValue && cbsValue != 255;
                    // Fields without a Timing Settings gate have their own APCB paths.
                    SettingOriginRow entry = Choose(e.Row, e.Row == "FCLK" ? "fclk" :
                        e.Row == "UCLK" ? "uclk" : "enum", am, cm, aodValue, cbsValue, true, true);
                    entry.Details = (entry.Source == "Auto" ? "Auto: " :
                        entry.Source == "AOD" ? "AMD Overclocking (AodSetupRpl): " : "AMD CBS (AmdSetupRPL): ")
                        + e.Row + " is an independent setting; Active Memory Timing Settings gates do not control it."
                        + "\nSource is saved UEFI setup, not a live hardware read.";
                    if (entry.Source == "AOD" || entry.Source == "CBS")
                    {
                        List<Option> options = entry.Source == "AOD" ? e.AodOptions : e.CbsOptions;
                        int storedCode = entry.Source == "AOD" ? aodValue : cbsValue;
                        Option option = options.FirstOrDefault(o => o.Value == storedCode);
                        if (option != null)
                        {
                            entry.Stored = option.Label + " (code " + storedCode + ")";
                            if (e.Row == "FCLK")
                            {
                                Match freq = Regex.Match(option.Label ?? "", @"(\d+)\s*MHz");
                                int mhz;
                                entry.StoredNumber = freq.Success && int.TryParse(freq.Groups[1].Value, out mhz)
                                    ? (double?)mhz : null;
                            }
                        }
                    }
                    report.Add(entry);
                }

                // AodSmm writes Memory Target Speed only when the timing gate is enabled
                // and the hidden 0x3A selector is NOT 1 (the firmware default is 0xFF).
                // When 0x3A == 1, firmware takes a separate, not-yet-decoded profile path.
                bool aodSpeed = aodGate && aod[0x3A] != 1;
                report.Add(Choose("Speed", "speed", aodSpeed, cbsGate, U16(aod, 0x3D),
                    U16(amd, 0x117), aodSpeed, cbsGate));

                foreach (VoltageField v in layout.Voltages)
                {
                    bool gate = v.GateOptions.Contains((int)aod[v.Gate]);
                    var entry = Choose(v.Row, "voltage", gate, false, U16(aod, v.Value), 0, gate, false);
                    if (entry.Source == "AOD")
                        entry.Stored += " mV";
                    string gateName = v.Gate == 0x192 ? "VDDIO Ctrl" :
                        v.Gate == 0x19B ? "VPP Ctrl" : "VDD Misc Control";
                    string gateState = v.Gate == 0x192
                        ? (aod[v.Gate] == 1 ? "Manual" : aod[v.Gate] == 2 ? "Separate" : "Auto")
                        : (gate ? "Manual" : "Auto");
                    entry.Details = "AMD Overclocking (AodSetupRpl) " + gateName +
                        ": " + gateState + " (0x" + v.Gate.ToString("X3") + ")."
                        + (gate ? "\nStored voltage control is active." : "\nVoltage control is Auto; AGESA selects the value.")
                        + "\nSaved NVRAM does not independently confirm the running voltage.";
                    if (v.Row == "CPU VDDIO")
                        entry.Details += "\nAPU VDDIO is handed to firmware through extended CMOS (0xA0/0xA1), not an APCB token.";
                    report.Add(entry);
                }
                foreach (string row in layout.AgesaRows)
                    report.Add(new SettingOriginRow { Row = row, Source = "AGESA",
                        Details = "No manual setting for this row in the DRFXI 1.15/1.17 AMD memory menus; AGESA computes it." });
                foreach (string row in layout.UnknownRows)
                    report.Add(new SettingOriginRow { Row = row, Source = "?",
                        Details = "The DRFXI menu field is not mapped unambiguously to this ZenTimings row." });
                // tRFC(ns) is derived from the currently running tRFC, not independently configurable.
                report.Add(new SettingOriginRow { Row = "tRFC (ns)", Source = report.Find("tRFC").Source,
                    Details = "Derived from the running tRFC and MCLK; source classification follows tRFC." });
                report.Add(new SettingOriginRow { Row = "MCLK", Source = report.Find("Speed").Source,
                    Stored = report.Find("Speed").Stored, StoredNumber = report.Find("Speed").StoredNumber,
                    Kind = "mclk", Details = "MCLK is half the DDR data rate selected in the BIOS.",
                    OtherMenuManual = report.Find("Speed").OtherMenuManual });
                return report;
            }
            catch (Exception ex)
            {
                return Unavailable("DRFXI layout load / resolve failed: " + ex.Message);
            }
        }

        private static SettingOriginRow Choose(string row, string kind, bool aodManual, bool cbsManual,
            int aodValue, int cbsValue, bool aodGate, bool cbsGate)
        {
            string source = aodManual ? "AOD" : cbsManual ? "CBS" : "Auto";
            int stored = aodManual ? aodValue : cbsValue;
            return new SettingOriginRow
            {
                Row = row,
                Kind = kind,
                Source = source,
                Stored = (aodManual || cbsManual) ? stored.ToString(CultureInfo.InvariantCulture) : null,
                StoredNumber = aodManual || cbsManual ? (double?)stored : null,
                OtherMenuManual = aodManual && cbsManual,
                Details = source == "Auto"
                    ? "Auto: neither AMD Overclocking nor AMD CBS applies a manual setting. AGESA decides the running value."
                    : (source == "AOD" ? "AMD Overclocking (AodSetupRpl)" : "AMD CBS (AmdSetupRPL)")
                        + "\nAOD timing gate: " + (aodGate ? "Enabled" : "Auto/disabled")
                        + "; CBS timing gate: " + (cbsGate ? "Enabled" : "Auto/disabled")
                        + "\nThe setting was saved in NVRAM; application to hardware is not independently verified."
            };
        }

        private static int U16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);

        private static uint U32(byte[] data, int offset) =>
            (uint)data[offset] | ((uint)data[offset + 1] << 8) |
            ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);

        private static int GetNumber(byte[] data, int offset, List<Option> options)
        {
            // Only AOD FCLK uses a two-byte enum. All other DRFXI enumerations are one byte.
            return options.Any(x => x.Value > 255) ? U16(data, offset) : data[offset];
        }

        private static bool ValidStep(int value, int min, int max, int step) =>
            value >= min && value <= max && (step <= 0 || (value - min) % step == 0);

        private static string CheckPlausibility(Layout layout, byte[] amd, byte[] aod)
        {
            if (amd == null || amd.Length != layout.Fingerprint.AmdSize)
                return "AmdSetupRPL size is not 0x6D5";
            if (aod == null || aod.Length != layout.Fingerprint.AodSize)
                return "AodSetupRpl size is not 0x1AC";
            if (U32(amd, 0) != layout.Fingerprint.AmdSignature
                || U32(amd, 4) != (uint)layout.Fingerprint.AmdLength
                || U32(amd, 8) != layout.Fingerprint.AmdGeneration)
                return "AmdSetupRPL header signature / length / generation differs from the DRFXI layout";
            if (aod[0x3C] != 1 && aod[0x3C] != 255)
                return "AOD Active Memory Timing Settings gate invalid";
            if (amd[0x116] != 1 && amd[0x116] != 255)
                return "CBS Active Memory Timing Settings gate invalid";

            foreach (TimingField field in layout.Timings)
            {
                int ac = aod[field.AodCtrl], cc = amd[field.CbsCtrl];
                if ((ac != 0 && ac != 1) || (cc != 0 && cc != 1))
                    return field.Row + " control outside {0,1}";
                if (ac == 1 && !ValidStep(U16(aod, field.AodValue), field.AodMin, field.AodMax, field.AodStep))
                    return field.Row + " AOD value outside IFR range/step";
                if (cc == 1 && !ValidStep(U16(amd, field.CbsValue), field.CbsMin, field.CbsMax, field.CbsStep))
                    return field.Row + " CBS value outside IFR range/step";
            }

            foreach (EnumField field in layout.Enums)
            {
                int av = GetNumber(aod, field.AodOffset, field.AodOptions);
                if (!field.AodOptions.Any(o => o.Value == av))
                    return field.Row + " AOD enum value is not in IFR options";
                if (field.CbsOffset.HasValue)
                {
                    int cv = GetNumber(amd, field.CbsOffset.Value, field.CbsOptions);
                    if (!field.CbsOptions.Any(o => o.Value == cv))
                        return field.Row + " CBS enum value is not in IFR options";
                }
            }
            if (aod[0x192] > 2 || aod[0x19B] > 1 || aod[0x97] > 1)
                return "AOD voltage control enum invalid";
            foreach (VoltageField field in layout.Voltages)
                if (field.GateOptions.Contains((int)aod[field.Gate])
                    && !ValidStep(U16(aod, field.Value), field.Min, field.Max, 0))
                    return field.Row + " AOD voltage outside IFR range";

            if (aod[0x3C] == 1 && aod[0x3A] != 1
                && !ValidStep(U16(aod, 0x3D), 2000, 12000, 200))
                return "AOD speed outside IFR range";
            if (amd[0x116] == 1 && !ValidStep(U16(amd, 0x117), 2000, 11200, 0))
                return "CBS speed outside IFR range";
            return null;
        }

        private static Layout LoadLayout()
        {
            using (Stream stream = typeof(DrfxiSettingOrigins).Assembly.GetManifestResourceStream(LayoutResource))
            {
                if (stream == null) throw new InvalidOperationException("Embedded DRFXI layout resource missing.");
                var serializer = new DataContractJsonSerializer(typeof(Layout));
                return (Layout)serializer.ReadObject(stream);
            }
        }

        [DataContract]
        private sealed class Layout
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "fingerprint")] public Fingerprint Fingerprint;
            [DataMember(Name = "timings")] public List<TimingField> Timings;
            [DataMember(Name = "enums")] public List<EnumField> Enums;
            [DataMember(Name = "voltages")] public List<VoltageField> Voltages;
            [DataMember(Name = "agesaRows")] public List<string> AgesaRows;
            [DataMember(Name = "unknownRows")] public List<string> UnknownRows;
        }

        [DataContract]
        private sealed class Fingerprint
        {
            [DataMember(Name = "amdSize")] public int AmdSize;
            [DataMember(Name = "amdSignature")] public uint AmdSignature;
            [DataMember(Name = "amdLength")] public int AmdLength;
            [DataMember(Name = "amdGeneration")] public uint AmdGeneration;
            [DataMember(Name = "aodSize")] public int AodSize;
        }

        [DataContract]
        private sealed class TimingField
        {
            [DataMember(Name = "row")] public string Row;
            [DataMember(Name = "aodCtrl")] public int AodCtrl;
            [DataMember(Name = "aodValue")] public int AodValue;
            [DataMember(Name = "cbsCtrl")] public int CbsCtrl;
            [DataMember(Name = "cbsValue")] public int CbsValue;
            [DataMember(Name = "aodMin")] public int AodMin;
            [DataMember(Name = "aodMax")] public int AodMax;
            [DataMember(Name = "cbsMin")] public int CbsMin;
            [DataMember(Name = "cbsMax")] public int CbsMax;
            [DataMember(Name = "aodStep")] public int AodStep;
            [DataMember(Name = "cbsStep")] public int CbsStep;
        }

        [DataContract]
        private sealed class EnumField
        {
            [DataMember(Name = "row")] public string Row;
            [DataMember(Name = "aodOffset")] public int AodOffset;
            [DataMember(Name = "cbsOffset")] public int? CbsOffset;
            [DataMember(Name = "aodOptions")] public List<Option> AodOptions;
            [DataMember(Name = "cbsOptions")] public List<Option> CbsOptions;
        }

        [DataContract]
        private sealed class Option
        {
            [DataMember(Name = "value")] public int Value;
            [DataMember(Name = "label")] public string Label;
        }

        [DataContract]
        private sealed class VoltageField
        {
            [DataMember(Name = "row")] public string Row;
            [DataMember(Name = "value")] public int Value;
            [DataMember(Name = "gate")] public int Gate;
            [DataMember(Name = "gateOptions")] public List<int> GateOptions;
            [DataMember(Name = "min")] public int Min;
            [DataMember(Name = "max")] public int Max;
        }
    }
}
