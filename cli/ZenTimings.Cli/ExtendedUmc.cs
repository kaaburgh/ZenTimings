using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZenStates.Core.Dictionaries;

namespace ZenTimings.Cli
{
    internal static class ExtendedUmc
    {
        internal sealed record Result(string status, SortedDictionary<string,uint> fields,
            SortedDictionary<string,string> registers, SortedDictionary<string,string> unavailable,
            string refresh_mode, uint? fgr_multiplier, bool? fgr_on_the_fly,
            double? nominal_effective_trfc_ns, string nitro, string electrical_status);

        // Additional fields use the original dictionary names, not aliases that
        // might accidentally match a saved BIOS setting with different semantics.
        // Exclude LPDDR-only WCK fields and experimental whole-register aliases.
        private static readonly HashSet<string> ExtraFields = new(StringComparer.Ordinal) {
            "DimmEccEn","BurstCtrl","BurstLength","AggrPwrDownEn","PowerDownMode",
            "BankGroupEn","Preamble2t","UclkGtFclk","RPpb","RCpb","TRCPAGE",
            "RDRDBan","WRWRBan","MW","ShortInit","ZqcsInterval","TzqOperCal","Tzqcs",
            "MODPDA","MRDPDA","MOD","MRD","DLLK","XS","RankBusyDly","CmdParLatency",
            "AlertParDly","AlertCrcDly","MRRI","MRR","MRW","CtrlSwitchClks",
            "AggrPwrDownDly","PwrDownDly","PD","CSH","SRX2SRX","STAB",
            "AlertCrcPulse","AlertParPulse","STAG","STAGsb","CPDED","CACSH","XP",
            "PARINL","PHYWRD","PHYRDL","PHYWRL","RDDATAEN","TgearHold","TgearSetup",
            "LpExitDly","LpDly","PHYUPD_CmdDly","PHYUPD_WrDatDly","PHYUPD_resp",
            "WRMPR","CmdStgCnt","RcvrWait","WRPOST","WRPRE","RDPOST","RDPRE",
            "ECSc","DdrMaxRate","DdrMaxRateEnf","OdtsReadInterval"
        };
        private static uint Bits(uint value,int high,int low) =>
            (value >> low) & (uint)((1UL << (high-low+1))-1);

        internal static uint[] Addresses(int type)
        {
            if(type!=0 && type!=1) throw new ArgumentOutOfRangeException(nameof(type));
            var defs=type==0?DDR4Dictionary.defs:DDR5Dictionary.defs;
            uint[] bgs={0x50050,0x50058,0x500D0,0x500D4};
            return defs.Where(p=>p.Value.Any(f=>ExtraFields.Contains(f.Name))).Select(p=>p.Key)
                .Concat(bgs).Concat(type==1?new uint[]{0x50284}:Array.Empty<uint>())
                .Append(0x5012Cu).Distinct().OrderBy(x=>x).ToArray();
        }

        internal static SortedDictionary<string,uint> DecodeKnownFields(int type,IReadOnlyDictionary<uint,uint> words)
        {
            var defs=type==0?DDR4Dictionary.defs:DDR5Dictionary.defs;
            var fields=new SortedDictionary<string,uint>(StringComparer.Ordinal);
            foreach(var p in defs)
                if(words.TryGetValue(p.Key,out uint value))
                    foreach(var f in p.Value.Where(f=>ExtraFields.Contains(f.Name)))
                        fields.Add(f.Name,Bits(value,f.HiBit,f.LoBit));
            return fields;
        }

        internal static Result Capture(int type,uint offset, IReadOnlyDictionary<uint,uint> basic,
            IReadOnlyDictionary<string,uint> timings,double nominalMclk,Func<uint,uint> read)
        {
            var addresses=Addresses(type);
            var words=new Dictionary<uint,uint>();
            var registers=new SortedDictionary<string,string>(StringComparer.Ordinal);
            var unavailable=new SortedDictionary<string,string>(StringComparer.Ordinal);
            string Address(uint a)=>$"0x{offset|a:X8}";
            foreach(uint a in addresses)
            {
                if(basic.TryGetValue(a,out uint v)) { words[a]=v; continue; }
                try
                {
                    uint x=read(offset|a);
                    if(x==uint.MaxValue) throw new IOException("all-ones response");
                    words[a]=x;
                }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException)
                { unavailable[Address(a)]=ex.Message; }
            }
            foreach(uint a in addresses.Reverse())
            {
                if(basic.ContainsKey(a) || !words.TryGetValue(a,out uint v)) continue;
                try
                {
                    if(read(offset|a)!=v) throw new IOException("unstable register");
                }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException)
                { words.Remove(a); unavailable[Address(a)]=ex.Message; }
            }
            foreach(var p in words) registers[Address(p.Key)]=$"0x{p.Value:X8}";
            var fields=DecodeKnownFields(type,words);

            // Same pattern test as BaseDramTimings.ReadBankGroupSwap. An all-zero
            // BGS pair is left unresolved rather than advertising Enabled.
            if(words.TryGetValue(0x50050,out uint b0) && words.TryGetValue(0x50058,out uint b1) &&
                (b0!=0 || b1!=0))
                fields["BGS"]=(b0==0x87654321 && b1==0x87654321)?0u:1u;
            else unavailable["BGS"]="missing or all-zero mapping pair";
            if(words.TryGetValue(0x500D0,out uint a0) && words.TryGetValue(0x500D4,out uint a1))
                fields["BGSAlt"]=(Bits(a0,10,4)>0 || Bits(a1,10,4)>0)?1u:0u;
            else unavailable["BGSAlt"]="missing alternate mapping pair";

            string refresh="unavailable";
            uint? multiplier=null;
            bool? onTheFly=null;
            double? ns=null;
            if(words.TryGetValue(0x5012C,out uint mode))
            {
                uint fgr=Bits(mode,18,16);
                fields["FGR"]=fgr;
                if(type==0)
                {
                    multiplier=fgr switch {0=>1u,1 or 5=>2u,2 or 6=>4u,_=>null};
                    onTheFly=multiplier.HasValue?(fgr is 5 or 6):null;
                    refresh=!multiplier.HasValue?"reserved":fgr==0?"normal":onTheFly==true?"fine-granularity on-the-fly":"fine-granularity fixed";
                    // On-the-fly can switch between refresh modes: do not
                    // claim one effective tRFC in that case.
                    string key=multiplier switch {1=>"tRFC",2=>"tRFC2",4=>"tRFC4",_=>null};
                    if(onTheFly==false && key!=null && timings.TryGetValue(key,out uint cycles) && cycles>0)
                        ns=cycles*1000.0/nominalMclk;
                }
                else
                {
                    bool pb=Bits(mode,1,1)!=0;
                    refresh=pb?(fgr==0?"per-bank only":"mixed"):(fgr==0?"normal":"fine-granularity");
                    if(!pb && fgr==0 && timings.TryGetValue("tRFC",out uint cycles) && cycles>0)
                        ns=cycles*1000.0/nominalMclk;
                    // Do not infer effective tRFC for DDR5 mixed/per-bank or
                    // unverified FGR codes.
                }
            }
            string nitro=null;
            if(type==1 && words.TryGetValue(0x50284,out uint n))
            {
                fields["NitroRxData"]=Bits(n,9,8);
                fields["NitroTxData"]=Bits(n,5,4);
                fields["NitroCtrlLine"]=Bits(n,1,0);
                fields["NitroRxDatChnDlyMode"]=Bits(n,18,18);
                fields["NitroTxDatChnDlyMode"]=Bits(n,17,17);
                fields["NitroCtrlLineDlyMode"]=Bits(n,16,16);
                nitro=$"{fields["NitroRxData"]}/{fields["NitroTxData"]}/{fields["NitroCtrlLine"]}";
            }
            return new Result(unavailable.Count==0?"complete":"partial",fields,registers,unavailable,
                refresh,multiplier,onTheFly,ns,nitro,
                "ProcODT, RTT and drive strengths not acquired; require validated APOB/APCB or other hardware source.");
        }
    }
}
