using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace ZenTimings.Cli
{
    // ryzen_smu's 4-byte store requests a READ; an 8-byte store writes a
    // hardware register. Never expose an 8-byte operation or arbitrary addresses.
    internal sealed class RyzenSmuReader
    {
        private const string PathName="/sys/kernel/ryzen_smu_drv/smn";
        public uint Read(uint address)
        {
            // Only the two client UMC ranges; no arbitrary raw-register CLI.
            if ((address & 3)!=0 || (address & 0xFFE00000)!=0 ||
                (address & 0xFFFFF)<0x50000 || (address & 0xFFFFF)>0x50DF4)
                throw new ArgumentOutOfRangeException(nameof(address));
            byte[] request=new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(request,address);
            // Single syscall, not a buffered stream or partial-write retry.
            using(var file=File.OpenHandle(PathName,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))
            {
                nint n=Write(file.DangerousGetHandle(),request,4);
                if(n!=4) throw new IOException($"SMN read request failed (count {n}, errno {Marshal.GetLastPInvokeError()}).");
            }
            using var response=new FileStream(PathName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            byte[] data=new byte[4];
            response.ReadExactly(data);
            if(response.ReadByte()!=-1) throw new IOException("Unexpected SMN response size.");
            return BinaryPrimitives.ReadUInt32LittleEndian(data);
        }
        [DllImport("libc",EntryPoint="write",SetLastError=true)]
        private static extern nint Write(IntPtr fd,byte[] bytes,nuint count);
    }
}
