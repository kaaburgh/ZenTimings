# MSI MAG B650 TOMAHAWK WIFI — BIOS 7D75v1E

**Evidence status: `ai-proposed`.** This is a firmware-image-derived candidate, not supported origin display. The application must keep showing unknown origins until the MSI apply path and a real hardware readout are checked.

## Input and provenance

- User-supplied archive: `7D75v1E.zip`
- Firmware member: `7D75v1E/E7D75AMS.1E0`
- Raw image size: 33,554,432 bytes (32 MiB)
- SHA-256: `cc84d397d04617d86f2980dd79e04ef90fb3302b72e47adf43f54c52eff4f368`
- Release notes: `7D75v1E/7D75v1x.txt`
- Board stated by the release notes: **MAG B650 TOMAHAWK WIFI (MS-7D75)**
- Release date: **2024-02-21**
- Release notes identify an AMI BIOS, **AGESA ComboPI 1.1.0.2b**, and a fix for the Ryzen 8000-series STAPM issue.

The image was unpacked and examined offline with UEFIExtract_A75 (full tree) and IFRExtractor-RS 1.6.1. No vendor firmware binary is copied into this repository; only derived metadata and offsets are committed.

## Discovered variable stores

| Variable | GUID | IFR size | Evidence in image |
|---|---|---:|---|
| `Setup` | `EC87D643-EBA4-4BB5-A1E5-3F3E36B20DA9` | `0x7C4` | Present in StdDefaults NVAR; attributes `0x83` (Runtime, AsciiName, Valid) |
| `MsiOcInfoFlag` | `48419251-DD85-4597-84B4-11182014ADDE` | `0xF3` | HII varstore used by MSI OC form conditions; no StdDefaults NVAR entry found |
| `AmdSetupRPL` | `3A997502-647A-4C82-998E-52EF9486A247` | `0x779` | Present in StdDefaults NVAR; attributes `0x83` |
| `AmdSetupPHX` | same AMD GUID | `0x831` | Present in StdDefaults NVAR; attributes `0x83` |
| `AodSetupRpl` | `5ED15DC0-EDEF-4161-9151-6014C4CC630C` | `0x320` | Declared by AodDxe IFR and referenced by AodSmm; no StdDefaults NVAR entry found |
| `AodSetupPhx` | same AOD GUID | `0x320` | Declared by AodDxe IFR and referenced by AodSmm; no StdDefaults NVAR entry found |

The `0x83` attributes prove that the *image's default NVAR entries* are runtime variables. They do not prove that the target machine currently exposes every variable at runtime. In particular, AOD and `MsiOcInfoFlag` need a live read.

## MSI OC Setup fields

MSI's own `Setup` variable contains profile selection and direct memory-clock controls. The IFR defines two CPU/platform paths. The first is labelled RPL here because its surrounding AMD CBS family is RPL; the MSI selector's exact CPU-family mapping is not yet proven:

| Setting | RPL candidate path (selector 0x2C8 = 2) | Alternate path (selector 0x2C8 in 3,4) |
|---|---:|---:|
| A-XMP selector | `0x595` | `0x6F9` |
| EXPO selector | `0x596` | `0x6FA` |
| DRAM Frequency | `0x51B` | `0x68C` |
| FCLK Frequency | `0x516` | `0x686` |
| UCLK DIV1 MODE | `0x518` | `0x688` |
| Memory Context Restore | `0x594` | `0x6F8` |
| Memory Try It! | `0x597` | `0x6FB` |

The RPL-candidate-path EXPO values are Disabled=0, Profile 1=1, Profile 2=2, User Profile 1=3. A-XMP has Disabled=0, Profiles 1–3=1–3, and User Profiles 1–2=4–5. DRAM/FCLK value `0xFFFF` means Auto; UCLK `0xFF` means Auto, 1 means UCLK=MEMCLK, and 0 means UCLK=MEMCLK/2.

The IFR references `MsiOcInfoFlag` QuestionId `0x2C8` at offset `0x01` and QuestionId `0x2C9` at offset `0x18` to gate these paths. The exact CPU-family meaning of the selector values is not yet established. Older A-XMP/EXPO fields at `Setup+0x73/0x74` are inside an unconditional `SuppressIf True` and are not treated as active settings.

The factory defaults in the image have both EXPO/A-XMP selectors disabled, DRAM/FCLK Auto, UCLK Auto, MCR Auto, and Memory Try It disabled. These are firmware defaults, **not** evidence of the friend's live settings.

## AMD CBS maps

The BIOS includes separate Raphael and Phoenix CBS formsets and varstores.

| Field | Raphael: `AmdSetupRPL` | Phoenix: `AmdSetupPHX` |
|---|---:|---:|
| Active Memory Timing Settings | `0x116` | `0x05E` |
| Memory Target Speed (16-bit MT/s) | `0x117` | `0x05F` |
| Power Down Enable | `0x16F` | `0x0BB` |
| Memory Context Restore | `0x26C` | `0x437` |
| Infinity Fabric Frequency and Dividers | `0x269` (8-bit enum) | no DDR5 field found in this formset |

For both CBS formsets, timing controls are one byte and values are 16-bit, with the value immediately following its control. The full ordered control/value maps are in the machine-readable JSON next to this document. The Raphael map begins at `tCL 0x119/0x11A`; Phoenix begins at `tCL 0x061/0x062`.

The Raphael timing offsets happen to overlap the first part of the DRFXI CBS map. That is not a sufficient fingerprint: this image's RPL varstore is `0x779` bytes, versus DRFXI's `0x6D5`, and its first three 32-bit words at offsets `0x00`, `0x04`, and `0x08` are all zero in StdDefaults (not DRFXI's `0xFC4053AA` / `728` / `0x4B9D5809` header). The Phoenix store is `0x831` bytes and has the same zeroed first three words. The DRFXI resolver must not be reused.

## AMD AOD maps

AodDxe declares both AOD formsets, each with a `0x320`-byte varstore. The offsets differ between RPL and PHX:

| Field | AOD RPL | AOD PHX |
|---|---:|---:|
| Active Memory Timing Settings | `0x3C` | `0x3A` |
| Memory Target Speed | `0x3D` | `0x3B` |
| tCL control/value | `0x3F/0x40` | `0x3D/0x3E` |
| Power Down Enable | `0x93` | `0x91` |
| FCLK | `0x94` (16-bit) | `0x92` (16-bit) |
| UCLK DIV1 MODE | `0x96` | `0x94` |

The complete timing maps are recorded in the JSON. AodSmm contains the strings `AodSetupRpl` and `AodSetupPhx`, but neither variable appears in the extracted StdDefaults NVAR list. Therefore the image proves the formset layout and code references, not that a fresh installation has a readable runtime AOD variable.

## Why this remains ai-proposed

1. MSI Setup, AMD CBS, and AMD AOD expose overlapping memory controls. IFR tells us what the menus store, not which path wins when multiple menus contain settings.
2. The MSI-specific apply path in `MsiOcDxe`, `MsiOcSmi`, `MsiOcMemSPDDxe`, and `MsiOcMemSPDPei` has not been fully traced. `MsiOcMemSPDDxe` also contains identifiers such as `MsiOcMemSpdData`, `MsiOcMemSpdXMPInfo`, `MsiOcUserXMPProfile`, `MsiOcMemSpdCheckSum`, `MsiOcMemPatchID`, `MsiOcMemPmicInfo`, and `MsiOcMemECCInfo`; their GUIDs, storage formats, and apply semantics remain unresolved.
3. RPL and PHX use different CBS/AOD varstores and offsets. `MsiOcInfoFlag` gates the UI paths, but its selector values have not yet been mapped to CPU families.
4. The AOD variables are not in the image's default NVAR list, so their runtime presence/attributes need to be checked on the board.
5. There is no live MSI hardware readout to compare against ZenTimings values or APCB tokens.

Accordingly, the catalog candidate is deliberately `ai-proposed`. Live and offline origin resolution must refuse to show AOD/CBS/Manual badges for this layout.

## Next evidence needed

The Debug dialog's explicit **Include BIOS setup variables (sensitive configuration)** checkbox now exports every variable in the selected candidate catalog, including missing-variable errors and returned attributes. On the MSI board, run the build as Administrator if firmware reads fail for lack of privilege, enable that checkbox, and share the report privately. The default debug report still omits raw variable contents. The first readout should establish the actual SMBIOS BIOS string, active RPL/PHX branch, whether AOD variables exist, and the saved MSI/AMD menu values; it still will not by itself prove apply precedence.
