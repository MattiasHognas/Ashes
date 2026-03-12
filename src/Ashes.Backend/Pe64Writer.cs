using Ashes.Semantics;

using System.Buffers.Binary;
using System.Text;
using LibObjectFile.PE;

namespace Ashes.Backend;

/// <summary>
/// PE backend using Iced.Intel to encode x86-64 instructions and LibObjectFile to write a PE32+ image.
/// Produces a Windows x86-64 console executable directly (no external toolchain).
///
/// Notes:
/// - We keep Ashes's INTERNAL calling convention (envPtr in RDI, arg in RSI) for lambdas/closures.
/// - For calls into Win32 APIs we follow Windows x64 ABI (RCX,RDX,R8,R9 + 32-byte shadow space).
/// - We import KERNEL32.DLL: ExitProcess, GetStdHandle, WriteFile, ReadFile, GetCommandLineW, WideCharToMultiByte, LocalFree,
///   CreateFileA, CloseHandle, GetFileSizeEx, GetFileAttributesA.
/// - We import SHELL32.DLL: CommandLineToArgvW.
/// - We import WS2_32.DLL: WSAStartup, socket, connect, send, recv, closesocket, inet_addr, gethostbyname.
/// </summary>
public sealed class Pe64Writer
{
    private readonly WindowsX64CodegenIced _codegen;

    public Pe64Writer(WindowsX64CodegenIced? codegen = null)
    {
        _codegen = codegen ?? new WindowsX64CodegenIced();
    }

    // PE conventions
    private const ulong ImageBase = 0x0000000140000000UL;
    private const uint TextRva = 0x00001000;
    private const uint SectionAlignment = 0x00001000;
    private const int ImportDllCount = 3;
    private const int ShellImportCount = 1;
    private const int Ws2ImportCount = 8;

    // Import Address Table layout (we put IAT directory first in .rdata)
    private const int IatIndex_ExitProcess = 0;
    private const int IatIndex_GetStdHandle = 1;
    private const int IatIndex_WriteFile = 2;
    private const int IatIndex_ReadFile = 3;
    private const int IatIndex_GetCommandLineW = 4;
    private const int IatIndex_WideCharToMultiByte = 5;
    private const int IatIndex_LocalFree = 6;
    private const int IatIndex_CreateFileA = 7;
    private const int IatIndex_CloseHandle = 8;
    private const int IatIndex_GetFileSizeEx = 9;
    private const int IatIndex_GetFileAttributesA = 10;
    private const int IatIndex_CommandLineToArgvW = 11;
    private const int IatIndex_WSAStartup = 12;
    private const int IatIndex_socket = 13;
    private const int IatIndex_connect = 14;
    private const int IatIndex_send = 15;
    private const int IatIndex_recv = 16;
    private const int IatIndex_closesocket = 17;
    private const int IatIndex_inet_addr = 18;
    private const int IatIndex_gethostbyname = 19;
    private const int KernelImportCount = IatIndex_GetFileAttributesA + 1;
    private const int Ws2ImportStart = IatIndex_WSAStartup;

    // Runtime memory
    private const int HeapSize = 1024 * 1024 * 4; // 4 MiB to support large recursive list programs
    private const int IntBufSize = 64;          // for itoa
    private const int InputBufSize = 64 * 1024;
    private const int ShadowAndAlign = 0x28;    // 0x20 shadow + 8 align (typical Win64 prolog around calls)

    public byte[] CompileToPe(IrProgram p)
    {
        // -------------------------
        // .rdata: imports + literals
        // -------------------------
        var rdata = new PEStreamSectionData();
        var rdataOffsets = new Dictionary<string, int>(StringComparer.Ordinal);

        // Import metadata written into rdata stream
        Align(rdata, 2);
        var kernelNameOff = (int)rdata.Stream.Position;
        rdata.Stream.Write(Encoding.ASCII.GetBytes("KERNEL32.DLL\0"));
        var kernelName = new PEAsciiStringLink(rdata, new RVO((uint)kernelNameOff));
        var shellNameOff = (int)rdata.Stream.Position;
        rdata.Stream.Write(Encoding.ASCII.GetBytes("SHELL32.DLL\0"));
        var shellName = new PEAsciiStringLink(rdata, new RVO((uint)shellNameOff));
        var ws2NameOff = (int)rdata.Stream.Position;
        rdata.Stream.Write(Encoding.ASCII.GetBytes("WS2_32.DLL\0"));
        var ws2Name = new PEAsciiStringLink(rdata, new RVO((uint)ws2NameOff));

        // Hint is optional; 0 is fine.
        var hnExitProcess = WriteImportHintName(rdata, 0, "ExitProcess");
        var hnGetStdHandle = WriteImportHintName(rdata, 0, "GetStdHandle");
        var hnWriteFile = WriteImportHintName(rdata, 0, "WriteFile");
        var hnReadFile = WriteImportHintName(rdata, 0, "ReadFile");
        var hnGetCommandLineW = WriteImportHintName(rdata, 0, "GetCommandLineW");
        var hnWideCharToMultiByte = WriteImportHintName(rdata, 0, "WideCharToMultiByte");
        var hnLocalFree = WriteImportHintName(rdata, 0, "LocalFree");
        var hnCreateFileA = WriteImportHintName(rdata, 0, "CreateFileA");
        var hnCloseHandle = WriteImportHintName(rdata, 0, "CloseHandle");
        var hnGetFileSizeEx = WriteImportHintName(rdata, 0, "GetFileSizeEx");
        var hnGetFileAttributesA = WriteImportHintName(rdata, 0, "GetFileAttributesA");
        var hnCommandLineToArgvW = WriteImportHintName(rdata, 0, "CommandLineToArgvW");
        var hnWSAStartup = WriteImportHintName(rdata, 0, "WSAStartup");
        var hnSocket = WriteImportHintName(rdata, 0, "socket");
        var hnConnect = WriteImportHintName(rdata, 0, "connect");
        var hnSend = WriteImportHintName(rdata, 0, "send");
        var hnRecv = WriteImportHintName(rdata, 0, "recv");
        var hnCloseSocket = WriteImportHintName(rdata, 0, "closesocket");
        var hnInetAddr = WriteImportHintName(rdata, 0, "inet_addr");
        var hnGetHostByName = WriteImportHintName(rdata, 0, "gethostbyname");

        // Build IAT/ILT/Import directory objects (these become section data too)
        var kernelIat = new PEImportAddressTable64() { hnExitProcess, hnGetStdHandle, hnWriteFile, hnReadFile, hnGetCommandLineW, hnWideCharToMultiByte, hnLocalFree, hnCreateFileA, hnCloseHandle, hnGetFileSizeEx, hnGetFileAttributesA };
        var shellIat = new PEImportAddressTable64() { hnCommandLineToArgvW };
        var ws2Iat = new PEImportAddressTable64() { hnWSAStartup, hnSocket, hnConnect, hnSend, hnRecv, hnCloseSocket, hnInetAddr, hnGetHostByName };
        var iatDirectory = new PEImportAddressTableDirectory() { kernelIat, shellIat, ws2Iat };

        var kernelIlt = new PEImportLookupTable64() { hnExitProcess, hnGetStdHandle, hnWriteFile, hnReadFile, hnGetCommandLineW, hnWideCharToMultiByte, hnLocalFree, hnCreateFileA, hnCloseHandle, hnGetFileSizeEx, hnGetFileAttributesA };
        var shellIlt = new PEImportLookupTable64() { hnCommandLineToArgvW };
        var ws2Ilt = new PEImportLookupTable64() { hnWSAStartup, hnSocket, hnConnect, hnSend, hnRecv, hnCloseSocket, hnInetAddr, hnGetHostByName };
        var importDirectory = new PEImportDirectory()
        {
            Entries =
            {
                new PEImportDirectoryEntry(kernelName, kernelIat, kernelIlt),
                new PEImportDirectoryEntry(shellName, shellIat, shellIlt),
                new PEImportDirectoryEntry(ws2Name, ws2Iat, ws2Ilt)
            }
        };

        // Ashes literals (string objects) into rdata stream
        string trueLabel = InternStringLiteral(rdata, rdataOffsets, "true", label: null);
        string falseLabel = InternStringLiteral(rdata, rdataOffsets, "false", label: null);
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_readline_too_long", "readLine() exceeded max line length");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_fs_read_failed", "Ashes.File.readText() failed");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_fs_write_failed", "Ashes.File.writeText() failed");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_fs_invalid_utf8", "Ashes.File.readText() encountered invalid UTF-8");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_tcp_connect_failed", "Ashes.Net.Tcp.connect() failed");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_tcp_send_failed", "Ashes.Net.Tcp.send() failed");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_tcp_receive_failed", "Ashes.Net.Tcp.receive() failed");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_tcp_close_failed", "Ashes.Net.Tcp.close() failed");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_tcp_invalid_utf8", "Ashes.Net.Tcp.receive() encountered invalid UTF-8");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_tcp_invalid_max_bytes", "Ashes.Net.Tcp.receive() maxBytes must be positive");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_tcp_invalid_host", "Ashes.Net.Tcp.connect() requires an IPv4 address literal");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_tcp_resolve_failed", "Ashes.Net.Tcp.connect() could not resolve host");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_empty", "");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_default_path", "/");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_get_prefix", "GET ");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_post_prefix", "POST ");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_host_header", " HTTP/1.1\r\nHost: ");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_content_length_header", "\r\nContent-Length: ");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_request_suffix", "\r\nConnection: close\r\n\r\n");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_status_prefix", "HTTP ");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_chunked_header", "Transfer-Encoding: chunked");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_https_not_supported", "https not supported");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_malformed_url", "malformed URL");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_malformed_response", "malformed HTTP response");
        EnsureStringLiteral(rdata, rdataOffsets, "__rt_http_unsupported_transfer_encoding", "unsupported transfer encoding");

        foreach (var s in p.StringLiterals)
        {
            EnsureStringLiteral(rdata, rdataOffsets, s.Label, s.Value);
        }

        // newline byte
        Mark(rdata, rdataOffsets, "nl");
        rdata.Stream.WriteByte(0x0A);

        // Pad rdata stream to 8-byte alignment so the IAT that follows is properly aligned.
        // The IAT slot VA is rdataVa + (padded rdata stream size) + (index * 8).
        Align(rdata, 8);
        int iatSectionOffset = (int)rdata.Stream.Length;

        // -------------------------
        // .data: heap + mutable state
        // -------------------------
        var data = new PEStreamSectionData();
        var dataOffsets = new Dictionary<string, int>(StringComparer.Ordinal);

        Align(data, 8);
        Mark(data, dataOffsets, "heap_ptr");
        WriteU64(data, 0);

        Align(data, 8);
        Mark(data, dataOffsets, "program_args");
        WriteU64(data, 0);

        Align(data, 8);
        Mark(data, dataOffsets, "program_argc");
        WriteU64(data, 0);

        Align(data, 16);
        int heapOff = (int)data.Stream.Position;
        Mark(data, dataOffsets, "heap");
        data.Stream.Write(new byte[HeapSize]); // initialized to 0

        Align(data, 8);
        int intBufOff = (int)data.Stream.Position;
        Mark(data, dataOffsets, "buf_i64");
        data.Stream.Write(new byte[IntBufSize]);

        Align(data, 8);
        Mark(data, dataOffsets, "stdout_handle");
        WriteU64(data, 0);

        Align(data, 8);
        Mark(data, dataOffsets, "stdin_handle");
        WriteU64(data, 0);

        Align(data, 8);
        Mark(data, dataOffsets, "bytes_written");
        WriteU64(data, 0); // WriteFile expects LPDWORD; we give it &bytes_written and let it write lower 4 bytes

        Align(data, 8);
        Mark(data, dataOffsets, "bytes_read");
        WriteU64(data, 0);

        Align(data, 8);
        Mark(data, dataOffsets, "file_size");
        WriteU64(data, 0);

        Align(data, 8);
        Mark(data, dataOffsets, "winsock_started");
        WriteU64(data, 0);

        Align(data, 8);
        Mark(data, dataOffsets, "wsadata");
        data.Stream.Write(new byte[512]);

        Mark(data, dataOffsets, "stdin_byte");
        data.Stream.WriteByte(0);

        Align(data, 8);
        Mark(data, dataOffsets, "stdin_buf");
        data.Stream.Write(new byte[InputBufSize]);

        // -------------------------
        // .text: machine code
        // -------------------------
        byte[] GenerateTextBytes(uint rdataRva, uint dataRva)
        {
            ulong textVa = ImageBase + TextRva;
            ulong rdataVa = ImageBase + rdataRva;
            ulong dataVa = ImageBase + dataRva;

            ulong AddrOf(string name)
            {
                if (rdataOffsets.TryGetValue(name, out var roff))
                {
                    return rdataVa + (ulong)roff;
                }

                if (dataOffsets.TryGetValue(name, out var doff))
                {
                    return dataVa + (ulong)doff;
                }

                throw new InvalidOperationException($"Unknown symbol: {name}");
            }

            ulong IatSlotVa(int index)
            {
                // IAT starts at rdataVa + iatSectionOffset (after the rdata stream data).
                ulong iatBase = rdataVa + (ulong)iatSectionOffset;
                return index switch
                {
                    < KernelImportCount => iatBase + (ulong)(index * 8),
                    IatIndex_CommandLineToArgvW => iatBase + (ulong)((KernelImportCount + 1) * 8),
                    >= Ws2ImportStart and < Ws2ImportStart + Ws2ImportCount => iatBase + (ulong)((KernelImportCount + 1 + ShellImportCount + 1 + (index - Ws2ImportStart)) * 8),
                    _ => throw new InvalidOperationException($"Unknown IAT index: {index}")
                };
            }

            return _codegen.GenerateText(p, textVa, AddrOf, IatSlotVa, trueLabel, falseLabel);
        }

        // Two-pass generation:
        // 1) Emit text with probe RVAs to measure the actual .text size.
        // 2) Compute final section RVAs from measured sizes and re-emit with final addresses.
        uint probeRDataRva = checked(TextRva + SectionAlignment);
        uint probeDataRva = checked(probeRDataRva + SectionAlignment);
        var textBytes = GenerateTextBytes(probeRDataRva, probeDataRva);
        uint rdataRva = AlignUp(checked(TextRva + (uint)textBytes.Length), SectionAlignment);
        uint rdataVirtualSize = checked((uint)rdata.Stream.Length + ComputeRDataMetadataSize());
        uint dataRva = AlignUp(checked(rdataRva + rdataVirtualSize), SectionAlignment);
        textBytes = GenerateTextBytes(rdataRva, dataRva);

        var pe = new PEFile();

        // Sections
        var textSection = pe.AddSection(PESectionName.Text, TextRva);
        var rdataSection = pe.AddSection(PESectionName.RData, rdataRva);
        var dataSection = pe.AddSection(PESectionName.Data, dataRva);

        // Layout: rdata stream first so AddrOf(name) = rdataVa + rdataOffsets[name] is correct,
        // then the PE import structures (IAT, ILT, import directory).
        rdataSection.Content.Add(rdata);
        rdataSection.Content.Add(iatDirectory);
        rdataSection.Content.Add(kernelIlt);
        rdataSection.Content.Add(shellIlt);
        rdataSection.Content.Add(ws2Ilt);
        rdataSection.Content.Add(importDirectory);
        dataSection.Content.Add(data);

        var code = new PEStreamSectionData();
        code.Stream.Write(textBytes);
        textSection.Content.Add(code);

        // Optional header
        pe.OptionalHeader.AddressOfEntryPoint = new(code, 0);
        pe.OptionalHeader.BaseOfCode = textSection;

        // The generated code uses hardcoded absolute addresses (no relocations), so the image
        // must always be loaded at ImageBase. Remove DYNAMIC_BASE and HIGH_ENTROPY_VA to
        // disable ASLR; keep NX_COMPAT and TS_AWARE for modern Windows compatibility.
        pe.OptionalHeader.DllCharacteristics =
            System.Reflection.PortableExecutable.DllCharacteristics.NxCompatible |
            System.Reflection.PortableExecutable.DllCharacteristics.TerminalServerAware;

        // Write PE
        using var output = new MemoryStream();
        pe.Write(output, new() { EnableStackTrace = true });
        return output.ToArray();
    }




    // ----------------------------
    // rdata/data helpers
    // ----------------------------
    private static void Align(PEStreamSectionData s, int align)
    {
        long pos = s.Stream.Position;
        long pad = (align - (pos % align)) % align;
        for (int i = 0; i < pad; i++)
        {
            s.Stream.WriteByte(0);
        }
    }

    private static void Mark(PEStreamSectionData s, Dictionary<string, int> offsets, string name)
    {
        offsets[name] = (int)s.Stream.Position;
    }

    private static void WriteU64(PEStreamSectionData s, ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        s.Stream.Write(b);
    }

    private static string InternStringLiteral(PEStreamSectionData rdata, Dictionary<string, int> offsets, string value, string? label)
    {
        label ??= "str_" + StableId(value);
        EnsureStringLiteral(rdata, offsets, label, value);
        return label;
    }

    private static void EnsureStringLiteral(PEStreamSectionData rdata, Dictionary<string, int> offsets, string label, string value)
    {
        if (offsets.ContainsKey(label))
        {
            return;
        }

        Align(rdata, 8);
        Mark(rdata, offsets, label);
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteU64(rdata, (ulong)bytes.Length);
        rdata.Stream.Write(bytes);
    }

    private static string StableId(string s)
    {
        // Small stable id (not crypto): FNV-1a 32-bit
        unchecked
        {
            uint h = 2166136261;
            foreach (var ch in s)
            {
                h ^= ch;
                h *= 16777619;
            }
            return h.ToString("x8");
        }
    }

    private static PEImportHintNameLink WriteImportHintName(PEStreamSectionData stream, ushort hint, string name)
    {
        Align(stream, 2);
        var offset = (int)stream.Stream.Position;
        stream.Stream.WriteByte((byte)(hint & 0xFF));
        stream.Stream.WriteByte((byte)((hint >> 8) & 0xFF));
        stream.Stream.Write(Encoding.ASCII.GetBytes(name));
        stream.Stream.WriteByte(0); // null terminator
        if (stream.Stream.Position % 2 != 0)
        {
            stream.Stream.WriteByte(0); // word-align
        }

        return new PEImportHintNameLink(stream, new RVO((uint)offset));
    }

    private static int Align16(int n)
    {
        return (n + 15) & ~15;
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static uint ComputeRDataMetadataSize()
    {
        // .rdata appends:
        // - IAT directory (3 tables, null-terminated): (kernel+1 + shell+1 + ws2+1) * 8
        // - ILT tables (same layout):                   (kernel+1 + shell+1 + ws2+1) * 8
        // - import directory entries + null terminator: (3+1) * 20
        const uint tableBytes = (uint)((KernelImportCount + 1 + ShellImportCount + 1 + Ws2ImportCount + 1) * sizeof(ulong));
        const uint directoryBytes = (uint)((ImportDllCount + 1) * 20);
        return checked(tableBytes + tableBytes + directoryBytes);
    }
}
