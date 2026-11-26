using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using ZenStates.Core.Hardware.Apob;

namespace ZenTimings.Cli
{
    // Empirical legacy Picasso layout, validated against M33KT24A persisted
    // APOB and independent live UMC reports. Not a general S3 interpreter.
    internal static class ApobReplay
    {
        internal sealed record Observation(int payload_offset,string word);
        internal sealed record Register(string last_word,Observation[] history);
        internal sealed record Channel(int umc_address_index,double? nominal_mclk_mhz,
            SortedDictionary<string,Register> observed_registers,string[] missing_registers,
            SortedDictionary<string,uint> candidate_timings,
            SortedDictionary<string,uint> candidate_extended_fields,
            SortedDictionary<string,SortedDictionary<string,uint>> refresh_word_candidates);
        internal sealed record Companion(uint type,int count,int end_exclusive,uint initial_marker_id);
        internal sealed record Result(string status,uint entry_offset,int record_count,int start_offset,
            int end_exclusive,int opaque_trailing_bytes,SortedDictionary<string,int> marker_counts,
            Companion[] companions,Channel[] channels,string note);
        private static uint U32(ReadOnlySpan<byte> p,int i)=>BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(i,4));
        private static ReadOnlySpan<byte> Payload(byte[] b,ApobEntry e)=>b.AsSpan((int)e.DataOffset,(int)e.DataSize);
        private static string Hex(uint v)=>$"0x{v:X8}";
        private static bool Bounds(ReadOnlySpan<byte> p,out int count,out int end)
        {
            count=0;end=0;
            if(p.Length<32) return false;
            uint n=U32(p,16);
            if(n==0 || n>(p.Length-32)/8) return false;
            count=(int)n;end=32+count*8;
            return true;
        }
        internal static Result Decode(byte[] b,List<ApobEntry> entries,uint version,int header,bool validatedLegacySpd)
        {
            if(!validatedLegacySpd || version!=0x13 || header!=0xC0) return null;
            // Require the unique observed structural family, including the
            // five independently bounded companion records. Never scan arbitrary bytes.
            ApobEntry Unique(uint g,uint t,uint size)
            {
                var a=entries.Where(e=>e.GroupId==g && e.DataTypeId==t && e.InstanceId==0).ToArray();
                return a.Length==1 && a[0].RawGroupId==g && a[0].RawDataTypeId==t && a[0].Size==size?a[0]:null;
            }
            var replay=Unique(7,4,13904);
            if(replay==null || Unique(7,3,5356)==null) return null;
            var p=Payload(b,replay);
            if(!Bounds(p,out int n,out int end) || n<6 ||
               U32(p,32)!=0x1D040 || U32(p,40)!=0x50100 || U32(p,48)!=0x150100 ||
               U32(p,56)!=0xFFFFDEAD || U32(p,60)!=0x16 ||
               U32(p,64)!=0xFFFFDEAD || U32(p,68)!=0x14) return null;
            var companions=new List<Companion>();
            for(uint t=10;t<=14;t++)
            {
                var e=Unique(1,t,2768);
                if(e==null) return null;
                var q=Payload(b,e);
                if(!Bounds(q,out int count,out int stop) || count<3 ||
                   !q.Slice(0,8).SequenceEqual(p.Slice(0,8)) ||
                   !q.Slice(12,4).SequenceEqual(p.Slice(12,4)) ||
                   !q.Slice(20,12).SequenceEqual(p.Slice(20,12)) ||
                   U32(q,32)!=0xFFFFDEAD || U32(q,36)!=7+2*(t-10) ||
                   U32(q,stop-16)!=0xFFFFDEAD || U32(q,stop-12)!=0x20 ||
                   U32(q,stop-8)!=0xFFFFDEAD || U32(q,stop-4)!=0x17) return null;
                for(int i=stop;i<q.Length;i++) if(q[i]!=0) return null;
                companions.Add(new Companion(t,count,stop,U32(q,36)));
            }
            var known=RunningTimings.TimingAddresses(0).Concat(ExtendedUmc.Addresses(0))
                .Concat(new uint[]{0x50000,0x50008,0x50DF0}).Distinct().OrderBy(x=>x).ToArray();
            var all=new HashSet<uint>(known.Concat(known.Select(a=>a|0x100000u)));
            var hits=new Dictionary<uint,List<Observation>>();
            var last=new Dictionary<uint,uint>();
            var markers=new SortedDictionary<string,int>(StringComparer.Ordinal);
            for(int i=32;i<end;i+=8)
            {
                uint addr=U32(p,i),value=U32(p,i+4);
                if(addr==0xFFFFDEAD || addr==0xFFFFFADE)
                {
                    string key=Hex(addr);
                    markers[key]=markers.TryGetValue(key,out int c)?c+1:1;
                }
                if(!all.Contains(addr)) continue;
                if(!hits.TryGetValue(addr,out var history)) hits[addr]=history=new List<Observation>();
                history.Add(new Observation(i,Hex(value)));
                last[addr]=value;
            }
            var channels=new List<Channel>();
            for(int ch=0;ch<2;ch++)
            {
                uint offset=(uint)ch<<20;
                var words=known.Where(a=>last.ContainsKey(a|offset)).ToDictionary(a=>a,a=>last[a|offset]);
                // Require actual DDR4 timing words and plausible primary timing
                // values, never infer channel presence from missing state bits.
                if(!words.TryGetValue(0x50100,out uint cfg) || (cfg&3)!=0 ||
                   !words.TryGetValue(0x50200,out uint ratio)) return null;
                var timings=RunningTimings.DecodeKnownFields(0,words);
                if(!timings.TryGetValue("tCL",out uint cl)||cl<5 ||
                   !timings.TryGetValue("tRCDRD",out uint rd)||rd<5 ||
                   !timings.TryGetValue("tRP",out uint rp)||rp<5 ||
                   !timings.TryGetValue("tRAS",out uint ras)||ras<10) return null;
                double mclk=RunningTimings.NominalMclk(0,ratio);
                if(mclk<200 || mclk>3200) return null;
                var regs=new SortedDictionary<string,Register>(StringComparer.Ordinal);
                foreach(var a in known.Where(a=>words.ContainsKey(a)))
                    regs[Hex(a|offset)]=new Register(Hex(words[a]),hits[a|offset].ToArray());
                var refresh=new SortedDictionary<string,SortedDictionary<string,uint>>(StringComparer.Ordinal);
                foreach(uint a in new uint[]{0x50260,0x50264})
                    if(words.TryGetValue(a,out uint v))
                        refresh[Hex(a|offset)]=RunningTimings.DecodeRefreshWord(0,v);
                channels.Add(new Channel(ch,mclk,regs,known.Where(a=>!words.ContainsKey(a)).Select(a=>Hex(a|offset)).ToArray(),
                    timings,ExtendedUmc.DecodeKnownFields(0,words),refresh));
            }
            return new Result("experimental bounded replay observations",replay.Offset,n,32,end,p.Length-end,
                markers,companions.ToArray(),channels.ToArray(),
                "Last observed allowlisted words, not executed operations or live registers. Marker semantics, address context and opaque trailer are not decoded. Missing registers are not synthesized; refresh words are candidates, not effective refresh selection. No automatic DIMM/electrical channel association. No raw replay payload exported or executed.");
        }
    }
}
