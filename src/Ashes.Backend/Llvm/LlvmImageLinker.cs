using System.Buffers.Binary;

namespace Ashes.Backend.Llvm;

internal static class LlvmImageLinker
{
    private const uint SectionTypeRela = 4;
    private const uint SectionTypeRel = 9;
    private const int PageSize = 0x1000;
    private const ulong ElfBaseVa = 0x400000;

    public static byte[] LinkLinuxExecutable(byte[] objectBytes)
    {
        var parsed = ParseElfObject(objectBytes);
        int textFileOffset = PageSize;
        int dataFileOffset = Align(textFileOffset + parsed.TextBytes.Length, PageSize);
        ulong textVa = ElfBaseVa + (ulong)textFileOffset;
        ulong dataVa = ElfBaseVa + (ulong)dataFileOffset;

        return Elf64ImageWriter.BuildTwoSegmentElf(
            textBytes: parsed.TextBytes,
            dataBytes: [],
            bssSize: 0,
            entryOffsetInText: parsed.EntryOffsetInText,
            textFileOff: textFileOffset,
            dataFileOff: dataFileOffset,
            textVA: textVa,
            dataVA: dataVa);
    }

    public static byte[] BuildMinimalWindowsStub()
    {
        var bytes = new byte[1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        bytes[0x82] = 0;
        bytes[0x83] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94), 0xF0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x96), 0x0022);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xA8), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xAC), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0xB0), 0x0000000140000000UL);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xB8), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xBC), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xD0), 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xD4), 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x104), 16);
        bytes[0x188] = (byte)'.';
        bytes[0x189] = (byte)'t';
        bytes[0x18A] = (byte)'e';
        bytes[0x18B] = (byte)'x';
        bytes[0x18C] = (byte)'t';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x190), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x194), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x198), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x19C), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1A4), 0x60000020);
        bytes[0x200] = 0xC3;
        return bytes;
    }

    private static ParsedElfObject ParseElfObject(byte[] objectBytes)
    {
        ReadOnlySpan<byte> bytes = objectBytes;
        if (bytes.Length < 64
            || bytes[0] != 0x7F
            || bytes[1] != (byte)'E'
            || bytes[2] != (byte)'L'
            || bytes[3] != (byte)'F')
        {
            throw new InvalidOperationException("LLVM did not emit a valid ELF object.");
        }

        if (bytes[4] != 2 || bytes[5] != 1)
        {
            throw new InvalidOperationException("Only ELF64 little-endian LLVM objects are supported.");
        }

        ulong sectionHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(40, 8));
        ushort sectionHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(58, 2));
        ushort sectionHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(60, 2));
        ushort sectionNamesIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(62, 2));

        if (sectionHeaderEntrySize < 64 || sectionHeaderCount == 0)
        {
            throw new InvalidOperationException("LLVM object is missing ELF section headers.");
        }

        var sections = new ElfSectionHeader[sectionHeaderCount];
        for (int i = 0; i < sectionHeaderCount; i++)
        {
            int offset = checked((int)sectionHeaderOffset + i * sectionHeaderEntrySize);
            sections[i] = new ElfSectionHeader(
                NameOffset: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)),
                Type: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4)),
                Offset: BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 24, 8)),
                Size: BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 32, 8)),
                Link: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 40, 4)),
                Info: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 44, 4)),
                EntrySize: BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 56, 8)));
        }

        var sectionNames = ReadStringTable(bytes, sections[sectionNamesIndex]);
        int textSectionIndex = -1;
        ElfSectionHeader? symtab = null;
        for (int i = 0; i < sections.Length; i++)
        {
            string name = ReadElfString(sectionNames, sections[i].NameOffset);
            if (name == ".text")
            {
                textSectionIndex = i;
            }
            else if (name == ".symtab")
            {
                symtab = sections[i];
            }
            else if ((sections[i].Type == SectionTypeRela || sections[i].Type == SectionTypeRel)
                     && sections[i].Info == (uint)textSectionIndex
                     && sections[i].Size != 0)
            {
                throw new InvalidOperationException("LLVM emitted text relocations that are not supported by the current ELF linker path.");
            }
        }

        if (textSectionIndex < 0)
        {
            throw new InvalidOperationException("LLVM object did not contain a .text section.");
        }

        if (symtab is null)
        {
            throw new InvalidOperationException("LLVM object did not contain a symbol table.");
        }

        var textSection = sections[textSectionIndex];
        byte[] textBytes = bytes.Slice(checked((int)textSection.Offset), checked((int)textSection.Size)).ToArray();

        var symbolStrings = ReadStringTable(bytes, sections[checked((int)symtab.Value.Link)]);
        int entryOffset = FindEntryOffset(bytes, symtab.Value, symbolStrings, textSectionIndex);
        return new ParsedElfObject(textBytes, entryOffset);
    }

    private static byte[] ReadStringTable(ReadOnlySpan<byte> bytes, ElfSectionHeader section)
    {
        return bytes.Slice(checked((int)section.Offset), checked((int)section.Size)).ToArray();
    }

    private static int FindEntryOffset(ReadOnlySpan<byte> bytes, ElfSectionHeader symtab, byte[] symbolStrings, int textSectionIndex)
    {
        if (symtab.EntrySize == 0)
        {
            throw new InvalidOperationException("LLVM symbol table is missing entry size metadata.");
        }

        int count = checked((int)(symtab.Size / symtab.EntrySize));
        for (int i = 0; i < count; i++)
        {
            int offset = checked((int)symtab.Offset + i * (int)symtab.EntrySize);
            uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            ushort sectionIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 6, 2));
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 8, 8));
            string name = ReadElfString(symbolStrings, nameOffset);
            if (name == "_start")
            {
                if (sectionIndex != textSectionIndex)
                {
                    throw new InvalidOperationException("LLVM entry symbol '_start' was not emitted in the .text section.");
                }

                return checked((int)value);
            }
        }

        throw new InvalidOperationException("LLVM object did not define the '_start' entry symbol.");
    }

    private static string ReadElfString(byte[] table, uint offset)
    {
        int start = checked((int)offset);
        int end = start;
        while (end < table.Length && table[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(table, start, end - start);
    }

    private static int Align(int value, int align)
    {
        int mask = align - 1;
        return (value + mask) & ~mask;
    }

    private readonly record struct ParsedElfObject(byte[] TextBytes, int EntryOffsetInText);

    private readonly record struct ElfSectionHeader(
        uint NameOffset,
        uint Type,
        ulong Offset,
        ulong Size,
        uint Link,
        uint Info,
        ulong EntrySize);
}
