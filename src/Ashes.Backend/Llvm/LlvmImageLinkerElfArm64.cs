using System.Buffers.Binary;
using System.Text;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmImageLinker
{
    private const int Arm64TrampolineLength = 28;
    private const int Arm64ImportStubLength = 12;
    private const ushort ElfMachineAArch64 = 183;
    private const uint ElfRelocAArch64GlobDat = 1025;
    private const string LinuxArm64DynamicLoaderPath = "/lib/ld-linux-aarch64.so.1";

    // AArch64 ELF relocation types
    private const uint ElfRelocAArch64Call26 = 283;
    private const uint ElfRelocAArch64Jump26 = 282;
    private const uint ElfRelocAArch64AdrPrelPgHi21 = 275;
    private const uint ElfRelocAArch64AdrGotPage = 311;
    private const uint ElfRelocAArch64AddAbsLo12Nc = 277;
    private const uint ElfRelocAArch64Abs64 = 257;
    private const uint ElfRelocAArch64Abs32 = 258;
    private const uint ElfRelocAArch64Prel32 = 261;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc8 = 278;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc16 = 284;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc32 = 285;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc64 = 286;
    private const uint ElfRelocAArch64LdstImm12Lo12Nc128 = 299;
    private const uint ElfRelocAArch64Ld64GotLo12Nc = 312;
    // Local-exec TLS: patch the ADD immediate with the symbol's TPREL (offset from TPIDR_EL0).
    private const uint ElfRelocAArch64TlsLeAddTprelHi12 = 549;
    private const uint ElfRelocAArch64TlsLeAddTprelLo12Nc = 551;

    public static byte[] LinkLinuxArm64Executable(
        byte[] objectBytes,
        string entrySymbolName,
        LinkedImagePayload? linkedPayload = null,
        IReadOnlyDictionary<string, string>? externalLibraries = null)
    {
        ulong textVa = ElfBaseVa + (ulong)PageSize;
        ulong objectTextVa = textVa + (ulong)Arm64TrampolineLength;
        var parsed = ParseElfObject(objectBytes, entrySymbolName);

        LinuxArm64TlsLayout tls = ComputeLinuxArm64TlsLayout(parsed);
        LinuxArm64DataLayout dataLayout = LayoutLinuxArm64ImportsAndData(
            objectBytes, parsed, externalLibraries, linkedPayload, objectTextVa);

        byte[] finalDataBytes = ApplyLinuxArm64DataAndTextRelocations(
            objectBytes, parsed, dataLayout, objectTextVa, tls);
        byte[] codeBytes = ApplyLinuxArm64DebugRelocationsAndBuildCode(
            objectBytes, parsed, dataLayout, objectTextVa);

        LinuxArm64WriteLayout layout = ComputeLinuxArm64WriteLayout(
            parsed, tls, dataLayout, finalDataBytes, codeBytes);

        var output = new byte[layout.TotalSize];
        WriteElf64Arm64Header(output, textVa, layout.ProgramHeaderCount,
            sectionHeaderOffset: layout.HasDebug ? layout.SectionHeaderOffset : 0,
            sectionHeaderCount: layout.HasDebug ? layout.SectionHeaderCount : 0,
            shstrtabIndex: layout.HasDebug ? layout.ShstrtabIndex : 0);

        WriteLinuxArm64LoadSegmentHeaders(output, layout, textVa, dataLayout, codeBytes, finalDataBytes);
        WriteLinuxArm64DynamicTlsSegmentHeaders(output, layout, dataLayout, tls);

        // Write .text content
        Array.Copy(codeBytes, 0, output, dataLayout.TextFileOffset, codeBytes.Length);

        // Write .data content
        if (layout.HasData)
        {
            Array.Copy(finalDataBytes, 0, output, dataLayout.DataFileOffset, finalDataBytes.Length);
        }

        // Write debug sections and section headers
        if (layout.HasDebug)
        {
            WriteLinuxArm64DebugContent(output, parsed, layout);
            WriteLinuxArm64SectionHeaders(output, parsed, layout, dataLayout, textVa, codeBytes.Length, finalDataBytes.Length);
        }

        return output;
    }

    private readonly record struct LinuxArm64TlsLayout(
        bool HasTls,
        Dictionary<int, ulong> TlsBlockOffsets,
        ulong TlsMemSize,
        ulong TlsAlign,
        ulong TlsTprelBase);

    private static LinuxArm64TlsLayout ComputeLinuxArm64TlsLayout(ParsedElfObject parsed)
    {
        // aarch64 local-exec TLS layout (the per-thread arena). TP (TPIDR_EL0) points at a 16-byte
        // TCB; the static TLS block follows, aligned. A symbol's TPREL = alignUp(16, maxAlign) +
        // its offset within the block. The arena cursors are all zero-initialised, so every TLS
        // section is .tbss (no file image) — reject .tdata rather than silently mislink it.
        bool hasTls = parsed.TlsSections.Count > 0;
        var tlsBlockOffsets = new Dictionary<int, ulong>();
        ulong tlsMemSize = 0;
        ulong tlsAlign = 1;
        foreach (ElfTlsSection t in parsed.TlsSections)
        {
            if (t.InitBytes.Length > 0)
            {
                throw new InvalidOperationException("AArch64 TLS with initialized .tdata is not supported (arena cursors are zero-initialised .tbss).");
            }

            ulong a = Math.Max(1UL, t.Alignment);
            tlsAlign = Math.Max(tlsAlign, a);
            tlsMemSize = (tlsMemSize + a - 1) & ~(a - 1);
            tlsBlockOffsets[t.SectionIndex] = tlsMemSize;
            tlsMemSize += t.Size;
        }

        ulong tlsTprelBase = (16UL + tlsAlign - 1) & ~(tlsAlign - 1);
        return new LinuxArm64TlsLayout(hasTls, tlsBlockOffsets, tlsMemSize, tlsAlign, tlsTprelBase);
    }

    private readonly record struct LinuxArm64DataLayout(
        List<LinuxDynamicImport> Imports,
        int TextFileOffset,
        int DataFileOffset,
        ulong DataVa,
        LaidOutElfSections LaidOutData,
        LinuxDynamicImportLayout ImportLayout,
        Dictionary<string, ulong> ExternalSymbolVas,
        List<LinkerDataSegment> ExtraDataSegments);

    private static LinuxArm64DataLayout LayoutLinuxArm64ImportsAndData(
        byte[] objectBytes,
        ParsedElfObject parsed,
        IReadOnlyDictionary<string, string>? externalLibraries,
        LinkedImagePayload? linkedPayload,
        ulong objectTextVa)
    {
        List<LinuxDynamicImport> imports = CollectLinuxDynamicImports(objectBytes, parsed, externalLibraries);
        int textFileOffset = PageSize;
        int codeLength = Arm64TrampolineLength + parsed.TextBytes.Length + imports.Count * Arm64ImportStubLength;
        int dataFileOffset = Align(textFileOffset + codeLength, PageSize);
        ulong dataVa = ElfBaseVa + (ulong)dataFileOffset;
        var laidOutData = LayoutElfAllocatedSections(parsed.AllocatedSections, dataVa);
        int importDataOffset = Align(laidOutData.DataBytes.Length, 8);
        LinuxDynamicImportLayout importLayout = imports.Count == 0
            ? LinuxDynamicImportLayout.Empty
            : BuildLinuxArm64DynamicImportLayout(imports, dataVa, checked((uint)importDataOffset), objectTextVa + (ulong)parsed.TextBytes.Length);

        var externalSymbolVas = new Dictionary<string, ulong>(importLayout.ImportStubVas, StringComparer.Ordinal);
        var extraDataSegments = new List<LinkerDataSegment>();
        if (importLayout.Bytes.Length > 0)
        {
            extraDataSegments.Add(new LinkerDataSegment(importDataOffset, importLayout.Bytes));
        }

        if (linkedPayload is LinkedImagePayload payload)
        {
            int payloadDataOffset = importLayout.Bytes.Length > 0
                ? importDataOffset + importLayout.Bytes.Length
                : laidOutData.DataBytes.Length;
            payloadDataOffset = Align(payloadDataOffset, payload.Alignment);
            extraDataSegments.Add(new LinkerDataSegment(payloadDataOffset, payload.Bytes));
            ulong payloadStartVa = dataVa + (ulong)payloadDataOffset;
            externalSymbolVas[payload.StartSymbolName] = payloadStartVa;
            externalSymbolVas[payload.EndSymbolName] = payloadStartVa + (ulong)payload.Bytes.Length;
        }

        return new LinuxArm64DataLayout(
            imports, textFileOffset, dataFileOffset, dataVa, laidOutData,
            importLayout, externalSymbolVas, extraDataSegments);
    }

    private static byte[] ApplyLinuxArm64DataAndTextRelocations(
        byte[] objectBytes,
        ParsedElfObject parsed,
        LinuxArm64DataLayout dataLayout,
        ulong objectTextVa,
        LinuxArm64TlsLayout tls)
    {
        // Patch relocations that live inside allocated data (e.g. a `switch` jump table in
        // `.rodata` holding absolute `.text` block addresses). Done before the data bytes are
        // sealed into the final segment so the patched bytes flow through.
        ApplyElfArm64AllocatedSectionRelocations(
            objectBytes,
            dataLayout.LaidOutData.DataBytes,
            dataLayout.DataVa,
            parsed.AllocatedRelocationSections,
            parsed.SymbolTable,
            parsed.TextSectionIndex,
            objectTextVa,
            dataLayout.LaidOutData.SectionBaseVas,
            dataLayout.ExternalSymbolVas);

        byte[] finalDataBytes = dataLayout.ExtraDataSegments.Count == 0
            ? dataLayout.LaidOutData.DataBytes
            : BuildLinuxDataBytes(dataLayout.LaidOutData.DataBytes, dataLayout.ExtraDataSegments);
        ApplyElfArm64TextRelocations(
            objectBytes,
            parsed.TextBytes,
            parsed.RelocationSections,
            parsed.SymbolTable,
            parsed.TextSectionIndex,
            objectTextVa,
            dataLayout.LaidOutData.SectionBaseVas,
            dataLayout.ExternalSymbolVas,
            tls.TlsBlockOffsets,
            tls.TlsTprelBase);

        return finalDataBytes;
    }

    private static byte[] ApplyLinuxArm64DebugRelocationsAndBuildCode(
        byte[] objectBytes,
        ParsedElfObject parsed,
        LinuxArm64DataLayout dataLayout,
        ulong objectTextVa)
    {
        // Patch DWARF section-internal relocations (str_offsets bases, abbrev/line offsets,
        // .debug_addr entries). Debug sections are non-ALLOC and keep a zero base in the final
        // image, so section-relative references resolve to raw offsets — same as the x64 linker.
        var debugSectionBaseVas = new Dictionary<int, ulong>(dataLayout.LaidOutData.SectionBaseVas);
        foreach (var debugSection in parsed.DebugSections)
        {
            debugSectionBaseVas[debugSection.SectionIndex] = 0;
        }

        ApplyElfDebugRelocations(
            objectBytes,
            parsed.DebugSections,
            parsed.DebugRelocationSections,
            parsed.SymbolTable,
            parsed.TextSectionIndex,
            objectTextVa,
            debugSectionBaseVas,
            dataLayout.ExternalSymbolVas);

        return BuildArm64Trampoline(parsed.EntryOffsetInText)
            .Concat(parsed.TextBytes)
            .Concat(dataLayout.ImportLayout.StubBytes)
            .ToArray();
    }

    private readonly record struct LinuxArm64WriteLayout(
        bool HasData,
        bool HasDebug,
        bool HasDynamicImports,
        int ProgramHeaderCount,
        int EndOfLoadable,
        List<int> DebugFileOffsets,
        int ShstrtabOffset,
        byte[] ShstrtabBytes,
        Dictionary<string, int> ShstrtabNameOffsets,
        int SectionHeaderCount,
        int ShstrtabIndex,
        int SectionHeaderOffset,
        int TotalSize);

    private static LinuxArm64WriteLayout ComputeLinuxArm64WriteLayout(
        ParsedElfObject parsed,
        LinuxArm64TlsLayout tls,
        LinuxArm64DataLayout dataLayout,
        byte[] finalDataBytes,
        byte[] codeBytes)
    {
        bool hasData = finalDataBytes.Length > 0;
        bool hasDebug = parsed.DebugSections.Count > 0;
        bool hasDynamicImports = dataLayout.Imports.Count > 0;
        int programHeaderCount = 1 + (hasData ? 1 : 0) + (hasDynamicImports ? 2 : 0) + (tls.HasTls ? 1 : 0);

        int endOfLoadable = hasData
            ? dataLayout.DataFileOffset + finalDataBytes.Length
            : dataLayout.TextFileOffset + codeBytes.Length;

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

        LinuxArm64ShstrtabLayout shstrtab = ComputeLinuxArm64ShstrtabLayout(
            parsed, hasData, hasDebug, debugCursor, endOfLoadable);

        return new LinuxArm64WriteLayout(
            hasData, hasDebug, hasDynamicImports, programHeaderCount, endOfLoadable,
            debugFileOffsets, shstrtab.ShstrtabOffset, shstrtab.ShstrtabBytes, shstrtab.ShstrtabNameOffsets,
            shstrtab.SectionHeaderCount, shstrtab.ShstrtabIndex, shstrtab.SectionHeaderOffset, shstrtab.TotalSize);
    }

    private readonly record struct LinuxArm64ShstrtabLayout(
        int ShstrtabOffset,
        byte[] ShstrtabBytes,
        Dictionary<string, int> ShstrtabNameOffsets,
        int SectionHeaderCount,
        int ShstrtabIndex,
        int SectionHeaderOffset,
        int TotalSize);

    private static LinuxArm64ShstrtabLayout ComputeLinuxArm64ShstrtabLayout(
        ParsedElfObject parsed,
        bool hasData,
        bool hasDebug,
        int debugCursor,
        int endOfLoadable)
    {
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

        return new LinuxArm64ShstrtabLayout(
            shstrtabOffset, shstrtabBytes, shstrtabNameOffsets,
            sectionHeaderCount, shstrtabIndex, sectionHeaderOffset, totalSize);
    }

    private static void WriteLinuxArm64LoadSegmentHeaders(
        byte[] output,
        LinuxArm64WriteLayout layout,
        ulong textVa,
        LinuxArm64DataLayout dataLayout,
        byte[] codeBytes,
        byte[] finalDataBytes)
    {
        if (layout.HasDynamicImports)
        {
            WriteElf64ProgramHeader(output, 0,
                fileOffset: 0,
                virtualAddress: ElfBaseVa,
                fileSize: (ulong)(dataLayout.TextFileOffset + codeBytes.Length),
                memorySize: (ulong)(dataLayout.TextFileOffset + codeBytes.Length),
                flags: 0x05); // PF_R | PF_X
        }
        else
        {
            WriteElf64ProgramHeader(output, 0,
                fileOffset: (ulong)dataLayout.TextFileOffset,
                virtualAddress: textVa,
                fileSize: (ulong)codeBytes.Length,
                memorySize: (ulong)codeBytes.Length,
                flags: 0x05); // PF_R | PF_X
        }

        if (layout.HasData)
        {
            // Program header 2: data (R+W)
            WriteElf64ProgramHeader(output, 1,
                fileOffset: (ulong)dataLayout.DataFileOffset,
                virtualAddress: dataLayout.DataVa,
                fileSize: (ulong)finalDataBytes.Length,
                memorySize: (ulong)finalDataBytes.Length,
                flags: 0x06); // PF_R | PF_W
        }
    }

    private static void WriteLinuxArm64DynamicTlsSegmentHeaders(
        byte[] output,
        LinuxArm64WriteLayout layout,
        LinuxArm64DataLayout dataLayout,
        LinuxArm64TlsLayout tls)
    {
        if (layout.HasDynamicImports)
        {
            int interpHeaderIndex = layout.HasData ? 2 : 1;
            int dynamicHeaderIndex = interpHeaderIndex + 1;
            WriteElf64ProgramHeader(output, interpHeaderIndex,
                fileOffset: (ulong)(dataLayout.DataFileOffset + dataLayout.ImportLayout.InterpDataOffset),
                virtualAddress: dataLayout.DataVa + dataLayout.ImportLayout.InterpDataOffset,
                fileSize: (ulong)dataLayout.ImportLayout.InterpByteCount,
                memorySize: (ulong)dataLayout.ImportLayout.InterpByteCount,
                flags: 0x04,
                type: ElfProgramTypeInterp,
                alignment: 1);
            WriteElf64ProgramHeader(output, dynamicHeaderIndex,
                fileOffset: (ulong)(dataLayout.DataFileOffset + dataLayout.ImportLayout.DynamicDataOffset),
                virtualAddress: dataLayout.DataVa + dataLayout.ImportLayout.DynamicDataOffset,
                fileSize: (ulong)dataLayout.ImportLayout.DynamicByteCount,
                memorySize: (ulong)dataLayout.ImportLayout.DynamicByteCount,
                flags: 0x06,
                type: ElfProgramTypeDynamic,
                alignment: 8);
        }

        if (tls.HasTls)
        {
            // PT_TLS: the initialization template the loader copies into each thread's TLS block.
            // All arena cursors are zero-init (.tbss), so the file image is empty (fileSize 0) and
            // memorySize is the total block size; the loader zero-fills it per thread. p_vaddr sits
            // in the loaded data segment (unread since fileSize is 0).
            WriteElf64ProgramHeader(output, layout.ProgramHeaderCount - 1,
                fileOffset: (ulong)dataLayout.DataFileOffset,
                virtualAddress: dataLayout.DataVa,
                fileSize: 0,
                memorySize: tls.TlsMemSize,
                flags: 0x04, // PF_R
                type: ElfProgramTypeTls,
                alignment: tls.TlsAlign);
        }
    }

    private static void WriteLinuxArm64DebugContent(byte[] output, ParsedElfObject parsed, LinuxArm64WriteLayout layout)
    {
        for (int i = 0; i < parsed.DebugSections.Count; i++)
        {
            Array.Copy(parsed.DebugSections[i].Bytes, 0, output, layout.DebugFileOffsets[i], parsed.DebugSections[i].Bytes.Length);
        }

        Array.Copy(layout.ShstrtabBytes, 0, output, layout.ShstrtabOffset, layout.ShstrtabBytes.Length);
    }

    private static void WriteLinuxArm64SectionHeaders(
        byte[] output,
        ParsedElfObject parsed,
        LinuxArm64WriteLayout layout,
        LinuxArm64DataLayout dataLayout,
        ulong textVa,
        int codeLength,
        int dataLength)
    {
        int sectionHeaderOffset = layout.SectionHeaderOffset;
        int shIdx = 0;

        // SHT_NULL entry
        WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx++, 0, 0, 0, 0, 0, 0, 0);

        // .text
        WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx++,
            nameOffset: (uint)layout.ShstrtabNameOffsets[".text"],
            type: 1, // SHT_PROGBITS
            flags: 0x06, // SHF_ALLOC | SHF_EXECINSTR
            fileOffset: (ulong)dataLayout.TextFileOffset,
            size: (ulong)codeLength,
            addr: textVa,
            alignment: 16);

        // .data (if present)
        if (layout.HasData)
        {
            WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx++,
                nameOffset: (uint)layout.ShstrtabNameOffsets[".data"],
                type: 1, // SHT_PROGBITS
                flags: 0x03, // SHF_ALLOC | SHF_WRITE
                fileOffset: (ulong)dataLayout.DataFileOffset,
                size: (ulong)dataLength,
                addr: dataLayout.DataVa,
                alignment: 8);
        }

        WriteLinuxArm64DebugSectionHeaders(output, parsed, layout, sectionHeaderOffset, ref shIdx);

        // .shstrtab
        WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx,
            nameOffset: (uint)layout.ShstrtabNameOffsets[".shstrtab"],
            type: 3, // SHT_STRTAB
            flags: 0,
            fileOffset: (ulong)layout.ShstrtabOffset,
            size: (ulong)layout.ShstrtabBytes.Length,
            addr: 0,
            alignment: 1);
    }

    private static void WriteLinuxArm64DebugSectionHeaders(
        byte[] output,
        ParsedElfObject parsed,
        LinuxArm64WriteLayout layout,
        int sectionHeaderOffset,
        ref int shIdx)
    {
        // Debug sections
        for (int i = 0; i < parsed.DebugSections.Count; i++)
        {
            WriteElf64SectionHeader(output, sectionHeaderOffset, shIdx++,
                nameOffset: (uint)layout.ShstrtabNameOffsets[parsed.DebugSections[i].Name],
                type: 1, // SHT_PROGBITS
                flags: 0, // no flags (non-ALLOC)
                fileOffset: (ulong)layout.DebugFileOffsets[i],
                size: (ulong)parsed.DebugSections[i].Bytes.Length,
                addr: 0,
                alignment: Math.Max(1, parsed.DebugSections[i].Alignment));
        }
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

    /// <summary>
    /// Applies AArch64 relocations whose target is an allocated data section — the jump-table case
    /// is a <c>.rodata</c> array of absolute <c>.text</c> block addresses (<c>R_AARCH64_ABS64</c>).
    /// Mirrors <see cref="ApplyElfArm64TextRelocations"/> but patches the laid-out data buffer at
    /// the target section's offset. (LLVM currently lowers our switches to branch trees on AArch64,
    /// so this is rarely exercised, but it keeps jump-table relocations safe across LLVM versions
    /// and switch densities.)
    /// </summary>
    private static void ApplyElfArm64AllocatedSectionRelocations(
        ReadOnlySpan<byte> objectBytes,
        byte[] dataBytes,
        ulong dataVa,
        List<ElfSectionHeader> relocationSections,
        ElfSectionHeader symtab,
        int textSectionIndex,
        ulong loadedTextVa,
        IReadOnlyDictionary<int, ulong> sectionBaseVas,
        IReadOnlyDictionary<string, ulong> externalSymbolVas)
    {
        if (relocationSections.Count == 0)
        {
            return;
        }

        byte[] strtab = ReadStringTable(objectBytes, ReadElfSectionHeader(objectBytes, (int)symtab.Link));
        var definedSymbolVas = BuildDefinedSymbolTable(objectBytes, symtab, strtab, textSectionIndex, loadedTextVa, sectionBaseVas);

        foreach (ElfSectionHeader relocationSection in relocationSections)
        {
            if (relocationSection.EntrySize == 0)
            {
                throw new InvalidOperationException("LLVM ELF relocation section is missing entry size metadata.");
            }

            int targetSectionIndex = checked((int)relocationSection.Info);
            if (!sectionBaseVas.TryGetValue(targetSectionIndex, out ulong targetSectionVa))
            {
                continue;
            }

            int sectionOffsetInData = checked((int)(targetSectionVa - dataVa));
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
                long targetVa = checked((long)ResolveElfTargetVa(symbol, textSectionIndex, loadedTextVa, sectionBaseVas, strtab, definedSymbolVas, externalSymbolVas) + addend);
                int patchOffset = checked(sectionOffsetInData + (int)relocOffset);
                long placeVa = checked((long)targetSectionVa + (long)relocOffset);
                switch (relocationType)
                {
                    case ElfRelocAArch64Abs64:
                        BinaryPrimitives.WriteInt64LittleEndian(dataBytes.AsSpan(patchOffset, 8), targetVa);
                        break;
                    case ElfRelocAArch64Abs32:
                        BinaryPrimitives.WriteInt32LittleEndian(dataBytes.AsSpan(patchOffset, 4), checked((int)targetVa));
                        break;
                    case ElfRelocAArch64Prel32:
                        BinaryPrimitives.WriteInt32LittleEndian(dataBytes.AsSpan(patchOffset, 4), checked((int)(targetVa - placeVa)));
                        break;
                    default:
                        throw new InvalidOperationException($"LLVM AArch64 emitted unsupported allocated-section relocation type {relocationType}.");
                }
            }
        }
    }

    private static void ApplyElfArm64TextRelocations(
        ReadOnlySpan<byte> objectBytes,
        byte[] textBytes,
        List<ElfSectionHeader> relocationSections,
        ElfSectionHeader symtab,
        int textSectionIndex,
        ulong loadedTextVa,
        IReadOnlyDictionary<int, ulong> sectionBaseVas,
        IReadOnlyDictionary<string, ulong>? externalSymbolVas = null,
        IReadOnlyDictionary<int, ulong>? tlsBlockOffsets = null,
        ulong tlsTprelBase = 0)
    {
        byte[] strtab = ReadStringTable(objectBytes, ReadElfSectionHeader(objectBytes, (int)symtab.Link));
        var definedSymbolVas = BuildDefinedSymbolTable(objectBytes, symtab, strtab, textSectionIndex, loadedTextVa, sectionBaseVas);

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

                if (TryApplyElfArm64TlsRelocation(relocationType, textBytes, relocOffset, symbol, addend, tlsBlockOffsets, tlsTprelBase))
                {
                    continue;
                }

                long targetVa = checked((long)ResolveElfTargetVa(symbol, textSectionIndex, loadedTextVa, sectionBaseVas, strtab, definedSymbolVas, externalSymbolVas) + addend);
                long placeVa = checked((long)loadedTextVa + (long)relocOffset);
                ApplyElfArm64TextRelocation(relocationType, textBytes, relocOffset, targetVa, placeVa);
            }
        }
    }

    private static bool TryApplyElfArm64TlsRelocation(
        uint relocationType,
        byte[] textBytes,
        ulong relocOffset,
        ElfSymbol symbol,
        long addend,
        IReadOnlyDictionary<int, ulong>? tlsBlockOffsets,
        ulong tlsTprelBase)
    {
        // Local-exec TLS: the symbol lives in a .tbss TLS section (no VA); resolve it to a
        // TPREL offset and patch the ADD immediate (bits 21:10). HI12 takes bits [23:12],
        // LO12_NC takes bits [11:0]. Handled here so it never reaches the VA resolver.
        if (relocationType is not (ElfRelocAArch64TlsLeAddTprelHi12 or ElfRelocAArch64TlsLeAddTprelLo12Nc))
        {
            return false;
        }

        if (tlsBlockOffsets is null || !tlsBlockOffsets.TryGetValue(symbol.SectionIndex, out ulong blockOffset))
        {
            throw new InvalidOperationException($"AArch64 TLS relocation references a symbol not in a TLS section (index {symbol.SectionIndex}).");
        }

        long tprel = checked((long)tlsTprelBase + (long)blockOffset + (long)symbol.Value + addend);
        uint imm12 = relocationType == ElfRelocAArch64TlsLeAddTprelHi12
            ? (uint)((tprel >> 12) & 0xFFF)
            : (uint)(tprel & 0xFFF);
        Span<byte> tlsPatch = textBytes.AsSpan(checked((int)relocOffset), 4);
        uint tlsInsn = BinaryPrimitives.ReadUInt32LittleEndian(tlsPatch);
        tlsInsn = (tlsInsn & ~(0xFFFu << 10)) | (imm12 << 10);
        BinaryPrimitives.WriteUInt32LittleEndian(tlsPatch, tlsInsn);
        return true;
    }

    private static void ApplyElfArm64TextRelocation(
        uint relocationType,
        byte[] textBytes,
        ulong relocOffset,
        long targetVa,
        long placeVa)
    {
        switch (relocationType)
        {
            case ElfRelocAArch64Call26:
            case ElfRelocAArch64Jump26:
                ApplyElfArm64Branch26Relocation(textBytes, relocOffset, targetVa, placeVa);
                break;
            case ElfRelocAArch64AdrPrelPgHi21:
            case ElfRelocAArch64AdrGotPage:
                ApplyElfArm64AdrpPageRelocation(textBytes, relocOffset, targetVa, placeVa);
                break;
            case ElfRelocAArch64Ld64GotLo12Nc:
                ApplyElfArm64GotLoadRelocation(textBytes, relocOffset, targetVa);
                break;
            case ElfRelocAArch64AddAbsLo12Nc:
                ApplyElfArm64ScaledImm12Relocation(textBytes, relocOffset, targetVa, 0);
                break;
            case ElfRelocAArch64LdstImm12Lo12Nc8:
                ApplyElfArm64ScaledImm12Relocation(textBytes, relocOffset, targetVa, 0);
                break;
            case ElfRelocAArch64LdstImm12Lo12Nc16:
                ApplyElfArm64ScaledImm12Relocation(textBytes, relocOffset, targetVa, 1);
                break;
            case ElfRelocAArch64LdstImm12Lo12Nc32:
                ApplyElfArm64ScaledImm12Relocation(textBytes, relocOffset, targetVa, 2);
                break;
            case ElfRelocAArch64LdstImm12Lo12Nc64:
                ApplyElfArm64ScaledImm12Relocation(textBytes, relocOffset, targetVa, 3);
                break;
            case ElfRelocAArch64LdstImm12Lo12Nc128:
                ApplyElfArm64ScaledImm12Relocation(textBytes, relocOffset, targetVa, 4);
                break;
            case ElfRelocAArch64Abs64:
                BinaryPrimitives.WriteInt64LittleEndian(textBytes.AsSpan(checked((int)relocOffset), 8), targetVa);
                break;
            case ElfRelocAArch64Abs32:
                BinaryPrimitives.WriteInt32LittleEndian(textBytes.AsSpan(checked((int)relocOffset), 4), checked((int)targetVa));
                break;
            case ElfRelocAArch64Prel32:
                BinaryPrimitives.WriteInt32LittleEndian(textBytes.AsSpan(checked((int)relocOffset), 4), checked((int)(targetVa - placeVa)));
                break;
            default:
                throw new InvalidOperationException($"LLVM ELF emitted unsupported AArch64 .text relocation type {relocationType}.");
        }
    }

    private static void ApplyElfArm64Branch26Relocation(byte[] textBytes, ulong relocOffset, long targetVa, long placeVa)
    {
        // Encodes a 26-bit signed offset (in 4-byte units) into a BL/B instruction.
        long pcRelOffset = targetVa - placeVa;
        if ((pcRelOffset & 0x3) != 0)
        {
            throw new InvalidOperationException(
                $"AArch64 CALL26/JUMP26 relocation at offset 0x{relocOffset:X} has unaligned target offset {pcRelOffset}.");
        }

        long imm26Value = pcRelOffset >> 2;
        const int MinImm26 = -(1 << 25);
        const int MaxImm26 = (1 << 25) - 1;
        if (imm26Value < MinImm26 || imm26Value > MaxImm26)
        {
            throw new InvalidOperationException(
                $"AArch64 CALL26/JUMP26 relocation at offset 0x{relocOffset:X} is out of range: immediate {imm26Value} does not fit in signed 26 bits.");
        }

        int imm26 = (int)imm26Value;
        Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
        uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
        instruction = (instruction & 0xFC000000) | ((uint)imm26 & 0x03FFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
    }

    private static void ApplyElfArm64AdrpPageRelocation(byte[] textBytes, ulong relocOffset, long targetVa, long placeVa)
    {
        // ADRP: page-relative 21-bit signed offset shifted by 12.
        // ADR_GOT_PAGE is relaxed the same way for final ET_EXEC images:
        // the paired GOT load can resolve directly against the symbol page.
        long pageTarget = targetVa & ~0xFFFL;
        long pagePc = placeVa & ~0xFFFL;
        long pageDelta = pageTarget - pagePc;
        long immFull = pageDelta >> 12;
        const long MinImm21 = -(1L << 20);
        const long MaxImm21 = (1L << 20) - 1;
        if (immFull < MinImm21 || immFull > MaxImm21)
        {
            throw new InvalidOperationException(
                $"AArch64 ADR_PREL_PG_HI21 relocation at offset 0x{relocOffset:X} is out of range: page delta {immFull} does not fit in signed 21 bits (±4 GiB).");
        }

        int immHi = (int)immFull;
        int immLo = immHi & 0x3;
        int immHi19 = (immHi >> 2) & 0x7FFFF;
        Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
        uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
        instruction = (instruction & 0x9F00001F) | ((uint)immLo << 29) | ((uint)immHi19 << 5);
        BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
    }

    private static void ApplyElfArm64GotLoadRelocation(byte[] textBytes, ulong relocOffset, long targetVa)
    {
        // Relax LDR Xt, [Xn, #:got_lo12:sym] into ADD Xt, Xn, #lo12
        // for final linked images where the symbol address is already known.
        Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
        uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
        const uint LdrUnsignedOffset64Mask = 0xFFC00000;
        const uint LdrUnsignedOffset64Opcode = 0xF9400000;
        if ((instruction & LdrUnsignedOffset64Mask) != LdrUnsignedOffset64Opcode)
        {
            throw new InvalidOperationException(
                $"AArch64 LD64_GOT_LO12_NC relocation at offset 0x{relocOffset:X} targeted unexpected instruction 0x{instruction:X8}.");
        }

        uint imm12 = (uint)(targetVa & 0xFFF);
        uint rnAndRt = instruction & 0x3FF;
        uint addInstruction = 0x91000000u | (imm12 << 10) | rnAndRt;
        BinaryPrimitives.WriteUInt32LittleEndian(patch, addInstruction);
    }

    private static void ApplyElfArm64ScaledImm12Relocation(byte[] textBytes, ulong relocOffset, long targetVa, int scaleShift)
    {
        // ADD / LDR / STR: low 12 bits of the target address (no carry), scaled by the
        // access size (1/2/4/8/16-byte -> shift 0/1/2/3/4).
        uint imm12 = (uint)((targetVa & 0xFFF) >> scaleShift);
        Span<byte> patch = textBytes.AsSpan(checked((int)relocOffset), 4);
        uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(patch);
        instruction = (instruction & 0xFFC003FF) | (imm12 << 10);
        BinaryPrimitives.WriteUInt32LittleEndian(patch, instruction);
    }

    private static LinuxDynamicImportLayout BuildLinuxArm64DynamicImportLayout(
        IReadOnlyList<LinuxDynamicImport> imports,
        ulong dataVa,
        uint dataOffset,
        ulong stubBaseVa)
    {
        var importStubVas = new Dictionary<string, ulong>(StringComparer.Ordinal);
        LinuxArm64ImportData data = BuildLinuxArm64ImportDataStream(imports, dataVa, dataOffset);
        byte[] stubBytes = BuildLinuxArm64ImportStubs(imports, dataVa, data.GotDataOffset, stubBaseVa, importStubVas);

        return new LinuxDynamicImportLayout(
            StubBytes: stubBytes,
            Bytes: data.Bytes,
            ImportStubVas: importStubVas,
            InterpDataOffset: data.InterpDataOffset,
            InterpByteCount: data.InterpByteCount,
            DynamicDataOffset: data.DynamicDataOffset,
            DynamicByteCount: data.DynamicByteCount);
    }

    private readonly record struct LinuxArm64ImportData(
        byte[] Bytes,
        uint GotDataOffset,
        uint InterpDataOffset,
        uint InterpByteCount,
        uint DynamicDataOffset,
        uint DynamicByteCount);

    private static LinuxArm64ImportData BuildLinuxArm64ImportDataStream(
        IReadOnlyList<LinuxDynamicImport> imports,
        ulong dataVa,
        uint dataOffset)
    {
        var stream = new MemoryStream();

        uint interpDataOffset = dataOffset;
        byte[] interpBytes = Encoding.ASCII.GetBytes(LinuxArm64DynamicLoaderPath + "\0");
        stream.Write(interpBytes);
        AlignImportStream(stream, 8);

        string[] libraries = imports.Select(static import => import.LibraryName).Distinct(StringComparer.Ordinal).ToArray();
        var dynstrOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        byte[] dynstrBytes = BuildLinuxArm64DynStr(libraries, imports, dynstrOffsets);
        byte[] dynsymBytes = BuildLinuxDynamicSymbolTable(imports, dynstrOffsets);
        byte[] hashBytes = BuildLinuxElfHash(imports);

        uint hashDataOffset = dataOffset + checked((uint)stream.Position);
        stream.Write(hashBytes);
        AlignImportStream(stream, 8);

        uint dynstrDataOffset = dataOffset + checked((uint)stream.Position);
        stream.Write(dynstrBytes);
        AlignImportStream(stream, 8);

        uint dynsymDataOffset = dataOffset + checked((uint)stream.Position);
        stream.Write(dynsymBytes);
        AlignImportStream(stream, 8);

        uint gotDataOffset = dataOffset + checked((uint)stream.Position);
        stream.Write(new byte[imports.Count * 8]);
        AlignImportStream(stream, 8);

        uint relaDataOffset = dataOffset + checked((uint)stream.Position);
        byte[] relaBytes = BuildLinuxArm64GlobalDataRelocations(imports, dataVa + gotDataOffset);
        stream.Write(relaBytes);
        AlignImportStream(stream, 8);

        uint dynamicDataOffset = dataOffset + checked((uint)stream.Position);
        byte[] dynamicBytes = BuildLinuxDynamicTable(
            libraries,
            dynstrOffsets,
            dataVa + hashDataOffset,
            dataVa + dynstrDataOffset,
            dynstrBytes.Length,
            dataVa + dynsymDataOffset,
            dataVa + relaDataOffset,
            relaBytes.Length);
        stream.Write(dynamicBytes);

        return new LinuxArm64ImportData(
            stream.ToArray(),
            gotDataOffset,
            interpDataOffset,
            checked((uint)interpBytes.Length),
            dynamicDataOffset,
            checked((uint)dynamicBytes.Length));
    }

    private static byte[] BuildLinuxArm64DynStr(
        string[] libraries,
        IReadOnlyList<LinuxDynamicImport> imports,
        Dictionary<string, int> dynstrOffsets)
    {
        var dynstrStream = new MemoryStream();
        dynstrStream.WriteByte(0);
        foreach (string library in libraries)
        {
            dynstrOffsets[library] = checked((int)dynstrStream.Position);
            dynstrStream.Write(Encoding.ASCII.GetBytes(library + "\0"));
        }

        foreach (LinuxDynamicImport import in imports)
        {
            dynstrOffsets[import.SymbolName] = checked((int)dynstrStream.Position);
            dynstrStream.Write(Encoding.ASCII.GetBytes(import.SymbolName + "\0"));
        }

        return dynstrStream.ToArray();
    }

    private static byte[] BuildLinuxArm64ImportStubs(
        IReadOnlyList<LinuxDynamicImport> imports,
        ulong dataVa,
        uint gotDataOffset,
        ulong stubBaseVa,
        Dictionary<string, ulong> importStubVas)
    {
        byte[] stubBytes = new byte[imports.Count * Arm64ImportStubLength];
        for (int i = 0; i < imports.Count; i++)
        {
            ulong stubVa = stubBaseVa + (ulong)(i * Arm64ImportStubLength);
            ulong gotEntryVa = dataVa + gotDataOffset + (ulong)(i * 8);

            long pageTarget = (long)(gotEntryVa & ~0xFFFUL);
            long pagePc = (long)(stubVa & ~0xFFFUL);
            long pageDelta = pageTarget - pagePc;
            long immFull = pageDelta >> 12;
            const long MinImm21 = -(1L << 20);
            const long MaxImm21 = (1L << 20) - 1;
            if (immFull < MinImm21 || immFull > MaxImm21)
            {
                throw new InvalidOperationException(
                    $"AArch64 import stub for '{imports[i].SymbolName}' is out of ADRP range: page delta {immFull}.");
            }

            uint immHi = (uint)immFull;
            uint immLo = immHi & 0x3;
            uint immHi19 = (immHi >> 2) & 0x7FFFF;
            uint gotLo12 = (uint)((gotEntryVa & 0xFFF) >> 3);

            int stubOffset = i * Arm64ImportStubLength;
            BinaryPrimitives.WriteUInt32LittleEndian(
                stubBytes.AsSpan(stubOffset, 4),
                0x90000010u | (immLo << 29) | (immHi19 << 5));
            BinaryPrimitives.WriteUInt32LittleEndian(
                stubBytes.AsSpan(stubOffset + 4, 4),
                0xF9400210u | (gotLo12 << 10));
            BinaryPrimitives.WriteUInt32LittleEndian(
                stubBytes.AsSpan(stubOffset + 8, 4),
                0xD61F0200u);

            importStubVas[imports[i].SymbolName] = stubVa;
        }

        return stubBytes;
    }

    private static byte[] BuildLinuxArm64GlobalDataRelocations(IReadOnlyList<LinuxDynamicImport> imports, ulong gotVa)
    {
        var bytes = new byte[imports.Count * Elf64RelaSize];
        for (int i = 0; i < imports.Count; i++)
        {
            int offset = i * Elf64RelaSize;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), gotVa + (ulong)(i * 8));
            ulong info = ((ulong)imports[i].SymbolIndex << 32) | ElfRelocAArch64GlobDat;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8, 8), info);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 16, 8), 0);
        }

        return bytes;
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
