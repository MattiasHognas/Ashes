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
    private const int IatIndex_CommandLineToArgvW = 7;

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
        var L_alloc = asm.CreateLabel();
        var L_write_str = asm.CreateLabel();
        var L_print_str = asm.CreateLabel();
        var L_read_line = asm.CreateLabel();
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

        // read_line() -> RAX = OptionString
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
