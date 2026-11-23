# MSI MAG B650 TOMAHAWK WIFI (MS-7D75) — BIOS 7D75v1E

## Evidence and firmware identity

For a byte-level and IFR comparison against the preceding `7D75v1E5` build (2024-01-25), see [1E5 → 1E0 comparison](msi-mag-b650-7d75v1e5-comparison.md). AMD CBS/AOD IFR matches exactly, but MSI `Setup` size differs (`0x7C2` vs `0x7C4`). Both are now registered using the same offsets and distinct per-release fingerprints.


Source supplied for this analysis: MSI BIOS archive `7D75v1E.zip`.

- Archive SHA-256: `aa922ad65d6fc3b60e1007e34e6c058d1cda884301583d53a80a1ecad462091b`
- BIOS image: `E7D75AMS.1E0`, 33,554,432 bytes (32 MiB)
- BIOS image SHA-256: `cc84d397d04617d86f2980dd79e04ef90fb3302b72e47adf43f54c52eff4f368`
- MSI release notes identify this as MAG B650 TOMAHAWK WIFI (MS-7D75), V1.E, dated 2024-02-21.
- Release notes: AMI BIOS; AGESA ComboPI 1.1.0.2b update; Ryzen 8000 STAPM fix.
- The image contains the build string `E7D75AMS.1E0 02/20/2024`.

The catalog recognizes both `1.E0` (aliases `E7D75AMS.1E0` and `7D75v1E`) and `1.E5` (aliases `E7D75AMS.1E5` and `7D75v1E5`) as `derived` releases of **one shared MSI layout**. Their AMD memory fields use identical IFR offsets; the selected release supplies its own Setup-size fingerprint. Hardware SMBIOS output is still needed to confirm the exact running version.

## UEFI / IFR extraction

The image was parsed offline with the project UEFI toolchain. The following IFR declarations were found:

| Varstore | GUID | IFR size | Role |
|---|---|---:|---|
| `AmdSetupRPL` | `3A997502-647A-4C82-998E-52EF9486A247` | `0x779` (1913) | AMD CBS Raphael/RPL settings |
| `AodSetupRpl` | `5ED15DC0-EDEF-4161-9151-6014C4CC630C` | `0x320` (800) | AMD Overclocking / AOD settings |
| `Setup` | `EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9` | `0x7C2` (1E5) / `0x7C4` (1E0) | MSI setup, OC menu and profile selectors |

Important distinction: the external `StdDefaults` NVRAM block contains defaults for `AmdSetupRPL` and `Setup`, but no `AodSetupRpl` default entry. The AOD driver nevertheless declares the `AodSetupRpl` varstore in IFR. This does not prove that the runtime variable is absent; its availability must be checked on the physical board. `Setup` is also optional until runtime read access is confirmed. Accordingly, missing AOD or Setup data does not get interpreted as Auto.

## Memory timing fields

The IFR-derived active-menu gates are:

- AOD `AodSetupRpl[0x3C]`: `Auto=255`, `Enabled=1`.
- CBS `AmdSetupRPL[0x116]`: `Auto=255`, `Enabled=1`.

The timing control/value offsets align with the AMD Raphael menu layout used by the existing resolver, but the size and IFR ranges are specific to this MSI release. The layout resource records all 26 UI rows (including the two UI rows backed by the same tRCD field), with per-menu ranges.

Notable range differences from the existing DRFXI profile include:

- AOD tCL: 22–62, step 2; CBS tCL: 22–64.
- AOD tRAS minimum: 20; CBS tRAS minimum: 30.
- AOD tWR: 48–96, step 6; CBS tWR: 48–138.
- AOD tFAW minimum: 8; CBS tFAW minimum: 20.

CBS Memory Target Speed is at `AmdSetupRPL[0x117]`, guarded by the CBS gate. The IFR describes 2000 and 2400 MT/s as special values and 3200–11200 MT/s in 200 MT/s increments.

AOD Memory Target Speed is at `AodSetupRpl[0x3D]`, but the adjacent selector `AodSetupRpl[0x3A]` is an unnamed numeric IFR question. Its MSI-specific write semantics have not been established, so this resolver deliberately does not infer AOD Speed from that path. If the AOD speed gate is enabled, Speed/MCLK is shown as unknown unless a clearly separate CBS-only state can be established.

## MSI EXPO / A-XMP state

The MSI `Setup` IFR exposes a top-level profile selector pair plus two conditional groups:

- Top-level A-XMP and AMD EXPO: `Setup[0x73]` and `Setup[0x74]`
- Conditional A-XMP selectors: `Setup[0x595]` and `Setup[0x6F9]`
- Conditional EXPO selectors: `Setup[0x596]` and `Setup[0x6FA]`
- Memory Try It! profile selectors: `Setup[0x597]` and `Setup[0x6FB]` (16-bit)

The IFR options are Disabled/Profile 1/Profile 2 (and User Profile 1 for EXPO). These controls are found in the MSI Overclocking forms. Their active-branch and firmware-application relationships have not been established. The implementation captures the listed raw values as diagnostics only; a non-zero selector is not proof that the profile was applied. Additional independent MSI controls include DRAM Frequency (`0x51B`/`0x68C`), High-Efficiency Mode (`0x57D`/`0x6F2`) and Memory Timing Preset (`0x57E`/`0x6F3`). The resolver therefore **does not claim Auto** for an MSI timing or Speed solely because AMD CBS and AOD both appear Auto; unmatched rows display `?`.

## Resolver policy and evidence status

Catalog resolver: `msi-mag-b650-raphael-v1e`.

Evidence status: `derived` — extracted deterministically from the supplied BIOS image and IFR, but not yet checked against a real board readout.

Fail-closed rules:

- `AmdSetupRPL` must be readable and have exactly `0x779` bytes.
- `AodSetupRpl`, if readable, must have exactly `0x320` bytes; `Setup`, if readable, must match the **selected BIOS version**: `0x7C2` for 1E5 or `0x7C4` for 1E0.
- Missing AOD or Setup data is not treated as Auto.
- If both AMD CBS and AOD expose a Manual timing, the resolver reports unknown. AOD-over-CBS precedence is not assumed for this MSI BIOS.
- If MSI A-XMP/EXPO / Memory Try It! selectors are non-zero and no explicit AMD CBS/AOD Manual control is found, the timing source is reported as unknown. All raw selector values are included in the tooltip for hardware validation.
- Even when those selectors are zero, the independent MSI OC/DRAM Timing paths are not yet fully mapped, so we do not infer Auto.
- No firmware writes, BIOS flashing, flash reads, or network access are performed by the application.

This first resolver maps the timing controls and CBS Memory Target Speed only. FCLK/UCLK, voltages, Power Down, drive strengths, and the effective MSI profile-application path remain unmapped; their rows intentionally receive no inferred origin badge.

## Hardware validation still required

On a real MS-7D75 board running this BIOS, run ZenTimings as Administrator so the read-only UEFI API can enable `SeSystemEnvironmentPrivilege`:

1. Confirm the exact `Win32_BaseBoard.Product` and `Win32_BIOS.SMBIOSBIOSVersion` strings used by catalog selection.
2. Confirm whether Windows can read `AmdSetupRPL`, `AodSetupRpl`, and `Setup`, and confirm the three lengths.
3. Compare the MSI EXPO selector raw values with the BIOS UI and a ZenTimings screenshot.
4. Confirm the source classifications against a default/Auto configuration and, if possible, a known EXPO-enabled configuration.
5. Do not upgrade this layout to `verified` until a real hardware variable readout and matching UI state are available.

The PR adds this as a derived first-board implementation on top of Phase 2. It is a diagnostic build for collecting the first MSI hardware readout, not a claim that the MSI profile application path is fully understood.
