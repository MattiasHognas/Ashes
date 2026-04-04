using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using LibObjectFile.PE;

namespace Ashes.Backend.Llvm;

internal static class LlvmImageLinker
{
    private const ulong PeImageBase = 0x0000000000400000UL;
    private const ulong ElfSectionFlagAlloc = 0x2;
    private const uint SectionTypeRela = 4;
    private const uint SectionTypeNoBits = 8;
    private const uint SectionTypeRel = 9;
    private const int PageSize = 0x1000;
    private const ulong ElfBaseVa = 0x400000;
    private const int LinuxTrampolineLength = 20;
    private const uint PeTextRva = 0x00001000;
    private const uint PeSectionAlignment = 0x00001000;
    private const int WindowsTrampolineLength = 24;
    private const int WindowsChkstkStubLength = 1;
    private const int WindowsTextPrefixLength = WindowsTrampolineLength + WindowsChkstkStubLength;
    private const uint ElfRelocX86_64Pc32 = 2;
    private const uint ElfRelocX86_64_32 = 10;
    private const uint ElfRelocX86_64_32S = 11;
    private const ushort CoffRelocAmd64Addr32 = 0x0002;
    private const ushort CoffRelocAmd64Rel32 = 0x0004;

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

    public static byte[] LinkWindowsExecutable(byte[] objectBytes, string entrySymbolName)
    {
        var parsed = ParseCoffObject(objectBytes, entrySymbolName);

        var rdata = new PEStreamSectionData();
        var extraSectionOffsets = new Dictionary<int, uint>();
        for (int sectionIndex = 0; sectionIndex < parsed.Sections.Length; sectionIndex++)
        {
            CoffSectionHeader section = parsed.Sections[sectionIndex];
            int sectionNumber = sectionIndex + 1;
            uint totalSize = Math.Max(section.SizeOfRawData, section.VirtualSize);
            bool isRuntimeDataSection = section.Name is ".rdata" or ".data" or ".bss";
            if (sectionNumber == parsed.TextSectionNumber || totalSize == 0 || !isRuntimeDataSection)
            {
                continue;
            }

            Align(rdata, 16);
            extraSectionOffsets[sectionNumber] = checked((uint)rdata.Stream.Position);
            if (section.SizeOfRawData != 0)
            {
                long rawEnd = (long)section.PointerToRawData + section.SizeOfRawData;
                if (section.PointerToRawData != 0 && rawEnd <= objectBytes.Length)
                {
                    rdata.Stream.Write(objectBytes, checked((int)section.PointerToRawData), checked((int)section.SizeOfRawData));
                }
            }

            for (uint i = section.SizeOfRawData; i < totalSize; i++)
            {
                rdata.Stream.WriteByte(0);
            }
        }

        Align(rdata, 2);
        int kernelNameOffset = (int)rdata.Stream.Position;
        rdata.Stream.Write(Encoding.ASCII.GetBytes("KERNEL32.DLL\0"));
        var kernelName = new PEAsciiStringLink(rdata, new RVO((uint)kernelNameOffset));
        int shellNameOffset = (int)rdata.Stream.Position;
        rdata.Stream.Write(Encoding.ASCII.GetBytes("SHELL32.DLL\0"));
        var shellName = new PEAsciiStringLink(rdata, new RVO((uint)shellNameOffset));
        int ws2NameOffset = (int)rdata.Stream.Position;
        rdata.Stream.Write(Encoding.ASCII.GetBytes("WS2_32.DLL\0"));
        var ws2Name = new PEAsciiStringLink(rdata, new RVO((uint)ws2NameOffset));
        var exitProcessHintName = WriteImportHintName(rdata, 0, "ExitProcess");
        var getStdHandleHintName = WriteImportHintName(rdata, 0, "GetStdHandle");
        var writeFileHintName = WriteImportHintName(rdata, 0, "WriteFile");
        var readFileHintName = WriteImportHintName(rdata, 0, "ReadFile");
        var createFileHintName = WriteImportHintName(rdata, 0, "CreateFileA");
        var closeHandleHintName = WriteImportHintName(rdata, 0, "CloseHandle");
        var getFileAttributesHintName = WriteImportHintName(rdata, 0, "GetFileAttributesA");
        var getCommandLineHintName = WriteImportHintName(rdata, 0, "GetCommandLineW");
        var wideCharToMultiByteHintName = WriteImportHintName(rdata, 0, "WideCharToMultiByte");
        var localFreeHintName = WriteImportHintName(rdata, 0, "LocalFree");
        var commandLineToArgvHintName = WriteImportHintName(rdata, 0, "CommandLineToArgvW");
        var wsaStartupHintName = WriteImportHintName(rdata, 0, "WSAStartup");
        var socketHintName = WriteImportHintName(rdata, 0, "socket");
        var connectHintName = WriteImportHintName(rdata, 0, "connect");
        var sendHintName = WriteImportHintName(rdata, 0, "send");
        var recvHintName = WriteImportHintName(rdata, 0, "recv");
        var closeSocketHintName = WriteImportHintName(rdata, 0, "closesocket");
        Align(rdata, 8);
        int iatSectionOffset = (int)rdata.Stream.Length;

        var kernel32Iat = new PEImportAddressTable64() { exitProcessHintName, getStdHandleHintName, writeFileHintName, readFileHintName, createFileHintName, closeHandleHintName, getFileAttributesHintName, getCommandLineHintName, wideCharToMultiByteHintName, localFreeHintName };
        var shell32Iat = new PEImportAddressTable64() { commandLineToArgvHintName };
        var ws2Iat = new PEImportAddressTable64() { wsaStartupHintName, socketHintName, connectHintName, sendHintName, recvHintName, closeSocketHintName };
        var iatDirectory = new PEImportAddressTableDirectory() { kernel32Iat, shell32Iat, ws2Iat };
        var kernel32Ilt = new PEImportLookupTable64() { exitProcessHintName, getStdHandleHintName, writeFileHintName, readFileHintName, createFileHintName, closeHandleHintName, getFileAttributesHintName, getCommandLineHintName, wideCharToMultiByteHintName, localFreeHintName };
        var shell32Ilt = new PEImportLookupTable64() { commandLineToArgvHintName };
        var ws2Ilt = new PEImportLookupTable64() { wsaStartupHintName, socketHintName, connectHintName, sendHintName, recvHintName, closeSocketHintName };
        var importDirectory = new PEImportDirectory
        {
            Entries =
            {
                new PEImportDirectoryEntry(kernelName, kernel32Iat, kernel32Ilt),
                new PEImportDirectoryEntry(shellName, shell32Iat, shell32Ilt),
                new PEImportDirectoryEntry(ws2Name, ws2Iat, ws2Ilt)
            }
        };

        uint rdataRva = AlignUp(checked(PeTextRva + (uint)(WindowsTextPrefixLength + parsed.TextBytes.Length)), PeSectionAlignment);
        ulong exitProcessIatVa = PeImageBase + rdataRva + (ulong)iatSectionOffset;
        ulong getStdHandleIatVa = exitProcessIatVa + 8;
        ulong writeFileIatVa = exitProcessIatVa + 16;
        ulong readFileIatVa = exitProcessIatVa + 24;
        ulong createFileIatVa = exitProcessIatVa + 32;
        ulong closeHandleIatVa = exitProcessIatVa + 40;
        ulong getFileAttributesIatVa = exitProcessIatVa + 48;
        ulong getCommandLineIatVa = exitProcessIatVa + 56;
        ulong wideCharToMultiByteIatVa = exitProcessIatVa + 64;
        ulong localFreeIatVa = exitProcessIatVa + 72;
        ulong commandLineToArgvIatVa = exitProcessIatVa + 88;
        ulong wsaStartupIatVa = exitProcessIatVa + 104;
        ulong socketIatVa = exitProcessIatVa + 112;
        ulong connectIatVa = exitProcessIatVa + 120;
        ulong sendIatVa = exitProcessIatVa + 128;
        ulong recvIatVa = exitProcessIatVa + 136;
        ulong closeSocketIatVa = exitProcessIatVa + 144;
        ulong chkstkStubVa = PeImageBase + PeTextRva + WindowsTrampolineLength;
        var sectionBaseVas = extraSectionOffsets.ToDictionary(
            static pair => pair.Key,
            pair => PeImageBase + rdataRva + pair.Value);
        ApplyCoffTextRelocations(
            objectBytes,
            parsed.TextBytes,
            parsed.TextSection,
            parsed.SymbolTableOffset,
            parsed.SymbolCount,
            parsed.TextSectionNumber,
            sectionBaseVas,
            new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                ["__imp_ExitProcess"] = exitProcessIatVa,
                ["__imp_GetStdHandle"] = getStdHandleIatVa,
                ["__imp_WriteFile"] = writeFileIatVa,
                ["__imp_ReadFile"] = readFileIatVa,
                ["__imp_CreateFileA"] = createFileIatVa,
                ["__imp_CloseHandle"] = closeHandleIatVa,
                ["__imp_GetFileAttributesA"] = getFileAttributesIatVa,
                ["__imp_GetCommandLineW"] = getCommandLineIatVa,
                ["__imp_WideCharToMultiByte"] = wideCharToMultiByteIatVa,
                ["__imp_LocalFree"] = localFreeIatVa,
                ["__imp_CommandLineToArgvW"] = commandLineToArgvIatVa,
                ["__imp_WSAStartup"] = wsaStartupIatVa,
                ["__imp_socket"] = socketIatVa,
                ["__imp_connect"] = connectIatVa,
                ["__imp_send"] = sendIatVa,
                ["__imp_recv"] = recvIatVa,
                ["__imp_closesocket"] = closeSocketIatVa,
                ["__chkstk"] = chkstkStubVa
            });
        byte[] codeBytes = BuildWindowsTrampoline(parsed.EntryOffsetInText, exitProcessIatVa)
            .Concat(BuildWindowsChkstkStub())
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
        rdataSection.Content.Add(kernel32Ilt);
        rdataSection.Content.Add(shell32Ilt);
        rdataSection.Content.Add(ws2Ilt);
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

    private static int Align(int value, int align)
    {
        int mask = align - 1;
        return (value + mask) & ~mask;
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
                VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 8, 4)),
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
        int entryOffset = FindCoffSymbolOffset(bytes, symbolTableOffset, symbolCount, sections, entrySymbolName, textSectionIndex + 1);
        return new ParsedCoffObject(textBytes, entryOffset, textSection, sections, symbolTableOffset, symbolCount, textSectionIndex + 1);
    }

    private static void ApplyCoffTextRelocations(
        ReadOnlySpan<byte> bytes,
        byte[] textBytes,
        CoffSectionHeader textSection,
        uint symbolTableOffset,
        uint symbolCount,
        int textSectionNumber,
        IReadOnlyDictionary<int, ulong> sectionBaseVas,
        IReadOnlyDictionary<string, ulong> importSymbolVas)
    {
        for (int i = 0; i < textSection.NumberOfRelocations; i++)
        {
            int offset = checked((int)textSection.PointerToRelocations + i * 10);
            uint relocationOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            int symbolIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4)));
            ushort relocationType = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 8, 2));
            CoffSymbol symbol = ReadCoffSymbol(bytes, symbolTableOffset, symbolCount, symbolIndex);

            switch (relocationType)
            {
                case CoffRelocAmd64Addr32:
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        textBytes.AsSpan(checked((int)relocationOffset), 4),
                        checked((uint)ResolveCoffTargetVa(symbol, textSectionNumber, sectionBaseVas, importSymbolVas)));
                    break;
                case CoffRelocAmd64Rel32:
                    long nextInstructionVa = checked((long)(PeImageBase + PeTextRva + (uint)WindowsTextPrefixLength + relocationOffset + 4));
                    long relativeTarget = checked((long)ResolveCoffTargetVa(symbol, textSectionNumber, sectionBaseVas, importSymbolVas) - nextInstructionVa);
                    BinaryPrimitives.WriteInt32LittleEndian(textBytes.AsSpan(checked((int)relocationOffset), 4), checked((int)relativeTarget));
                    break;
                default:
                    throw new InvalidOperationException($"LLVM COFF emitted unsupported .text relocation type 0x{relocationType:X4}.");
            }
        }
    }

    private static ulong ResolveCoffTargetVa(
        CoffSymbol symbol,
        int textSectionNumber,
        IReadOnlyDictionary<int, ulong> sectionBaseVas,
        IReadOnlyDictionary<string, ulong> importSymbolVas)
    {
        if (symbol.SectionNumber == textSectionNumber)
        {
            return PeImageBase + PeTextRva + (uint)WindowsTextPrefixLength + symbol.Value;
        }

        if (sectionBaseVas.TryGetValue(symbol.SectionNumber, out ulong sectionBaseVa))
        {
            return sectionBaseVa + symbol.Value;
        }

        if (symbol.SectionNumber == 0 && importSymbolVas.TryGetValue(symbol.Name, out ulong importVa))
        {
            return importVa;
        }

        throw new InvalidOperationException($"LLVM COFF text relocation targeted unsupported symbol '{symbol.Name}' in section {symbol.SectionNumber}.");
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

    private static CoffSymbol ReadCoffSymbol(ReadOnlySpan<byte> bytes, uint symbolTableOffset, uint symbolCount, int symbolIndex)
    {
        int offset = checked((int)symbolTableOffset + (symbolIndex * 18));
        return new CoffSymbol(
            Name: ReadCoffName(bytes.Slice(offset, 8), bytes, symbolTableOffset, symbolCount),
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

    private static byte[] BuildWindowsTrampoline(int entryOffsetInText, ulong exitProcessIatVa)
    {
        var bytes = new byte[WindowsTrampolineLength];
        int index = 0;
        bytes[index++] = 0x48;
        bytes[index++] = 0x83;
        bytes[index++] = 0xEC;
        bytes[index++] = 0x28;
        bytes[index++] = 0xE8;
        int relativeCall = checked(WindowsTextPrefixLength + entryOffsetInText - 9);
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

    private static byte[] BuildWindowsChkstkStub()
    {
        return [0xC3];
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

    private readonly record struct ParsedElfObject(
        byte[] TextBytes,
        int EntryOffsetInText,
        List<ElfSectionHeader> RelocationSections,
        ElfSectionHeader SymbolTable,
        int TextSectionIndex,
        List<ElfAllocatedSection> AllocatedSections);
    private readonly record struct ParsedCoffObject(
        byte[] TextBytes,
        int EntryOffsetInText,
        CoffSectionHeader TextSection,
        CoffSectionHeader[] Sections,
        uint SymbolTableOffset,
        uint SymbolCount,
        int TextSectionNumber);

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

    private readonly record struct CoffSectionHeader(
        string Name,
        uint VirtualSize,
        uint SizeOfRawData,
        uint PointerToRawData,
        uint PointerToRelocations,
        ushort NumberOfRelocations);

    private readonly record struct CoffSymbol(
        string Name,
        uint Value,
        short SectionNumber);
}
