using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ZenStates.Core.Hardware.Apob;
using ZenStates.Core.Common;
using ZenTimings.Common;

namespace ZenTimings.Cli
{
    internal static class LinuxApob
    {
        private const int MaxBytes=4*1024*1024, MaxHeader=4096;
        private static readonly long[] Candidates={0xA200000,0x9F00000,0x4000000};
        internal sealed record Entry(uint offset,uint group,uint type,uint instance,uint size,bool encrypted,string name);
        internal sealed record Value(int? code,string display);
        internal sealed record Electrical(uint instance,int channel,ushort memclk_mhz,ushort ddr_max_rate,
            SortedDictionary<string,Value> settings);
        internal sealed record SpdSlot(uint instance,int socket,int channel,int dimm,LinuxMemoryInventory.Spd data);
        internal sealed record Result(bool available,string status,string source,string address,
            uint? version,uint? table_size,uint? header_size,Entry[] entries,Electrical[] electrical,
            SpdSlot[] spd,string[] diagnostics,string note)
        {
            public string image_offset { get; init; }
            public ApobBiosDirectory.Result bios_directory { get; init; }
            public ApobReplay.Result replay { get; init; }
            public ApobLegacyConfig.Result gen_config { get; init; }
            public ApobDdr5Config.Result ddr5_config { get; init; }
            public ApobTrainingStructure.Result ddr5_training_structure { get; init; }
            public ApobBootInfo.Result ddr5_boot_info { get; init; }
            public ApobHealthSummary.Result health_summary { get; init; }
        }
        private const string Note="APOB is firmware boot/training data, not a live sensor. Layouts inherited from pinned ZenStates-Core; HMAC not verified. No raw bytes or serial numbers exported. No automatic UMC association.";
        private static uint U32(byte[] b,int o)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o,4));

        private static (uint version,int size,int header) Header(byte[] b)
        {
            if(b.Length<16 || U32(b,0)!=0x424F5041) throw new InvalidDataException("APOB signature absent.");
            uint v=U32(b,4), size=U32(b,8), h=U32(b,12);
            if(h<16 || h>MaxHeader || (h&3)!=0 || size<h+48 || size>MaxBytes)
                throw new InvalidDataException("Invalid or unsupported APOB bounds.");
            return (v,(int)size,(int)h);
        }

        internal static Result Parse(byte[] b,string source,string address=null,bool decodeDdr4=true)
        {
            var h=Header(b);
            if(b.Length!=h.size) throw new InvalidDataException("APOB length differs from header.");
            // Upstream header offset list, excluding the final 32-byte hash.
            var starts=new List<uint>();
            for(int i=0x30;i+4<=h.header-32;i+=4)
            {
                uint o=U32(b,i);
                if(o>=h.header && (ulong)o+48<=(ulong)b.Length) starts.Add(o);
            }
            var raw=ApobEntry.Enumerate(b,(uint)h.header,(uint)h.size,starts);
            // Refuse ambiguous/overlapping chains and malformed short entries.
            ulong previousEnd=(uint)h.header;
            foreach(var e in raw)
            {
                if(e.Size<48 || e.Offset<previousEnd || (ulong)e.Offset+e.Size>(ulong)b.Length)
                    throw new InvalidDataException("Overlapping or invalid APOB entry.");
                previousEnd=(ulong)e.Offset+e.Size;
            }
            if(raw.Count==0) throw new InvalidDataException("No structurally valid APOB entries.");
            // M75n M33KT24A AFU dump: APOB v0x13, header 0xC0. MEM 1/17 is
            // a 120-byte non-SPD entry; MEM 1/18 is the DDR4 SPD array. Do
            // not interpret newer MBIST (1/18) records as SPD.
            bool legacyDdr4=decodeDdr4 && h.version==0x13 && h.header==0xC0;
            bool IsLegacySpd(ApobEntry e) => legacyDdr4 && e.RawGroupId==1 && e.RawDataTypeId==18 &&
                e.Size==2180 && b[e.Offset+48]==1 && b[e.Offset+49]==2 &&
                raw.Count(x=>x.RawGroupId==1 && x.RawDataTypeId==17 && x.InstanceId==e.InstanceId && x.Size==120)==1 &&
                raw.Count(x=>x.RawGroupId==1 && x.RawDataTypeId==2 && x.InstanceId==e.InstanceId && x.Size==256)==1;
            var entries=raw.Select(e=>new Entry(e.Offset,e.GroupId,e.DataTypeId,e.InstanceId,e.Size,e.IsEncrypted,
                IsLegacySpd(e)?"DIMM SPD data (legacy v0x13 candidate)":
                legacyDdr4 && e.RawGroupId==1 && e.RawDataTypeId==17 && e.Size==120?"Legacy non-SPD record (unknown)":e.Name)).ToArray();
            var electrical=new List<Electrical>(); var spd=new List<SpdSlot>(); var diagnostics=new List<string>(); bool validatedLegacySpd=false;
            foreach(var e in raw.Where(e=>e.GroupId==1 && (e.DataTypeId==2 || e.DataTypeId==17 || IsLegacySpd(e))))
            {
                string label=$"MEM {e.DataTypeId} instance {e.InstanceId} at 0x{e.Offset:X}";
                if(e.IsEncrypted || e.RawGroupId!=e.GroupId || e.RawDataTypeId!=e.DataTypeId)
                { diagnostics.Add(label+": opaque entry skipped"); continue; }
                if(raw.Count(x=>x.GroupId==e.GroupId && x.DataTypeId==e.DataTypeId && x.InstanceId==e.InstanceId)>1)
                { diagnostics.Add(label+": ambiguous duplicate instance skipped"); continue; }
                if(e.DataTypeId==2 && decodeDdr4)
                {
                    var c=ApobMemGeneralConfig.Decode(b,e.Offset);
                    if(c==null) { diagnostics.Add(label+": unsupported DDR4 config size"); continue; }
                    if(c.MemClkFreq<200 || c.MemClkFreq>3200)
                    { diagnostics.Add(label+": implausible DDR4 MEMCLK"); continue; }
                    foreach(var ch in c.Channels.Where(x=>x.IsPopulated))
                    {
                        Value V(EncodedValueBase x)=>new(x.RawValue,x.ToString());
                        var settings=new SortedDictionary<string,Value>(StringComparer.Ordinal) {
                            ["ProcODT"]=V(ch.ProcOdt),["RttNom"]=V(ch.RttNom),["RttWr"]=V(ch.RttWr),
                            ["RttPark"]=V(ch.RttPark),["AddrCmdSetup"]=V(ch.AddrCmdSetup),
                            ["CsOdtSetup"]=V(ch.CsOdtSetup),["CkeSetup"]=V(ch.CkeSetup),
                            ["ClkDrvStren"]=V(ch.ClkDrvStren),["AddrCmdDrvStren"]=V(ch.AddrCmdDrvStren),
                            ["CsOdtCmdDrvStren"]=V(ch.CsOdtCmdDrvStren),["CkeDrvStren"]=V(ch.CkeDrvStren)
                        };
                        electrical.Add(new Electrical(e.InstanceId,ch.Channel,c.MemClkFreq,c.DdrMaxRate,settings));
                    }
                }
                if(e.DataTypeId==17 || IsLegacySpd(e))
                {
                    bool legacy=IsLegacySpd(e);
                    if(legacyDdr4 && e.DataTypeId==17 && e.Size==120)
                    { diagnostics.Add(label+": legacy non-SPD record skipped"); continue; }
                    var slots=ApobDimmSpdParser.Decode(b,e.Offset);
                    if(legacy)
                    {
                        // Exact captured two-channel topology, both CRCs valid.
                        // Reuse the upstream record decoder, not an SPD byte scan.
                        bool valid=slots.Count==2 && slots.Select(x=>x.Channel).OrderBy(x=>x).SequenceEqual(new[]{0,1}) &&
                            slots.All(x=>x.Socket==0 && x.Dimm==0 && x.DeviceType==0x0C && x.Data.Length==512 &&
                                LinuxMemoryInventory.ParseSpd("candidate","ee1004",x.Data).base_crc_valid==true &&
                                LinuxMemoryInventory.ParseSpd("candidate","ee1004",x.Data).module_crc_valid==true);
                        if(!valid) { diagnostics.Add(label+": legacy SPD topology/CRC mismatch"); continue; }
                        if(spd.Count>0) { diagnostics.Add(label+": another SPD source already decoded; legacy candidate skipped"); continue; }
                        diagnostics.Add(label+": legacy Picasso v0x13 DDR4 SPD array");
                        validatedLegacySpd=e.InstanceId==0;
                    }
                    foreach(var slot in slots)
                    {
                        if(slot.DeviceType!=0x0C && slot.DeviceType!=0x12)
                        { diagnostics.Add(label+": unsupported SPD type"); continue; }
                        string name=$"socket {slot.Socket} channel {slot.Channel} DIMM {slot.Dimm}";
                        var d=LinuxMemoryInventory.ParseSpd(name,slot.DeviceType==0x0C?"ee1004":"spd5118",slot.Data)
                            with { driver="APOB (boot copy)" };
                        spd.Add(new SpdSlot(e.InstanceId,slot.Socket,slot.Channel,slot.Dimm,d));
                    }
                    if(slots.Count==0) diagnostics.Add(label+": no decodable present SPD records");
                }
            }
            var ddr5Config=ApobDdr5Config.Decode(b,raw,h.version,h.header,spd);
            bool variant=ddr5Config==null;
            if(variant) ddr5Config=ApobDdr5Config.Decode(b,raw,h.version,h.header,spd,variant:true);
            var ddr5Replay=ApobReplay.DecodeDdr5(b,raw,ddr5Config);
            var correlation=ApobDdr5Config.Correlate(ddr5Config,ddr5Replay);
            // A non-donor layout is provisional until the saved GEN state and
            // both independently decoded replay address blocks agree.
            if(variant && correlation==null) { ddr5Config=null; ddr5Replay=null; }
            if(ddr5Config!=null)
            {
                ddr5Config=ddr5Config with { replay_correlation=correlation };
                // MEM 2 remains undecoded; distinguish that from unsupported
                // DDR4 once a separate DDR5 layout has been established.
                for(int i=0;i<diagnostics.Count;i++)
                    if(diagnostics[i].StartsWith("MEM 2 ",StringComparison.Ordinal) &&
                       diagnostics[i].EndsWith(": unsupported DDR4 config size",StringComparison.Ordinal))
                        diagnostics[i]=diagnostics[i].Replace("unsupported DDR4 config size","DDR5 layout established separately; MEM 2 not decoded");
            }
            return new Result(true,"structurally parsed, experimental",source,address,h.version,(uint)h.size,(uint)h.header,
                entries,electrical.ToArray(),spd.ToArray(),diagnostics.ToArray(),Note) {
                    replay=ApobReplay.Decode(b,raw,h.version,h.header,validatedLegacySpd) ?? ddr5Replay,
                    ddr5_config=ddr5Config,
                    ddr5_training_structure=ApobTrainingStructure.Decode(b,raw,ddr5Config),
                    ddr5_boot_info=ApobBootInfo.Decode(b,raw,ddr5Config),
                    health_summary=ApobHealthSummary.Decode(b,raw,h.version,h.header,validatedLegacySpd,spd),
                    gen_config=ApobLegacyConfig.Decode(b,raw,h.version,h.header,validatedLegacySpd,electrical,spd)
                };
        }

        internal static Result ReadFile(string path)
        {
            try
            {
                using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                byte[] header=new byte[16]; f.ReadExactly(header);
                var h=Header(header);
                if(f.Length!=h.size) throw new InvalidDataException("APOB file length differs from header.");
                f.Position=0; byte[] b=new byte[h.size]; f.ReadExactly(b);
                return Parse(b,"offline file (DDR4 config layout inferred from entry size)");
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            { return Failure("offline file",new[]{ex.GetType().Name+": "+ex.Message}); }
        }

        // File-only SPI image search; no hardware or flash device access.
        // A signature alone is not enough: require a complete contiguous entry
        // chain and decodable electrical settings or a CRC-valid SPD record.
        internal static Result ReadImage(string path)
        {
            const string source="offline SPI image (persisted firmware boot data)";
            const int maxImage=64*1024*1024;
            try
            {
                using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                long length=f.Length;
                if(length<16 || length>maxImage) throw new InvalidDataException("SPI image length outside 16 bytes..64 MiB.");
                byte[] image=new byte[(int)length]; f.ReadExactly(image);
                if(f.ReadByte()!=-1) throw new InvalidDataException("SPI image grew during read.");
                var matches=new List<Result>(); int cursor=0, signatures=0;
                while(cursor<=image.Length-16)
                {
                    int found=image.AsSpan(cursor).IndexOf("APOB"u8);
                    if(found<0) break;
                    int offset=cursor+found;
                    cursor=offset+4;
                    if(++signatures>4096) throw new InvalidDataException("Too many APOB signatures; image search aborted.");
                    if(offset>image.Length-16) break;
                    try
                    {
                        var h=Header(image.AsSpan(offset,16).ToArray());
                        if(h.size>image.Length-offset) continue;
                        byte[] b=image.AsSpan(offset,h.size).ToArray();
                        // Strict chain walk, unlike recovery from a standalone
                        // table: never interpret arbitrary embedded signatures.
                        int p=h.header, n=0;
                        while(p<b.Length)
                        {
                            if(b.Length-p<48 || ++n>4096) break;
                            uint g=U32(b,p)&0xFFFF,t=U32(b,p+4)&0xFFFF,size=U32(b,p+12);
                            if(g<1 || g>9 || t==0 || size<48 || size>b.Length-p) break;
                            p+=(int)size;
                        }
                        if(p!=b.Length || n<2) continue;
                        var parsed=Parse(b,source);
                        if(parsed.entries.Length!=n || !(parsed.electrical.Any(c=>c.settings["ProcODT"].display!="N/A" &&
                            c.settings["ProcODT"].code>0) || parsed.spd.Any(x=>x.data.base_crc_valid==true))) continue;
                        matches.Add(parsed with { image_offset=$"0x{offset:X8}" });
                        if(matches.Count>1)
                            return Failure(source,new[]{"Multiple validated APOB tables; extract the intended complete table and use --apob-file."}) with { bios_directory=ApobBiosDirectory.Inspect(image) };
                    }
                    catch(Exception ex) when(ex is InvalidDataException || ex is ArgumentException || ex is OverflowException)
                    { /* Reject malformed signature candidates, continue bounded search. */ }
                }
                if(matches.Count==0)
                    return Failure(source,new[]{$"No complete supported APOB table with decoded memory evidence; {signatures} signature candidates examined."}) with { bios_directory=ApobBiosDirectory.Inspect(image) };
                var result=matches[0];
                return result with { diagnostics=result.diagnostics.Append($"Unique validated table; {signatures} signature candidates examined.").ToArray(),
                    bios_directory=ApobBiosDirectory.Inspect(image,int.Parse(result.image_offset.Substring(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture),result.table_size) };
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is NotSupportedException)
            { return Failure(source,new[]{ex.GetType().Name+": "+ex.Message}); }
        }

        internal sealed record Range(long start,long end,string name);
        internal static Range[] ParseMap(IEnumerable<string> lines)
        {
            var ranges=new List<Range>();
            foreach(string line in lines)
            {
                var m=Regex.Match(line,@"^\s*([0-9a-fA-F]+)-([0-9a-fA-F]+)\s*:\s*(.+?)\s*$");
                if(!m.Success) continue;
                if(!long.TryParse(m.Groups[1].Value,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out long a) ||
                   !long.TryParse(m.Groups[2].Value,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out long z) ||
                   a<0 || z<a) continue;
                ranges.Add(new Range(a,z,m.Groups[3].Value));
            }
            return ranges.ToArray();
        }
        // Strict conservative policy: entirely within an explicitly reserved/NVS
        // interval and no overlap with any other resource (including System RAM,
        // PCI BARs, MMIO, or child resources). Never scan System RAM.
        internal static bool Allowed(Range[] map,long address,int count)
        {
            if(count<=0 || address<0 || address>long.MaxValue-count) return false;
            long end=address+count-1;
            var overlaps=map.Where(r=>r.start<=end && r.end>=address).ToArray();
            return overlaps.Length>0 && overlaps.Any(r=>r.start<=address && r.end>=end) &&
                overlaps.All(r=>r.name.Equals("reserved",StringComparison.OrdinalIgnoreCase) ||
                               r.name.Equals("ACPI Non-volatile Storage",StringComparison.OrdinalIgnoreCase));
        }

        internal static Result Capture()
        {
            var diagnostics=new List<string>();
            try
            {
                var cpu=RunningTimings.Identify();
                int type=RunningTimings.MemoryType(cpu);
                if(type<0) return Failure("live",new[]{"unsupported CPU; no physical-memory access"});
                var map=ParseMap(File.ReadLines("/proc/iomem"));
                foreach(long address in Candidates)
                {
                    string label=$"0x{address:X8}";
                    if(!Allowed(map,address,16)) { diagnostics.Add(label+": not in an exclusively reserved/NVS range; skipped"); continue; }
                    try
                    {
                        // Honor kernel restrictions. No mmap, writes, driver loading,
                        // boot parameter changes, or fallback to arbitrary memory.
                        using var f=File.OpenHandle("/dev/mem",FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                        byte[] Read(int length)
                        {
                            if(!Allowed(map,address,length)) throw new InvalidDataException("APOB crosses reserved range boundary.");
                            var data=new byte[length];int done=0;
                            while(done<length)
                            {
                                int n=RandomAccess.Read(f,data.AsSpan(done),address+done);
                                if(n==0) throw new EndOfStreamException("Short physical read.");
                                done+=n;
                            }
                            return data;
                        }
                        var h=Header(Read(16));
                        byte[] b=Read(h.size);
                        var confirm=Read(16);
                        if(!b.AsSpan(0,16).SequenceEqual(confirm))
                            throw new InvalidDataException("APOB header changed during read.");
                        var result=Parse(b,"live reserved /dev/mem",label,type==0);
                        return result with { diagnostics=diagnostics.Concat(result.diagnostics).ToArray() };
                    }
                    catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is NotSupportedException)
                    { diagnostics.Add(label+": "+ex.GetType().Name+": "+ex.Message); }
                }
            }
            catch(Exception ex) { diagnostics.Add(ex.GetType().Name+": "+ex.Message); }
            return Failure("live",diagnostics.ToArray());
        }

        // Explicit comparison mode, narrower than generic --apob. Never use
        // an input file to select an arbitrary physical address.
        internal static byte[] CaptureDrfxiComparison()
        {
            if(!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Live comparison requires Linux.");
            var cpu=RunningTimings.Identify();
            var firmware=new LinuxFirmware("/sys");
            if(RunningTimings.MemoryType(cpu)!=1 ||
               firmware.Board==null || firmware.Board.IndexOf("DRFXI",StringComparison.OrdinalIgnoreCase)<0)
                throw new InvalidDataException("Live comparison requires recognized DRFXI and supported DDR5 CPU.");
            const long address=DrfxiApobComparison.RamAddress;
            var map=ParseMap(File.ReadLines("/proc/iomem"));
            if(!Allowed(map,address,16))
                throw new InvalidDataException("APOB address not exclusively reserved/NVS; no physical read attempted.");
            using var f=File.OpenHandle("/dev/mem",FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            byte[] Read(int count)
            {
                if(!Allowed(map,address,count))
                    throw new InvalidDataException("APOB crosses reserved range boundary.");
                byte[] data=new byte[count];
                int done=0;
                while(done<count)
                {
                    int n=RandomAccess.Read(f,data.AsSpan(done),address+done);
                    if(n==0) throw new EndOfStreamException("Short physical read.");
                    done+=n;
                }
                return data;
            }
            return StableDrfxiComparison(Read);
        }

        internal static byte[] StableDrfxiComparison(Func<int,byte[]> read)
        {
            var header=read(16);
            int n=DrfxiApobComparison.GetTableSize(header);
            var first=read(n);
            var second=read(n);
            if(first.Length!=n || second.Length!=n || !first.AsSpan(0,16).SequenceEqual(header) ||
                !first.AsSpan().SequenceEqual(second))
                throw new InvalidDataException("APOB changed or was incomplete during two reads.");
            return first;
        }

        private static Result Failure(string source,string[] diagnostics)=>
            new(false,"APOB unavailable",source,null,null,null,null,Array.Empty<Entry>(),Array.Empty<Electrical>(),
                Array.Empty<SpdSlot>(),diagnostics,Note);
    }
}
