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
                    case "Setup": return "M·UEFI";
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
            if ((Source != "AOD" && Source != "CBS" && Source != "Setup") || !StoredNumber.HasValue)
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
            if (Source == "AOD" || Source == "CBS" || Source == "Setup")
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
        internal byte[] Setup { get; set; }
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

    // Diagnostic-only result: never passed to an origin resolver without its
    // separate catalog and IFR checks. Raw bytes require explicit user opt-in.
    public sealed class FirmwareVariableProbeResult
    {
        public byte[] Bytes { get; set; }
        public uint Attributes { get; set; }
        public int Win32Error { get; set; }
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

        // Always independent of the layout catalog. ONLY call this from the
        // explicit Debug Report "Include BIOS setup variables" opt-in path.
        // Read-only GetFirmwareEnvironmentVariableExW, no writes or uploads.
        public static IReadOnlyList<string> CaptureFirmwareDiagnostics(
            string board, string biosVersion, string boardVendor, string biosVendor,
            string smbiosBoard, string smbiosBiosVersion, string agesa)
        {
            int privilege = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? EnablePrivilege() : 1;
            return FormatFirmwareDiagnostics(ResolveLiveBoardIdentity(board, biosVersion), biosVersion, boardVendor, biosVendor,
                smbiosBoard, smbiosBiosVersion, agesa,
                (name, guid) =>
                {
                    if (privilege != 0)
                        return new FirmwareVariableProbeResult { Win32Error = privilege };
                    uint attributes;
                    int error;
                    byte[] bytes = ReadVariable(name, guid, out attributes, out error);
                    return new FirmwareVariableProbeResult
                    {
                        Bytes = bytes, Attributes = attributes, Win32Error = error
                    };
                });
        }

        // Pure formatter with an injectable read function for tests. A failure
        // reading one varstore never suppresses attempts to read the others.
        public static IReadOnlyList<string> FormatFirmwareDiagnostics(
            string board, string biosVersion, string boardVendor, string biosVendor,
            string smbiosBoard, string smbiosBiosVersion, string agesa,
            Func<string, string, FirmwareVariableProbeResult> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            var lines = new List<string>();
            lines.Add("Capture: explicit user opt-in; read-only UEFI variables.");
            lines.Add("This section is RAW EVIDENCE ONLY: no Manual/Auto badges are derived from it.");
            lines.Add("Resolver board (systemInfo.MbName): " + (board ?? "<unavailable>")); // May use Lenovo system identity fallback.
            lines.Add("Resolver BIOS (systemInfo.BiosVersion): " + (biosVersion ?? "<unavailable>"));
            lines.Add("Win32_BaseBoard.Product: " + (smbiosBoard ?? "<unavailable>"));
            lines.Add("Win32_BaseBoard.Manufacturer: " + (boardVendor ?? "<unavailable>"));
            lines.Add("Win32_BIOS.SMBIOSBIOSVersion: " + (smbiosBiosVersion ?? "<unavailable>"));
            lines.Add("Win32_BIOS.Manufacturer: " + (biosVendor ?? "<unavailable>"));
            lines.Add("AGESA: " + (agesa ?? "<unavailable>"));
            try
            {
                lines.Add("Catalog decision: " + BiosOriginLayoutCatalog.DescribeSelection(board, biosVersion));
            }
            catch (Exception ex)
            {
                lines.Add("Catalog decision failed: " + ex.Message + "; no badges may be inferred.");
            }
            BiosOriginLayoutCatalog.BiosOriginCatalogMatch rawAsrockCandidate = null;
            try
            {
                var candidate = BiosOriginLayoutCatalog.Select(board, biosVersion);
                if (candidate != null && candidate.Resolver == AsrockSteelLegendSettingOrigins.Resolver)
                    rawAsrockCandidate = candidate;
            }
            catch { /* Raw capture remains independent of catalog selection. */ }
            lines.Add(rawAsrockCandidate == null
                ? "Reference offsets below are from MSI 7D75v1E5/1E0 IFR."
                : "Reference offsets: ASRock " + rawAsrockCandidate.ReleaseVersion +
                    " Setup IFR; AMD reference probes below remain MSI 7D75v1E5/1E0 IFR.");
            lines.Add("UNVERIFIED — these offsets MUST NOT be interpreted as matching an unknown BIOS.");

            var varstores = new[]
            {
                new { Name = "AmdSetupRPL", Guid = "{3a997502-647a-4c82-998e-52ef9486a247}" },
                new { Name = "AmdSetup", Guid = "{3a997502-647a-4c82-998e-52ef9486a247}" },
                new { Name = "AodSetupRpl", Guid = "{5ed15dc0-edef-4161-9151-6014c4cc630c}" },
                new { Name = "Setup", Guid = "{ec87d643-eba4-4bb5-a1e5-3f3e36b20da9}" },
                new { Name = "AMD_PBS_SETUP", Guid = "{a339d746-f678-49b3-9fc7-54ce0f9df226}" }
            };
            foreach (var variable in varstores)
            {
                lines.Add("Variable: " + variable.Name + " GUID " + variable.Guid);
                FirmwareVariableProbeResult probe;
                try
                {
                    probe = read(variable.Name, variable.Guid);
                }
                catch (Exception ex)
                {
                    lines.Add("  Read error: " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }
                if (probe == null || probe.Bytes == null)
                {
                    int error = probe == null ? -1 : probe.Win32Error;
                    lines.Add("  Read failed: " + ErrorMeaning(error) + " (" + error + ")");
                    continue;
                }
                lines.Add("  Win32 error: " + probe.Win32Error +
                    "; attributes: 0x" + probe.Attributes.ToString("X8") +
                    "; length: " + probe.Bytes.Length);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    lines.Add("  SHA-256: " + BitConverter.ToString(sha.ComputeHash(probe.Bytes))
                        .Replace("-", "").ToLowerInvariant());
                try
                {
                    if (variable.Name == "AmdSetup")
                    {
                        foreach (string line in LenovoM75nSettingOrigins.DescribeRawOffsets(probe.Bytes))
                            lines.Add(line);
                    }
                    else if (rawAsrockCandidate != null && variable.Name == "Setup")
                    {
                        foreach (string line in AsrockSteelLegendSettingOrigins.DescribeRawSetupOffsets(
                            probe.Bytes, rawAsrockCandidate))
                            lines.Add(line);
                    }
                    else
                    {
                        foreach (string line in MsiMagB650SettingOrigins.DescribeRawReferenceOffsets(
                            variable.Name, probe.Bytes))
                            lines.Add(line);
                    }
                }
                catch (Exception ex)
                {
                    lines.Add("  Raw offset reference unavailable: " + ex.Message);
                }
                // Reuse the existing offline replay format. No blob is emitted
                // in any other code path or when the checkbox is unchecked.
                lines.Add(variable.Name + ".base64: " + Convert.ToBase64String(probe.Bytes));
            }
            return lines;
        }

        // Lenovo SMBIOS BaseBoard.Product can be a numeric OEM ID, not M75n.
        // Use system family/model only for the supported exact M33 releases; no fallback
        // to firmware version alone. Registry is read-only and never used by offline replay.
        private static string ResolveLiveBoardIdentity(string board, string biosVersion)
        {
            if (!LenovoM75nSettingOrigins.IsRelease(biosVersion) ||
                Environment.OSVersion.Platform != PlatformID.Win32NT) return board;
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (key == null) return board;
                    string vendor = key.GetValue("SystemManufacturer") as string;
                    string family = key.GetValue("SystemFamily") as string ?? "";
                    string model = key.GetValue("SystemProductName") as string ?? "";
                    return ResolveBoardIdentity(board, biosVersion, vendor, family, model);
                }
            }
            catch { /* No identity inference if the registry cannot be read. */ }
            return board;
        }

        // Shared SMBIOS identity policy. The caller supplies OS-specific DMI data.
        public static string ResolveBoardIdentity(string board, string biosVersion,
            string vendor, string family, string model)
        {
            if (!LenovoM75nSettingOrigins.IsRelease(biosVersion)) return board;
            if (string.Equals(vendor, "LENOVO", StringComparison.OrdinalIgnoreCase) &&
                (Regex.IsMatch(family ?? "", @"(?:^|[^a-zA-Z0-9])M75n(?:$|[^a-zA-Z0-9])", RegexOptions.IgnoreCase) ||
                 Regex.IsMatch(model ?? "", @"^(11BS|11BT|11BU|11BV|11G4|11G5|11G6|11G7|11BW|11BX|11GW|11GX)[A-Z0-9]*$", RegexOptions.IgnoreCase)))
                return "ThinkCentre M75n";
            return board;
        }

        // Read-only, platform-neutral entry point. The provider returns payload bytes
        // without OS-specific attributes, null for absent variables, or throws on I/O error.
        public static SettingOriginReport ReadFromProvider(string board, string biosVersion,
            Func<string, string, byte[]> read)
        {
            try
            {
                if (read == null) throw new ArgumentNullException(nameof(read));
                var candidate = BiosOriginLayoutCatalog.Select(board, biosVersion);
                if (candidate == null)
                    return Unavailable("No supported origin layout catalog entry for this board / BIOS.");
                if (!BiosOriginLayoutCatalog.MayDisplayOrigins(candidate.Status))
                    return Unavailable("AI-proposed origin layouts require maintainer verification before display.");
                if (candidate.Resolver != "drfxi-dragonrange-v1" && candidate.Resolver != "msi-mag-b650-raphael-v1e"
                    && candidate.Resolver != AsrockSteelLegendSettingOrigins.Resolver
                    && candidate.Resolver != LenovoM75nSettingOrigins.Resolver)
                    return Unavailable("Origin layout has no trusted resolver: " + candidate.LayoutId);
                var blobs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var variable in candidate.Variables)
                {
                    byte[] bytes = read(variable.Name, variable.Guid);
                    if (bytes == null)
                    {
                        if (variable.Optional) continue;
                        return Unavailable("UEFI read failed: " + variable.Name + ": unavailable.");
                    }
                    blobs.Add(variable.Name, bytes);
                }
                return ResolveCatalogCandidate(candidate, blobs);
            }
            catch (Exception ex)
            {
                return Unavailable("UEFI origin catalog/read failed: " + ex.Message);
            }
        }

        public static SettingOriginReport ReadFromFirmware(string board, string biosVersion)
        {
            try
            {
                var candidate = BiosOriginLayoutCatalog.Select(ResolveLiveBoardIdentity(board, biosVersion), biosVersion);
                if (candidate == null)
                    return Unavailable("No supported origin layout catalog entry for this board / BIOS.");
                if (!BiosOriginLayoutCatalog.MayDisplayOrigins(candidate.Status))
                    return Unavailable("AI-proposed origin layouts require maintainer verification before display.");
                if (candidate.Resolver != "drfxi-dragonrange-v1" && candidate.Resolver != "msi-mag-b650-raphael-v1e"
                    && candidate.Resolver != AsrockSteelLegendSettingOrigins.Resolver
                    && candidate.Resolver != LenovoM75nSettingOrigins.Resolver)
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
                    {
                        if (variable.Optional) continue;
                        return Unavailable("UEFI read failed: " + variable.Name + ": " + ErrorMeaning(error));
                    }
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
                if (candidate == null && LenovoM75nSettingOrigins.IsRelease(biosVersion) && reportText != null)
                {
                    Match identity = Regex.Match(reportText,
                        @"^Resolver board \(systemInfo\.MbName\):[ \t]*(ThinkCentre M75n)[ \t]*\r?$",
                        RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    if (identity.Success)
                        candidate = BiosOriginLayoutCatalog.Select(identity.Groups[1].Value, biosVersion);
                }
                if (candidate == null)
                    return Unavailable("No supported origin layout for board / BIOS in debug report.");
                if (!BiosOriginLayoutCatalog.MayDisplayOrigins(candidate.Status))
                    return Unavailable("AI-proposed origin layouts are not trusted for offline display.");
                if (candidate.Resolver != "drfxi-dragonrange-v1" && candidate.Resolver != "msi-mag-b650-raphael-v1e"
                    && candidate.Resolver != AsrockSteelLegendSettingOrigins.Resolver
                    && candidate.Resolver != LenovoM75nSettingOrigins.Resolver)
                    return Unavailable("Origin layout has no trusted offline resolver: " + candidate.LayoutId);
                var blobs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var variable in candidate.Variables)
                {
                    byte[] bytes = DecodeReportBlob(reportText, variable.Name);
                    if (bytes == null)
                    {
                        if (variable.Optional) continue;
                        return Unavailable("This debug report lacks BIOS setup variable " + variable.Name + ".");
                    }
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
                byte[] diagnosticBlob;
                if (blobs.TryGetValue("AmdSetupRPL", out diagnosticBlob)) rejected.AmdSetup = diagnosticBlob;
                if (blobs.TryGetValue("AodSetupRpl", out diagnosticBlob)) rejected.AodSetup = diagnosticBlob;
                if (blobs.TryGetValue("Setup", out diagnosticBlob)) rejected.Setup = diagnosticBlob;
                return rejected;
            }

            // The catalog selects a family-specific IFR-derived resolver. Each resolver
            // validates its own fields against the exact BIOS layout before showing badges.
            SettingOriginReport resolved;
            if (candidate.Resolver == "drfxi-dragonrange-v1")
            {
                resolved = FromBlobs(blobs["AmdSetupRPL"], blobs["AodSetupRpl"]);
            }
            else if (candidate.Resolver == "msi-mag-b650-raphael-v1e")
            {
                byte[] aod, setup;
                blobs.TryGetValue("AodSetupRpl", out aod);
                blobs.TryGetValue("Setup", out setup);
                resolved = MsiMagB650SettingOrigins.FromBlobs(blobs["AmdSetupRPL"], aod, setup, candidate);
            }
            else if (candidate.Resolver == AsrockSteelLegendSettingOrigins.Resolver)
            {
                byte[] aod, setup;
                blobs.TryGetValue("AodSetupRpl", out aod);
                blobs.TryGetValue("Setup", out setup);
                resolved = AsrockSteelLegendSettingOrigins.FromBlobs(blobs["AmdSetupRPL"], aod, setup, candidate);
            }
            else if (candidate.Resolver == LenovoM75nSettingOrigins.Resolver)
            {
                byte[] setup;
                blobs.TryGetValue("Setup", out setup);
                resolved = LenovoM75nSettingOrigins.FromBlobs(blobs["AmdSetup"], setup, candidate);
            }
            else
            {
                return Unavailable("Origin layout has no trusted resolver: " + candidate.Resolver);
            }
            if (resolved.IsMatched)
            {
                resolved.Status += "; catalog status=" + candidate.Status
                    + ", layout=" + candidate.LayoutId;
                string evidence = candidate.Status == "verified"
                    ? "Verified against a real firmware-variable readout."
                    : candidate.Status == "derived"
                        ? "Derived from firmware metadata; no hardware readout verified this BIOS release."
                        : "Proposed mapping, not yet verified on hardware.";
                foreach (SettingOriginRow row in resolved.Rows.Values)
                    row.Details = (row.Details ?? "") + "\nLayout catalog: " +
                        candidate.LayoutId + " (" + candidate.Status + "). " + evidence;
            }
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
