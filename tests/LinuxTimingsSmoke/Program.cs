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
    // Live Lenovo M75n / M33KT24A, Picasso 17h:18h, ryzen_smu 0.1.2.
    // Only UMC registers, no raw UEFI blobs, serials or machine identifiers.
    private static void M75nCapture()
    {
        var bank = new Dictionary<uint,uint> {
            [0x50000]=0x00000001,[0x50008]=0x00000000,[0x50100]=0x80000200,
            [0x5012C]=0x71100460,[0x50200]=0x00000124,[0x50204]=0x11112711,
            [0x50208]=0x00110038,[0x5020C]=0x09000807,[0x50210]=0x00000024,
            [0x50214]=0x0009030C,[0x50218]=0x00000012,[0x50220]=0x03010303,
            [0x50224]=0x03010505,[0x50228]=0x00000A00,[0x50230]=0x00002494,
            [0x50254]=0x06040008,[0x50260]=0x3049C9A5,[0x50264]=0x21060138,
            [0x50DF0]=0x00020024
        };
        var raw = new Dictionary<uint,uint>();
        foreach(var p in bank) { raw.Add(p.Key,p.Value); raw.Add(p.Key|0x100000,p.Value); }
        var expected = new Dictionary<string,uint> {
            ["Cmd2T"]=0,["GDM"]=0,["PwrDwn"]=1,["tCKE"]=6,["tCL"]=17,["tCWL"]=12,
            ["tFAW"]=36,["tRAS"]=39,["tRC"]=56,["tRCDRD"]=17,["tRCDWR"]=17,
            ["tRDRDDD"]=3,["tRDRDSC"]=1,["tRDRDSCL"]=3,["tRDRDSD"]=3,["tRDWR"]=10,
            ["tREFI"]=9364,["tRFC"]=421,["tRFC2"]=313,["tRFC4"]=193,["tRP"]=17,
            ["tRRDL"]=8,["tRRDS"]=7,["tRTP"]=9,["tWR"]=18,["tWRRD"]=0,
            ["tWRWRDD"]=5,["tWRWRSC"]=1,["tWRWRSCL"]=3,["tWRWRSD"]=5,["tWTRL"]=9,["tWTRS"]=3
        };
        var snapshot = RunningTimings.Capture(new("AuthenticAMD",0x17,0x18),a=>raw[a],"M75n capture");
        Check(snapshot.Channels.Length==2,"M75n both UMCs");
        for (int i=0;i<2;i++)
        {
            var c=snapshot.Channels[i];
            Check(c.Index==i && c.MemoryType=="DDR4" && c.NominalMclkMHz==1200,"M75n channel identity and ratio");
            Check(c.Registers.Count==bank.Count && c.Timings.Count==expected.Count,"M75n complete capture");
            foreach(var p in bank)
                Check(c.Registers[$"0x{(p.Key|((uint)i<<20)):X8}"]==$"0x{p.Value:X8}","M75n register "+p.Key.ToString("X"));
            foreach(var p in expected)
                Check(c.Timings[p.Key]==p.Value,"M75n "+p.Key);
        }
    }
    // Selected words from the two gzip-validated M75n PM Table captures.
    // No complete telemetry dump is committed.
    private static void PowerTableCaptures()
    {
        var cpu=new RunningTimings.CpuId("AuthenticAMD",0x17,0x18);
        byte[] Make(uint fclk,uint mclk,uint vsoc)
        {
            var b=new byte[0x6AC];
            void Set(int offset,uint word)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset,4),word);
            Set(0x298,fclk); Set(0x29C,fclk); Set(0x2A0,mclk);
            Set(0x104,vsoc); Set(0xF0,0x3F4CCCCC);
            return b;
        }
        LinuxPowerTable.Sample Decode(byte[] b,uint version=0x1E0004,ulong size=0x6AC,string driver="0.1.2")
            =>LinuxPowerTable.Decode(cpu,driver,"03","1","4.30.72.0",version,size,b);
        var a=Make(0x440B222C,0x4451C3D8,0x3F378F80);
        var b=Make(0x440D4AA3,0x44524DB4,0x3F3A02EB);
        var p=Decode(a);var q=Decode(b);
        Check(Math.Abs(p.fclk_mhz-556.53394f)<0.001f && p.fclk_mhz==p.uclk_mhz,"PM capture 1 FCLK/UCLK");
        Check(Math.Abs(p.mclk_mhz-839.06006f)<0.001f && Math.Abs(p.vsoc_v-0.7170334f)<0.00001f,"PM capture 1 MCLK/VSOC");
        Check(Math.Abs(q.fclk_mhz-565.1662f)<0.001f && q.fclk_mhz==q.uclk_mhz,"PM capture 2 FCLK/UCLK");
        Check(Math.Abs(q.mclk_mhz-841.2141f)<0.001f && Math.Abs(q.vsoc_v-0.726607f)<0.00001f,"PM capture 2 MCLK/VSOC");
        Check(Math.Abs(p.cldo_vddp_v-0.8f)<0.00001f && p.cldo_vddp_v==q.cldo_vddp_v,"PM VDDP");
        Fails<NotSupportedException>(()=>Decode(a,version:0x1E0005),"unknown PM version rejected");
        Fails<NotSupportedException>(()=>Decode(a,driver:"0.1.3"),"unknown driver rejected");
        Fails<NotSupportedException>(()=>Decode(a,size:0x608),"unknown PM size rejected");
        Fails<InvalidDataException>(()=>Decode(a[..^1]),"truncated PM Table rejected");
        a[0x608]=1;
        Fails<InvalidDataException>(()=>Decode(a),"nonzero PM tail rejected");
        a[0x608]=0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0x298,4),0x7FC00000);
        Fails<InvalidDataException>(()=>Decode(a),"NaN clock rejected");
        Fails<NotSupportedException>(()=>LinuxPowerTable.Decode(new("AuthenticAMD",0x19,0x61),"0.1.2","03","1","4.30.72.0",0x1E0004,0x6AC,b),"wrong CPU rejected");
    }

    private static void Main()
    {
        M75nCapture();
        PowerTableCaptures();
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
