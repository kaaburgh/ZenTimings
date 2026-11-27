using System.IO;

namespace ZenTimings.Cli
{
    internal static class LinuxCapabilities
    {
        internal sealed record Result(RunningTimings.CpuId cpu,bool umc_map_candidate,bool smn_file_visible,
            bool efivarfs_directory_visible,bool dmi_table_visible,LinuxPowerTable.MetadataProbe pm_table,
            LinuxMemoryInventory.BoundDeviceCounts bound_spd_devices,string note);

        internal static Result Capture(string root,RunningTimings.CpuId cpu)
        {
            string driverRoot=Path.Combine(root,"kernel/ryzen_smu_drv");
            return new Result(cpu,RunningTimings.MemoryType(cpu)>=0,
                File.Exists(Path.Combine(driverRoot,"smn")),
                Directory.Exists(Path.Combine(root,"firmware/efi/efivars")),
                File.Exists(Path.Combine(root,"firmware/dmi/tables/DMI")),
                LinuxPowerTable.ProbeMetadata(cpu,driverRoot),LinuxMemoryInventory.ProbeBoundDevices(root),
                "Metadata/paths only. No SMN request, PM Table read, firmware variable, DMI table, SPD EEPROM, hwmon sensor or physical-memory read. Visibility and a known tuple do not imply permission, successful acquisition, or hardware validation.");
        }
    }
}
