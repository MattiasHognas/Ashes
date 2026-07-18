using Ashes.Semantics;
using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LlvmValueHandle EmitReadLine(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitReadLineSlots slots = EmitReadLineScratch(state);
        EmitReadLineBlocks blocks = EmitReadLineCreateBlocks(state);
        LlvmApi.BuildBr(builder, blocks.Loop);
        EmitReadLineLoopRefill(state, slots, blocks);
        EmitReadLineInspectStore(state, slots, blocks);
        EmitReadLineFinish(state, slots, blocks);
        LlvmApi.PositionBuilderAtEnd(builder, blocks.Continue);
        return LlvmApi.BuildLoad2(builder, state.I64, slots.ResultSlot, "read_line_result_value");
    }

    private readonly record struct EmitReadLineSlots(
        LlvmValueHandle InputBufPtr,
        LlvmValueHandle ByteSlot,
        LlvmValueHandle LenSlot,
        LlvmValueHandle ResultSlot,
        LlvmValueHandle StdinBufPtr,
        LlvmValueHandle StdinPosSlot,
        LlvmValueHandle StdinLenSlot,
        LlvmValueHandle StdinHandle,
        LlvmValueHandle BytesReadSlot);

    private readonly record struct EmitReadLineBlocks(
        LlvmBasicBlockHandle Loop,
        LlvmBasicBlockHandle Refill,
        LlvmBasicBlockHandle HaveByte,
        LlvmBasicBlockHandle Inspect,
        LlvmBasicBlockHandle SkipCr,
        LlvmBasicBlockHandle StoreByte,
        LlvmBasicBlockHandle AppendByte,
        LlvmBasicBlockHandle Eof,
        LlvmBasicBlockHandle FinishSome,
        LlvmBasicBlockHandle ReturnNone,
        LlvmBasicBlockHandle Overflow,
        LlvmBasicBlockHandle Continue);

    private static EmitReadLineSlots EmitReadLineScratch(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle inputBufType = LlvmApi.ArrayType2(state.I8, InputBufSize);
        // The line buffer and scratch slots are module globals, not stack allocas: a 64 KB
        // alloca per call grows the stack ~64 KB/iteration when readLine runs inside a TCO loop
        // (a single never-returning stack frame), overflowing after a few hundred lines. A single
        // reused global is safe here — Ashes is single-threaded and readLine is non-reentrant; the
        // buffer is copied to a fresh heap string before the call returns.
        LlvmValueHandle inputBuf = ReadLineScratchGlobal(state, "__ashes_readline_buf", inputBufType);
        LlvmValueHandle inputBufPtr = GetArrayElementPointer(state, inputBufType, inputBuf, LlvmApi.ConstInt(state.I64, 0, 0), "read_line_buf_ptr");
        LlvmValueHandle byteSlot = ReadLineScratchGlobal(state, "__ashes_readline_byte", state.I8);
        LlvmValueHandle lenSlot = ReadLineScratchGlobal(state, "__ashes_readline_len", state.I64);
        LlvmValueHandle resultSlot = ReadLineScratchGlobal(state, "__ashes_readline_result", state.I64);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), lenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        // Buffered stdin: bytes are pulled from a refillable module-global buffer rather than one
        // read() syscall per byte. The buffer, read position and read length PERSIST across readLine
        // calls (a refill may read past the newline; the leftover feeds the next call). Zero-init
        // means rpos==rlen==0 on first use, which triggers the first refill.
        LlvmTypeHandle stdinBufType = LlvmApi.ArrayType2(state.I8, StdinReadBufSize);
        LlvmValueHandle stdinBuf = ReadLineScratchGlobal(state, "__ashes_stdin_rbuf", stdinBufType);
        LlvmValueHandle stdinBufPtr = GetArrayElementPointer(state, stdinBufType, stdinBuf, LlvmApi.ConstInt(state.I64, 0, 0), "stdin_rbuf_ptr");
        LlvmValueHandle stdinPosSlot = ReadLineScratchGlobal(state, "__ashes_stdin_rpos", state.I64);
        LlvmValueHandle stdinLenSlot = ReadLineScratchGlobal(state, "__ashes_stdin_rlen", state.I64);

        LlvmValueHandle stdinHandle = default;
        LlvmValueHandle bytesReadSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "stdin_handle");
            bytesReadSlot = ReadLineScratchGlobal(state, "__ashes_readline_bytes_read", state.I32);
        }

        return new EmitReadLineSlots(inputBufPtr, byteSlot, lenSlot, resultSlot, stdinBufPtr, stdinPosSlot, stdinLenSlot, stdinHandle, bytesReadSlot);
    }

    private static EmitReadLineBlocks EmitReadLineCreateBlocks(LlvmCodegenState state)
    {
        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_loop");
        var refillBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_refill");
        var haveByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_have_byte");
        var inspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_inspect");
        var skipCrBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_skip_cr");
        var storeByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_store_byte");
        var appendByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_append_byte");
        var eofBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_eof");
        var finishSomeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_finish_some");
        var returnNoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_return_none");
        var overflowBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_overflow");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_continue");
        return new EmitReadLineBlocks(loopBlock, refillBlock, haveByteBlock, inspectBlock, skipCrBlock, storeByteBlock, appendByteBlock, eofBlock, finishSomeBlock, returnNoneBlock, overflowBlock, continueBlock);
    }

    private static void EmitReadLineLoopRefill(LlvmCodegenState state, EmitReadLineSlots slots, EmitReadLineBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (inputBufPtr, byteSlot, lenSlot, resultSlot, stdinBufPtr, stdinPosSlot, stdinLenSlot, stdinHandle, bytesReadSlot) = slots;
        var (loopBlock, refillBlock, haveByteBlock, inspectBlock, skipCrBlock, storeByteBlock, appendByteBlock, eofBlock, finishSomeBlock, returnNoneBlock, overflowBlock, continueBlock) = blocks;

        // loop: if the buffer is exhausted, refill; otherwise take the next buffered byte.
        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle curPos = LlvmApi.BuildLoad2(builder, state.I64, stdinPosSlot, "read_line_rpos");
        LlvmValueHandle curLen = LlvmApi.BuildLoad2(builder, state.I64, stdinLenSlot, "read_line_rlen");
        LlvmValueHandle exhausted = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, curPos, curLen, "read_line_buf_exhausted");
        LlvmApi.BuildCondBr(builder, exhausted, refillBlock, haveByteBlock);

        // refill: one block read into the shared buffer. n <= 0 means EOF.
        LlvmApi.PositionBuilderAtEnd(builder, refillBlock);
        LlvmValueHandle refilled = IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSyscall(
                state,
                SyscallRead,
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.BuildPtrToInt(builder, stdinBufPtr, state.I64, "read_line_rbuf_ptr_int"),
                LlvmApi.ConstInt(state.I64, StdinReadBufSize, 0),
                "sys_read_line_block")
            : EmitWindowsReadBlock(state, stdinHandle, stdinBufPtr, LlvmApi.ConstInt(state.I32, StdinReadBufSize, 0), bytesReadSlot);
        LlvmApi.BuildStore(builder, refilled, stdinLenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), stdinPosSlot);
        LlvmValueHandle refilledEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, refilled, LlvmApi.ConstInt(state.I64, 0, 0), "read_line_refill_empty");
        LlvmApi.BuildCondBr(builder, refilledEmpty, eofBlock, haveByteBlock);

        // haveByte: read buf[pos] into byteSlot and advance pos.
        LlvmApi.PositionBuilderAtEnd(builder, haveByteBlock);
        LlvmValueHandle takePos = LlvmApi.BuildLoad2(builder, state.I64, stdinPosSlot, "read_line_take_pos");
        LlvmValueHandle takePtr = LlvmApi.BuildGEP2(builder, state.I8, stdinBufPtr, [takePos], "read_line_take_ptr");
        LlvmValueHandle takenByte = LlvmApi.BuildLoad2(builder, state.I8, takePtr, "read_line_taken_byte");
        LlvmApi.BuildStore(builder, takenByte, byteSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, takePos, LlvmApi.ConstInt(state.I64, 1, 0), "read_line_take_pos_next"), stdinPosSlot);
        LlvmApi.BuildBr(builder, inspectBlock);
    }

    private static void EmitReadLineInspectStore(LlvmCodegenState state, EmitReadLineSlots slots, EmitReadLineBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (inputBufPtr, byteSlot, lenSlot, resultSlot, stdinBufPtr, stdinPosSlot, stdinLenSlot, stdinHandle, bytesReadSlot) = slots;
        var (loopBlock, refillBlock, haveByteBlock, inspectBlock, skipCrBlock, storeByteBlock, appendByteBlock, eofBlock, finishSomeBlock, returnNoneBlock, overflowBlock, continueBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, inspectBlock);
        LlvmValueHandle currentByte = LlvmApi.BuildLoad2(builder, state.I8, byteSlot, "read_line_current_byte");
        LlvmValueHandle isLf = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, 10, 0), "read_line_is_lf");
        LlvmApi.BuildCondBr(builder, isLf, finishSomeBlock, skipCrBlock);

        LlvmApi.PositionBuilderAtEnd(builder, skipCrBlock);
        LlvmValueHandle isCr = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, 13, 0), "read_line_is_cr");
        LlvmApi.BuildCondBr(builder, isCr, loopBlock, storeByteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeByteBlock);
        LlvmValueHandle currentLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "read_line_len_value");
        LlvmValueHandle atCapacity = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, currentLen, LlvmApi.ConstInt(state.I64, InputBufSize, 0), "read_line_at_capacity");
        LlvmApi.BuildCondBr(builder, atCapacity, overflowBlock, appendByteBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendByteBlock);
        LlvmValueHandle destPtr = LlvmApi.BuildGEP2(builder, state.I8, inputBufPtr, [currentLen], "read_line_dest_ptr");
        LlvmApi.BuildStore(builder, currentByte, destPtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, currentLen, LlvmApi.ConstInt(state.I64, 1, 0), "read_line_len_next"), lenSlot);
        LlvmApi.BuildBr(builder, loopBlock);
    }

    private static void EmitReadLineFinish(LlvmCodegenState state, EmitReadLineSlots slots, EmitReadLineBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (inputBufPtr, byteSlot, lenSlot, resultSlot, stdinBufPtr, stdinPosSlot, stdinLenSlot, stdinHandle, bytesReadSlot) = slots;
        var (loopBlock, refillBlock, haveByteBlock, inspectBlock, skipCrBlock, storeByteBlock, appendByteBlock, eofBlock, finishSomeBlock, returnNoneBlock, overflowBlock, continueBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, eofBlock);
        LlvmValueHandle lenAtEof = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "read_line_len_at_eof");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, lenAtEof, LlvmApi.ConstInt(state.I64, 0, 0), "read_line_is_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, returnNoneBlock, finishSomeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finishSomeBlock);
        LlvmValueHandle finalLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "read_line_final_len");
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, finalLen, LlvmApi.ConstInt(state.I64, 8, 0), "read_line_string_bytes"));
        StoreMemory(state, stringRef, 0, finalLen, "read_line_string_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, "read_line_string_dest"), inputBufPtr, finalLen, "read_line_copy_bytes");
        LlvmValueHandle someRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someRef, 8, stringRef, "read_line_some_value");
        LlvmApi.BuildStore(builder, someRef, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnNoneBlock);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, overflowBlock);
        EmitPanic(state, EmitStackStringObject(state, "readLine input too long"));
    }

    /// <summary>
    /// Returns a zero-initialised, internal-linkage module global of <paramref name="type"/> used
    /// as reusable <c>readLine</c> scratch. Created once per module (cached by name) and lives in
    /// <c>.bss</c>, so the 64 KB line buffer adds no file size and — crucially — costs zero stack
    /// per call, even when readLine is invoked from inside a TCO loop.
    /// </summary>
    private static LlvmValueHandle ReadLineScratchGlobal(LlvmCodegenState state, string name, LlvmTypeHandle type) =>
        state.Target.GetOrAddNamedGlobal(name, () =>
        {
            LlvmValueHandle global = LlvmApi.AddGlobal(state.Target.Module, type, name);
            LlvmApi.SetInitializer(global, LlvmApi.ConstNull(type));
            LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
            return global;
        });

    // Set to 1 by the SIGINT/SIGTERM handler; the accept step checks it and completes with
    // ServerShutdownSentinel so serve() stops accepting and returns Ok(()) (graceful shutdown).
    private const string ServerShutdownSentinel = "__ashes_server_shutdown";

    private const int EpollMaskTableSize = 65536;

    private readonly record struct LinuxTlsGlobals(
        LlvmValueHandle InitStatusGlobal,
        LlvmValueHandle ContextGlobal,
        LlvmValueHandle RuntimeGlobal,
        LlvmValueHandle MbedTlsReadCallback,
        LlvmValueHandle MbedTlsWriteCallback,
        LlvmValueHandle ServerConfigGlobal);

    private static void EmitEntryProgramArgsInitialization(LlvmCodegenState state)
    {
        LlvmApi.BuildStore(state.Target.Builder, LlvmApi.ConstInt(state.I64, 0, 0), state.ProgramArgsSlot);

        if (IsLinuxFlavor(state.Flavor))
        {
            EmitLinuxProgramArgsInitialization(state);
            return;
        }

        EmitWindowsProgramArgsInitialization(state);
    }

    private readonly record struct EmitLinuxProgramArgsSlots(
        LlvmValueHandle ListSlot,
        LlvmValueHandle IndexSlot,
        LlvmValueHandle ArgPtrSlot,
        LlvmValueHandle LenSlot,
        LlvmValueHandle StackPtr);

    private readonly record struct EmitLinuxProgramArgsBlocks(
        LlvmBasicBlockHandle Init,
        LlvmBasicBlockHandle LoopCheck,
        LlvmBasicBlockHandle LenCheck,
        LlvmBasicBlockHandle LenBody,
        LlvmBasicBlockHandle BuildNode,
        LlvmBasicBlockHandle Done,
        LlvmBasicBlockHandle LenLoopCheck);

    private static void EmitLinuxProgramArgsInitialization(LlvmCodegenState state)
    {
        EmitLinuxProgramArgsSlots slots = EmitLinuxProgramArgsPrologue(state, out EmitLinuxProgramArgsBlocks blocks);
        EmitLinuxProgramArgsLoop(state, slots, blocks);
        EmitLinuxProgramArgsBuild(state, slots, blocks);
    }

    private static EmitLinuxProgramArgsSlots EmitLinuxProgramArgsPrologue(LlvmCodegenState state, out EmitLinuxProgramArgsBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_list");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_index");
        LlvmValueHandle argPtrSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_arg_ptr");
        LlvmValueHandle lenSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_arg_len");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), listSlot);

        LlvmValueHandle stackPtr = state.EntryStackPointer;
        LlvmValueHandle argc = LoadMemory(state, stackPtr, 0, "program_args_argc");

        var initBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_init");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_loop_check");
        var lenCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_len_check");
        var lenBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_len_body");
        var buildNodeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_build_node");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_done");
        var lenLoopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_len_loop_check");
        blocks = new EmitLinuxProgramArgsBlocks(initBlock, loopCheckBlock, lenCheckBlock, lenBodyBlock, buildNodeBlock, doneBlock, lenLoopCheckBlock);

        LlvmValueHandle hasArgs = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            argc,
            LlvmApi.ConstInt(state.I64, 1, 0),
            "program_args_has_args");
        LlvmApi.BuildCondBr(builder, hasArgs, initBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, argc, LlvmApi.ConstInt(state.I64, 1, 0), "program_args_start_index"),
            indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        return new EmitLinuxProgramArgsSlots(listSlot, indexSlot, argPtrSlot, lenSlot, stackPtr);
    }

    private static void EmitLinuxProgramArgsLoop(LlvmCodegenState state, EmitLinuxProgramArgsSlots slots, EmitLinuxProgramArgsBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (listSlot, indexSlot, argPtrSlot, lenSlot, stackPtr) = slots;
        var (initBlock, loopCheckBlock, lenCheckBlock, lenBodyBlock, buildNodeBlock, doneBlock, lenLoopCheckBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "program_args_index_value");
        LlvmValueHandle shouldContinue = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            index,
            LlvmApi.ConstInt(state.I64, 0, 0),
            "program_args_continue");
        LlvmApi.BuildCondBr(builder, shouldContinue, lenCheckBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, lenCheckBlock);
        LlvmValueHandle argvEntryOffset = LlvmApi.BuildMul(builder, index, LlvmApi.ConstInt(state.I64, 8, 0), "program_args_argv_entry_offset");
        LlvmValueHandle argvEntryAddress = LlvmApi.BuildAdd(builder,
            stackPtr,
            LlvmApi.BuildAdd(builder, LlvmApi.ConstInt(state.I64, 8, 0), argvEntryOffset, "program_args_argv_offset"),
            "program_args_argv_entry_addr");
        LlvmValueHandle argPtr = LoadMemory(state, argvEntryAddress, 0, "program_args_argv_entry");
        LlvmApi.BuildStore(builder, argPtr, argPtrSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), lenSlot);

        LlvmApi.BuildBr(builder, lenLoopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, lenLoopCheckBlock);
        LlvmValueHandle currentLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "program_args_current_len");
        LlvmValueHandle currentArgPtr = LlvmApi.BuildLoad2(builder, state.I64, argPtrSlot, "program_args_current_arg_ptr");
        LlvmValueHandle currentBytePtr = LlvmApi.BuildGEP2(builder,
            state.I8,
            LlvmApi.BuildIntToPtr(builder, currentArgPtr, state.I8Ptr, "program_args_arg_bytes"),
            [currentLen],
            "program_args_current_byte_ptr");
        LlvmValueHandle currentByte = LlvmApi.BuildLoad2(builder, state.I8, currentBytePtr, "program_args_current_byte");
        LlvmValueHandle reachedTerminator = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Eq,
            currentByte,
            LlvmApi.ConstInt(state.I8, 0, 0),
            "program_args_reached_terminator");
        LlvmApi.BuildCondBr(builder, reachedTerminator, buildNodeBlock, lenBodyBlock);
    }

    private static void EmitLinuxProgramArgsBuild(LlvmCodegenState state, EmitLinuxProgramArgsSlots slots, EmitLinuxProgramArgsBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (listSlot, indexSlot, argPtrSlot, lenSlot, stackPtr) = slots;
        var (initBlock, loopCheckBlock, lenCheckBlock, lenBodyBlock, buildNodeBlock, doneBlock, lenLoopCheckBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, lenBodyBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "program_args_current_len"), LlvmApi.ConstInt(state.I64, 1, 0), "program_args_next_len"),
            lenSlot);
        LlvmApi.BuildBr(builder, lenLoopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildNodeBlock);
        LlvmValueHandle argLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "program_args_arg_len_value");
        LlvmValueHandle stringRef = EmitAllocDynamic(
            state,
            LlvmApi.BuildAdd(builder, argLen, LlvmApi.ConstInt(state.I64, 8, 0), "program_args_string_bytes"));
        StoreMemory(state, stringRef, 0, argLen, "program_args_string_len");
        EmitCopyBytes(
            state,
            GetStringBytesPointer(state, stringRef, "program_args_string_dest"),
            LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, argPtrSlot, "program_args_copy_arg_ptr"), state.I8Ptr, "program_args_string_src"),
            argLen,
            "program_args_copy_bytes");
        LlvmValueHandle consRef = EmitAlloc(state, 16);
        StoreMemory(state, consRef, 0, stringRef, "program_args_cons_head");
        StoreMemory(state, consRef, 8, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_prev_list"), "program_args_cons_tail");
        LlvmApi.BuildStore(builder, consRef, listSlot);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "program_args_index_before_dec"), LlvmApi.ConstInt(state.I64, 1, 0), "program_args_index_dec"),
            indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
    }

    private readonly record struct EmitWindowsProgramArgsSlots(
        LlvmTypeHandle I16,
        LlvmTypeHandle I16Ptr,
        LlvmTypeHandle WideCharToMultiByteType,
        LlvmTypeHandle LocalFreeType,
        LlvmValueHandle ListSlot,
        LlvmValueHandle ArgcSlot,
        LlvmValueHandle IndexSlot,
        LlvmValueHandle WideArgSlot,
        LlvmValueHandle WideLenSlot,
        LlvmValueHandle StringRefSlot,
        LlvmValueHandle ArgvWide);

    private readonly record struct EmitWindowsProgramArgsBlocks(
        LlvmBasicBlockHandle HaveArgv,
        LlvmBasicBlockHandle MaybeLoop,
        LlvmBasicBlockHandle LoopCheck,
        LlvmBasicBlockHandle WideArgSetup,
        LlvmBasicBlockHandle WideLenBody,
        LlvmBasicBlockHandle WideLenInc,
        LlvmBasicBlockHandle ConvertArg,
        LlvmBasicBlockHandle CreateUtf8String,
        LlvmBasicBlockHandle CreateEmptyString,
        LlvmBasicBlockHandle LinkArg,
        LlvmBasicBlockHandle FreeArgv,
        LlvmBasicBlockHandle Done);

    private static void EmitWindowsProgramArgsInitialization(LlvmCodegenState state)
    {
        EmitWindowsProgramArgsSlots slots = EmitWindowsProgramArgsSetup(state);
        EmitWindowsProgramArgsBlocks blocks = EmitWindowsProgramArgsCreateBlocks(state);
        EmitWindowsProgramArgsDecide(state, slots, blocks);
        EmitWindowsProgramArgsWideLen(state, slots, blocks);
        EmitWindowsProgramArgsConvert(state, slots, blocks);
        EmitWindowsProgramArgsTail(state, slots, blocks);
    }

    private static EmitWindowsProgramArgsSlots EmitWindowsProgramArgsSetup(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle i16PtrPtr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmTypeHandle getCommandLineType = LlvmApi.FunctionType(i16Ptr, []);
        LlvmTypeHandle wideCharToMultiByteType = LlvmApi.FunctionType(state.I32, [state.I32, state.I32, i16Ptr, state.I32, state.I8Ptr, state.I32, state.I8Ptr, state.I8Ptr]);
        LlvmTypeHandle localFreeType = LlvmApi.FunctionType(state.I8Ptr, [state.I8Ptr]);
        LlvmTypeHandle commandLineToArgvType = LlvmApi.FunctionType(i16PtrPtr, [i16Ptr, state.I32Ptr]);

        LlvmValueHandle listSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_list");
        LlvmValueHandle argcSlot = LlvmApi.BuildAlloca(builder, state.I32, "program_args_argc");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I32, "program_args_index");
        LlvmValueHandle wideArgSlot = LlvmApi.BuildAlloca(builder, i16Ptr, "program_args_wide_arg");
        LlvmValueHandle wideLenSlot = LlvmApi.BuildAlloca(builder, state.I32, "program_args_wide_len");
        LlvmValueHandle stringRefSlot = LlvmApi.BuildAlloca(builder, state.I64, "program_args_string_ref");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), listSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), argcSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), stringRefSlot);

        LlvmValueHandle getCommandLinePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsGetCommandLineImport,
            "get_command_line_ptr");
        LlvmValueHandle commandLinePtr = LlvmApi.BuildCall2(builder,
            getCommandLineType,
            getCommandLinePtr,
            Array.Empty<LlvmValueHandle>(),
            "command_line");

        LlvmValueHandle commandLineToArgvPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCommandLineToArgvImport,
            "command_line_to_argv_ptr");
        LlvmValueHandle argvWide = LlvmApi.BuildCall2(builder,
            commandLineToArgvType,
            commandLineToArgvPtr,
            [commandLinePtr, argcSlot],
            "argv_wide");

        return new EmitWindowsProgramArgsSlots(i16, i16Ptr, wideCharToMultiByteType, localFreeType, listSlot, argcSlot, indexSlot, wideArgSlot, wideLenSlot, stringRefSlot, argvWide);
    }

    private static EmitWindowsProgramArgsBlocks EmitWindowsProgramArgsCreateBlocks(LlvmCodegenState state)
    {
        var haveArgvBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_have_argv");
        var maybeLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_maybe_loop");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_loop_check");
        var wideArgSetupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_wide_arg_setup");
        var wideLenBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_wide_len_body");
        var wideLenIncBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_wide_len_inc");
        var convertArgBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_convert_arg");
        var createUtf8StringBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_create_utf8_string");
        var createEmptyStringBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_create_empty_string");
        var linkArgBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_link_arg");
        var freeArgvBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_free_argv");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_done");
        return new EmitWindowsProgramArgsBlocks(haveArgvBlock, maybeLoopBlock, loopCheckBlock, wideArgSetupBlock, wideLenBodyBlock, wideLenIncBlock, convertArgBlock, createUtf8StringBlock, createEmptyStringBlock, linkArgBlock, freeArgvBlock, doneBlock);
    }

    private static void EmitWindowsProgramArgsDecide(LlvmCodegenState state, EmitWindowsProgramArgsSlots slots, EmitWindowsProgramArgsBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (i16, i16Ptr, wideCharToMultiByteType, localFreeType, listSlot, argcSlot, indexSlot, wideArgSlot, wideLenSlot, stringRefSlot, argvWide) = slots;
        var (haveArgvBlock, maybeLoopBlock, loopCheckBlock, wideArgSetupBlock, wideLenBodyBlock, wideLenIncBlock, convertArgBlock, createUtf8StringBlock, createEmptyStringBlock, linkArgBlock, freeArgvBlock, doneBlock) = blocks;

        LlvmValueHandle hasArgv = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            LlvmApi.BuildPtrToInt(builder, argvWide, state.I64, "argv_wide_i64"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "program_args_has_argv");
        LlvmApi.BuildCondBr(builder, hasArgv, haveArgvBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, haveArgvBlock);
        LlvmValueHandle argc = LlvmApi.BuildLoad2(builder, state.I32, argcSlot, "program_args_argc_value");
        LlvmValueHandle hasUserArgs = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            argc,
            LlvmApi.ConstInt(state.I32, 1, 0),
            "program_args_has_user_args");
        LlvmApi.BuildCondBr(builder, hasUserArgs, maybeLoopBlock, freeArgvBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeLoopBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, argc, LlvmApi.ConstInt(state.I32, 1, 0), "program_args_start_index"),
            indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);
    }

    private static void EmitWindowsProgramArgsWideLen(LlvmCodegenState state, EmitWindowsProgramArgsSlots slots, EmitWindowsProgramArgsBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (i16, i16Ptr, wideCharToMultiByteType, localFreeType, listSlot, argcSlot, indexSlot, wideArgSlot, wideLenSlot, stringRefSlot, argvWide) = slots;
        var (haveArgvBlock, maybeLoopBlock, loopCheckBlock, wideArgSetupBlock, wideLenBodyBlock, wideLenIncBlock, convertArgBlock, createUtf8StringBlock, createEmptyStringBlock, linkArgBlock, freeArgvBlock, doneBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I32, indexSlot, "program_args_index_value");
        LlvmValueHandle shouldContinue = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            index,
            LlvmApi.ConstInt(state.I32, 0, 0),
            "program_args_continue");
        LlvmApi.BuildCondBr(builder, shouldContinue, wideArgSetupBlock, freeArgvBlock);

        LlvmApi.PositionBuilderAtEnd(builder, wideArgSetupBlock);
        LlvmValueHandle wideArgPtrPtr = LlvmApi.BuildGEP2(builder,
            i16Ptr,
            argvWide,
            [LlvmApi.BuildSExt(builder, index, state.I64, "program_args_index_i64")],
            "program_args_wide_arg_ptr");
        LlvmValueHandle wideArgPtr = LlvmApi.BuildLoad2(builder, i16Ptr, wideArgPtrPtr, "program_args_wide_arg_value");
        LlvmApi.BuildStore(builder, wideArgPtr, wideArgSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), wideLenSlot);
        LlvmApi.BuildBr(builder, wideLenBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, wideLenBodyBlock);
        LlvmValueHandle wideLen = LlvmApi.BuildLoad2(builder, state.I32, wideLenSlot, "program_args_wide_len_value");
        LlvmValueHandle wideCharPtr = LlvmApi.BuildGEP2(builder,
            i16,
            LlvmApi.BuildLoad2(builder, i16Ptr, wideArgSlot, "program_args_wide_arg_current"),
            [LlvmApi.BuildSExt(builder, wideLen, state.I64, "program_args_wide_len_i64")],
            "program_args_wide_char_ptr");
        LlvmValueHandle wideChar = LlvmApi.BuildLoad2(builder, i16, wideCharPtr, "program_args_wide_char");
        LlvmValueHandle atTerminator = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Eq,
            wideChar,
            LlvmApi.ConstInt(i16, 0, 0),
            "program_args_at_wide_terminator");
        LlvmApi.BuildCondBr(builder, atTerminator, convertArgBlock, wideLenIncBlock);

        LlvmApi.PositionBuilderAtEnd(builder, wideLenIncBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I32, wideLenSlot, "program_args_wide_len_before_inc"), LlvmApi.ConstInt(state.I32, 1, 0), "program_args_wide_len_inc"),
            wideLenSlot);
        LlvmApi.BuildBr(builder, wideLenBodyBlock);
    }

    private static void EmitWindowsProgramArgsConvert(LlvmCodegenState state, EmitWindowsProgramArgsSlots slots, EmitWindowsProgramArgsBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (i16, i16Ptr, wideCharToMultiByteType, localFreeType, listSlot, argcSlot, indexSlot, wideArgSlot, wideLenSlot, stringRefSlot, argvWide) = slots;
        var (haveArgvBlock, maybeLoopBlock, loopCheckBlock, wideArgSetupBlock, wideLenBodyBlock, wideLenIncBlock, convertArgBlock, createUtf8StringBlock, createEmptyStringBlock, linkArgBlock, freeArgvBlock, doneBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, convertArgBlock);
        LlvmValueHandle wideArg = LlvmApi.BuildLoad2(builder, i16Ptr, wideArgSlot, "program_args_wide_arg_for_convert");
        LlvmValueHandle wcharCount = LlvmApi.BuildLoad2(builder, state.I32, wideLenSlot, "program_args_wchar_count");
        LlvmValueHandle wideCharToMultiBytePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWideCharToMultiByteImport,
            "wide_char_to_multi_byte_ptr");
        LlvmValueHandle nullI8Ptr = LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, "null_i8_ptr");
        LlvmValueHandle byteCount = LlvmApi.BuildCall2(builder,
            wideCharToMultiByteType,
            wideCharToMultiBytePtr,
            [
                LlvmApi.ConstInt(state.I32, Utf8CodePage, 0),
                LlvmApi.ConstInt(state.I32, 0, 0),
                wideArg,
                wcharCount,
                nullI8Ptr,
                LlvmApi.ConstInt(state.I32, 0, 0),
                nullI8Ptr,
                nullI8Ptr
            ],
            "program_args_byte_count");
        LlvmValueHandle hasBytes = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Sgt,
            byteCount,
            LlvmApi.ConstInt(state.I32, 0, 0),
            "program_args_has_bytes");
        LlvmApi.BuildCondBr(builder, hasBytes, createUtf8StringBlock, createEmptyStringBlock);

        LlvmApi.PositionBuilderAtEnd(builder, createUtf8StringBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(
            state,
            LlvmApi.BuildAdd(builder, LlvmApi.BuildZExt(builder, byteCount, state.I64, "program_args_byte_count_i64"), LlvmApi.ConstInt(state.I64, 8, 0), "program_args_string_bytes"));
        StoreMemory(state, stringRef, 0, LlvmApi.BuildZExt(builder, byteCount, state.I64, "program_args_string_len"), "program_args_string_len");
        LlvmValueHandle stringDest = GetStringBytesPointer(state, stringRef, "program_args_string_dest");
        LlvmApi.BuildCall2(builder,
            wideCharToMultiByteType,
            wideCharToMultiBytePtr,
            [
                LlvmApi.ConstInt(state.I32, Utf8CodePage, 0),
                LlvmApi.ConstInt(state.I32, 0, 0),
                wideArg,
                wcharCount,
                stringDest,
                byteCount,
                nullI8Ptr,
                nullI8Ptr
            ],
            "program_args_copy_utf8");
        LlvmApi.BuildStore(builder, stringRef, stringRefSlot);
        LlvmApi.BuildBr(builder, linkArgBlock);
    }

    private static void EmitWindowsProgramArgsTail(LlvmCodegenState state, EmitWindowsProgramArgsSlots slots, EmitWindowsProgramArgsBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (i16, i16Ptr, wideCharToMultiByteType, localFreeType, listSlot, argcSlot, indexSlot, wideArgSlot, wideLenSlot, stringRefSlot, argvWide) = slots;
        var (haveArgvBlock, maybeLoopBlock, loopCheckBlock, wideArgSetupBlock, wideLenBodyBlock, wideLenIncBlock, convertArgBlock, createUtf8StringBlock, createEmptyStringBlock, linkArgBlock, freeArgvBlock, doneBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, createEmptyStringBlock);
        LlvmValueHandle emptyStringRef = EmitAlloc(state, 8);
        StoreMemory(state, emptyStringRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "program_args_empty_string_len");
        LlvmApi.BuildStore(builder, emptyStringRef, stringRefSlot);
        LlvmApi.BuildBr(builder, linkArgBlock);

        LlvmApi.PositionBuilderAtEnd(builder, linkArgBlock);
        LlvmValueHandle consRef = EmitAlloc(state, 16);
        StoreMemory(state, consRef, 0, LlvmApi.BuildLoad2(builder, state.I64, stringRefSlot, "program_args_string_ref_value"), "program_args_cons_head");
        StoreMemory(state, consRef, 8, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_prev_list"), "program_args_cons_tail");
        LlvmApi.BuildStore(builder, consRef, listSlot);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildSub(builder, LlvmApi.BuildLoad2(builder, state.I32, indexSlot, "program_args_index_before_dec"), LlvmApi.ConstInt(state.I32, 1, 0), "program_args_index_dec"),
            indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, freeArgvBlock);
        LlvmValueHandle localFreePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsLocalFreeImport,
            "local_free_ptr");
        LlvmApi.BuildCall2(builder,
            localFreeType,
            localFreePtr,
            [LlvmApi.BuildBitCast(builder, argvWide, state.I8Ptr, "argv_wide_hlocal")],
            "program_args_local_free");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
    }

    private readonly record struct EmitReadExactSlots(
        LlvmValueHandle DestPtr,
        LlvmValueHandle ReadSoFarSlot,
        LlvmValueHandle ResultSlot,
        LlvmValueHandle StringRef,
        LlvmValueHandle StdinHandle,
        LlvmValueHandle BytesReadSlot);

    private readonly record struct EmitReadExactBlocks(
        LlvmBasicBlockHandle Loop,
        LlvmBasicBlockHandle Read,
        LlvmBasicBlockHandle Error,
        LlvmBasicBlockHandle Done,
        LlvmBasicBlockHandle Continue);

    // Ashes.IO.readExact(n): read exactly n bytes from stdin; return Result(Str, Str).
    private static LlvmValueHandle EmitReadExact(LlvmCodegenState state, LlvmValueHandle countVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        EmitReadExactSlots slots = EmitReadExactSetup(state, countVal);
        EmitReadExactDrain(state, slots, countVal);
        EmitReadExactBlocks blocks = EmitReadExactCreateBlocks(state);
        LlvmApi.BuildBr(builder, blocks.Loop);
        EmitReadExactLoop(state, slots, blocks, countVal);
        return EmitReadExactTail(state, slots, blocks);
    }

    private static EmitReadExactSlots EmitReadExactSetup(LlvmCodegenState state, LlvmValueHandle countVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;

        // Allocate: string header (8 bytes for length) + count bytes.
        LlvmValueHandle totalBytes = LlvmApi.BuildAdd(builder, countVal, LlvmApi.ConstInt(state.I64, 8, 0), "re_total_bytes");
        LlvmValueHandle stringRef = EmitAllocDynamic(state, totalBytes);
        StoreMemory(state, stringRef, 0, countVal, "re_string_len");
        LlvmValueHandle destPtr = GetStringBytesPointer(state, stringRef, "re_dest_ptr");

        LlvmValueHandle readSoFarSlot = LlvmApi.BuildAlloca(builder, state.I64, "re_read_so_far");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "re_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), readSoFarSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmValueHandle stdinHandle = default;
        LlvmValueHandle bytesReadSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "re_stdin_handle");
            bytesReadSlot = LlvmApi.BuildAlloca(builder, state.I32, "re_bytes_read");
        }

        return new EmitReadExactSlots(destPtr, readSoFarSlot, resultSlot, stringRef, stdinHandle, bytesReadSlot);
    }

    private static void EmitReadExactDrain(LlvmCodegenState state, EmitReadExactSlots slots, LlvmValueHandle countVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (destPtr, readSoFarSlot, resultSlot, stringRef, stdinHandle, bytesReadSlot) = slots;

        // Drain bytes already buffered by readLine (the shared stdin buffer) before touching the
        // fd, so readLine + readExact interleave correctly — e.g. Ashes.Rpc reads the header with
        // readLine then the body with readExact. Without this, readLine's read-ahead would be lost.
        LlvmTypeHandle reStdinBufType = LlvmApi.ArrayType2(state.I8, StdinReadBufSize);
        LlvmValueHandle reStdinBuf = ReadLineScratchGlobal(state, "__ashes_stdin_rbuf", reStdinBufType);
        LlvmValueHandle reStdinBufPtr = GetArrayElementPointer(state, reStdinBufType, reStdinBuf, LlvmApi.ConstInt(state.I64, 0, 0), "re_stdin_rbuf_ptr");
        LlvmValueHandle reStdinPosSlot = ReadLineScratchGlobal(state, "__ashes_stdin_rpos", state.I64);
        LlvmValueHandle reStdinLenSlot = ReadLineScratchGlobal(state, "__ashes_stdin_rlen", state.I64);
        LlvmValueHandle rePos = LlvmApi.BuildLoad2(builder, state.I64, reStdinPosSlot, "re_rpos");
        LlvmValueHandle reLen = LlvmApi.BuildLoad2(builder, state.I64, reStdinLenSlot, "re_rlen");
        LlvmValueHandle reAvail = LlvmApi.BuildSub(builder, reLen, rePos, "re_avail");
        LlvmValueHandle reAvailLtCount = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, reAvail, countVal, "re_avail_lt_count");
        LlvmValueHandle reToDrain = LlvmApi.BuildSelect(builder, reAvailLtCount, reAvail, countVal, "re_to_drain");
        LlvmValueHandle reSrcPtr = LlvmApi.BuildGEP2(builder, state.I8, reStdinBufPtr, [rePos], "re_drain_src");
        EmitCopyBytes(state, destPtr, reSrcPtr, reToDrain, "re_drain_copy");
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, rePos, reToDrain, "re_rpos_next"), reStdinPosSlot);
        LlvmApi.BuildStore(builder, reToDrain, readSoFarSlot);
    }

    private static EmitReadExactBlocks EmitReadExactCreateBlocks(LlvmCodegenState state)
    {
        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "re_loop");
        var readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "re_read");
        var errorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "re_error");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "re_done");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "re_continue");
        return new EmitReadExactBlocks(loopBlock, readBlock, errorBlock, doneBlock, continueBlock);
    }

    private static void EmitReadExactLoop(LlvmCodegenState state, EmitReadExactSlots slots, EmitReadExactBlocks blocks, LlvmValueHandle countVal)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (destPtr, readSoFarSlot, resultSlot, stringRef, stdinHandle, bytesReadSlot) = slots;
        var (loopBlock, readBlock, errorBlock, doneBlock, continueBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle readSoFar = LlvmApi.BuildLoad2(builder, state.I64, readSoFarSlot, "re_read_so_far_val");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, readSoFar, countVal, "re_done_cond");
        LlvmApi.BuildCondBr(builder, done, doneBlock, readBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle cursorPtr = LlvmApi.BuildGEP2(builder, state.I8, destPtr, [readSoFar], "re_cursor_ptr");
        LlvmValueHandle remaining = LlvmApi.BuildSub(builder, countVal, readSoFar, "re_remaining");
        LlvmValueHandle nRead;
        if (IsLinuxFlavor(state.Flavor))
        {
            nRead = EmitLinuxSyscall(state, SyscallRead, LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.BuildPtrToInt(builder, cursorPtr, state.I64, "re_cursor_int"), remaining, "re_read_call");
        }
        else
        {
            // EmitWindowsReadFile returns a success flag (i1); the actual byte count
            // is written to bytesReadSlot. Treat a failed read (or a zero-byte read at
            // EOF) as a non-positive count so the loop reports unexpected EOF.
            LlvmValueHandle readOk = EmitWindowsReadFile(state, stdinHandle, cursorPtr,
                LlvmApi.BuildTrunc(builder, remaining, state.I32, "re_remaining_i32"), bytesReadSlot, "re_read_call");
            LlvmValueHandle bytesRead = LlvmApi.BuildZExt(builder,
                LlvmApi.BuildLoad2(builder, state.I32, bytesReadSlot, "re_bytes_read_val"), state.I64, "re_bytes_read_i64");
            nRead = LlvmApi.BuildSelect(builder, readOk, bytesRead, LlvmApi.ConstInt(state.I64, 0, 0), "re_read_i64");
        }

        LlvmValueHandle readFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, nRead, LlvmApi.ConstInt(state.I64, 0, 0), "re_read_failed");
        LlvmApi.BuildCondBr(builder, readFailed, errorBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        LlvmValueHandle newReadSoFar = LlvmApi.BuildAdd(builder, readSoFar, nRead, "re_new_read_so_far");
        LlvmApi.BuildStore(builder, newReadSoFar, readSoFarSlot);
        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, errorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitStackStringObject(state, "readExact: unexpected EOF")), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);
    }

    private static LlvmValueHandle EmitReadExactTail(LlvmCodegenState state, EmitReadExactSlots slots, EmitReadExactBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        var (destPtr, readSoFarSlot, resultSlot, stringRef, stdinHandle, bytesReadSlot) = slots;
        var (loopBlock, readBlock, errorBlock, doneBlock, continueBlock) = blocks;

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmValueHandle resultAtDone = LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "re_result_at_done");
        LlvmValueHandle isError = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resultAtDone, LlvmApi.ConstInt(state.I64, 0, 0), "re_is_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "re_return_error");
        var returnOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "re_return_ok");
        var finalBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "re_final");
        LlvmApi.BuildCondBr(builder, isError, returnErrorBlock, returnOkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildBr(builder, finalBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnOkBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, stringRef), resultSlot);
        LlvmApi.BuildBr(builder, finalBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finalBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "re_final_result");
    }
}
