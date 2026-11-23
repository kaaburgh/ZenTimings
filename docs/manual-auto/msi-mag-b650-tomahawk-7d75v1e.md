# MSI MAG B650 TOMAHAWK WIFI (MS-7D75) — BIOS 7D75v1E

## Evidence and firmware identity

Source supplied for this analysis: MSI BIOS archive `7D75v1E.zip`.

- Archive SHA-256: `aa922ad65d6fc3b60e1007e34e6c058d1cda884301583d53a80a1ecad462091b`
- BIOS image: `E7D75AMS.1E0`, 33,554,432 bytes (32 MiB)
- BIOS image SHA-256: `cc84d397d04617d86f2980dd79e04ef90fb3302b72e47adf43f54c52eff4f368`
- MSI release notes identify this as MAG B650 TOMAHAWK WIFI (MS-7D75), V1.E, dated 2024-02-21.
- Release notes: AMI BIOS; AGESA ComboPI 1.1.0.2b update; Ryzen 8000 STAPM fix.
- The image contains the build string `E7D75AMS.1E0 02/20/2024`.

The catalog registers the running BIOS identifiers `1.E0`, `E7D75AMS.1E0`, and the marketing alias `7D75v1E`. Hardware SMBIOS output is still needed to confirm which identifier Windows exposes on this board.

## UEFI / IFR extraction

The image was parsed offline with the project UEFI toolchain. The following IFR declarations were found:

| Varstore | GUID | IFR size | Role |
|---|---|---:|---|
| `AmdSetupRPL` | `3A997502-647A-4C82-998E-52EF9486A247` | `0x779` (1913) | AMD CBS Raphael/RPL settings |
| `AodSetupRpl` | `5ED15DC0-EDEF-4161-9151-6014C4CC630C` | `0x320` (800) | AMD Overclocking / AOD settings |
| `Setup` | `EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9` | `0x7C4` (1988) | MSI setup, OC menu and profile selectors |

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

The MSI `Setup` IFR exposes two conditional groups:

- A-XMP selectors: `Setup[0x595]` and `Setup[0x6F9]`
- EXPO selectors: `Setup[0x596]` and `Setup[0x6FA]`

The IFR options are Disabled/Profile 1/Profile 2 (and User Profile 1 for EXPO). The two groups are shown under different conditional branches in the same Overclocking form. Their active-branch relationship has not yet been resolved. The implementation therefore captures and displays these raw values as diagnostics only; it does not claim that a non-zero selector proves the profile was applied or assign the EXPO profile as the source of each timing.

## Resolver policy and evidence status

Catalog resolver: `msi-mag-b650-raphael-v1e`.

Evidence status: `derived` — extracted deterministically from the supplied BIOS image and IFR, but not yet checked against a real board readout.

Fail-closed rules:

- `AmdSetupRPL` must be readable and have exactly `0x779` bytes.
- `AodSetupRpl`, if readable, must have exactly `0x320` bytes; `Setup`, if readable, must have exactly `0x7C4` bytes.
- Missing AOD or Setup data is not treated as Auto.
- If both AMD CBS and AOD expose a Manual timing, the resolver reports unknown. AOD-over-CBS precedence is not assumed for this MSI BIOS.
- If MSI A-XMP/EXPO selectors are non-zero and no explicit AMD CBS/AOD Manual control is found, the timing source is reported as unknown. Raw selector values are included in the tooltip for hardware validation.
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
