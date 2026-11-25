using System;
using System.Collections.Generic;
using System.IO;
using ZenTimings.Cli;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition,string message)
    {
        checks++;
        if(!condition) throw new Exception("FAILED: "+message);
    }
    private static void Fails<T>(Action action,string message) where T:Exception
    {
        try { action(); }
        catch(T) { checks++; return; }
        throw new Exception("FAILED: "+message);
    }
    private static Dictionary<uint,uint> Fixture(bool ddr5)
    {
        var r=new Dictionary<uint,uint>();
        // Initialize the exact subset of registers read by the decoder.
        foreach(uint addr in new uint[]{0x50100,0x5012C,0x50200,0x50204,0x50208,0x5020C,
            0x50210,0x50214,0x50218,0x50220,0x50224,0x50228,0x50230,0x50254,0x50260,
            0x50264,0x50268,0x5026C,0x502C0,0x502C4,0x502C8,0x502CC,0x50DF0,0x50000,0x50008})
            r[addr]=0;
        r[0x50000]=1;
        r[0x150DF0]=1u<<19; r[0x150000]=0; r[0x150008]=0;
        r[0x50100]=ddr5?1u:0;
        r[0x50200]=ddr5?3000u|(1u<<18):36u|(1u<<11);
        r[0x50204]=ddr5?36u|(64u<<8)|(40u<<16)|(42u<<24):16u|(39u<<8)|(18u<<16)|(20u<<24);
        r[0x50208]=ddr5?104u|(40u<<16):57u|(18u<<16);
        r[0x50210]=ddr5?128u:32u;
        r[0x50214]=ddr5?34u|(7u<<8)|(24u<<16):14u|(4u<<8)|(12u<<16);
        r[0x50220]=(ddr5?45u:13u)<<24;
        r[0x50228]=ddr5?48u<<8:20u<<8;
        r[0x50230]=ddr5?11677u:9360u;
        r[0x50260]=ddr5?0x00C00138u:0x21060138u;
        r[0x50264]=ddr5?480u|(260u<<16):350u|(192u<<11)|(132u<<22);
        r[0x502C0]=180;
        return r;
    }
    private static void Main()
    {
        foreach(bool ddr5 in new[]{false,true})
        {
            var cpu=new RunningTimings.CpuId("AuthenticAMD",ddr5?0x19:0x17,ddr5?0x61:0x18);
            var r=Fixture(ddr5);
            var snap=RunningTimings.Capture(cpu,a=>r[a],"fixture");
            Check(snap.Channels.Length==1,"inactive second channel not read");
            var c=snap.Channels[0];
            Check(c.MemoryType==(ddr5?"DDR5":"DDR4"),"memory type");
            Check(c.NominalMclkMHz==(ddr5?3000:1200),"UMC ratio");
            Check(c.Timings["tCL"]==(ddr5?36:16),"CL");
            Check(c.Timings["tRCDWR"]==(ddr5?42:20),"separate write timing");
            Check(c.Timings["tRFC"]==(ddr5?480:350),"refresh sentinel");
            Check(c.Timings["tRFC2"]==(ddr5?260:192),"refresh width");
            Check(c.Timings["GDM"]==1,"GDM bit position");
            Check(c.Timings["tFAW"]==(ddr5?128:32),"FAW width");
            Check(c.Timings["tRDRDSCL"]==(ddr5?45:13),"SCL width");
            Check(c.Timings["tRDWR"]==(ddr5?48:20),"RDWR width");
            Check(c.Timings["tREFI"]==(ddr5?11677:9360),"REFI");
            Check(ddr5?c.Timings["tRFCsb"]==180:c.Timings["tRFC4"]==132,"generation-specific refresh");
            // The actual M75n saved Auto values 312/192/132 encode precisely
            // the DDR4 reset/reference pattern 0x21060138. Its appearance in
            // the UMC register alone cannot establish the effective rank value.
            uint activeRefresh=r[0x50264];
            r[0x50264]=r[0x50260];
            Check(!RunningTimings.Capture(cpu,a=>r[a],"fixture").Channels[0].Timings.ContainsKey("tRFC"),
                "default-pattern-only refresh remains unresolved");
            r[0x50264]=activeRefresh;
            r[0x50260]=r[0x50264]+1;
            Check(!RunningTimings.Capture(cpu,a=>r[a],"fixture").Channels[0].Timings.ContainsKey("tRFC"),
                "conflicting refresh values remain unresolved");
            int reads=0;
            Fails<IOException>(()=>RunningTimings.Capture(cpu,a=>a==0x50204 && ++reads>1?r[a]^1:r[a],"fixture"),
                "unstable register rejected");
            r[0x50204]=uint.MaxValue;
            Fails<IOException>(()=>RunningTimings.Capture(cpu,a=>r[a],"fixture"),"all-ones response rejected");
            r[0x50204]=0;
            Fails<InvalidDataException>(()=>RunningTimings.Capture(cpu,a=>r[a],"fixture"),"zero primary timings rejected");
            r=Fixture(ddr5);
            r[0x50100]^=1;
            Fails<InvalidDataException>(()=>RunningTimings.Capture(cpu,a=>r[a],"fixture"),"wrong DRAM type rejected");
        }
        int accesses=0;
        Fails<NotSupportedException>(()=>RunningTimings.Capture(new("GenuineIntel",6,1),a=>{accesses++;return 0;},"fixture"),"Intel rejected");
        Fails<NotSupportedException>(()=>RunningTimings.Capture(new("AuthenticAMD",0x19,0x74),a=>{accesses++;return 0;},"fixture"),"unvalidated AMD model rejected");
        Check(accesses==0,"no requests on unsupported CPU");
        Console.WriteLine("OK: "+checks+" running timing assertions");
    }
}
