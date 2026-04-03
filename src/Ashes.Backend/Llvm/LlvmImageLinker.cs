using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using LibObjectFile.PE;

namespace Ashes.Backend.Llvm;

internal static class LlvmImageLinker
{
    private const ulong PeImageBase = 0x0000000000400000UL;
    private const uint SectionTypeRela = 4;
    private const uint SectionTypeRel = 9;
    private const int PageSize = 0x1000;
    private const ulong ElfBaseVa = 0x400000;
    private const int LinuxTrampolineLength = 20;
    private const uint PeTextRva = 0x00001000;
    private const uint PeSectionAlignment = 0x00001000;
    private const int WindowsTrampolineLength = 24;
    private const uint ElfRelocX86_64_32 = 10;
    private const uint ElfRelocX86_64_32S = 11;
    private const ushort CoffRelocAmd64Addr32 = 0x0002;

    public static byte[] LinkLinuxExecutable(byte[] objectBytes, string entrySymbolName)
    {
        ulong textVa = ElfBaseVa + (ulong)PageSize;
        ulong objectTextVa = textVa + LinuxTrampolineLength;
        var parsed = ParseElfObject(objectBytes, entrySymbolName, objectTextVa);
        int textFileOffset = PageSize;
        byte[] codeBytes = BuildLinuxTrampoline(parsed.EntryOffsetInText)
            .Concat(parsed.TextBytes)
            .ToArray();
        int dataFileOffset = Align(textFileOffset + codeBytes.Length, PageSize);
        ulong dataVa = ElfBaseVa + (ulong)dataFileOffset;

        return Elf64ImageWriter.BuildTwoSegmentElf(
            textBytes: codeBytes,
            dataBytes: [],
            bssSize: 0,
            entryOffsetInText: 0,
            textFileOff: textFileOffset,
            dataFileOff: dataFileOffset,
            textVA: textVa,
            dataVA: dataVa);
    }

    public static byte[] LinkWindowsExecutable(byte[] objectBytes, string entrySymbolName)
    {
        var parsed = ParseCoffObject(objectBytes, entrySymbolName);

        var rdata = new PEStreamSectionData();
        Align(rdata, 2);
        int kernelNameOffset = (int)rdata.Stream.Position;
        rdata.Stream.Write(Encoding.ASCII.GetBytes("KERNEL32.DLL\0"));
        var kernelName = new PEAsciiStringLink(rdata, new RVO((uint)kernelNameOffset));
        var exitProcessHintName = WriteImportHintName(rdata, 0, "ExitProcess");
        Align(rdata, 8);
        int iatSectionOffset = (int)rdata.Stream.Length;

        var exitProcessIat = new PEImportAddressTable64() { exitProcessHintName };
        var iatDirectory = new PEImportAddressTableDirectory() { exitProcessIat };
        var exitProcessIlt = new PEImportLookupTable64() { exitProcessHintName };
        var importDirectory = new PEImportDirectory
        {
            Entries =
            {
                new PEImportDirectoryEntry(kernelName, exitProcessIat, exitProcessIlt)
            }
        };

        uint rdataRva = AlignUp(checked(PeTextRva + (uint)(WindowsTrampolineLength + parsed.TextBytes.Length)), PeSectionAlignment);
        ulong exitProcessIatVa = PeImageBase + rdataRva + (ulong)iatSectionOffset;
        byte[] codeBytes = BuildWindowsTrampoline(parsed.EntryOffsetInText, parsed.TextBytes.Length, exitProcessIatVa)
            .Concat(parsed.TextBytes)
            .ToArray();

        var pe = new PEFile();
        var textSection = pe.AddSection(PESectionName.Text, PeTextRva);
        var rdataSection = pe.AddSection(PESectionName.RData, rdataRva);

        var code = new PEStreamSectionData();
        code.Stream.Write(codeBytes);
        textSection.Content.Add(code);
        rdataSection.Content.Add(rdata);
        rdataSection.Content.Add(iatDirectory);
        rdataSection.Content.Add(exitProcessIlt);
        rdataSection.Content.Add(importDirectory);

        pe.OptionalHeader.AddressOfEntryPoint = new(code, 0);
        pe.OptionalHeader.BaseOfCode = textSection;
        pe.OptionalHeader.DllCharacteristics =
            DllCharacteristics.NxCompatible |
            DllCharacteristics.TerminalServerAware;

        using var output = new MemoryStream();
        pe.Write(output, new() { EnableStackTrace = true });
        return output.ToArray();
    }

    private static ParsedElfObject ParseElfObject(byte[] objectBytes, string entrySymbolName, ulong loadedTextVa)
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
        var textRelocationSections = sections
            .Where(static section => (section.Type == SectionTypeRela || section.Type == SectionTypeRel) && section.Size != 0)
            .Where(section => section.Info == (uint)textSectionIndex)
            .ToList();

        var symbolStrings = ReadStringTable(bytes, sections[checked((int)symtab.Value.Link)]);
        int entryOffset = FindEntryOffset(bytes, symtab.Value, symbolStrings, textSectionIndex, entrySymbolName);
        ApplyElfTextRelocations(bytes, textBytes, textRelocationSections, symtab.Value, textSectionIndex, loadedTextVa);
        return new ParsedElfObject(textBytes, entryOffset);
    }

    private static void ApplyElfTextRelocations(
        ReadOnlySpan<byte> objectBytes,
        byte[] textBytes,
        List<ElfSectionHeader> relocationSections,
        ElfSectionHeader symtab,
        int textSectionIndex,
        ulong loadedTextVa)
    {
        foreach (ElfSectionHeader relocationSection in relocationSections)
        {
            if (relocationSection.EntrySize == 0)
            {
                throw new InvalidOperationException("LLVM ELF relocation section is missing entry size metadata.");
            }

            int count = checked((int)(relocationSection.Size / relocationSection.EntrySize));
            for (int i = 0; i < count; i++)
            {
                int offset = checked((int)relocationSection.Offset + i * (int)relocationSection.EntrySize);
                ulong relocOffset = BinaryPrimitives.ReadUInt64LittleEndian(objectBytes.Slice(offset, 8));
                ulong info = BinaryPrimitives.ReadUInt64LittleEndian(objectBytes.Slice(offset + 8, 8));
                long addend = relocationSection.Type == SectionTypeRela
                    ? BinaryPrimitives.ReadInt64LittleEndian(objectBytes.Slice(offset + 16, 8))
                    : 0;

                int symbolIndex = checked((int)(info >> 32));
                uint relocationType = unchecked((uint)info);
                ElfSymbol symbol = ReadElfSymbol(objectBytes, symtab, symbolIndex);
                if (symbol.SectionIndex != textSectionIndex)
                {
                    throw new InvalidOperationException("LLVM ELF text relocation targeted an unsupported non-text symbol.");
                }

                long targetVa = checked((long)(loadedTextVa + symbol.Value) + addend);
                Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                switch (relocationType)
                {
                    case ElfRelocX86_64_32:
                        BinaryPrimitives.WriteUInt32LittleEndian(patch, checked((uint)targetVa));
                        break;
                    case ElfRelocX86_64_32S:
                        BinaryPrimitives.WriteInt32LittleEndian(patch, checked((int)targetVa));
                        break;
                    default:
                        throw new InvalidOperationException($"LLVM ELF emitted unsupported .text relocation type {relocationType}.");
                }
            }
        }
    }

    private static ElfSymbol ReadElfSymbol(ReadOnlySpan<byte> bytes, ElfSectionHeader symtab, int symbolIndex)
    {
        if (symtab.EntrySize == 0)
        {
            throw new InvalidOperationException("LLVM symbol table is missing entry size metadata.");
        }

        int offset = checked((int)symtab.Offset + symbolIndex * (int)symtab.EntrySize);
        return new ElfSymbol(
            SectionIndex: BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 6, 2)),
            Value: BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 8, 8)));
    }

    private static byte[] ReadStringTable(ReadOnlySpan<byte> bytes, ElfSectionHeader section)
    {
        return bytes.Slice(checked((int)section.Offset), checked((int)section.Size)).ToArray();
    }

    private static int FindEntryOffset(ReadOnlySpan<byte> bytes, ElfSectionHeader symtab, byte[] symbolStrings, int textSectionIndex, string entrySymbolName)
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
            if (name == entrySymbolName)
            {
                if (sectionIndex != textSectionIndex)
                {
                    throw new InvalidOperationException($"LLVM entry symbol '{entrySymbolName}' was not emitted in the .text section.");
                }

                return checked((int)value);
            }
        }

        throw new InvalidOperationException($"LLVM object did not define the '{entrySymbolName}' entry symbol.");
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

    private static ParsedCoffObject ParseCoffObject(byte[] objectBytes, string entrySymbolName)
    {
        ReadOnlySpan<byte> bytes = objectBytes;
        if (bytes.Length < 20)
        {
            throw new InvalidOperationException("LLVM did not emit a valid COFF object.");
        }

        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        uint symbolTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
        uint symbolCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));

        var sections = new CoffSectionHeader[sectionCount];
        int textSectionIndex = -1;
        for (int i = 0; i < sectionCount; i++)
        {
            int offset = 20 + (i * 40);
            string name = ReadCoffName(bytes.Slice(offset, 8), bytes, symbolTableOffset, symbolCount);
            sections[i] = new CoffSectionHeader(
                Name: name,
                SizeOfRawData: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 16, 4)),
                PointerToRawData: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 20, 4)),
                PointerToRelocations: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 24, 4)),
                NumberOfRelocations: BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 32, 2)));

            if (name == ".text")
            {
                textSectionIndex = i;
            }
        }

        if (textSectionIndex < 0)
        {
            throw new InvalidOperationException("LLVM COFF object did not contain a .text section.");
        }

        CoffSectionHeader textSection = sections[textSectionIndex];
        byte[] textBytes = bytes.Slice(checked((int)textSection.PointerToRawData), checked((int)textSection.SizeOfRawData)).ToArray();
        ApplyCoffTextRelocations(bytes, textBytes, textSection, symbolTableOffset, symbolCount, textSectionIndex + 1);
        int entryOffset = FindCoffSymbolOffset(bytes, symbolTableOffset, symbolCount, sections, entrySymbolName, textSectionIndex + 1);
        return new ParsedCoffObject(textBytes, entryOffset);
    }

    private static void ApplyCoffTextRelocations(
        ReadOnlySpan<byte> bytes,
        byte[] textBytes,
        CoffSectionHeader textSection,
        uint symbolTableOffset,
        uint symbolCount,
        int textSectionNumber)
    {
        for (int i = 0; i < textSection.NumberOfRelocations; i++)
        {
            int offset = checked((int)textSection.PointerToRelocations + i * 10);
            uint relocationOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            int symbolIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4)));
            ushort relocationType = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 8, 2));
            CoffSymbol symbol = ReadCoffSymbol(bytes, symbolTableOffset, symbolIndex);
            if (symbol.SectionNumber != textSectionNumber)
            {
                throw new InvalidOperationException("LLVM COFF text relocation targeted an unsupported non-text symbol.");
            }

            switch (relocationType)
            {
                case CoffRelocAmd64Addr32:
                    uint targetVa = checked((uint)(PeImageBase + PeTextRva + (uint)WindowsTrampolineLength + symbol.Value));
                    BinaryPrimitives.WriteUInt32LittleEndian(textBytes.AsSpan(checked((int)relocationOffset), 4), targetVa);
                    break;
                default:
                    throw new InvalidOperationException($"LLVM COFF emitted unsupported .text relocation type 0x{relocationType:X4}.");
            }
        }
    }

    private static int FindCoffSymbolOffset(
        ReadOnlySpan<byte> bytes,
        uint symbolTableOffset,
        uint symbolCount,
        CoffSectionHeader[] sections,
        string entrySymbolName,
        int expectedSectionNumber)
    {
        int stringTableOffset = checked((int)(symbolTableOffset + symbolCount * 18));
        for (int symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
        {
            int offset = checked((int)symbolTableOffset + (symbolIndex * 18));
            string name = ReadCoffName(bytes.Slice(offset, 8), bytes, symbolTableOffset, symbolCount);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 8, 4));
            short sectionNumber = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset + 12, 2));
            byte auxCount = bytes[offset + 17];

            if (name == entrySymbolName)
            {
                if (sectionNumber != expectedSectionNumber)
                {
                    throw new InvalidOperationException($"LLVM COFF symbol '{entrySymbolName}' was not emitted in the .text section.");
                }

                return checked((int)value);
            }

            symbolIndex += auxCount;
        }

        throw new InvalidOperationException($"LLVM COFF object did not define symbol '{entrySymbolName}'.");
    }

    private static CoffSymbol ReadCoffSymbol(ReadOnlySpan<byte> bytes, uint symbolTableOffset, int symbolIndex)
    {
        int offset = checked((int)symbolTableOffset + (symbolIndex * 18));
        return new CoffSymbol(
            Value: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 8, 4)),
            SectionNumber: BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset + 12, 2)));
    }

    private static string ReadCoffName(ReadOnlySpan<byte> nameBytes, ReadOnlySpan<byte> fileBytes, uint symbolTableOffset, uint symbolCount)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(nameBytes[..4]) == 0)
        {
            int stringTableOffset = checked((int)(symbolTableOffset + symbolCount * 18));
            int nameOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(nameBytes[4..8]));
            int start = stringTableOffset + nameOffset;
            int end = start;
            while (end < fileBytes.Length && fileBytes[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(fileBytes.Slice(start, end - start));
        }

        int length = 0;
        while (length < 8 && nameBytes[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(nameBytes[..length]);
    }

    private static byte[] BuildWindowsTrampoline(int entryOffsetInText, int textLength, ulong exitProcessIatVa)
    {
        var bytes = new byte[WindowsTrampolineLength];
        int index = 0;
        bytes[index++] = 0x48;
        bytes[index++] = 0x83;
        bytes[index++] = 0xEC;
        bytes[index++] = 0x28;
        bytes[index++] = 0xE8;
        int relativeCall = checked(bytes.Length + entryOffsetInText - 9);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index, 4), relativeCall);
        index += 4;
        bytes[index++] = 0x31;
        bytes[index++] = 0xC9;
        bytes[index++] = 0x48;
        bytes[index++] = 0xB8;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(index, 8), exitProcessIatVa);
        index += 8;
        bytes[index++] = 0xFF;
        bytes[index++] = 0x10;
        bytes[index] = 0xCC;
        return bytes;
    }

    private static byte[] BuildLinuxTrampoline(int entryOffsetInText)
    {
        var bytes = new byte[LinuxTrampolineLength];
        int index = 0;
        bytes[index++] = 0x48;
        bytes[index++] = 0x89;
        bytes[index++] = 0xE7;
        bytes[index++] = 0xE8;
        int relativeCall = checked(LinuxTrampolineLength + entryOffsetInText - 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index, 4), relativeCall);
        index += 4;
        bytes[index++] = 0xBF;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index, 4), 0);
        index += 4;
        bytes[index++] = 0xB8;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index, 4), 60);
        index += 4;
        bytes[index++] = 0x0F;
        bytes[index] = 0x05;
        return bytes;
    }

    private static void Align(PEStreamSectionData stream, int align)
    {
        long pos = stream.Stream.Position;
        long pad = (align - (pos % align)) % align;
        for (int i = 0; i < pad; i++)
        {
            stream.Stream.WriteByte(0);
        }
    }

    private static PEImportHintNameLink WriteImportHintName(PEStreamSectionData stream, ushort hint, string name)
    {
        Align(stream, 2);
        int offset = (int)stream.Stream.Position;
        stream.Stream.WriteByte((byte)(hint & 0xFF));
        stream.Stream.WriteByte((byte)((hint >> 8) & 0xFF));
        stream.Stream.Write(Encoding.ASCII.GetBytes(name));
        stream.Stream.WriteByte(0);
        if (stream.Stream.Position % 2 != 0)
        {
            stream.Stream.WriteByte(0);
        }

        return new PEImportHintNameLink(stream, new RVO((uint)offset));
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private readonly record struct ParsedElfObject(byte[] TextBytes, int EntryOffsetInText);
    private readonly record struct ParsedCoffObject(byte[] TextBytes, int EntryOffsetInText);

    private readonly record struct ElfSectionHeader(
        uint NameOffset,
        uint Type,
        ulong Offset,
        ulong Size,
        uint Link,
        uint Info,
        ulong EntrySize);

    private readonly record struct ElfSymbol(
        ushort SectionIndex,
        ulong Value);

    private readonly record struct CoffSectionHeader(
        string Name,
        uint SizeOfRawData,
        uint PointerToRawData,
        uint PointerToRelocations,
        ushort NumberOfRelocations);

    private readonly record struct CoffSymbol(
        uint Value,
        short SectionNumber);
}
