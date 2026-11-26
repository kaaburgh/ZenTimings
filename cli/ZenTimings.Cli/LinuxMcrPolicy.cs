using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using ZenTimings.BiosOrigin;

namespace ZenTimings.Cli
{
    // Saved CBS policy only. No AOD, physical memory, CMOS, APCB or other
    // variable reads. A saved Auto is never promoted to Enabled or Disabled.
    internal static class LinuxMcrPolicy
    {
        internal sealed record Result(
            bool available, string status, string bios_release, string layout_evidence,
            string saved_cbs_mcr, bool? stored_combo_cbs_suppresses_memory_features)
        {
            public string note => "Saved CBS setting and one IFR predicate only. " +
                "The platform selector may be recomputed at runtime. APCB policy, actual menu visibility " +
                "and whether this boot restored memory context are not measured.";
        }

        internal static Result Capture(LinuxFirmware firmware)
        {
            var match = BiosOriginLayoutCatalog.Select(firmware.Board, firmware.Bios);
            if (match == null || match.Resolver != "drfxi-dragonrange-v1" ||
                (match.ReleaseVersion != "1.15" && match.ReleaseVersion != "1.17") ||
                !BiosOriginLayoutCatalog.MayDisplayOrigins(match.Status))
                return new Result(false, "Unsupported DRFXI board or BIOS release.", null, null, null, null);

            Result Unavailable(string status) =>
                new Result(false, status, match.ReleaseVersion, "IFR-derived", null, null);
            if (!firmware.IsAvailable) return Unavailable("efivarfs unavailable.");

            var variable = match.Variables.Single(v => v.Name == "AmdSetupRPL");
            byte[] b;
            try
            {
                b = firmware.Read(variable.Name, variable.Guid)?.Payload;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Unavailable("AmdSetupRPL read failed: " + ex.GetType().Name + ".");
            }
            if (b == null) return Unavailable("AmdSetupRPL not visible to EFI runtime services.");
            if (b.Length != variable.Size || b.Length != 0x6D5 ||
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0, 4)) != 0xFC4053AAu ||
                BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4, 4)) != 0x2D8u)
                return Unavailable("AmdSetupRPL size or stable header mismatch.");

            // +8 is the stored CBS settings fingerprint according to the
            // CbsSetupDxeRPL -> APCB sync investigation, not an invariant
            // schema field. Do not compare or export it. This intentionally
            // does not relax the existing full setting-origin resolver.
            string mcr = b[0x263] switch
            {
                0 => "Disabled",
                1 => "Enabled",
                0xFF => "Auto",
                _ => null
            };
            if (mcr == null) return Unavailable("Unrecognized MCR encoding.");
            // IFR SuppressIf Combo CBS != FF. This is a predicate on saved
            // bytes, NOT an evaluation of the running HII form browser.
            return new Result(true, "Saved CBS policy decoded.", match.ReleaseVersion,
                "IFR-derived", mcr, b[0x20] != 0xFF);
        }
    }
}
