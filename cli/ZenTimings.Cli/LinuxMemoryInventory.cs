using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ZenStates.Core.Hardware.DRAM.DDR4.Spd;

namespace ZenTimings.Cli
{
    internal static class LinuxMemoryInventory
    {
        internal sealed record Module(string locator,string bank_locator,string manufacturer,string part_number,
            string memory_type,string form_factor,ulong? capacity_bytes,int? ranks,int? data_width_bits,
            int? total_width_bits,uint? rated_mts,uint? configured_mts,int? configured_mv);
        internal sealed record Spd(string device,string driver,string status,int? bytes,string memory_type,
            string revision,bool? base_crc_valid,bool? module_crc_valid,string part_number,
            string manufacturer_jep106,int? tck_min_ps,int? jedec_rate_from_tck_mts)
        {
            public Ddr4Details ddr4 { get; init; }
        }
        internal sealed record XmpProfile(int profile_number,int? speed_mts,int? voltage_mv,int? tck_min_ps,
            SortedDictionary<string,int> timings_clocks,SortedDictionary<string,int> timings_ps,int[] supported_cl);
        internal sealed record Ddr4Details(string status,string module_type,string module_manufacturer,string dram_manufacturer,
            long? capacity_mib,int? package_ranks,int? logical_ranks,int? device_width_bits,int? primary_bus_width_bits,
            int? ecc_extension_bits,int? die_density_mbit,int? die_count,int? bank_groups,int? banks_per_group,
            int? row_bits,int? column_bits,string package_type,bool is_3ds,bool is_asymmetric,bool has_thermal_sensor,
            int? jedec_speed_mts,int[] supported_cl,SortedDictionary<string,int> jedec_min_ps,
            SortedDictionary<string,int> jedec_timings_at_max_rate_clocks,string xmp_status,string xmp_revision,
            XmpProfile[] xmp_profiles);
        private static int? Positive(int x)=>x>0?x:null;
        private static string SafeText(string x)=>string.IsNullOrWhiteSpace(x)?null:
            new string(x.Trim().Select(c=>char.IsControl(c)?'?':c).ToArray());
        private static SortedDictionary<string,int> PositiveTimings(params (string key,int value)[] values)
        {
            var r=new SortedDictionary<string,int>(StringComparer.Ordinal);
            foreach(var p in values) if(p.value>0) r.Add(p.key,p.value);
            return r;
        }
        private static Ddr4Details DecodeDdr4(byte[] b,bool? moduleCrc)
        {
            // Only full images are eligible for module-specific/XMP decoding.
            // The upstream decoder pads shorter images; suppress derived fields
            // outside the bytes we actually acquired.
            bool full=b.Length==512 && moduleCrc==true;
            var d=Ddr4SpdDecoder.Decode(b,!full);
            if(!d.IsValid || !d.BaseCrcValid) return null;
            bool timing=b[17]==0;
            var profiles=full && d.HasXmp?d.XmpProfiles.Where(x=>x!=null && x.IsValid)
                .Select(x=>new XmpProfile(x.ProfileNumber,Positive((int)Math.Round(x.ClockMHz*2)),Positive(x.VddMv),Positive(x.tCKAVGminPs),
                    PositiveTimings(("tCL",x.CL),("tRCD",x.tRCD),("tRP",x.tRP),("tRAS",x.tRAS),("tRC",x.tRC),
                        ("tRFC",x.tRFC1),("tRFC2",x.tRFC2),("tRFC4",x.tRFC4),("tFAW",x.tFAW),
                        ("tRRDS",x.tRRD_S),("tRRDL",x.tRRD_L),("tCCDL",x.tCCD_L)),
                    PositiveTimings(("tAA",x.tAAminPs),("tRCD",x.tRCDminPs),("tRP",x.tRPminPs),
                        ("tRAS",x.tRASminPs),("tRC",x.tRCminPs),("tRFC",x.tRFC1minPs),("tRFC2",x.tRFC2minPs),
                        ("tRFC4",x.tRFC4minPs),("tFAW",x.tFAWminPs),("tRRDS",x.tRRD_SminPs),
                        ("tRRDL",x.tRRD_LminPs),("tCCDL",x.tCCD_LminPs)),x.SupportedCLs?.ToArray()??Array.Empty<int>()))
                .ToArray():Array.Empty<XmpProfile>();
            return new Ddr4Details(full?"full image; upstream decoder":"base fields only; module/XMP not verified",
                SafeText(d.ModuleTypeString),b.Length>=322?SafeText(d.ModuleManufacturer):null,
                b.Length>=352?SafeText(d.DramManufacturer):null,d.TotalCapacityMB>0?d.TotalCapacityMB:null,
                Positive(d.PackageRanks),Positive(d.LogicalRanks),Positive(d.DeviceWidthBits),Positive(d.PrimaryBusWidthBits),
                d.PrimaryBusWidthBits>0?d.BusWidthExtensionBits:null,Positive(d.DieDensityMbit),Positive(d.DieCount),
                Positive(d.BankGroups),Positive(d.BanksPerGroup),Positive(d.RowBits),Positive(d.ColumnBits),
                SafeText(d.PackageType),d.Is3DS,d.IsAsymmetric,d.HasThermalSensor,
                timing?Positive((int)Math.Round(d.ClockMHz*2)):null,d.SupportedCLs?.ToArray()??Array.Empty<int>(),
                timing?PositiveTimings(("tAA",d.tAAminPs),("tRCD",d.tRCDminPs),("tRP",d.tRPminPs),
                    ("tRAS",d.tRASminPs),("tRC",d.tRCminPs),("tRFC",d.tRFC1minPs),("tRFC2",d.tRFC2minPs),
                    ("tRFC4",d.tRFC4minPs),("tFAW",d.tFAWminPs),("tRRDS",d.tRRD_SminPs),
                    ("tRRDL",d.tRRD_LminPs),("tCCDL",d.tCCD_LminPs),("tWR",d.tWRminPs),
                    ("tWTRS",d.tWTR_SminPs),("tWTRL",d.tWTR_LminPs)):null,
                timing?PositiveTimings(("tCL",d.CL),("tRCD",d.tRCD),("tRP",d.tRP),("tRAS",d.tRAS),("tRC",d.tRC)):null,
                !full?"unavailable: incomplete image or invalid module CRC":d.HasXmp?"present (upstream decoded)":"not present",
                full?SafeText(d.XmpRevision):null,profiles);
        }
        internal sealed record Result(string smbios_status,Module[] modules,string spd_status,Spd[] spd,
            string note);

        // SMBIOS Type 17 formatted offsets. Never export serial number, asset tag,
        // raw DMI records or raw SPD (which contains serial numbers).
        internal static Module[] ParseDmi(byte[] table)
        {
            var result=new List<Module>();
            int p=0;
            while(p<table.Length)
            {
                if(table.Length-p<4) throw new InvalidDataException("Truncated SMBIOS header.");
                int type=table[p], len=table[p+1];
                if(len<4 || len>table.Length-p) throw new InvalidDataException("Invalid SMBIOS structure length.");
                int q=p+len;
                while(q+1<table.Length && (table[q]!=0 || table[q+1]!=0)) q++;
                if(q+1>=table.Length) throw new InvalidDataException("Unterminated SMBIOS string set.");
                if(type==127) return result.ToArray();
                if(type==17)
                {
                    if(len<0x15) throw new InvalidDataException("Truncated SMBIOS Type 17.");
                    byte B(int o)=>o<len?table[p+o]:(byte)0;
                    ushort W(int o)=>o+2<=len?BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(p+o,2)):(ushort)0;
                    uint D(int o)=>o+4<=len?BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(p+o,4)):0;
                    string Str(int o)
                    {
                        int index=B(o); if(index==0) return null;
                        int start=p+len;
                        for(int n=1;start<=q;n++)
                        {
                            int end=start;
                            while(end<=q && table[end]!=0) end++;
                            if(n==index)
                            {
                                string s=Encoding.UTF8.GetString(table,start,end-start).Trim();
                                // Terminal-safe even for malformed firmware strings.
                                return s.Length==0?null:new string(s.Select(c=>char.IsControl(c)?'?':c).ToArray());
                            }
                            start=end+1;
                        }
                        return null;
                    }
                    ushort size=W(0x0C);
                    if(size!=0)
                    {
                        ulong? bytes=size switch {
                            0xFFFF=>null,
                            0x7FFF=>(D(0x1C)&0x7FFFFFFF)==0?null:(ulong?)(D(0x1C)&0x7FFFFFFF)*1024*1024,
                            _=>(ulong)(size&0x7FFF)*((size&0x8000)!=0?1024UL:1048576UL)
                        };
                        uint? Speed(int o,int ext)
                        {
                            ushort v=W(o);
                            uint x=v==0xFFFF?D(ext):v;
                            return x==0 || x==uint.MaxValue?null:x;
                        }
                        int? Width(int o)=>W(o) is 0 or 0xFFFF?null:W(o);
                        int rank=B(0x1B)&15;
                        string memory=B(0x12) switch {0x18=>"DDR3",0x1A=>"DDR4",0x1E=>"LPDDR4",
                            0x22=>"DDR5",0x23=>"LPDDR5",_=>$"0x{B(0x12):X2}"};
                        string form=B(0x0E) switch {0x09=>"DIMM",0x0D=>"SODIMM",0x10=>"Die",_=>$"0x{B(0x0E):X2}"};
                        result.Add(new Module(Str(0x10),Str(0x11),Str(0x17),Str(0x1A),memory,form,
                            bytes,rank==0?null:rank,Width(0x0A),Width(0x08),Speed(0x15,0x54),
                            Speed(0x20,0x58),W(0x26) is 0 or 0xFFFF?null:W(0x26)));
                    }
                }
                p=q+2;
            }
            // A table without the terminal structure may be a partial read.
            throw new InvalidDataException("Missing SMBIOS end-of-table marker.");
        }

        private static byte[] ReadBounded(string path,int limit)
        {
            using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            var buffer=new byte[limit+1]; int n=0;
            while(n<buffer.Length)
            {
                int got=f.Read(buffer,n,buffer.Length-n);
                if(got==0) break;
                n+=got;
            }
            if(n>limit) throw new InvalidDataException("Input exceeds size limit.");
            return buffer[..n];
        }
        internal static ushort Crc16(ReadOnlySpan<byte> bytes)
        {
            ushort crc=0;
            foreach(byte b in bytes)
            {
                crc^=(ushort)(b<<8);
                for(int i=0;i<8;i++) crc=unchecked((ushort)((crc<<1)^((crc&0x8000)!=0?0x1021:0)));
            }
            return crc;
        }
        internal static Spd ParseSpd(string device,string driver,byte[] b)
        {
            bool ddr4=driver=="ee1004";
            int crcOffset=ddr4?126:510;
            string type=ddr4?"DDR4":"DDR5";
            Spd Error(string message,bool? crc=null)=>new(device,driver,message,b.Length,type,null,crc,null,null,null,null,null);
            if(driver!="ee1004" && driver!="spd5118") return Error("unsupported driver");
            if(b.Length<crcOffset+2 || b.Length>(ddr4?512:1024))
                return Error("truncated or oversized SPD");
            if(b[2]!=(ddr4?0x0C:0x12)) return Error("SPD type does not match driver");
            bool crc=Crc16(b.AsSpan(0,crcOffset))==BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(crcOffset,2));
            if(!crc) return Error("base CRC mismatch",false);
            bool? moduleCrc=ddr4 && b.Length>=256?Crc16(b.AsSpan(128,126))==
                BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(254,2)):null;
            int tck=ddr4?b[18]*125+unchecked((sbyte)b[125]):BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(20,2));
            // Report the raw JEDEC timing, not a guessed active speed. DDR4
            // uses the standard 125 ps MTB and 1 ps FTB only (byte 17 == 0).
            if(ddr4 && b[17]!=0) tck=0;
            int part=ddr4?329:521, partLen=ddr4?20:30, mfg=ddr4?320:512;
            string pn=null,id=null;
            if(b.Length>=part+partLen)
            {
                var span=b.AsSpan(part,partLen);
                if(span.ToArray().All(x=>x>=0x20 && x<=0x7E || x==0 || x==0xFF))
                    pn=Encoding.ASCII.GetString(span).Trim(' ','\0','?');
                if(string.IsNullOrWhiteSpace(pn)) pn=null;
            }
            if(b.Length>=mfg+2 && (b[mfg+1]&0x7F)!=0 && (b[mfg+1]&0x7F)!=0x7F)
                id=$"{b[mfg]&0x7F:X2}:{b[mfg+1]&0x7F:X2}";
            int? rate=tck>0?(int)Math.Round(2000000.0/tck):null;
            var result=new Spd(device,driver,"base CRC valid; partial field decode",b.Length,type,
                $"{b[1]>>4}.{b[1]&15}",true,moduleCrc,pn,id,tck>0?tck:null,rate);
            if(ddr4)
            {
                try
                {
                    var details=DecodeDdr4(b,moduleCrc);
                    if(details!=null) result=result with { status="base CRC valid; DDR4 decoded",ddr4=details };
                }
                catch(Exception ex) when(ex is ArgumentException || ex is IndexOutOfRangeException || ex is OverflowException)
                { result=result with { status="base CRC valid; extended DDR4 decode failed: "+ex.GetType().Name }; }
            }
            return result;
        }

        internal static Result Capture(string sysRoot)
        {
            var modules=Array.Empty<Module>(); string dmiStatus;
            try
            {
                modules=ParseDmi(ReadBounded(Path.Combine(sysRoot,"firmware/dmi/tables/DMI"),4*1024*1024));
                dmiStatus=modules.Length>0?"available":"no populated Type 17 records";
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            { dmiStatus=ex.GetType().Name+": "+ex.Message; }
            var spd=new List<Spd>(); string spdStatus;
            try
            {
                string root=Path.Combine(sysRoot,"bus/i2c/devices");
                foreach(string path in Directory.EnumerateDirectories(root).OrderBy(x=>x,StringComparer.Ordinal))
                {
                    string name=Path.GetFileName(path);
                    if(!Regex.IsMatch(name,@"^\d+-005[0-7]$")) continue;
                    string driverPath=Path.Combine(path,"driver");
                    string driver;
                    try { driver=Path.GetFileName(new DirectoryInfo(driverPath).ResolveLinkTarget(false)?.FullName); }
                    catch(IOException) { continue; }
                    if(driver!="ee1004" && driver!="spd5118") continue;
                    try
                    {
                        var b=ReadBounded(Path.Combine(path,"eeprom"),driver=="ee1004"?512:1024);
                        spd.Add(ParseSpd(name,driver,b));
                    }
                    catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                    { spd.Add(new Spd(name,driver,ex.GetType().Name+": "+ex.Message,null,null,null,null,null,null,null,null,null)); }
                }
                spdStatus=spd.Count==0?"no bound SPD EEPROM devices exposed":spd.Any(x=>x.base_crc_valid==true)?
                    "available (check per-device status)":"bound devices found but no valid SPD";
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            { spdStatus=ex.GetType().Name+": "+ex.Message; }
            return new Result(dmiStatus,modules,spdStatus,spd.ToArray(),
                "SMBIOS values are firmware-reported, not live sensor readings. SPD and DMI entries are not automatically matched to physical UMCs. No serials or raw bytes exported; no bus scans.");
        }
    }
}
