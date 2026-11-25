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

The maps are compiled directly from the checked-out submodule at commit 869b7e82572337e5a673e92230edc92bddf0e44b: [DDR4](https://github.com/irusanov/ZenStates-Core/blob/869b7e82572337e5a673e92230edc92bddf0e44b/Dictionaries/DDR4Dictionary.cs), [DDR5](https://github.com/irusanov/ZenStates-Core/blob/869b7e82572337e5a673e92230edc92bddf0e44b/Dictionaries/DDR5Dictionary.cs), and the refresh/ratio handling derives from Hardware/DRAM/Ddr4Timings.cs, Ddr5Timings.cs, BaseDramTimings.cs and MemoryConfig.cs at that revision. The CLI intentionally omits ambiguous rank-dependent refresh values, rather than selecting a first non-default value. All output clocks are nominal from the UMC ratio assuming 100 MHz BCLK; they are NOT measured frequencies. FCLK/UCLK, PM table, SPD and voltage telemetry remain future work.

Compare mode reports each populated UMC separately, never merges channel readings or treats unknown origins as Auto. The existing BIOS resolver controls whether saved settings are trusted. Running data is independent of UEFI access, and compare mode can return a running snapshot even when origins are unavailable; check origins_matched/origins_status. The comparison uses the existing DiffersFrom method, excludes nominal clocks, and does not assert AGESA application or a required reboot. JSON includes the selected raw UMC registers (not raw UEFI blobs) for reproducible diagnosis.

### Acceptance

Run the decoder tests without hardware:

    dotnet run --project tests/LinuxTimingsSmoke/LinuxTimingsSmoke.csproj -c Release

Then on the M75n and BD790i, obtain a --timings --json snapshot, independently compare with known-good Windows ZenTimings values at identical BIOS settings, check both populated UMCs, and investigate driver errors before trusting results. Do not change BIOS settings just for initial acceptance. No hardware validation was performed while authoring this PR.
