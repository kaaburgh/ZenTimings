# MSI MS-7D75: 7D75v1E5 → 7D75v1E firmware comparison

This analysis uses the two **user-provided** MSI BIOS archives. Both images were unpacked with UEFIExtract A75 and their AMD CBS, AOD, PBS and MSI Setup IFR was extracted with IFRExtractor-RS v1.6.1. No firmware was flashed.

## Identity

| | 7D75v1E5 | 7D75v1E |
|---|---|---|
| Release notes | 2024-01-25, V1.E5 | 2024-02-21, V1.E |
| Image filename | `E7D75AMS.1E5` | `E7D75AMS.1E0` |
| Raw image size | 33,554,432 bytes | 33,554,432 bytes |
| Image SHA-256 | `0fb6ed2a75dbc84e8c6e9273f5fe9a3e2e674bb202d7ec6de6dbe339605b3f8f` | `cc84d397d04617d86f2980dd79e04ef90fb3302b72e47adf43f54c52eff4f368` |
| Archive SHA-256 | `be18701e69f0d06b7108082f8c2ec35290382e86ff193996c8fc4c386362e3cd` | `aa922ad65d6fc3b60e1007e34e6c058d1cda884301583d53a80a1ecad462091b` |

Both release notes reference AGESA ComboPI 1.1.0.2b. The later release notes additionally mention the Ryzen 8000 STAPM fix. This comparison does **not** prove the entire AGESA payload or executable memory-training path is identical.

The images differ in 7,538,582 out of 33,554,432 bytes (22.47%). 6,183 out of 8,192 aligned 4-KiB blocks are identical. UEFIExtract finds the same list of 533 GUID records and the same 522 PE/TE image paths; 141 executable images compare byte-for-byte identical, 381 differ (some only by one or a handful of constants). Binary image-level byte divergence is **not** equivalent to behavioral divergence.

## Direct IFR comparison — core result

After excluding the IFR extractor's initial input-hash line, all extracted form text is byte-for-byte equal for:

- `CbsSetupDxeRPL` (5,356 IFR lines after the header, one form text);
- `CbsSetupDxePHX` (6,583 lines);
- `AodDxe` (four extracted form texts, each equal);
- `AmdPbsSetupDxe` (two extracted form texts, each equal).

Thus **all IFR-declared field offsets, ranges, option values, varstore declarations and menu gates for AMD CBS and AMD Overclocking are unchanged** between these builds.

The important IFR varstores are:

| Variable | 1E5 size | 1E0 size | Conclusion |
|---|---:|---:|---|
| `AmdSetupRPL` | `0x779` | `0x779` | Same IFR, size and external NVAR defaults |
| `AodSetupRpl` | `0x320` | `0x320` | Same IFR; still no default entry in external StdDefaults |
| `Setup` (MSI) | `0x7C2` | `0x7C4` | **Different release-specific fingerprint** |

The external `AmdSetupRPL` StdDefaults bodies are exactly identical, 1,913 bytes each, SHA-256 prefix `7eab1384a73dfb36159908a6`. `Setup` StdDefaults bodies are 1,986 versus 1,988 bytes. A byte-sequence alignment finds two additional bytes in the later defaults at about `0x761` and `0x78C` (the second insertion is shifted by the first one). Those are high-offset additions, not a blanket renumbering of every MSI memory setting.

Normalizing the changing question/form IDs in MSI Setup IFR leaves 62 edit blocks: one varstore-size change and 60 setting-offset changes, all near the high end of `Setup` (`0x761`–`0x7C3` in the later image). The changed setting offsets shift by 1 or 2 bytes. The memory OC fields relevant below retain their offsets.

## MSI memory controls that remain stable across both releases

The following fields are explicitly present in **both** MSI Setup IFR extracts:

| Menu setting | `Setup` offset(s) | Interpretation |
|---|---|---|
| Top-level A-XMP | `0x73` | Raw selector (0 = disabled) |
| Top-level AMD EXPO | `0x74` | Raw selector (0 = disabled) |
| Conditional A-XMP | `0x595`, `0x6F9` | Raw selectors; active branch not established |
| Conditional EXPO | `0x596`, `0x6FA` | Raw selectors; active branch not established |
| DRAM Frequency | `0x51B`, `0x68C` | Independent MSI frequency menus; 16-bit fields |
| FCLK Frequency | `0x516`, `0x686` | Independent MSI frequency menus |
| UCLK DIV1 MODE | `0x518`, `0x688` | Independent MSI clock-ratio menus |
| High-Efficiency Mode | `0x57D`, `0x6F2` | MSI memory optimization menu |
| Memory Timing Preset | `0x57E`, `0x6F3` | MSI timing preset menu |
| Memory Try It! | `0x597`, `0x6FB` | MSI memory OC preset; 16-bit fields |

**Important PR review correction prompted by this comparison:** the first 1E0 resolver tracked only the four conditional EXPO/A-XMP selectors. The top-level `0x73`/`0x74` selectors and Memory Try It controls also need to be considered for the debug report, and the independent MSI memory-frequency / timing controls mean **CBS/AOD Auto alone is insufficient to declare MSI timing or Speed to be Auto**. PR #5 now captures the additional raw selectors and leaves unmatched rows as `?` pending a vendor apply-path mapping. This is intentional, not a regression.

## Static executable comparison — no obvious new AMD menu logic

While the PE images have different SHA-256 hashes, examining the PE `.text` sections gives a more precise view of the selected AMD components:

| Module | Differing bytes in `.text` | Observation |
|---|---:|---|
| `CbsSetupDxeRPL` | 11 of 32,320 | Changed immediate identifiers, each by 1 |
| `AodDxe` | 20 of 100,672 | Changed immediate identifiers, each by 1 |
| `AodSmm` | 12 of 31,232 | Changed immediate identifiers, each by 1 |
| `CbsSetupSmmRPL` | 1 of 5,120 | One immediate identifier changed by 1 |

For these specific sections, the differing instruction bytes are one-unit changes of integer immediates (e.g. `0x49E` versus `0x49D`), while the remaining bytes and instruction/branch layout are identical. This is consistent with regenerated indices following a setup-question change and **supports reusing the same IFR timing mapping**. It does not establish firmware's actual effective CBS/AOD precedence, MSI profile-application behavior, APCB priority or runtime variable visibility.

The MSI `Setup` executable itself changed far more substantially, including resources, consistent with its IFR/form changes. It must not be assumed byte-compatible with the previous release.

## Versioning decision for PR #5

- `7D75v1E` / `E7D75AMS.1E0` and `7D75v1E5` / `E7D75AMS.1E5` are **both supported as `derived`**, with one shared IFR layout. Canonical releases `1.E0` and `1.E5` each recognize the corresponding SMBIOS and vendor naming aliases.
- The catalog stores an independent **release-specific Setup size fingerprint**: `1.E0 → 0x7C4`, `1.E5 → 0x7C2`. AMD CBS and AOD fingerprints are inherited unchanged from the shared entry. The MSI resolver receives the selected release fingerprint rather than assuming a single size.
- A readout with the wrong Setup size is rejected even when the timing offsets are shared. No BIOS version is silently inferred from the blob.
- A hardware readout is required before promoting either BIOS to `verified` or trusting the active MSI EXPO/manual configuration path.
