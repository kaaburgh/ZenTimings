using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using ZenStates.Core.Dictionaries;

namespace ZenTimings.Cli
{
    internal static class RunningTimings
    {
        internal sealed record CpuId(string Vendor, int Family, int Model);
        internal sealed record Channel(int Index, string MemoryType, double NominalMclkMHz,
            SortedDictionary<string, uint> Timings, SortedDictionary<string, string> Registers,
            string RefreshStatus);
        internal sealed record Snapshot(CpuId Cpu, string Source, Channel[] Channels, string Note);
        // Deliberately limited to the two target silicon groups, not arbitrary Zen models.
        internal static int MemoryType(CpuId cpu) =>
            cpu.Vendor != "AuthenticAMD" ? -1 :
            cpu.Family == 0x17 && (cpu.Model == 0x11 || cpu.Model == 0x18) ? 0 :
            cpu.Family == 0x19 && cpu.Model == 0x61 ? 1 : -1;

        internal static CpuId Identify()
        {
            if (!X86Base.IsSupported) throw new NotSupportedException("x86 CPUID required.");
            var v = X86Base.CpuId(0,0);
            var bytes = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0),v.Ebx);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),v.Edx);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8),v.Ecx);
            int eax = X86Base.CpuId(1,0).Eax;
            int family = (eax >> 8) & 15, model = (eax >> 4) & 15;
            if (family == 15) family += (eax >> 20) & 255;
            if (((eax >> 8) & 15) is 6 or 15) model |= ((eax >> 16) & 15) << 4;
            return new CpuId(System.Text.Encoding.ASCII.GetString(bytes), family, model);
        }

        private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) {
            "CL","RCDRD","RCDWR","RP","RAS","RC","RTP","RRDL","RRDS","FAW","WTRL","WTRS",
            "CWL","WR","RDRDSCL","RDRDSC","RDRDSD","RDRDDD","WRWRSCL","WRWRSC","WRWRSD","WRWRDD",
            "RDWR","WRRD","REFI","CKE","GDM","Cmd2T","PowerDown"
        };
        private static uint Bits(uint value,int high,int low) =>
            (value >> low) & (uint)((1UL << (high-low+1))-1);
        private static string Row(string name) => name == "PowerDown" ? "PwrDwn" :
            name is "Cmd2T" or "GDM" ? name : "t"+name;

        // Address list is built from a restricted subset of the pinned upstream maps.
        // Only two UMCs on these specific client CPU groups. No arbitrary address input.
        internal static Snapshot Capture(CpuId cpu, Func<uint,uint> read, string source)
        {
            int type = MemoryType(cpu);
            if (type < 0) throw new NotSupportedException("Unsupported CPUID; no SMN requests issued.");
            var defs = type == 0 ? DDR4Dictionary.defs : DDR5Dictionary.defs;
            var registers = defs.Where(p => p.Value.Any(f => Fields.Contains(f.Name)))
                .Select(p => p.Key).Concat(new uint[]{0x50100,0x50200,0x50260,0x50264})
                .Concat(type == 1 ? new uint[]{0x50268,0x5026C,0x502C0,0x502C4,0x502C8,0x502CC} : Array.Empty<uint>())
                .Distinct().OrderBy(x=>x).ToArray();
            var channels = new List<Channel>();
            for (int i=0;i<2;i++)
            {
                uint offset = (uint)i << 20;
                uint Read(uint reg)
                {
                    uint v=read(offset|reg);
                    if(v==uint.MaxValue) throw new IOException($"SMN all-ones response at 0x{offset|reg:X8}.");
                    return v;
                }
                // Probe presence before reading timing registers. Mirror the pinned
                // MemoryConfig.ReadChannelsNoLock channel/DIMM test.
                uint state=Read(0x50DF0), cs0=Read(0x50000), cs2=Read(0x50008);
                if ((state & (1u<<19))!=0 || ((cs0|cs2)&1)==0) continue;
                var raw = registers.ToDictionary(a=>a,a=>Read(a));
                foreach(uint a in registers.Reverse())
                    if (Read(a)!=raw[a]) throw new IOException($"Unstable SMN snapshot at UMC {i}, 0x{a:X}.");
                if(Read(0x50DF0)!=state || Read(0x50000)!=cs0 || Read(0x50008)!=cs2)
                    throw new IOException("UMC presence changed during read.");
                if ((raw[0x50100]&3)!=type)
                    throw new InvalidDataException("UMC DRAM type does not agree with supported CPU/map.");
                var timings=new SortedDictionary<string,uint>(StringComparer.Ordinal);
                foreach(var p in defs)
                    foreach(var f in p.Value.Where(f=>Fields.Contains(f.Name)))
                        timings.Add(Row(f.Name),Bits(raw[p.Key],f.HiBit,f.LoBit));
                if (timings["tCL"] < 5 || timings["tRCDRD"] < 5 || timings["tRP"] < 5 || timings["tRAS"] < 10)
                    throw new InvalidDataException("Implausible primary timings; no result published.");
                // Same UMC ratio formula as pinned Ddr4Timings/Ddr5Timings.
                // 100 MHz is assumed BCLK, not measured MCLK.
                double mclk = type==0 ? Bits(raw[0x50200],6,0)*100.0/3 :
                    Bits(raw[0x50200],15,0);
                if(mclk<200 || mclk>10000) throw new InvalidDataException("Implausible memory ratio.");
                // If several programmed refresh values differ, do not guess which rank
                // is effective. Exclude upstream's unused/default patterns.
                uint sentinel=type==0 ? 0x21060138u : 0x00C00138u;
                var refresh=(type==0 ? new uint[]{0x50260,0x50264} : new uint[]{0x50260,0x50264,0x50268,0x5026C})
                    .Select(a=>raw[a]).Where(v=>v!=0 && v!=sentinel).Distinct().ToArray();
                string refreshStatus="unavailable or ambiguous";
                if(refresh.Length==1)
                {
                    uint v=refresh[0];
                    timings["tRFC"]=Bits(v,type==0?10:15,0);
                    timings["tRFC2"]=Bits(v,type==0?21:31,type==0?11:16);
                    if(type==0) timings["tRFC4"]=Bits(v,31,22);
                    refreshStatus="one distinct non-default refresh word";
                }
                if(type==1)
                {
                    var sb=new uint[]{0x502C0,0x502C4,0x502C8,0x502CC}
                        .Select(a=>Bits(raw[a],10,0)).Where(v=>v!=0).Distinct().ToArray();
                    if(sb.Length==1) timings["tRFCsb"]=sb[0];
                }
                raw.Add(0x50DF0,state);raw.Add(0x50000,cs0);raw.Add(0x50008,cs2);
                channels.Add(new Channel(i,type==0?"DDR4":"DDR5",mclk,timings,
                    new SortedDictionary<string,string>(raw.ToDictionary(p=>$"0x{offset|p.Key:X8}",p=>$"0x{p.Value:X8}")),
                    refreshStatus));
            }
            if(channels.Count==0) throw new IOException("No populated UMC found.");
            return new Snapshot(cpu,source,channels.ToArray(),
                "Experimental UMC snapshot; clocks are nominal (100 MHz BCLK), FCLK/UCLK not measured. Concurrent driver clients can invalidate reads.");
        }
    }
}
