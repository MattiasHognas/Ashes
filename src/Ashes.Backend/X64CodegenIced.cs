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
