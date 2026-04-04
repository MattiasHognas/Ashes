using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using LibObjectFile.PE;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmImageLinker
{
    private const ulong PeImageBase = 0x0000000000400000UL;
    private const uint PeTextRva = 0x00001000;
    private const uint PeSectionAlignment = 0x00001000;
    private const uint BssDataAlignment = 16;
    private const int WindowsTrampolineLength = 24;
    private const int WindowsChkstkStubLength = 35;
    private const int WindowsTextPrefixLength = WindowsTrampolineLength + WindowsChkstkStubLength;
    private const ushort CoffRelocAmd64Addr32 = 0x0002;
    private const ushort CoffRelocAmd64Rel32 = 0x0004;
    private static readonly MethodInfo SetPeImageBaseMethod =
        typeof(PEOptionalHeader)
            .GetProperty(nameof(PEOptionalHeader.ImageBase), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetSetMethod(nonPublic: true)
        ?? throw new InvalidOperationException("Failed to locate the PEOptionalHeader.ImageBase setter via reflection, which is required to keep the emitted PE header image base aligned with the linker VA layout.");

    public static byte[] LinkWindowsExecutable(byte[] objectBytes, string entrySymbolName)
    {
        var parsed = ParseCoffObject(objectBytes, entrySymbolName);

        var rdata = new PEStreamSectionData();
        var extraSectionOffsets = new Dictionary<int, uint>();
        uint bssTotalSize = 0;
        var bssSectionOffsets = new Dictionary<int, uint>();
        for (int sectionIndex = 0; sectionIndex < parsed.Sections.Length; sectionIndex++)
        {
            CoffSectionHeader section = parsed.Sections[sectionIndex];
            int sectionNumber = sectionIndex + 1;
            uint totalSize = Math.Max(section.SizeOfRawData, section.VirtualSize);
            bool isDataSection = section.Name is ".rdata" or ".data" or ".bss";
            if (sectionNumber == parsed.TextSectionNumber || totalSize == 0 || !isDataSection)
            {
                continue;
            }

            // COFF BSS sections have PointerToRawData=0 (no file-backed data); they are
            // emitted as a separate PE section whose VirtualSize the loader zero-fills.
            bool isBss = section.Name == ".bss" || (section.PointerToRawData == 0 && totalSize > 0);
            if (isBss)
            {
                uint bssAlign = (BssDataAlignment - (bssTotalSize % BssDataAlignment)) % BssDataAlignment;
                bssTotalSize += bssAlign;
                bssSectionOffsets[sectionNumber] = bssTotalSize;
                bssTotalSize += totalSize;
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
        uint bssRva = 0;
        if (bssTotalSize > 0)
        {
            bssRva = AlignUp(checked(rdataRva + (uint)rdata.Stream.Length), PeSectionAlignment);
            foreach (var pair in bssSectionOffsets)
            {
                sectionBaseVas[pair.Key] = PeImageBase + bssRva + pair.Value;
            }
        }
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

        if (bssTotalSize > 0)
        {
            var bssSection = pe.AddSection(PESectionName.Bss, bssRva);
            bssSection.SetVirtualSizeModeToFixed(bssTotalSize);
        }

        pe.OptionalHeader.AddressOfEntryPoint = new(code, 0);
        pe.OptionalHeader.BaseOfCode = textSection;
        SetPeImageBase(pe.OptionalHeader, PeImageBase);
        pe.OptionalHeader.DllCharacteristics =
            DllCharacteristics.NxCompatible |
            DllCharacteristics.TerminalServerAware;

        using var output = new MemoryStream();
        pe.Write(output, new() { EnableStackTrace = true });
        return output.ToArray();
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
            Span<byte> patch = textBytes.AsSpan(checked((int)relocationOffset), 4);
            int addend = BinaryPrimitives.ReadInt32LittleEndian(patch);

            switch (relocationType)
            {
                case CoffRelocAmd64Addr32:
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        patch,
                        checked((uint)(checked((long)ResolveCoffTargetVa(symbol, textSectionNumber, sectionBaseVas, importSymbolVas)) + addend)));
                    break;
                case CoffRelocAmd64Rel32:
                    long nextInstructionVa = checked((long)(PeImageBase + PeTextRva + (uint)WindowsTextPrefixLength + relocationOffset + 4));
                    long relativeTarget = checked((long)ResolveCoffTargetVa(symbol, textSectionNumber, sectionBaseVas, importSymbolVas) + addend - nextInstructionVa);
                    BinaryPrimitives.WriteInt32LittleEndian(patch, checked((int)relativeTarget));
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

    private static void SetPeImageBase(PEOptionalHeader optionalHeader, ulong imageBase)
    {
        SetPeImageBaseMethod.Invoke(optionalHeader, [imageBase]);
    }

    private static int FindCoffSymbolOffset(
        ReadOnlySpan<byte> bytes,
        uint symbolTableOffset,
        uint symbolCount,
        CoffSectionHeader[] sections,
        string entrySymbolName,
        int expectedSectionNumber)
    {
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
        // Windows x64 __chkstk: probes each 4KB page between the caller's stack
        // pointer and the new stack pointer to trigger guard-page expansion.
        // Input:  rax = number of bytes to allocate.
        // Output: rax unchanged; caller does 'sub rsp, rax' after return.
        return [
            0x51,                                       // push rcx
            0x50,                                       // push rax
            0x48, 0x8D, 0x4C, 0x24, 0x18,              // lea  rcx, [rsp + 24]   ; caller's original rsp
            // .check:
            0x48, 0x3D, 0x00, 0x10, 0x00, 0x00,        // cmp  rax, 0x1000
            0x72, 0x11,                                 // jb   .done             (+17)
            0x48, 0x81, 0xE9, 0x00, 0x10, 0x00, 0x00,  // sub  rcx, 0x1000
            0x84, 0x09,                                 // test byte ptr [rcx], cl ; probe page
            0x48, 0x2D, 0x00, 0x10, 0x00, 0x00,        // sub  rax, 0x1000
            0xEB, 0xE7,                                 // jmp  .check            (-25)
            // .done:
            0x58,                                       // pop  rax
            0x59,                                       // pop  rcx
            0xC3                                        // ret
        ];
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

    private readonly record struct ParsedCoffObject(
        byte[] TextBytes,
        int EntryOffsetInText,
        CoffSectionHeader TextSection,
        CoffSectionHeader[] Sections,
        uint SymbolTableOffset,
        uint SymbolCount,
        int TextSectionNumber);

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
