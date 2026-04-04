using System.Buffers.Binary;
using System.Text;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmImageLinker
{
    private const ulong ElfSectionFlagAlloc = 0x2;
    private const uint SectionTypeRela = 4;
    private const uint SectionTypeNoBits = 8;
    private const uint SectionTypeRel = 9;
    private const int PageSize = 0x1000;
    private const ulong ElfBaseVa = 0x400000;
    private const int LinuxTrampolineLength = 20;
    private const uint ElfRelocX86_64Pc32 = 2;
    private const uint ElfRelocX86_64_32 = 10;
    private const uint ElfRelocX86_64_32S = 11;

    public static byte[] LinkLinuxExecutable(byte[] objectBytes, string entrySymbolName)
    {
        ulong textVa = ElfBaseVa + (ulong)PageSize;
        ulong objectTextVa = textVa + LinuxTrampolineLength;
        var parsed = ParseElfObject(objectBytes, entrySymbolName);
        int textFileOffset = PageSize;
        int codeLength = LinuxTrampolineLength + parsed.TextBytes.Length;
        int dataFileOffset = Align(textFileOffset + codeLength, PageSize);
        ulong dataVa = ElfBaseVa + (ulong)dataFileOffset;
        var laidOutData = LayoutElfAllocatedSections(parsed.AllocatedSections, dataVa);
        ApplyElfTextRelocations(
            objectBytes,
            parsed.TextBytes,
            parsed.RelocationSections,
            parsed.SymbolTable,
            parsed.TextSectionIndex,
            objectTextVa,
            laidOutData.SectionBaseVas);

        byte[] codeBytes = BuildLinuxTrampoline(parsed.EntryOffsetInText)
            .Concat(parsed.TextBytes)
            .ToArray();

        return Elf64ImageWriter.BuildTwoSegmentElf(
            textBytes: codeBytes,
            dataBytes: laidOutData.DataBytes,
            bssSize: 0,
            entryOffsetInText: 0,
            textFileOff: textFileOffset,
            dataFileOff: dataFileOffset,
            textVA: textVa,
            dataVA: dataVa);
    }

    private static ParsedElfObject ParseElfObject(byte[] objectBytes, string entrySymbolName)
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
                Flags: BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 8, 8)),
                Offset: BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 24, 8)),
                Size: BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 32, 8)),
                Link: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 40, 4)),
                Info: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 44, 4)),
                AddressAlign: BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 48, 8)),
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
        var allocatedSections = new List<ElfAllocatedSection>();
        for (int i = 0; i < sections.Length; i++)
        {
            if (i == textSectionIndex)
            {
                continue;
            }

            ElfSectionHeader section = sections[i];
            if (section.Size == 0 || (section.Flags & ElfSectionFlagAlloc) == 0)
            {
                continue;
            }

            allocatedSections.Add(new ElfAllocatedSection(
                SectionIndex: i,
                Bytes: section.Type == SectionTypeNoBits
                    ? new byte[checked((int)section.Size)]
                    : bytes.Slice(checked((int)section.Offset), checked((int)section.Size)).ToArray(),
                Alignment: section.AddressAlign));
        }

        int entryOffset = FindEntryOffset(bytes, symtab.Value, symbolStrings, textSectionIndex, entrySymbolName);
        return new ParsedElfObject(
            TextBytes: textBytes,
            EntryOffsetInText: entryOffset,
            RelocationSections: textRelocationSections,
            SymbolTable: symtab.Value,
            TextSectionIndex: textSectionIndex,
            AllocatedSections: allocatedSections);
    }

    private static void ApplyElfTextRelocations(
        ReadOnlySpan<byte> objectBytes,
        byte[] textBytes,
        List<ElfSectionHeader> relocationSections,
        ElfSectionHeader symtab,
        int textSectionIndex,
        ulong loadedTextVa,
        IReadOnlyDictionary<int, ulong> sectionBaseVas)
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
                long targetVa = checked((long)ResolveElfTargetVa(symbol, textSectionIndex, loadedTextVa, sectionBaseVas) + addend);
                long placeVa = checked((long)loadedTextVa + (long)relocOffset);
                Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                switch (relocationType)
                {
                    case ElfRelocX86_64Pc32:
                        BinaryPrimitives.WriteInt32LittleEndian(patch, checked((int)(targetVa - placeVa)));
                        break;
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

    private static ulong ResolveElfTargetVa(
        ElfSymbol symbol,
        int textSectionIndex,
        ulong loadedTextVa,
        IReadOnlyDictionary<int, ulong> sectionBaseVas)
    {
        if (symbol.SectionIndex == textSectionIndex)
        {
            return loadedTextVa + symbol.Value;
        }

        if (sectionBaseVas.TryGetValue(symbol.SectionIndex, out ulong sectionBaseVa))
        {
            return sectionBaseVa + symbol.Value;
        }

        throw new InvalidOperationException($"LLVM ELF text relocation targeted unsupported section {symbol.SectionIndex}.");
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

        return Encoding.ASCII.GetString(table, start, end - start);
    }

    private static LaidOutElfSections LayoutElfAllocatedSections(
        IReadOnlyList<ElfAllocatedSection> sections,
        ulong dataVa)
    {
        if (sections.Count == 0)
        {
            return new LaidOutElfSections([], new Dictionary<int, ulong>());
        }

        using var stream = new MemoryStream();
        var sectionBaseVas = new Dictionary<int, ulong>();
        foreach (ElfAllocatedSection section in sections)
        {
            int alignment = checked((int)Math.Max(1, Math.Min(section.Alignment, (ulong)int.MaxValue)));
            int alignedOffset = Align(checked((int)stream.Position), alignment);
            while (stream.Position < alignedOffset)
            {
                stream.WriteByte(0);
            }

            sectionBaseVas[section.SectionIndex] = dataVa + (ulong)stream.Position;
            stream.Write(section.Bytes);
        }

        return new LaidOutElfSections(stream.ToArray(), sectionBaseVas);
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

    private readonly record struct ParsedElfObject(
        byte[] TextBytes,
        int EntryOffsetInText,
        List<ElfSectionHeader> RelocationSections,
        ElfSectionHeader SymbolTable,
        int TextSectionIndex,
        List<ElfAllocatedSection> AllocatedSections);

    private readonly record struct ElfSectionHeader(
        uint NameOffset,
        uint Type,
        ulong Flags,
        ulong Offset,
        ulong Size,
        uint Link,
        uint Info,
        ulong AddressAlign,
        ulong EntrySize);

    private readonly record struct ElfSymbol(
        ushort SectionIndex,
        ulong Value);

    private readonly record struct ElfAllocatedSection(
        int SectionIndex,
        byte[] Bytes,
        ulong Alignment);

    private readonly record struct LaidOutElfSections(
        byte[] DataBytes,
        Dictionary<int, ulong> SectionBaseVas);
}
