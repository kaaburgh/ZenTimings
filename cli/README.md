# ZenTimings CLI (Linux)

Saved BIOS settings plus opt-in experimental running UMC timings. Compiles the existing C# BIOS catalog/resolvers and embeds the same resources as WPF; no forked BIOS offset tables. Running mode compiles only the two pinned ZenStates-Core timing dictionaries, not its Windows hardware stack or PawnIO. The Windows project continues to target .NET Framework.

## Build

Requires .NET 10 SDK. From the repository root (initialize the pinned submodule first):

    git submodule update --init ZenStates-Core

    dotnet build cli/ZenTimings.Cli/ZenTimings.Cli.csproj -c Release
    dotnet publish cli/ZenTimings.Cli/ZenTimings.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/linux-x64

The Linux CI artifact is a tar.gz containing a self-contained x64 executable (glibc Linux, no installed .NET runtime required; normal system libraries still required). Extract the archive into a dedicated directory. For origins/replay no driver loading is needed; for running timings an already installed and loaded ryzen_smu is required.

## Use

    ./zentimings --help
    sudo ./zentimings --origins
    sudo ./zentimings --json
    ./zentimings --replay report.txt --json

Use sudo only if needed for firmware read permissions. The CLI reads DMI identity in /sys/class/dmi/id and EFI variables in /sys/firmware/efi/efivars. It strips the four-byte little-endian attributes prefix before passing payload bytes to the shared resolver. It never writes firmware, clears immutable flags, loads drivers, mounts filesystems, or probes arbitrary variable names. Unknown boards/releases and mismatched fingerprints fail closed. An optional variable that is absent may be skipped, but an I/O error or malformed attribute prefix is not silently treated as absence. EFI runtime services can hide variables regardless of root permissions.

Live matching uses the shared Lenovo system family/machine type fallback, not BIOS version alone. All existing catalog evidence tiers still apply. A matched result means saved settings passed existing catalog/resolver checks; no claim that the hardware applied them. The default origins/replay modes remain unchanged and never access SMN.

### Capture for research (explicit opt-in)

Raw reports may contain private firmware data. Save locally with restrictive permissions and inspect before sharing; do not post full NVRAM on a public issue.

    (umask 077; sudo ./zentimings --debug > report.txt)

This attempts the five known variable names independently, with attributes, length, SHA-256 and payload base64. Capture does not require catalog recognition. No upload occurs. The ordinary table/JSON never contains raw blobs. The report uses the existing Windows resolver identity and variable base64 line format for offline replay. Windows debug reports containing those identity lines can be replayed on Linux; older reports may require explicit paired overrides:

    ./zentimings --replay report.txt --board 'ThinkCentre M75n' --bios M33KT24A --json

Offline replay never reads the current host's firmware. Exit codes: 0 matched (or debug captured at least one variable), 1 unavailable/unmatched, 2 invalid command or uncaught I/O error. For tests only, --sysfs-root points at a synthetic sysfs tree; never use arbitrary untrusted paths with sudo.

## Tests

    dotnet run --project tests/DrfxiOriginSmoke/DrfxiOriginSmoke.Linux.csproj -c Release
    python3 tests/LinuxCli/test_cli.py cli/ZenTimings.Cli/bin/Release/net10.0/zentimings.dll

CI runs existing resolver smoke tests on Linux, synthetic efivarfs/DMI integration tests, and publishes the x64 artifact. Hardware acceptance is still required, first on M75n M33KT24A, then on BD790i and other known boards. Compare read-only Linux capture sizes/header words with existing Windows/AFU evidence; do not flash or change firmware to test the CLI.

## Experimental running memory timings

    sudo ./zentimings --timings
    sudo ./zentimings --timings --json
    sudo ./zentimings --compare --json

Explicit opt-in only. Stop other users of the ryzen_smu SMN interface (including monitoring apps that use SMN) first. Do not run concurrently with firmware tuning software. The CLI does not install/load the driver, issue mailbox commands, write any register values, access PCI config directly or use /dev/mem. It uses the driver's four-byte address-only read request and reads back the four-byte result. The driver is external; inspect its version and trust it before running as root.

Supported CPU IDs: AuthenticAMD family 17h models 11h/18h (Raven/Picasso-class DDR4), family 19h model 61h (Raphael/Dragon Range DDR5). Other CPUs are rejected before any SMN operation. These are candidate maps, not hardware-verified compatibility claims. Only UMC 0/1 are probed; presence is checked via the pinned upstream channel-disable and chip-select bits, and the DRAM type is checked against the CPU map. LPDDR and other families are deliberately out of scope. A failed/unstable read fails the snapshot, not silently a zero. All-ones responses, implausible primary timings, and inconsistent register snapshots are rejected.

Read-only here means no mutation of UMC data or firmware settings. The driver sysfs protocol requires writing a four-byte address to select a read; eight-byte writes would mutate registers and are never issued. Its result is a global buffer, not a per-client transaction. The CLI serializes its own instances and rereads registers in reverse order, but this cannot establish atomicity with other clients or reliably detect every underlying driver error (the reference driver logs SMN read errors and can still acknowledge the request). Therefore this mode is experimental. There is no unsafe direct PCI fallback. See [driver protocol](https://github.com/amkillam/ryzen_smu/blob/main/README.md#syskernelryzen_smu_drvsmn) and [driver implementation](https://github.com/amkillam/ryzen_smu/blob/main/drv.c).

The maps are compiled directly from the checked-out submodule at commit 869b7e82572337e5a673e92230edc92bddf0e44b: [DDR4](https://github.com/irusanov/ZenStates-Core/blob/869b7e82572337e5a673e92230edc92bddf0e44b/Dictionaries/DDR4Dictionary.cs), [DDR5](https://github.com/irusanov/ZenStates-Core/blob/869b7e82572337e5a673e92230edc92bddf0e44b/Dictionaries/DDR5Dictionary.cs), and the refresh/ratio handling derives from Hardware/DRAM/Ddr4Timings.cs, Ddr5Timings.cs, BaseDramTimings.cs and MemoryConfig.cs at that revision. The CLI intentionally omits ambiguous rank-dependent refresh values, rather than selecting a first non-default value. All output clocks are nominal from the UMC ratio assuming 100 MHz BCLK; they are NOT measured frequencies. Optional Picasso PM Table telemetry is described below. SPD and wider voltage telemetry remain future work.

Compare mode reports each populated UMC separately, never merges channel readings or treats unknown origins as Auto. The existing BIOS resolver controls whether saved settings are trusted. Running data is independent of UEFI access, and compare mode can return a running snapshot even when origins are unavailable; check origins_matched/origins_status. The comparison uses the existing DiffersFrom method, excludes nominal clocks, and does not assert AGESA application or a required reboot. JSON includes the selected raw UMC registers (not raw UEFI blobs) for reproducible diagnosis.

### Memory module inventory and optional SPD acquisition

    sudo ./zentimings --modules
    sudo ./zentimings --modules --json
    sudo ./zentimings --compare --extended --pm-table --modules --json

Explicit opt-in. Reads the kernel-provided binary SMBIOS table /sys/firmware/dmi/tables/DMI directly (no dmidecode dependency). Type 17 records with populated or unknown capacity are listed, including locator, bank locator, manufacturer, part number, memory type, form factor, capacity, ranks, widths, rated/configured data rate and configured voltage when present. Empty slots are skipped; unknown fields remain null. These are firmware-reported values, not live measurements. A malformed table produces an inventory status rather than invalidating a good UMC snapshot.

The same mode checks already-enumerated I2C device directories at SPD addresses 0x50–0x57 and reads eeprom only for devices bound to ee1004 or spd5118. No I2C address scans, direct SMBus accesses, driver loading/binding, or sysfs writes. SPD reads may involve kernel-managed bus transactions. A missing EEPROM is reported explicitly. The initial SPD decoder checks DDR4/DDR5 device type, bounds and base CRC (DDR4 bytes 0–125, DDR5 bytes 0–509), also DDR4 module CRC when present. Exports a limited set of revision, module part number, manufacturer JEP106 code and tCK minimum/rate derived from tCK; the manufacturing area is not covered by the base CRC. The calculated rate is not a measured clock or rounded JEDEC speed grade. This is intentionally **not** full SPD/profile decoding: capacity/organization, all JEDEC timings, XMP/EXPO, and PMIC/temperature remain follow-up work. No raw SPD, serial numbers or asset tags are emitted. No automatic association between SPD device, SMBIOS locator and UMC channel is assumed.

On M75n no bound SPD device is expected based on the previous hardware report; SMBIOS should show two Micron 4 GiB single-rank DDR4 modules rated 2667 MT/s, configured 2400 MT/s, 1200 mV. Standalone --modules does not require SMN or UEFI variable access; it returns 0 if module inventory or valid SPD is available, otherwise 1. In combined mode, check the separate inventory statuses; optional inventory failure does not discard a working UMC result. The --sysfs-root option is permitted for standalone fixture testing only; never point it at untrusted directories with sudo. Synthetic tests cover two populated modules plus an empty slot, privacy exclusions, valid DDR4/DDR5 SPD CRC, invalid/truncated SPD and malformed SMBIOS. Live inventory/SPD decoding has not yet been verified; M75n can validate SMBIOS and the absent-SPD path, but not the positive SPD path.

### Extended UMC readout (experimental)

    sudo ./zentimings --timings --extended
    sudo ./zentimings --compare --extended --pm-table --json

Explicit opt-in; the ordinary timing path still reads the same registers. Extended mode adds controller configuration (ECC enable, burst, power-down), mode-register and PHY timings, BGS/BGSAlt, fine-granularity refresh and DDR5 Nitro. It uses the pinned DDR4/DDR5 dictionaries and the decoding rules in BaseDramTimings.cs, Ddr4Timings.cs and Ddr5Timings.cs. Extra field names follow the upstream dictionaries and are intentionally kept separate from saved BIOS comparison rows. JSON adds a nullable extended object per UMC, with raw registers, decoded fields, refresh mode, Nitro and unavailable-address diagnostics. All new hardware reads stay inside the already restricted UMC range and the existing SMN lock; missing, all-ones or unstable optional registers are omitted and reported rather than presented as zero. The underlying global driver buffer limitation remains. An all-zero BGS mapping pair is unresolved. A nominal effective tRFC duration is reported only when the refresh mode and relevant non-ambiguous tRFC word support it, using the UMC ratio at assumed 100 MHz BCLK (not SMU telemetry). DDR4 on-the-fly/reserved refresh and DDR5 non-normal modes omit the duration. Text output also renders Cmd2T as 1T/2T and GDM/Power Down as Enabled/Disabled; numeric JSON is unchanged.

**Electrical parameters are not part of this decoder.** Windows DDR4 ApplyDdr4MemoryConfig obtains ProcODT, RTT, drive strengths and setup values from APOB, or a BIOS APCB configuration acquired through ACPI/WMI. The DDR5 path uses AOD/APOB data. Neither can be inferred from the UMC timing dictionaries. Linux acquisition of these sources remains separate work; this CLI explicitly reports electrical parameters unavailable rather than inventing register offsets or using inactive saved BIOS values. No direct physical-memory reads, arbitrary ACPI calls or SMBus probing were added.

Tests cover synthetic DDR4/DDR5 extended words, different FGR modes, Nitro, BGS, optional register errors and preservation of all existing basic timing fixtures. Extended capture has not yet been tested on real hardware. On M75n, collect one --compare --extended --pm-table --json report; compare the existing timing rows with the preceding capture and check extended.status / unavailable. DDR5 remains pending on BD790i.

### Experimental Picasso PM Table

    sudo ./zentimings --pm-table
    sudo ./zentimings --compare --pm-table --json

Explicit opt-in; the driver can issue an SMU refresh internally when reading pm_table. No direct mailbox or register-value writes by the CLI. Stop other SMU clients first. Uses read-only sysfs files and the same inter-process mutex as the UMC reader. The two captures are sequential, not atomic. UMC-only modes remain unchanged, and a failed optional PM Table read does not discard a valid UMC snapshot; check pm_table.available and status.

Supported tuple: AMD family 17h model 18h, ryzen_smu 0.1.2, codename 03, MP1 interface 1, version 0x001E0004, reported and actual size 0x6AC (1708), all bytes from 0x608 onward zero. Metadata is checked before and after the read; unknown versions or lengths are rejected. The pinned [ZenStates-Core PowerTable.cs](https://github.com/irusanov/ZenStates-Core/blob/869b7e82572337e5a673e92230edc92bddf0e44b/Hardware/PowerTable.cs#L175-L191) defines 0x608 for this version, with float32 LE offsets FCLK=0x298, UCLK=0x29C, MCLK=0x2A0, VSOC=0x104, CLDO VDDP=0xF0. Two real M75n samples have zero-filled extra 164 bytes. Sample 1: 556.534 / 556.534 / 839.060 MHz, 0.717033 / 0.8 V; sample 2: 565.166 / 565.166 / 841.214 MHz, 0.726607 / 0.8 V. Only selected words are retained in the regression tests. These labels are inherited from upstream; clock averaging, BCLK correction and physical voltage accuracy have not been independently verified. Do not use these dynamic readings to derive UMC timing nanoseconds or compare against saved frequency settings. Nominal UMC MCLK was 1200 MHz in the hardware snapshot. No DDR5 PM Table support yet.

During a subsequent four-worker STREAM run, amdgpu's pp_dpm_mclk selected 1200 MHz (933 MHz before/after the run). The third PM Table capture, bracketed by 1200 MHz DPM readings, still reported FCLK/UCLK 721.104 MHz, MCLK 931.185 MHz, VSOC 0.793271 V and CLDO VDDP 0.8 V. It had the same length and zero-padding fingerprint. This supports load-sensitive telemetry but does not establish the averaging window or prove instantaneous clock semantics. The selected DPM state, UMC ratio and PM Table readings must remain separate. Regression tests retain the selected load words as well.

### Acceptance

Run the decoder tests without hardware:

    dotnet run --project tests/LinuxTimingsSmoke/LinuxTimingsSmoke.csproj -c Release

Hardware acceptance on ThinkCentre M75n, Ryzen 3 PRO 3300U, BIOS M33KT24A, Linux 6.12, ryzen_smu 0.1.2: build baad993 passed both --pm-table and --compare --pm-table --json (exit 0). Both UMCs reported nominal 1200 MHz / DDR4-2400, 19 registers and 32 decoded rows per channel; all running values matched the earlier hardware fixture. Standalone PM Table returned FCLK/UCLK 542.50 MHz, MCLK 835.56 MHz, VSOC 0.7074 V and CLDO VDDP 0.8 V. Combined snapshot returned 913.1367 / 913.1367 / 928.0435 MHz, VSOC 0.80741495 V and CLDO VDDP 0.8 V. This confirms the live CLI acquisition path, not the physical semantics of the telemetry fields. Origin fingerprint matched; manual overrides remain untested.

SMBIOS Type 17 reported two 4 GiB single-rank Micron 4ATF51264HZ-2G6E1 DDR4 SODIMMs, rated 2667 MT/s, configured 2400 MT/s and 1.2 V. No registered SPD EEPROM nodes were present. A user-run active read scan of the three PIIX4 SMBus adapters found no responding addresses, including 0x50–0x57; the DesignWare adapter responded at 0x38 and 0x3f only. Display DDC/AUX adapters were not scanned. This establishes that SPD was not accessible through the tested standard interfaces on this machine, not that SPD is physically absent. Prefer SMBIOS fallback for module inventory; do not automatically probe I2C buses. No raw SMBIOS identifiers or scan dumps are retained here.

BD790i / DDR5 hardware acceptance and independent comparison with Windows ZenTimings at identical BIOS settings remain outstanding. Investigate driver errors before trusting results. Do not change BIOS settings solely for initial acceptance.
