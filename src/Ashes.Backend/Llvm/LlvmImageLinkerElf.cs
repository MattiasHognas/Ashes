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

    private const int ElfHeaderSize = 64;
    private const int ElfProgramHeaderSize = 56;

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

        bool hasData = laidOutData.DataBytes.Length > 0;
        bool hasDebug = parsed.DebugSections.Count > 0;
        int programHeaderCount = hasData ? 2 : 1;

        int endOfLoadable = hasData
            ? dataFileOffset + laidOutData.DataBytes.Length
            : textFileOffset + codeBytes.Length;

        // Layout debug sections after loadable content
        var debugFileOffsets = new List<int>();
        int debugCursor = endOfLoadable;
        foreach (var debugSection in parsed.DebugSections)
        {
            int align = checked((int)Math.Max(1, Math.Min(debugSection.Alignment, (ulong)int.MaxValue)));
            debugCursor = Align(debugCursor, align);
            debugFileOffsets.Add(debugCursor);
            debugCursor += debugSection.Bytes.Length;
        }

        // Build section header string table (.shstrtab)
        int shstrtabOffset = 0;
        byte[] shstrtabBytes = [];
        int sectionHeaderCount = 0;
        int shstrtabIndex = 0;
        int sectionHeaderOffset = 0;

        if (hasDebug)
        {
            // Section names: null, .text, (.data if present), .debug_*, .shstrtab
            var shstrtab = new MemoryStream();
            shstrtab.WriteByte(0); // null name at offset 0

            var nameOffsets = new Dictionary<string, int>(StringComparer.Ordinal);

            nameOffsets[".text"] = (int)shstrtab.Position;
            shstrtab.Write(Encoding.ASCII.GetBytes(".text\0"));

            if (hasData)
            {
                nameOffsets[".data"] = (int)shstrtab.Position;
                shstrtab.Write(Encoding.ASCII.GetBytes(".data\0"));
            }

            foreach (var debugSection in parsed.DebugSections)
            {
                nameOffsets[debugSection.Name] = (int)shstrtab.Position;
                shstrtab.Write(Encoding.ASCII.GetBytes(debugSection.Name + "\0"));
            }

            nameOffsets[".shstrtab"] = (int)shstrtab.Position;
            shstrtab.Write(Encoding.ASCII.GetBytes(".shstrtab\0"));

            shstrtabBytes = shstrtab.ToArray();
            shstrtabOffset = debugCursor;

            // Section header count: null + .text + (.data?) + debug sections + .shstrtab
            sectionHeaderCount = 1 + 1 + (hasData ? 1 : 0) + parsed.DebugSections.Count + 1;
            shstrtabIndex = sectionHeaderCount - 1; // .shstrtab is last

            sectionHeaderOffset = Align(shstrtabOffset + shstrtabBytes.Length, 8);
        }

        int totalSize = hasDebug
            ? sectionHeaderOffset + sectionHeaderCount * ElfSectionHeaderSize
            : endOfLoadable;

        var output = new byte[totalSize];
        WriteElf64Header(output, textVa, programHeaderCount,
            sectionHeaderOffset: hasDebug ? sectionHeaderOffset : 0,
            sectionHeaderCount: hasDebug ? sectionHeaderCount : 0,
            shstrtabIndex: hasDebug ? shstrtabIndex : 0);

        // Program header 1: text (R+X)
        WriteElf64ProgramHeader(output, 0,
            fileOffset: (ulong)textFileOffset,
            virtualAddress: textVa,
            fileSize: (ulong)codeBytes.Length,
            memorySize: (ulong)codeBytes.Length,
            flags: 0x05); // PF_R | PF_X

        if (hasData)
        {
            // Program header 2: data (R+W)
            WriteElf64ProgramHeader(output, 1,
                fileOffset: (ulong)dataFileOffset,
                virtualAddress: dataVa,
                fileSize: (ulong)laidOutData.DataBytes.Length,
                memorySize: (ulong)laidOutData.DataBytes.Length,
                flags: 0x06); // PF_R | PF_W
        }

        // Write .text content
        Array.Copy(codeBytes, 0, output, textFileOffset, codeBytes.Length);

        // Write .data content
        if (hasData)
        {
            Array.Copy(laidOutData.DataBytes, 0, output, dataFileOffset, laidOutData.DataBytes.Length);
        }

        // Write debug sections and section headers
        if (hasDebug)
        {
            var nameOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
            var shstrtab = new MemoryStream();
            shstrtab.WriteByte(0);
            nameOffsets[".text"] = (int)shstrtab.Position;
            shstrtab.Write(Encoding.ASCII.GetBytes(".text\0"));
            if (hasData)
            {
                nameOffsets[".data"] = (int)shstrtab.Position;
                shstrtab.Write(Encoding.ASCII.GetBytes(".data\0"));
            }
            foreach (var debugSection in parsed.DebugSections)
            {
                nameOffsets[debugSection.Name] = (int)shstrtab.Position;
                shstrtab.Write(Encoding.ASCII.GetBytes(debugSection.Name + "\0"));
            }
            nameOffsets[".shstrtab"] = (int)shstrtab.Position;
            shstrtab.Write(Encoding.ASCII.GetBytes(".shstrtab\0"));
            shstrtabBytes = shstrtab.ToArray();

            for (int i = 0; i < parsed.DebugSections.Count; i++)
            {
                Array.Copy(parsed.DebugSections[i].Bytes, 0, output, debugFileOffsets[i], parsed.DebugSections[i].Bytes.Length);
            }

            Array.Copy(shstrtabBytes, 0, output, shstrtabOffset, shstrtabBytes.Length);

            // Write section headers
            int shIdx = 0;

            // SHT_NULL entry
            WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx++, 0, 0, 0, 0, 0, 0, 0);

            // .text
            WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx++,
                nameOffset: (uint)nameOffsets[".text"],
                type: 1, // SHT_PROGBITS
                flags: 0x06, // SHF_ALLOC | SHF_EXECINSTR
                fileOffset: (ulong)textFileOffset,
                size: (ulong)codeBytes.Length,
                addr: textVa,
                alignment: 16);

            // .data (if present)
            if (hasData)
            {
                WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx++,
                    nameOffset: (uint)nameOffsets[".data"],
                    type: 1, // SHT_PROGBITS
                    flags: 0x03, // SHF_ALLOC | SHF_WRITE
                    fileOffset: (ulong)dataFileOffset,
                    size: (ulong)laidOutData.DataBytes.Length,
                    addr: dataVa,
                    alignment: 8);
            }

            // Debug sections
            for (int i = 0; i < parsed.DebugSections.Count; i++)
            {
                WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx++,
                    nameOffset: (uint)nameOffsets[parsed.DebugSections[i].Name],
                    type: 1, // SHT_PROGBITS
                    flags: 0, // no flags (non-ALLOC)
                    fileOffset: (ulong)debugFileOffsets[i],
                    size: (ulong)parsed.DebugSections[i].Bytes.Length,
                    addr: 0,
                    alignment: Math.Max(1, parsed.DebugSections[i].Alignment));
            }

            // .shstrtab
            WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx,
                nameOffset: (uint)nameOffsets[".shstrtab"],
                type: 3, // SHT_STRTAB
                flags: 0,
                fileOffset: (ulong)shstrtabOffset,
                size: (ulong)shstrtabBytes.Length,
                addr: 0,
                alignment: 1);
        }

        return output;
    }

    private const int ElfSectionHeaderSize = 64;

    private static void WriteElf64Header(byte[] output, ulong entryPoint, int programHeaderCount,
        int sectionHeaderOffset = 0, int sectionHeaderCount = 0, int shstrtabIndex = 0)
    {
        // e_ident: magic, class, data, version, OS/ABI, padding
        output[0] = 0x7F;
        output[1] = (byte)'E';
        output[2] = (byte)'L';
        output[3] = (byte)'F';
        output[4] = 2;   // ELFCLASS64
        output[5] = 1;   // ELFDATA2LSB
        output[6] = 1;   // EV_CURRENT
        output[7] = 0;   // ELFOSABI_NONE
        // bytes 8..15 are padding (zero)

        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(16, 2), 2);  // e_type: ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(18, 2), 62); // e_machine: EM_X86_64
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20, 4), 1);  // e_version: EV_CURRENT
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(24, 8), entryPoint); // e_entry
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(32, 8), ElfHeaderSize); // e_phoff
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(40, 8), (ulong)sectionHeaderOffset);  // e_shoff
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(48, 4), 0);  // e_flags
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(52, 2), ElfHeaderSize);  // e_ehsize
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(54, 2), ElfProgramHeaderSize); // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(56, 2), checked((ushort)programHeaderCount)); // e_phnum
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(58, 2), sectionHeaderCount > 0 ? (ushort)ElfSectionHeaderSize : (ushort)0);  // e_shentsize
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(60, 2), checked((ushort)sectionHeaderCount));  // e_shnum
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(62, 2), checked((ushort)shstrtabIndex));  // e_shstrndx
    }

    private static void WriteElf64ProgramHeader(byte[] output, int index,
        ulong fileOffset, ulong virtualAddress, ulong fileSize, ulong memorySize, uint flags)
    {
        int offset = ElfHeaderSize + index * ElfProgramHeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), 1);             // p_type: PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 4, 4), flags);     // p_flags
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 8, 8), fileOffset); // p_offset
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 16, 8), virtualAddress); // p_vaddr
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 24, 8), virtualAddress); // p_paddr
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 32, 8), fileSize);  // p_filesz
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 40, 8), memorySize); // p_memsz
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 48, 8), (ulong)PageSize); // p_align
    }

    private static void WriteElf64SectionHeader(byte[] output, int sectionHeaderTableOffset, int index,
        uint nameOffset, uint type, ulong flags, ulong fileOffset, ulong size, ulong addr, ulong alignment)
    {
        int offset = sectionHeaderTableOffset + index * ElfSectionHeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), nameOffset);       // sh_name
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 4, 4), type);         // sh_type
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 8, 8), flags);        // sh_flags
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 16, 8), addr);        // sh_addr
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 24, 8), fileOffset);  // sh_offset
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 32, 8), size);        // sh_size
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 40, 4), 0);           // sh_link
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 44, 4), 0);           // sh_info
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 48, 8), alignment);   // sh_addralign
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 56, 8), 0);           // sh_entsize
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
        var debugSections = new List<ElfDebugSection>();
        var debugRelocationSections = new List<ElfSectionHeader>();
        for (int i = 0; i < sections.Length; i++)
        {
            if (i == textSectionIndex)
            {
                continue;
            }

            ElfSectionHeader section = sections[i];
            string sectionName = ReadElfString(sectionNames, section.NameOffset);

            if (section.Size == 0)
            {
                continue;
            }

            // Collect debug sections (non-ALLOC, name starts with .debug)
            if (sectionName.StartsWith(".debug", StringComparison.Ordinal)
                && (section.Flags & ElfSectionFlagAlloc) == 0
                && section.Type != SectionTypeRela
                && section.Type != SectionTypeRel)
            {
                debugSections.Add(new ElfDebugSection(
                    Name: sectionName,
                    SectionIndex: i,
                    Bytes: bytes.Slice(checked((int)section.Offset), checked((int)section.Size)).ToArray(),
                    Alignment: section.AddressAlign));
                continue;
            }

            // Collect debug relocation sections (.rela.debug_*)
            if (sectionName.StartsWith(".rela.debug", StringComparison.Ordinal)
                && (section.Type == SectionTypeRela || section.Type == SectionTypeRel))
            {
                debugRelocationSections.Add(section);
                continue;
            }

            if ((section.Flags & ElfSectionFlagAlloc) == 0)
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
            AllocatedSections: allocatedSections,
            DebugSections: debugSections,
            DebugRelocationSections: debugRelocationSections);
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
        List<ElfAllocatedSection> AllocatedSections,
        List<ElfDebugSection> DebugSections,
        List<ElfSectionHeader> DebugRelocationSections);

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

    private readonly record struct ElfDebugSection(
        string Name,
        int SectionIndex,
        byte[] Bytes,
        ulong Alignment);
}
