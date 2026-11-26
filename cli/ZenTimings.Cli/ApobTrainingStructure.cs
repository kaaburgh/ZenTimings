using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using ZenStates.Core.Hardware.Apob;

namespace ZenTimings.Cli
{
    // Aggregate observations of the observed DDR5 v0x18 family.
    // No raw PHY or PMU words or assumed lane/CSR mappings.
    internal static class ApobTrainingStructure
    {
        internal sealed record Phase(uint type,int record_count_candidate,int end_exclusive_candidate,
            int trailing_bytes,int adjacent_u16_pairs,int equal_adjacent_u16_pairs,
            bool donor_terminal_markers_match);
        internal sealed record Match(int compared_u16,int equal_u16,int equal_nonzero_u16);
        internal sealed record Alignment(int mem30_offset_minus_mem31_bytes,Match shifted,
            Match zero_shift,Match mem30_self_repeat_at_0x24fc);
        internal sealed record Pmu(int candidate_block_stride,bool both_rate_words_match_highest_gen,
            int prefix_256_equal_bytes,int compared_bytes,int equal_bytes,int equal_nonzero_bytes,
            int remaining_bytes_not_examined);
        internal sealed record Result(string status,Phase[] phy_phases,Alignment phy_fixed_alignment,
            Pmu pmu_smb,string note);

        private static uint U32(ReadOnlySpan<byte> p,int o)=>BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(o,4));
        private static ushort U16(ReadOnlySpan<byte> p,int o)=>BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(o,2));

        private static Phase InspectPhase(ReadOnlySpan<byte> p,uint type)
        {
            if(p.Length!=10272) return null;
            uint count=U32(p,16);
            if(count<2 || count>(p.Length-32)/8 ||
                U32(p,32)!=0xDEAD || U32(p,36)!=0xB) return null;
            int n=(int)count,end=32+8*n,equal=0;
            for(int i=32;i<end;i+=4)
                if(U16(p,i)==U16(p,i+2)) equal++;
            bool terminal=n>=3 &&
                U32(p,end-24)==0xDEAD && U32(p,end-20)==2 &&
                U32(p,end-16)==0xDEAD && U32(p,end-12)==16 &&
                U32(p,end-8)==0xDEAD && U32(p,end-4)==1;
            return new Phase(type,n,end,p.Length-end,n*2,equal,terminal);
        }

        private static Match Compare(ReadOnlySpan<byte> a,ReadOnlySpan<byte> b,int shift)
        {
            // All spans are already bounded to the candidate count.
            int count=Math.Max(0,Math.Min(a.Length-shift,b.Length))/2;
            int equal=0,nonzero=0;
            for(int i=0;i<count;i++)
            {
                ushort x=U16(a,shift+2*i),y=U16(b,2*i);
                if(x==y) { equal++;if(x!=0) nonzero++; }
            }
            return new Match(count,equal,nonzero);
        }

        internal static Result Decode(byte[] b,List<ApobEntry> entries,ApobDdr5Config.Result config)
        {
            // The existing DDR5 decoder has already checked version, header,
            // SPD topology, entry sizes, GEN and both UMC timing blocks.
            if(config?.replay_correlation==null) return null;
            var phases=new List<Phase>();
            ReadOnlySpan<byte> a=default,c=default;
            for(uint t=30;t<=31;t++)
            {
                var e=ApobDdr5Config.Unique(entries,1,t,10320);
                if(e==null) continue;
                var p=b.AsSpan((int)e.DataOffset,(int)e.DataSize);
                var phase=InspectPhase(p,t);
                if(phase==null) continue;
                phases.Add(phase);
                var bounded=p.Slice(32,phase.end_exclusive_candidate-32);
                if(t==30) a=bounded; else c=bounded;
            }
            Alignment alignment=null;
            if(!a.IsEmpty && !c.IsEmpty)
                alignment=new Alignment(0x2FC,Compare(a,c,0x2FC),Compare(a,c,0),
                    Compare(a,a,0x24FC));

            Pmu pmu=null;
            var m=ApobDdr5Config.Unique(entries,1,5,16432);
            if(m!=null)
            {
                var p=b.AsSpan((int)m.DataOffset,(int)m.DataSize);
                const int stride=0x1FA2;
                uint rate=config.groups[0].states[0].nominal_rate_mts;
                int equal=0,nonzero=0,prefix=0;
                for(int i=0;i<stride;i++)
                    if(p[i]==p[stride+i])
                    {
                        equal++;if(p[i]!=0) nonzero++;
                        if(i<256) prefix++;
                    }
                pmu=new Pmu(stride,U16(p,2)==rate && U16(p,stride+2)==rate,
                    prefix,stride,equal,nonzero,p.Length-2*stride);
            }
            if(phases.Count==0 && pmu==null) return null;
            return new Result("bounded structural observations only",phases.ToArray(),alignment,pmu,
                "Donor-derived comparisons, not a PHY address/value stream, physical channel or P-state mapping, proven PMU block boundaries, or trained per-lane values. Unknown words and tails not exported; HMAC not verified.");
        }
    }
}
