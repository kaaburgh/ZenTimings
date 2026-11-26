using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZenTimings.Cli
{
    // File-only comparison of last observed replay words and a previously
    // captured CLI snapshot. Never execute replay or acquire hardware data.
    internal static class ApobUmcCorrelation
    {
        internal sealed record Snapshot(int Type, Dictionary<int,Dictionary<uint,uint>> Channels);
        internal sealed record Fields(int compared, int equal, string[] differing_fields);
        internal sealed record Channel(int umc_address_index, string status, int compared_registers,
            int equal_registers, string[] differing_register_addresses, string[] replay_only_addresses,
            string[] running_only_addresses, bool? config_bit31_only_difference,
            double? replay_nominal_mclk_mhz, double? running_nominal_mclk_mhz,
            Fields timing_fields, Fields extended_fields);
        internal sealed record Result(bool available, string status, string replay_layout, Channel[] channels)
        {
            public string note => "Last observed boot replay words vs a supplied historical UMC report, by exact SMN address. " +
                "Neither file is authenticated; same machine, DIMM mapping, capture time, MCR execution and active refresh rank are not established. " +
                "Whole-register equality includes non-timing bits. No raw words, filenames or private identifiers exported.";
        }

        private static Result Unavailable(string status) => new(false,status,null,Array.Empty<Channel>());
        private static string Hex(uint v) => $"0x{v:X8}";
        private static uint Word(string s)
        {
            if(s==null || s.Length!=10 || !s.StartsWith("0x",StringComparison.Ordinal) ||
                !uint.TryParse(s.AsSpan(2),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out uint v))
                throw new InvalidDataException("Invalid UMC hex word.");
            return v;
        }

        private static JsonElement Property(JsonElement obj,string name)
        {
            if(obj.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Expected UMC report object.");
            var entries=obj.EnumerateObject().Where(p=>p.NameEquals(name)).ToArray();
            if(entries.Length!=1) throw new InvalidDataException("Missing or duplicate report field.");
            return entries[0].Value;
        }

        internal static Snapshot ParseReport(JsonElement obj)
        {
            if(Property(obj,"schema_version").GetInt32()!=1 ||
                !Property(obj,"running_available").GetBoolean() || !Property(obj,"experimental").GetBoolean() ||
                Property(obj,"source").GetString()!="ryzen_smu")
                throw new InvalidDataException("Expected a successful schema-1 ryzen_smu CLI snapshot.");
            var cpu=Property(obj,"cpu");
            int type=RunningTimings.MemoryType(new RunningTimings.CpuId(Property(cpu,"Vendor").GetString(),
                Property(cpu,"Family").GetInt32(),Property(cpu,"Model").GetInt32()));
            if(type<0) throw new InvalidDataException("Unsupported UMC report CPU.");
            var basic=new HashSet<uint>(RunningTimings.TimingAddresses(type).Concat(new uint[]{0x50000,0x50008,0x50DF0}));
            var extra=new HashSet<uint>(ExtendedUmc.Addresses(type));
            var channels=new Dictionary<int,Dictionary<uint,uint>>();
            foreach(var ch in Property(obj,"channels").EnumerateArray())
            {
                int index=Property(ch,"channel").GetInt32();
                if(index<0 || index>1 || channels.ContainsKey(index) ||
                    Property(ch,"memory_type").GetString()!=(type==0?"DDR4":"DDR5"))
                    throw new InvalidDataException("Unexpected or duplicate UMC index/type.");
                uint offset=(uint)index<<20;
                var words=new Dictionary<uint,uint>();
                void ReadRegisters(JsonElement regs,HashSet<uint> allow,bool extended)
                {
                    var seen=new HashSet<uint>();
                    foreach(var item in regs.EnumerateObject())
                    {
                        uint address=Word(item.Name),word=Word(item.Value.GetString());
                        uint local=address^offset;
                        if(!allow.Contains(local) || !seen.Add(local) || word==uint.MaxValue)
                            throw new InvalidDataException("Unsupported, duplicate or invalid UMC register.");
                        if(words.TryGetValue(local,out uint previous))
                        {
                            if(!extended || previous!=word) throw new InvalidDataException("Conflicting UMC register.");
                        }
                        else words.Add(local,word);
                    }
                    if(!extended && !seen.SetEquals(allow))
                        throw new InvalidDataException("Incomplete basic UMC snapshot.");
                }
                ReadRegisters(Property(ch,"registers"),basic,false);
                var ext=Property(ch,"extended");
                if(ext.ValueKind!=JsonValueKind.Null)
                    ReadRegisters(Property(ext,"registers"),extra,true);
                if((words[0x50100]&3)!=type || (words[0x50DF0]&(1u<<19))!=0 ||
                    ((words[0x50000]|words[0x50008])&1)==0)
                    throw new InvalidDataException("UMC presence or DRAM type mismatch.");
                var decoded=RunningTimings.DecodeKnownFields(type,words);
                double mclk=RunningTimings.NominalMclk(type,words[0x50200]);
                if(decoded["tCL"]<5 || decoded["tRCDRD"]<5 || decoded["tRP"]<5 ||
                    decoded["tRAS"]<10 || mclk<200 || mclk>10000)
                    throw new InvalidDataException("Implausible UMC report timings or ratio.");
                channels.Add(index,words);
            }
            if(channels.Count==0) throw new InvalidDataException("No populated UMC in report.");
            return new Snapshot(type,channels);
        }

        internal static Result CompareFile(LinuxApob.Result boot,string path)
        {
            if(!boot.available || boot.replay==null ||
                (boot.version==0x18 && boot.ddr5_config?.replay_correlation==null))
                return Unavailable("No independently validated bounded APOB replay.");
            try
            {
                using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                long n=file.Length;
                if(n<2 || n>4*1024*1024) throw new InvalidDataException("UMC report outside size limit.");
                byte[] bytes=new byte[(int)n];file.ReadExactly(bytes);
                if(file.ReadByte()!=-1) throw new InvalidDataException("UMC report grew during read.");
                using var doc=JsonDocument.Parse(bytes);
                return Compare(boot.replay,ParseReport(doc.RootElement));
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException ||
                ex is InvalidDataException || ex is JsonException || ex is InvalidOperationException ||
                ex is FormatException || ex is KeyNotFoundException || ex is NotSupportedException)
            {
                // Do not return exception text, which can include local paths or input.
                return Unavailable("Invalid or unreadable UMC report ("+ex.GetType().Name+").");
            }
        }

        internal static Result Compare(ApobReplay.Result replay,Snapshot snapshot)
        {
            int type=replay.layout switch {
                "picasso-v0x13"=>0,
                "ddr5-donor-v0x18" or "ddr5-v0x18-same-layout-candidate"=>1,
                _=>-1
            };
            if(type<0 || type!=snapshot.Type)
                return Unavailable("UMC CPU/map and bounded replay layout do not agree.");
            var observed=replay.channels.ToDictionary(c=>c.umc_address_index);
            var rows=new List<Channel>();
            foreach(int i in observed.Keys.Union(snapshot.Channels.Keys).OrderBy(x=>x))
            {
                uint offset=(uint)i<<20;
                var a=observed.TryGetValue(i,out var c)?
                    c.observed_registers.ToDictionary(p=>Word(p.Key)^offset,p=>Word(p.Value.last_word)):
                    new Dictionary<uint,uint>();
                // All-ones is not evidence of a successful register observation.
                foreach(uint key in a.Where(p=>p.Value==uint.MaxValue).Select(p=>p.Key).ToArray()) a.Remove(key);
                var b=snapshot.Channels.TryGetValue(i,out var live)?live:new Dictionary<uint,uint>();
                string[] Addresses(IEnumerable<uint> values)=>values.OrderBy(x=>x).Select(x=>Hex(x|offset)).ToArray();
                var both=a.Keys.Intersect(b.Keys).ToArray();
                var diff=both.Where(k=>a[k]!=b[k]).ToArray();
                Fields CompareFields(SortedDictionary<string,uint> x,SortedDictionary<string,uint> y)
                {
                    var common=x.Keys.Intersect(y.Keys,StringComparer.Ordinal).ToArray();
                    var different=common.Where(k=>x[k]!=y[k]).OrderBy(k=>k,StringComparer.Ordinal).ToArray();
                    return new Fields(common.Length,common.Length-different.Length,different);
                }
                bool? bit31=a.TryGetValue(0x50100,out uint ac) && b.TryGetValue(0x50100,out uint bc)
                    ? (ac^bc)==0x80000000u : null;
                double? Ratio(Dictionary<uint,uint> x)=>x.TryGetValue(0x50200,out uint value)?RunningTimings.NominalMclk(type,value):null;
                rows.Add(new Channel(i,both.Length>0?"compared":a.Count==0?"no replay block":"no running block",
                    both.Length,both.Length-diff.Length,Addresses(diff),Addresses(a.Keys.Except(b.Keys)),
                    Addresses(b.Keys.Except(a.Keys)),bit31,Ratio(a),Ratio(b),
                    CompareFields(RunningTimings.DecodeKnownFields(type,a),RunningTimings.DecodeKnownFields(type,b)),
                    CompareFields(ExtendedUmc.DecodeKnownFields(type,a),ExtendedUmc.DecodeKnownFields(type,b))));
            }
            bool available=rows.Any(c=>c.compared_registers>0);
            return new Result(available,available?"Compared address intersections; differences are valid results.":"No shared register observations.",replay.layout,rows.ToArray());
        }
    }
}
