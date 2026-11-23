using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace ZenTimings.BiosOrigin
{
    // MSI's own Setup varstore and AMD's CBS/AOD varstores coexist on this board.
    // This resolver reports saved menu state only; it does not claim that a profile
    // or override was applied by firmware.
    public static class MsiMagB650SettingOrigins
    {
        public const string LayoutResource =
            "ZenTimings.Resources.BiosLayouts.msi-mag-b650-tomahawk-7d75v1e.json";

        public static SettingOriginReport FromBlobs(byte[] amd, byte[] aod, byte[] setup)
        {
            try
            {
                Layout layout = LoadLayout();
                string reason = CheckPlausibility(layout, amd, aod, setup);
                if (reason != null)
                    return new SettingOriginReport
                    {
                        LayoutId = layout.Id,
                        IsMatched = false,
                        Plausibility = "failed: " + reason,
                        Status = "MSI 7D75v1E layout rejected: " + reason
                    };

                bool aodAvailable = aod != null;
                bool setupAvailable = setup != null;
                bool aodGate = aodAvailable && aod[layout.Gates.AodActive] == 1;
                bool cbsGate = amd[layout.Gates.CbsActive] == 1;
                bool axmpSelected = setupAvailable && layout.SetupProfiles.AxmpOffsets.Any(o => setup[o] != 0);
                bool expoSelected = setupAvailable && layout.SetupProfiles.ExpoOffsets.Any(o => setup[o] != 0);
                bool profileSelected = axmpSelected || expoSelected;
                string profileDetails = BuildProfileDetails(layout, setup, axmpSelected, expoSelected);

                var report = new SettingOriginReport
                {
                    LayoutId = layout.Id,
                    IsMatched = true,
                    Plausibility = "passed",
                    Status = "matched (MSI 7D75v1E IFR-derived saved setup; hardware validation pending)",
                    AmdSetup = (byte[])amd.Clone(),
                    AodSetup = aodAvailable ? (byte[])aod.Clone() : null,
                    Setup = setupAvailable ? (byte[])setup.Clone() : null
                };

                foreach (TimingField field in layout.Timings)
                {
                    bool aodManual = aodGate && aod[field.AodCtrl] == 1;
                    bool cbsManual = cbsGate && amd[field.CbsCtrl] == 1;
                    int aodValue = aodAvailable ? U16(aod, field.AodValue) : 0;
                    int cbsValue = U16(amd, field.CbsValue);
                    report.Add(Choose(field.Row, aodManual, cbsManual, aodAvailable,
                        setupAvailable, profileSelected, aodValue, cbsValue, profileDetails));
                }

                // CBS exposes a conventional target-speed field. MSI's AOD 0x3A
                // selector is present in IFR but its write semantics are not known
                // for this BIOS, so an enabled AOD speed path is deliberately unknown.
                SettingOriginRow speed;
                if (cbsGate && aodAvailable && !aodGate)
                {
                    speed = Stored("Speed", "speed", "CBS", U16(amd, layout.Speed.CbsValue),
                        "AMD CBS Memory Target Speed is Manual. " + profileDetails);
                }
                else if (cbsGate || aodGate || !aodAvailable || !setupAvailable || profileSelected)
                {
                    speed = Unknown("Speed",
                        "MSI 7D75v1E Speed source is unresolved. CBS gate=" + (cbsGate ? "enabled" : "Auto")
                        + "; AOD gate=" + (aodAvailable ? (aodGate ? "enabled" : "Auto") : "unavailable")
                        + ". The AOD selector at 0x" + layout.Speed.AodSelector.ToString("X")
                        + " is not interpreted for this BIOS.", profileDetails);
                }
                else
                {
                    speed = Automatic("Speed", "Neither AMD CBS nor the readable MSI/AOD state selects a manual speed.");
                }
                report.Add(speed);
                report.Add(new SettingOriginRow
                {
                    Row = "MCLK",
                    Source = speed.Source,
                    Stored = speed.Stored,
                    StoredNumber = speed.StoredNumber,
                    Kind = "mclk",
                    Details = "MCLK is half the DDR data rate selected in BIOS. " + (speed.Details ?? ""),
                    OtherMenuManual = speed.OtherMenuManual
                });

                SettingOriginRow trfc = report.Find("tRFC");
                if (trfc != null)
                    report.Add(new SettingOriginRow
                    {
                        Row = "tRFC (ns)",
                        Source = trfc.Source,
                        Details = "Derived from the running tRFC and MCLK; source classification follows tRFC. "
                            + (trfc.Details ?? "")
                    });

                return report;
            }
            catch (Exception ex)
            {
                return Unavailable("MSI 7D75v1E layout load / resolve failed: " + ex.Message);
            }
        }

        private static SettingOriginRow Choose(string row, bool aodManual, bool cbsManual,
            bool aodAvailable, bool setupAvailable, bool profileSelected,
            int aodValue, int cbsValue, string profileDetails)
        {
            if (aodManual && cbsManual)
                return Unknown(row, "Both AMD Overclocking and AMD CBS are Manual. "
                    + "Their precedence is not established for MSI BIOS 7D75v1E.", profileDetails);
            if (aodManual)
                return Stored(row, "timing", "AOD", aodValue,
                    "AMD Overclocking (AodSetupRpl) has a Manual control for this timing. "
                    + "The saved setting is not proof of hardware application. " + profileDetails);
            if (cbsManual)
            {
                if (!aodAvailable)
                    return Unknown(row, "AMD CBS has a Manual value, but AodSetupRpl was not runtime-readable; "
                        + "a second-menu override cannot be ruled out.", profileDetails);
                return Stored(row, "timing", "CBS", cbsValue,
                    "AMD CBS (AmdSetupRPL) has a Manual control for this timing. "
                    + "The saved setting is not proof of hardware application. " + profileDetails);
            }
            if (!aodAvailable)
                return Unknown(row, "AodSetupRpl was not runtime-readable; Auto cannot be established.", profileDetails);
            if (!setupAvailable)
                return Unknown(row, "MSI Setup was not runtime-readable; the A-XMP / EXPO profile state is unknown.", profileDetails);
            if (profileSelected)
                return Unknown(row, "No manual AMD CBS/AOD override was found, but an MSI A-XMP / EXPO selector is non-zero. "
                    + "The profile's per-timing application path is not mapped.", profileDetails);
            return Automatic(row, "No Manual control was found in the readable AMD CBS/AOD variables; "
                + "the MSI A-XMP / EXPO selectors are disabled.");
        }

        private static SettingOriginRow Stored(string row, string kind, string source, int value, string details)
        {
            return new SettingOriginRow
            {
                Row = row,
                Kind = kind,
                Source = source,
                Stored = value.ToString(CultureInfo.InvariantCulture),
                StoredNumber = value,
                Details = details
            };
        }

        private static SettingOriginRow Automatic(string row, string details) =>
            new SettingOriginRow { Row = row, Source = "Auto", Details = "Auto: " + details };

        private static SettingOriginRow Unknown(string row, string details, string profileDetails) =>
            new SettingOriginRow { Row = row, Source = "?", Details = details + " " + profileDetails };

        private static string BuildProfileDetails(Layout layout, byte[] setup, bool axmpSelected, bool expoSelected)
        {
            if (setup == null)
                return "MSI Setup variable was not readable; A-XMP/EXPO selection is unknown.";
            string axmp = string.Join(", ", layout.SetupProfiles.AxmpOffsets.Select(o =>
                "0x" + o.ToString("X") + "=" + setup[o].ToString(CultureInfo.InvariantCulture)));
            string expo = string.Join(", ", layout.SetupProfiles.ExpoOffsets.Select(o =>
                "0x" + o.ToString("X") + "=" + setup[o].ToString(CultureInfo.InvariantCulture)));
            return "MSI Setup profile selectors (raw): A-XMP [" + axmp + "]; EXPO [" + expo + "]. "
                + (expoSelected || axmpSelected
                    ? "At least one selector is non-zero; the active conditional group and effective application are not established."
                    : "All exposed A-XMP/EXPO selectors are zero.");
        }

        private static string CheckPlausibility(Layout layout, byte[] amd, byte[] aod, byte[] setup)
        {
            if (amd == null || amd.Length != layout.Fingerprint.AmdSize)
                return "AmdSetupRPL size is not 0x" + layout.Fingerprint.AmdSize.ToString("X");
            if (aod != null && aod.Length != layout.Fingerprint.AodSize)
                return "AodSetupRpl size is not 0x" + layout.Fingerprint.AodSize.ToString("X");
            if (setup != null && setup.Length != layout.Fingerprint.SetupSize)
                return "Setup size is not 0x" + layout.Fingerprint.SetupSize.ToString("X");

            int cbsGate = amd[layout.Gates.CbsActive];
            if (cbsGate != 1 && cbsGate != 255)
                return "CBS Active Memory Timing Settings gate outside IFR options";
            if (aod != null)
            {
                int aodGate = aod[layout.Gates.AodActive];
                if (aodGate != 1 && aodGate != 255)
                    return "AOD Active Memory Timing Settings gate outside IFR options";
            }

            foreach (TimingField field in layout.Timings)
            {
                int cbsControl = amd[field.CbsCtrl];
                if (cbsControl != 0 && cbsControl != 1)
                    return field.Row + " CBS control outside IFR options {0,1}";
                if (cbsControl == 1 && !ValidStep(U16(amd, field.CbsValue),
                    field.CbsMin, field.CbsMax, field.CbsStep))
                    return field.Row + " CBS value outside IFR range/step";

                if (aod != null)
                {
                    int aodControl = aod[field.AodCtrl];
                    if (aodControl != 0 && aodControl != 1)
                        return field.Row + " AOD control outside IFR options {0,1}";
                    if (aodControl == 1 && !ValidStep(U16(aod, field.AodValue),
                        field.AodMin, field.AodMax, field.AodStep))
                        return field.Row + " AOD value outside IFR range/step";
                }
            }

            if (cbsGate == 1 && !ValidSpeed(U16(amd, layout.Speed.CbsValue)))
                return "CBS Memory Target Speed outside MSI IFR options";
            if (setup != null)
            {
                foreach (int offset in layout.SetupProfiles.AxmpOffsets)
                    if (setup[offset] > 5) return "MSI A-XMP selector outside IFR options";
                foreach (int offset in layout.SetupProfiles.ExpoOffsets)
                    if (setup[offset] > 3) return "MSI EXPO selector outside IFR options";
            }
            return null;
        }

        private static bool ValidSpeed(int value) =>
            value == 2000 || value == 2400 ||
            (value >= 3200 && value <= 11200 && value % 200 == 0);

        private static bool ValidStep(int value, int min, int max, int step) =>
            value >= min && value <= max && (step <= 0 || (value - min) % step == 0);

        private static int U16(byte[] data, int offset) =>
            data[offset] | (data[offset + 1] << 8);

        private static SettingOriginReport Unavailable(string status) =>
            new SettingOriginReport
            {
                LayoutId = null,
                IsMatched = false,
                Plausibility = "not checked",
                Status = status
            };

        private static Layout LoadLayout()
        {
            using (Stream stream = typeof(MsiMagB650SettingOrigins).Assembly.GetManifestResourceStream(LayoutResource))
            {
                if (stream == null) throw new InvalidOperationException("Embedded MSI 7D75v1E layout resource missing.");
                var serializer = new DataContractJsonSerializer(typeof(Layout));
                return (Layout)serializer.ReadObject(stream);
            }
        }

        [DataContract]
        private sealed class Layout
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "fingerprint")] public Fingerprint Fingerprint;
            [DataMember(Name = "gates")] public Gates Gates;
            [DataMember(Name = "speed")] public Speed Speed;
            [DataMember(Name = "setupProfiles")] public SetupProfiles SetupProfiles;
            [DataMember(Name = "timings")] public List<TimingField> Timings;
        }

        [DataContract]
        private sealed class Fingerprint
        {
            [DataMember(Name = "amdSize")] public int AmdSize;
            [DataMember(Name = "aodSize")] public int AodSize;
            [DataMember(Name = "setupSize")] public int SetupSize;
        }

        [DataContract]
        private sealed class Gates
        {
            [DataMember(Name = "aodActive")] public int AodActive;
            [DataMember(Name = "cbsActive")] public int CbsActive;
        }

        [DataContract]
        private sealed class Speed
        {
            [DataMember(Name = "aodSelector")] public int AodSelector;
            [DataMember(Name = "aodValue")] public int AodValue;
            [DataMember(Name = "cbsValue")] public int CbsValue;
        }

        [DataContract]
        private sealed class SetupProfiles
        {
            [DataMember(Name = "aXmpOffsets")] public List<int> AxmpOffsets;
            [DataMember(Name = "expoOffsets")] public List<int> ExpoOffsets;
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
            [DataMember(Name = "aodStep")] public int AodStep;
            [DataMember(Name = "cbsMin")] public int CbsMin;
            [DataMember(Name = "cbsMax")] public int CbsMax;
            [DataMember(Name = "cbsStep")] public int CbsStep;
        }
    }
}
