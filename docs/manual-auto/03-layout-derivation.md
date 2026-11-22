# Deriving the field layout from one vendor BIOS image

Question: for a new board, can the "which byte of which variable means 'tCL is Manual'" layout be computed from the single BIOS file the vendor publishes, deterministically? Short answer: **for the AMD CBS/AOD menus, yes**; two parts need code analysis once per AGESA family, and vendor menus need a semantic step that is not fully deterministic.

## 1. What the BD790i work actually required

Everything below was derived from the official `DRFXI.BIN` (hash-checked), no board needed until validation. Tools are in the research repository (`scripts/fwtools/`).

| Step | BD790i result | Reusable as |
|---|---|---|
| Image identity, unpack FV/FFS, LZMA, AMD-zlib volumes | done (`un7z.py`, `fvparse.py`) | deterministic, generic |
| Find menu drivers | `CbsSetupDxeRPL`, `AodDxe` | generic by varstore/formset search; name patterns per family |
| IFR + strings → questions | 574 CBS + 118 AOD questions with offset/width/options/defaults (`ifrmini.py`) | deterministic, generic |
| Ctrl/value pairing | 25 timing pairs in each menu | deterministic by AMD naming convention ("X Ctrl" + "X") |
| Defaults | CBS from shipped `StdDefaults`; AOD from IFR | deterministic |
| Apply semantics (gates, skip values, precedence) | CBS sync routine disassembled (`cbs_apcb_map.py`); AodSmm writes extracted (`aod_apcb_map.py`, 63 writes) | once per AGESA family: AMD's code, same across vendors |
| Runtime-created question | APU VDDIO (0x197) exists only in `AodDxe` code, not in static IFR | found by code reading |
| Validation | owner's 1.15 readback: 21/21 APCB tokens predicted | needs one real readout per layout family |

Most of the research effort went into the 1.15→1.17 regression (flash coverage, PSP directories, PCD normalisation), which the origin column does not need.

## 2. Pipeline for a new board

| # | Step | Deterministic? | Typical effort |
|---|---|---|---|
| 1 | Unwrap the vendor package (7z/zip/exe/capsule) to the BIOS image | yes for raw/AMI capsules; vendor EXE wrappers (Dell/HP/Lenovo) need known unpackers (e.g. platomav/BIOSUtilities) | minutes; first time per vendor format: hours |
| 2 | Walk FVs, decompress (LZMA, Tiano, AMD zlib) | yes | automated |
| 3 | Collect HII packages; parse IFR and strings | yes | automated |
| 4 | Select memory-relevant varstores: AMD ones by name (`AmdSetup*`, `AodSetup*`), vendor ones by prompt keywords | yes for AMD; heuristic for vendor | automated + review |
| 5 | Pair Ctrl/value, read gates from IFR `SuppressIf/GrayOutIf` | yes (convention) | automated |
| 6 | Map prompts to ZenTimings rows | AMD prompts: dictionary per AGESA family, deterministic; vendor prompts: dictionary + review (AI-assisted proposals) | AMD: automated; vendor: hours–days |
| 7 | Apply semantics (which Manual settings reach APCB, guards, precedence) | from AMD code once per AGESA family; vendor apply code per vendor | family: 1–2 d once; vendor: 1–3 d once |
| 8 | Runtime-created questions | needs code reading (rare; on DRFXI only APU VDDIO) | per family, once |
| 9 | Runtime visibility of the varstore | not in the image reliably (attributes come from the driver's SetVariable call); check on hardware or via a user report | one readout |
| 10 | Plausibility self-test on a real readout | yes | minutes |

For a board in an AGESA family already covered, with stock AMD menus: steps 1–6 and 10 run automatically; the human part is a 1–2 h review. For a new family: +1–2 days once. For a vendor with its own OC menu: +2–5 days the first time.

## 3. Where determinism ends

- **Semantics of vendor prompts.** Offsets are exact; whether "DRAM CAS# Latency" in vendor X's menu feeds the same token as AMD's Tcl is a mapping decision. Dictionaries plus review; AI can propose, a readout confirms.
- **Apply logic.** The IFR describes the UI (what is visible/greyed), not what the firmware does on save. For AMD menus this was confirmed from code and real data; for vendor menus it must be checked once per vendor.
- **Runtime-created questions** do not appear in the static IFR at all.
- **Runtime attribute** of the variable decides whether Windows can read it; this is visible only on a live system.

## 4. Encryption and other obstacles

- AMD platform security encrypts or signs some PSP firmware blobs; the menu drivers and their IFR live in the ordinary UEFI firmware volumes and are not encrypted on consumer AMD platforms.
- Capsule signatures (vendor or AMD PSB) authenticate the image; they do not hide its contents.
- Vendor EXE/installer wrappers and occasional proprietary compression are packaging problems with known unpackers, not encryption.
- Setup data may also be described in AMI's TSE data (`AMITSESetupData`) for visibility/access; this matters for "is the menu visible", not for the variable layout.

The expectation that the general case has no encryption holds.

## 5. Cost summary per new board

| Situation | One-time | Per later BIOS release |
|---|---|---|
| Same AGESA family, AMD menus only | 1–2 h review + 1 real readout | automatic (fingerprint changes → re-derive, plausibility) |
| New AGESA family | +1–2 d (dictionary, apply-code check, sample image) | automatic |
| Vendor OC menu | +2–5 d per vendor | hours if the vendor menu changes |
| Variables not runtime-visible | APCB path only (see 02, section 6) | — |

The scarce resource is not code but **one real readout per layout family** to move an entry from `derived` to `verified`; the "Report a problem" flow and the readout scripts are how those arrive.
