# Lenovo ThinkCentre M75n — M33KT24A / M33KT32A / M33KT33A DDR4 origins

Stacked on ASRock PR #7. Evidence tier derived, not hardware verified. These are Lenovo OEM mobile Raven/Picasso-family firmware, not generic socket AM4 support. Lenovo identifies the M33 firmware family at https://support.lenovo.com/de/de/downloads/ds544268.

## Evidence

| Input | Embedded BIOS ID | SHA-256 |
|---|---|---|
| bios_afu.bin | M33KT24A1.24 | a0720adabb2448910ac0e02a01fc899e97881af3eb5bc6178e6f6d3823980dc4 |
| ImageM33(1).ROM | M33KT32A1.32 | 7d73d99b0b7b7fbe8999d2495b0df7e5adf4936bb3fb003838640fff7e17e145 |
| ImageM33.ROM | M33KT33A1.33 | fe2c8dc35ee74059cddf1fc49fe313a065bc9a1279bb3e090ea28a2d9aa0c498 |

All three 16 MiB images were independently unpacked by UEFIExtract NE A75. IFRExtractor-RS 1.6.1 extracted CbsSetupDxeRV, AmdPbsSetupDxe and both Setup formsets. All four normalized IFR texts (excluding extractor input hash line) are identical between 1.32 and 1.33. The 1.24 CBS and PBS IFR texts also match byte-for-byte; the two 1.24 Setup IFRs differ (6238/6449 lines vs 6309/6520 including extraction header). Normalized CBS IFR SHA-256: b37ed819eb80482c2c43dd3580002c62803f3d7b420c1e4016e6d22c78b0b4a3. PE32 hashes differ for CBS and Setup, so IFR equality is not evidence of identical execution. PBS PE32 is identical.

CBS declares AmdSetup (NOT AmdSetupRPL), GUID 3A997502-647A-4C82-998E-52EF9486A247, size 0x53F. Setup declares EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9 size 0x1C1 for 1.32/1.33 and 0x1BE for 1.24; matching bodies exist in StdDefaults. AmdSetup is absent from extracted StdDefaults; header fingerprint cannot be pinned from these bodies for 1.32/1.33. No AOD setup driver/form was found in the extracted inventory. Runtime visibility and header signature remain unverified. Native Setup Memory Clock at 0x184 is in the Family 15h-only form, not mapped to Raven/Picasso. No native XMP selector found in the extracted Setup forms.

## Mapping

CBS Form 0x703A: Overclock gate 0x9F, Auto=255, Enabled=1. When inactive the saved stale timing bytes are not interpreted as manual. The direct OneOf fields have Auto=255 and distinct permitted enumerations; gated fields use Auto=0, Manual=1. For independent modes outside the OC gate, Auto=255.

| Row | Offset / control | Representation |
|---|---|---|
| Speed | A0 | explicit enum to nominal MHz; doubled for DDR data rate |
| tCL | A1 | u8, 8–33 |
| tRCDRD / WR | A2 / A3 | u8, 8–27 |
| tRP / tRAS | A4 / A5 | u8, 8–27 / 21–58 |
| tRC | A6 / A7 | control / u8, 29–135 |
| tRRDS / L | A8 / A9 | u8, 4–12 |
| tFAW | AA / AB | control / u8, 6–54 |
| tWTRS / L | AC / AD | u8, 2–14 |
| tWR | AE / AF | control / u8, 10–81 |
| tRDRDSCL | B3 / B4 | control / u8, 1–15 |
| tWRWRSCL | B5 / B6 | control / u8, 1–63 |
| tRFC / 2 / 4 | B7/B8, BA/BB, BD/BE | control / u16 LE, 60–990 |
| tCWL | C2 | u8 enum 9,10,11,12,14,16,18,20 |
| tRTP | C3 | u8, 5–14 |
| tCKE | 159 | u8, 1–31 |
| tRDRDSC / SD / DD | 144 / 150 / 152 | u8, 1–11 / 1–15 / 1–15 |
| tWRWRSC / SD / DD | 15A / 151 / 153 | u8, 1–15 |
| tRDWR / tWRRD | 156 / 143 | u8, 1–31 / 1–15 |
| Cmd2T / GDM / PwrDwn | 14E / 155 / 14C | independent enum 0/1/255 |

Clock values are explicit IFR labels (not arithmetic on the selector). The nominal clock/data rate is shown without numeric mismatch comparison due to rounded/inconsistent legacy labels. Manual timing mismatch comparisons retain existing behavior. Auto/disabled remains unknown (not an assertion of effective auto). ProcODT, RTT, BGS and other enum mappings, voltage, FCLK/UCLK, UMA and effective firmware override precedence are deferred. UMA fields are outside this PR's origin scope.

## Identity and validation

Exact M33KT24A, M33KT32A or M33KT33A plus M75n board token. Numeric Lenovo board IDs can be resolved from Windows read-only registry SMBIOS system family/model, restricted to Lenovo and official M75n machine types listed at https://support.lenovo.com/lk/en/solutions/ht518129. Offline replay may use the recorded resolved identity from the explicit debug capture; it never reads the current host. The actual target BaseBoard.Product is not known yet. AmdSetup size and active-field checks are mandatory, Setup size checked per release if readable. For 1.24 only, the resolver additionally checks the first two u32 words observed in AFU NVRAM; no inferred header for 1.32/1.33, no version-only matching, no firmware writes. Explicit debug capture now tries five variables including AmdSetup, independent of recognition. Existing MSI raw probes retain their reference-only semantics.

Synthetic tests cover all three canonical versions, separate RD/WR, manual gates, stale bytes, enums, sizes, independent modes, diagnostics and offline replay. Physical acceptance: exact SMBIOS identity, readable variable lengths, values and screenshot. Current ROMs do not establish compatibility with any other version. Windows CI and actual hardware validation pending at authoring.

## M33KT24A AFU dump comparison (2026-11-25)

The 16 MiB AFU dump embeds BIOS ID M33KT24A1.24 and ThinkCentre M75n. It contains NVAR records, unlike a clean vendor update image. Three AmdSetup bodies (one Link, its DataOnly successor and a mirrored Full record) have size 0x53F; the successor and Full record match byte-for-byte. The first two little-endian u32 values in all three records are 0x420C3F32 and 0x00000164. These are observed header words, not a proof of their invariance across machines. They are checked only for M33KT24A, fail closed on mismatch, and are not extrapolated to 1.32/1.33. No live Windows UEFI API read was performed.

Normalized IFR SHA-256 for all three CBS formsets: b37ed819eb80482c2c43dd3580002c62803f3d7b420c1e4016e6d22c78b0b4a3. PBS IFR: 8520d635cfee756c4be242a23ade91ecd2ccbb6162c1e0abfe98e8c3f6b3a131. PBS PE32 also matches. 1.24 Setup forms differ, but native Memory Clock remains at 0x184 in the Family 15h-only form; no new Raven memory source is inferred. Setup varstore size 0x1BE is registered as a release override (1.32/1.33: 0x1C1).

The extracted saved OC gate at 0x9F is 0xFF; mapped timing controls are Auto, while some value bytes contain e.g. tRC=57, tWR=18, tRFC=312. Those stale values must not produce manual badges. A sanitized synthetic fixture reproduces only mapped setting bytes and the two header words; no raw firmware, full NVRAM blob, machine IDs, serial numbers or other machine-specific data are committed. The raw dump is evidence for static compatibility, not live runtime validation. All three catalog releases remain derived.
