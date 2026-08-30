using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace DS3Minimap.Memory;

internal sealed class MemoryReader(SafeProcessHandle process)
{
    public byte[] ReadBytes(nint address, int count)
    {
        if (address == 0) throw new ArgumentOutOfRangeException(nameof(address), "A process-memory address cannot be zero.");
        if (count <= 0 || count > 128 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(count), count, "A process-memory read must be between 1 byte and 128 MiB.");
        var buffer = new byte[count];
        if (!NativeMethods.ReadProcessMemory(process, address, buffer, (nuint)count, out var bytesRead) || bytesRead != (nuint)count)
            throw new InvalidOperationException($"ReadProcessMemory failed at 0x{address:X}.");
        return buffer;
    }

    public nint ReadPointer(nint address)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(address, sizeof(long)));
        if (value < 0x10000 || value > 0x00007FFFFFFFFFFF) return 0;
        return (nint)value;
    }

    public float ReadSingle(nint address) => BitConverter.ToSingle(ReadBytes(address, sizeof(float)));
    public int ReadInt32(nint address) => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(address, sizeof(int)));
    public uint ReadUInt32(nint address) => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(address, sizeof(uint)));
    public byte ReadByte(nint address) => ReadBytes(address, 1)[0];
}
