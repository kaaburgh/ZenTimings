using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using ZenStates.Core.Hardware.Apob;

namespace ZenTimings.Cli
{
    // M33KT24A/Picasso empirical GEN 7/3 layout. The DDR4 clock byte
    // order and MR interpretation follow pinned upstream ApobLayout and
    // ApobDdr4ModeRegisters; group offsets and stride are specific to this dump.
    internal static class ApobLegacyConfig
    {
        internal sealed record ModeRegisters(string[] words,int dram_drive_ohm,int rtt_nom_code,
            int rtt_wr_code,int rtt_park_code,double? initial_vref_dq_percent_vddq,bool vref_range2);
        internal sealed record State(int state_index,int payload_memclk_offset,int memclk_mhz,int nominal_rate_mts,
            bool? matches_boot_memclk,SortedDictionary<string,uint> timings_clocks,
            SortedDictionary<string,int> config_min_ps,SortedDictionary<string,int> refresh_ns,
            ModeRegisters mode_registers);
        internal sealed record Group(int group_index,State[] states);
        internal sealed record MetadataObservation(int payload_offset,int index_word,int word_20,int word_22,int word_24,
            int[] equal_group_indices,bool word_20_matches_group_memclk,bool word_22_matches_all_spd_rated_clocks,
            bool word_24_matches_all_group_mr6_low6);
        internal sealed record MetadataResult(MetadataObservation[] observations,string note);
        internal sealed record Result(string status,uint entry_offset,Group[] groups,string note)
        {
            public MetadataResult metadata_observations { get; init; }
        }
        private static readonly int[] Bases={0xAF8,0xC88,0xDE8,0xF50};
        private static readonly int[] Clocks={1200,1067,933,800};
        private static readonly int[] Rates={2400,2133,1867,1600};
        private static readonly string[] TimingNames={"tCL","tCWL","tRCD","tRP","tRTP","tRAS","tRC","tWR",
            "tRRDS","tWTRS","tFAW","tRRDL","tWTRL","tCCDL"};
        private static readonly string[] MinimumNames={"tRCD","tRP","tRTP","tRAS","tRC","tWR",
            "tRRDS","tWTRS","tFAW","tRRDL","tWTRL","tCCDL"};
        private static ushort U16(byte[] b,int o)=>BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o,2));
        private static uint U32(byte[] b,int o)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o,4));

        // Only the observed M33KT24A metadata shape. A mismatch leaves the
        // proven GEN timing decode untouched. Raw word names are offsets
        // within the 48-byte metadata record, not asserted field semantics.
        private static MetadataResult Metadata(byte[] b,int start,List<Group> groups,IReadOnlyList<LinuxApob.SpdSlot> spd)
        {
            int[] offsets={0xAB0,0xC40};
            ushort[] common={1,0,0,0,1,1,0,0,0,0,0,1,0,0,1};
            var observations=new List<MetadataObservation>();
            for(int i=0;i<2;i++)
            {
                int o=start+offsets[i];
                if(U16(b,o)!=i || !common.Select((v,j)=>v==U16(b,o+2+2*j)).All(x=>x)) return null;
                if(U16(b,o+0x26)!=0 || U16(b,o+0x28)!=0 || U16(b,o+0x2A)!=0 ||
                   U16(b,o+0x2C)!=0 || U16(b,o+0x2E)!=1) return null;
                // Compare the entire four-block groups, not just decoded fields.
                if(!b.AsSpan(start+Bases[i]-24,336).SequenceEqual(b.AsSpan(start+Bases[i+2]-24,336)))
                    return null;
                int w20=U16(b,o+0x20),w22=U16(b,o+0x22),w24=U16(b,o+0x24);
                if(w20<200 || w20>3200 || w22<200 || w22>3200 || w24>63) return null;
                bool clock=w20==groups[i].states[0].memclk_mhz;
                bool rated=spd.Count==2 && spd.All(x=>x.data.tck_min_ps is int t && t>0 &&
                    Math.Abs(w22-1000000.0/t)<=1.0);
                bool mr6=groups[i].states.Concat(groups[i+2].states).All(x=>
                    (Convert.ToUInt32(x.mode_registers.words[6].Substring(2),16)&63)==w24);
                // Correlation, not a claim of target speed or channel association.
                if(!clock || !rated || !mr6) return null;
                observations.Add(new MetadataObservation(offsets[i],i,w20,w22,w24,new[]{i,i+2},clock,rated,mr6));
            }
            return new MetadataResult(observations.ToArray(),
                "Observed indexed metadata records and byte-identical four-state group pairs. Word offsets are hexadecimal. Correlations to highest saved MEMCLK, SPD tCK and MR6 low bits are empirical; physical channel mapping and word semantics unknown. SPD correlation does not prove the firmware can train at that rate.");
        }

        internal static Result Decode(byte[] b,List<ApobEntry> entries,uint version,int header,bool legacySpd,
            IReadOnlyList<LinuxApob.Electrical> electrical,IReadOnlyList<LinuxApob.SpdSlot> spd)
        {
            if(!legacySpd || version!=0x13 || header!=0xC0) return null;
            var hits=entries.Where(e=>e.Is(7,3) && e.InstanceId==0).ToArray();
            if(hits.Length!=1 || hits[0].RawGroupId!=7 || hits[0].RawDataTypeId!=3 || hits[0].Size!=5356)
                return null;
            var entry=hits[0];
            int start=(int)entry.DataOffset, end=start+(int)entry.DataSize;
            if(start<0 || end>b.Length || start+Bases[3]+3*0x54+0x38>end) return null;
            var bootClocks=electrical.Select(e=>e.memclk_mhz).Distinct().ToArray();
            var groups=new List<Group>();
            for(int g=0;g<Bases.Length;g++)
            {
                var states=new List<State>();
                for(int i=0;i<Clocks.Length;i++)
                {
                    int rel=Bases[g]+i*0x54,o=start+rel;
                    int clk=U16(b,o);
                    if(clk!=Clocks[i] || U16(b,o+2)!=0) return null;
                    var timings=new SortedDictionary<string,uint>(StringComparer.Ordinal);
                    for(int j=0;j<TimingNames.Length;j++) timings[TimingNames[j]]=b[o+4+j];
                    uint T(string key)=>timings[key];
                    if(T("tCL")<8 || T("tCL")>40 || T("tCWL")<5 || T("tCWL")>T("tCL") ||
                       T("tRCD")<5 || T("tRP")<5 || T("tRAS")<=T("tRCD") ||
                       T("tRC")<T("tRAS")+T("tRP") || T("tWR")<5 || T("tWR")>64 ||
                       T("tRTP")<2 || T("tRTP")>32 || T("tRRDS")<2 || T("tRRDS")>16 ||
                       T("tRRDL")<T("tRRDS") || T("tWTRS")<1 || T("tWTRL")<T("tWTRS") ||
                       T("tFAW")<T("tRRDS") || T("tFAW")>96 || T("tCCDL")<4 || T("tCCDL")>8) return null;
                    var minima=new SortedDictionary<string,int>(StringComparer.Ordinal);
                    for(int j=0;j<MinimumNames.Length;j++)
                    {
                        int ps=U16(b,o-24+2*j)*25;
                        if(ps<=0 || ps>200000) return null;
                        minima[MinimumNames[j]]=ps;
                    }
                    uint r1=U32(b,o+0x12),r2=U32(b,o+0x16),r4=U32(b,o+0x1A);
                    if(r1<100 || r1>2000 || r2==0 || r4==0 || r2>r1 || r4>r2) return null;
                    var refresh=new SortedDictionary<string,int>(StringComparer.Ordinal) {
                        ["tRFC"]=(int)r1,["tRFC2"]=(int)r2,["tRFC4"]=(int)r4
                    };
                    // Fixed MR offset, not a heuristic scan across later states.
                    uint mr=(uint)(o+0x2A);
                    var m=ApobDdr4ModeRegisters.Find(b,mr,mr,(ulong)end,T("tCL"),T("tCWL"),T("tWR"),T("tCCDL"));
                    if(m==null) return null;
                    var mode=new ModeRegisters(Enumerable.Range(0,7).Select(k=>$"0x{m[k]:X4}").ToArray(),
                        m.DriverImpedance,(m[1]>>8)&7,(m[2]>>9)&7,(m[5]>>6)&7,m.VrefDq,m.VrefDqRange2);
                    // All four groups share the same frequency-specific timings
                    // in this observed layout. MR6 Vref can differ by group.
                    if(g>0)
                    {
                        var prior=groups[0].states[i];
                        if(!prior.timings_clocks.SequenceEqual(timings) ||
                           !prior.config_min_ps.SequenceEqual(minima) || !prior.refresh_ns.SequenceEqual(refresh)) return null;
                    }
                    states.Add(new State(i,rel,clk,Rates[i],bootClocks.Length==1?clk==bootClocks[0]:null,
                        timings,minima,refresh,mode));
                }
                groups.Add(new Group(g,states.ToArray()));
            }
            return new Result("experimental fixed legacy GEN configuration layout",entry.Offset,groups.ToArray(),
                "Four groups of four saved frequency blocks; group/UMC/DIMM association and runtime P-state selection unknown. Rates are nominal DDR4 bins; configuration minimum times are 25 ps units, not necessarily SPD minima. Refresh values are ns, not programmed UMC clocks. MR6 VrefDQ is the saved mode-register value, not trained per-device Vref or live voltage. HMAC not verified.") { metadata_observations=Metadata(b,start,groups,spd) };
        }
    }
}
