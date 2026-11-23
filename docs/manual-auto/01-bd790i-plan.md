# Phase 1 — BD790i (DRFXI 1.15 / 1.17)

Scope: one board family, two BIOS releases with identical setup layouts. No layout derivation in the app yet; the layout is a checked-in table generated from the research repository. Estimated effort: **2–3 days** including UI and tests.

## 1. Detection (when the feature turns on)

All conditions must hold, otherwise the column shows `?` and the feature stays silent:

1. SMBIOS: board/BIOS strings identify DRFXI (ZenTimings already shows `DRFXI | BIOS 1.15`), BIOS version in {1.15, 1.17}.
2. `AmdSetupRPL` (GUID `3a997502-647a-4c82-998e-52ef9486a247`) reads back with **size 0x6D5**, header signature `0xFC4053AA` at +0x00, length field `0x2D8` at +0x04 and CBS generation `0x4B9D5809` at +0x08 (the generation is a fingerprint of the CBS code defaults; identical in 1.15 and 1.17). **CONFIRMED** (image + owner readback).
3. `AodSetupRpl` (GUID `5ed15dc0-edef-4161-9151-6014c4cc630c`) reads back with **size 0x1AC**. **CONFIRMED**.
4. Plausibility check passes (section 4).

Both variables carry the AMI runtime attribute in NVRAM (attr `0x83`), so they should be readable from Windows. **TO VERIFY** on hardware (`drfxi-mem.ps1` performs exactly this read).

## 2. Reading the variables

- `GetFirmwareEnvironmentVariableExW(name, "{GUID}", buf, size, out attr)` after enabling `SeSystemEnvironmentPrivilege` (`OpenProcessToken` + `LookupPrivilegeValue` + `AdjustTokenPrivileges`). ZenTimings already runs elevated; no driver is involved.
- Read-only by construction: the code must not reference `SetFirmwareEnvironmentVariable*`. A unit test greps the assembly for it.
- Win32 errors to surface in the debug report: 1314 (privilege), 203 (variable absent / not runtime-visible), 1 (legacy BIOS).
- The C# reader already exists inside `scripts/win/drfxi-mem.ps1` of the research repository and can be moved over almost verbatim.

## 3. Layout table (generated, checked in)

Generated from `setup-questions.json` + `cbs-apcb-map.json` + `aod-apcb-map.json` into one resource, e.g. `WPF/Resources/BiosLayouts/drfxi-dragonrange-v1.json`. Timing pairs (`Ctrl` byte: 0 Auto / 1 Manual; value: UINT16):

| ZenTimings row | AOD ctrl / value | CBS ctrl / value |
|---|---|---|
| tCL | 0x3F / 0x40 | 0x119 / 0x11A |
| tRCDWR + tRCDRD (one setting) | 0x42 / 0x43 | 0x11C / 0x11D |
| tRP | 0x45 / 0x46 | 0x11F / 0x120 |
| tRAS | 0x48 / 0x49 | 0x122 / 0x123 |
| tRC | 0x4B / 0x4C | 0x125 / 0x126 |
| tWR | 0x4E / 0x4F | 0x128 / 0x129 |
| tRFC | 0x51 / 0x52 | 0x12B / 0x12C |
| tRFC2 | 0x54 / 0x55 | 0x12E / 0x12F |
| tRFCsb | 0x57 / 0x58 | 0x131 / 0x132 |
| tRTP | 0x5A / 0x5B | 0x134 / 0x135 |
| tRRDL | 0x5D / 0x5E | 0x137 / 0x138 |
| tRRDS | 0x60 / 0x61 | 0x13A / 0x13B |
| tFAW | 0x63 / 0x64 | 0x13D / 0x13E |
| tWTRL | 0x66 / 0x67 | 0x140 / 0x141 |
| tWTRS | 0x69 / 0x6A | 0x143 / 0x144 |
| tRDRDSCL / SC / SD / DD | 0x6C/0x6D, 0x6F/0x70, 0x72/0x73, 0x75/0x76 | 0x146/0x147, 0x149/0x14A, 0x14C/0x14D, 0x14F/0x150 |
| tWRWRSCL / SC / SD / DD | 0x78/0x79, 0x7B/0x7C, 0x7E/0x7F, 0x81/0x82 | 0x152/0x153, 0x155/0x156, 0x158/0x159, 0x15B/0x15C |
| tWRRD | 0x84 / 0x85 | 0x15E / 0x15F |
| tRDWR | 0x87 / 0x88 | 0x161 / 0x162 |

Section gates: AOD *Active Memory Timing Settings* `0x3C` (1 = Enabled; the Memory Target Speed token is written only when hidden AOD `0x3A` != 1 (default 0xFF); `0x3A` == 1 selects a separate, not-yet-decoded profile path), CBS *Active Memory Timing Settings* `0x116` (1 = Enabled, 0xFF = Auto). A Manual pair under a disabled gate is **not** applied → origin `Auto`.

Single-byte enums (0xFF = Auto) and other rows:

| ZenTimings row | AOD | CBS |
|---|---|---|
| Speed / MCLK | Memory Target Speed 0x3D (u16, MT/s), written when 0x3C == 1 and 0x3A != 1 | 0x117 |
| FCLK | 0x94 (u16 MHz, 0xFFFF Auto) | 0x260 (option code, 0xFF Auto; 2000 MHz = 48) |
| UCLK | UCLK DIV1 MODE 0x96 (1 = UCLK=MEMCLK) | — |
| ProcCaDs, ProcDqDs, ProcOdt, DramDqDs | 0x8A, 0x8B, 0x8C, 0x8D | 0x165, 0x16D, 0x16C, 0x16E |
| RttNomWr, RttNomRd, RttWr, RttPark, RttParkDqs | 0x8E, 0x8F, 0x90, 0x91, 0x92 | 0x167, 0x168, 0x169, 0x16A, 0x16B |
| PowerDown | 0x93 | 0x16F |
| MEM VDD / VDDQ / CPU VDDIO | DIMM VDD 0x193, DIMM VDDQ 0x195, APU VDDIO 0x197 (u16 mV), active only when VDDIO Ctrl 0x192 ∈ {1 Manual, 2 Separate} | — |
| MEM VPP | 0x199 when VPP Ctrl 0x19B == 1 | — |
| VDD MISC | 0x98 when VDD Misc Control 0x97 == 1 | — |
| (no row) Platform PMIC control | 0x1A9 (0xFF Auto, 0 Disable = processor firmware programs the DIMM PMICs) | — |
| Memory Context Restore (no row today) | — | 0x263 (0xFF Auto = platform default TRUE) |

Rows with **no menu field** on this BIOS (origin `AGESA`): tCWL, tREFI, tCKE, tSTAG, tMOD, tMODPDA, tMRD, tMRDPDA, tPHY*, tRDPRE, tWRPRE, GDM, Cmd2T, Nitro, Refresh Mode. BGS/BGS Alt: CBS has `BankSwapMode` (0x177), mapping to ZenTimings' BGS flags **UNKNOWN** → show `?` for BGS in phase 1.

VSOC: AOD *SoC/Uncore OC Mode* 0x18A and *SoC Voltage* 0x18B exist; their apply guard was not traced → leave VSOC out of phase 1.

## 4. Plausibility check

Before showing anything, validate every field of the table against the IFR description shipped with it: Ctrl bytes ∈ {0, 1}; values within the IFR range (e.g. Tcl 22–64, Trcd 8–62, Trfc1 50–4095); enums ∈ option set; voltages within 800–1430 mV. Any failure → origin `?` for the whole board and a diagnostic entry (phase 2 turns that into a GitHub issue). A wrong layout almost always fails this check because random bytes rarely land inside every range and option set.

## 5. Resolving the origin

```
for each row:
  aodManual = AOD.gate==1 && AOD.ctrl==1          (or enum != 0xFF, or voltage gate active)
  cbsManual = CBS.gate==1 && CBS.ctrl==1          (or enum != 0xFF)
  origin    = aodManual ? AOD : cbsManual ? CBS : (row has a menu ? Auto : AGESA)
  stored    = value of the winning source
  flag ≠    = origin in {AOD, CBS} && stored != running (after unit conversion: MT/s→MCLK, FCLK code→MHz, mV→V)
```

Precedence AOD over CBS: AOD writes APCB tokens in override instance 0 with priority mask 0x02, CBS in instance 1 with 0x04 (**CONFIRMED** on the owner readback); that 0x02 wins in ABL is **INFERENCE** (public APCB priority order, ABL not readable). If both are Manual for the same row, show `AOD` and mention CBS in the tooltip.

Note: the variables hold what was **saved**; values reach the APCB at Setup save and take effect at the next boot. Right after a save without reboot the `≠` flag is expected — the tooltip says so.

## 6. UI

- `TimingRow` gets an `Origin` dependency property rendered as a small badge (`A` / `M·AOD` / `M·CBS` / `·`), theme-aware colors, hidden when the feature is off. Tooltip: source menu, stored value, gate state, "differs from running value" when flagged.
- A View-menu toggle "Show setting origin" (default on when a layout matched).
- Snapshot export (`WPF/Export`) and the debug report include the origin per row, the layout id and the plausibility result.
- Mock/debug-report mode: the report embeds the two variable blobs (they contain only setup values; still behind an explicit "include BIOS setup variables" checkbox), so a report reproduces the column offline.

## 7. Tests and acceptance

Test vectors from the research repository: the owner's readback values and the synthetic variables used by `memory_state_report.py` (evidence 18/20).

Acceptance on the owner's BD790i X3D (1.15):

- 13 rows `AOD` (tCL 36, tRCD 40, tRP 40, tRAS 64, tRC 104, tWR 48, tRFC 480, tRTP 16, tRRDL 12, tRRDS 8, tFAW 32, tWTRL 24, tWTRS 7), Speed 6000 and FCLK 2000 `AOD`, UCLK `AOD`, MEM VDD/VDDQ/CPU VDDIO `AOD` (1.20 V stored);
- turnarounds, tRFC2, tRFCsb, ODT/RTT/drive strengths, PowerDown: `Auto`;
- tCWL, tREFI, GDM, Cmd2T, Nitro: `AGESA`;
- no `≠` on timings. **Hardware evidence (2026-11-23):** read-only Win32 UEFI read and the origin column worked on DRFXI 1.15; Speed/MCLK were incorrectly Auto because the earlier 0x3A condition was inverted. Corrected in this PR; retest pending. MEM VDD/VDDQ `≠` was confirmed as a real saved-vs-running mismatch: PMIC5100 set-points 1100 mV and ADC about 1095 mV, despite 1200 mV saved in AOD. Reason for the firmware/PMIC difference is still unknown.

## 8. Work breakdown

| Step | Effort |
|---|---|
| Port the variable reader (privilege + read + error mapping) | 0.5 d |
| Layout resource generator (Python, in the research repo) + JSON loader | 0.5 d |
| Origin resolver + plausibility check + unit tests with recorded blobs | 0.5–1 d |
| TimingRow badge, tooltip, toggle, themes | 0.5 d |
| Export/debug-report integration, mock mode | 0.5 d |
| Hardware run on the owner's board, fix-ups | 0.5 d |

Out of scope for phase 1: layout derivation, other boards, APCB flash read, issue reporting.
