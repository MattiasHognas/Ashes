using System.Buffers.Binary;

namespace Ashes.Backend;

public static class Elf64ImageWriter
{
    private const int EhdrSize = 64;
    private const int PhdrSize = 56;
    private const int PhdrCount = 2;

    public static byte[] BuildTwoSegmentElf(
        byte[] textBytes,
        byte[] dataBytes,
        int bssSize,
        int entryOffsetInText,
        int textFileOff,
        int dataFileOff,
        ulong textVA,
        ulong dataVA)
    {
        int fileSize = dataFileOff + dataBytes.Length;
        var file = new byte[fileSize];

        // e_ident
        file[0] = 0x7F; file[1] = (byte)'E'; file[2] = (byte)'L'; file[3] = (byte)'F';
        file[4] = 2; // 64-bit
        file[5] = 1; // LE
        file[6] = 1; // version
        file[7] = 0; // SYSV

        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(16), 2);  // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(18), 62); // EM_X86_64
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 1);  // version

        ulong entryVA = textVA + (ulong)entryOffsetInText;
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(24), entryVA);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(32), 64); // phoff
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(40), 0);  // shoff
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(48), 0);  // flags
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(52), (ushort)EhdrSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(54), (ushort)PhdrSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(56), (ushort)PhdrCount);

        int ph0 = 64;
        int ph1 = 64 + PhdrSize;

        WritePhdr(file.AsSpan(ph0),
            p_type: 1,
            p_flags: 5,
            p_offset: (ulong)textFileOff,
            p_vaddr: textVA,
            p_paddr: textVA,
            p_filesz: (ulong)textBytes.Length,
            p_memsz: (ulong)textBytes.Length,
            p_align: (ulong)0x1000);

        WritePhdr(file.AsSpan(ph1),
            p_type: 1,
            p_flags: 6,
            p_offset: (ulong)dataFileOff,
            p_vaddr: dataVA,
            p_paddr: dataVA,
            p_filesz: (ulong)dataBytes.Length,
            p_memsz: (ulong)dataBytes.Length + (ulong)bssSize,
            p_align: (ulong)0x1000);

        Buffer.BlockCopy(textBytes, 0, file, textFileOff, textBytes.Length);
        Buffer.BlockCopy(dataBytes, 0, file, dataFileOff, dataBytes.Length);

        return file;
    }

    private static void WritePhdr(
        Span<byte> ph,
        uint p_type,
        uint p_flags,
        ulong p_offset,
        ulong p_vaddr,
        ulong p_paddr,
        ulong p_filesz,
        ulong p_memsz,
        ulong p_align)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(ph.Slice(0), p_type);
        BinaryPrimitives.WriteUInt32LittleEndian(ph.Slice(4), p_flags);
        BinaryPrimitives.WriteUInt64LittleEndian(ph.Slice(8), p_offset);
        BinaryPrimitives.WriteUInt64LittleEndian(ph.Slice(16), p_vaddr);
        BinaryPrimitives.WriteUInt64LittleEndian(ph.Slice(24), p_paddr);
        BinaryPrimitives.WriteUInt64LittleEndian(ph.Slice(32), p_filesz);
        BinaryPrimitives.WriteUInt64LittleEndian(ph.Slice(40), p_memsz);
        BinaryPrimitives.WriteUInt64LittleEndian(ph.Slice(48), p_align);
    }
}
