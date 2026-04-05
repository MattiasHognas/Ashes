using System.Buffers.Binary;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmImageLinker
{
    private const int Arm64TrampolineLength = 28;
    private const ushort ElfMachineAArch64 = 183;

    // AArch64 ELF relocation types
    private const uint ElfRelocAArch64Call26 = 283;
    private const uint ElfRelocAArch64Jump26 = 282;
    private const uint ElfRelocAArch64AdrPrelPgHi21 = 275;
    private const uint ElfRelocAArch64AddAbsLo12Nc = 277;
    private const uint ElfRelocAArch64Abs64 = 257;
    private const uint ElfRelocAArch64Abs32 = 258;
    private const uint ElfRelocAArch64Prel32 = 261;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc8 = 278;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc16 = 284;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc32 = 285;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc64 = 286;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc128 = 299;

    public static byte[] LinkLinuxArm64Executable(byte[] objectBytes, string entrySymbolName)
    {
        ulong textVa = ElfBaseVa + (ulong)PageSize;
        ulong objectTextVa = textVa + (ulong)Arm64TrampolineLength;
        var parsed = ParseElfObject(objectBytes, entrySymbolName);
        int textFileOffset = PageSize;
        int codeLength = Arm64TrampolineLength + parsed.TextBytes.Length;
        int dataFileOffset = Align(textFileOffset + codeLength, PageSize);
        ulong dataVa = ElfBaseVa + (ulong)dataFileOffset;
        var laidOutData = LayoutElfAllocatedSections(parsed.AllocatedSections, dataVa);
        ApplyElfArm64TextRelocations(
            objectBytes,
            parsed.TextBytes,
            parsed.RelocationSections,
            parsed.SymbolTable,
            parsed.TextSectionIndex,
            objectTextVa,
            laidOutData.SectionBaseVas);

        byte[] codeBytes = BuildArm64Trampoline(parsed.EntryOffsetInText)
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
        Dictionary<string, int> shstrtabNameOffsets = new(StringComparer.Ordinal);
        int sectionHeaderCount = 0;
        int shstrtabIndex = 0;
        int sectionHeaderOffset = 0;

        if (hasDebug)
        {
            (shstrtabBytes, shstrtabNameOffsets) = BuildSectionNameStringTable(hasData, parsed.DebugSections);
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
        WriteElf64Arm64Header(output, textVa, programHeaderCount,
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
                nameOffset: (uint)shstrtabNameOffsets[".text"],
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
                    nameOffset: (uint)shstrtabNameOffsets[".data"],
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
                    nameOffset: (uint)shstrtabNameOffsets[parsed.DebugSections[i].Name],
                    type: 1, // SHT_PROGBITS
                    flags: 0, // no flags (non-ALLOC)
                    fileOffset: (ulong)debugFileOffsets[i],
                    size: (ulong)parsed.DebugSections[i].Bytes.Length,
                    addr: 0,
                    alignment: Math.Max(1, parsed.DebugSections[i].Alignment));
            }

            // .shstrtab
            WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx,
                nameOffset: (uint)shstrtabNameOffsets[".shstrtab"],
                type: 3, // SHT_STRTAB
                flags: 0,
                fileOffset: (ulong)shstrtabOffset,
                size: (ulong)shstrtabBytes.Length,
                addr: 0,
                alignment: 1);
        }

        return output;
    }

    private static void WriteElf64Arm64Header(byte[] output, ulong entryPoint, int programHeaderCount,
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
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(18, 2), ElfMachineAArch64); // e_machine: EM_AARCH64
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

    private static void ApplyElfArm64TextRelocations(
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

                switch (relocationType)
                {
                    case ElfRelocAArch64Call26:
                    case ElfRelocAArch64Jump26:
                        {
                            // Encodes a 26-bit signed offset (in 4-byte units) into a BL/B instruction.
                            long pcRelOffset = targetVa - placeVa;
                            int imm26 = checked((int)(pcRelOffset >> 2)) & 0x03FFFFFF;
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            instruction = (instruction & 0xFC000000) | (uint)imm26;
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
                            break;
                        }
                    case ElfRelocAArch64AdrPrelPgHi21:
                        {
                            // ADRP: page-relative 21-bit offset shifted by 12.
                            long pageTarget = targetVa & ~0xFFFL;
                            long pagePc = placeVa & ~0xFFFL;
                            long pageDelta = pageTarget - pagePc;
                            int immHi = (int)(pageDelta >> 12);
                            int immLo = immHi & 0x3;
                            int immHi19 = (immHi >> 2) & 0x7FFFF;
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            instruction = (instruction & 0x9F00001F) | ((uint)immLo << 29) | ((uint)immHi19 << 5);
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
                            break;
                        }
                    case ElfRelocAArch64AddAbsLo12Nc:
                        {
                            // ADD: low 12 bits of target address (no carry).
                            uint imm12 = (uint)(targetVa & 0xFFF);
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            instruction = (instruction & 0xFFC003FF) | (imm12 << 10);
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
                            break;
                        }
                    case ElfRelocAArch64LdstImm12Lo12Nc8:
                        {
                            // LDR/STR with 12-bit immediate, 1-byte scaled.
                            uint imm12 = (uint)(targetVa & 0xFFF);
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            instruction = (instruction & 0xFFC003FF) | (imm12 << 10);
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
                            break;
                        }
                    case ElfRelocAArch64LdstImm12Lo12Nc16:
                        {
                            // LDR/STR with 12-bit immediate, 2-byte scaled.
                            uint imm12 = (uint)((targetVa & 0xFFF) >> 1);
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            instruction = (instruction & 0xFFC003FF) | (imm12 << 10);
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
                            break;
                        }
                    case ElfRelocAArch64LdstImm12Lo12Nc32:
                        {
                            // LDR/STR with 12-bit immediate, 4-byte scaled.
                            uint imm12 = (uint)((targetVa & 0xFFF) >> 2);
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            instruction = (instruction & 0xFFC003FF) | (imm12 << 10);
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
                            break;
                        }
                    case ElfRelocAArch64LdstImm12Lo12Nc64:
                        {
                            // LDR/STR with 12-bit immediate, 8-byte scaled.
                            uint imm12 = (uint)((targetVa & 0xFFF) >> 3);
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            instruction = (instruction & 0xFFC003FF) | (imm12 << 10);
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
                            break;
                        }
                    case ElfRelocAArch64LdstImm12Lo12Nc128:
                        {
                            // LDR/STR with 12-bit immediate, 16-byte scaled.
                            uint imm12 = (uint)((targetVa & 0xFFF) >> 4);
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            instruction = (instruction & 0xFFC003FF) | (imm12 << 10);
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
                            break;
                        }
                    case ElfRelocAArch64Abs64:
                        {
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 8);
                            BinaryPrimitives.WriteInt64LittleEndian(patch, targetVa);
                            break;
                        }
                    case ElfRelocAArch64Abs32:
                        {
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            BinaryPrimitives.WriteInt32LittleEndian(patch, checked((int)targetVa));
                            break;
                        }
                    case ElfRelocAArch64Prel32:
                        {
                            Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
                            BinaryPrimitives.WriteInt32LittleEndian(patch, checked((int)(targetVa - placeVa)));
                            break;
                        }
                    default:
                        throw new InvalidOperationException($"LLVM ELF emitted unsupported AArch64 .text relocation type {relocationType}.");
                }
            }
        }
    }

    private static byte[] BuildArm64Trampoline(int entryOffsetInText)
    {
        // AArch64 trampoline (28 bytes = 7 instructions):
        //   mov  x0, sp          // pass stack pointer as first argument
        //   bl   <entry>         // call entry function
        //   mov  x0, #0          // exit code 0
        //   mov  x8, #93         // syscall 93 = exit
        //   svc  #0              // invoke syscall
        //   brk  #0              // unreachable trap (padding)
        //   brk  #0              // unreachable trap (padding)
        var bytes = new byte[Arm64TrampolineLength];

        // mov x0, sp  => 0x910003E0
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x910003E0);

        // bl <offset>: entry is at (Arm64TrampolineLength + entryOffsetInText) from the BL instruction at offset 4.
        int branchOffset = Arm64TrampolineLength + entryOffsetInText - 4;
        uint blImm26 = (uint)((branchOffset >> 2) & 0x03FFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0x94000000 | blImm26);

        // mov x0, #0  => 0xD2800000
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0xD2800000);

        // mov x8, #93  => movz x8, #93  => 0xD2800BA8
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 0xD2800BA8);

        // svc #0  => 0xD4000001
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 0xD4000001);

        // brk #0  => 0xD4200000 (trap, unreachable)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 0xD4200000);

        // brk #0  => 0xD4200000 (trap, unreachable)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 0xD4200000);

        return bytes;
    }
}
