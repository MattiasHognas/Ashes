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
    private const uint PeDefaultStackReserveBytes = 0x00800000; // 8 MiB default; grows on demand up to reserve.
    private const uint PeStackCommitBytes = 0x00001000;         // 4 KiB committed initially (Windows default page).
    private const string PeStackReserveEnvVar = "ASHES_WIN_STACK_RESERVE_BYTES";
    private const int WindowsTrampolineLength = 24;
    private const int WindowsChkstkStubLength = 35;
    private const int WindowsTextPrefixLength = WindowsTrampolineLength + WindowsChkstkStubLength;
    private const ushort CoffRelocAmd64Addr64 = 0x0001;
    private const ushort CoffRelocAmd64Addr32 = 0x0002;
    private const ushort CoffRelocAmd64Rel32 = 0x0004;
    private const ushort CoffRelocAmd64Rel32_1 = 0x0005;
    private const ushort CoffRelocAmd64Rel32_2 = 0x0006;
    private const ushort CoffRelocAmd64Rel32_3 = 0x0007;
    private const ushort CoffRelocAmd64Rel32_4 = 0x0008;
    private const ushort CoffRelocAmd64Rel32_5 = 0x0009;
    private const ushort CoffRelocAmd64SecRel = 0x000B;

    private const int PeDosHeaderSize = 64;
    private const int PeSignatureSize = 4;
    private const int PeCoffHeaderSize = 20;
    private const int PeOptionalHeaderSize = 240;
    private const int PeSectionHeaderSize = 40;
    private const int PeHeadersStart = PeDosHeaderSize + PeSignatureSize + PeCoffHeaderSize + PeOptionalHeaderSize;

    // jmp qword ptr [rip+disp32] (6 bytes) + 2 bytes of int3 padding.
    private const int WindowsImportThunkLength = 8;

    /// <summary>
    /// Windows C-runtime and platform entry points that statically linked bitcode payloads (the
    /// Mbed TLS module, and the compiler's own TLS codegen) reference by <em>direct</em> call or
    /// address. The PE counterpart of the ELF linker's <c>LinuxDynamicImportLibraries</c>: any
    /// undefined COFF symbol found here gets an import (IAT slot + call thunk) from the named DLL.
    /// Only symbols actually undefined in the object are imported, so the map may be generous —
    /// but every name listed must exist in the DLL's export table on real Windows and Wine.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> WindowsRuntimeImportLibraries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BCryptGenRandom"] = "bcrypt.dll",
            ["FindClose"] = "kernel32.dll",
            ["FindFirstFileW"] = "kernel32.dll",
            ["FindNextFileW"] = "kernel32.dll",
            ["GetLastError"] = "kernel32.dll",
            ["GetSystemTimeAsFileTime"] = "kernel32.dll",
            ["MoveFileExA"] = "kernel32.dll",
            ["MultiByteToWideChar"] = "kernel32.dll",
            ["calloc"] = "msvcrt.dll",
            ["exit"] = "msvcrt.dll",
            ["fclose"] = "msvcrt.dll",
            ["feof"] = "msvcrt.dll",
            ["ferror"] = "msvcrt.dll",
            ["fflush"] = "msvcrt.dll",
            ["fgets"] = "msvcrt.dll",
            ["fopen"] = "msvcrt.dll",
            ["fputc"] = "msvcrt.dll",
            ["fputs"] = "msvcrt.dll",
            ["fread"] = "msvcrt.dll",
            ["free"] = "msvcrt.dll",
            ["fseek"] = "msvcrt.dll",
            ["ftell"] = "msvcrt.dll",
            ["fwrite"] = "msvcrt.dll",
            ["getenv"] = "msvcrt.dll",
            ["gmtime"] = "msvcrt.dll",
            ["malloc"] = "msvcrt.dll",
            ["memchr"] = "msvcrt.dll",
            ["memmove"] = "msvcrt.dll",
            ["printf"] = "msvcrt.dll",
            ["puts"] = "msvcrt.dll",
            ["rand"] = "msvcrt.dll",
            ["remove"] = "msvcrt.dll",
            ["rename"] = "msvcrt.dll",
            ["setbuf"] = "msvcrt.dll",
            ["srand"] = "msvcrt.dll",
            ["sscanf"] = "msvcrt.dll",
            ["strchr"] = "msvcrt.dll",
            ["strcmp"] = "msvcrt.dll",
            ["strcpy"] = "msvcrt.dll",
            ["strncmp"] = "msvcrt.dll",
            ["strncpy"] = "msvcrt.dll",
            ["strstr"] = "msvcrt.dll",
            ["time"] = "msvcrt.dll",
            ["tolower"] = "msvcrt.dll",
            ["toupper"] = "msvcrt.dll",
            ["vsnprintf"] = "msvcrt.dll",
        };

    public static byte[] LinkWindowsExecutable(
        byte[] objectBytes,
        string entrySymbolName,
        LinkedImagePayload? linkedPayload = null,
        IReadOnlyDictionary<string, string>? externalLibraries = null)
    {
        var parsed = ParseCoffObject(objectBytes, entrySymbolName);
        externalLibraries = CollectWindowsRuntimeImports(objectBytes, parsed.SymbolTableOffset, parsed.SymbolCount, externalLibraries);

        (byte[] rdataBytes, WindowsRdataLayout rdataLayout) = BuildWindowsRdataSection(
            objectBytes, parsed, externalLibraries, linkedPayload);
        WindowsSymbolResolution resolution = ResolveWindowsSymbols(rdataBytes, rdataLayout, parsed, linkedPayload);
        byte[] codeBytes = ApplyWindowsRelocations(objectBytes, parsed, rdataBytes, rdataLayout, resolution);
        return WriteWindowsImage(parsed, codeBytes, rdataBytes, rdataLayout, resolution);
    }

    private readonly record struct WindowsExtraSectionsLayout(
        Dictionary<int, uint> ExtraSectionOffsets,
        uint BssTotalSize,
        Dictionary<int, uint> BssSectionOffsets);

    private readonly record struct WindowsHintOffsetsA(
        int ExitProcess, int GetStdHandle, int WriteFile, int ReadFile, int CreateFile,
        int CloseHandle, int GetFileAttributes, int GetCommandLine, int WideCharToMultiByte,
        int LocalFree, int CommandLineToArgv, int WsaStartup, int Socket, int Connect, int Send,
        int Recv, int CloseSocket, int IoctlSocket, int WsaGetLastError, int Bind, int SetSockOpt,
        int WsaIoctl, int WsaSend);

    private readonly record struct WindowsHintOffsetsB(
        int WsaRecv, int WsaPoll, int Listen, int Accept, int Sleep, int GetTickCount64,
        int VirtualAlloc, int VirtualFree, int CreateIoCompletionPort, int GetQueuedCompletionStatus,
        int LoadLibrary, int GetProcAddress, int CertOpenSystemStore, int CertEnumCertificatesInStore,
        int CertCloseStore, int CreatePipe, int CreateProcessA, int TerminateProcess,
        int WaitForSingleObject, int GetExitCodeProcess, int CreateThread, int GetSystemInfo,
        int GetConsoleMode, int SetConsoleMode);

    private readonly record struct WindowsIatLayout(
        int IatSectionOffset,
        int Kernel32IatOffset,
        int Shell32IatOffset,
        int Ws2IatOffset,
        int Crypt32IatOffset,
        List<(WindowsImportLibrary Import, int Offset)> ExternalIatOffsets,
        int IatEndOffset,
        int Kernel32IltOffset,
        int Shell32IltOffset,
        int Ws2IltOffset,
        int Crypt32IltOffset,
        List<(WindowsImportLibrary Import, int Offset)> ExternalIltOffsets);

    private readonly record struct WindowsRdataLayout(
        Dictionary<int, uint> ExtraSectionOffsets,
        uint BssTotalSize,
        Dictionary<int, uint> BssSectionOffsets,
        WindowsImportLibrary[] ExternalImports,
        uint RdataRva,
        int Kernel32IatOffset,
        int Shell32IatOffset,
        int Ws2IatOffset,
        int Crypt32IatOffset,
        List<(WindowsImportLibrary Import, int Offset)> ExternalIatOffsets,
        int IatSectionOffset,
        int IatEndOffset,
        int ImportDirOffset,
        int ImportDirSize,
        ulong PayloadStartVa,
        ulong PayloadEndVa,
        int ExternalThunkCount);

    private static (byte[] RdataBytes, WindowsRdataLayout Layout) BuildWindowsRdataSection(
        byte[] objectBytes,
        ParsedCoffObject parsed,
        IReadOnlyDictionary<string, string>? externalLibraries,
        LinkedImagePayload? linkedPayload)
    {
        using var rdataStream = new MemoryStream();
        WindowsExtraSectionsLayout extra = WriteWindowsExtraSections(rdataStream, objectBytes, parsed);
        WriteWindowsDllNames(rdataStream, out int kernelNameOffset, out int shellNameOffset, out int ws2NameOffset, out int crypt32NameOffset);
        var externalImports = BuildWindowsExternalImports(externalLibraries);
        var externalNameOffsets = WriteWindowsExternalDllNames(rdataStream, externalImports);
        WindowsHintOffsetsA hintsA = WriteWindowsHintNamesA(rdataStream);
        WindowsHintOffsetsB hintsB = WriteWindowsHintNamesB(rdataStream);
        var externalHintOffsets = WriteWindowsExternalHints(rdataStream, externalImports);

        // Group hint offsets by DLL for IAT/ILT construction
        int[] kernel32Hints = [hintsA.ExitProcess, hintsA.GetStdHandle, hintsA.WriteFile, hintsA.ReadFile, hintsA.CreateFile, hintsA.CloseHandle, hintsA.GetFileAttributes, hintsA.GetCommandLine, hintsA.WideCharToMultiByte, hintsA.LocalFree, hintsB.Sleep, hintsB.VirtualAlloc, hintsB.VirtualFree, hintsB.CreateIoCompletionPort, hintsB.GetQueuedCompletionStatus, hintsB.LoadLibrary, hintsB.GetProcAddress, hintsB.CreatePipe, hintsB.CreateProcessA, hintsB.TerminateProcess, hintsB.WaitForSingleObject, hintsB.GetExitCodeProcess, hintsB.CreateThread, hintsB.GetSystemInfo, hintsB.GetTickCount64, hintsB.GetConsoleMode, hintsB.SetConsoleMode];
        int[] shell32Hints = [hintsA.CommandLineToArgv];
        int[] ws2Hints = [hintsA.WsaStartup, hintsA.Socket, hintsA.Connect, hintsA.Send, hintsA.Recv, hintsA.CloseSocket, hintsA.IoctlSocket, hintsA.WsaGetLastError, hintsA.Bind, hintsA.SetSockOpt, hintsA.WsaIoctl, hintsA.WsaSend, hintsB.WsaRecv, hintsB.WsaPoll, hintsB.Listen, hintsB.Accept];
        int[] crypt32Hints = [hintsB.CertOpenSystemStore, hintsB.CertEnumCertificatesInStore, hintsB.CertCloseStore];

        // Import call thunks live at the end of .text (one per external import symbol), so the
        // .rdata base must account for them before any RVA-dependent content is written.
        int externalThunkCount = externalImports.Sum(static import => import.SymbolNames.Length);
        uint rdataRva = AlignUp(checked(PeTextRva + (uint)(WindowsTextPrefixLength + parsed.TextBytes.Length + externalThunkCount * WindowsImportThunkLength)), PeSectionAlignment);

        WindowsIatLayout iat = WriteWindowsIatIlt(rdataStream, kernel32Hints, shell32Hints, ws2Hints, crypt32Hints, externalImports, externalHintOffsets, rdataRva);
        (int importDirOffset, int importDirSize) = WriteWindowsImportDirectory(
            rdataStream, iat, externalNameOffsets, kernelNameOffset, shellNameOffset, ws2NameOffset, crypt32NameOffset, rdataRva);
        (ulong payloadStartVa, ulong payloadEndVa) = WriteWindowsPayload(rdataStream, linkedPayload, rdataRva);

        var layout = new WindowsRdataLayout(
            extra.ExtraSectionOffsets, extra.BssTotalSize, extra.BssSectionOffsets, externalImports, rdataRva,
            iat.Kernel32IatOffset, iat.Shell32IatOffset, iat.Ws2IatOffset, iat.Crypt32IatOffset, iat.ExternalIatOffsets,
            iat.IatSectionOffset, iat.IatEndOffset, importDirOffset, importDirSize, payloadStartVa, payloadEndVa,
            externalThunkCount);
        return (rdataStream.ToArray(), layout);
    }

    private static WindowsExtraSectionsLayout WriteWindowsExtraSections(
        MemoryStream rdataStream,
        byte[] objectBytes,
        ParsedCoffObject parsed)
    {
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
            bool isBss = string.Equals(section.Name, ".bss", StringComparison.Ordinal) || (section.PointerToRawData == 0 && totalSize > 0);
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

        return new WindowsExtraSectionsLayout(extraSectionOffsets, bssTotalSize, bssSectionOffsets);
    }

    private static void WriteWindowsDllNames(
        MemoryStream rdataStream,
        out int kernelNameOffset,
        out int shellNameOffset,
        out int ws2NameOffset,
        out int crypt32NameOffset)
    {
        // Write DLL name strings
        AlignStream(rdataStream, 2);
        kernelNameOffset = (int)rdataStream.Position;
        rdataStream.Write(Encoding.ASCII.GetBytes("KERNEL32.DLL\0"));
        shellNameOffset = (int)rdataStream.Position;
        rdataStream.Write(Encoding.ASCII.GetBytes("SHELL32.DLL\0"));
        ws2NameOffset = (int)rdataStream.Position;
        rdataStream.Write(Encoding.ASCII.GetBytes("WS2_32.DLL\0"));
        crypt32NameOffset = (int)rdataStream.Position;
        rdataStream.Write(Encoding.ASCII.GetBytes("CRYPT32.DLL\0"));
    }

    private static Dictionary<string, int> WriteWindowsExternalDllNames(
        MemoryStream rdataStream,
        WindowsImportLibrary[] externalImports)
    {
        var externalNameOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in externalImports)
        {
            externalNameOffsets[import.LibraryName] = (int)rdataStream.Position;
            rdataStream.Write(Encoding.ASCII.GetBytes(import.LibraryName));
            rdataStream.WriteByte(0);
        }

        return externalNameOffsets;
    }

    private static WindowsHintOffsetsA WriteWindowsHintNamesA(MemoryStream rdataStream)
    {
        // Write Hint/Name entries for each import function (first half, in stream order)
        return new WindowsHintOffsetsA(
            ExitProcess: WriteHintName(rdataStream, 0, "ExitProcess"),
            GetStdHandle: WriteHintName(rdataStream, 0, "GetStdHandle"),
            WriteFile: WriteHintName(rdataStream, 0, "WriteFile"),
            ReadFile: WriteHintName(rdataStream, 0, "ReadFile"),
            CreateFile: WriteHintName(rdataStream, 0, "CreateFileA"),
            CloseHandle: WriteHintName(rdataStream, 0, "CloseHandle"),
            GetFileAttributes: WriteHintName(rdataStream, 0, "GetFileAttributesA"),
            GetCommandLine: WriteHintName(rdataStream, 0, "GetCommandLineW"),
            WideCharToMultiByte: WriteHintName(rdataStream, 0, "WideCharToMultiByte"),
            LocalFree: WriteHintName(rdataStream, 0, "LocalFree"),
            CommandLineToArgv: WriteHintName(rdataStream, 0, "CommandLineToArgvW"),
            WsaStartup: WriteHintName(rdataStream, 0, "WSAStartup"),
            Socket: WriteHintName(rdataStream, 0, "socket"),
            Connect: WriteHintName(rdataStream, 0, "connect"),
            Send: WriteHintName(rdataStream, 0, "send"),
            Recv: WriteHintName(rdataStream, 0, "recv"),
            CloseSocket: WriteHintName(rdataStream, 0, "closesocket"),
            IoctlSocket: WriteHintName(rdataStream, 0, "ioctlsocket"),
            WsaGetLastError: WriteHintName(rdataStream, 0, "WSAGetLastError"),
            Bind: WriteHintName(rdataStream, 0, "bind"),
            SetSockOpt: WriteHintName(rdataStream, 0, "setsockopt"),
            WsaIoctl: WriteHintName(rdataStream, 0, "WSAIoctl"),
            WsaSend: WriteHintName(rdataStream, 0, "WSASend"));
    }

    private static WindowsHintOffsetsB WriteWindowsHintNamesB(MemoryStream rdataStream)
    {
        // Write Hint/Name entries for each import function (second half, in stream order)
        return new WindowsHintOffsetsB(
            WsaRecv: WriteHintName(rdataStream, 0, "WSARecv"),
            WsaPoll: WriteHintName(rdataStream, 0, "WSAPoll"),
            Listen: WriteHintName(rdataStream, 0, "listen"),
            Accept: WriteHintName(rdataStream, 0, "accept"),
            Sleep: WriteHintName(rdataStream, 0, "Sleep"),
            GetTickCount64: WriteHintName(rdataStream, 0, "GetTickCount64"),
            VirtualAlloc: WriteHintName(rdataStream, 0, "VirtualAlloc"),
            VirtualFree: WriteHintName(rdataStream, 0, "VirtualFree"),
            CreateIoCompletionPort: WriteHintName(rdataStream, 0, "CreateIoCompletionPort"),
            GetQueuedCompletionStatus: WriteHintName(rdataStream, 0, "GetQueuedCompletionStatus"),
            LoadLibrary: WriteHintName(rdataStream, 0, "LoadLibraryA"),
            GetProcAddress: WriteHintName(rdataStream, 0, "GetProcAddress"),
            CertOpenSystemStore: WriteHintName(rdataStream, 0, "CertOpenSystemStoreA"),
            CertEnumCertificatesInStore: WriteHintName(rdataStream, 0, "CertEnumCertificatesInStore"),
            CertCloseStore: WriteHintName(rdataStream, 0, "CertCloseStore"),
            CreatePipe: WriteHintName(rdataStream, 0, "CreatePipe"),
            CreateProcessA: WriteHintName(rdataStream, 0, "CreateProcessA"),
            TerminateProcess: WriteHintName(rdataStream, 0, "TerminateProcess"),
            WaitForSingleObject: WriteHintName(rdataStream, 0, "WaitForSingleObject"),
            GetExitCodeProcess: WriteHintName(rdataStream, 0, "GetExitCodeProcess"),
            CreateThread: WriteHintName(rdataStream, 0, "CreateThread"),
            GetSystemInfo: WriteHintName(rdataStream, 0, "GetSystemInfo"),
            GetConsoleMode: WriteHintName(rdataStream, 0, "GetConsoleMode"),
            SetConsoleMode: WriteHintName(rdataStream, 0, "SetConsoleMode"));
    }

    private static Dictionary<string, int> WriteWindowsExternalHints(
        MemoryStream rdataStream,
        WindowsImportLibrary[] externalImports)
    {
        var externalHintOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var import in externalImports)
        {
            foreach (var symbol in import.SymbolNames)
            {
                externalHintOffsets[symbol] = WriteHintName(rdataStream, 0, symbol);
            }
        }

        return externalHintOffsets;
    }

    private static WindowsIatLayout WriteWindowsIatIlt(
        MemoryStream rdataStream,
        int[] kernel32Hints,
        int[] shell32Hints,
        int[] ws2Hints,
        int[] crypt32Hints,
        WindowsImportLibrary[] externalImports,
        Dictionary<string, int> externalHintOffsets,
        uint rdataRva)
    {
        // Write IAT (Import Address Table) — 8 bytes per entry + 8-byte null terminator per DLL
        AlignStream(rdataStream, 8);
        int iatSectionOffset = (int)rdataStream.Position;

        int kernel32IatOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, kernel32Hints, rdataRva);
        int shell32IatOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, shell32Hints, rdataRva);
        int ws2IatOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, ws2Hints, rdataRva);
        int crypt32IatOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, crypt32Hints, rdataRva);
        var externalIatOffsets = new List<(WindowsImportLibrary Import, int Offset)>();
        foreach (var import in externalImports)
        {
            int offset = (int)rdataStream.Position;
            WriteImportAddressTable(rdataStream, import.SymbolNames.Select(symbol => externalHintOffsets[symbol]).ToArray(), rdataRva);
            externalIatOffsets.Add((import, offset));
        }

        int iatEndOffset = (int)rdataStream.Position;

        // Write ILT (Import Lookup Table) — identical structure to IAT
        int kernel32IltOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, kernel32Hints, rdataRva);
        int shell32IltOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, shell32Hints, rdataRva);
        int ws2IltOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, ws2Hints, rdataRva);
        int crypt32IltOffset = (int)rdataStream.Position;
        WriteImportAddressTable(rdataStream, crypt32Hints, rdataRva);
        var externalIltOffsets = new List<(WindowsImportLibrary Import, int Offset)>();
        foreach (var import in externalImports)
        {
            int offset = (int)rdataStream.Position;
            WriteImportAddressTable(rdataStream, import.SymbolNames.Select(symbol => externalHintOffsets[symbol]).ToArray(), rdataRva);
            externalIltOffsets.Add((import, offset));
        }

        return new WindowsIatLayout(
            iatSectionOffset, kernel32IatOffset, shell32IatOffset, ws2IatOffset, crypt32IatOffset,
            externalIatOffsets, iatEndOffset, kernel32IltOffset, shell32IltOffset, ws2IltOffset,
            crypt32IltOffset, externalIltOffsets);
    }

    private static (int ImportDirOffset, int ImportDirSize) WriteWindowsImportDirectory(
        MemoryStream rdataStream,
        WindowsIatLayout iat,
        Dictionary<string, int> externalNameOffsets,
        int kernelNameOffset,
        int shellNameOffset,
        int ws2NameOffset,
        int crypt32NameOffset,
        uint rdataRva)
    {
        // Write Import Directory Table (fixed imports + external imports + null terminator)
        // Each entry is 20 bytes: ILT RVA, TimeDateStamp, ForwarderChain, Name RVA, IAT RVA
        int importDirOffset = (int)rdataStream.Position;
        WriteImportDirectoryEntry(rdataStream, rdataRva + (uint)iat.Kernel32IltOffset, rdataRva + (uint)kernelNameOffset, rdataRva + (uint)iat.Kernel32IatOffset);
        WriteImportDirectoryEntry(rdataStream, rdataRva + (uint)iat.Shell32IltOffset, rdataRva + (uint)shellNameOffset, rdataRva + (uint)iat.Shell32IatOffset);
        WriteImportDirectoryEntry(rdataStream, rdataRva + (uint)iat.Ws2IltOffset, rdataRva + (uint)ws2NameOffset, rdataRva + (uint)iat.Ws2IatOffset);
        WriteImportDirectoryEntry(rdataStream, rdataRva + (uint)iat.Crypt32IltOffset, rdataRva + (uint)crypt32NameOffset, rdataRva + (uint)iat.Crypt32IatOffset);
        for (int i = 0; i < iat.ExternalIatOffsets.Count; i++)
        {
            var import = iat.ExternalIatOffsets[i].Import;
            WriteImportDirectoryEntry(
                rdataStream,
                rdataRva + (uint)iat.ExternalIltOffsets[i].Offset,
                rdataRva + (uint)externalNameOffsets[import.LibraryName],
                rdataRva + (uint)iat.ExternalIatOffsets[i].Offset);
        }
        // Null terminator entry (20 zero bytes)
        for (int i = 0; i < 20; i++)
        {
            rdataStream.WriteByte(0);
        }

        int importDirSize = (int)rdataStream.Position - importDirOffset;
        return (importDirOffset, importDirSize);
    }

    private static (ulong PayloadStartVa, ulong PayloadEndVa) WriteWindowsPayload(
        MemoryStream rdataStream,
        LinkedImagePayload? linkedPayload,
        uint rdataRva)
    {
        ulong payloadStartVa = 0;
        ulong payloadEndVa = 0;
        if (linkedPayload is LinkedImagePayload payload)
        {
            AlignStream(rdataStream, payload.Alignment);
            int payloadOffset = (int)rdataStream.Position;
            rdataStream.Write(payload.Bytes, 0, payload.Bytes.Length);
            payloadStartVa = PeImageBase + rdataRva + (uint)payloadOffset;
            payloadEndVa = payloadStartVa + (ulong)payload.Bytes.Length;
        }

        return (payloadStartVa, payloadEndVa);
    }

    private readonly record struct WindowsKernel32IatVas(
        ulong ExitProcess, ulong GetStdHandle, ulong WriteFile, ulong ReadFile, ulong CreateFile,
        ulong CloseHandle, ulong GetFileAttributes, ulong GetCommandLine, ulong WideCharToMultiByte,
        ulong LocalFree, ulong Sleep, ulong VirtualAlloc, ulong VirtualFree, ulong CreateIoCompletionPort,
        ulong GetQueuedCompletionStatus, ulong LoadLibrary, ulong GetProcAddress, ulong CreatePipe,
        ulong CreateProcessA, ulong TerminateProcess, ulong WaitForSingleObject, ulong GetExitCodeProcess,
        ulong CreateThread, ulong GetSystemInfo, ulong GetTickCount64, ulong GetConsoleMode, ulong SetConsoleMode);

    private readonly record struct WindowsNetIatVas(
        ulong CommandLineToArgv, ulong WsaStartup, ulong Socket, ulong Connect, ulong Send, ulong Recv,
        ulong CloseSocket, ulong IoctlSocket, ulong WsaGetLastError, ulong Bind, ulong SetSockOpt,
        ulong WsaIoctl, ulong WsaSend, ulong WsaRecv, ulong WsaPoll, ulong Listen, ulong Accept,
        ulong CertOpenSystemStore, ulong CertEnumCertificatesInStore, ulong CertCloseStore);

    private readonly record struct WindowsSymbolResolution(
        Dictionary<string, ulong> ExternalSymbolVas,
        Dictionary<int, ulong> SectionBaseVas,
        uint BssRva,
        byte[] ExternalThunkBytes,
        ulong ExitProcessIatVa);

    private static WindowsSymbolResolution ResolveWindowsSymbols(
        byte[] rdataBytes,
        WindowsRdataLayout layout,
        ParsedCoffObject parsed,
        LinkedImagePayload? linkedPayload)
    {
        // Compute IAT VAs from recorded offsets — each entry is 8 bytes.
        ulong kernel32IatVa = PeImageBase + layout.RdataRva + (ulong)layout.Kernel32IatOffset;
        ulong shell32IatVa = PeImageBase + layout.RdataRva + (ulong)layout.Shell32IatOffset;
        ulong ws2IatVa = PeImageBase + layout.RdataRva + (ulong)layout.Ws2IatOffset;
        ulong crypt32IatVa = PeImageBase + layout.RdataRva + (ulong)layout.Crypt32IatOffset;

        WindowsKernel32IatVas kernelVas = ComputeWindowsKernel32IatVas(kernel32IatVa);
        WindowsNetIatVas netVas = ComputeWindowsNetIatVas(shell32IatVa, ws2IatVa, crypt32IatVa);
        ulong chkstkStubVa = PeImageBase + PeTextRva + WindowsTrampolineLength;
        var externalSymbolVas = BuildWindowsExternalSymbolVas(kernelVas, netVas, chkstkStubVa);

        (Dictionary<int, ulong> sectionBaseVas, uint bssRva) = BuildWindowsSectionBaseVas(layout, rdataBytes.Length);
        byte[] externalThunkBytes = BuildWindowsExternalThunks(layout, externalSymbolVas, parsed, linkedPayload);

        return new WindowsSymbolResolution(externalSymbolVas, sectionBaseVas, bssRva, externalThunkBytes, kernel32IatVa);
    }

    private static WindowsKernel32IatVas ComputeWindowsKernel32IatVas(ulong kernel32IatVa)
    {
        return new WindowsKernel32IatVas(
            ExitProcess: kernel32IatVa,
            GetStdHandle: kernel32IatVa + 1 * 8,
            WriteFile: kernel32IatVa + 2 * 8,
            ReadFile: kernel32IatVa + 3 * 8,
            CreateFile: kernel32IatVa + 4 * 8,
            CloseHandle: kernel32IatVa + 5 * 8,
            GetFileAttributes: kernel32IatVa + 6 * 8,
            GetCommandLine: kernel32IatVa + 7 * 8,
            WideCharToMultiByte: kernel32IatVa + 8 * 8,
            LocalFree: kernel32IatVa + 9 * 8,
            Sleep: kernel32IatVa + 10 * 8,
            VirtualAlloc: kernel32IatVa + 11 * 8,
            VirtualFree: kernel32IatVa + 12 * 8,
            CreateIoCompletionPort: kernel32IatVa + 13 * 8,
            GetQueuedCompletionStatus: kernel32IatVa + 14 * 8,
            LoadLibrary: kernel32IatVa + 15 * 8,
            GetProcAddress: kernel32IatVa + 16 * 8,
            CreatePipe: kernel32IatVa + 17 * 8,
            CreateProcessA: kernel32IatVa + 18 * 8,
            TerminateProcess: kernel32IatVa + 19 * 8,
            WaitForSingleObject: kernel32IatVa + 20 * 8,
            GetExitCodeProcess: kernel32IatVa + 21 * 8,
            CreateThread: kernel32IatVa + 22 * 8,
            GetSystemInfo: kernel32IatVa + 23 * 8,
            GetTickCount64: kernel32IatVa + 24 * 8,
            GetConsoleMode: kernel32IatVa + 25 * 8,
            SetConsoleMode: kernel32IatVa + 26 * 8);
    }

    private static WindowsNetIatVas ComputeWindowsNetIatVas(ulong shell32IatVa, ulong ws2IatVa, ulong crypt32IatVa)
    {
        return new WindowsNetIatVas(
            CommandLineToArgv: shell32IatVa,
            WsaStartup: ws2IatVa,
            Socket: ws2IatVa + 1 * 8,
            Connect: ws2IatVa + 2 * 8,
            Send: ws2IatVa + 3 * 8,
            Recv: ws2IatVa + 4 * 8,
            CloseSocket: ws2IatVa + 5 * 8,
            IoctlSocket: ws2IatVa + 6 * 8,
            WsaGetLastError: ws2IatVa + 7 * 8,
            Bind: ws2IatVa + 8 * 8,
            SetSockOpt: ws2IatVa + 9 * 8,
            WsaIoctl: ws2IatVa + 10 * 8,
            WsaSend: ws2IatVa + 11 * 8,
            WsaRecv: ws2IatVa + 12 * 8,
            WsaPoll: ws2IatVa + 13 * 8,
            Listen: ws2IatVa + 14 * 8,
            Accept: ws2IatVa + 15 * 8,
            CertOpenSystemStore: crypt32IatVa,
            CertEnumCertificatesInStore: crypt32IatVa + 1 * 8,
            CertCloseStore: crypt32IatVa + 2 * 8);
    }

    private static Dictionary<string, ulong> BuildWindowsExternalSymbolVas(
        WindowsKernel32IatVas kernel,
        WindowsNetIatVas net,
        ulong chkstkStubVa)
    {
        return new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["__imp_ExitProcess"] = kernel.ExitProcess,
            ["__imp_GetStdHandle"] = kernel.GetStdHandle,
            ["__imp_WriteFile"] = kernel.WriteFile,
            ["__imp_ReadFile"] = kernel.ReadFile,
            ["__imp_CreateFileA"] = kernel.CreateFile,
            ["__imp_CloseHandle"] = kernel.CloseHandle,
            ["__imp_GetFileAttributesA"] = kernel.GetFileAttributes,
            ["__imp_GetCommandLineW"] = kernel.GetCommandLine,
            ["__imp_WideCharToMultiByte"] = kernel.WideCharToMultiByte,
            ["__imp_LocalFree"] = kernel.LocalFree,
            ["__imp_Sleep"] = kernel.Sleep,
            ["__imp_GetTickCount64"] = kernel.GetTickCount64,
            ["__imp_VirtualAlloc"] = kernel.VirtualAlloc,
            ["__imp_VirtualFree"] = kernel.VirtualFree,
            ["__imp_CreateIoCompletionPort"] = kernel.CreateIoCompletionPort,
            ["__imp_GetQueuedCompletionStatus"] = kernel.GetQueuedCompletionStatus,
            ["__imp_LoadLibraryA"] = kernel.LoadLibrary,
            ["__imp_GetProcAddress"] = kernel.GetProcAddress,
            ["__imp_CommandLineToArgvW"] = net.CommandLineToArgv,
            ["__imp_WSAStartup"] = net.WsaStartup,
            ["__imp_socket"] = net.Socket,
            ["__imp_connect"] = net.Connect,
            ["__imp_send"] = net.Send,
            ["__imp_recv"] = net.Recv,
            ["__imp_closesocket"] = net.CloseSocket,
            ["__imp_ioctlsocket"] = net.IoctlSocket,
            ["__imp_WSAGetLastError"] = net.WsaGetLastError,
            ["__imp_bind"] = net.Bind,
            ["__imp_setsockopt"] = net.SetSockOpt,
            ["__imp_WSAIoctl"] = net.WsaIoctl,
            ["__imp_WSASend"] = net.WsaSend,
            ["__imp_WSARecv"] = net.WsaRecv,
            ["__imp_WSAPoll"] = net.WsaPoll,
            ["__imp_listen"] = net.Listen,
            ["__imp_accept"] = net.Accept,
            ["__imp_CertOpenSystemStoreA"] = net.CertOpenSystemStore,
            ["__imp_CertEnumCertificatesInStore"] = net.CertEnumCertificatesInStore,
            ["__imp_CertCloseStore"] = net.CertCloseStore,
            ["__imp_CreatePipe"] = kernel.CreatePipe,
            ["__imp_CreateProcessA"] = kernel.CreateProcessA,
            ["__imp_TerminateProcess"] = kernel.TerminateProcess,
            ["__imp_WaitForSingleObject"] = kernel.WaitForSingleObject,
            ["__imp_GetExitCodeProcess"] = kernel.GetExitCodeProcess,
            ["__imp_CreateThread"] = kernel.CreateThread,
            ["__imp_GetSystemInfo"] = kernel.GetSystemInfo,
            ["__imp_GetConsoleMode"] = kernel.GetConsoleMode,
            ["__imp_SetConsoleMode"] = kernel.SetConsoleMode,
            ["__chkstk"] = chkstkStubVa,
        };
    }

    private static (Dictionary<int, ulong> SectionBaseVas, uint BssRva) BuildWindowsSectionBaseVas(
        WindowsRdataLayout layout,
        int rdataLength)
    {
        var sectionBaseVas = layout.ExtraSectionOffsets.ToDictionary(
            static pair => pair.Key,
            pair => PeImageBase + layout.RdataRva + pair.Value);
        uint bssRva = 0;
        if (layout.BssTotalSize > 0)
        {
            bssRva = AlignUp(checked(layout.RdataRva + (uint)rdataLength), PeSectionAlignment);
            foreach (var pair in layout.BssSectionOffsets)
            {
                sectionBaseVas[pair.Key] = PeImageBase + bssRva + pair.Value;
            }
        }

        return (sectionBaseVas, bssRva);
    }

    private static byte[] BuildWindowsExternalThunks(
        WindowsRdataLayout layout,
        Dictionary<string, ulong> externalSymbolVas,
        ParsedCoffObject parsed,
        LinkedImagePayload? linkedPayload)
    {
        // Each external import symbol gets its IAT slot exposed as `__imp_<name>` (indirect
        // references) plus a call thunk at the end of .text exposed as `<name>` (direct calls and
        // address-taken references from statically linked bitcode, e.g. Mbed TLS).
        ulong externalThunkBaseVa = PeImageBase + PeTextRva + (ulong)(WindowsTextPrefixLength + parsed.TextBytes.Length);
        byte[] externalThunkBytes = new byte[layout.ExternalThunkCount * WindowsImportThunkLength];
        int externalThunkIndex = 0;
        foreach (var (import, offset) in layout.ExternalIatOffsets)
        {
            for (int i = 0; i < import.SymbolNames.Length; i++)
            {
                ulong iatEntryVa = PeImageBase + layout.RdataRva + (ulong)offset + (ulong)i * 8UL;
                externalSymbolVas["__imp_" + import.SymbolNames[i]] = iatEntryVa;

                ulong thunkVa = externalThunkBaseVa + (ulong)(externalThunkIndex * WindowsImportThunkLength);
                int thunkOffset = externalThunkIndex * WindowsImportThunkLength;
                externalThunkBytes[thunkOffset] = 0xFF; // jmp qword ptr [rip+disp32]
                externalThunkBytes[thunkOffset + 1] = 0x25;
                BinaryPrimitives.WriteInt32LittleEndian(
                    externalThunkBytes.AsSpan(thunkOffset + 2, 4),
                    checked((int)((long)iatEntryVa - (long)(thunkVa + 6))));
                externalThunkBytes[thunkOffset + 6] = 0xCC; // int3 padding
                externalThunkBytes[thunkOffset + 7] = 0xCC;
                externalSymbolVas[import.SymbolNames[i]] = thunkVa;
                externalThunkIndex++;
            }
        }

        if (linkedPayload is LinkedImagePayload windowsPayload)
        {
            externalSymbolVas[windowsPayload.StartSymbolName] = layout.PayloadStartVa;
            externalSymbolVas[windowsPayload.EndSymbolName] = layout.PayloadEndVa;
        }

        return externalThunkBytes;
    }

    private static byte[] ApplyWindowsRelocations(
        byte[] objectBytes,
        ParsedCoffObject parsed,
        byte[] rdataBytes,
        WindowsRdataLayout layout,
        WindowsSymbolResolution resolution)
    {
        ApplyCoffTextRelocations(
            objectBytes,
            parsed.TextBytes,
            parsed.TextSection,
            parsed.SymbolTableOffset,
            parsed.SymbolCount,
            parsed.TextSectionNumber,
            resolution.SectionBaseVas,
            resolution.ExternalSymbolVas);
        byte[] codeBytes = BuildWindowsTrampoline(parsed.EntryOffsetInText, resolution.ExitProcessIatVa)
            .Concat(BuildWindowsChkstkStub())
            .Concat(parsed.TextBytes)
            .Concat(resolution.ExternalThunkBytes)
            .ToArray();

        // Patch relocations that live inside data sections — most importantly a `switch` jump table
        // in `.rdata`, whose 8-byte entries are absolute `.text` block addresses
        // (IMAGE_REL_AMD64_ADDR64) that the static linker must fill in.
        ApplyCoffDataRelocations(
            objectBytes,
            rdataBytes,
            parsed.Sections,
            parsed.TextSectionNumber,
            layout.ExtraSectionOffsets,
            parsed.SymbolTableOffset,
            parsed.SymbolCount,
            resolution.SectionBaseVas,
            resolution.ExternalSymbolVas);

        ApplyCoffDebugRelocations(
            objectBytes,
            parsed.DebugSections,
            parsed.SymbolTableOffset,
            parsed.SymbolCount,
            parsed.TextSectionNumber,
            resolution.SectionBaseVas,
            resolution.ExternalSymbolVas);

        return codeBytes;
    }

    private readonly record struct WindowsSectionLayout(
        bool HasBss,
        int DebugSectionCount,
        int SectionCount,
        uint HeadersFileSize,
        uint TextFileOffset,
        uint TextRawSize,
        uint RdataFileOffset,
        uint RdataRawSize,
        uint SizeOfImage,
        uint TotalFileSize,
        uint[] DebugRvas,
        uint[] DebugFileOffsets,
        uint DebugRvaCursor,
        uint DebugFileCursor,
        uint BssRva,
        uint BssTotalSize);

    private readonly record struct WindowsDebugStringTable(
        byte[] StringTableBytes,
        uint StringTableFileOffset,
        uint SizeOfImage,
        uint TotalFileSize,
        string[] DebugNameFields);

    private static byte[] WriteWindowsImage(
        ParsedCoffObject parsed,
        byte[] codeBytes,
        byte[] rdataBytes,
        WindowsRdataLayout layout,
        WindowsSymbolResolution resolution)
    {
        WindowsSectionLayout sections = ComputeWindowsSectionLayout(parsed, codeBytes.Length, rdataBytes.Length, layout, resolution.BssRva);
        WindowsDebugStringTable debugStr = BuildWindowsDebugStringTable(parsed, sections);

        var output = new byte[debugStr.TotalFileSize];
        WriteWindowsDosAndCoffHeader(output, sections.SectionCount, debugStr.StringTableFileOffset);
        WriteWindowsOptionalHeader(output, sections, layout.BssTotalSize, debugStr.SizeOfImage);
        WriteWindowsDataDirectories(output, layout);
        WriteWindowsSectionHeaders(output, parsed, sections, layout, debugStr, codeBytes.Length, rdataBytes.Length);
        WriteWindowsSectionData(output, parsed, codeBytes, rdataBytes, sections, debugStr);
        return output;
    }

    private static WindowsSectionLayout ComputeWindowsSectionLayout(
        ParsedCoffObject parsed,
        int codeLength,
        int rdataLength,
        WindowsRdataLayout layout,
        uint bssRva)
    {
        // Compute section count and file layout
        bool hasBss = layout.BssTotalSize > 0;
        int debugSectionCount = parsed.DebugSections.Count;
        int sectionCount = (hasBss ? 3 : 2) + debugSectionCount; // .text, .rdata, optionally .bss, debug sections
        int headersSize = PeDosHeaderSize + PeSignatureSize + PeCoffHeaderSize + PeOptionalHeaderSize + sectionCount * PeSectionHeaderSize;
        uint headersFileSize = AlignUp((uint)headersSize, PeFileAlignment);

        uint textFileOffset = headersFileSize;
        uint textRawSize = AlignUp((uint)codeLength, PeFileAlignment);

        uint rdataFileOffset = textFileOffset + textRawSize;
        uint rdataRawSize = AlignUp((uint)rdataLength, PeFileAlignment);

        uint bssFileOffset = 0;
        if (hasBss)
        {
            bssFileOffset = rdataFileOffset + rdataRawSize;
        }

        // Compute SizeOfImage (the last section VA + its aligned virtual size)
        uint sizeOfImage;
        if (hasBss)
        {
            sizeOfImage = bssRva + AlignUp(layout.BssTotalSize, PeSectionAlignment);
        }
        else
        {
            sizeOfImage = layout.RdataRva + AlignUp((uint)rdataLength, PeSectionAlignment);
        }

        uint totalFileSize = hasBss ? bssFileOffset : rdataFileOffset + rdataRawSize;

        // Lay out debug sections after all loadable content. They are mapped (the PE format has
        // no non-ALLOC sections) but marked discardable; debuggers locate them by name.
        var debugRvas = new uint[debugSectionCount];
        var debugFileOffsets = new uint[debugSectionCount];
        uint debugRvaCursor = sizeOfImage;
        uint debugFileCursor = totalFileSize;
        for (int i = 0; i < debugSectionCount; i++)
        {
            debugRvas[i] = debugRvaCursor;
            debugFileOffsets[i] = debugFileCursor;
            debugRvaCursor = AlignUp(checked(debugRvaCursor + (uint)parsed.DebugSections[i].Bytes.Length), PeSectionAlignment);
            debugFileCursor = AlignUp(checked(debugFileCursor + (uint)parsed.DebugSections[i].Bytes.Length), PeFileAlignment);
        }

        return new WindowsSectionLayout(
            hasBss, debugSectionCount, sectionCount, headersFileSize, textFileOffset, textRawSize,
            rdataFileOffset, rdataRawSize, sizeOfImage, totalFileSize, debugRvas, debugFileOffsets,
            debugRvaCursor, debugFileCursor, bssRva, layout.BssTotalSize);
    }

    private static WindowsDebugStringTable BuildWindowsDebugStringTable(
        ParsedCoffObject parsed,
        WindowsSectionLayout sections)
    {
        // PE section names longer than 8 characters (every .debug_* name) go through a COFF
        // string table referenced as "/<offset>" — the MinGW/LLD convention debuggers understand.
        // The table sits at the end of the file with PointerToSymbolTable aiming at it and a
        // symbol count of zero.
        uint stringTableFileOffset = 0;
        byte[] stringTableBytes = [];
        var debugNameFields = new string[sections.DebugSectionCount];
        uint sizeOfImage = sections.SizeOfImage;
        uint totalFileSize = sections.TotalFileSize;
        if (sections.DebugSectionCount > 0)
        {
            sizeOfImage = sections.DebugRvaCursor;
            using var stringTable = new MemoryStream();
            stringTable.Write(new byte[4]); // total-size field, patched below
            for (int i = 0; i < sections.DebugSectionCount; i++)
            {
                string name = parsed.DebugSections[i].Name;
                if (name.Length <= 8)
                {
                    debugNameFields[i] = name;
                    continue;
                }

                debugNameFields[i] = "/" + stringTable.Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                stringTable.Write(Encoding.ASCII.GetBytes(name));
                stringTable.WriteByte(0);
            }

            stringTableBytes = stringTable.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(stringTableBytes.AsSpan(0, 4), (uint)stringTableBytes.Length);
            stringTableFileOffset = sections.DebugFileCursor;
            totalFileSize = checked(stringTableFileOffset + (uint)stringTableBytes.Length);
        }

        return new WindowsDebugStringTable(stringTableBytes, stringTableFileOffset, sizeOfImage, totalFileSize, debugNameFields);
    }

    private static void WriteWindowsDosAndCoffHeader(byte[] output, int sectionCount, uint stringTableFileOffset)
    {
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
        // With debug sections present, PointerToSymbolTable references the (empty) symbol table
        // whose trailing string table resolves the "/<offset>" long section names.
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(coffOff + 8, 4), stringTableFileOffset); // PointerToSymbolTable
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(coffOff + 12, 4), 0);      // NumberOfSymbols
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(coffOff + 16, 2), PeOptionalHeaderSize); // SizeOfOptionalHeader
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(coffOff + 18, 2), 0x0022); // Characteristics: EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE
    }

    private static void WriteWindowsOptionalHeader(
        byte[] output,
        WindowsSectionLayout sections,
        uint bssTotalSize,
        uint sizeOfImage)
    {
        uint stackReserveBytes = ResolvePeStackReserveBytes();

        // Optional header (PE32+)
        int optOff = PeDosHeaderSize + PeSignatureSize + PeCoffHeaderSize;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff, 2), 0x020B);       // Magic: PE32+
        output[optOff + 2] = 1;  // MajorLinkerVersion
        output[optOff + 3] = 0;  // MinorLinkerVersion
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 4, 4), sections.TextRawSize);   // SizeOfCode
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 8, 4), sections.RdataRawSize);  // SizeOfInitializedData
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
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 60, 4), sections.HeadersFileSize);   // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 64, 4), 0);  // CheckSum
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 68, 2), 3);  // Subsystem: CONSOLE
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(optOff + 70, 2), 0x8100); // DllCharacteristics: NX_COMPAT | TERMINAL_SERVER_AWARE
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 72, 8), stackReserveBytes); // SizeOfStackReserve
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 80, 8), PeStackCommitBytes); // SizeOfStackCommit
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 88, 8), 0x100000);  // SizeOfHeapReserve
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(optOff + 96, 8), 0x1000);    // SizeOfHeapCommit
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 104, 4), 0);        // LoaderFlags
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optOff + 108, 4), 16);       // NumberOfRvaAndSizes
    }

    private static void WriteWindowsDataDirectories(byte[] output, WindowsRdataLayout layout)
    {
        // Data directories (16 entries, each 8 bytes = 128 bytes starting at optOff+112)
        // Entry 1 (index 1): Import Directory
        int dataDirOff = PeDosHeaderSize + PeSignatureSize + PeCoffHeaderSize + 112;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirOff + 8, 4), layout.RdataRva + (uint)layout.ImportDirOffset);  // Import Table RVA
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirOff + 12, 4), (uint)layout.ImportDirSize);               // Import Table Size

        // Entry 12 (index 12): IAT Directory
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirOff + 96, 4), layout.RdataRva + (uint)layout.IatSectionOffset); // IAT RVA
        int iatTotalSize = layout.IatEndOffset - layout.IatSectionOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirOff + 100, 4), (uint)iatTotalSize); // IAT Size
    }

    private static void WriteWindowsSectionHeaders(
        byte[] output,
        ParsedCoffObject parsed,
        WindowsSectionLayout sections,
        WindowsRdataLayout layout,
        WindowsDebugStringTable debugStr,
        int codeLength,
        int rdataLength)
    {
        // Section headers
        int secOff = PeHeadersStart;

        // .text section header
        Encoding.ASCII.GetBytes(".text").CopyTo(output.AsSpan(secOff));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 8, 4), (uint)codeLength);   // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 12, 4), PeTextRva);                // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 16, 4), sections.TextRawSize);              // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 20, 4), sections.TextFileOffset);           // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 36, 4), 0x60000020);               // Characteristics: CODE | EXECUTE | READ
        secOff += PeSectionHeaderSize;

        // .rdata section header
        Encoding.ASCII.GetBytes(".rdata").CopyTo(output.AsSpan(secOff));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 8, 4), (uint)rdataLength);   // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 12, 4), layout.RdataRva);                  // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 16, 4), sections.RdataRawSize);              // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 20, 4), sections.RdataFileOffset);           // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 36, 4), 0x40000040);               // Characteristics: INITIALIZED_DATA | READ
        secOff += PeSectionHeaderSize;

        secOff = WriteWindowsBssSectionHeader(output, sections, secOff);
        WriteWindowsDebugSectionHeaders(output, parsed, sections, debugStr, secOff);
    }

    private static int WriteWindowsBssSectionHeader(byte[] output, WindowsSectionLayout sections, int secOff)
    {
        // .bss section header (if needed)
        if (sections.HasBss)
        {
            Encoding.ASCII.GetBytes(".bss").CopyTo(output.AsSpan(secOff));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 8, 4), sections.BssTotalSize);          // VirtualSize
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 12, 4), sections.BssRva);                // VirtualAddress
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 16, 4), 0);                     // SizeOfRawData (zero for BSS)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 20, 4), 0);                     // PointerToRawData
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 36, 4), 0xC0000080);           // Characteristics: UNINITIALIZED_DATA | READ | WRITE
            secOff += PeSectionHeaderSize;
        }

        return secOff;
    }

    private static void WriteWindowsDebugSectionHeaders(
        byte[] output,
        ParsedCoffObject parsed,
        WindowsSectionLayout sections,
        WindowsDebugStringTable debugStr,
        int secOff)
    {
        // Debug section headers
        for (int i = 0; i < sections.DebugSectionCount; i++)
        {
            Encoding.ASCII.GetBytes(debugStr.DebugNameFields[i]).CopyTo(output.AsSpan(secOff));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 8, 4), (uint)parsed.DebugSections[i].Bytes.Length); // VirtualSize
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 12, 4), sections.DebugRvas[i]);                              // VirtualAddress
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 16, 4), AlignUp((uint)parsed.DebugSections[i].Bytes.Length, PeFileAlignment)); // SizeOfRawData
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 20, 4), sections.DebugFileOffsets[i]);                       // PointerToRawData
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(secOff + 36, 4), 0x42000040);           // Characteristics: INITIALIZED_DATA | DISCARDABLE | READ
            secOff += PeSectionHeaderSize;
        }
    }

    private static void WriteWindowsSectionData(
        byte[] output,
        ParsedCoffObject parsed,
        byte[] codeBytes,
        byte[] rdataBytes,
        WindowsSectionLayout sections,
        WindowsDebugStringTable debugStr)
    {
        // Write section data
        Array.Copy(codeBytes, 0, output, (int)sections.TextFileOffset, codeBytes.Length);
        Array.Copy(rdataBytes, 0, output, (int)sections.RdataFileOffset, rdataBytes.Length);
        for (int i = 0; i < sections.DebugSectionCount; i++)
        {
            Array.Copy(parsed.DebugSections[i].Bytes, 0, output, (int)sections.DebugFileOffsets[i], parsed.DebugSections[i].Bytes.Length);
        }

        if (debugStr.StringTableBytes.Length > 0)
        {
            Array.Copy(debugStr.StringTableBytes, 0, output, (int)debugStr.StringTableFileOffset, debugStr.StringTableBytes.Length);
        }
    }

    private static uint ResolvePeStackReserveBytes()
    {
        string? raw = Environment.GetEnvironmentVariable(PeStackReserveEnvVar);
        if (uint.TryParse(raw, out uint parsed) && parsed >= PeStackCommitBytes)
        {
            return AlignUp(parsed, PeSectionAlignment);
        }

        return PeDefaultStackReserveBytes;
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

    /// <summary>
    /// Scans the object's COFF symbol table for undefined (section 0) symbols that name a known
    /// Windows runtime entry point (<see cref="WindowsRuntimeImportLibraries"/>) and merges them
    /// into the caller-supplied external-library map. Statically linked bitcode payloads reference
    /// libc/platform functions by direct call, so the linker synthesizes the imports the same way
    /// the ELF linker consults its dynamic-import map. Caller-supplied mappings take precedence.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? CollectWindowsRuntimeImports(
        ReadOnlySpan<byte> bytes,
        uint symbolTableOffset,
        uint symbolCount,
        IReadOnlyDictionary<string, string>? externalLibraries)
    {
        Dictionary<string, string>? merged = null;
        for (int symbolIndex = 0; symbolIndex < symbolCount;)
        {
            int offset = checked((int)symbolTableOffset + (symbolIndex * 18));
            short sectionNumber = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset + 12, 2));
            byte auxSymbolCount = bytes[offset + 17];
            if (sectionNumber == 0)
            {
                string name = ReadCoffName(bytes.Slice(offset, 8), bytes, symbolTableOffset, symbolCount);
                if (WindowsRuntimeImportLibraries.TryGetValue(name, out string? library)
                    && (externalLibraries is null || !externalLibraries.ContainsKey(name)))
                {
                    merged ??= externalLibraries is null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : new Dictionary<string, string>(externalLibraries, StringComparer.Ordinal);
                    merged[name] = library;
                }
            }

            symbolIndex += 1 + auxSymbolCount;
        }

        return merged ?? externalLibraries;
    }

    private static WindowsImportLibrary[] BuildWindowsExternalImports(IReadOnlyDictionary<string, string>? externalLibraries)
    {
        if (externalLibraries is null || externalLibraries.Count == 0)
        {
            return [];
        }

        return externalLibraries
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(static pair => NormalizeWindowsLibraryName(pair.Value), StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new WindowsImportLibrary(
                group.Key,
                group.Select(static pair => pair.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
            .Where(static import => import.SymbolNames.Length > 0)
            .ToArray();
    }

    private static string NormalizeWindowsLibraryName(string libraryName)
    {
        var normalized = libraryName.Trim();
        return normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".DLL";
    }

    private sealed record WindowsImportLibrary(string LibraryName, string[] SymbolNames);

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

            if (string.Equals(name, ".text", StringComparison.Ordinal))
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

        // Collect DWARF debug sections (present only for --debug compilations) so they can be
        // carried into the final image for GDB.
        var debugSections = new List<CoffDebugSection>();
        for (int i = 0; i < sectionCount; i++)
        {
            if (sections[i].Name.StartsWith(".debug", StringComparison.Ordinal) && sections[i].SizeOfRawData > 0)
            {
                byte[] sectionBytes = bytes.Slice(checked((int)sections[i].PointerToRawData), checked((int)sections[i].SizeOfRawData)).ToArray();
                debugSections.Add(new CoffDebugSection(sections[i].Name, i + 1, sectionBytes, sections[i]));
            }
        }

        return new ParsedCoffObject(textBytes, entryOffset, textSection, sections, symbolTableOffset, symbolCount, textSectionIndex + 1, debugSections);
    }

    /// <summary>
    /// Applies the relocations inside DWARF debug sections. LLVM emits two kinds there:
    /// <c>IMAGE_REL_AMD64_SECREL</c> for section-relative DWARF references (offsets into
    /// .debug_str, .debug_abbrev, ...) and <c>IMAGE_REL_AMD64_ADDR64</c> for code addresses
    /// (.debug_addr entries). Debug sections keep zero-base semantics, so a SECREL value is
    /// just the target symbol's offset within its section.
    /// </summary>
    private static void ApplyCoffDebugRelocations(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<CoffDebugSection> debugSections,
        uint symbolTableOffset,
        uint symbolCount,
        int textSectionNumber,
        IReadOnlyDictionary<int, ulong> sectionBaseVas,
        IReadOnlyDictionary<string, ulong> importSymbolVas)
    {
        foreach (CoffDebugSection debugSection in debugSections)
        {
            CoffSectionHeader header = debugSection.Header;
            for (int i = 0; i < header.NumberOfRelocations; i++)
            {
                int offset = checked((int)header.PointerToRelocations + i * 10);
                uint relocationOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
                int symbolIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4)));
                ushort relocationType = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 8, 2));
                CoffSymbol symbol = ReadCoffSymbol(bytes, symbolTableOffset, symbolCount, symbolIndex);

                switch (relocationType)
                {
                    case CoffRelocAmd64SecRel:
                        {
                            Span<byte> patch = debugSection.Bytes.AsSpan(checked((int)relocationOffset), 4);
                            uint addend = BinaryPrimitives.ReadUInt32LittleEndian(patch);
                            BinaryPrimitives.WriteUInt32LittleEndian(patch, checked(addend + symbol.Value));
                        }
                        break;
                    case CoffRelocAmd64Addr64:
                        {
                            Span<byte> patch = debugSection.Bytes.AsSpan(checked((int)relocationOffset), 8);
                            ulong addend = BinaryPrimitives.ReadUInt64LittleEndian(patch);
                            ulong targetVa = ResolveCoffTargetVa(symbol, textSectionNumber, sectionBaseVas, importSymbolVas);
                            BinaryPrimitives.WriteUInt64LittleEndian(patch, checked(targetVa + addend));
                        }
                        break;
                    default:
                        throw new InvalidOperationException($"LLVM COFF emitted unsupported debug relocation type 0x{relocationType:X4} in {debugSection.Name}.");
                }
            }
        }
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
            ulong targetVa = ResolveCoffTargetVa(symbol, textSectionNumber, sectionBaseVas, importSymbolVas);

            switch (relocationType)
            {
                case CoffRelocAmd64Addr64:
                    Span<byte> patch64 = textBytes.AsSpan(checked((int)relocationOffset), 8);
                    ulong addend64 = BinaryPrimitives.ReadUInt64LittleEndian(patch64);
                    BinaryPrimitives.WriteUInt64LittleEndian(patch64, checked(targetVa + addend64));
                    break;
                case CoffRelocAmd64Addr32:
                    Span<byte> addr32Patch = textBytes.AsSpan(checked((int)relocationOffset), 4);
                    int addr32Addend = BinaryPrimitives.ReadInt32LittleEndian(addr32Patch);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        addr32Patch,
                        checked((uint)(checked((long)targetVa) + addr32Addend)));
                    break;
                case CoffRelocAmd64Rel32:
                case CoffRelocAmd64Rel32_1:
                case CoffRelocAmd64Rel32_2:
                case CoffRelocAmd64Rel32_3:
                case CoffRelocAmd64Rel32_4:
                case CoffRelocAmd64Rel32_5:
                    Span<byte> rel32Patch = textBytes.AsSpan(checked((int)relocationOffset), 4);
                    int rel32Addend = BinaryPrimitives.ReadInt32LittleEndian(rel32Patch);
                    // REL32_N: displacement is relative to (relocation offset + 4 + N) where N = type - REL32.
                    int extraDisplacement = relocationType - CoffRelocAmd64Rel32;
                    long nextInstructionVa = checked((long)(PeImageBase + PeTextRva + (uint)WindowsTextPrefixLength + relocationOffset + 4 + (uint)extraDisplacement));
                    long relativeTarget = checked((long)targetVa + rel32Addend - nextInstructionVa);
                    BinaryPrimitives.WriteInt32LittleEndian(rel32Patch, checked((int)relativeTarget));
                    break;
                default:
                    throw new InvalidOperationException($"LLVM COFF emitted unsupported .text relocation type 0x{relocationType:X4}.");
            }
        }
    }

    /// <summary>
    /// Applies relocations that live inside data sections (e.g. a <c>switch</c> jump table in
    /// <c>.rdata</c>). Each entry is an absolute <c>.text</c> block address
    /// (<see cref="CoffRelocAmd64Addr64"/>); COFF stores the addend in place, so the final value is
    /// <c>S + A</c>. The patched bytes live in the concatenated data blob at the section's offset.
    /// </summary>
    private static void ApplyCoffDataRelocations(
        ReadOnlySpan<byte> bytes,
        byte[] rdataBytes,
        CoffSectionHeader[] sections,
        int textSectionNumber,
        IReadOnlyDictionary<int, uint> dataSectionOffsets,
        uint symbolTableOffset,
        uint symbolCount,
        IReadOnlyDictionary<int, ulong> sectionBaseVas,
        IReadOnlyDictionary<string, ulong> importSymbolVas)
    {
        for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            CoffSectionHeader section = sections[sectionIndex];
            int sectionNumber = sectionIndex + 1;
            if (section.NumberOfRelocations == 0
                || !dataSectionOffsets.TryGetValue(sectionNumber, out uint sectionOffsetInRdata))
            {
                continue;
            }

            for (int i = 0; i < section.NumberOfRelocations; i++)
            {
                int offset = checked((int)section.PointerToRelocations + i * 10);
                uint relocationOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
                int symbolIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4)));
                ushort relocationType = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 8, 2));
                CoffSymbol symbol = ReadCoffSymbol(bytes, symbolTableOffset, symbolCount, symbolIndex);
                ulong targetVa = ResolveCoffTargetVa(symbol, textSectionNumber, sectionBaseVas, importSymbolVas);
                int patchOffset = checked((int)(sectionOffsetInRdata + relocationOffset));

                switch (relocationType)
                {
                    case CoffRelocAmd64Addr64:
                        Span<byte> patch64 = rdataBytes.AsSpan(patchOffset, 8);
                        ulong addend64 = BinaryPrimitives.ReadUInt64LittleEndian(patch64);
                        BinaryPrimitives.WriteUInt64LittleEndian(patch64, checked(targetVa + addend64));
                        break;
                    case CoffRelocAmd64Addr32:
                        Span<byte> patch32 = rdataBytes.AsSpan(patchOffset, 4);
                        int addend32 = BinaryPrimitives.ReadInt32LittleEndian(patch32);
                        BinaryPrimitives.WriteUInt32LittleEndian(patch32, checked((uint)(checked((long)targetVa) + addend32)));
                        break;
                    default:
                        throw new InvalidOperationException($"LLVM COFF emitted unsupported data-section relocation type 0x{relocationType:X4}.");
                }
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

            if (string.Equals(name, entrySymbolName, StringComparison.Ordinal))
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
            return ReadStringTableName(fileBytes, stringTableOffset + nameOffset);
        }

        int length = 0;
        while (length < 8 && nameBytes[length] != 0)
        {
            length++;
        }

        // Section headers spell long names as "/<decimal offset into the string table>" —
        // a different convention from the zero-prefixed symbol form above. DWARF section
        // names (.debug_info, ...) all exceed 8 characters and use it.
        if (length > 1 && nameBytes[0] == (byte)'/')
        {
            var digits = Encoding.ASCII.GetString(nameBytes[1..length]);
            if (int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int tableOffset))
            {
                int stringTableOffset = checked((int)(symbolTableOffset + symbolCount * 18));
                return ReadStringTableName(fileBytes, stringTableOffset + tableOffset);
            }
        }

        return Encoding.ASCII.GetString(nameBytes[..length]);
    }

    private static string ReadStringTableName(ReadOnlySpan<byte> fileBytes, int start)
    {
        int end = start;
        while (end < fileBytes.Length && fileBytes[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(fileBytes[start..end]);
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
        int TextSectionNumber,
        IReadOnlyList<CoffDebugSection> DebugSections);

    private readonly record struct CoffDebugSection(
        string Name,
        int SectionNumber,
        byte[] Bytes,
        CoffSectionHeader Header);

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
