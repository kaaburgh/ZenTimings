using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using ZenStates.Core.Hardware.Apob;

namespace ZenTimings.Cli
{
    internal static class ApobBootInfo
    {
        internal sealed record Result(string status,int[] present_slot_indices,bool both_spd_identity_matches,
            uint apcb_active_instance_code,bool dimm_configuration_updated_candidate,byte apcb_recovery_flag_code,
            byte action_on_bist_failure_code,byte workload_profile_code,uint last_pmu_train_time_raw,string note);
        private static uint U32(ReadOnlySpan<byte> p,int o)=>BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(o,4));
        private static bool All(ReadOnlySpan<byte> p,byte value)
        {
            foreach(byte v in p) if(v!=value) return false;
            return true;
        }
        internal static Result Decode(byte[] b,List<ApobEntry> entries,ApobDdr5Config.Result config)
        {
            if(config?.replay_correlation==null) return null;
            var e=ApobDdr5Config.Unique(entries,1,16,120);
            var spd=ApobDdr5Config.Unique(entries,1,17,4196);
            if(e==null || spd==null) return null;
            var p=b.AsSpan((int)e.DataOffset,(int)e.DataSize);
            var q=b.AsSpan((int)spd.DataOffset,(int)spd.DataSize);
            if(q[0]!=1 || q[1]!=2) return null;
            // Compare four-slot layout against the two CRC-validated DDR5
            // records already accepted by the GEN decoder. The four identity
            // bytes are serial numbers in this DDR5 donor, NOT exportable CRCs.
            int[] slots={-1,-1};
            for(int i=0;i<2;i++)
            {
                int o=4+1036*i;
                if(q[o+1]!=1 || q[o+8]!=0 || q[o+10]!=0 || q[o+9]>1 ||
                    slots[q[o+9]]!=-1 || q[o+14]!=0x12) return null;
                slots[q[o+9]]=o+12;
            }
            for(int i=0;i<4;i++)
            {
                var r=p.Slice(4+i*12,12);
                if(!All(r.Slice(9,3),0)) return null;
                if(i%2!=0)
                {
                    if(!All(r,0)) return null;
                    continue;
                }
                if(slots[i/2]<0) return null;
                var d=q.Slice(slots[i/2],1024);
                var identity=d.Slice(517,4);
                if(All(identity,0) || All(identity,255) ||
                    r[8]!=1 || !r.Slice(0,2).SequenceEqual(d.Slice(512,2)) ||
                    !r.Slice(2,2).SequenceEqual(d.Slice(552,2)) ||
                    !r.Slice(4,4).SequenceEqual(identity)) return null;
            }
            if(p[52]>1 || U32(p,68)!=0) return null;
            return new Result("observed DDR5 boot-info layout corroborated by both SPD identities locally",
                new[]{0,2},true,U32(p,0),p[52]!=0,p[53],p[54],p[55],U32(p,56),
                "Field names follow public APCB boot info structure; time unit and flag code semantics not established. No serial, checksum/identity word, or last-part serial exported. Does not prove MCR was used on this boot.");
        }
    }
}
