using System;
using System.Buffers.Binary;

namespace ZenStates.Core
{
    // The linked APOB entry enumerator requires only this byte-array helper.
    internal static class Utils
    {
        internal static uint ReadUInt32(byte[] data,uint offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)offset),4));
    }
}
