using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace ZenTimings.Cli
{
    internal static class LinuxPowerTable
    {
        private const string Root="/sys/kernel/ryzen_smu_drv";
        internal const uint Version=0x001E0004;
        internal const int PayloadSize=0x608, DriverSize=0x6AC;
        internal const uint DragonRangeVersion=0x540208;
        internal const int DragonRangeSize=0x8D0;
        internal sealed record Sample(string layout, string driver_version, string smu_firmware,
            string table_version, int table_size, float fclk_mhz, float uclk_mhz, float mclk_mhz,
            float vsoc_v, float cldo_vddp_v, string note)
        {
            [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public float? vdd_misc_v { get; init; }
            [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public Svi3Rail[] svi3 { get; init; }
        }
        internal sealed record Svi3Rail(string rail,float? vid_v,float? voltage_v,float? current_a,float? vrm_temperature_c);
        internal sealed record Result(bool available, Sample sample, string status);

        private static bool IsPicasso(RunningTimings.CpuId cpu) =>
            cpu.Vendor=="AuthenticAMD" && cpu.Family==0x17 && cpu.Model==0x18;
        private static bool IsRaphaelClass(RunningTimings.CpuId cpu) =>
            cpu.Vendor=="AuthenticAMD" && cpu.Family==0x19 && cpu.Model==0x61;

        // Picasso is hardware-observed. Dragon Range is source-derived for
        // exactly one version/driver tuple; Linux hardware acceptance pending.
        // No generic version fallback or arbitrary size compatibility.
        internal static void ValidateIdentity(RunningTimings.CpuId cpu, string driver, string codename,
            string mp1, uint version, ulong length)
        {
            if(IsPicasso(cpu) && driver=="0.1.2" && codename=="03" && mp1=="1" &&
                version==Version && length==DriverSize) return;
            // amkillam/ryzen_smu d298366: family 19h model 61h maps to
            // CODENAME_RAPHAEL=20, IF_VERSION_11=2, version 0x540208 size 0x8D0.
            if(IsRaphaelClass(cpu) && driver=="0.1.7" && codename=="20" && mp1=="2" &&
                version==DragonRangeVersion && length==DragonRangeSize) return;
            throw new NotSupportedException($"Unverified PM Table tuple: CPU {cpu.Family:X}:{cpu.Model:X}, driver {driver}, codename {codename}, MP1 {mp1}, version 0x{version:X8}, size {length}.");
        }

        // Offsets: pinned ZenStates-Core Hardware/PowerTable.cs, 0x1E0004.
        // Observed Linux table extends that layout with a zero-filled tail.
        internal static Sample Decode(RunningTimings.CpuId cpu, string driver, string codename,
            string mp1, string firmware, uint version, ulong length, byte[] bytes)
        {
            ValidateIdentity(cpu,driver,codename,mp1,version,length);
            if((ulong)bytes.Length!=length || (IsPicasso(cpu) && bytes.Skip(PayloadSize).Any(b=>b!=0)))
                throw new InvalidDataException("PM Table length or zero-padding fingerprint mismatch.");
            float Raw(int offset)=>BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset,4)));
            float Read(int offset,float low,float high)
            {
                float v=Raw(offset);
                if(!float.IsFinite(v) || v<low || v>high)
                    throw new InvalidDataException($"Implausible PM Table float at 0x{offset:X}.");
                return v;
            }
            if(IsRaphaelClass(cpu))
            {
                // Exact 0x540208 definition from pinned ZenStates-Core.
                // Corroborated against a Windows Dragon Range debug report;
                // the Linux driver returns a different, shorter extent.
                float? Optional(int offset,float low,float high)
                {
                    float v=Raw(offset);
                    return float.IsFinite(v) && v>=low && v<=high?v:null;
                }
                Svi3Rail Rail(string name,int offset)=>new(name,
                    Optional(offset,0,2),Optional(offset+4,0,2),Optional(offset+8,0,500),
                    Optional(offset+0x10,-40,150));
                return new Sample("zen4-540208-linux-0.1.7",driver,firmware,$"0x{version:X8}",bytes.Length,
                    Read(0x11C,0,5000),Read(0x12C,0,5000),Read(0x13C,200,5000),
                    Read(0xD4,0.1f,2),Read(0x434,0,2),
                    "Source-derived Dragon Range layout; Linux hardware validation pending. SVI3 labels inherited from upstream; invalid optional fields omitted. No measured BCLK correction, no configured clock substitution.")
                {
                    vdd_misc_v=Optional(0xE8,0,2),
                    svi3=new[]{Rail("VDD",0xBC),Rail("SOC",0xD0),Rail("MISC",0xE4)}
                };
            }
            return new Sample("picasso-1e0004-linux-0.1.2",driver,firmware,$"0x{version:X8}",bytes.Length,
                Read(0x298,0,5000),Read(0x29C,0,5000),Read(0x2A0,0,5000),
                Read(0x104,0,2),Read(0xF0,0,2),
                "Upstream-mapped SMU telemetry; averaging and physical sensor semantics not independently verified. Not the configured UMC frequency. No BCLK correction or saved/running comparison.");
        }

        private static byte[] ReadFile(string name,int limit,string root=Root)
        {
            using var file=new FileStream(Path.Combine(root,name),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            var buffer=new byte[limit+1];
            int count=0;
            while(count<buffer.Length)
            {
                int n=file.Read(buffer,count,buffer.Length-count);
                if(n==0) break;
                count+=n;
            }
            if(count>limit) throw new InvalidDataException(name+": oversized response.");
            return buffer[..count];
        }

        internal sealed record MetadataProbe(string driver_version,string smu_firmware,string codename,string mp1_if_version,
            string table_version,ulong? table_size,bool table_file_visible,bool? tuple_supported,string status);

        // Cached driver metadata only. Do not open pm_table or smn, and do
        // not interpret path visibility as permission or actual capture success.
        internal static MetadataProbe ProbeMetadata(RunningTimings.CpuId cpu,string driverRoot)
        {
            T Try<T>(Func<T> read,T unavailable)
            {
                try { return read(); }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                { return unavailable; }
            }
            string Text(string name)=>Try(()=>{
                string value=System.Text.Encoding.ASCII.GetString(ReadFile(name,64,driverRoot)).Trim();
                return value.Length>0 && value.All(c=>c>=0x20 && c<=0x7E)?value:null;
            },(string)null);
            var driver=Text("drv_version");
            var firmware=Text("version");
            var codename=Text("codename");
            var mp1=Text("mp1_if_version");
            uint? version=Try<uint?>(()=>{
                byte[] bytes=ReadFile("pm_table_version",4,driverRoot);
                return bytes.Length==4?BinaryPrimitives.ReadUInt32LittleEndian(bytes):null;
            },null);
            ulong? length=Try<ulong?>(()=>{
                byte[] bytes=ReadFile("pm_table_size",8,driverRoot);
                return bytes.Length==8?BinaryPrimitives.ReadUInt64LittleEndian(bytes):null;
            },null);
            bool visible=File.Exists(Path.Combine(driverRoot,"pm_table"));
            bool? supported=null;
            string status="incomplete/unreadable metadata; no PM Table capture attempted";
            if(driver!=null && codename!=null && mp1!=null && version.HasValue && length.HasValue)
            {
                try
                {
                    ValidateIdentity(cpu,driver,codename,mp1,version.Value,length.Value);
                    supported=true;
                    status=visible?"known metadata tuple; capture not tested":"known metadata tuple; PM Table path not visible";
                }
                catch(NotSupportedException)
                {
                    supported=false;
                    status="unsupported metadata tuple; no PM Table capture attempted";
                }
            }
            return new MetadataProbe(driver,firmware,codename,mp1,version.HasValue?$"0x{version.Value:X8}":null,
                length,visible,supported,status);
        }

        internal static Result Capture()
        {
            try
            {
                var cpu=RunningTimings.Identify();
                if(!IsPicasso(cpu) && !IsRaphaelClass(cpu))
                    throw new NotSupportedException("PM Table support limited to Picasso 17h:18h and Raphael-class 19h:61h.");
                string Text(string name)=>System.Text.Encoding.ASCII.GetString(ReadFile(name,64)).Trim();
                var driver=Text("drv_version");
                var codename=Text("codename");
                var mp1=Text("mp1_if_version");
                var firmware=Text("version");
                uint GetVersion()
                {
                    byte[] b=ReadFile("pm_table_version",4);
                    if(b.Length!=4) throw new InvalidDataException("Invalid PM Table version length.");
                    return BinaryPrimitives.ReadUInt32LittleEndian(b);
                }
                ulong GetSize()
                {
                    byte[] b=ReadFile("pm_table_size",8);
                    if(b.Length!=8) throw new InvalidDataException("Invalid x64 PM Table size length.");
                    return BinaryPrimitives.ReadUInt64LittleEndian(b);
                }
                uint version=GetVersion(); ulong length=GetSize();
                ValidateIdentity(cpu,driver,codename,mp1,version,length);
                Console.Error.WriteLine("PM Table read may trigger a driver-managed SMU refresh; stop other SMU clients.");
                using var mutex=new System.Threading.Mutex(false,"ZenTimingsLinuxSmnRead");
                if(!mutex.WaitOne(0)) throw new IOException("Another ZenTimings SMN reader is active.");
                byte[] bytes;
                try
                {
                    bytes=ReadFile("pm_table",checked((int)length));
                    if(GetVersion()!=version || GetSize()!=length)
                        throw new InvalidDataException("PM Table metadata changed during capture.");
                }
                finally { mutex.ReleaseMutex(); }
                return new Result(true,Decode(cpu,driver,codename,mp1,firmware,version,length,bytes),
                    IsPicasso(cpu)?"mapped, experimental":"source-derived, experimental; Linux hardware validation pending");
            }
            catch(Exception ex) { return new Result(false,null,ex.GetType().Name+": "+ex.Message); }
        }
    }
}
