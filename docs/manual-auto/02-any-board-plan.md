# Phase 2 — any board

Goal: the origin column on any AMD board ZenTimings supports, without hand-written tables per board, with a deterministic way to know when the answer is not trustworthy, and a cheap path for users to report what failed.

## 1. Where a layout comes from (resolution ladder)

```
1. Layout DB hit         fingerprint of the running firmware matches a DB entry       → use it
2. Derive from BIOS file user selects the BIOS file they flashed (vendor download);   → derive in-app, verify
                         ZenTimings derives the layout locally and checks it matches     against the running
                         the running firmware                                            firmware, cache by hash
3. Live flash read       (research) derive from the running image itself              → no version mismatch possible
4. Nothing matched       origin "?" + "Report a problem"                               → GitHub issue
```

Independently of the ladder, where flash can be read, **APCB tokens** give a board-independent answer (section 6).

### Fingerprint (DB key)

Not the board name alone — layouts change with AGESA updates inside one board's BIOS history. Key = all of:

- variable name + GUID + size for every varstore used (e.g. `AmdSetupRPL`/0x6D5, `AodSetupRpl`/0x1AC);
- header fields where they exist (CBS: signature + generation fingerprint at +0x08);
- SMBIOS board/BIOS version strings (informational, used to pick candidates);
- AGESA string (ZenTimings already reads it).

A candidate layout is accepted only if the **plausibility check** passes on the live variables (all Ctrl bytes in {0,1}, all values within IFR ranges, enums within option sets). This makes the DB lookup self-verifying: a wrong or stale entry fails instead of showing wrong origins.

## 2. In-app layout derivation from the user's BIOS file

Feasible and light enough for the app:

| Part | Size of work | Notes |
|---|---|---|
| Capsule/raw image detection, FV/FFS walker | ~400 LOC | AMI capsule header, raw 16/32 MiB images |
| Decompression | LZMA: already in the repo (`WPF/Decompressor/7zip`); EFI/Tiano: port from EDK II (BSD) ~500 LOC; zlib: `DeflateStream` after the 2-byte header (AMD uses zlib inside PSP-embedded BIOS volumes) | |
| HII: string package + IFR parser | ~600 LOC | standard UEFI formats; the research repo's `ifrmini.py` is the reference |
| Module/varstore discovery + Ctrl/value pairing + semantic mapping | ~400 LOC + dictionaries | see `03-layout-derivation.md` |
| Plausibility check, cache (keyed by image SHA-256) | ~200 LOC | |

Runtime cost: one pass over a 16–32 MiB file, decompressing the DXE volumes (tens of MiB) — about 1–3 s and <200 MiB peak once, then cached. No network.

Matching the file to the running firmware: SMBIOS BIOS version string equals the version embedded in the image (AMI `$BVDT`/SMBIOS type 0 template), variable sizes equal the varstore sizes declared in the IFR, CBS generation fingerprint equals the one recomputed from the image defaults (as on DRFXI), and the plausibility check passes on the live variables. If any fails: "this file does not match your BIOS".

What the app must **not** do: download BIOS files, redistribute them, or upload them anywhere.

## 3. Layout database

- JSON files in the fork (`layouts/<agesa-family>/<fingerprint>.json`), shipped with releases; optional update from the fork's GitHub releases (signed/hashed manifest).
- Each entry has a `status`: `verified` (a real readout matched, e.g. the 21/21 APCB check on BD790i), `derived` (deterministic extraction, no real readout yet), `ai-proposed` (contains vendor-field mappings proposed by the AI pipeline, not yet verified). The UI shows the status in the tooltip; `ai-proposed` fields display `?` until verified unless the user opts in.

Sketch of an entry:

```json
{
  "id": "minisforum-drfxi-dr-fl1-1.0.0.3h-v1",
  "status": "verified",
  "match": { "smbios": {"board": "DRFXI", "bios": ["1.15", "1.17"]},
             "vars": [{"name": "AmdSetupRPL", "guid": "3a997502-…", "size": 1749,
                       "header": {"u32@0": "0xFC4053AA", "u32@8": "0x4B9D5809"}},
                      {"name": "AodSetupRpl", "guid": "5ed15dc0-…", "size": 428}] },
  "sources": [
    {"menu": "AOD", "var": "AodSetupRpl", "gate": {"off": "0x3C", "on": [1]}, "precedence": 1},
    {"menu": "CBS", "var": "AmdSetupRPL", "gate": {"off": "0x116", "on": [1]}, "precedence": 2}],
  "fields": [
    {"row": "tCL", "menu": "AOD", "ctrl": {"off": "0x3F", "manual": 1}, "value": {"off": "0x40", "w": 2},
     "range": [22, 64], "step": 2, "apcb_token": "0xFC195E5C"}
  ],
  "provenance": {"image_sha256": "99f3bab5…", "tool": "derive v1", "evidence": "<link>"}
}
```

## 4. "Report a problem" → GitHub issue

When the ladder ends in `?`, plausibility fails, or the user flags a wrong origin:

1. Build a diagnostic JSON: CPU codename/family, AGESA string, SMBIOS board and BIOS strings, variable names/GUIDs/sizes/attributes and Win32 errors, the layout id tried, every plausibility failure (row, offset, value, allowed range), running values of the affected rows, image SHA-256 if a file was used. **No** serial numbers, UUIDs, MACs or raw NVRAM. Raw setup-variable blobs (settings only) only after an explicit checkbox.
2. Show the JSON to the user before anything leaves the machine.
3. Open `https://github.com/<fork>/issues/new?template=layout-report.yml&title=…&body=…` in the browser — no token, the user submits under their own account. URLs are limited to roughly 8 KB, so the URL carries a summary and the full JSON is copied to the clipboard / saved as a file to drag into the issue.
4. An issue form (`.github/ISSUE_TEMPLATE/layout-report.yml`) asks for the vendor BIOS download URL and the BIOS version.

## 5. AI-assisted maintainer pipeline (offline, not in the app)

AI does not belong in the app's runtime path (determinism, privacy, offline use, cost). It fits on the maintainer side, for the part that is not deterministic — vendor menus and unusual prompts:

```
issue with vendor BIOS URL
  → CI job downloads the official file, records its hash
  → deterministic extractor (same code as the app, CLI build) → candidate layout + list of unmapped questions
  → LLM step: given unmapped questions (prompt, help text, options, form path, varstore, neighbouring
    questions) and the semantic dictionary, propose row mappings with confidence and rationale;
    optionally summarise vendor apply code (disassembly) for gates/precedence
  → deterministic validation: IFR consistency, plausibility against the reporter's diagnostic values
  → PR to the layout DB with status "derived" or "ai-proposed"
  → becomes "verified" only after a real readout from a user matches (running values / APCB tokens)
```

Rules: every AI-proposed mapping is reviewable in the PR, never auto-merged, never shown as trusted before a real readout matches.

## 6. APCB path (board-independent where flash is readable)

APCB override tokens are what ABL consumes; their IDs are AGESA-wide constants per memory generation (e.g. `0xFC195E5C` = Tcl on DDR5), CBS and AOD write them into separate instance/priority slots, and an Auto setting leaves no token. Reading them needs:

- a flash read from Windows: memory-mapped BIOS window via the physical-memory read ZenTimings already uses for the ACPI AOD region. **TO VERIFY**: whether the APCB area of a 32 MiB part is inside the mapped window, and behaviour with AMD ROM Armor on mobile platforms;
- PSP/BIOS directory walk to find the active APCB (standard AMD format), APCB v3 parser (~300 LOC);
- a token dictionary per memory generation (DDR4 Zen 2/3, DDR5 Zen 4/5), derived once per AGESA family from the CBS/AOD apply code.

Gain: vendor OC menus that write standard tokens are covered without per-vendor work, and the column shows what training actually uses. Limits: vendor-specific APCB types; precedence between slots is **INFERENCE** until verified on more boards.

## 7. Coverage tiers

| Tier | Boards | Expected path | Effort per new BIOS family |
|---|---|---|---|
| A | stock AMD CBS/AOD menus (mini-PCs, many laptops, some boards) | deterministic derivation, DB entry | minutes automated + 1–2 h review |
| B | vendor OC menus (desktop AM5/AM4 from large vendors) | vendor adapter (dictionary + apply-code check) or APCB path | 2–5 days per vendor first time, hours per BIOS |
| C | setup variables not runtime-visible (e.g. AMI `Setup` without the runtime attribute) | APCB path only | depends on flash read |
| D | unusual packaging / non-standard compression | extractor plug-in | case by case |

## 8. Milestones and effort

| Milestone | Effort |
|---|---|
| M1 phase 1 (BD790i) | 2–3 d |
| M2 deterministic deriver (CLI + in-app), CBS/AOD of the Raphael/Dragon Range family, DB loader, plausibility check | 1–1.5 w |
| M3 "Report a problem" + issue form | 2–3 d |
| M4 further AGESA families (Phoenix/Hawk Point, Strix, Granite Ridge, DDR4 Zen 3), one sample image + one readout each | 1–2 d each |
| M5 first vendor adapter (tier B) | 3–5 d |
| M6 APCB path incl. flash-read research on hardware | ~1 w |
| M7 AI-assisted CI pipeline | 3–5 d |

## 9. Risks

- **Stored ≠ applied.** Variables show saved intent; the `≠` flag and the APCB path cover it.
- **Stale DB entries.** Mitigated by fingerprint + plausibility; a stale entry degrades to `?`, never to a wrong answer silently.
- **Vendor menus.** Semantics are not deterministic; that is what the AI-assisted review and real readouts are for.
- **Low-level access.** The variable path needs no driver; the APCB path depends on what the existing driver stack allows. Read-only in every case.
- **Upstream.** The fork is GPL-3.0; layout JSON contains derived metadata (offsets, prompts), no vendor binaries. Whether upstream accepts the feature is open; keeping it in a separate assembly/folder eases a later PR.
