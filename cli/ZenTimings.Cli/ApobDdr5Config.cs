using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using ZenStates.Core.Common;
using ZenStates.Core.Hardware.Apob;

namespace ZenTimings.Cli
{
    // Empirical v0x18 donor from 795iX3D.bin. The 32-bit timing order and
    // electrical field offsets are from pinned upstream ApobLayout.cs.
    // Do not infer CPU identity, live voltages or physical channel association.
    internal static class ApobDdr5Config
    {
        internal sealed record State(int state_index,int payload_memclk_offset,uint memclk_mhz,uint dfi_clk_mhz,
            uint nominal_rate_mts,SortedDictionary<string,uint> timings_clocks,
            SortedDictionary<string,uint> refresh_slot_words,SortedDictionary<string,uint> refresh_candidates_clocks,
            int payload_electrical_offset,SortedDictionary<string,LinuxApob.Value> electrical_candidates);
        internal sealed record Group(int group_index,SortedDictionary<string,LinuxApob.Value> mem25_record,State[] states);
        internal sealed record ReplayCorrelation(int[] umc_address_indices,string[] matched_primary_fields,
            SortedDictionary<string,uint> matching_refresh_candidates,string note);
        internal sealed record Result(string status,uint entry_offset,Group[] groups,string note)
        {
            public string layout { get; init; }
            public ReplayCorrelation replay_correlation { get; init; }
        }
        private static readonly (string name,int off)[] Fields={
            ("tCL",0x0C),("tRCDWR",0x10),("tRCDRD",0x14),("tRP",0x18),("tRAS",0x1C),("tRC",0x20),
            ("tCWL",0x24),("tRRDS",0x28),("tRRDL",0x2C),("tFAW",0x30),("tWTRL",0x34),("tWTRS",0x38),
            ("tRTP",0x3C),("tWR",0x40),("XS",0x5C),("XP",0x64),("PD",0x68),("tREFI",0x6C),
            ("CACSH",0xC4),("CSH",0xC8),("tCCDL",0x100),("tCCDL_WR",0x104),("tCCDL_WR2",0x108),
            ("DLLK",0x118),("ECSc",0x11C)
        };
        private static uint U32(ReadOnlySpan<byte> b,int o)=>BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o,4));
        internal static ApobEntry Unique(List<ApobEntry> entries,uint g,uint t,uint size)
        {
            var a=entries.Where(e=>e.GroupId==g && e.DataTypeId==t && e.InstanceId==0).ToArray();
            return a.Length==1 && a[0].RawGroupId==g && a[0].RawDataTypeId==t && a[0].Size==size?a[0]:null;
        }
        private static SortedDictionary<string,LinuxApob.Value> Electrical(ReadOnlySpan<byte> b,bool ext)
        {
            var r=new SortedDictionary<string,LinuxApob.Value>(StringComparer.Ordinal);
            void Add(string key,EncodedValueBase value)=>r.Add(key,new LinuxApob.Value(value.RawValue,value.ToString()));
            r["Gdm"]=new LinuxApob.Value(b[1],"saved code "+b[1]);
            Add("RttNomRd",new Rtt(b[2]));Add("RttNomWr",new Rtt(b[3]));
            Add("RttWr",new Rtt(b[4]));Add("RttPark",new Rtt(b[5]));Add("RttParkDqs",new Rtt(b[6]));
            Add("DramDataDs",new DramDataDrvStren(b[7]));
            Add("CkOdtA",new GroupOdtImpedance(b[8]));Add("CsOdtA",new GroupOdtImpedance(b[9]));
            Add("CaOdtA",new GroupOdtImpedance(b[10]));Add("CkOdtB",new GroupOdtImpedance(b[11]));
            Add("CsOdtB",new GroupOdtImpedance(b[12]));Add("CaOdtB",new GroupOdtImpedance(b[13]));
            Add("ProcOdt",new ProcOdt(b[14]));Add("ProcDqDs",new ProcOdt(b[15]));
            Add("ProcCaDs",new ProcOdtImpedance(b[17]));
            if(ext) {
                Add("ProcCkDs",new ProcOdtImpedance(b[18]));
                Add("ProcCsDs",new ProcOdtImpedance(b[19]));
            }
            int v=ext?20:18;
            Add("PhyVref",new PhyVref(b[v]));Add("DramVrefDq",new DramVref(b[v+1]));
            Add("DramVrefCa",new DramVref(b[v+2]));Add("DramVrefCs",new DramVref(b[v+3]));
            return r;
        }
        // Validate a non-donor candidate against independently observed UMC
        // words in both address blocks. This does not select live/P-state
        // values or associate any physical DIMM/channel.
        internal static ReplayCorrelation Correlate(Result config,ApobReplay.Result replay)
        {
            if(config==null || replay==null || replay.channels.Length!=2) return null;
            var state=config.groups[0].states[0];
            string[] fields={"tCL","tCWL","tFAW","tRAS","tRC","tRCDRD","tRCDWR","tREFI",
                "tRP","tRRDL","tRRDS","tRTP","tWR","tWTRL","tWTRS"};
            var expected=new SortedDictionary<string,uint>(StringComparer.Ordinal);
            // The alternate slots are independently supported by SPD minima.
            // Nonzero standard-layout slots are only candidates until they
            // match the observed replay. A zero standard slot is unavailable.
            foreach(var f in new[]{(name:"tRFC",key:"0x88"),(name:"tRFC2",key:"0x94"),(name:"tRFCsb",key:"0xAC")})
            {
                uint mapped=state.refresh_slot_words[f.key];
                expected[f.name]=mapped!=0?mapped:state.refresh_candidates_clocks[f.name];
            }
            foreach(var c in replay.channels)
            {
                if(c.nominal_mclk_mhz!=state.memclk_mhz ||
                    fields.Any(k=>!c.candidate_timings.TryGetValue(k,out uint value) || value!=state.timings_clocks[k]))
                    return null;
                // tRFC and tRFC2 must match one complete word, not be
                // cherry-picked from unrelated register candidates.
                if(!c.refresh_word_candidates.Values.Any(w=>
                        w.TryGetValue("tRFC",out uint a) && a==expected["tRFC"] &&
                        w.TryGetValue("tRFC2",out uint b) && b==expected["tRFC2"]) ||
                   !c.refresh_word_candidates.Values.Any(w=>
                        w.TryGetValue("tRFCsb",out uint a) && a==expected["tRFCsb"])) return null;
            }
            return new ReplayCorrelation(replay.channels.Select(x=>x.umc_address_index).ToArray(),fields,expected,
                "Highest saved GEN state matches both UMC replay address blocks in 15 primary/secondary/refresh-interval fields and complete observed tRFC/tRFC2 and tRFCsb candidates. Nonzero standard refresh slots compared preferentially, otherwise SPD-like alternate slots. No proof of active state, effective refresh selection, physical group mapping or present hardware.");
        }

        internal static Result Decode(byte[] b,List<ApobEntry> entries,uint version,int header,IReadOnlyList<LinuxApob.SpdSlot> spd,bool variant=false)
        {
            if(version!=0x18 || header!=0xA8 || spd.Count!=2 ||
               !spd.Select(s=>s.channel).OrderBy(x=>x).SequenceEqual(new[]{0,1}) ||
               spd.Any(s=>s.instance!=0 || s.socket!=0 || s.dimm!=0 || s.data.memory_type!="DDR5" ||
                   s.data.bytes!=1024 || s.data.base_crc_valid!=true || s.data.ddr5==null)) return null;
            var gen=Unique(entries,7,3,6020);
            var main=Unique(entries,1,25,96);
            if(gen==null || main==null || Unique(entries,1,17,4196)==null ||
                Unique(entries,7,4,15312)==null || Unique(entries,1,30,10320)==null ||
                Unique(entries,1,31,10320)==null) return null;
            var p=b.AsSpan((int)gen.DataOffset,(int)gen.DataSize);
            var m=b.AsSpan((int)main.DataOffset,(int)main.DataSize);
            var groups=new List<Group>();
            uint[] clocks={2400,1600,1000}; // Narrow donor fingerprint, not a general frequency search.
            int[] refreshOffsets={0x84,0x88,0x8C,0x90,0x94,0x98,0x9C,0xA8,0xAC,0xB0};
            for(int g=0;g<2;g++)
            {
                int first=0x630+g*0x7A0;
                var mm=m.Slice(24*g,24);
                if(mm[0]!=1) return null;
                var states=new List<State>();
                for(int i=0;i<3;i++)
                {
                    int o=first+i*0x164, eo=first+0x58F+i*0x19;
                    uint clk=U32(p,o),dfi=U32(p,o+4);
                    if(dfi>4000 || dfi*2!=clk || U32(p,o+8)!=0) return null;
                    if(variant)
                    {
                        if(clk<400 || clk>4000 || (i==0 && clk<1000) ||
                            (i>0 && clk>=states[i-1].memclk_mhz)) return null;
                    }
                    else if(clk!=clocks[i]) return null;
                    var timings=new SortedDictionary<string,uint>(StringComparer.Ordinal);
                    foreach(var f in Fields) timings[f.name]=U32(p,o+f.off);
                    if(timings["tCL"]<10 || timings["tCL"]>100 || timings["tCWL"]<5 ||
                        timings["tCWL"]>timings["tCL"] || timings["tRCDRD"]<5 ||
                        timings["tRCDWR"]<5 || timings["tRP"]<5 || timings["tRAS"]<10 ||
                        timings["tRC"]<timings["tRAS"] || timings["tREFI"]==0 ||
                        timings.Values.Any(v=>v>65535)) return null;
                    var raw=new SortedDictionary<string,uint>(StringComparer.Ordinal);
                    foreach(int x in refreshOffsets) raw[$"0x{x:X2}"]=U32(p,o+x);
                    uint r1=U32(p,o+0x84),r2=U32(p,o+0x90),rs=U32(p,o+0xA8);
                    // Standard desktop offsets +88/+94/+AC are zero here.
                    // Only report the alternate copies when duplicated and
                    // independently consistent with both validated SPD minima.
                    if((!variant && (U32(p,o+0x88)!=0 || U32(p,o+0x94)!=0 || U32(p,o+0xAC)!=0)) ||
                       raw.Values.Any(v=>v>65535) ||
                       r1==0 || r2==0 || rs==0 || r1<r2 || r2<rs ||
                       U32(p,o+0x8C)!=r1 || U32(p,o+0x98)!=r1 ||
                       U32(p,o+0x9C)!=r2 || U32(p,o+0xB0)!=rs) return null;
                    var refresh=new SortedDictionary<string,uint>(StringComparer.Ordinal) {
                        ["tRFC"]=r1,["tRFC2"]=r2,["tRFCsb"]=rs
                    };
                    foreach(var slot in spd)
                        foreach(var f in refresh)
                            if(!slot.data.ddr5.jedec_refresh_ns.TryGetValue(f.Key,out int ns) ||
                                Math.Abs(f.Value-ns*clk/1000.0)>1.01) return null;
                    var ex=p.Slice(eo,25);
                    if(i==0 && (!mm.Slice(1,17).SequenceEqual(ex.Slice(1,17)) ||
                                !mm.Slice(18,4).SequenceEqual(ex.Slice(20,4)))) return null;
                    if(i>0 && !ex.SequenceEqual(p.Slice(first+0x58F+0x19,25))) return null;
                    if(g>0)
                    {
                        var prev=groups[0].states[i];
                        if(prev.memclk_mhz!=clk || prev.dfi_clk_mhz!=dfi || !prev.timings_clocks.SequenceEqual(timings) || !prev.refresh_slot_words.SequenceEqual(raw)) return null;
                    }
                    states.Add(new State(i,o,clk,dfi,clk*2,timings,raw,refresh,eo,Electrical(ex,true)));
                }
                groups.Add(new Group(g,Electrical(mm,false),states.ToArray()));
            }
            return new Result(variant?"experimental DDR5 v0x18 same-layout candidate":"experimental DDR5 v0x18 donor configuration",gen.Offset,groups.ToArray(),
                "Saved frequency blocks and electrical record candidates, not live measurements. Layout identified by donor structure, not CPU ID. MEM25 main and first extended record agree at mapped offsets. Refresh alternatives correlate with SPD and duplicate slots; not proof of selected UMC refresh mode. No automatic group/UMC/DIMM or P-state mapping; Gdm is saved code, not a live register interpretation. HMAC not verified.") { layout=variant?"ddr5-v0x18-same-layout-candidate":"ddr5-donor-v0x18" };
        }
    }
}
