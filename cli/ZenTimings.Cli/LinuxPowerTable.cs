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
        internal sealed record Sample(string layout, string driver_version, string smu_firmware,
            string table_version, int table_size, float fclk_mhz, float uclk_mhz, float mclk_mhz,
            float vsoc_v, float cldo_vddp_v, string note);
        internal sealed record Result(bool available, Sample sample, string status);

        // Exact hardware-observed tuple. No generic version fallback and no
        // assumption that a larger buffer implies a compatible layout.
        internal static void ValidateIdentity(RunningTimings.CpuId cpu, string driver, string codename,
            string mp1, uint version, ulong length)
        {
            if(cpu.Vendor!="AuthenticAMD" || cpu.Family!=0x17 || cpu.Model!=0x18 ||
                driver!="0.1.2" || codename!="03" || mp1!="1" || version!=Version || length!=DriverSize)
                throw new NotSupportedException($"Unverified PM Table tuple: CPU {cpu.Family:X}:{cpu.Model:X}, driver {driver}, codename {codename}, MP1 {mp1}, version 0x{version:X8}, size {length}.");
        }

        // Offsets: pinned ZenStates-Core Hardware/PowerTable.cs, 0x1E0004.
        // Observed Linux table extends that layout with a zero-filled tail.
        internal static Sample Decode(RunningTimings.CpuId cpu, string driver, string codename,
            string mp1, string firmware, uint version, ulong length, byte[] bytes)
        {
            ValidateIdentity(cpu,driver,codename,mp1,version,length);
            if(bytes.Length!=DriverSize || bytes.Skip(PayloadSize).Any(b=>b!=0))
                throw new InvalidDataException("PM Table length or zero-padding fingerprint mismatch.");
            float Read(int offset,float low,float high)
            {
                float v=BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset,4)));
                if(!float.IsFinite(v) || v<low || v>high)
                    throw new InvalidDataException($"Implausible PM Table float at 0x{offset:X}.");
                return v;
            }
            return new Sample("picasso-1e0004-linux-0.1.2",driver,firmware,$"0x{version:X8}",bytes.Length,
                Read(0x298,0,5000),Read(0x29C,0,5000),Read(0x2A0,0,5000),
                Read(0x104,0,2),Read(0xF0,0,2),
                "Upstream-mapped SMU telemetry; averaging and physical sensor semantics not independently verified. Not the configured UMC frequency. No BCLK correction or saved/running comparison.");
        }

        private static byte[] ReadFile(string name,int limit)
        {
            using var file=new FileStream(Path.Combine(Root,name),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
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

        internal static Result Capture()
        {
            try
            {
                var cpu=RunningTimings.Identify();
                if(cpu.Vendor!="AuthenticAMD" || cpu.Family!=0x17 || cpu.Model!=0x18)
                    throw new NotSupportedException("PM Table support limited to Picasso-class CPU 17h:18h.");
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
                    bytes=ReadFile("pm_table",DriverSize);
                    if(GetVersion()!=version || GetSize()!=length)
                        throw new InvalidDataException("PM Table metadata changed during capture.");
                }
                finally { mutex.ReleaseMutex(); }
                return new Result(true,Decode(cpu,driver,codename,mp1,firmware,version,length,bytes),"mapped, experimental");
            }
            catch(Exception ex) { return new Result(false,null,ex.GetType().Name+": "+ex.Message); }
        }
    }
}
