using System.Buffers.Binary;
using System.Text;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmImageLinker
{
    private const ulong PeImageBase = 0x0000000000400000UL;
    private const uint PeTextRva = 0x00001000;
    private const uint PeSectionAlignment = 0x00001000;
    private const uint PeFileAlignment = 0x00000200;
    private const uint BssDataAlignment = 16;
    private const int WindowsTrampolineLength = 24;
    private const int WindowsChkstkStubLength = 35;
    private const int WindowsTextPrefixLength = WindowsTrampolineLength + WindowsChkstkStubLength;
    private const ushort CoffRelocAmd64Addr32 = 0x0002;
    private const ushort CoffRelocAmd64Rel32 = 0x0004;

    private const int PeDosHeaderSize = 64;
    private const int PeSignatureSize = 4;
    private const int PeCoffHeaderSize = 20;
    private const int PeOptionalHeaderSize = 240;
    private const int PeSectionHeaderSize = 40;
    private const int PeHeadersStart = PeDosHeaderSize + PeSignatureSize + PeCoffHeaderSize + PeOptionalHeaderSize;

    public static byte[] LinkWindowsExecutable(byte[] objectBytes, string entrySymbolName)
    {
        var parsed = ParseCoffObject(objectBytes, entrySymbolName);

        using var rdataStream = new MemoryStream();
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

            AlignStream(rdataStream, 16);
            extraSectionOffsets[sectionNumber] = checked((uint)rdataStream.Position);
            if (section.SizeOfRawData != 0)
            {
                long rawEnd = (long)section.PointerToRawData + section.SizeOfRawData;
                if (section.PointerToRawData != 0 && rawEnd <= objectBytes.Length)
                {
                    rdataStream.Write(objectBytes, checked((int)section.PointerToRawData), checked((int)section.SizeOfRawData));
                }
            }

            for (uint i = section.SizeOfRawData; i < totalSize; i++)
            {
                rdataStream.WriteByte(0);
            }
        }

        // Write DLL name strings
        AlignStream(rdataStream, 2);
        int kernelNameOffset = (int)rdataStream.Position;
        rdataStream.Write(Encoding.ASCII.GetBytes("KERNEL32.DLL\0"));
        int shellNameOffset = (int)rdataStream.Position;
        rdataStream.Write(Encoding.ASCII.GetBytes("SHELL32.DLL\0"));
        int ws2NameOffset = (int)rdataStream.Position;
        rdataStream.Write(Encoding.ASCII.GetBytes("WS2_32.DLL\0"));

        // Write Hint/Name entries for each import function
        int exitProcessHintOffset = WriteHintName(rdataStream, 0, "ExitProcess");
        int getStdHandleHintOffset = WriteHintName(rdataStream, 0, "GetStdHandle");
        int writeFileHintOffset = WriteHintName(rdataStream, 0, "WriteFile");
        int readFileHintOffset = WriteHintName(rdataStream, 0, "ReadFile");
        int createFileHintOffset = WriteHintName(rdataStream, 0, "CreateFileA");
        int closeHandleHintOffset = WriteHintName(rdataStream, 0, "CloseHandle");
        int getFileAttributesHintOffset = WriteHintName(rdataStream, 0, "GetFileAttributesA");
        int getCommandLineHintOffset = WriteHintName(rdataStream, 0, "GetCommandLineW");
        int wideCharToMultiByteHintOffset = WriteHintName(rdataStream, 0, "WideCharToMultiByte");
        int localFreeHintOffset = WriteHintName(rdataStream, 0, "LocalFree");
        int commandLineToArgvHintOffset = WriteHintName(rdataStream, 0, "CommandLineToArgvW");
        int wsaStartupHintOffset = WriteHintName(rdataStream, 0, "WSAStartup");
        int socketHintOffset = WriteHintName(rdataStream, 0, "socket");
        int connectHintOffset = WriteHintName(rdataStream, 0, "connect");
        int sendHintOffset = WriteHintName(rdataStream, 0, "send");
        int recvHintOffset = WriteHintName(rdataStream, 0, "recv");
        int closeSocketHintOffset = WriteHintName(rdataStream, 0, "closesocket");

        // Group hint offsets by DLL for IAT/ILT construction
        int[] kernel32Hints = [exitProcessHintOffset, getStdHandleHintOffset, writeFileHintOffset, readFileHintOffset, createFileHintOffset, closeHandleHintOffset, getFileAttributesHintOffset, getCommandLineHintOffset, wideCharToMultiByteHintOffset, localFreeHintOffset];
        int[] shell32Hints = [commandLineToArgvHintOffset];
        int[] ws2Hints = [wsaStartupHintOffset, socketHintOffset, connectHintOffset, sendHintOffset, recvHintOffset, closeSocketHintOffset];

        // Write IAT (Import Address Table) — 8 bytes per entry + 8-byte null terminator per DLL
        AlignStream(rdataStream, 8);
        int iatSectionOffset = (int)rdataStream.Position;

        uint rdataRva = AlignUp(checked(PeTextRva + (uint)(WindowsTextPrefixLength + parsed.TextBytes.Length)), PeSectionAlignment);

        int kernel32IatOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, kernel32Hints, rdataRva);
        int shell32IatOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, shell32Hints, rdataRva);
        int ws2IatOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, ws2Hints, rdataRva);

        // Write ILT (Import Lookup Table) — identical structure to IAT
        int kernel32IltOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, kernel32Hints, rdataRva);
        int shell32IltOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, shell32Hints, rdataRva);
        int ws2IltOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, ws2Hints, rdataRva);

        // Write Import Directory Table (3 entries + null terminator)
        // Each entry is 20 bytes: ILT RVA, TimeDateStamp, ForwarderChain, Name RVA, IAT RVA
        int importDirOffset = (int)rdataStream.Position;
        WriteImportDirectoryEntry(rdataStream, rdataRva + (uint)kernel32IltOffset, rdataRva + (uint)kernelNameOffset, rdataRva + (uint)kernel32IatOffset);
        WriteImportDirectoryEntry(rdataStream, rdataRva + (uint)shell32IltOffset, rdataRva + (uint)shellNameOffset, rdataRva + (uint)shell32IatOffset);
        WriteImportDirectoryEntry(rdataStream, rdataRva + (uint)ws2IltOffset, rdataRva + (uint)ws2NameOffset, rdataRva + (uint)ws2IatOffset);
        // Null terminator entry (20 zero bytes)
        for (int i = 0; i < 20; i++)
        {
            rdataStream.WriteByte(0);
        }

        int importDirSize = (int)rdataStream.Position - importDirOffset;

        // Compute IAT VAs from recorded offsets — each entry is 8 bytes.
        ulong kernel32IatVa = PeImageBase + rdataRva + (ulong)kernel32IatOffset;
        ulong shell32IatVa = PeImageBase + rdataRva + (ulong)shell32IatOffset;
        ulong ws2IatVa = PeImageBase + rdataRva + (ulong)ws2IatOffset;

        ulong exitProcessIatVa = kernel32IatVa;
        ulong getStdHandleIatVa = kernel32IatVa + 1 * 8;
        ulong writeFileIatVa = kernel32IatVa + 2 * 8;
        ulong readFileIatVa = kernel32IatVa + 3 * 8;
        ulong createFileIatVa = kernel32IatVa + 4 * 8;
        ulong closeHandleIatVa = kernel32IatVa + 5 * 8;
        ulong getFileAttributesIatVa = kernel32IatVa + 6 * 8;
        ulong getCommandLineIatVa = kernel32IatVa + 7 * 8;
        ulong wideCharToMultiByteIatVa = kernel32IatVa + 8 * 8;
        ulong localFreeIatVa = kernel32IatVa + 9 * 8;
        ulong commandLineToArgvIatVa = shell32IatVa;
        ulong wsaStartupIatVa = ws2IatVa;
        ulong socketIatVa = ws2IatVa + 1 * 8;
        ulong connectIatVa = ws2IatVa + 2 * 8;
        ulong sendIatVa = ws2IatVa + 3 * 8;
        ulong recvIatVa = ws2IatVa + 4 * 8;
        ulong closeSocketIatVa = ws2IatVa + 5 * 8;
        ulong chkstkStubVa = PeImageBase + PeTextRva + WindowsTrampolineLength;
        var sectionBaseVas = extraSectionOffsets.ToDictionary(
            static pair => pair.Key,
            pair => PeImageBase + rdataRva + pair.Value);
        uint bssRva = 0;
        if (bssTotalSize > 0)
        {
            bssRva = AlignUp(checked(rdataRva + (uint)rdataStream.Length), PeSectionAlignment);
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

        byte[] rdataBytes = rdataStream.ToArray();

        // Compute section count and file layout
        bool hasBss = bssTotalSize > 0;
        int sectionCount = hasBss ? 3 : 2; // .text, .rdata, optionally .bss
        int headersSize = PeDosHeaderSize + PeSignatureSize + PeCoffHeaderSize + PeOptionalHeaderSize + sectionCount * PeSectionHeaderSize;
        uint headersFileSize = AlignUp((uint)headersSize, PeFileAlignment);

        uint textFileOffset = headersFileSize;
        uint textRawSize = AlignUp((uint)codeBytes.Length, PeFileAlignment);

        uint rdataFileOffset = textFileOffset + textRawSize;
        uint rdataRawSize = AlignUp((uint)rdataBytes.Length, PeFileAlignment);

        uint bssFileOffset = 0;
        if (hasBss)
        {
            bssFileOffset = rdataFileOffset + rdataRawSize;
        }

        // Compute SizeOfImage (the last section VA + its aligned virtual size)
        uint sizeOfImage;
        if (hasBss)
        {
            sizeOfImage = bssRva + AlignUp(bssTotalSize, PeSectionAlignment);
        }
        else
        {
            sizeOfImage = rdataRva + AlignUp((uint)rdataBytes.Length, PeSectionAlignment);
        }

        uint totalFileSize = hasBss ? bssFileOffset : rdataFileOffset + rdataRawSize;

        var output = new byte[totalFileSize];

        // DOS header (minimal)
        output[0] = (byte)'M';
        output[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(60, 4), PeDosHeaderSize); // e_lfanew

        // PE signature
        int peOff = PeDosHeaderSize;
        output[peOff] = (byte)'P';
        output[peOff + 1] = (byte)'E';
        output[peOff + 2] = 0;
        output[peOff + 3] = 0;

        // COFF header
        int coffOff = peOff + PeSignatureSize;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(coffOff, 2), 0x8664);      // Machine: AMD64
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(coffOff + 2, 2), checked((ushort)sectionCount));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(coffOff + 4, 4), 0);       // TimeDateStamp
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(coffOff + 8, 4), 0);       // PointerToSymbolTable
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(coffOff + 12, 4), 0);      // NumberOfSymbols
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(coffOff + 16, 2), PeOptionalHeaderSize); // SizeOfOptionalHeader
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(coffOff + 18, 2), 0x0022); // Characteristics: EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE

        // Optional header (PE32+)
        int optOff = coffOff + PeCoffHeaderSize;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff, 2), 0x020B);       // Magic: PE32+
        output[optOff + 2] = 1;  // MajorLinkerVersion
        output[optOff + 3] = 0;  // MinorLinkerVersion
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 4, 4), textRawSize);   // SizeOfCode
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 8, 4), rdataRawSize);  // SizeOfInitializedData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 12, 4), bssTotalSize);           // SizeOfUninitializedData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 16, 4), PeTextRva);              // AddressOfEntryPoint
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 20, 4), PeTextRva);              // BaseOfCode
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 24, 8), PeImageBase);            // ImageBase
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 32, 4), PeSectionAlignment);     // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 36, 4), PeFileAlignment);        // FileAlignment
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 40, 2), 6);  // MajorOperatingSystemVersion
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 42, 2), 0);  // MinorOperatingSystemVersion
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 44, 2), 0);  // MajorImageVersion
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 46, 2), 0);  // MinorImageVersion
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 48, 2), 6);  // MajorSubsystemVersion
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 50, 2), 0);  // MinorSubsystemVersion
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 52, 4), 0);  // Win32VersionValue
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 56, 4), sizeOfImage);      // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 60, 4), headersFileSize);   // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 64, 4), 0);  // CheckSum
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 68, 2), 3);  // Subsystem: CONSOLE
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 70, 2), 0x8100); // DllCharacteristics: NX_COMPAT | TERMINAL_SERVER_AWARE
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 72, 8), 0x100000);  // SizeOfStackReserve
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 80, 8), 0x1000);    // SizeOfStackCommit
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 88, 8), 0x100000);  // SizeOfHeapReserve
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 96, 8), 0x1000);    // SizeOfHeapCommit
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 104, 4), 0);        // LoaderFlags
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 108, 4), 16);       // NumberOfRvaAndSizes

        // Data directories (16 entries, each 8 bytes = 128 bytes starting at optOff+112)
        // Entry 1 (index 1): Import Directory
        int dataDirOff = optOff + 112;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirOff + 8, 4), rdataRva + (uint)importDirOffset);  // Import Table RVA
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirOff + 12, 4), (uint)importDirSize);               // Import Table Size

        // Entry 12 (index 12): IAT Directory
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirOff + 96, 4), rdataRva + (uint)iatSectionOffset); // IAT RVA
        int iatTotalSize = ws2IatOffset + (ws2Hints.Length + 1) * 8 - iatSectionOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirOff + 100, 4), (uint)iatTotalSize); // IAT Size

        // Section headers
        int secOff = PeHeadersStart;

        // .text section header
        Encoding.ASCII.GetBytes(".text").CopyTo(output.AsSpan(secOff));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 8, 4), (uint)codeBytes.Length);   // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 12, 4), PeTextRva);                // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 16, 4), textRawSize);              // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 20, 4), textFileOffset);           // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 36, 4), 0x60000020);               // Characteristics: CODE | EXECUTE | READ
        secOff += PeSectionHeaderSize;

        // .rdata section header
        Encoding.ASCII.GetBytes(".rdata").CopyTo(output.AsSpan(secOff));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 8, 4), (uint)rdataBytes.Length);   // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 12, 4), rdataRva);                  // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 16, 4), rdataRawSize);              // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 20, 4), rdataFileOffset);           // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 36, 4), 0x40000040);               // Characteristics: INITIALIZED_DATA | READ
        secOff += PeSectionHeaderSize;

        // .bss section header (if needed)
        if (hasBss)
        {
            Encoding.ASCII.GetBytes(".bss").CopyTo(output.AsSpan(secOff));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 8, 4), bssTotalSize);          // VirtualSize
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 12, 4), bssRva);                // VirtualAddress
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 16, 4), 0);                     // SizeOfRawData (zero for BSS)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 20, 4), 0);                     // PointerToRawData
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 36, 4), 0xC0000080);           // Characteristics: UNINITIALIZED_DATA | READ | WRITE
        }

        // Write section data
        Array.Copy(codeBytes, 0, output, (int)textFileOffset, codeBytes.Length);
        Array.Copy(rdataBytes, 0, output, (int)rdataFileOffset, rdataBytes.Length);

        return output;
    }

    private static void WriteImportAddressTable(MemoryStream stream, int[] hintNameOffsets, uint rdataRva)
    {
        Span<byte> entry = stackalloc byte[8];
        foreach (int hintOffset in hintNameOffsets)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(entry, rdataRva + (uint)hintOffset);
            stream.Write(entry);
        }

        // Null terminator
        entry.Clear();
        stream.Write(entry);
    }

    private static void WriteImportDirectoryEntry(MemoryStream stream, uint iltRva, uint nameRva, uint iatRva)
    {
        Span<byte> entry = stackalloc byte[20];
        entry.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(entry[..4], iltRva);   // OriginalFirstThunk (ILT RVA)
        // TimeDateStamp = 0 (bytes 4..8)
        // ForwarderChain = 0 (bytes 8..12)
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..16], nameRva); // Name RVA
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..20], iatRva);  // FirstThunk (IAT RVA)
        stream.Write(entry);
    }

    private static int WriteHintName(MemoryStream stream, ushort hint, string name)
    {
        AlignStream(stream, 2);
        int offset = (int)stream.Position;
        Span<byte> hintBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(hintBytes, hint);
        stream.Write(hintBytes);
        stream.Write(Encoding.ASCII.GetBytes(name));
        stream.WriteByte(0);
        if (stream.Position % 2 != 0)
        {
            stream.WriteByte(0);
        }

        return offset;
    }

    private static void AlignStream(MemoryStream stream, int align)
    {
        long pos = stream.Position;
        long pad = (align - (pos % align)) % align;
        for (int i = 0; i < pad; i++)
        {
            stream.WriteByte(0);
        }
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
