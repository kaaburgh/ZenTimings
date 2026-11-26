using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ZenTimings.Common
{
    // No device access in this class. Accept only the observed DRFXI v0x18
    // family and the two checksummed directory copies. Never emit raw bytes,
    // identity words, hashes, date values, paths or register values.
    internal static class DrfxiApobComparison
    {
        internal const int RamAddress = 0x0A200000;
        internal const int NvOffset = 0x125000;
        internal const int NvCapacity = 0x28000;
        private const int ImageSize = 32 * 1024 * 1024;
        private const int HeaderSize = 0xA8;
        private const int EntryHeaderSize = 48;

        [DataContract]
        internal sealed class Record
        {
            [DataMember] public string name;
            [DataMember] public string status;
            [DataMember] public bool? header_equal;
            [DataMember] public bool? payload_equal;
            [DataMember] public int? changed_payload_bytes;
            [DataMember] public bool? training_date_word_equal;
        }

        [DataContract]
        internal sealed class Report
        {
            [DataMember] public int schema_version = 1;
            [DataMember] public string purpose = "DRFXI RAM versus persisted APOB; equality only";
            [DataMember] public string observation_source;
            [DataMember] public int persisted_table_bytes;
            [DataMember] public int observed_table_bytes;
            [DataMember] public bool table_equal;
            [DataMember] public bool header_equal;
            [DataMember] public bool entry_layout_equal;
            [DataMember] public bool same_spd_identities;
            [DataMember] public Record[] known_records;
            [DataMember] public string note = "Same-board candidate, not proof of freshness or MCR execution. No raw data, paths, hashes, date or identity values exported. HMAC not verified.";

            internal string ToJson()
            {
                var serializer = new DataContractJsonSerializer(typeof(Report));
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, this);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }

        private sealed class Entry
        {
            internal int Offset, Size;
            internal uint Group, Type, Instance;
            internal string Key { get { return (Group & 65535).ToString(CultureInfo.InvariantCulture) + "/" +
                (Type & 65535).ToString(CultureInfo.InvariantCulture) + "/" + Instance.ToString(CultureInfo.InvariantCulture); } }
            internal bool Plain { get { return Group <= 65535 && Type <= 65535; } }
        }

        private static readonly Tuple<uint,uint,int,string>[] Known = {
            Tuple.Create(1u,16u,120,"APCB boot info"),
            Tuple.Create(1u,2u,732,"MEM configuration"),
            Tuple.Create(1u,25u,96,"electrical"),
            Tuple.Create(7u,3u,6020,"GEN configuration"),
            Tuple.Create(7u,4u,15312,"UMC replay"),
            Tuple.Create(1u,5u,16432,"PMU SMB"),
            Tuple.Create(1u,18u,140,"MBIST"),
            Tuple.Create(7u,6u,1076,"event log"),
            Tuple.Create(1u,17u,4196,"SPD"),
            Tuple.Create(1u,22u,1012,"training failure"),
            Tuple.Create(1u,30u,10320,"PHY phase 0"),
            Tuple.Create(1u,31u,10320,"PHY phase 1")
        };

        private static ushort U16(byte[] b,int p) => (ushort)(b[p] | b[p+1]<<8);
        private static uint U32(byte[] b,int p) => U16(b,p) | ((uint)U16(b,p+2)<<16);
        private static ulong U64(byte[] b,int p) => U32(b,p) | ((ulong)U32(b,p+4)<<32);
        private static bool Equal(byte[] a,int x,byte[] b,int y,int n)
        {
            for(int i=0;i<n;i++) if(a[x+i]!=b[y+i]) return false;
            return true;
        }
        private static int Changed(byte[] a,int x,byte[] b,int y,int n)
        {
            int count=0;
            for(int i=0;i<n;i++) if(a[x+i]!=b[y+i]) count++;
            return count;
        }
        private static byte[] ReadExactly(Stream s,int count)
        {
            byte[] b=new byte[count];
            int i=0;
            while(i<count)
            {
                int n=s.Read(b,i,count-i);
                if(n==0) throw new InvalidDataException("Short image read.");
                i+=n;
            }
            return b;
        }

        private static uint Fletcher32(byte[] b,int start,int length)
        {
            uint a=65535,z=65535;
            for(int i=start;i<start+length;i+=2)
            {
                a=(a+U16(b,i))%65535; z=(z+a)%65535;
            }
            return ((z==0?65535:z)<<16)|(a==0?65535:a);
        }

        private static void ValidateDirectory(byte[] b,int p)
        {
            if(U32(b,p)!=0x324C4224 || U32(b,p+8)!=18 ||
                U32(b,p+4)!=Fletcher32(b,p+8,8+24*18))
                throw new InvalidDataException("Expected checksummed DRFXI BL2 directory missing.");
            int ram=0,nv=0;
            for(int i=0;i<18;i++)
            {
                int e=p+16+24*i;
                uint t=U32(b,e),s=U32(b,e+4);
                ulong src=U64(b,e+8),dst=U64(b,e+16);
                if((t&255)==0x61)
                {
                    if(t!=0x61 || s!=0 || src!=0 || dst!=RamAddress)
                        throw new InvalidDataException("Unexpected APOB RAM directory entry.");
                    ram++;
                }
                if((t&255)==0x63)
                {
                    if(t!=0x20000063 || s!=NvCapacity ||
                        src!=(0x4000000000000000UL|NvOffset) || dst!=ulong.MaxValue)
                        throw new InvalidDataException("Unexpected APOB NV directory entry.");
                    nv++;
                }
            }
            if(ram!=1 || nv!=1)
                throw new InvalidDataException("Ambiguous or missing APOB directory entry.");
        }

        internal static int GetTableSize(byte[] header)
        {
            if(header==null || header.Length<16 || U32(header,0)!=0x424F5041 ||
                U32(header,4)!=0x18 || U32(header,12)!=HeaderSize)
                throw new InvalidDataException("Expected DRFXI APOB v0x18 / 0xA8 header.");
            uint n=U32(header,8);
            if(n<HeaderSize+2*EntryHeaderSize || n>NvCapacity)
                throw new InvalidDataException("APOB length outside observed region.");
            return (int)n;
        }

        private static List<Entry> Parse(byte[] b)
        {
            if(GetTableSize(b)!=b.Length) throw new InvalidDataException("APOB length mismatch.");
            var entries=new List<Entry>();
            var keys=new HashSet<string>(StringComparer.Ordinal);
            int p=HeaderSize;
            while(p<b.Length)
            {
                if(b.Length-p<EntryHeaderSize || entries.Count>=256)
                    throw new InvalidDataException("Truncated or excessive APOB entries.");
                uint g=U32(b,p),t=U32(b,p+4),i=U32(b,p+8),s=U32(b,p+12);
                if((g&65535)<1 || (g&65535)>9 || (t&65535)==0 ||
                    s<EntryHeaderSize || s>b.Length-p)
                    throw new InvalidDataException("Invalid APOB entry.");
                var e=new Entry {Offset=p,Size=(int)s,Group=g,Type=t,Instance=i};
                if(!keys.Add(e.Key)) throw new InvalidDataException("Ambiguous APOB entry key.");
                entries.Add(e); p+=(int)s;
            }
            return entries;
        }

        private static Entry Find(List<Entry> entries,uint group,uint type,int size)
        {
            return entries.SingleOrDefault(e=>e.Plain && e.Group==group && e.Type==type &&
                e.Instance==0 && e.Size==size);
        }

        // Only validated 2-channel DDR5 SPD topology, with nonblank identity.
        private static int[] SpdOffsets(byte[] b,List<Entry> entries)
        {
            var e=Find(entries,1,17,4196);
            if(e==null) throw new InvalidDataException("Known DDR5 SPD entry not found.");
            int p=e.Offset+EntryHeaderSize;
            if(b[p]!=1 || b[p+1]!=2) throw new InvalidDataException("Unexpected SPD topology.");
            int[] result={-1,-1};
            for(int i=0;i<2;i++)
            {
                int o=p+4+i*1036,d=o+12;
                int ch=b[o+9];
                if(b[o+1]!=1 || b[o+8]!=0 || b[o+10]!=0 || ch>1 || result[ch]!=-1 || b[d+2]!=0x12)
                    throw new InvalidDataException("Unexpected SPD slot.");
                int crc=0;
                for(int j=0;j<510;j++)
                {
                    crc^=b[d+j]<<8;
                    for(int k=0;k<8;k++) crc=((crc&0x8000)!=0?(crc<<1)^0x1021:crc<<1)&65535;
                }
                if(crc!=U16(b,d+510)) throw new InvalidDataException("DDR5 SPD CRC mismatch.");
                uint serial=U32(b,d+517);
                if(serial==0 || serial==uint.MaxValue)
                    throw new InvalidDataException("Blank SPD identity; comparison not corroborated.");
                result[ch]=d;
            }
            return result;
        }

        internal static byte[] ReadPersisted(string path)
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(file.Length!=ImageSize) throw new InvalidDataException("Expected a 32 MiB DRFXI image file.");
                byte[] image=ReadExactly(file,ImageSize);
                if(file.ReadByte()!=-1) throw new InvalidDataException("Image length changed.");
                ValidateDirectory(image,0x51D000);
                ValidateDirectory(image,0x8E5000);
                var header=new byte[16];
                Buffer.BlockCopy(image,NvOffset,header,0,16);
                var table=new byte[GetTableSize(header)];
                Buffer.BlockCopy(image,NvOffset,table,0,table.Length);
                var entries=Parse(table);
                // The persisted reference is the precise known family.
                if(table.Length!=72576 || entries.Count!=26 ||
                    Known.Any(k=>Find(entries,k.Item1,k.Item2,k.Item3)==null))
                    throw new InvalidDataException("Unsupported persisted DRFXI APOB layout.");
                SpdOffsets(table,entries);
                return table;
            }
        }

        internal static byte[] ReadTableFile(string path)
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                int n=GetTableSize(ReadExactly(file,16));
                if(file.Length!=n) throw new InvalidDataException("APOB file length mismatch.");
                file.Position=0;
                byte[] result=ReadExactly(file,n);
                if(file.ReadByte()!=-1) throw new InvalidDataException("APOB file length changed.");
                return result;
            }
        }

        internal static Report Compare(byte[] persisted,byte[] observed,string source)
        {
            if(source!="Windows cached APOB" && source!="Linux reserved memory (two equal reads)" &&
                source!="offline APOB file")
                throw new ArgumentException("Unsupported observation source.");
            var p=Parse(persisted);var q=Parse(observed);
            if(persisted.Length!=72576 || p.Count!=26 ||
                Known.Any(k=>Find(p,k.Item1,k.Item2,k.Item3)==null))
                throw new InvalidDataException("Unsupported persisted reference layout.");
            var a=SpdOffsets(persisted,p);var b=SpdOffsets(observed,q);
            // Compare identities locally, including part number, module/DRAM
            // manufacturer and serial. Never emit their values.
            bool identities=true;
            for(int i=0;i<2;i++)
                identities &= Equal(persisted,a[i]+512,observed,b[i]+512,42);
            if(!identities) throw new InvalidDataException("DDR5 SPD identities do not match reference.");
            var rows=new List<Record>();
            foreach(var k in Known)
            {
                var x=Find(p,k.Item1,k.Item2,k.Item3);
                var y=Find(q,k.Item1,k.Item2,k.Item3);
                var r=new Record {name=k.Item4,status=y==null?"missing or unsupported":"compared"};
                if(y!=null)
                {
                    r.header_equal=Equal(persisted,x.Offset,observed,y.Offset,EntryHeaderSize);
                    int u=x.Offset+EntryHeaderSize,v=y.Offset+EntryHeaderSize,n=x.Size-EntryHeaderSize;
                    r.payload_equal=Equal(persisted,u,observed,v,n);
                    // Do not report count or positions for identity-bearing records.
                    if(k.Item1==1 && k.Item2==16)
                        r.training_date_word_equal=Equal(persisted,u+56,observed,v+56,4);
                    else if(!(k.Item1==1 && k.Item2==17))
                        r.changed_payload_bytes=Changed(persisted,u,observed,v,n);
                }
                rows.Add(r);
            }
            bool layout=p.Count==q.Count && p.Zip(q,(x,y)=>x.Offset==y.Offset &&
                x.Size==y.Size && x.Group==y.Group && x.Type==y.Type && x.Instance==y.Instance).All(x=>x);
            return new Report {observation_source=source,persisted_table_bytes=persisted.Length,
                observed_table_bytes=observed.Length,table_equal=persisted.SequenceEqual(observed),
                header_equal=Equal(persisted,0,observed,0,HeaderSize),entry_layout_equal=layout,
                same_spd_identities=identities,known_records=rows.ToArray()};
        }
    }
}
