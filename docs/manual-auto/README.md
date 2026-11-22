# "Manual / Auto" origin column — plan

Goal: next to every memory value ZenTimings shows, tell the user **where the value comes from**:

| Origin | Meaning |
|---|---|
| `AOD` | set Manual in *AMD Overclocking* (AodSetupRpl) |
| `CBS` | set Manual in *AMD CBS* (AmdSetupRPL) |
| `Vendor` | set in a board vendor's own OC menu (phase 2) |
| `Auto` | no manual setting; AGESA derived the value (SPD / defaults) |
| `AGESA` | parameter has no menu field at all (e.g. tCWL, tREFI, GDM, Cmd2T on DRFXI) |
| `≠` flag | a manual setting exists but the running register value differs (clamped/rounded by AGESA, overridden by another menu, or not applied yet) |
| `?` | layout unknown or failed the plausibility check |

Plan status: **design only, no code yet.** Documents:

1. [`01-bd790i-plan.md`](01-bd790i-plan.md) — phase 1: MINISFORUM BD790i / BD790i X3D, BIOS DRFXI 1.15 and 1.17 (Ryzen 7945HX/7945HX3D, Dragon Range, AGESA DragonRangePI-FL1 1.0.0.3h).
2. [`02-any-board-plan.md`](02-any-board-plan.md) — phase 2: architecture for any board (layout database, in-app layout derivation from the user's BIOS file, plausibility check, "report a problem" GitHub issue, AI-assisted maintainer pipeline, APCB path).
3. [`03-layout-derivation.md`](03-layout-derivation.md) — how the field layout is computed from one vendor BIOS image, what is deterministic, what is not, and the effort per new board, based on the BD790i work.

## Data sources (what exists today and what is new)

| Source | Gives | Access from Windows | In ZenTimings today |
|---|---|---|---|
| UMC registers (SMN) | running value of every timing | ring-0 driver (PawnIO / InpOut) | yes (the values shown) |
| ACPI AOD region (`AODE` OperationRegion, ZenStates-Core `Hardware/Aod`) | "current AOD values" published by firmware (timings, ProcODT, MemVddio/Vddq/Vpp, ApuVddio) | physical memory read | yes (AOD panels, voltages) |
| UEFI setup variables `AmdSetupRPL` / `AodSetupRpl` (+ vendor varstores) | **what the user set and whether it is Manual** | `GetFirmwareEnvironmentVariableEx` (admin + SeSystemEnvironmentPrivilege), no driver; only for variables with the runtime attribute | **new** |
| APCB override tokens in SPI flash | what ABL actually consumes at training; absent token = Auto; token IDs are AGESA-wide constants | flash read (memory-mapped BIOS window) — to be researched | **new, optional** |

The new column comes from the setup variables (phase 1) and, where flash can be read, from APCB (phase 2). The running register value stays the reference for the `≠` flag.

## Evidence this plan builds on

Research repository: [kaaburgh/DRFXI-bios-analysis](https://github.com/kaaburgh/DRFXI-bios-analysis) (branch `memory-117-config-lifecycle`):

- `evidence/memory-117/setup-questions.json` — all 574 CBS and 118 AOD questions of DRFXI 1.15/1.17 (offset, width, options, defaults), extracted from the official image;
- `evidence/memory-117/cbs-apcb-map.json`, `aod-apcb-map.json` — which setting becomes which APCB token, under which guard (extracted from `CbsSetupDxeRPL` and `AodSmm` code);
- `evidence/memory-117/20-owner-115-readback-aod-validation.txt` — on a real BD790i X3D readback the AOD map predicted all 21 stored APCB tokens exactly;
- `scripts/fwtools/memory_state_report.py`, `scripts/win/drfxi-mem.ps1` — working offline/Windows readers of the two variables with the same "manual / Auto" logic this plan ports to C#.

Labels used in these documents: **CONFIRMED** (byte/IFR fact or real-data match), **INFERENCE**, **UNKNOWN**, **TO VERIFY** (needs a run on hardware).
