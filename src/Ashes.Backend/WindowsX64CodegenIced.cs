using Ashes.Semantics;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Ashes.Backend;

/// <summary>
/// Windows x64 code generator using Iced.Intel. Produces .text bytes for the PE backend.
/// </summary>
public sealed class WindowsX64CodegenIced
{
    // Import Address Table layout indices (must match PE writer's IAT)
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

    // Windows x64 ABI: 32-byte shadow space + alignment padding
    private const int ShadowAndAlign = 0x28;

    public byte[] GenerateText(
        IrProgram p,
        ulong textVA,
        Func<string, ulong> addrOf,
        Func<int, ulong> iatSlotVa,
        string trueLabel,
        string falseLabel)
    {
        var asm = new Assembler(64);

        // Labels
        var L_start = asm.CreateLabel();
        var L_start_main = asm.CreateLabel();

        var L_init_heap = asm.CreateLabel();
        var L_init_stdout = asm.CreateLabel();
        var L_init_stdin = asm.CreateLabel();
        var L_init_winsock = asm.CreateLabel();
        var L_alloc = asm.CreateLabel();
        var L_make_unit = asm.CreateLabel();
        var L_make_result_ok = asm.CreateLabel();
        var L_make_result_error = asm.CreateLabel();
        var L_make_string_slice = asm.CreateLabel();
        var L_make_http_status_error = asm.CreateLabel();
        var L_write_str = asm.CreateLabel();
        var L_print_str = asm.CreateLabel();
        var L_read_line = asm.CreateLabel();
        var L_string_to_cstr = asm.CreateLabel();
        var L_validate_utf8 = asm.CreateLabel();
        var L_fs_read_text = asm.CreateLabel();
        var L_fs_write_text = asm.CreateLabel();
        var L_fs_exists = asm.CreateLabel();
        var L_tcp_connect = asm.CreateLabel();
        var L_tcp_send = asm.CreateLabel();
        var L_tcp_receive = asm.CreateLabel();
        var L_tcp_close = asm.CreateLabel();
        var L_http_request = asm.CreateLabel();
        var L_panic_str = asm.CreateLabel();
        var L_print_bool = asm.CreateLabel();
        var L_concat = asm.CreateLabel();
        var L_print_int = asm.CreateLabel();
        var L_itoa = asm.CreateLabel();
        var L_str_eq = asm.CreateLabel();

        // itoa internal labels
        var L_abs_ok = asm.CreateLabel();
        var L_zero = asm.CreateLabel();
        var L_loop = asm.CreateLabel();
        var L_maybe_sign = asm.CreateLabel();
        var L_done = asm.CreateLabel();

        // Function label map for user lambdas
        var funcLabels = new Dictionary<string, Label>(StringComparer.Ordinal);
        foreach (var f in p.Functions)
        {
            funcLabels[f.Label] = asm.CreateLabel();
        }

        // Helpers for Win64 ABI calls (shadow + alignment)
        void CallLabel(Label target)
        {
            asm.sub(rsp, ShadowAndAlign);
            asm.call(target);
            asm.add(rsp, ShadowAndAlign);
        }

        void CallIat(int iatIndex)
        {
            // rax = &IAT[index]
            asm.mov(rax, (long)iatSlotVa(iatIndex));
            // rax = [rax]
            asm.mov(rax, __[rax]);
            asm.sub(rsp, ShadowAndAlign);
            asm.call(rax);
            asm.add(rsp, ShadowAndAlign);
        }

        // _start: init_stdout, init_heap, call main, ExitProcess(0)
        asm.Label(ref L_start);

        CallLabel(L_init_stdout);
        CallLabel(L_init_stdin);
        // heap init is lazy in function bodies too, but do it here for deterministic tests
        CallLabel(L_init_heap);
        asm.mov(rbx, (long)addrOf("program_args"));
        asm.xor(rax, rax);
        asm.mov(__[rbx], rax);
        // argvW = CommandLineToArgvW(GetCommandLineW(), &argc)
        asm.mov(rax, (long)iatSlotVa(IatIndex_GetCommandLineW));
        asm.mov(rax, __[rax]);
        asm.sub(rsp, ShadowAndAlign);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);
        asm.mov(rcx, rax);
        asm.mov(rdx, (long)addrOf("program_argc"));
        asm.mov(rax, (long)iatSlotVa(IatIndex_CommandLineToArgvW));
        asm.mov(rax, __[rax]);
        asm.sub(rsp, ShadowAndAlign);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);
        asm.mov(r12, rax); // argvW**
        var L_args_done = asm.CreateLabel();
        var L_args_loop = asm.CreateLabel();
        var L_arg_wide_len = asm.CreateLabel();
        var L_arg_len_ready = asm.CreateLabel();
        var L_arg_skip_convert = asm.CreateLabel();
        var L_arg_skip = asm.CreateLabel();
        var L_args_finish = asm.CreateLabel();
        asm.test(r12, r12);
        asm.jz(L_args_done);
        asm.mov(rbx, (long)addrOf("program_argc"));
        asm.mov(r13d, __dword_ptr[rbx]);
        asm.xor(r14, r14); // list = []
        asm.cmp(r13d, 1);
        asm.jle(L_args_finish);
        asm.dec(r13d); // i = argc - 1
        asm.Label(ref L_args_loop);
        asm.cmp(r13d, 0); // stop once only argv[0] remains
        asm.jle(L_args_finish);
        asm.movsxd(r10, r13d);
        asm.mov(r15, __[r12 + r10 * 8]); // wide arg pointer
        asm.xor(r11d, r11d); // wchar count
        asm.Label(ref L_arg_wide_len);
        asm.movzx(eax, __word_ptr[r15 + r11 * 2]);
        asm.test(eax, eax);
        asm.jz(L_arg_len_ready);
        asm.inc(r11d);
        asm.jmp(L_arg_wide_len);
        asm.Label(ref L_arg_len_ready);
        // bytes = WideCharToMultiByte(CP_UTF8,0,wide,wchars,NULL,0,NULL,NULL)
        asm.mov(rcx, 65001);
        asm.xor(rdx, rdx);
        asm.mov(r8, r15);
        asm.mov(r9d, r11d);
        asm.sub(rsp, 0x48);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x20], rax); // lpMultiByteStr
        asm.mov(__[rsp + 0x28], rax); // cbMultiByte
        asm.mov(__[rsp + 0x30], rax); // lpDefaultChar
        asm.mov(__[rsp + 0x38], rax); // lpUsedDefaultChar
        asm.mov(rax, (long)iatSlotVa(IatIndex_WideCharToMultiByte));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, 0x48);
        asm.mov(r10d, eax); // byte count
        asm.test(r10d, r10d);
        asm.jle(L_arg_skip);
        asm.mov(rdi, r10);
        asm.add(rdi, 8);
        CallLabel(L_alloc);
        asm.mov(__[rax], r10);
        asm.mov(rbx, rax); // string object
        // WideCharToMultiByte(CP_UTF8,0,wide,wchars,str+8,bytes,NULL,NULL)
        asm.mov(rcx, 65001);
        asm.xor(rdx, rdx);
        asm.mov(r8, r15);
        asm.mov(r9d, r11d);
        asm.sub(rsp, 0x48);
        asm.lea(r11, __[rbx + 8]);
        asm.mov(__[rsp + 0x20], r11); // lpMultiByteStr
        asm.mov(__[rsp + 0x28], r10); // cbMultiByte
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x30], rax); // lpDefaultChar
        asm.mov(__[rsp + 0x38], rax); // lpUsedDefaultChar
        asm.mov(rax, (long)iatSlotVa(IatIndex_WideCharToMultiByte));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, 0x48);
        // r10 (byte count) is no longer needed; save the string ptr before alloc clobbers rbx.
        asm.mov(r10, rbx);
        asm.mov(rdi, 16);
        CallLabel(L_alloc);
        asm.mov(__[rax + 0], r10);
        asm.mov(__[rax + 8], r14);
        asm.mov(r14, rax);
        asm.jmp(L_arg_skip_convert);
        asm.Label(ref L_arg_skip);
        // empty string
        asm.mov(rdi, 8);
        CallLabel(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        // Save string ptr in r10 before the list-node alloc clobbers rbx.
        asm.mov(r10, rax);
        asm.mov(rdi, 16);
        CallLabel(L_alloc);
        asm.mov(__[rax + 0], r10);
        asm.mov(__[rax + 8], r14);
        asm.mov(r14, rax);
        asm.Label(ref L_arg_skip_convert);
        asm.dec(r13d);
        asm.jmp(L_args_loop);
        asm.Label(ref L_args_finish);
        asm.mov(rbx, (long)addrOf("program_args"));
        asm.mov(__[rbx], r14);
        // LocalFree(argvW)
        asm.mov(rcx, r12);
        asm.mov(rax, (long)iatSlotVa(IatIndex_LocalFree));
        asm.mov(rax, __[rax]);
        asm.sub(rsp, ShadowAndAlign);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);
        asm.Label(ref L_args_done);

        CallLabel(L_start_main);

        // ExitProcess(0)
        asm.mov(rcx, 0);
        CallIat(IatIndex_ExitProcess);

        // --------------------------
        // Entry function (_start_main)
        // --------------------------
        asm.Label(ref L_start_main);
        EmitFunctionBody(asm, p.EntryFunction, addrOf, funcLabels, trueLabel, falseLabel, hasParams: false);

        // Emit lambda functions
        foreach (var f in p.Functions)
        {
            var lbl = funcLabels[f.Label];
            asm.Label(ref lbl);
            EmitFunctionBody(asm, f, addrOf, funcLabels, trueLabel, falseLabel, hasParams: true);
        }

        // --------------------------
        // Runtime helpers
        // --------------------------

        // init_stdout: stdout_handle = GetStdHandle(-11)
        asm.Label(ref L_init_stdout);
        asm.mov(rcx, unchecked((int)0xFFFFFFF5)); // STD_OUTPUT_HANDLE = -11
        CallIat(IatIndex_GetStdHandle);
        asm.mov(rbx, (long)addrOf("stdout_handle"));
        asm.mov(__[rbx], rax);
        asm.ret();

        // init_stdin: stdin_handle = GetStdHandle(-10)
        asm.Label(ref L_init_stdin);
        asm.mov(rcx, unchecked((int)0xFFFFFFF6)); // STD_INPUT_HANDLE = -10
        CallIat(IatIndex_GetStdHandle);
        asm.mov(rbx, (long)addrOf("stdin_handle"));
        asm.mov(__[rbx], rax);
        asm.ret();

        // init_winsock() -> EAX = 1 on success, 0 on failure
        asm.Label(ref L_init_winsock);
        var L_init_winsock_done = asm.CreateLabel();
        var L_init_winsock_fail = asm.CreateLabel();
        asm.mov(rbx, (long)addrOf("winsock_started"));
        asm.mov(rax, __[rbx]);
        asm.cmp(rax, 0);
        asm.jne(L_init_winsock_done);
        asm.mov(rcx, 0x0202);
        asm.mov(rdx, (long)addrOf("wsadata"));
        CallIat(IatIndex_WSAStartup);
        asm.test(eax, eax);
        asm.jne(L_init_winsock_fail);
        asm.mov(rax, 1);
        asm.mov(__[rbx], rax);
        asm.Label(ref L_init_winsock_done);
        asm.mov(eax, 1);
        asm.ret();
        asm.Label(ref L_init_winsock_fail);
        asm.xor(eax, eax);
        asm.ret();

        // init_heap: [heap_ptr] = &heap
        asm.Label(ref L_init_heap);
        asm.mov(rax, (long)addrOf("heap"));
        asm.mov(rbx, (long)addrOf("heap_ptr"));
        asm.mov(__[rbx], rax);
        asm.ret();

        // alloc(size in RDI) -> RAX
        asm.Label(ref L_alloc);
        asm.mov(rbx, (long)addrOf("heap_ptr"));
        asm.mov(rax, __[rbx]);
        asm.mov(r11, rax);
        asm.add(r11, rdi);
        asm.mov(__[rbx], r11);
        asm.ret();

        // make_unit() -> RAX = Unit value
        asm.Label(ref L_make_unit);
        asm.mov(rdi, 8);
        CallLabel(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        asm.ret();

        // make_result_ok(RDI=payload) -> RAX = Result(Str, T)
        asm.Label(ref L_make_result_ok);
        asm.mov(r8, rdi);
        asm.mov(rdi, 16);
        CallLabel(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        asm.mov(__[rax + 8], r8);
        asm.ret();

        // make_result_error(RDI=error string ptr) -> RAX = Result(Str, T)
        asm.Label(ref L_make_result_error);
        asm.mov(r8, rdi);
        asm.mov(rdi, 16);
        CallLabel(L_alloc);
        asm.mov(rbx, 1);
        asm.mov(__[rax], rbx);
        asm.mov(__[rax + 8], r8);
        asm.ret();

        // make_string_slice(RDI=src bytes ptr, RSI=len) -> RAX=string*
        asm.Label(ref L_make_string_slice);
        var L_make_string_slice_done = asm.CreateLabel();
        asm.mov(r12, rdi);
        asm.mov(r13, rsi);
        asm.mov(rdi, r13);
        asm.add(rdi, 8);
        CallLabel(L_alloc);
        asm.mov(r14, rax);
        asm.mov(__[r14], r13);
        asm.test(r13, r13);
        asm.je(L_make_string_slice_done);
        asm.lea(rdi, __[r14 + 8]);
        asm.mov(rsi, r12);
        asm.mov(rcx, r13);
        asm.rep.movsb();
        asm.Label(ref L_make_string_slice_done);
        asm.mov(rax, r14);
        asm.ret();

        // make_http_status_error(RDI=statusCode) -> RAX=string* ("HTTP <code>")
        asm.Label(ref L_make_http_status_error);
        asm.mov(rsi, (long)addrOf("buf_i64"));
        CallLabel(L_itoa);
        asm.mov(r12, rsi);
        asm.mov(r13, rdx);
        asm.mov(rdi, r13);
        asm.add(rdi, 13);
        CallLabel(L_alloc);
        asm.mov(r14, rax);
        asm.mov(rcx, r13);
        asm.add(rcx, 5);
        asm.mov(__[r14], rcx);
        asm.lea(rdi, __[r14 + 8]);
        asm.mov(rsi, (long)addrOf("__rt_http_status_prefix"));
        asm.add(rsi, 8);
        asm.mov(rcx, 5);
        asm.rep.movsb();
        asm.mov(rsi, r12);
        asm.mov(rcx, r13);
        asm.rep.movsb();
        asm.mov(rax, r14);
        asm.ret();

        // write_str(RDI=string*)
        asm.Label(ref L_write_str);
        // rcx = stdout handle
        asm.mov(rbx, (long)addrOf("stdout_handle"));
        asm.mov(rcx, __[rbx]);
        // rdx = &bytes
        asm.lea(rdx, __[rdi + 8]);
        // r8 = len
        asm.mov(r8, __[rdi]);
        // r9 = &bytes_written
        asm.mov(r9, (long)addrOf("bytes_written"));
        // [rsp+0x20] = NULL (lpOverlapped) - within shadow space
        asm.sub(rsp, ShadowAndAlign);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x20], rax);
        // call WriteFile
        asm.mov(rax, (long)iatSlotVa(IatIndex_WriteFile));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);
        asm.ret();

        // print_str(RDI=string*)
        asm.Label(ref L_print_str);
        CallLabel(L_write_str);

        // newline
        asm.mov(rbx, (long)addrOf("stdout_handle"));
        asm.mov(rcx, __[rbx]);
        asm.mov(rdx, (long)addrOf("nl"));
        asm.mov(r8, 1);
        asm.mov(r9, (long)addrOf("bytes_written"));
        asm.sub(rsp, ShadowAndAlign);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x20], rax);
        asm.mov(rax, (long)iatSlotVa(IatIndex_WriteFile));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);

        asm.ret();

        // read_line() -> RAX = Maybe<Str>
        asm.Label(ref L_read_line);
        var L_read_line_loop = asm.CreateLabel();
        var L_read_line_eof = asm.CreateLabel();
        var L_read_line_finish_some = asm.CreateLabel();
        var L_read_line_return_none = asm.CreateLabel();
        var L_read_line_overflow = asm.CreateLabel();

        asm.mov(r12, (long)addrOf("stdin_buf"));
        asm.xor(r13, r13);

        asm.Label(ref L_read_line_loop);
        asm.mov(rbx, (long)addrOf("stdin_handle"));
        asm.mov(rcx, __[rbx]);
        asm.mov(rdx, (long)addrOf("stdin_byte"));
        asm.mov(r8, 1);
        asm.mov(r9, (long)addrOf("bytes_read"));
        asm.sub(rsp, ShadowAndAlign);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x20], rax);
        asm.mov(rax, (long)iatSlotVa(IatIndex_ReadFile));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);
        asm.mov(rbx, (long)addrOf("bytes_read"));
        asm.mov(eax, __dword_ptr[rbx]);
        asm.test(eax, eax);
        asm.jz(L_read_line_eof);
        asm.mov(r10, (long)addrOf("stdin_byte"));
        asm.movzx(eax, __byte_ptr[r10]);
        asm.cmp(al, 10);
        asm.je(L_read_line_finish_some);
        asm.cmp(al, 13);
        asm.je(L_read_line_loop);
        asm.cmp(r13, 65536);
        asm.jae(L_read_line_overflow);
        asm.mov(__byte_ptr[r12 + r13], al);
        asm.inc(r13);
        asm.jmp(L_read_line_loop);

        asm.Label(ref L_read_line_eof);
        asm.cmp(r13, 0);
        asm.je(L_read_line_return_none);

        asm.Label(ref L_read_line_finish_some);
        asm.mov(rdi, r13);
        asm.add(rdi, 8);
        CallLabel(L_alloc);
        asm.mov(__[rax], r13);
        asm.mov(r14, rax);
        asm.lea(rdi, __[rax + 8]);
        asm.mov(rsi, r12);
        asm.mov(rcx, r13);
        asm.rep.movsb();
        asm.mov(rdi, 16);
        CallLabel(L_alloc);
        asm.mov(rbx, 1);
        asm.mov(__[rax], rbx);
        asm.mov(__[rax + 8], r14);
        asm.ret();

        asm.Label(ref L_read_line_return_none);
        asm.mov(rdi, 8);
        CallLabel(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        asm.ret();

        asm.Label(ref L_read_line_overflow);
        asm.mov(rdi, (long)addrOf("__rt_readline_too_long"));
        CallLabel(L_panic_str);
        asm.ret();

        // string_to_cstr(RDI=string*) -> RAX = heap-allocated null-terminated bytes
        asm.Label(ref L_string_to_cstr);
        asm.mov(r8, __[rdi]);
        asm.mov(r9, rdi);
        asm.mov(rdi, r8);
        asm.add(rdi, 1);
        CallLabel(L_alloc);
        asm.mov(r10, rax);
        asm.mov(rdi, rax);
        asm.lea(rsi, __[r9 + 8]);
        asm.mov(rcx, r8);
        asm.rep.movsb();
        asm.mov(__byte_ptr[r10 + r8], 0);
        asm.mov(rax, r10);
        asm.ret();

        // validate_utf8(RDI=bytes, RSI=len) -> RAX = 1 if valid, 0 if invalid
        asm.Label(ref L_validate_utf8);
        var L_utf8_loop = asm.CreateLabel();
        var L_utf8_ascii = asm.CreateLabel();
        var L_utf8_two = asm.CreateLabel();
        var L_utf8_three = asm.CreateLabel();
        var L_utf8_e0 = asm.CreateLabel();
        var L_utf8_ed = asm.CreateLabel();
        var L_utf8_f0 = asm.CreateLabel();
        var L_utf8_four = asm.CreateLabel();
        var L_utf8_f4 = asm.CreateLabel();
        var L_utf8_valid = asm.CreateLabel();
        var L_utf8_invalid = asm.CreateLabel();

        asm.xor(rcx, rcx);
        asm.Label(ref L_utf8_loop);
        asm.cmp(rcx, rsi);
        asm.je(L_utf8_valid);
        asm.movzx(eax, __byte_ptr[rdi + rcx]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_ascii);
        asm.cmp(al, 0xC2);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xDF);
        asm.jbe(L_utf8_two);
        asm.cmp(al, 0xE0);
        asm.je(L_utf8_e0);
        asm.cmp(al, 0xEC);
        asm.jbe(L_utf8_three);
        asm.cmp(al, 0xED);
        asm.je(L_utf8_ed);
        asm.cmp(al, 0xEF);
        asm.jbe(L_utf8_three);
        asm.cmp(al, 0xF0);
        asm.je(L_utf8_f0);
        asm.cmp(al, 0xF3);
        asm.jbe(L_utf8_four);
        asm.cmp(al, 0xF4);
        asm.je(L_utf8_f4);
        asm.jmp(L_utf8_invalid);

        asm.Label(ref L_utf8_ascii);
        asm.inc(rcx);
        asm.jmp(L_utf8_loop);

        asm.Label(ref L_utf8_two);
        asm.mov(rax, rsi);
        asm.sub(rax, rcx);
        asm.cmp(rax, 2);
        asm.jb(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 1]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.add(rcx, 2);
        asm.jmp(L_utf8_loop);

        asm.Label(ref L_utf8_three);
        asm.mov(rax, rsi);
        asm.sub(rax, rcx);
        asm.cmp(rax, 3);
        asm.jb(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 1]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 2]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.add(rcx, 3);
        asm.jmp(L_utf8_loop);

        asm.Label(ref L_utf8_e0);
        asm.mov(rax, rsi);
        asm.sub(rax, rcx);
        asm.cmp(rax, 3);
        asm.jb(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 1]);
        asm.cmp(al, 0xA0);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 2]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.add(rcx, 3);
        asm.jmp(L_utf8_loop);

        asm.Label(ref L_utf8_ed);
        asm.mov(rax, rsi);
        asm.sub(rax, rcx);
        asm.cmp(rax, 3);
        asm.jb(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 1]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0x9F);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 2]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.add(rcx, 3);
        asm.jmp(L_utf8_loop);

        asm.Label(ref L_utf8_f0);
        asm.mov(rax, rsi);
        asm.sub(rax, rcx);
        asm.cmp(rax, 4);
        asm.jb(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 1]);
        asm.cmp(al, 0x90);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 2]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 3]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.add(rcx, 4);
        asm.jmp(L_utf8_loop);

        asm.Label(ref L_utf8_four);
        asm.mov(rax, rsi);
        asm.sub(rax, rcx);
        asm.cmp(rax, 4);
        asm.jb(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 1]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 2]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 3]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.add(rcx, 4);
        asm.jmp(L_utf8_loop);

        asm.Label(ref L_utf8_f4);
        asm.mov(rax, rsi);
        asm.sub(rax, rcx);
        asm.cmp(rax, 4);
        asm.jb(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 1]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0x8F);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 2]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.movzx(eax, __byte_ptr[rdi + rcx + 3]);
        asm.cmp(al, 0x80);
        asm.jb(L_utf8_invalid);
        asm.cmp(al, 0xBF);
        asm.ja(L_utf8_invalid);
        asm.add(rcx, 4);
        asm.jmp(L_utf8_loop);

        asm.Label(ref L_utf8_valid);
        asm.mov(rax, 1);
        asm.ret();

        asm.Label(ref L_utf8_invalid);
        asm.xor(rax, rax);
        asm.ret();

        // fs_read_text(RDI=path string*) -> RAX=Result(Str, Str)
        asm.Label(ref L_fs_read_text);
        var L_fs_read_loop = asm.CreateLabel();
        var L_fs_read_after = asm.CreateLabel();
        var L_fs_read_close_and_return = asm.CreateLabel();
        var L_fs_read_fail = asm.CreateLabel();
        var L_fs_read_fail_with_handle = asm.CreateLabel();
        var L_fs_read_invalid_utf8 = asm.CreateLabel();

        asm.call(L_string_to_cstr);
        asm.mov(rcx, rax);
        asm.mov(rdx, unchecked((int)0x80000000));
        asm.mov(r8, 1);
        asm.xor(r9, r9);
        asm.sub(rsp, 0x38);
        asm.xor(rax, rax);
        asm.mov(r10, 3);
        asm.mov(__[rsp + 0x20], r10);
        asm.mov(r10, 0x80);
        asm.mov(__[rsp + 0x28], r10);
        asm.mov(__[rsp + 0x30], rax);
        asm.mov(rax, (long)iatSlotVa(IatIndex_CreateFileA));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, 0x38);
        asm.cmp(rax, -1);
        asm.je(L_fs_read_fail);
        asm.mov(r12, rax);

        asm.mov(rdi, 0x100000 + 8);
        CallLabel(L_alloc);
        asm.mov(r14, rax);
        asm.lea(r15, __[r14 + 8]);
        asm.mov(rcx, r12);
        asm.mov(rdx, r15);
        asm.mov(r8, 0x100000);
        asm.mov(r9, (long)addrOf("bytes_read"));
        asm.sub(rsp, ShadowAndAlign);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x20], rax);
        asm.mov(rax, (long)iatSlotVa(IatIndex_ReadFile));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);
        asm.test(eax, eax);
        asm.jz(L_fs_read_fail_with_handle);
        asm.mov(rbx, (long)addrOf("bytes_read"));
        asm.mov(eax, __dword_ptr[rbx]);
        asm.movsxd(r13, eax);
        asm.mov(__[r14], r13);

        asm.Label(ref L_fs_read_after);
        asm.lea(rdi, __[r14 + 8]);
        asm.mov(rsi, r13);
        CallLabel(L_validate_utf8);
        asm.cmp(rax, 0);
        asm.je(L_fs_read_invalid_utf8);

        asm.Label(ref L_fs_read_close_and_return);
        asm.mov(rcx, r12);
        CallIat(IatIndex_CloseHandle);
        asm.mov(rdi, r14);
        CallLabel(L_make_result_ok);
        asm.ret();

        asm.Label(ref L_fs_read_invalid_utf8);
        asm.mov(rcx, r12);
        CallIat(IatIndex_CloseHandle);
        asm.mov(rdi, (long)addrOf("__rt_fs_invalid_utf8"));
        CallLabel(L_make_result_error);
        asm.ret();

        asm.Label(ref L_fs_read_fail_with_handle);
        asm.mov(rcx, r12);
        CallIat(IatIndex_CloseHandle);
        asm.Label(ref L_fs_read_fail);
        asm.mov(rdi, (long)addrOf("__rt_fs_read_failed"));
        CallLabel(L_make_result_error);
        asm.ret();

        // fs_write_text(RDI=path string*, RSI=text string*) -> RAX=Result(Str, Unit)
        asm.Label(ref L_fs_write_text);
        var L_fs_write_loop = asm.CreateLabel();
        var L_fs_write_close = asm.CreateLabel();
        var L_fs_write_fail = asm.CreateLabel();
        var L_fs_write_fail_with_handle = asm.CreateLabel();

        asm.mov(r15, rsi);
        asm.call(L_string_to_cstr);
        asm.mov(rcx, rax);
        asm.mov(rdx, 0x40000000);
        asm.xor(r8, r8);
        asm.xor(r9, r9);
        asm.sub(rsp, 0x38);
        asm.xor(rax, rax);
        asm.mov(r10, 2);
        asm.mov(__[rsp + 0x20], r10);
        asm.mov(r10, 0x80);
        asm.mov(__[rsp + 0x28], r10);
        asm.mov(__[rsp + 0x30], rax);
        asm.mov(rax, (long)iatSlotVa(IatIndex_CreateFileA));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, 0x38);
        asm.cmp(rax, -1);
        asm.je(L_fs_write_fail);
        asm.mov(r12, rax);
        asm.lea(r13, __[r15 + 8]);
        asm.mov(r14, __[r15]);

        asm.Label(ref L_fs_write_loop);
        asm.cmp(r14, 0);
        asm.je(L_fs_write_close);
        asm.mov(rcx, r12);
        asm.mov(rdx, r13);
        asm.mov(r8, r14);
        asm.mov(r9, (long)addrOf("bytes_written"));
        asm.sub(rsp, ShadowAndAlign);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x20], rax);
        asm.mov(rax, (long)iatSlotVa(IatIndex_WriteFile));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);
        asm.test(eax, eax);
        asm.jz(L_fs_write_fail_with_handle);
        asm.mov(rbx, (long)addrOf("bytes_written"));
        asm.mov(eax, __dword_ptr[rbx]);
        asm.test(eax, eax);
        asm.jz(L_fs_write_fail_with_handle);
        asm.movsxd(r10, eax);
        asm.sub(r14, r10);
        asm.add(r13, r10);
        asm.jmp(L_fs_write_loop);

        asm.Label(ref L_fs_write_close);
        asm.mov(rcx, r12);
        CallIat(IatIndex_CloseHandle);
        CallLabel(L_make_unit);
        asm.mov(rdi, rax);
        CallLabel(L_make_result_ok);
        asm.ret();

        asm.Label(ref L_fs_write_fail_with_handle);
        asm.mov(rcx, r12);
        CallIat(IatIndex_CloseHandle);
        asm.Label(ref L_fs_write_fail);
        asm.mov(rdi, (long)addrOf("__rt_fs_write_failed"));
        CallLabel(L_make_result_error);
        asm.ret();

        // fs_exists(RDI=path string*) -> RAX=Result(Str, Bool)
        asm.Label(ref L_fs_exists);
        var L_fs_exists_false = asm.CreateLabel();
        asm.call(L_string_to_cstr);
        asm.mov(rcx, rax);
        CallIat(IatIndex_GetFileAttributesA);
        asm.cmp(eax, unchecked((int)0xFFFFFFFF));
        asm.je(L_fs_exists_false);
        asm.mov(rdi, 1);
        CallLabel(L_make_result_ok);
        asm.ret();

        asm.Label(ref L_fs_exists_false);
        asm.xor(rdi, rdi);
        CallLabel(L_make_result_ok);
        asm.ret();

        // tcp_connect(RDI=host string*, RSI=port int) -> RAX=Result(Str, Socket)
        asm.Label(ref L_tcp_connect);
        var L_tcp_connect_fail = asm.CreateLabel();
        var L_tcp_connect_fail_close = asm.CreateLabel();
        var L_tcp_connect_resolve_host = asm.CreateLabel();
        var L_tcp_connect_resolve_fail = asm.CreateLabel();
        var L_tcp_connect_have_addr = asm.CreateLabel();
        asm.mov(r12, rsi);
        CallLabel(L_init_winsock);
        asm.test(eax, eax);
        asm.jz(L_tcp_connect_fail);
        asm.cmp(r12, 0);
        asm.jle(L_tcp_connect_fail);
        asm.cmp(r12, 65535);
        asm.jg(L_tcp_connect_fail);
        asm.call(L_string_to_cstr);
        asm.mov(r14, rax);
        asm.mov(rcx, r14);
        CallIat(IatIndex_inet_addr);
        asm.cmp(eax, unchecked((int)0xFFFFFFFF));
        asm.je(L_tcp_connect_resolve_host);
        asm.mov(r13d, eax);
        asm.jmp(L_tcp_connect_have_addr);

        asm.Label(ref L_tcp_connect_resolve_host);
        asm.mov(rcx, r14);
        CallIat(IatIndex_gethostbyname);
        asm.test(rax, rax);
        asm.jz(L_tcp_connect_resolve_fail);
        asm.mov(rbx, __[rax + 24]);
        asm.test(rbx, rbx);
        asm.jz(L_tcp_connect_resolve_fail);
        asm.mov(rbx, __[rbx]);
        asm.test(rbx, rbx);
        asm.jz(L_tcp_connect_resolve_fail);
        asm.mov(r13d, __dword_ptr[rbx]);
        asm.Label(ref L_tcp_connect_have_addr);
        asm.mov(rcx, 2);
        asm.mov(rdx, 1);
        asm.mov(r8, 6);
        CallIat(IatIndex_socket);
        asm.cmp(rax, -1);
        asm.je(L_tcp_connect_fail);
        asm.mov(r15, rax);
        asm.sub(rsp, 0x48);
        asm.mov(__word_ptr[rsp + 0x20], 2);
        asm.mov(ax, r12w);
        asm.xchg(al, ah);
        asm.mov(__word_ptr[rsp + 0x22], ax);
        asm.mov(__dword_ptr[rsp + 0x24], r13d);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x28], rax);
        asm.mov(rcx, r15);
        asm.lea(rdx, __[rsp + 0x20]);
        asm.mov(r8, 16);
        CallIat(IatIndex_connect);
        asm.add(rsp, 0x48);
        asm.test(eax, eax);
        asm.jne(L_tcp_connect_fail_close);
        asm.mov(rdi, r15);
        CallLabel(L_make_result_ok);
        asm.ret();

        asm.Label(ref L_tcp_connect_resolve_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_resolve_failed"));
        CallLabel(L_make_result_error);
        asm.ret();

        asm.Label(ref L_tcp_connect_fail_close);
        asm.mov(rcx, r15);
        CallIat(IatIndex_closesocket);
        asm.Label(ref L_tcp_connect_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_connect_failed"));
        CallLabel(L_make_result_error);
        asm.ret();

        // tcp_send(RDI=socket, RSI=text string*) -> RAX=Result(Str, Int)
        asm.Label(ref L_tcp_send);
        var L_tcp_send_loop = asm.CreateLabel();
        var L_tcp_send_fail = asm.CreateLabel();
        var L_tcp_send_done = asm.CreateLabel();
        asm.mov(r12, rdi);
        asm.mov(r13, rsi);
        asm.mov(rbx, __[r13]);
        asm.mov(r14, rbx);
        asm.lea(r15, __[r13 + 8]);
        asm.Label(ref L_tcp_send_loop);
        asm.cmp(r14, 0);
        asm.je(L_tcp_send_done);
        asm.mov(rcx, r12);
        asm.mov(rdx, r15);
        asm.mov(r8, r14);
        asm.xor(r9, r9);
        CallIat(IatIndex_send);
        asm.cmp(eax, -1);
        asm.je(L_tcp_send_fail);
        asm.test(eax, eax);
        asm.jz(L_tcp_send_fail);
        asm.movsxd(r10, eax);
        asm.sub(r14, r10);
        asm.add(r15, r10);
        asm.jmp(L_tcp_send_loop);
        asm.Label(ref L_tcp_send_done);
        asm.mov(rdi, rbx);
        CallLabel(L_make_result_ok);
        asm.ret();
        asm.Label(ref L_tcp_send_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_send_failed"));
        CallLabel(L_make_result_error);
        asm.ret();

        // tcp_receive(RDI=socket, RSI=maxBytes) -> RAX=Result(Str, Str)
        asm.Label(ref L_tcp_receive);
        var L_tcp_receive_fail = asm.CreateLabel();
        var L_tcp_receive_invalid_max = asm.CreateLabel();
        var L_tcp_receive_invalid_utf8 = asm.CreateLabel();
        var L_tcp_receive_empty = asm.CreateLabel();
        asm.mov(r12, rdi);
        asm.mov(r13, rsi);
        asm.cmp(r13, 0);
        asm.jle(L_tcp_receive_invalid_max);
        asm.mov(rdi, r13);
        asm.add(rdi, 8);
        CallLabel(L_alloc);
        asm.mov(r14, rax);
        asm.lea(r15, __[r14 + 8]);
        asm.mov(rcx, r12);
        asm.mov(rdx, r15);
        asm.mov(r8, r13);
        asm.xor(r9, r9);
        CallIat(IatIndex_recv);
        asm.cmp(eax, -1);
        asm.je(L_tcp_receive_fail);
        asm.test(eax, eax);
        asm.jz(L_tcp_receive_empty);
        asm.movsxd(r13, eax);
        asm.mov(__[r14], r13);
        asm.mov(rdi, r15);
        asm.mov(rsi, r13);
        CallLabel(L_validate_utf8);
        asm.cmp(rax, 0);
        asm.je(L_tcp_receive_invalid_utf8);
        asm.mov(rdi, r14);
        CallLabel(L_make_result_ok);
        asm.ret();
        asm.Label(ref L_tcp_receive_empty);
        asm.mov(rdi, 8);
        CallLabel(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        asm.mov(rdi, rax);
        CallLabel(L_make_result_ok);
        asm.ret();
        asm.Label(ref L_tcp_receive_invalid_max);
        asm.mov(rdi, (long)addrOf("__rt_tcp_invalid_max_bytes"));
        CallLabel(L_make_result_error);
        asm.ret();
        asm.Label(ref L_tcp_receive_invalid_utf8);
        asm.mov(rdi, (long)addrOf("__rt_tcp_invalid_utf8"));
        CallLabel(L_make_result_error);
        asm.ret();
        asm.Label(ref L_tcp_receive_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_receive_failed"));
        CallLabel(L_make_result_error);
        asm.ret();

        // tcp_close(RDI=socket) -> RAX=Result(Str, Unit)
        asm.Label(ref L_tcp_close);
        var L_tcp_close_fail = asm.CreateLabel();
        asm.mov(rcx, rdi);
        CallIat(IatIndex_closesocket);
        asm.test(eax, eax);
        asm.jne(L_tcp_close_fail);
        CallLabel(L_make_unit);
        asm.mov(rdi, rax);
        CallLabel(L_make_result_ok);
        asm.ret();
        asm.Label(ref L_tcp_close_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_close_failed"));
        CallLabel(L_make_result_error);
        asm.ret();

        // http_request(RDI=url string*, RSI=body string* or 0, RDX=method: 0=get, 1=post)
        asm.Label(ref L_http_request);
        var L_http_return_result = asm.CreateLabel();
        var L_http_return_error = asm.CreateLabel();
        var L_http_check_http = asm.CreateLabel();
        var L_http_scan_host = asm.CreateLabel();
        var L_http_parse_port = asm.CreateLabel();
        var L_http_parse_port_loop = asm.CreateLabel();
        var L_http_default_path = asm.CreateLabel();
        var L_http_after_path = asm.CreateLabel();
        var L_http_use_post_prefix = asm.CreateLabel();
        var L_http_have_prefix = asm.CreateLabel();
        var L_http_have_host_info = asm.CreateLabel();
        var L_http_skip_post_digits = asm.CreateLabel();
        var L_http_have_request_len = asm.CreateLabel();
        var L_http_copy_body = asm.CreateLabel();
        var L_http_send_suffix = asm.CreateLabel();
        var L_http_receive_loop = asm.CreateLabel();
        var L_http_receive_done = asm.CreateLabel();
        var L_http_store_first_chunk = asm.CreateLabel();
        var L_http_find_separator = asm.CreateLabel();
        var L_http_separator_found = asm.CreateLabel();
        var L_http_find_status_space = asm.CreateLabel();
        var L_http_have_status_space = asm.CreateLabel();
        var L_http_chunk_scan_outer = asm.CreateLabel();
        var L_http_chunk_scan_inner = asm.CreateLabel();
        var L_http_chunk_scan_no_match = asm.CreateLabel();
        var L_http_chunk_scan_done = asm.CreateLabel();
        var L_http_chunked_found = asm.CreateLabel();
        var L_http_separator_continue = asm.CreateLabel();
        var L_http_status_success = asm.CreateLabel();
        var L_http_parse_url_fail = asm.CreateLabel();
        var L_http_parse_response_fail = asm.CreateLabel();
        var L_http_send_fail = asm.CreateLabel();
        var L_http_recv_fail = asm.CreateLabel();

        asm.sub(rsp, 0x80);
        asm.mov(__[rsp], rdi);
        asm.mov(__[rsp + 8], rsi);
        asm.mov(__[rsp + 16], rdx);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 32], rax);
        asm.mov(__[rsp + 48], rax);

        asm.mov(r15, __[rsp]);
        asm.mov(r12, __[r15]);
        asm.lea(r11, __[r15 + 8]);

        asm.cmp(r12, 8);
        asm.jb(L_http_check_http);
        asm.movzx(eax, __byte_ptr[r11]);
        asm.cmp(al, 104);
        asm.jne(L_http_check_http);
        asm.movzx(eax, __byte_ptr[r11 + 1]);
        asm.cmp(al, 116);
        asm.jne(L_http_check_http);
        asm.movzx(eax, __byte_ptr[r11 + 2]);
        asm.cmp(al, 116);
        asm.jne(L_http_check_http);
        asm.movzx(eax, __byte_ptr[r11 + 3]);
        asm.cmp(al, 112);
        asm.jne(L_http_check_http);
        asm.movzx(eax, __byte_ptr[r11 + 4]);
        asm.cmp(al, 115);
        asm.jne(L_http_check_http);
        asm.movzx(eax, __byte_ptr[r11 + 5]);
        asm.cmp(al, 58);
        asm.jne(L_http_check_http);
        asm.movzx(eax, __byte_ptr[r11 + 6]);
        asm.cmp(al, 47);
        asm.jne(L_http_check_http);
        asm.movzx(eax, __byte_ptr[r11 + 7]);
        asm.cmp(al, 47);
        asm.jne(L_http_check_http);
        asm.mov(rdi, (long)addrOf("__rt_http_https_not_supported"));
        asm.jmp(L_http_return_error);

        asm.Label(ref L_http_check_http);
        asm.cmp(r12, 7);
        asm.jb(L_http_parse_url_fail);
        asm.movzx(eax, __byte_ptr[r11]);
        asm.cmp(al, 104);
        asm.jne(L_http_parse_url_fail);
        asm.movzx(eax, __byte_ptr[r11 + 1]);
        asm.cmp(al, 116);
        asm.jne(L_http_parse_url_fail);
        asm.movzx(eax, __byte_ptr[r11 + 2]);
        asm.cmp(al, 116);
        asm.jne(L_http_parse_url_fail);
        asm.movzx(eax, __byte_ptr[r11 + 3]);
        asm.cmp(al, 112);
        asm.jne(L_http_parse_url_fail);
        asm.movzx(eax, __byte_ptr[r11 + 4]);
        asm.cmp(al, 58);
        asm.jne(L_http_parse_url_fail);
        asm.movzx(eax, __byte_ptr[r11 + 5]);
        asm.cmp(al, 47);
        asm.jne(L_http_parse_url_fail);
        asm.movzx(eax, __byte_ptr[r11 + 6]);
        asm.cmp(al, 47);
        asm.jne(L_http_parse_url_fail);

        asm.mov(r9, 7);
        asm.mov(r10, 80);
        asm.Label(ref L_http_scan_host);
        asm.cmp(r9, r12);
        asm.jae(L_http_default_path);
        asm.movzx(eax, __byte_ptr[r11 + r9]);
        asm.cmp(al, 58);
        asm.je(L_http_parse_port);
        asm.cmp(al, 47);
        asm.je(L_http_after_path);
        asm.cmp(al, 63);
        asm.je(L_http_parse_url_fail);
        asm.cmp(al, 35);
        asm.je(L_http_parse_url_fail);
        asm.inc(r9);
        asm.jmp(L_http_scan_host);

        asm.Label(ref L_http_parse_port);
        asm.mov(rbx, r9);
        asm.sub(rbx, 7);
        asm.cmp(rbx, 0);
        asm.jle(L_http_parse_url_fail);
        asm.lea(rax, __[r11 + 7]);
        asm.mov(__[rsp + 40], rax);
        asm.mov(__[rsp + 48], rbx);
        asm.mov(r10, 0);
        asm.xor(r8, r8);
        asm.inc(r9);
        asm.Label(ref L_http_parse_port_loop);
        asm.cmp(r9, r12);
        asm.jae(L_http_default_path);
        asm.movzx(eax, __byte_ptr[r11 + r9]);
        asm.cmp(al, 47);
        asm.je(L_http_after_path);
        asm.cmp(al, 48);
        asm.jb(L_http_parse_url_fail);
        asm.cmp(al, 57);
        asm.ja(L_http_parse_url_fail);
        asm.imul(r10, r10, 10);
        asm.sub(rax, '0');
        asm.add(r10, rax);
        asm.cmp(r10, 65535);
        asm.jg(L_http_parse_url_fail);
        asm.inc(r8);
        asm.inc(r9);
        asm.jmp(L_http_parse_port_loop);

        asm.Label(ref L_http_default_path);
        asm.mov(rbx, r9);
        asm.sub(rbx, 7);
        asm.cmp(rbx, 0);
        asm.jle(L_http_parse_url_fail);
        asm.mov(rax, (long)addrOf("__rt_http_default_path"));
        asm.add(rax, 8);
        asm.mov(__[rsp + 24], rax);
        asm.mov(rax, 1);
        asm.mov(__[rsp + 32], rax);
        asm.jmp(L_http_after_path);

        asm.Label(ref L_http_after_path);
        asm.mov(rbx, r9);
        asm.sub(rbx, 7);
        asm.cmp(rbx, 0);
        asm.jle(L_http_parse_url_fail);
        asm.mov(rax, __[rsp + 48]);
        asm.cmp(rax, 0);
        asm.jne(L_http_have_host_info);
        asm.lea(rax, __[r11 + 7]);
        asm.mov(__[rsp + 40], rax);
        asm.mov(__[rsp + 48], rbx);
        asm.Label(ref L_http_have_host_info);
        asm.mov(__[rsp + 56], r10);
        asm.mov(rax, __[rsp + 32]);
        asm.cmp(rax, 0);
        asm.jne(L_http_use_post_prefix);
        asm.cmp(r9, r12);
        asm.jae(L_http_use_post_prefix);
        asm.lea(rax, __[r11 + r9]);
        asm.mov(__[rsp + 24], rax);
        asm.mov(rax, r12);
        asm.sub(rax, r9);
        asm.mov(__[rsp + 32], rax);

        asm.Label(ref L_http_use_post_prefix);
        asm.mov(rdi, __[rsp + 40]);
        asm.mov(rsi, __[rsp + 48]);
        CallLabel(L_make_string_slice);
        asm.mov(rdi, rax);
        asm.mov(rsi, __[rsp + 56]);
        CallLabel(L_tcp_connect);
        asm.mov(rdx, __[rax]);
        asm.cmp(rdx, 0);
        asm.jne(L_http_return_result);
        asm.mov(r15, __[rax + 8]);
        asm.mov(__[rsp + 64], r15);

        asm.mov(rax, __[rsp + 16]);
        asm.cmp(rax, 0);
        asm.jne(L_http_have_prefix);
        asm.mov(rax, (long)addrOf("__rt_http_get_prefix"));
        asm.mov(__[rsp + 72], rax);
        asm.jmp(L_http_skip_post_digits);

        asm.Label(ref L_http_have_prefix);
        asm.mov(rax, (long)addrOf("__rt_http_post_prefix"));
        asm.mov(__[rsp + 72], rax);
        asm.mov(rdi, __[rsp + 8]);
        asm.mov(rdi, __[rdi]);
        asm.mov(rsi, (long)addrOf("buf_i64"));
        CallLabel(L_itoa);
        asm.mov(__[rsp + 80], rsi);
        asm.mov(__[rsp + 88], rdx);

        asm.Label(ref L_http_skip_post_digits);
        asm.mov(rbx, __[rsp + 72]);
        asm.mov(rdi, __[rbx]);
        asm.add(rdi, __[rsp + 32]);
        asm.mov(rax, (long)addrOf("__rt_http_host_header"));
        asm.add(rdi, __[rax]);
        asm.add(rdi, __[rsp + 48]);
        asm.mov(rax, __[rsp + 16]);
        asm.cmp(rax, 0);
        asm.je(L_http_have_request_len);
        asm.mov(rax, (long)addrOf("__rt_http_content_length_header"));
        asm.add(rdi, __[rax]);
        asm.add(rdi, __[rsp + 88]);
        asm.mov(rax, (long)addrOf("__rt_http_request_suffix"));
        asm.add(rdi, __[rax]);
        asm.mov(rax, __[rsp + 8]);
        asm.add(rdi, __[rax]);
        asm.jmp(L_http_send_suffix);

        asm.Label(ref L_http_have_request_len);
        asm.mov(rax, (long)addrOf("__rt_http_request_suffix"));
        asm.add(rdi, __[rax]);

        asm.Label(ref L_http_send_suffix);
        asm.mov(__[rsp + 104], rdi);
        asm.add(rdi, 8);
        CallLabel(L_alloc);
        asm.mov(r12, rax);
        asm.mov(rbx, __[rsp + 104]);
        asm.mov(__[r12], rbx);
        asm.lea(rdi, __[r12 + 8]);

        asm.mov(rsi, __[rsp + 72]);
        asm.mov(rcx, __[rsi]);
        asm.add(rsi, 8);
        asm.rep.movsb();

        asm.mov(rsi, __[rsp + 24]);
        asm.mov(rcx, __[rsp + 32]);
        asm.rep.movsb();

        asm.mov(rsi, (long)addrOf("__rt_http_host_header"));
        asm.mov(rcx, __[rsi]);
        asm.add(rsi, 8);
        asm.rep.movsb();

        asm.mov(rsi, __[rsp + 40]);
        asm.mov(rcx, __[rsp + 48]);
        asm.rep.movsb();

        asm.mov(rax, __[rsp + 16]);
        asm.cmp(rax, 0);
        asm.je(L_http_copy_body);
        asm.mov(rsi, (long)addrOf("__rt_http_content_length_header"));
        asm.mov(rcx, __[rsi]);
        asm.add(rsi, 8);
        asm.rep.movsb();
        asm.mov(rsi, __[rsp + 80]);
        asm.mov(rcx, __[rsp + 88]);
        asm.rep.movsb();

        asm.Label(ref L_http_copy_body);
        asm.mov(rsi, (long)addrOf("__rt_http_request_suffix"));
        asm.mov(rcx, __[rsi]);
        asm.add(rsi, 8);
        asm.rep.movsb();

        asm.mov(rax, __[rsp + 16]);
        asm.cmp(rax, 0);
        asm.je(L_http_receive_loop);
        asm.mov(rsi, __[rsp + 8]);
        asm.mov(rcx, __[rsi]);
        asm.add(rsi, 8);
        asm.rep.movsb();

        asm.Label(ref L_http_receive_loop);
        asm.mov(rdi, __[rsp + 64]);
        asm.mov(rsi, r12);
        CallLabel(L_tcp_send);
        asm.mov(rdx, __[rax]);
        asm.cmp(rdx, 0);
        asm.jne(L_http_send_fail);

        asm.mov(rax, (long)addrOf("__rt_empty"));
        asm.mov(__[rsp + 96], rax);
        asm.Label(ref L_http_receive_done);
        asm.mov(rdi, __[rsp + 64]);
        asm.mov(rsi, 65536);
        CallLabel(L_tcp_receive);
        asm.mov(rdx, __[rax]);
        asm.cmp(rdx, 0);
        asm.jne(L_http_recv_fail);
        asm.mov(r13, __[rax + 8]);
        asm.mov(rax, __[r13]);
        asm.cmp(rax, 0);
        asm.je(L_http_find_separator);
        asm.mov(rdi, __[rsp + 96]);
        asm.mov(rax, __[rdi]);
        asm.cmp(rax, 0);
        asm.je(L_http_store_first_chunk);
        asm.mov(rdi, __[rsp + 96]);
        asm.mov(rsi, r13);
        CallLabel(L_concat);
        asm.mov(__[rsp + 96], rax);
        asm.jmp(L_http_receive_done);

        asm.Label(ref L_http_store_first_chunk);
        asm.mov(__[rsp + 96], r13);
        asm.jmp(L_http_receive_done);

        asm.Label(ref L_http_find_separator);
        asm.mov(rdi, __[rsp + 64]);
        CallLabel(L_tcp_close);
        asm.mov(rdx, __[rax]);
        asm.cmp(rdx, 0);
        asm.jne(L_http_return_result);
        asm.mov(r14, __[rsp + 96]);
        asm.mov(r12, __[r14]);
        asm.lea(r13, __[r14 + 8]);
        asm.cmp(r12, 12);
        asm.jb(L_http_parse_response_fail);
        asm.xor(r8, r8);
        asm.Label(ref L_http_separator_found);
        asm.mov(rax, r12);
        asm.sub(rax, 3);
        asm.cmp(r8, rax);
        asm.jae(L_http_parse_response_fail);
        asm.movzx(eax, __byte_ptr[r13 + r8]);
        asm.cmp(al, 13);
        asm.jne(L_http_separator_continue);
        asm.movzx(eax, __byte_ptr[r13 + r8 + 1]);
        asm.cmp(al, 10);
        asm.jne(L_http_separator_continue);
        asm.movzx(eax, __byte_ptr[r13 + r8 + 2]);
        asm.cmp(al, 13);
        asm.jne(L_http_separator_continue);
        asm.movzx(eax, __byte_ptr[r13 + r8 + 3]);
        asm.cmp(al, 10);
        asm.je(L_http_find_status_space);
        asm.Label(ref L_http_separator_continue);
        asm.inc(r8);
        asm.jmp(L_http_separator_found);

        asm.Label(ref L_http_find_status_space);
        asm.xor(r9, r9);
        asm.Label(ref L_http_have_status_space);
        asm.cmp(r9, r8);
        asm.jae(L_http_parse_response_fail);
        asm.movzx(eax, __byte_ptr[r13 + r9]);
        asm.cmp(al, 32);
        asm.je(L_http_chunk_scan_outer);
        asm.inc(r9);
        asm.jmp(L_http_have_status_space);

        asm.Label(ref L_http_chunk_scan_outer);
        asm.mov(rax, r9);
        asm.add(rax, 3);
        asm.cmp(rax, r8);
        asm.jae(L_http_parse_response_fail);
        asm.movzx(eax, __byte_ptr[r13 + r9 + 1]);
        asm.cmp(al, 48);
        asm.jb(L_http_parse_response_fail);
        asm.cmp(al, 57);
        asm.ja(L_http_parse_response_fail);
        asm.sub(rax, '0');
        asm.imul(r10, rax, 100);
        asm.movzx(eax, __byte_ptr[r13 + r9 + 2]);
        asm.cmp(al, 48);
        asm.jb(L_http_parse_response_fail);
        asm.cmp(al, 57);
        asm.ja(L_http_parse_response_fail);
        asm.sub(rax, '0');
        asm.imul(rax, rax, 10);
        asm.add(r10, rax);
        asm.movzx(eax, __byte_ptr[r13 + r9 + 3]);
        asm.cmp(al, 48);
        asm.jb(L_http_parse_response_fail);
        asm.cmp(al, 57);
        asm.ja(L_http_parse_response_fail);
        asm.sub(rax, '0');
        asm.add(r10, rax);

        asm.mov(r11, (long)addrOf("__rt_http_chunked_header"));
        asm.mov(rbx, __[r11]);
        asm.add(r11, 8);
        asm.cmp(r8, rbx);
        asm.jb(L_http_chunk_scan_done);
        asm.xor(r9, r9);
        asm.Label(ref L_http_chunk_scan_inner);
        asm.mov(rax, r8);
        asm.sub(rax, rbx);
        asm.cmp(r9, rax);
        asm.ja(L_http_chunk_scan_done);
        asm.xor(rcx, rcx);
        asm.Label(ref L_http_chunk_scan_no_match);
        asm.cmp(rcx, rbx);
        asm.je(L_http_chunked_found);
        asm.mov(rax, r13);
        asm.add(rax, r9);
        asm.add(rax, rcx);
        asm.movzx(eax, __byte_ptr[rax]);
        asm.mov(rdx, r11);
        asm.add(rdx, rcx);
        asm.movzx(edx, __byte_ptr[rdx]);
        asm.cmp(al, dl);
        asm.jne(L_http_chunk_scan_done);
        asm.inc(rcx);
        asm.jmp(L_http_chunk_scan_no_match);

        asm.Label(ref L_http_chunk_scan_done);
        asm.inc(r9);
        asm.mov(rax, r8);
        asm.sub(rax, rbx);
        asm.cmp(r9, rax);
        asm.jbe(L_http_chunk_scan_inner);

        asm.lea(rdi, __[r13 + r8 + 4]);
        asm.mov(rsi, r12);
        asm.sub(rsi, r8);
        asm.sub(rsi, 4);
        CallLabel(L_make_string_slice);
        asm.mov(r12, rax);
        asm.cmp(r10, 200);
        asm.jb(L_http_return_result);
        asm.cmp(r10, 299);
        asm.ja(L_http_status_success);
        asm.mov(rdi, r12);
        CallLabel(L_make_result_ok);
        asm.jmp(L_http_return_result);

        asm.Label(ref L_http_chunked_found);
        asm.mov(rdi, (long)addrOf("__rt_http_unsupported_transfer_encoding"));
        asm.jmp(L_http_return_error);

        asm.Label(ref L_http_parse_url_fail);
        asm.mov(rdi, (long)addrOf("__rt_http_malformed_url"));
        asm.jmp(L_http_return_error);

        asm.Label(ref L_http_parse_response_fail);
        asm.mov(rdi, (long)addrOf("__rt_http_malformed_response"));
        asm.jmp(L_http_return_error);

        asm.Label(ref L_http_send_fail);
        asm.mov(__[rsp + 112], rax);
        asm.mov(rdi, __[rsp + 64]);
        CallLabel(L_tcp_close);
        asm.mov(rax, __[rsp + 112]);
        asm.jmp(L_http_return_result);

        asm.Label(ref L_http_recv_fail);
        asm.mov(__[rsp + 112], rax);
        asm.mov(rdi, __[rsp + 64]);
        CallLabel(L_tcp_close);
        asm.mov(rax, __[rsp + 112]);
        asm.jmp(L_http_return_result);

        asm.Label(ref L_http_status_success);
        asm.mov(rdi, r10);
        CallLabel(L_make_http_status_error);
        asm.mov(rdi, rax);
        asm.Label(ref L_http_return_error);
        CallLabel(L_make_result_error);
        asm.Label(ref L_http_return_result);
        asm.add(rsp, 0x80);
        asm.ret();

        // panic_str(RDI=string*) -> prints message and exits with code 1
        asm.Label(ref L_panic_str);
        CallLabel(L_print_str);
        asm.mov(rcx, 1);
        CallIat(IatIndex_ExitProcess);
        asm.ret();

        // print_bool(RDI=bool64)
        asm.Label(ref L_print_bool);
        var L_pb_false = asm.CreateLabel();
        var L_pb_end = asm.CreateLabel();

        asm.cmp(rdi, 0);
        asm.je(L_pb_false);
        asm.mov(rdi, (long)addrOf(trueLabel));
        CallLabel(L_print_str);
        asm.jmp(L_pb_end);
        asm.Label(ref L_pb_false);
        asm.mov(rdi, (long)addrOf(falseLabel));
        CallLabel(L_print_str);
        asm.Label(ref L_pb_end);
        asm.ret();


        // print_int(RDI=i64)
        // NOTE: we emit this after concat is patched (see below)
        // itoa(value in RDI, buf in RSI) -> RSI=ptr, RDX=len
        // then WriteFile(ptr,len), newline
        asm.Label(ref L_print_int);
        asm.mov(rsi, (long)addrOf("buf_i64"));
        CallLabel(L_itoa);

        // rcx=stdout handle
        asm.mov(rbx, (long)addrOf("stdout_handle"));
        asm.mov(rcx, __[rbx]);
        // r8 = len (rdx from itoa is length)
        asm.mov(r8, rdx);
        // rdx = ptr (rsi from itoa is pointer to digit string; WriteFile arg2 = lpBuffer)
        asm.mov(rdx, rsi);
        // r9 = &bytes_written
        asm.mov(r9, (long)addrOf("bytes_written"));

        asm.sub(rsp, ShadowAndAlign);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x20], rax);
        asm.mov(rax, (long)iatSlotVa(IatIndex_WriteFile));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);

        // newline
        asm.mov(rbx, (long)addrOf("stdout_handle"));
        asm.mov(rcx, __[rbx]);
        asm.mov(rdx, (long)addrOf("nl"));
        asm.mov(r8, 1);
        asm.mov(r9, (long)addrOf("bytes_written"));
        asm.sub(rsp, ShadowAndAlign);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 0x20], rax);
        asm.mov(rax, (long)iatSlotVa(IatIndex_WriteFile));
        asm.mov(rax, __[rax]);
        asm.call(rax);
        asm.add(rsp, ShadowAndAlign);

        asm.ret();

        // itoa(value in RDI, buf in RSI) -> RSI=ptr, RDX=len
        asm.Label(ref L_itoa);
        asm.mov(r8, rsi);              // buf base
        asm.lea(r9, __[r8 + 31]);      // buf end
        asm.xor(rcx, rcx);             // sign flag
        asm.mov(rax, rdi);             // value

        asm.cmp(rax, 0);
        asm.jge(L_abs_ok);
        asm.neg(rax);
        asm.mov(rcx, 1);
        asm.Label(ref L_abs_ok);

        asm.cmp(rax, 0);
        asm.je(L_zero);

        asm.mov(r10, 10);

        asm.Label(ref L_loop);
        asm.xor(rdx, rdx);
        asm.div(r10);
        asm.add(dl, (byte)'0');
        asm.mov(__byte_ptr[r9], dl);
        asm.dec(r9);
        asm.cmp(rax, 0);
        asm.jne(L_loop);

        asm.inc(r9);
        asm.mov(rsi, r9);

        asm.lea(r11, __[r8 + 31]);
        asm.mov(rdx, r11);
        asm.sub(rdx, rsi);
        asm.inc(rdx);

        asm.jmp(L_maybe_sign);

        asm.Label(ref L_zero);
        asm.mov(__byte_ptr[r9], (byte)'0');
        asm.mov(rsi, r9);
        asm.mov(rdx, 1);

        asm.Label(ref L_maybe_sign);
        asm.cmp(rcx, 1);
        asm.jne(L_done);
        asm.dec(rsi);
        asm.mov(__byte_ptr[rsi], (byte)'-');
        asm.inc(rdx);

        asm.Label(ref L_done);
        asm.ret();

        // str_eq(RDI=a, RSI=b) -> RAX (1=equal, 0=not-equal)
        // Uses the same internal RDI/RSI convention as all other runtime helpers in this backend.
        // String layout: [length:i64, bytes...]
        asm.Label(ref L_str_eq);
        var L_streq_ne = asm.CreateLabel();
        var L_streq_eq = asm.CreateLabel();
        asm.mov(rax, __[rdi]);       // lenA
        asm.cmp(rax, __[rsi]);       // lenA == lenB?
        asm.jne(L_streq_ne);
        asm.test(rax, rax);          // both empty?
        asm.je(L_streq_eq);
        asm.mov(rcx, rax);           // count = len
        asm.add(rdi, 8);             // bytes of a
        asm.add(rsi, 8);             // bytes of b
        asm.repe.cmpsb();
        asm.jne(L_streq_ne);
        asm.Label(ref L_streq_eq);
        asm.mov(rax, 1);
        asm.ret();
        asm.Label(ref L_streq_ne);
        asm.xor(rax, rax);
        asm.ret();

        // ---- Patch concat ----
        // Iced Assembler doesn't support editing previous emitted bytes easily.
        // We therefore emit concat as a separate small function by re-creating it correctly
        // at the end and never referencing the earlier placeholder.
        //
        // We'll do that by re-binding L_concat to the correct implementation *after* assembly.
        // In Iced, re-binding a label is not allowed; so we instead avoid using the placeholder:
        // EmitFunctionBody will call a dedicated label that we set here.
        //
        // To keep changes minimal, we will not use p.UsesConcatStr to call L_concat in this backend.
        //
        // (We implement concat directly in EmitFunctionBody below.)
        // ----------------------

        using var ms = new MemoryStream();
        var writer = new StreamCodeWriter(ms);
        asm.Assemble(writer, textVA);
        return ms.ToArray();

        // local function emitting IR function bodies
        void EmitFunctionBody(Assembler a, IrFunction fn, Func<string, ulong> addr, Dictionary<string, Label> fLabels, string tLbl, string fLbl, bool hasParams)
        {
            a.push(rbp);
            a.mov(rbp, rsp);

            int slotCount = fn.LocalCount + fn.TempCount;
            int frameSize = (slotCount * 8 + 15) & ~15;
            // Always reserve frameSize + 8 bytes: frameSize for locals/temps, and 8 extra bytes
            // to restore the 8-mod-16 alignment that push rbp disturbed (push rbp subtracts 8 from
            // an entry RSP of 8 mod 16, leaving 0 mod 16; adding 8 gives back 8 mod 16 so that
            // Win64 API calls from this function see the required RSP alignment at their entry).
            a.sub(rsp, frameSize + 8);

            if (hasParams)
            {
                a.mov(__[rbp - 8], rdi);
                a.mov(__[rbp - 16], rsi);
            }

            var localLabels = new Dictionary<string, Label>(StringComparer.Ordinal);

            Label GetLabel(string name)
            {
                if (!localLabels.TryGetValue(name, out var lab))
                {
                    lab = a.CreateLabel();
                    localLabels[name] = lab;
                }
                return lab;
            }

            int TempSlot(int t) => fn.LocalCount + t;
            int DispForSlot(int slot) => -8 * (slot + 1);

            bool needsHeap = p.UsesConcatStr || p.UsesClosures || fn.Instructions.Any(i => i is IrInst.Alloc or IrInst.AllocAdt);
            if (needsHeap)
            {
                var ok = a.CreateLabel();
                a.mov(rbx, (long)addr("heap_ptr"));
                a.mov(rax, __[rbx]);
                a.cmp(rax, 0);
                a.jne(ok);
                // init_heap
                CallLabel(L_init_heap);
                a.Label(ref ok);
            }

            foreach (var i in fn.Instructions)
            {
                if (i is IrInst.Label lab)
                {
                    _ = GetLabel(lab.Name);
                }
            }

            foreach (var inst in fn.Instructions)
            {
                switch (inst)
                {
                    case IrInst.Label lab:
                        a.nop();
                        var l = GetLabel(lab.Name);
                        a.Label(ref l);
                        break;

                    case IrInst.LoadConstInt lc:
                        a.mov(rax, lc.Value);
                        a.mov(__[rbp + DispForSlot(TempSlot(lc.Target))], rax);
                        break;

                    case IrInst.LoadConstFloat lc:
                        a.mov(rax, BitConverter.DoubleToInt64Bits(lc.Value));
                        a.mov(__[rbp + DispForSlot(TempSlot(lc.Target))], rax);
                        break;

                    case IrInst.LoadConstBool lb:
                        a.mov(rax, lb.Value ? 1 : 0);
                        a.mov(__[rbp + DispForSlot(TempSlot(lb.Target))], rax);
                        break;

                    case IrInst.LoadConstStr ls:
                        a.mov(rax, (long)addr(ls.StrLabel));
                        a.mov(__[rbp + DispForSlot(TempSlot(ls.Target))], rax);
                        break;

                    case IrInst.LoadProgramArgs pa:
                        a.mov(rbx, (long)addr("program_args"));
                        a.mov(rax, __[rbx]);
                        a.mov(__[rbp + DispForSlot(TempSlot(pa.Target))], rax);
                        break;

                    case IrInst.AddInt add:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(add.Left))]);
                        a.add(rax, __[rbp + DispForSlot(TempSlot(add.Right))]);
                        a.mov(__[rbp + DispForSlot(TempSlot(add.Target))], rax);
                        break;

                    case IrInst.AddFloat add:
                        a.movsd(xmm0, __[rbp + DispForSlot(TempSlot(add.Left))]);
                        a.movsd(xmm1, __[rbp + DispForSlot(TempSlot(add.Right))]);
                        a.addsd(xmm0, xmm1);
                        a.movsd(__[rbp + DispForSlot(TempSlot(add.Target))], xmm0);
                        break;

                    case IrInst.SubInt sub:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(sub.Left))]);
                        a.sub(rax, __[rbp + DispForSlot(TempSlot(sub.Right))]);
                        a.mov(__[rbp + DispForSlot(TempSlot(sub.Target))], rax);
                        break;

                    case IrInst.SubFloat sub:
                        a.movsd(xmm0, __[rbp + DispForSlot(TempSlot(sub.Left))]);
                        a.movsd(xmm1, __[rbp + DispForSlot(TempSlot(sub.Right))]);
                        a.subsd(xmm0, xmm1);
                        a.movsd(__[rbp + DispForSlot(TempSlot(sub.Target))], xmm0);
                        break;

                    case IrInst.MulInt mul:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(mul.Left))]);
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(mul.Right))]);
                        a.imul(rax, rbx);
                        a.mov(__[rbp + DispForSlot(TempSlot(mul.Target))], rax);
                        break;

                    case IrInst.MulFloat mul:
                        a.movsd(xmm0, __[rbp + DispForSlot(TempSlot(mul.Left))]);
                        a.movsd(xmm1, __[rbp + DispForSlot(TempSlot(mul.Right))]);
                        a.mulsd(xmm0, xmm1);
                        a.movsd(__[rbp + DispForSlot(TempSlot(mul.Target))], xmm0);
                        break;

                    case IrInst.DivInt div:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(div.Left))]);
                        a.cqo();
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(div.Right))]);
                        a.idiv(rbx);
                        a.mov(__[rbp + DispForSlot(TempSlot(div.Target))], rax);
                        break;

                    case IrInst.DivFloat div:
                        a.movsd(xmm0, __[rbp + DispForSlot(TempSlot(div.Left))]);
                        a.movsd(xmm1, __[rbp + DispForSlot(TempSlot(div.Right))]);
                        a.divsd(xmm0, xmm1);
                        a.movsd(__[rbp + DispForSlot(TempSlot(div.Target))], xmm0);
                        break;

                    case IrInst.CmpIntGe ge:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(ge.Left))]);
                        a.cmp(rax, __[rbp + DispForSlot(TempSlot(ge.Right))]);
                        a.setge(al);
                        a.movzx(rax, al);
                        a.mov(__[rbp + DispForSlot(TempSlot(ge.Target))], rax);
                        break;

                    case IrInst.CmpFloatGe ge:
                        a.movsd(xmm0, __[rbp + DispForSlot(TempSlot(ge.Left))]);
                        a.movsd(xmm1, __[rbp + DispForSlot(TempSlot(ge.Right))]);
                        a.ucomisd(xmm0, xmm1);
                        a.setae(al);
                        a.setnp(bl);
                        a.movzx(rax, al);
                        a.movzx(rbx, bl);
                        a.imul(rax, rbx);
                        a.mov(__[rbp + DispForSlot(TempSlot(ge.Target))], rax);
                        break;

                    case IrInst.CmpIntLe le:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(le.Left))]);
                        a.cmp(rax, __[rbp + DispForSlot(TempSlot(le.Right))]);
                        a.setle(al);
                        a.movzx(rax, al);
                        a.mov(__[rbp + DispForSlot(TempSlot(le.Target))], rax);
                        break;

                    case IrInst.CmpFloatLe le:
                        a.movsd(xmm0, __[rbp + DispForSlot(TempSlot(le.Left))]);
                        a.movsd(xmm1, __[rbp + DispForSlot(TempSlot(le.Right))]);
                        a.ucomisd(xmm0, xmm1);
                        a.setbe(al);
                        a.setnp(bl);
                        a.movzx(rax, al);
                        a.movzx(rbx, bl);
                        a.imul(rax, rbx);
                        a.mov(__[rbp + DispForSlot(TempSlot(le.Target))], rax);
                        break;

                    case IrInst.CmpIntEq eq:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(eq.Left))]);
                        a.cmp(rax, __[rbp + DispForSlot(TempSlot(eq.Right))]);
                        a.sete(al);
                        a.movzx(rax, al);
                        a.mov(__[rbp + DispForSlot(TempSlot(eq.Target))], rax);
                        break;

                    case IrInst.CmpFloatEq eq:
                        a.movsd(xmm0, __[rbp + DispForSlot(TempSlot(eq.Left))]);
                        a.movsd(xmm1, __[rbp + DispForSlot(TempSlot(eq.Right))]);
                        a.ucomisd(xmm0, xmm1);
                        a.sete(al);
                        a.setnp(bl);
                        a.movzx(rax, al);
                        a.movzx(rbx, bl);
                        a.imul(rax, rbx);
                        a.mov(__[rbp + DispForSlot(TempSlot(eq.Target))], rax);
                        break;

                    case IrInst.CmpIntNe ne:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(ne.Left))]);
                        a.cmp(rax, __[rbp + DispForSlot(TempSlot(ne.Right))]);
                        a.setne(al);
                        a.movzx(rax, al);
                        a.mov(__[rbp + DispForSlot(TempSlot(ne.Target))], rax);
                        break;

                    case IrInst.CmpFloatNe ne:
                        a.movsd(xmm0, __[rbp + DispForSlot(TempSlot(ne.Left))]);
                        a.movsd(xmm1, __[rbp + DispForSlot(TempSlot(ne.Right))]);
                        a.ucomisd(xmm0, xmm1);
                        a.setne(al);
                        a.movzx(rax, al);
                        a.mov(__[rbp + DispForSlot(TempSlot(ne.Target))], rax);
                        break;

                    case IrInst.CmpStrEq seq:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(seq.Left))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(seq.Right))]);
                        CallLabel(L_str_eq);
                        a.mov(__[rbp + DispForSlot(TempSlot(seq.Target))], rax);
                        break;

                    case IrInst.CmpStrNe sne:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(sne.Left))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(sne.Right))]);
                        CallLabel(L_str_eq);
                        a.xor(rax, 1);
                        a.mov(__[rbp + DispForSlot(TempSlot(sne.Target))], rax);
                        break;

                    case IrInst.ConcatStr cat:
                        // Implement concat inline using alloc:
                        // str = alloc(8 + lenA + lenB), store len, copy bytes
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(cat.Left))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(cat.Right))]);

                        // r8=lenA, r9=lenB, r10=total
                        a.mov(r8, __[rdi]);
                        a.mov(r9, __[rsi]);
                        a.mov(r10, r8);
                        a.add(r10, r9);

                        // Save ptr to string A on the stack before rdi is overwritten with alloc size.
                        a.push(rdi);

                        // alloc size = 8 + total
                        a.mov(rdi, r10);
                        a.add(rdi, 8);
                        CallLabel(L_alloc); // returns RAX; uses rbx internally

                        // Restore ptr to string A from stack into rbx (rax must not be clobbered).
                        a.pop(rbx);

                        // store len
                        a.mov(__[rax], r10);

                        // dest bytes ptr in r11 = rax+8
                        a.lea(r11, __[rax + 8]);

                        // copy A bytes: memcpy loop (rbx = ptr to string A)
                        var L_cpyA = a.CreateLabel();
                        var L_cpyA_done = a.CreateLabel();
                        a.xor(rcx, rcx); // i=0
                        a.cmp(r8, 0);
                        a.je(L_cpyA_done);
                        a.Label(ref L_cpyA);
                        a.mov(dl, __byte_ptr[rbx + rcx + 8]);
                        a.mov(__byte_ptr[r11 + rcx], dl);
                        a.inc(rcx);
                        a.cmp(rcx, r8);
                        a.jne(L_cpyA);
                        a.Label(ref L_cpyA_done);

                        // Advance dest ptr past A bytes so B-copy uses r11+rcx
                        a.add(r11, r8);

                        // copy B bytes: rcx=0
                        var L_cpyB = a.CreateLabel();
                        var L_cpyB_done = a.CreateLabel();
                        a.xor(rcx, rcx);
                        a.cmp(r9, 0);
                        a.je(L_cpyB_done);
                        a.Label(ref L_cpyB);
                        a.mov(dl, __byte_ptr[rsi + rcx + 8]);
                        a.mov(__byte_ptr[r11 + rcx], dl);
                        a.inc(rcx);
                        a.cmp(rcx, r9);
                        a.jne(L_cpyB);
                        a.Label(ref L_cpyB_done);

                        a.mov(__[rbp + DispForSlot(TempSlot(cat.Target))], rax);
                        break;

                    case IrInst.MakeClosure mc:
                        // closure = alloc(16); [0]=codeptr; [8]=envptr
                        a.mov(rdi, 16);
                        CallLabel(L_alloc);
                        a.mov(rbx, rax); // closure ptr

                        // code ptr
                        var target = fLabels[mc.FuncLabel];
                        // RIP-relative LEA via Iced label reference
                        a.lea(rax, __[target]);
                        a.mov(__[rbx], rax);

                        // env ptr in rax is stored temp
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(mc.EnvPtrTemp))]);
                        a.mov(__[rbx + 8], rax);

                        a.mov(__[rbp + DispForSlot(TempSlot(mc.Target))], rbx);
                        break;

                    case IrInst.CallClosure cc:
                        // closure temp holds [code, env]
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(cc.ClosureTemp))]);
                        a.mov(rax, __[rbx]);       // code
                        a.mov(rdi, __[rbx + 8]);   // env
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(cc.ArgTemp))]); // arg
                        // call code (indirect). Use Win64 shadow around call.
                        a.sub(rsp, ShadowAndAlign);
                        a.call(rax);
                        a.add(rsp, ShadowAndAlign);
                        a.mov(__[rbp + DispForSlot(TempSlot(cc.Target))], rax);
                        break;

                    case IrInst.PrintInt pi:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(pi.Source))]);
                        CallLabel(L_print_int);
                        break;

                    case IrInst.PrintStr ps:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ps.Source))]);
                        CallLabel(L_print_str);
                        break;

                    case IrInst.PrintBool pb:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(pb.Source))]);
                        CallLabel(L_print_bool);
                        break;

                    case IrInst.WriteStr ws:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ws.Source))]);
                        CallLabel(L_write_str);
                        break;

                    case IrInst.ReadLine rl:
                        CallLabel(L_read_line);
                        a.mov(__[rbp + DispForSlot(TempSlot(rl.Target))], rax);
                        break;

                    case IrInst.FileReadText frt:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(frt.PathTemp))]);
                        CallLabel(L_fs_read_text);
                        a.mov(__[rbp + DispForSlot(TempSlot(frt.Target))], rax);
                        break;

                    case IrInst.FileWriteText fwt:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(fwt.PathTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(fwt.TextTemp))]);
                        CallLabel(L_fs_write_text);
                        a.mov(__[rbp + DispForSlot(TempSlot(fwt.Target))], rax);
                        break;

                    case IrInst.FileExists fex:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(fex.PathTemp))]);
                        CallLabel(L_fs_exists);
                        a.mov(__[rbp + DispForSlot(TempSlot(fex.Target))], rax);
                        break;

                    case IrInst.HttpGet hg:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(hg.UrlTemp))]);
                        a.xor(rsi, rsi);
                        a.xor(rdx, rdx);
                        CallLabel(L_http_request);
                        a.mov(__[rbp + DispForSlot(TempSlot(hg.Target))], rax);
                        break;

                    case IrInst.HttpPost hp:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(hp.UrlTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(hp.BodyTemp))]);
                        a.mov(rdx, 1);
                        CallLabel(L_http_request);
                        a.mov(__[rbp + DispForSlot(TempSlot(hp.Target))], rax);
                        break;

                    case IrInst.NetTcpConnect ntc:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ntc.HostTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(ntc.PortTemp))]);
                        CallLabel(L_tcp_connect);
                        a.mov(__[rbp + DispForSlot(TempSlot(ntc.Target))], rax);
                        break;

                    case IrInst.NetTcpSend nts:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(nts.SocketTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(nts.TextTemp))]);
                        CallLabel(L_tcp_send);
                        a.mov(__[rbp + DispForSlot(TempSlot(nts.Target))], rax);
                        break;

                    case IrInst.NetTcpReceive ntr:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ntr.SocketTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(ntr.MaxBytesTemp))]);
                        CallLabel(L_tcp_receive);
                        a.mov(__[rbp + DispForSlot(TempSlot(ntr.Target))], rax);
                        break;

                    case IrInst.NetTcpClose ntcl:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ntcl.SocketTemp))]);
                        CallLabel(L_tcp_close);
                        a.mov(__[rbp + DispForSlot(TempSlot(ntcl.Target))], rax);
                        break;

                    case IrInst.PanicStr err:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(err.Source))]);
                        CallLabel(L_panic_str);
                        break;

                    case IrInst.JumpIfFalse jf:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(jf.CondTemp))]);
                        a.cmp(rax, 0);
                        a.je(GetLabel(jf.Target));
                        break;

                    case IrInst.Jump j:
                        a.jmp(GetLabel(j.Target));
                        break;

                    case IrInst.Return r:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(r.Source))]);
                        a.leave();
                        a.ret();
                        break;

                    case IrInst.LoadLocal ll:
                        a.mov(rax, __[rbp + DispForSlot(ll.Slot)]);
                        a.mov(__[rbp + DispForSlot(TempSlot(ll.Target))], rax);
                        break;

                    case IrInst.StoreLocal sl:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(sl.Source))]);
                        a.mov(__[rbp + DispForSlot(sl.Slot)], rax);
                        break;

                    case IrInst.LoadEnv le:
                        a.mov(rbx, __[rbp - 8]);
                        a.mov(rax, __[rbx + le.Index * 8]);
                        a.mov(__[rbp + DispForSlot(TempSlot(le.Target))], rax);
                        break;

                    case IrInst.Alloc al:
                        a.mov(rdi, al.SizeBytes);
                        CallLabel(L_alloc);
                        a.mov(__[rbp + DispForSlot(TempSlot(al.Target))], rax);
                        break;

                    case IrInst.AllocAdt aa:
                        // Allocate (1 + FieldCount) * 8 bytes; layout: [tag:i64, field0, ..., fieldN]
                        a.mov(rdi, (1 + aa.FieldCount) * 8);
                        CallLabel(L_alloc);
                        a.mov(__[rbp + DispForSlot(TempSlot(aa.Target))], rax);
                        // Store tag at offset 0
                        a.mov(rbx, rax);
                        a.mov(rax, (long)aa.Tag);
                        a.mov(__[rbx + 0], rax);
                        break;

                    case IrInst.SetAdtField sf:
                        // *(Ptr + 8 + FieldIndex*8) = Source
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(sf.Ptr))]);
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(sf.Source))]);
                        a.mov(__[rbx + 8 + sf.FieldIndex * 8], rax);
                        break;

                    case IrInst.GetAdtTag gt:
                        // Target = *(Ptr + 0)
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(gt.Ptr))]);
                        a.mov(rax, __[rbx + 0]);
                        a.mov(__[rbp + DispForSlot(TempSlot(gt.Target))], rax);
                        break;

                    case IrInst.GetAdtField gf:
                        // Target = *(Ptr + 8 + FieldIndex*8)
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(gf.Ptr))]);
                        a.mov(rax, __[rbx + 8 + gf.FieldIndex * 8]);
                        a.mov(__[rbp + DispForSlot(TempSlot(gf.Target))], rax);
                        break;

                    case IrInst.StoreMemOffset sm:
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(sm.BasePtr))]);
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(sm.Source))]);
                        a.mov(__[rbx + sm.OffsetBytes], rax);
                        break;

                    case IrInst.LoadMemOffset lm:
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(lm.BasePtr))]);
                        a.mov(rax, __[rbx + lm.OffsetBytes]);
                        a.mov(__[rbp + DispForSlot(TempSlot(lm.Target))], rax);
                        break;

                    default:
                        throw new NotSupportedException($"Unsupported IR inst: {inst.GetType().Name}");
                }
            }

            // Fallback return 0 if IR didn't end with Return (shouldn't happen)
            a.xor(rax, rax);
            if (frameSize > 0)
            {
                a.add(rsp, frameSize);
            }

            a.pop(rbp);
            a.ret();
        }
    }

}
