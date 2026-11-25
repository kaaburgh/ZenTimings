# ASRock X670E Steel Legend — IFR-derived origin mapping

Stacked on MSI PR #5, not master. Evidence tier **derived**; no physical readout.

## Inputs

All four supplied ZIPs contained one 32 MiB ROM. Extracted independently with UEFIExtract NE A75 and IFRExtractor-RS 1.6.1 from the project toolchain.

| ROM | SHA-256 | CBS size | AOD size | Setup size |
|---|---|---:|---:|---:|
| X670ESL1.11.ROM | `cd6c79c456eb3aceccbbbda058fa237483c02d73228a13a555aacce547beec7b` | 0x6D6 | 0x1A8 | 0x1CE |
| X670ESL1.11.AS01.ROM | `ee772d7a70ded175e5d382c3b094ccca0db17d7e6b2b39d68513158f039aad07` | 0x6D6 | 0x1A8 | 0x1CE |
| X670ESL1.11.AS03.ROM | `7ed13ed948e0fa26174b8aea641c8a73862f1ebd0c3a474efea6415c62f41335` | 0x6D6 | 0x1A8 | 0x1CE |
| X670ESL1.21.ROM | `dcfd54e12b345e4ccde214ee561865ddfe30aee3b368a43f5f28203681a377fe` | 0x6D6 | 0x1AB | 0x1EE |

The IFR VarStore GUIDs are the same as MSI for AmdSetupRPL, AodSetupRpl and Setup. The first three CBS u32 words are zero in each image's external StdDefaults; weak defaults only, not unique identification. Both Setup and CBS external default bodies match IFR sizes. AOD is declared in IFR but absent from external StdDefaults; runtime visibility unknown. Setup runtime readability is also unverified. 1.21 additionally declares ASRockEasySetup (one byte), not mapped here.

## Mapped fields

The 25 native timing controls have identical offsets/ranges across the three 1.11 images. The CBS and AOD timing control/value offsets and ranges remain stable across all four images. The native Setup map changes in 1.21: +9 bytes for frequency and first six timings, +11 for the remaining timings due to an inserted tREFI.

| Field | 1.11 family Setup | 1.21 Setup |
|---|---:|---:|
| DRAM Frequency (u16) | 0x161 | 0x16A |
| tCL | 0x163 | 0x16C |
| tRCDRD | 0x165 | 0x16E |
| tRP | 0x167 | 0x170 |
| tRAS | 0x169 | 0x172 |
| tRC | 0x16B | 0x174 |
| tWR | 0x16D | 0x176 |
| tREFI | absent | 0x178 |
| tRFC1 | 0x16F | 0x17A |
| tRFC2 | 0x171 | 0x17C |
| tRFCsb | 0x173 | 0x17E |
| tRTP | 0x175 | 0x180 |
| tRRDL | 0x177 | 0x182 |
| tRRDS | 0x179 | 0x184 |
| tFAW | 0x17B | 0x186 |
| tWTRL | 0x17D | 0x188 |
| tWTRS | 0x17F | 0x18A |
| tRDRDSCL | 0x181 | 0x18C |
| tRDRDSC | 0x183 | 0x18E |
| tRDRDSD | 0x185 | 0x190 |
| tRDRDDD | 0x187 | 0x192 |
| tWRWRSCL | 0x189 | 0x194 |
| tWRWRSC | 0x18B | 0x196 |
| tWRWRSD | 0x18D | 0x198 |
| tWRWRDD | 0x18F | 0x19A |
| tWRRD | 0x191 | 0x19C |
| tRDWR | 0x193 | 0x19E |
| DRAM Profile Setting (u8) | 0x1B6 | 0x1CE |
| DRAM Performance Mode (u8) | 0x1B7 | 0x1CF |

CBS gate 0x116 (255 Auto, 1 Enabled); target speed 0x117; timing pairs start at 0x119/0x11A with stride 3. AOD gate 0x3C; timing pairs 0x3F/0x40 with stride 3. AOD speed 0x3D is gray-out controlled by hidden byte 0x3A, so its interpretation is deferred. 1.21 introduces native tREFI and CBS tREFI at 0x2E1 (default 65535, under CBS gate); no CBS sentinel interpretation is assumed. 1.21 also exposes native Gear Down Mode at 0x1A0, not mapped in this slice.

**Profile encoding reverses:** in all three 1.11 images Auto=255 and generic DDR5 profile=0; in 1.21 Auto=0 and generic DDR5 profile=255. XMP 1–5, EXPO 6–7; performance mode 255=AGESA default, 1=Competitive, 2=Aggressive in all four. Defaults agree with these encodings. Native timing zero is the saved default, not evidence that AGESA selected Auto. Native nonzero timing range validation uses menu help bounds where IFR permits a wider storage range (e.g. tWTRS IFR max 64 vs help max 16). Native tRAS/tWR/tFAW minimums change from 30/48/20 to 24/42/12 in 1.21.

## Resolver behavior and acceptance

- One board family with four canonical releases. Three 1.11 releases share the mapping; 1.21 has size overrides and explicit native offset/selector changes. P/L version prefixes are candidate aliases (exact match only, actual SMBIOS strings pending).
- Decode only saved manual settings with a unique mapped source among native Setup, CBS and AOD, with all three variables readable and profile Auto/performance default. Otherwise report unknown with raw values; never assume precedence, profile application or Auto from a zero default. M·UEFI is the native Setup badge.
- tRCDWR remains unknown (native menu explicitly names tRCDRD); FCLK, UCLK, GDM, voltages, impedance, other vendor OC paths and effective precedence are not mapped. Native tREFI is decoded in 1.21 only when neither AMD gate is active.
- No firmware writes. Offline replay uses the same catalog and resolver; opt-in diagnostics remain independent of selection. Existing reference probes are MSI-specific and must not be interpreted as ASRock offsets.
- Run ZenTimings as Administrator, opt in to Include BIOS setup variables, save debug report plus screenshot and exact board/BIOS strings. Share raw report privately. Verify readable sizes/headers and controlled saved-value changes before promoting to verified. Do not flash or change firmware solely for this test.

Static extraction completed; Windows build and hardware validation have not been executed in this environment.
