using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using ZenStates.Core.Hardware.Apob;
using UpstreamBootInfo=ZenStates.Core.Hardware.Apob.ApobBootInfo;

namespace ZenTimings.Cli
{
    internal static class ApobBootInfo
    {
        internal sealed record Result(string status,int[] present_slot_indices,bool both_spd_identity_matches,
            uint apcb_active_instance_code,bool dimm_configuration_updated_candidate,byte apcb_recovery_flag_code,
            byte action_on_bist_failure_code,byte workload_profile_code,string last_training_date_candidate,
            string last_training_date_encoding,string note);
        private static uint U32(ReadOnlySpan<byte> p,int o)=>BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(o,4));
        private static bool All(ReadOnlySpan<byte> p,byte value)
        {
            foreach(byte v in p) if(v!=value) return false;
            return true;
        }
        // Same byte order as pinned upstream LastTrainingDate; unlike the
        // upstream heuristic, withhold an ambiguous date rather than choose
        // one of two plausible interpretations. No raw date word exported.
        private static DateTime? DateCandidate(uint raw,bool bcd)
        {
            var parts=new int[4];
            for(int i=0;i<4;i++)
            {
                int v=(int)((raw>>(8*i))&255);
                if(bcd)
                {
                    if((v&15)>9 || (v>>4)>9) return null;
                    v=(v>>4)*10+(v&15);
                }
                parts[i]=v;
            }
            int year=(parts[3]==0?20:parts[3])*100+parts[2];
            if(year<2000 || year>DateTime.UtcNow.Year+1 || parts[1]<1 || parts[1]>12 ||
                parts[0]<1 || parts[0]>DateTime.DaysInMonth(year,parts[1])) return null;
            return new DateTime(year,parts[1],parts[0]);
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
                // Upstream orders DRAM then module; both IDs are equal in
                // the donor, so that ordering cannot be established locally.
                // Accept either matching order, never return the raw IDs.
                bool vendorPair=(r.Slice(0,2).SequenceEqual(d.Slice(552,2)) && r.Slice(2,2).SequenceEqual(d.Slice(512,2))) ||
                    (r.Slice(0,2).SequenceEqual(d.Slice(512,2)) && r.Slice(2,2).SequenceEqual(d.Slice(552,2)));
                if(All(identity,0) || All(identity,255) || r[8]!=1 || !vendorPair ||
                    !r.Slice(4,4).SequenceEqual(identity)) return null;
            }
            if(p[52]>1 || U32(p,68)!=0) return null;
            var info=UpstreamBootInfo.Decode(b,e.Offset);
            if(info==null || info.Dimms.Count!=4) return null;
            var binary=DateCandidate(info.LastTrainingTimeRaw,false);
            var bcd=DateCandidate(info.LastTrainingTimeRaw,true);
            bool ambiguous=binary.HasValue && bcd.HasValue && binary!=bcd;
            string encoding=ambiguous?"ambiguous":binary.HasValue?(bcd.HasValue?"binary or BCD (same date)":"binary"):
                bcd.HasValue?"BCD":"unrecognized";
            DateTime? date=ambiguous?null:binary??bcd;
            return new Result("observed DDR5 boot-info layout corroborated by both SPD identities locally",
                new[]{0,2},true,info.ApcbActiveInstance,info.DimmConfigurationUpdated!=0,
                info.ApcbRecoveryFlag,info.ActionOnBistFailure,info.WorkloadProfile,
                date?.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),encoding,
                "LastPmuTrainTime is a candidate calendar date, not duration (pinned upstream). The local CLI displays a date only if unambiguous; sharing sanitizer omits it. No serial, checksum/identity word, or last-part serial exported. Flag semantics and MCR use not established.");
        }
    }
}
