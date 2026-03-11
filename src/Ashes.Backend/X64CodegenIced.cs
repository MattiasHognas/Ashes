using System.Buffers.Binary;
using System.Text;
using Ashes.Semantics;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Ashes.Backend;

/// <summary>
/// ELF backend using Iced.Intel to encode x86-64 instructions.
/// Produces a Linux x86-64 ELF64 executable directly (no nasm/ld).
///
/// Calling conventions (internal):
/// - Entry point: _start (we emit it and then call _start_main)
/// - Lambda functions: take (envPtr in RDI, arg in RSI), return in RAX.
/// - Closures are heap-allocated objects: [0]=code_ptr (u64), [8]=env_ptr (u64)
/// - Env blocks are heap-allocated arrays of u64 slots.
/// - All values are 64-bit in stack slots (ints, bools, pointers).
/// </summary>
public sealed class X64CodegenIced
{
    private const ulong BaseVA = 0x400000;
    private const int Page = 0x1000;

    private const int HeapSize = 1024 * 1024 * 4;
    private const int IntBufSize = 32;
    private const int InputBufSize = 64 * 1024;

    public byte[] CompileToElf(IrProgram p)
    {
        var data = new DataBuffer();

        // String literals (incl true/false for bool printing)
        EnsureStringLiteral(data, "__rt_true", "true");
        string trueLabel = "__rt_true";
        EnsureStringLiteral(data, "__rt_false", "false");
        string falseLabel = "__rt_false";
        EnsureStringLiteral(data, "__rt_readline_too_long", "readLine() exceeded max line length");
        EnsureStringLiteral(data, "__rt_fs_read_failed", "Ashes.Fs.readText() failed");
        EnsureStringLiteral(data, "__rt_fs_write_failed", "Ashes.Fs.writeText() failed");
        EnsureStringLiteral(data, "__rt_fs_invalid_utf8", "Ashes.Fs.readText() encountered invalid UTF-8");
        EnsureStringLiteral(data, "__rt_tcp_connect_failed", "Ashes.Net.Tcp.connect() failed");
        EnsureStringLiteral(data, "__rt_tcp_send_failed", "Ashes.Net.Tcp.send() failed");
        EnsureStringLiteral(data, "__rt_tcp_receive_failed", "Ashes.Net.Tcp.receive() failed");
        EnsureStringLiteral(data, "__rt_tcp_close_failed", "Ashes.Net.Tcp.close() failed");
        EnsureStringLiteral(data, "__rt_tcp_invalid_utf8", "Ashes.Net.Tcp.receive() encountered invalid UTF-8");
        EnsureStringLiteral(data, "__rt_tcp_invalid_max_bytes", "Ashes.Net.Tcp.receive() maxBytes must be positive");
        EnsureStringLiteral(data, "__rt_tcp_invalid_host", "Ashes.Net.Tcp.connect() requires an IPv4 address literal");
        EnsureStringLiteral(data, "__rt_tcp_resolve_failed", "Ashes.Net.Tcp.connect() could not resolve host");
        EnsureStringLiteral(data, "__rt_hosts_path", "/etc/hosts");

        foreach (var s in p.StringLiterals)
        {
            // already interned earlier; but ensure in data
            EnsureStringLiteral(data, s.Label, s.Value);
        }

        // newline
        data.Mark("nl");
        data.EmitByte(0x0A);

        // heap_ptr u64
        data.Align(8);
        data.Mark("heap_ptr");
        data.EmitUInt64(0);

        data.Mark("program_args");
        data.EmitUInt64(0);

        int dataFileSize = data.Length;

        // BSS layout in data segment VA space
        int bssStart = dataFileSize;
        int heapOff = Align(bssStart, 16);
        int intBufOff = Align(heapOff + HeapSize, 8);
        int inputBufOff = Align(intBufOff + IntBufSize, 8);
        int inputByteOff = inputBufOff + InputBufSize;
        int bssEnd = inputByteOff + 1;
        int bssSize = bssEnd - dataFileSize;

        int textFileOff = Page;
        int dataFileOff = 2 * Page;
        byte[] textBytes = Array.Empty<byte>();

        for (int iter = 0; iter < 4; iter++)
        {
            ulong textVA = BaseVA + (ulong)textFileOff;
            ulong dataVA = BaseVA + (ulong)dataFileOff;

            ulong AddrOf(string name)
            {
                if (data.TryGetOffset(name, out var off))
                {
                    return dataVA + (ulong)off;
                }

                return name switch
                {
                    "heap" => dataVA + (ulong)heapOff,
                    "buf_i64" => dataVA + (ulong)intBufOff,
                    "stdin_buf" => dataVA + (ulong)inputBufOff,
                    "stdin_byte" => dataVA + (ulong)inputByteOff,
                    _ => throw new InvalidOperationException($"Unknown data/bss symbol: {name}")
                };
            }

            textBytes = BuildText(p, textVA, AddrOf, trueLabel, falseLabel);

            int newDataFileOff = Align(textFileOff + textBytes.Length, Page);
            if (newDataFileOff == dataFileOff)
            {
                break;
            }

            dataFileOff = newDataFileOff;
        }

        ulong finalTextVA = BaseVA + (ulong)textFileOff;
        ulong finalDataVA = BaseVA + (ulong)dataFileOff;

        ulong FinalAddrOf(string name)
        {
            if (data.TryGetOffset(name, out var off))
            {
                return finalDataVA + (ulong)off;
            }

            return name switch
            {
                "heap" => finalDataVA + (ulong)heapOff,
                "buf_i64" => finalDataVA + (ulong)intBufOff,
                "stdin_buf" => finalDataVA + (ulong)inputBufOff,
                "stdin_byte" => finalDataVA + (ulong)inputByteOff,
                _ => throw new InvalidOperationException($"Unknown data/bss symbol: {name}")
            };
        }

        textBytes = BuildText(p, finalTextVA, FinalAddrOf, trueLabel, falseLabel);

        var dataBytes = data.ToArray();

        return Elf64ImageWriter.BuildTwoSegmentElf(
            textBytes: textBytes,
            dataBytes: dataBytes,
            bssSize: bssSize,
            entryOffsetInText: 0,
            textFileOff: textFileOff,
            dataFileOff: dataFileOff,
            textVA: finalTextVA,
            dataVA: finalDataVA
        );
    }

    private static byte[] BuildText(IrProgram p, ulong textVA, Func<string, ulong> addrOf, string trueLabel, string falseLabel)
    {
        var asm = new Assembler(64);

        // Labels
        var L_start = asm.CreateLabel(); // real ELF entry
        var L_start_main = asm.CreateLabel();

        var L_init_heap = asm.CreateLabel();
        var L_alloc = asm.CreateLabel();
        var L_make_unit = asm.CreateLabel();
        var L_make_result_ok = asm.CreateLabel();
        var L_make_result_error = asm.CreateLabel();
        var L_write_str = asm.CreateLabel();
        var L_print_str = asm.CreateLabel();
        var L_read_line = asm.CreateLabel();
        var L_string_to_cstr = asm.CreateLabel();
        var L_parse_ipv4 = asm.CreateLabel();
        var L_cstr_eq_token = asm.CreateLabel();
        var L_resolve_host_ipv4 = asm.CreateLabel();
        var L_validate_utf8 = asm.CreateLabel();
        var L_fs_read_text = asm.CreateLabel();
        var L_fs_write_text = asm.CreateLabel();
        var L_fs_exists = asm.CreateLabel();
        var L_tcp_connect = asm.CreateLabel();
        var L_tcp_send = asm.CreateLabel();
        var L_tcp_receive = asm.CreateLabel();
        var L_tcp_close = asm.CreateLabel();
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

        // _start: call _start_main, then exit (in case)
        asm.Label(ref L_start);
        asm.mov(rbx, (long)addrOf("program_args"));
        asm.xor(rax, rax);
        asm.mov(__[rbx], rax);
        asm.mov(rcx, __[rsp]); // argc
        var L_no_args = asm.CreateLabel();
        var L_build_args = asm.CreateLabel();
        var L_args_len_done = asm.CreateLabel();
        var L_args_loop = asm.CreateLabel();
        var L_args_done = asm.CreateLabel();
        asm.cmp(rcx, 1);
        asm.jle(L_no_args);
        asm.call(L_init_heap);
        asm.xor(r12, r12); // list = []
        asm.mov(r13, rcx);
        asm.dec(r13); // start at argc - 1
        asm.lea(r14, __[rsp + 8]); // argv base
        asm.Label(ref L_build_args);
        asm.cmp(r13, 0); // skip argv[0]
        asm.je(L_args_done);
        asm.mov(r15, __[r14 + r13 * 8]); // c string
        asm.xor(r9, r9); // len
        asm.Label(ref L_args_len_done);
        asm.mov(al, __byte_ptr[r15 + r9]);
        asm.cmp(al, 0);
        asm.je(L_args_loop);
        asm.inc(r9);
        asm.jmp(L_args_len_done);
        asm.Label(ref L_args_loop);
        asm.mov(rdi, r9);
        asm.add(rdi, 8); // string object bytes
        asm.call(L_alloc);
        asm.mov(__[rax], r9);
        asm.lea(rdi, __[rax + 8]);
        asm.mov(rsi, r15);
        asm.mov(rcx, r9);
        asm.rep.movsb();
        asm.mov(r10, rax); // string ptr
        asm.mov(rdi, 16); // cons cell bytes
        asm.call(L_alloc);
        asm.mov(__[rax + 0], r10);
        asm.mov(__[rax + 8], r12);
        asm.mov(r12, rax);
        asm.dec(r13);
        asm.jmp(L_build_args);
        asm.Label(ref L_args_done);
        asm.mov(rbx, (long)addrOf("program_args"));
        asm.mov(__[rbx], r12);
        asm.Label(ref L_no_args);
        asm.call(L_start_main);
        asm.mov(rdi, 0);
        asm.mov(rax, 60);
        asm.syscall();

        // Emit entry function (_start_main)
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

        // init_heap: [heap_ptr] = &heap
        asm.Label(ref L_init_heap);
        asm.mov(rax, (long)addrOf("heap"));
        asm.mov(rbx, (long)addrOf("heap_ptr"));
        asm.mov(__[rbx], rax);
        asm.ret();

        // alloc(size in RDI) -> RAX
        asm.Label(ref L_alloc);
        // rbx = &heap_ptr
        asm.mov(rbx, (long)addrOf("heap_ptr"));
        // rax = [heap_ptr]
        asm.mov(rax, __[rbx]);
        // r11 = rax + rdi
        asm.mov(r11, rax);
        asm.add(r11, rdi);
        // [heap_ptr] = r11
        asm.mov(__[rbx], r11);
        asm.ret();

        // make_unit() -> RAX = Unit value
        asm.Label(ref L_make_unit);
        asm.mov(rdi, 8);
        asm.call(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        asm.ret();

        // make_result_ok(RDI=payload) -> RAX = Result(Str, T)
        asm.Label(ref L_make_result_ok);
        asm.mov(r8, rdi);
        asm.mov(rdi, 16);
        asm.call(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        asm.mov(__[rax + 8], r8);
        asm.ret();

        // make_result_error(RDI=error string ptr) -> RAX = Result(Str, T)
        asm.Label(ref L_make_result_error);
        asm.mov(r8, rdi);
        asm.mov(rdi, 16);
        asm.call(L_alloc);
        asm.mov(rbx, 1);
        asm.mov(__[rax], rbx);
        asm.mov(__[rax + 8], r8);
        asm.ret();

        // write_str(RDI=string*)
        asm.Label(ref L_write_str);
        asm.mov(rdx, __[rdi]);         // len
        asm.lea(rsi, __[rdi + 8]);     // bytes
        asm.mov(rax, 1);              // SYS_write
        asm.mov(rdi, 1);              // stdout
        asm.syscall();
        asm.ret();

        // print_str(RDI=string*)
        asm.Label(ref L_print_str);
        asm.call(L_write_str);
        // newline
        asm.mov(rax, 1);
        asm.mov(rdi, 1);
        asm.mov(rsi, (long)addrOf("nl"));
        asm.mov(rdx, 1);
        asm.syscall();
        asm.ret();

        // read_line() -> RAX = OptionString
        asm.Label(ref L_read_line);
        var L_read_line_heap_ok = asm.CreateLabel();
        var L_read_line_loop = asm.CreateLabel();
        var L_read_line_eof = asm.CreateLabel();
        var L_read_line_finish_some = asm.CreateLabel();
        var L_read_line_make_some = asm.CreateLabel();
        var L_read_line_return_none = asm.CreateLabel();
        var L_read_line_overflow = asm.CreateLabel();

        asm.mov(rbx, (long)addrOf("heap_ptr"));
        asm.mov(rax, __[rbx]);
        asm.cmp(rax, 0);
        asm.jne(L_read_line_heap_ok);
        asm.call(L_init_heap);
        asm.Label(ref L_read_line_heap_ok);
        asm.mov(r12, (long)addrOf("stdin_buf"));
        asm.xor(r13, r13);

        asm.Label(ref L_read_line_loop);
        asm.mov(rax, 0);
        asm.mov(rdi, 0);
        asm.mov(rsi, (long)addrOf("stdin_byte"));
        asm.mov(rdx, 1);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.je(L_read_line_eof);
        asm.mov(r10, (long)addrOf("stdin_byte"));
        asm.movzx(eax, __byte_ptr[r10]);
        asm.cmp(al, 10);
        asm.je(L_read_line_finish_some);
        asm.cmp(al, 13);
        asm.je(L_read_line_loop);
        asm.cmp(r13, InputBufSize);
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
        asm.call(L_alloc);
        asm.mov(__[rax], r13);
        asm.mov(r14, rax);
        asm.lea(rdi, __[rax + 8]);
        asm.mov(rsi, r12);
        asm.mov(rcx, r13);
        asm.rep.movsb();

        asm.Label(ref L_read_line_make_some);
        asm.mov(rdi, 16);
        asm.call(L_alloc);
        asm.mov(rbx, 1);
        asm.mov(__[rax], rbx);
        asm.mov(__[rax + 8], r14);
        asm.ret();

        asm.Label(ref L_read_line_return_none);
        asm.mov(rdi, 8);
        asm.call(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        asm.ret();

        asm.Label(ref L_read_line_overflow);
        asm.mov(rdi, (long)addrOf("__rt_readline_too_long"));
        asm.call(L_panic_str);

        // string_to_cstr(RDI=string*) -> RAX = heap-allocated null-terminated bytes
        asm.Label(ref L_string_to_cstr);
        var L_string_to_cstr_heap_ok = asm.CreateLabel();
        asm.mov(rbx, (long)addrOf("heap_ptr"));
        asm.mov(rax, __[rbx]);
        asm.cmp(rax, 0);
        asm.jne(L_string_to_cstr_heap_ok);
        asm.call(L_init_heap);
        asm.Label(ref L_string_to_cstr_heap_ok);
        asm.mov(r8, __[rdi]);
        asm.mov(r9, rdi);
        asm.mov(rdi, r8);
        asm.add(rdi, 1);
        asm.call(L_alloc);
        asm.mov(r10, rax);
        asm.mov(rdi, rax);
        asm.lea(rsi, __[r9 + 8]);
        asm.mov(rcx, r8);
        asm.rep.movsb();
        asm.mov(__byte_ptr[r10 + r8], 0);
        asm.mov(rax, r10);
        asm.ret();

        // parse_ipv4(RDI=cstr) -> EAX=1 on success, EDX=address in sockaddr-compatible byte order
        asm.Label(ref L_parse_ipv4);
        var L_parse_ipv4_octet = asm.CreateLabel();
        var L_parse_ipv4_expect_separator = asm.CreateLabel();
        var L_parse_ipv4_done = asm.CreateLabel();
        var L_parse_ipv4_fail = asm.CreateLabel();
        asm.mov(rsi, rdi);
        asm.xor(r8d, r8d);
        asm.xor(r10d, r10d);
        asm.Label(ref L_parse_ipv4_octet);
        asm.cmp(r10d, 4);
        asm.je(L_parse_ipv4_fail);
        asm.xor(r9d, r9d);
        asm.xor(r11d, r11d);
        var L_parse_ipv4_digit_loop = asm.CreateLabel();
        var L_parse_ipv4_after_digits = asm.CreateLabel();
        asm.Label(ref L_parse_ipv4_digit_loop);
        asm.movzx(eax, __byte_ptr[rsi]);
        asm.cmp(al, 48);
        asm.jb(L_parse_ipv4_after_digits);
        asm.cmp(al, 57);
        asm.ja(L_parse_ipv4_after_digits);
        asm.imul(r9d, r9d, 10);
        asm.sub(eax, 48);
        asm.add(r9d, eax);
        asm.cmp(r9d, 255);
        asm.jg(L_parse_ipv4_fail);
        asm.inc(r11d);
        asm.inc(rsi);
        asm.jmp(L_parse_ipv4_digit_loop);
        asm.Label(ref L_parse_ipv4_after_digits);
        asm.cmp(r11d, 0);
        asm.je(L_parse_ipv4_fail);
        asm.shl(r8d, 8);
        asm.or(r8d, r9d);
        asm.inc(r10d);
        asm.cmp(r10d, 4);
        asm.je(L_parse_ipv4_done);
        asm.Label(ref L_parse_ipv4_expect_separator);
        asm.movzx(eax, __byte_ptr[rsi]);
        asm.cmp(al, 46);
        asm.jne(L_parse_ipv4_fail);
        asm.inc(rsi);
        asm.jmp(L_parse_ipv4_octet);
        asm.Label(ref L_parse_ipv4_done);
        asm.movzx(eax, __byte_ptr[rsi]);
        asm.cmp(al, 0);
        asm.jne(L_parse_ipv4_fail);
        asm.mov(edx, r8d);
        asm.bswap(edx);
        asm.mov(eax, 1);
        asm.ret();
        asm.Label(ref L_parse_ipv4_fail);
        asm.xor(eax, eax);
        asm.xor(edx, edx);
        asm.ret();

        // cstr_eq_token(RDI=cstr, RSI=tokenStart) -> EAX=1 if equal and token ends at delimiter
        asm.Label(ref L_cstr_eq_token);
        var L_cstr_eq_token_done = asm.CreateLabel();
        var L_cstr_eq_token_success = asm.CreateLabel();
        var L_cstr_eq_token_fail = asm.CreateLabel();
        asm.movzx(eax, __byte_ptr[rdi]);
        asm.movzx(edx, __byte_ptr[rsi]);
        asm.cmp(al, 0);
        asm.je(L_cstr_eq_token_done);
        asm.cmp(al, dl);
        asm.jne(L_cstr_eq_token_fail);
        asm.inc(rdi);
        asm.inc(rsi);
        asm.jmp(L_cstr_eq_token);
        asm.Label(ref L_cstr_eq_token_done);
        asm.cmp(dl, 0);
        asm.je(L_cstr_eq_token_success);
        asm.cmp(dl, 9);
        asm.je(L_cstr_eq_token_success);
        asm.cmp(dl, 10);
        asm.je(L_cstr_eq_token_success);
        asm.cmp(dl, 13);
        asm.je(L_cstr_eq_token_success);
        asm.cmp(dl, 32);
        asm.je(L_cstr_eq_token_success);
        asm.cmp(dl, 35);
        asm.jne(L_cstr_eq_token_fail);
        asm.Label(ref L_cstr_eq_token_success);
        asm.mov(eax, 1);
        asm.ret();
        asm.Label(ref L_cstr_eq_token_fail);
        asm.xor(eax, eax);
        asm.ret();

        // resolve_host_ipv4(RDI=host string*) -> EAX=1 on success, EDX=address in sockaddr-compatible byte order
        asm.Label(ref L_resolve_host_ipv4);
        var L_resolve_host_skip_ws = asm.CreateLabel();
        var L_resolve_host_skip_ws_done = asm.CreateLabel();
        var L_resolve_host_skip_name = asm.CreateLabel();
        var L_resolve_host_skip_name_done = asm.CreateLabel();
        var L_resolve_host_skip_line = asm.CreateLabel();
        var L_resolve_host_skip_line_done = asm.CreateLabel();
        var L_resolve_host_parse_loop = asm.CreateLabel();
        var L_resolve_host_scan_ip = asm.CreateLabel();
        var L_resolve_host_after_ip = asm.CreateLabel();
        var L_resolve_host_match = asm.CreateLabel();
        var L_resolve_host_read_fail = asm.CreateLabel();
        var L_resolve_host_success = asm.CreateLabel();
        var L_resolve_host_fail = asm.CreateLabel();
        asm.call(L_string_to_cstr);
        asm.mov(r12, rax);
        asm.mov(rdi, r12);
        asm.call(L_parse_ipv4);
        asm.cmp(eax, 0);
        asm.jne(L_resolve_host_success);

        asm.mov(rdi, (long)addrOf("__rt_hosts_path"));
        asm.call(L_string_to_cstr);
        asm.mov(rdi, rax);
        asm.mov(rax, 2);
        asm.xor(rsi, rsi);
        asm.xor(rdx, rdx);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_resolve_host_fail);
        asm.mov(r13, rax);
        asm.mov(rax, 0);
        asm.mov(rdi, r13);
        asm.mov(r14, (long)addrOf("stdin_buf"));
        asm.mov(rsi, r14);
        asm.mov(rdx, InputBufSize - 1);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jle(L_resolve_host_read_fail);
        asm.mov(__byte_ptr[r14 + rax], 0);
        asm.mov(rax, 3);
        asm.mov(rdi, r13);
        asm.syscall();
        asm.mov(r15, r14);

        asm.Label(ref L_resolve_host_parse_loop);
        asm.movzx(eax, __byte_ptr[r15]);
        asm.cmp(al, 0);
        asm.je(L_resolve_host_fail);
        asm.cmp(al, 10);
        asm.je(L_resolve_host_skip_line_done);
        asm.cmp(al, 13);
        asm.je(L_resolve_host_skip_line_done);
        asm.cmp(al, 35);
        asm.je(L_resolve_host_skip_line);
        asm.cmp(al, 32);
        asm.je(L_resolve_host_skip_ws);
        asm.cmp(al, 9);
        asm.je(L_resolve_host_skip_ws);
        asm.mov(r8, r15);
        asm.Label(ref L_resolve_host_scan_ip);
        asm.movzx(eax, __byte_ptr[r15]);
        asm.cmp(al, 0);
        asm.je(L_resolve_host_fail);
        asm.cmp(al, 32);
        asm.je(L_resolve_host_after_ip);
        asm.cmp(al, 9);
        asm.je(L_resolve_host_after_ip);
        asm.cmp(al, 10);
        asm.je(L_resolve_host_skip_line_done);
        asm.cmp(al, 13);
        asm.je(L_resolve_host_skip_line_done);
        asm.cmp(al, 35);
        asm.je(L_resolve_host_skip_line);
        asm.inc(r15);
        asm.jmp(L_resolve_host_scan_ip);

        asm.Label(ref L_resolve_host_after_ip);
        asm.mov(__byte_ptr[r15], 0);
        asm.inc(r15);
        asm.Label(ref L_resolve_host_skip_ws);
        asm.movzx(eax, __byte_ptr[r15]);
        asm.cmp(al, 32);
        asm.je(L_resolve_host_skip_ws_done);
        asm.cmp(al, 9);
        asm.je(L_resolve_host_skip_ws_done);
        asm.cmp(al, 10);
        asm.je(L_resolve_host_skip_line_done);
        asm.cmp(al, 13);
        asm.je(L_resolve_host_skip_line_done);
        asm.cmp(al, 35);
        asm.je(L_resolve_host_skip_line);
        asm.mov(rdi, r12);
        asm.mov(rsi, r15);
        asm.call(L_cstr_eq_token);
        asm.cmp(eax, 0);
        asm.jne(L_resolve_host_match);
        asm.Label(ref L_resolve_host_skip_name);
        asm.movzx(eax, __byte_ptr[r15]);
        asm.cmp(al, 0);
        asm.je(L_resolve_host_fail);
        asm.cmp(al, 32);
        asm.je(L_resolve_host_skip_name_done);
        asm.cmp(al, 9);
        asm.je(L_resolve_host_skip_name_done);
        asm.cmp(al, 10);
        asm.je(L_resolve_host_skip_line_done);
        asm.cmp(al, 13);
        asm.je(L_resolve_host_skip_line_done);
        asm.cmp(al, 35);
        asm.je(L_resolve_host_skip_line);
        asm.inc(r15);
        asm.jmp(L_resolve_host_skip_name);

        asm.Label(ref L_resolve_host_skip_name_done);
        asm.inc(r15);
        asm.jmp(L_resolve_host_skip_ws);

        asm.Label(ref L_resolve_host_match);
        asm.mov(rdi, r8);
        asm.call(L_parse_ipv4);
        asm.cmp(eax, 0);
        asm.jne(L_resolve_host_success);
        asm.jmp(L_resolve_host_fail);

        asm.Label(ref L_resolve_host_skip_ws_done);
        asm.inc(r15);
        asm.jmp(L_resolve_host_skip_ws);

        asm.Label(ref L_resolve_host_skip_line);
        asm.movzx(eax, __byte_ptr[r15]);
        asm.cmp(al, 0);
        asm.je(L_resolve_host_fail);
        asm.cmp(al, 10);
        asm.je(L_resolve_host_skip_line_done);
        asm.inc(r15);
        asm.jmp(L_resolve_host_skip_line);

        asm.Label(ref L_resolve_host_skip_line_done);
        asm.movzx(eax, __byte_ptr[r15]);
        asm.cmp(al, 10);
        asm.jne(L_resolve_host_fail);
        asm.inc(r15);
        asm.jmp(L_resolve_host_parse_loop);

        asm.Label(ref L_resolve_host_read_fail);
        asm.mov(rax, 3);
        asm.mov(rdi, r13);
        asm.syscall();
        asm.jmp(L_resolve_host_fail);

        asm.Label(ref L_resolve_host_success);
        asm.mov(eax, 1);
        asm.ret();

        asm.Label(ref L_resolve_host_fail);
        asm.xor(eax, eax);
        asm.xor(edx, edx);
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
        var L_fs_read_empty = asm.CreateLabel();
        var L_fs_read_loop = asm.CreateLabel();
        var L_fs_read_after = asm.CreateLabel();
        var L_fs_read_fail = asm.CreateLabel();
        var L_fs_read_invalid_utf8 = asm.CreateLabel();

        asm.mov(r15, rdi);
        asm.call(L_string_to_cstr);
        asm.mov(rdi, rax);
        asm.mov(rax, 2);
        asm.xor(rsi, rsi);
        asm.xor(rdx, rdx);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_fs_read_fail);
        asm.mov(r12, rax);

        asm.mov(rdi, r12);
        asm.xor(rsi, rsi);
        asm.mov(rdx, 2);
        asm.mov(rax, 8);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_fs_read_fail);
        asm.mov(r13, rax);

        asm.mov(rdi, r12);
        asm.xor(rsi, rsi);
        asm.xor(rdx, rdx);
        asm.mov(rax, 8);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_fs_read_fail);

        asm.mov(rdi, r13);
        asm.add(rdi, 8);
        asm.call(L_alloc);
        asm.mov(__[rax], r13);
        asm.mov(r14, rax);
        asm.cmp(r13, 0);
        asm.je(L_fs_read_empty);

        asm.lea(rbx, __[r14 + 8]);
        asm.mov(r10, r13);
        asm.Label(ref L_fs_read_loop);
        asm.cmp(r10, 0);
        asm.je(L_fs_read_after);
        asm.mov(rdi, r12);
        asm.mov(rsi, rbx);
        asm.mov(rdx, r10);
        asm.mov(rax, 0);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jle(L_fs_read_fail);
        asm.sub(r10, rax);
        asm.add(rbx, rax);
        asm.jmp(L_fs_read_loop);

        asm.Label(ref L_fs_read_after);
        asm.lea(rdi, __[r14 + 8]);
        asm.mov(rsi, r13);
        asm.call(L_validate_utf8);
        asm.cmp(rax, 0);
        asm.je(L_fs_read_invalid_utf8);

        asm.Label(ref L_fs_read_empty);
        asm.mov(rdi, r12);
        asm.mov(rax, 3);
        asm.syscall();
        asm.mov(rdi, r14);
        asm.call(L_make_result_ok);
        asm.ret();

        asm.Label(ref L_fs_read_invalid_utf8);
        asm.mov(rdi, r12);
        asm.mov(rax, 3);
        asm.syscall();
        asm.mov(rdi, (long)addrOf("__rt_fs_invalid_utf8"));
        asm.call(L_make_result_error);
        asm.ret();

        asm.Label(ref L_fs_read_fail);
        asm.mov(rdi, (long)addrOf("__rt_fs_read_failed"));
        asm.call(L_make_result_error);
        asm.ret();

        // fs_write_text(RDI=path string*, RSI=text string*) -> RAX=Result(Str, Unit)
        asm.Label(ref L_fs_write_text);
        var L_fs_write_loop = asm.CreateLabel();
        var L_fs_write_done = asm.CreateLabel();
        var L_fs_write_fail = asm.CreateLabel();

        asm.mov(r15, rsi);
        asm.call(L_string_to_cstr);
        asm.mov(rdi, rax);
        asm.mov(rax, 2);
        asm.mov(rsi, 0x241);
        asm.mov(rdx, 420);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_fs_write_fail);
        asm.mov(r12, rax);
        asm.lea(r13, __[r15 + 8]);
        asm.mov(r14, __[r15]);

        asm.Label(ref L_fs_write_loop);
        asm.cmp(r14, 0);
        asm.je(L_fs_write_done);
        asm.mov(rdi, r12);
        asm.mov(rsi, r13);
        asm.mov(rdx, r14);
        asm.mov(rax, 1);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jle(L_fs_write_fail);
        asm.sub(r14, rax);
        asm.add(r13, rax);
        asm.jmp(L_fs_write_loop);

        asm.Label(ref L_fs_write_done);
        asm.mov(rdi, r12);
        asm.mov(rax, 3);
        asm.syscall();
        asm.call(L_make_unit);
        asm.mov(rdi, rax);
        asm.call(L_make_result_ok);
        asm.ret();

        asm.Label(ref L_fs_write_fail);
        asm.mov(rdi, (long)addrOf("__rt_fs_write_failed"));
        asm.call(L_make_result_error);
        asm.ret();

        // fs_exists(RDI=path string*) -> RAX=Result(Str, Bool)
        asm.Label(ref L_fs_exists);
        var L_fs_exists_false = asm.CreateLabel();
        asm.call(L_string_to_cstr);
        asm.mov(rdi, rax);
        asm.mov(rax, 2);
        asm.xor(rsi, rsi);
        asm.xor(rdx, rdx);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_fs_exists_false);
        asm.mov(rdi, rax);
        asm.mov(rax, 3);
        asm.syscall();
        asm.mov(rdi, 1);
        asm.call(L_make_result_ok);
        asm.ret();

        asm.Label(ref L_fs_exists_false);
        asm.xor(rdi, rdi);
        asm.call(L_make_result_ok);
        asm.ret();

        // tcp_connect(RDI=host string*, RSI=port int) -> RAX=Result(Str, Socket)
        asm.Label(ref L_tcp_connect);
        var L_tcp_connect_fail = asm.CreateLabel();
        var L_tcp_connect_fail_close = asm.CreateLabel();
        var L_tcp_connect_resolve_fail = asm.CreateLabel();
        asm.mov(r12, rsi);
        asm.cmp(r12, 0);
        asm.jle(L_tcp_connect_fail);
        asm.cmp(r12, 65535);
        asm.jg(L_tcp_connect_fail);
        asm.sub(rsp, 0x10);
        asm.mov(__[rsp], r12);
        asm.call(L_resolve_host_ipv4);
        asm.mov(r12, __[rsp]);
        asm.add(rsp, 0x10);
        asm.cmp(eax, 0);
        asm.je(L_tcp_connect_resolve_fail);
        asm.mov(r13d, edx);
        asm.mov(rax, 41);
        asm.mov(rdi, 2);
        asm.mov(rsi, 1);
        asm.xor(rdx, rdx);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_tcp_connect_fail);
        asm.mov(r15, rax);
        asm.sub(rsp, 0x20);
        asm.mov(__word_ptr[rsp], 2);
        asm.mov(ax, r12w);
        asm.xchg(al, ah);
        asm.mov(__word_ptr[rsp + 2], ax);
        asm.mov(__dword_ptr[rsp + 4], r13d);
        asm.xor(rax, rax);
        asm.mov(__[rsp + 8], rax);
        asm.mov(rax, 42);
        asm.mov(rdi, r15);
        asm.mov(rsi, rsp);
        asm.mov(rdx, 16);
        asm.syscall();
        asm.add(rsp, 0x20);
        asm.cmp(rax, 0);
        asm.jl(L_tcp_connect_fail_close);
        asm.mov(rdi, r15);
        asm.call(L_make_result_ok);
        asm.ret();

        asm.Label(ref L_tcp_connect_resolve_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_resolve_failed"));
        asm.call(L_make_result_error);
        asm.ret();

        asm.Label(ref L_tcp_connect_fail_close);
        asm.mov(rax, 3);
        asm.mov(rdi, r15);
        asm.syscall();
        asm.Label(ref L_tcp_connect_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_connect_failed"));
        asm.call(L_make_result_error);
        asm.ret();

        // tcp_send(RDI=socket, RSI=text string*) -> RAX=Result(Str, Int)
        asm.Label(ref L_tcp_send);
        var L_tcp_send_loop = asm.CreateLabel();
        var L_tcp_send_done = asm.CreateLabel();
        var L_tcp_send_fail = asm.CreateLabel();
        asm.mov(r12, rdi);
        asm.mov(r13, rsi);
        asm.mov(rbx, __[r13]);
        asm.mov(r14, rbx);
        asm.lea(r15, __[r13 + 8]);
        asm.Label(ref L_tcp_send_loop);
        asm.cmp(r14, 0);
        asm.je(L_tcp_send_done);
        asm.mov(rax, 1);
        asm.mov(rdi, r12);
        asm.mov(rsi, r15);
        asm.mov(rdx, r14);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jle(L_tcp_send_fail);
        asm.sub(r14, rax);
        asm.add(r15, rax);
        asm.jmp(L_tcp_send_loop);
        asm.Label(ref L_tcp_send_done);
        asm.mov(rdi, rbx);
        asm.call(L_make_result_ok);
        asm.ret();
        asm.Label(ref L_tcp_send_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_send_failed"));
        asm.call(L_make_result_error);
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
        asm.call(L_alloc);
        asm.mov(r14, rax);
        asm.lea(r15, __[r14 + 8]);
        asm.mov(rax, 0);
        asm.mov(rdi, r12);
        asm.mov(rsi, r15);
        asm.mov(rdx, r13);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_tcp_receive_fail);
        asm.je(L_tcp_receive_empty);
        asm.mov(__[r14], rax);
        asm.mov(rdi, r15);
        asm.mov(rsi, rax);
        asm.call(L_validate_utf8);
        asm.cmp(rax, 0);
        asm.je(L_tcp_receive_invalid_utf8);
        asm.mov(rdi, r14);
        asm.call(L_make_result_ok);
        asm.ret();
        asm.Label(ref L_tcp_receive_empty);
        asm.mov(rdi, 8);
        asm.call(L_alloc);
        asm.xor(rbx, rbx);
        asm.mov(__[rax], rbx);
        asm.mov(rdi, rax);
        asm.call(L_make_result_ok);
        asm.ret();
        asm.Label(ref L_tcp_receive_invalid_max);
        asm.mov(rdi, (long)addrOf("__rt_tcp_invalid_max_bytes"));
        asm.call(L_make_result_error);
        asm.ret();
        asm.Label(ref L_tcp_receive_invalid_utf8);
        asm.mov(rdi, (long)addrOf("__rt_tcp_invalid_utf8"));
        asm.call(L_make_result_error);
        asm.ret();
        asm.Label(ref L_tcp_receive_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_receive_failed"));
        asm.call(L_make_result_error);
        asm.ret();

        // tcp_close(RDI=socket) -> RAX=Result(Str, Unit)
        asm.Label(ref L_tcp_close);
        var L_tcp_close_fail = asm.CreateLabel();
        asm.mov(rax, 3);
        asm.syscall();
        asm.cmp(rax, 0);
        asm.jl(L_tcp_close_fail);
        asm.call(L_make_unit);
        asm.mov(rdi, rax);
        asm.call(L_make_result_ok);
        asm.ret();
        asm.Label(ref L_tcp_close_fail);
        asm.mov(rdi, (long)addrOf("__rt_tcp_close_failed"));
        asm.call(L_make_result_error);
        asm.ret();

        // panic_str(RDI=string*) -> prints message and exits with code 1
        asm.Label(ref L_panic_str);
        asm.call(L_print_str);
        asm.mov(rdi, 1);
        asm.mov(rax, 60);
        asm.syscall();

        // print_bool(RDI=bool64)
        asm.Label(ref L_print_bool);
        // if rdi == 0 -> false
        var L_pb_false = asm.CreateLabel();
        var L_pb_end = asm.CreateLabel();

        asm.cmp(rdi, 0);
        asm.je(L_pb_false);

        asm.mov(rdi, (long)addrOf(trueLabel));
        asm.call(L_print_str);
        asm.jmp(L_pb_end);

        asm.Label(ref L_pb_false);
        asm.mov(rdi, (long)addrOf(falseLabel));
        asm.call(L_print_str);

        asm.Label(ref L_pb_end);
        asm.ret();

        // concat_str(RDI=a, RSI=b) -> RAX
        asm.Label(ref L_concat);

        // Ensure heap initialized (cheap): if [heap_ptr]==0 init
        var L_heap_ok = asm.CreateLabel();
        asm.mov(rbx, (long)addrOf("heap_ptr"));
        asm.mov(rax, __[rbx]);
        asm.cmp(rax, 0);
        asm.jne(L_heap_ok);
        asm.call(L_init_heap);
        asm.mov(rax, __[rbx]);
        asm.Label(ref L_heap_ok);

        asm.mov(r12, rdi);
        asm.mov(r13, rsi);

        asm.mov(r8, __[r12]); // lenA
        asm.mov(r9, __[r13]); // lenB

        asm.mov(r10, r8);
        asm.add(r10, r9);
        asm.add(r10, 8); // total bytes

        // dest = [heap_ptr] already in rax
        // newHeap = dest + total
        asm.mov(r11, rax);
        asm.add(r11, r10);
        asm.mov(__[rbx], r11);

        // [dest] = lenA+lenB
        asm.mov(rcx, r8);
        asm.add(rcx, r9);
        asm.mov(__[rax], rcx);

        // dst = dest+8
        asm.lea(rdi, __[rax + 8]);

        // copy A
        asm.lea(rsi, __[r12 + 8]);
        asm.mov(rcx, r8);
        asm.rep.movsb();

        // copy B
        asm.lea(rsi, __[r13 + 8]);
        asm.mov(rcx, r9);
        asm.rep.movsb();

        asm.ret();

        // print_int(RDI=int64)
        asm.Label(ref L_print_int);

        // RSI = &buf_i64
        asm.mov(rsi, (long)addrOf("buf_i64"));
        asm.call(L_itoa);

        // write(1, RSI, RDX)
        asm.mov(rax, 1);
        asm.mov(rdi, 1);
        asm.syscall();

        // newline
        asm.mov(rax, 1);
        asm.mov(rdi, 1);
        asm.mov(rsi, (long)addrOf("nl"));
        asm.mov(rdx, 1);
        asm.syscall();
        asm.ret();

        // itoa_i64
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

        // Assemble at desired RIP
        using var ms = new MemoryStream();
        var writer = new StreamCodeWriter(ms);
        asm.Assemble(writer, textVA);
        return ms.ToArray();

        // local function emitting IR function bodies
        void EmitFunctionBody(Assembler a, IrFunction fn, Func<string, ulong> addr, Dictionary<string, Label> fLabels, string tLbl, string fLbl, bool hasParams)
        {
            // Standard prologue
            a.push(rbp);
            a.mov(rbp, rsp);

            int slotCount = fn.LocalCount + fn.TempCount;
            int frameSize = Align16(slotCount * 8);
            if (frameSize > 0)
            {
                a.sub(rsp, frameSize);
            }

            // If lambda: store env and arg in locals 0 and 1
            if (hasParams)
            {
                // slot0 = env (rdi), slot1 = arg (rsi)
                a.mov(__[rbp - 8], rdi);
                a.mov(__[rbp - 16], rsi);
            }

            // Labels for IR control flow
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

            // Ensure heap init if needed for alloc/closures/concat
            bool needsHeap = p.UsesConcatStr || p.UsesClosures || fn.Instructions.Any(i => i is IrInst.Alloc or IrInst.AllocAdt);
            if (needsHeap)
            {
                // if [heap_ptr]==0 init_heap
                var ok = a.CreateLabel();
                a.mov(rbx, (long)addr("heap_ptr"));
                a.mov(rax, __[rbx]);
                a.cmp(rax, 0);
                a.jne(ok);
                a.call(L_init_heap);
                a.Label(ref ok);
            }

            // First pass: define labels where they appear (Iced lets forward refs, but Label must be bound at some point)
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

                    case IrInst.Jump j:
                        a.jmp(GetLabel(j.Target));
                        break;

                    case IrInst.JumpIfFalse jf:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(jf.CondTemp))]);
                        a.cmp(rax, 0);
                        a.je(GetLabel(jf.Target));
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

                    case IrInst.LoadLocal ll:
                        a.mov(rax, __[rbp + DispForSlot(ll.Slot)]);
                        a.mov(__[rbp + DispForSlot(TempSlot(ll.Target))], rax);
                        break;

                    case IrInst.StoreLocal sl:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(sl.Source))]);
                        a.mov(__[rbp + DispForSlot(sl.Slot)], rax);
                        break;

                    case IrInst.LoadEnv le:
                        // env ptr is local slot 0 => [rbp-8]
                        a.mov(rbx, __[rbp - 8]);           // env ptr
                        a.mov(rax, __[rbx + le.Index * 8]); // load slot
                        a.mov(__[rbp + DispForSlot(TempSlot(le.Target))], rax);
                        break;

                    case IrInst.Alloc al:
                        a.mov(rdi, al.SizeBytes);
                        a.call(L_alloc);
                        a.mov(__[rbp + DispForSlot(TempSlot(al.Target))], rax);
                        break;

                    case IrInst.AllocAdt aa:
                        // Allocate (1 + FieldCount) * 8 bytes; layout: [tag:i64, field0, ..., fieldN]
                        a.mov(rdi, (1 + aa.FieldCount) * 8);
                        a.call(L_alloc);
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

                    case IrInst.AddInt add:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(add.Left))]);
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(add.Right))]);
                        a.add(rax, rbx);
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
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(sub.Right))]);
                        a.sub(rax, rbx);
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
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(ge.Right))]);
                        a.cmp(rax, rbx);
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
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(le.Right))]);
                        a.cmp(rax, rbx);
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
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(eq.Right))]);
                        a.cmp(rax, rbx);
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
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(ne.Right))]);
                        a.cmp(rax, rbx);
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
                        a.call(L_str_eq);
                        a.mov(__[rbp + DispForSlot(TempSlot(seq.Target))], rax);
                        break;

                    case IrInst.CmpStrNe sne:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(sne.Left))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(sne.Right))]);
                        a.call(L_str_eq);
                        a.xor(rax, 1);
                        a.mov(__[rbp + DispForSlot(TempSlot(sne.Target))], rax);
                        break;

                    case IrInst.ConcatStr cat:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(cat.Left))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(cat.Right))]);
                        a.call(L_concat);
                        a.mov(__[rbp + DispForSlot(TempSlot(cat.Target))], rax);
                        break;

                    case IrInst.MakeClosure mc:
                        // allocate 16 bytes for closure object
                        a.mov(rdi, 16);
                        a.call(L_alloc); // rax=ptr
                        // store code_ptr
                        a.mov(rbx, (long)(textVA + (ulong)0)); // placeholder, patched below? We avoid patching by loading label via lea? Not possible cross-assemble.
                        // We'll instead use absolute address of function label after assemble; complex. So: use RIP-relative by Iced labels: we can lea rbx, [rip+label].
                        // Use lea with label:
                        var targetFn = fLabels[mc.FuncLabel];
                        a.lea(rbx, __[targetFn]);
                        a.mov(__[rax + 0], rbx);
                        // store env_ptr
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(mc.EnvPtrTemp))]);
                        a.mov(__[rax + 8], rbx);
                        // save closure ptr
                        a.mov(__[rbp + DispForSlot(TempSlot(mc.Target))], rax);
                        break;

                    case IrInst.CallClosure cc:
                        // load closure ptr
                        a.mov(rbx, __[rbp + DispForSlot(TempSlot(cc.ClosureTemp))]);
                        // load code_ptr into rax, env_ptr into rdi
                        a.mov(rax, __[rbx + 0]);
                        a.mov(rdi, __[rbx + 8]);
                        // load arg into rsi
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(cc.ArgTemp))]);
                        a.call(rax);
                        a.mov(__[rbp + DispForSlot(TempSlot(cc.Target))], rax);
                        break;

                    case IrInst.PrintInt pi:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(pi.Source))]);
                        a.call(L_print_int);
                        break;

                    case IrInst.PrintStr ps:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ps.Source))]);
                        a.call(L_print_str);
                        break;

                    case IrInst.PrintBool pb:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(pb.Source))]);
                        a.call(L_print_bool);
                        break;

                    case IrInst.WriteStr ws:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ws.Source))]);
                        a.call(L_write_str);
                        break;

                    case IrInst.ReadLine rl:
                        a.call(L_read_line);
                        a.mov(__[rbp + DispForSlot(TempSlot(rl.Target))], rax);
                        break;

                    case IrInst.FsReadText frt:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(frt.PathTemp))]);
                        a.call(L_fs_read_text);
                        a.mov(__[rbp + DispForSlot(TempSlot(frt.Target))], rax);
                        break;

                    case IrInst.FsWriteText fwt:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(fwt.PathTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(fwt.TextTemp))]);
                        a.call(L_fs_write_text);
                        a.mov(__[rbp + DispForSlot(TempSlot(fwt.Target))], rax);
                        break;

                    case IrInst.FsExists fex:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(fex.PathTemp))]);
                        a.call(L_fs_exists);
                        a.mov(__[rbp + DispForSlot(TempSlot(fex.Target))], rax);
                        break;

                    case IrInst.NetTcpConnect ntc:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ntc.HostTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(ntc.PortTemp))]);
                        a.call(L_tcp_connect);
                        a.mov(__[rbp + DispForSlot(TempSlot(ntc.Target))], rax);
                        break;

                    case IrInst.NetTcpSend nts:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(nts.SocketTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(nts.TextTemp))]);
                        a.call(L_tcp_send);
                        a.mov(__[rbp + DispForSlot(TempSlot(nts.Target))], rax);
                        break;

                    case IrInst.NetTcpReceive ntr:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ntr.SocketTemp))]);
                        a.mov(rsi, __[rbp + DispForSlot(TempSlot(ntr.MaxBytesTemp))]);
                        a.call(L_tcp_receive);
                        a.mov(__[rbp + DispForSlot(TempSlot(ntr.Target))], rax);
                        break;

                    case IrInst.NetTcpClose ntcl:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(ntcl.SocketTemp))]);
                        a.call(L_tcp_close);
                        a.mov(__[rbp + DispForSlot(TempSlot(ntcl.Target))], rax);
                        break;

                    case IrInst.PanicStr err:
                        a.mov(rdi, __[rbp + DispForSlot(TempSlot(err.Source))]);
                        a.call(L_panic_str);
                        break;

                    case IrInst.Return ret:
                        a.mov(rax, __[rbp + DispForSlot(TempSlot(ret.Source))]);
                        a.leave();
                        a.ret();
                        break;

                    default:
                        throw new NotSupportedException(inst.GetType().Name);
                }
            }

            // If function falls through (shouldn't), return 0
            a.mov(rax, 0);
            a.leave();
            a.ret();
        }
    }

    private static int Align(int n, int a)
    {
        return (n + (a - 1)) / a * a;
    }

    private static int Align16(int n)
    {
        return (n + 15) / 16 * 16;
    }

    // ------------------------------
    // Data buffer: bytes + label offsets
    // ------------------------------
    private sealed class DataBuffer
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labelOffsets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _intern = new(StringComparer.Ordinal);

        public int Length => _bytes.Count;

        public void Align(int align)
        {
            while ((_bytes.Count % align) != 0)
            {
                _bytes.Add(0);
            }
        }

        public void Mark(string labelName)
        {
            _labelOffsets[labelName] = _bytes.Count;
        }

        public bool TryGetOffset(string labelName, out int offset)
        {
            return _labelOffsets.TryGetValue(labelName, out offset);
        }

        public void EmitByte(byte b)
        {
            _bytes.Add(b);
        }

        public void EmitBytes(byte[] bs)
        {
            _bytes.AddRange(bs);
        }

        public void EmitUInt64(ulong v)
        {
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, v);
            _bytes.AddRange(tmp.ToArray());
        }

        public byte[] ToArray()
        {
            return _bytes.ToArray();
        }

        public string InternString(string value)
        {
            if (_intern.TryGetValue(value, out var label))
            {
                return label;
            }

            label = $"str_{_intern.Count}";
            _intern[value] = label;

            Align(8);
            Mark(label);
            var bytes = Encoding.UTF8.GetBytes(value);
            EmitUInt64((ulong)bytes.Length);
            EmitBytes(bytes);

            return label;
        }

        public void EnsureString(string label, string value)
        {
            if (_labelOffsets.ContainsKey(label))
            {
                return;
            }

            Align(8);
            Mark(label);
            var bytes = Encoding.UTF8.GetBytes(value);
            EmitUInt64((ulong)bytes.Length);
            EmitBytes(bytes);
        }
    }

    private static void EnsureStringLiteral(DataBuffer data, string label, string value)
    {
        data.EnsureString(label, value);
    }

    // ------------------------------
    // ELF writer
    // ------------------------------
}
