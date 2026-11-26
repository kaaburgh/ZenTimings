using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace ZenTimings.Cli
{
    // AMD BIOS directory metadata, not an authenticated chain of trust.
    // File-only observations; never use advertised addresses to access live memory.
    internal static class ApobBiosDirectory
    {
        internal sealed record Entry(string directory_offset,string type,uint size,int address_mode,
            string source_address,string destination_address,string image_offset);
        internal sealed record Directory(string offset,string cookie,uint entry_count,string checksum,Entry[] apob_entries);
        internal sealed record Region(string image_offset,uint size,string[] directory_offsets);
        internal sealed record Result(string status,Directory[] directories,Region[] nv_regions,
            string[] advertised_ram_addresses,string[] unresolved_nv_entries,bool? matched_nv_region,string note);
        private static uint U32(ReadOnlySpan<byte> b)=>BinaryPrimitives.ReadUInt32LittleEndian(b);
        private static ulong U64(ReadOnlySpan<byte> b)=>BinaryPrimitives.ReadUInt64LittleEndian(b);
        private static string Hex(ulong v)=>$"0x{v:X}";
        internal static uint Fletcher32(ReadOnlySpan<byte> b)
        {
            uint a=0xffff,z=0xffff;
            for(int i=0;i<b.Length;i+=2)
            {
                a=(a+BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(i,2)))%65535;
                z=(z+a)%65535;
            }
            return ((z==0?65535:z)<<16)|(a==0?65535:a);
        }
        internal static Result Inspect(byte[] image,int? apobOffset=null,uint? apobSize=null)
        {
            var directories=new List<Directory>();
            var regions=new List<(int offset,uint size,string directory)>();
            var ram=new SortedSet<ulong>();
            var unresolved=new List<string>();
            int examined=0;
            bool capped=false;
            // BIOS directory tables normally start at aligned flash offsets, but
            // scan for both cookies and validate the complete bounded checksum.
            foreach(string cookie in new[]{"$BHD","$BL2"})
            {
                byte[] signature=System.Text.Encoding.ASCII.GetBytes(cookie);
                int cursor=0;
                while(cursor<=image.Length-16)
                {
                    int found=image.AsSpan(cursor).IndexOf(signature);
                    if(found<0) break;
                    int p=cursor+found; cursor=p+4;
                    if(++examined>4096) { capped=true; break; }
                    if(p>image.Length-16) continue;
                    uint checksum=U32(image.AsSpan(p+4)),n=U32(image.AsSpan(p+8));
                    if(n==0 || n>256 || 16L+24L*n>image.Length-p) continue;
                    if(Fletcher32(image.AsSpan(p+8,8+24*(int)n))!=checksum) continue;
                    var entries=new List<Entry>();
                    for(int i=0;i<n;i++)
                    {
                        var e=image.AsSpan(p+16+24*i,24);
                        uint type=U32(e),size=U32(e.Slice(4));
                        if((type&255)!=0x61 && (type&255)!=0x63) continue;
                        ulong src=U64(e.Slice(8)),dst=U64(e.Slice(16));
                        int mode=(int)(src>>62);
                        ulong addr=src&0x3fffffffffffffffUL;
                        int? offset=null;
                        if((type&255)==0x63)
                        {
                            ulong? candidate=null;
                            if(mode==1) candidate=addr;
                            // Physical mapping only for standard full-image sizes.
                            else if(mode==0 && image.Length is 8388608 or 16777216 or 33554432 or 67108864)
                            {
                                ulong baseAddress=0x100000000UL-(ulong)image.Length;
                                if(addr>=baseAddress && addr<0x100000000UL) candidate=addr-baseAddress;
                            }
                            if(candidate.HasValue && size>0 && candidate.Value<(ulong)image.Length &&
                               size<=(ulong)image.Length-candidate.Value)
                            {
                                offset=(int)candidate.Value;
                                regions.Add((offset.Value,size,Hex((ulong)p)));
                            }
                            else unresolved.Add($"{Hex((ulong)p)} entry {i}: mode {mode}, source {Hex(src)}, size {size}");
                        }
                        else if(dst!=0 && dst!=ulong.MaxValue) ram.Add(dst);
                        entries.Add(new Entry(Hex((ulong)p),Hex(type),size,mode,Hex(src),Hex(dst),
                            offset.HasValue?Hex((ulong)offset.Value):null));
                    }
                    if(entries.Count>0)
                        directories.Add(new Directory(Hex((ulong)p),cookie,n,Hex(checksum),entries.ToArray()));
                }
                if(capped) break;
            }
            var distinct=regions.GroupBy(x=>(x.offset,x.size)).OrderBy(x=>x.Key.offset).ThenBy(x=>x.Key.size)
                .Select(g=>new Region(Hex((ulong)g.Key.offset),g.Key.size,g.Select(x=>x.directory).Distinct().ToArray())).ToArray();
            bool? matched=apobOffset.HasValue && apobSize.HasValue && regions.Count>0
                ? regions.Any(r=>r.offset==apobOffset.Value && apobSize.Value<=r.size):null;
            return new Result(capped?"partial directory scan (candidate limit)":directories.Count==0
                ?"no checksummed APOB directory metadata":"checksummed APOB directory metadata",
                directories.ToArray(),distinct,ram.Select(Hex).ToArray(),unresolved.ToArray(),matched,
                "Checksum verifies structure, not authenticity or active directory selection. Type 0x61 advertises RAM destination, 0x63 a nonvolatile region. Exact-start match only. Modes 2/3 and unsupported physical mappings are unresolved. Does not establish a valid APOB or authorize live memory access.");
        }
    }
}
