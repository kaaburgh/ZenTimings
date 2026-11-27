using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ZenStates.Core.Hardware.DRAM.DDR4.Spd;
using ZenStates.Core.Hardware.DRAM.DDR5.Spd;

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
            public Ddr5Details ddr5 { get; init; }
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
        internal sealed record Ddr5Profile(string kind,int profile_number,int? speed_mts,int? tck_min_ps,
            int? vdd_mv,int? vddq_mv,int? vpp_mv,SortedDictionary<string,int> timings_ps,
            SortedDictionary<string,int> refresh_ns,SortedDictionary<string,int> timings_clocks,int[] supported_cl);
        internal sealed record Ddr5Details(string status,string module_type,string module_manufacturer,string dram_manufacturer,
            long? capacity_mib,int? ranks_per_channel,int? logical_ranks_per_channel,int? subchannels_per_dimm,
            int? primary_bus_width_bits,int? ecc_extension_bits,int? device_width_bits,int? die_density_mbit,
            int? die_count,int? bank_groups,int? banks_per_group,int? row_bits,int? column_bits,
            bool is_asymmetric,bool has_thermal_sensor,int? jedec_speed_mts,int[] supported_cl,
            SortedDictionary<string,int> jedec_min_ps,SortedDictionary<string,int> jedec_refresh_ns,
            SortedDictionary<string,int> jedec_timings_at_max_rate_clocks,string xmp_status,string xmp_revision,
            string expo_status,string expo_revision,bool? expo_crc_valid,Ddr5Profile[] profiles);

        private static Ddr5Details DecodeDdr5(byte[] b)
        {
            bool full=b.Length==1024;
            var d=Ddr5SpdDecoder.Decode(b,!full);
            if(!d.IsValid || (full && !d.BaseCrcValid)) return null;
            var profiles=new List<Ddr5Profile>();
            // Base CRC does not cover profile bytes. EXPO has its own CRC;
            // upstream XMP decoder does not verify a profile checksum.
            if(full && d.HasXmp)
                foreach(var x in d.XmpProfiles.Where(x=>x!=null && x.IsValid))
                    profiles.Add(new Ddr5Profile("XMP",x.ProfileNumber,Positive(x.SpeedMTs),Positive(x.tCKAVGminPs),
                        Positive(x.VddMv),Positive(x.VddqMv),Positive(x.VppMv),
                        PositiveTimings(("tAA",x.tAAminPs),("tRCD",x.tRCDminPs),("tRP",x.tRPminPs),
                            ("tRAS",x.tRASminPs),("tRC",x.tRCminPs),("tWR",x.tWRminPs)),
                        PositiveTimings(("tRFC",x.tRFC1minNs),("tRFC2",x.tRFC2minNs),("tRFCsb",x.tRFCsbMinNs)),
                        PositiveTimings(("tCL",x.CL),("tRCD",x.tRCD),("tRP",x.tRP)),
                        x.SupportedCLs?.ToArray()??Array.Empty<int>()));
            if(full && d.HasExpo && d.ExpoCrcValid)
                foreach(var x in new[]{d.ExpoProfile1,d.ExpoProfile2}.Where(x=>x!=null && x.IsValid))
                    profiles.Add(new Ddr5Profile("EXPO",x.ProfileNumber,Positive(x.SpeedMTs),Positive(x.tCKAVGminPs),
                        Positive(x.VddMv),Positive(x.VddqMv),Positive(x.VppMv),
                        PositiveTimings(("tAA",x.tAAminPs),("tRCD",x.tRCDminPs),("tRP",x.tRPminPs),
                            ("tRAS",x.tRASminPs),("tRC",x.tRCminPs),("tWR",x.tWRminPs),
                            ("tRRDL",x.tRRD_L?.Ps??0),("tCCDL",x.tCCD_L?.Ps??0),
                            ("tCCDL_WR",x.tCCD_L_WR?.Ps??0),("tCCDL_WR2",x.tCCD_L_WR2?.Ps??0),
                            ("tFAW",x.tFAW?.Ps??0),("tCCDL_WTR",x.tCCD_L_WTR?.Ps??0),
                            ("tCCDS_WTR",x.tCCD_S_WTR?.Ps??0),("tRTP",x.tRTP?.Ps??0)),
                        PositiveTimings(("tRFC",x.tRFC1minNs),("tRFC2",x.tRFC2minNs),("tRFCsb",x.tRFCsbMinNs)),
                        PositiveTimings(("tCL",x.CL),("tRCD",x.tRCD),("tRP",x.tRP),("tRAS",x.tRAS),
                            ("tRC",x.tRC),("tWR",x.tWR),("tRRDL",x.tRRD_L?.Nck??0),("tCCDL",x.tCCD_L?.Nck??0),
                            ("tCCDL_WR",x.tCCD_L_WR?.Nck??0),("tCCDL_WR2",x.tCCD_L_WR2?.Nck??0),
                            ("tFAW",x.tFAW?.Nck??0),("tCCDL_WTR",x.tCCD_L_WTR?.Nck??0),
                            ("tCCDS_WTR",x.tCCD_S_WTR?.Nck??0),("tRTP",x.tRTP?.Nck??0)),Array.Empty<int>()));
            return new Ddr5Details(full?"full image; upstream decoder":"base fields only",
                SafeText(d.ModuleTypeString),b.Length>=514?SafeText(d.ModuleManufacturer):null,
                b.Length>=554?SafeText(d.DramManufacturer):null,d.TotalCapacityMB>0?d.TotalCapacityMB:null,
                Positive(d.RanksPerChannel),Positive(d.LogicalRanksPerChannel),Positive(d.SubChannelsPerDimm),
                Positive(d.PrimaryBusWidthBits),d.PrimaryBusWidthBits>0?d.BusWidthExtensionBits:null,
                Positive(d.FirstDeviceWidthBits),Positive(d.FirstDieDensityMbit),Positive(d.FirstDieCount),
                Positive(d.FirstBankGroups),Positive(d.FirstBanksPerBankGroup),Positive(d.FirstRowBits),Positive(d.FirstColumnBits),
                d.IsAsymmetric,d.HasThermalSensor,Positive(d.SpeedMTs),d.SupportedCLs?.ToArray()??Array.Empty<int>(),
                PositiveTimings(("tAA",d.tAAminPs),("tRCD",d.tRCDminPs),("tRP",d.tRPminPs),("tRAS",d.tRASminPs),
                    ("tRC",d.tRCminPs),("tWR",d.tWRminPs)),
                PositiveTimings(("tRFC",d.tRFC1minNs),("tRFC2",d.tRFC2minNs),("tRFCsb",d.tRFCsbMinNs)),
                PositiveTimings(("tCL",d.CL),("tRCD",d.tRCD),("tRP",d.tRP),("tRAS",d.tRAS),("tRC",d.tRC),("tWR",d.tWR)),
                !full?"unavailable: incomplete image":d.HasXmp?"present; checksum not verified":"not present",
                full?SafeText(d.XmpRevision):null,
                !full?"unavailable: incomplete image":!d.HasExpo?"not present":d.ExpoCrcValid?"CRC valid":"CRC mismatch; profiles omitted",
                full?SafeText(d.ExpoRevision):null,full && d.HasExpo?d.ExpoCrcValid:null,profiles.ToArray());
        }
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
        internal sealed record DimmTemperature(string device,string driver,int? temperature_millicelsius,
            string status);
        internal sealed record Result(string smbios_status,Module[] modules,string spd_status,Spd[] spd,
            string note)
        {
            public DimmTemperature[] dimm_temperatures { get; init; } = Array.Empty<DimmTemperature>();
        }

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
            else
            {
                try
                {
                    var details=DecodeDdr5(b);
                    if(details!=null) result=result with { status="base CRC valid; DDR5 decoded",ddr5=details };
                }
                catch(Exception ex) when(ex is ArgumentException || ex is IndexOutOfRangeException || ex is OverflowException)
                { result=result with { status="base CRC valid; extended DDR5 decode failed: "+ex.GetType().Name }; }
            }
            return result;
        }

        // Only a hwmon child of the already-bound SPD5118 I2C device.
        // Do not read alarm files: those may clear sticky status on read.
        private static DimmTemperature ReadTemperature(string path,string device)
        {
            DimmTemperature Result(int? value,string status)=>new(device,"spd5118",value,status);
            try
            {
                var sensors=new List<string>();
                foreach(var hw in Directory.EnumerateDirectories(Path.Combine(path,"hwmon")))
                {
                    if(!Regex.IsMatch(Path.GetFileName(hw),@"\Ahwmon[0-9]+\z")) continue;
                    if(Encoding.ASCII.GetString(ReadBounded(Path.Combine(hw,"name"),32)).Trim()=="spd5118")
                        sensors.Add(hw);
                }
                if(sensors.Count!=1)
                    return Result(null,sensors.Count==0?"no SPD5118 hwmon sensor":"ambiguous SPD5118 hwmon sensors");
                string raw=Encoding.ASCII.GetString(ReadBounded(Path.Combine(sensors[0],"temp1_input"),32)).Trim();
                // Linux SPD5118: signed 11-bit measurement, 250 millidegrees C per step.
                if(!int.TryParse(raw,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int value) ||
                    value < -256000 || value > 255750 || value%250!=0)
                    return Result(null,"invalid temperature reading");
                return Result(value,"available");
            }
            catch(DirectoryNotFoundException) { return Result(null,"hwmon unavailable"); }
            catch(FileNotFoundException) { return Result(null,"temperature attribute unavailable"); }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            { return Result(null,ex.GetType().Name+": temperature unavailable"); }
        }

        // Enumerate existing bound clients only, never instantiate or probe
        // an I2C address. Shared by inventory and temperature-only capture.
        private static IEnumerable<(string path,string name,string driver)> BoundSpdDevices(string sysRoot)
        {
            string root=Path.Combine(sysRoot,"bus/i2c/devices");
            foreach(string path in Directory.EnumerateDirectories(root).OrderBy(x=>x,StringComparer.Ordinal))
            {
                string name=Path.GetFileName(path);
                if(!Regex.IsMatch(name,@"\A[0-9]+-005[0-7]\z")) continue;
                string driver;
                try { driver=Path.GetFileName(new DirectoryInfo(Path.Combine(path,"driver")).ResolveLinkTarget(false)?.FullName); }
                catch(IOException) { continue; }
                if(driver=="ee1004" || driver=="spd5118") yield return (path,name,driver);
            }
        }

        internal sealed record TemperatureResult(bool available,string status,DimmTemperature[] sensors,string note);

        internal static TemperatureResult CaptureTemperatures(string sysRoot)
        {
            var sensors=new List<DimmTemperature>();
            const string note="SPD5118 hub sensor temperatures from already-bound Linux hwmon devices; no EEPROM read, DMI, slot mapping, bus scan or alarm read.";
            try
            {
                foreach(var (path,name,driver) in BoundSpdDevices(sysRoot))
                    if(driver=="spd5118") sensors.Add(ReadTemperature(path,name));
                bool available=sensors.Any(s=>s.temperature_millicelsius.HasValue);
                return new TemperatureResult(available,available?"available (check per-device status)":
                    sensors.Count==0?"no bound SPD5118 devices":"no valid SPD5118 temperature",sensors.ToArray(),note);
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            { return new TemperatureResult(false,ex.GetType().Name+": sensor enumeration unavailable",Array.Empty<DimmTemperature>(),note); }
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
            var spd=new List<Spd>(); var temperatures=new List<DimmTemperature>(); string spdStatus;
            try
            {
                foreach(var (path,name,driver) in BoundSpdDevices(sysRoot))
                {
                    if(driver=="spd5118") temperatures.Add(ReadTemperature(path,name));
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
                "SMBIOS values are firmware-reported. SPD5118 temperatures are kernel sensor readings, not SPD data. SPD and DMI entries are not automatically matched to physical UMCs. No serials or raw bytes exported; no bus scans.")
                { dimm_temperatures=temperatures.ToArray() };
        }
    }
}
