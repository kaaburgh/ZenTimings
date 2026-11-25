# ZenTimings BIOS origin CLI (Linux)

First phase: saved BIOS settings, not current memory-controller readings. Compiles the existing C# BIOS catalog/resolvers and embeds the same resources as WPF; no forked offset tables or ZenStates-Core/PawnIO dependency. The Windows project continues to target .NET Framework.

## Build

Requires .NET 10 SDK. From the repository root (no submodule checkout required):

    dotnet build cli/ZenTimings.Cli/ZenTimings.Cli.csproj -c Release
    dotnet publish cli/ZenTimings.Cli/ZenTimings.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/linux-x64

The Linux CI artifact is a tar.gz containing a self-contained x64 executable (glibc Linux, no installed .NET runtime required; normal system libraries still required). Extract the archive into a dedicated directory. No installation or driver loading is needed.

## Use

    ./zentimings --help
    sudo ./zentimings --origins
    sudo ./zentimings --json
    ./zentimings --replay report.txt --json

Use sudo only if needed for firmware read permissions. The CLI reads DMI identity in /sys/class/dmi/id and EFI variables in /sys/firmware/efi/efivars. It strips the four-byte little-endian attributes prefix before passing payload bytes to the shared resolver. It never writes firmware, clears immutable flags, loads drivers, mounts filesystems, or probes arbitrary variable names. Unknown boards/releases and mismatched fingerprints fail closed. An optional variable that is absent may be skipped, but an I/O error or malformed attribute prefix is not silently treated as absence. EFI runtime services can hide variables regardless of root permissions.

Live matching uses the shared Lenovo system family/machine type fallback, not BIOS version alone. All existing catalog evidence tiers still apply. A matched result means saved settings passed existing catalog/resolver checks; no claim that the hardware applied them. Running/mismatch columns are explicitly unavailable. No live UMC/SMU, frequency, SPD or voltage reads in this release.

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

Next phase (separate PR): Linux hardware backend for running DDR4/DDR5 timings and clocks, evaluating existing ryzen_smu/SMN access and avoiding uncoordinated PCI index/data accesses. No such backend is included here.
