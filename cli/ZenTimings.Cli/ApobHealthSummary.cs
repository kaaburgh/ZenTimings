using System;
using System.Collections.Generic;
using System.Linq;
using ZenStates.Core.Hardware.Apob;

namespace ZenTimings.Cli
{
    internal static class ApobHealthSummary
    {
        internal sealed record EventSummary(uint declared_count,int stored_count,bool count_exceeds_capacity,
            SortedDictionary<string,int> severity_counts,bool unused_slots_all_zero);
        internal sealed record PayloadSummary(int payload_bytes,bool all_zero_payload);
        internal sealed record Result(EventSummary event_log,PayloadSummary pmu_training_failure,
            PayloadSummary mbist_result,string note);
        internal static Result Decode(byte[] b,List<ApobEntry> entries,uint version,int header,
            bool validatedLegacySpd,IReadOnlyList<LinuxApob.SpdSlot> spd)
        {
            // Independent of successful GEN/replay: failed training is exactly
            // when these records are interesting. Require the known SPD family.
            bool legacy=version==0x13 && header==0xC0 && validatedLegacySpd;
            bool ddr5=version==0x18 && header==0xA8 &&
                ApobDdr5Config.Unique(entries,1,17,4196)!=null && spd.Count==2 &&
                spd.All(s=>s.instance==0 && s.socket==0 && s.dimm==0 &&
                    s.data.memory_type=="DDR5" && s.data.bytes==1024 && s.data.base_crc_valid==true) &&
                spd.Select(s=>s.channel).OrderBy(x=>x).SequenceEqual(new[]{0,1});
            if(!legacy && !ddr5) return null;
            EventSummary events=null;
            var entry=ApobDdr5Config.Unique(entries,7,6,1076);
            if(entry!=null)
            {
                int o=(int)entry.DataOffset;
                // Public Count u16, two alignment bytes, 64 16-byte entries.
                if(b[o+2]==0 && b[o+3]==0)
                {
                    var log=ApobEventLog.Decode(b,entry.Offset);
                    if(log!=null)
                    {
                        var counts=new SortedDictionary<string,int>(StringComparer.Ordinal);
                        foreach(var ev in log.Events)
                        {
                            // Do not export raw EventInfo/DataA/DataB or unknown class values.
                            string key=ev.EventClass<=9?ev.EventClassName:"Unknown class";
                            counts[key]=counts.TryGetValue(key,out int n)?n+1:1;
                        }
                        bool tailZero=true;
                        for(int i=o+4+16*log.Events.Count;i<o+entry.DataSize;i++)
                            if(b[i]!=0) {tailZero=false;break;}
                        events=new EventSummary(log.Count,log.Events.Count,log.Count>64,counts,tailZero);
                    }
                }
            }
            PayloadSummary Nonzero(uint type,uint size)
            {
                var e=ApobDdr5Config.Unique(entries,1,type,size);
                if(e==null) return null;
                bool allZero=true;
                for(int i=(int)e.DataOffset;i<e.Offset+e.Size;i++)
                    if(b[i]!=0) {allZero=false;break;}
                return new PayloadSummary((int)e.DataSize,allZero);
            }
            var failure=ddr5?Nonzero(22,1012):null;
            var mbist=ddr5?Nonzero(18,140):null; // Legacy MEM18 is SPD.
            if(events==null && failure==null && mbist==null) return null;
            return new Result(events,failure,mbist,
                "Boot-copy records, not a live diagnostic or proof of successful training. A zero payload may be uninitialized; nonzero payload semantics are not decoded. Only public AGESA severity counts, no event codes, parameters or raw data. HMAC not verified.");
        }
    }
}
