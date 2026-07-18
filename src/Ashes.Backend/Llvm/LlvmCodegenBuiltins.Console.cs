using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    // Kernel termios layout (identical on x86-64 and AArch64): four u32 flag words, one
    // control byte, then c_cc[19]. Raw mode clears the canonical/echo/signal input
    // processing while leaving output processing (OPOST) untouched so "\n" still renders
    // as a newline.
    private const int TermiosSizeBytes = 36;
    private const int TermiosIflagOffset = 0;
    private const int TermiosLflagOffset = 12;
    private const int TermiosVtimeOffset = 17 + 5;
    private const int TermiosVminOffset = 17 + 6;
    private const uint TermiosIflagRawClearMask = 0x5EB;   // IGNBRK|BRKINT|PARMRK|ISTRIP|INLCR|IGNCR|ICRNL|IXON
    private const uint TermiosLflagRawClearMask = 0x804B;  // ISIG|ICANON|ECHO|ECHONL|IEXTEN
    private const long TermiosTcgets = 0x5401;
    private const long TermiosTcsets = 0x5402;
    private const int ConsolePollBufSize = 4096;
    private const uint WindowsEnableProcessedInput = 0x1;
    private const uint WindowsEnableLineInput = 0x2;
    private const uint WindowsEnableEchoInput = 0x4;
    private const uint WindowsEnableQuickEditMode = 0x40;
    private const uint WindowsEnableExtendedFlags = 0x80;
    private const uint WindowsEnableVirtualTerminalInput = 0x200;
    private const uint WindowsEnableProcessedOutput = 0x1;
    private const uint WindowsEnableVirtualTerminalProcessing = 0x4;

    private static LlvmValueHandle EmitConsoleEnableRaw(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = ReadLineScratchGlobal(state, "__ashes_console_result", state.I64);
        var applyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_raw_apply");
        var okBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_raw_ok");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_raw_fail");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_raw_done");

        if (IsLinuxFlavor(state.Flavor))
        {
            EmitConsoleEnableRawLinux(state, applyBlock, okBlock, failBlock);
        }
        else
        {
            EmitConsoleEnableRawWindows(state, applyBlock, okBlock, failBlock);
        }

        LlvmApi.PositionBuilderAtEnd(builder, okBlock);
        LlvmValueHandle rawActiveFlag = ReadLineScratchGlobal(state, "__ashes_console_raw_active", state.I64);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), rawActiveFlag);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "console_raw_result");
    }

    private static void EmitConsoleEnableRawLinux(LlvmCodegenState state, LlvmBasicBlockHandle applyBlock, LlvmBasicBlockHandle okBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        (LlvmValueHandle savedPtr, LlvmValueHandle savedAddr) = ConsoleTermiosGlobal(state, "__ashes_console_saved_termios");
        (LlvmValueHandle workPtr, LlvmValueHandle workAddr) = ConsoleTermiosGlobal(state, "__ashes_console_work_termios");
        LlvmValueHandle getRet = EmitLinuxSyscall(state, SyscallIoctl,
            LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, TermiosTcgets, 0), savedAddr, "console_tcgets");
        LlvmValueHandle getFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, getRet, LlvmApi.ConstInt(state.I64, 0, 0), "console_tcgets_failed");
        LlvmApi.BuildCondBr(builder, getFailed, failBlock, applyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyBlock);
        EmitCopyBytes(state, workPtr, savedPtr, LlvmApi.ConstInt(state.I64, TermiosSizeBytes, 0), "console_termios_copy");
        EmitConsoleAndFlagWord(state, workPtr, TermiosIflagOffset, ~TermiosIflagRawClearMask, "console_iflag");
        EmitConsoleAndFlagWord(state, workPtr, TermiosLflagOffset, ~TermiosLflagRawClearMask, "console_lflag");
        EmitConsoleStoreByte(state, workPtr, TermiosVtimeOffset, 0, "console_vtime");
        EmitConsoleStoreByte(state, workPtr, TermiosVminOffset, 1, "console_vmin");
        LlvmValueHandle setRet = EmitLinuxSyscall(state, SyscallIoctl,
            LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, TermiosTcsets, 0), workAddr, "console_tcsets");
        LlvmValueHandle setFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, setRet, LlvmApi.ConstInt(state.I64, 0, 0), "console_tcsets_failed");
        LlvmApi.BuildCondBr(builder, setFailed, failBlock, okBlock);
    }

    private static void EmitConsoleEnableRawWindows(LlvmCodegenState state, LlvmBasicBlockHandle applyBlock, LlvmBasicBlockHandle okBlock, LlvmBasicBlockHandle failBlock)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "console_stdin_handle");
        LlvmValueHandle savedInSlot = ReadLineScratchGlobal(state, "__ashes_console_saved_mode_in", state.I32);
        LlvmValueHandle gotMode = EmitWindowsGetConsoleMode(state, stdinHandle, savedInSlot, "console_get_mode_in");
        LlvmValueHandle notConsole = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, gotMode, LlvmApi.ConstInt(state.I32, 0, 0), "console_stdin_not_console");
        LlvmApi.BuildCondBr(builder, notConsole, failBlock, applyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, applyBlock);
        LlvmValueHandle savedIn = LlvmApi.BuildLoad2(builder, state.I32, savedInSlot, "console_saved_mode_in_value");
        LlvmValueHandle cleared = LlvmApi.BuildAnd(builder, savedIn,
            LlvmApi.ConstInt(state.I32, ~(WindowsEnableProcessedInput | WindowsEnableLineInput | WindowsEnableEchoInput | WindowsEnableQuickEditMode), 0),
            "console_mode_in_cleared");
        LlvmValueHandle rawIn = LlvmApi.BuildOr(builder, cleared,
            LlvmApi.ConstInt(state.I32, WindowsEnableVirtualTerminalInput | WindowsEnableExtendedFlags, 0),
            "console_mode_in_raw");
        EmitWindowsSetConsoleMode(state, stdinHandle, rawIn, "console_set_mode_in");

        LlvmValueHandle stdoutHandle = EmitWindowsGetStdHandle(state, StdOutputHandle, "console_stdout_handle");
        LlvmValueHandle savedOutSlot = ReadLineScratchGlobal(state, "__ashes_console_saved_mode_out", state.I32);
        LlvmValueHandle outSavedFlag = ReadLineScratchGlobal(state, "__ashes_console_out_saved", state.I64);
        LlvmValueHandle gotOut = EmitWindowsGetConsoleMode(state, stdoutHandle, savedOutSlot, "console_get_mode_out");
        var outOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_raw_out_ok");
        LlvmValueHandle outIsConsole = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, gotOut, LlvmApi.ConstInt(state.I32, 0, 0), "console_stdout_is_console");
        LlvmApi.BuildCondBr(builder, outIsConsole, outOkBlock, okBlock);

        LlvmApi.PositionBuilderAtEnd(builder, outOkBlock);
        LlvmValueHandle savedOut = LlvmApi.BuildLoad2(builder, state.I32, savedOutSlot, "console_saved_mode_out_value");
        LlvmValueHandle vtOut = LlvmApi.BuildOr(builder, savedOut,
            LlvmApi.ConstInt(state.I32, WindowsEnableProcessedOutput | WindowsEnableVirtualTerminalProcessing, 0),
            "console_mode_out_vt");
        EmitWindowsSetConsoleMode(state, stdoutHandle, vtOut, "console_set_mode_out");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), outSavedFlag);
        LlvmApi.BuildBr(builder, okBlock);
    }

    private static LlvmValueHandle EmitConsoleRestore(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle rawActiveFlag = ReadLineScratchGlobal(state, "__ashes_console_raw_active", state.I64);
        var restoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_restore_apply");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_restore_done");
        LlvmValueHandle active = LlvmApi.BuildLoad2(builder, state.I64, rawActiveFlag, "console_raw_active_value");
        LlvmValueHandle isActive = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, active, LlvmApi.ConstInt(state.I64, 0, 0), "console_raw_is_active");
        LlvmApi.BuildCondBr(builder, isActive, restoreBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, restoreBlock);
        if (IsLinuxFlavor(state.Flavor))
        {
            (_, LlvmValueHandle savedAddr) = ConsoleTermiosGlobal(state, "__ashes_console_saved_termios");
            EmitLinuxSyscall(state, SyscallIoctl,
                LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, TermiosTcsets, 0), savedAddr, "console_restore_tcsets");
        }
        else
        {
            EmitConsoleRestoreWindows(state);
        }

        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), rawActiveFlag);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        return LlvmApi.ConstInt(state.I64, 0, 0);
    }

    private static void EmitConsoleRestoreWindows(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "console_restore_stdin");
        LlvmValueHandle savedInSlot = ReadLineScratchGlobal(state, "__ashes_console_saved_mode_in", state.I32);
        LlvmValueHandle savedIn = LlvmApi.BuildLoad2(builder, state.I32, savedInSlot, "console_restore_mode_in");
        EmitWindowsSetConsoleMode(state, stdinHandle, savedIn, "console_restore_set_in");

        LlvmValueHandle outSavedFlag = ReadLineScratchGlobal(state, "__ashes_console_out_saved", state.I64);
        var outRestoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_restore_out");
        var outDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_restore_out_done");
        LlvmValueHandle outSaved = LlvmApi.BuildLoad2(builder, state.I64, outSavedFlag, "console_out_saved_value");
        LlvmValueHandle outWasSaved = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, outSaved, LlvmApi.ConstInt(state.I64, 0, 0), "console_out_was_saved");
        LlvmApi.BuildCondBr(builder, outWasSaved, outRestoreBlock, outDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, outRestoreBlock);
        LlvmValueHandle stdoutHandle = EmitWindowsGetStdHandle(state, StdOutputHandle, "console_restore_stdout");
        LlvmValueHandle savedOutSlot = ReadLineScratchGlobal(state, "__ashes_console_saved_mode_out", state.I32);
        LlvmValueHandle savedOut = LlvmApi.BuildLoad2(builder, state.I32, savedOutSlot, "console_restore_mode_out");
        EmitWindowsSetConsoleMode(state, stdoutHandle, savedOut, "console_restore_set_out");
        LlvmApi.BuildBr(builder, outDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, outDoneBlock);
    }

    private readonly record struct ConsolePollBlocks(
        LlvmBasicBlockHandle Read,
        LlvmBasicBlockHandle Some,
        LlvmBasicBlockHandle Empty,
        LlvmBasicBlockHandle None,
        LlvmBasicBlockHandle Done);

    private static LlvmValueHandle EmitConsolePoll(LlvmCodegenState state, LlvmValueHandle timeoutMs)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = ReadLineScratchGlobal(state, "__ashes_console_poll_result", state.I64);
        LlvmTypeHandle bufType = LlvmApi.ArrayType2(state.I8, ConsolePollBufSize);
        LlvmValueHandle buf = ReadLineScratchGlobal(state, "__ashes_console_poll_buf", bufType);
        LlvmValueHandle bufPtr = GetArrayElementPointer(state, bufType, buf, LlvmApi.ConstInt(state.I64, 0, 0), "console_poll_buf_ptr");
        LlvmValueHandle negativeTimeout = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, timeoutMs, LlvmApi.ConstInt(state.I64, 0, 0), "console_poll_timeout_negative");
        LlvmValueHandle clampedTimeout = LlvmApi.BuildSelect(builder, negativeTimeout, LlvmApi.ConstInt(state.I64, 0, 0), timeoutMs, "console_poll_timeout");

        var blocks = new ConsolePollBlocks(
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_poll_read"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_poll_some"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_poll_empty"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_poll_none"),
            LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_poll_done"));

        LlvmValueHandle bytesRead = IsLinuxFlavor(state.Flavor)
            ? EmitConsolePollWaitReadLinux(state, clampedTimeout, bufPtr, blocks)
            : EmitConsolePollWaitReadWindows(state, clampedTimeout, bufPtr, blocks);

        LlvmValueHandle gotBytes = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, bytesRead, LlvmApi.ConstInt(state.I64, 0, 0), "console_poll_got_bytes");
        LlvmApi.BuildCondBr(builder, gotBytes, blocks.Some, blocks.None);
        EmitConsolePollFinish(state, resultSlot, bufPtr, bytesRead, blocks);

        LlvmApi.PositionBuilderAtEnd(builder, blocks.Done);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "console_poll_result_value");
    }

    private static LlvmValueHandle EmitConsolePollWaitReadLinux(LlvmCodegenState state, LlvmValueHandle clampedTimeout, LlvmValueHandle bufPtr, ConsolePollBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle pollFdType = LlvmApi.ArrayType2(state.I8, 8);
        LlvmValueHandle pollFd = ReadLineScratchGlobal(state, "__ashes_console_pollfd", pollFdType);
        LlvmValueHandle pollFdPtr = GetArrayElementPointer(state, pollFdType, pollFd, LlvmApi.ConstInt(state.I64, 0, 0), "console_pollfd_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), pollFdPtr);
        LlvmValueHandle eventsPtr = LlvmApi.BuildGEP2(builder, state.I8, pollFdPtr, [LlvmApi.ConstInt(state.I64, 4, 0)], "console_pollfd_events_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 1, 0), eventsPtr);
        LlvmValueHandle reventsPtr = LlvmApi.BuildGEP2(builder, state.I8, pollFdPtr, [LlvmApi.ConstInt(state.I64, 6, 0)], "console_pollfd_revents_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 0, 0), reventsPtr);

        LlvmTypeHandle timespecType = LlvmApi.ArrayType2(state.I64, 2);
        LlvmValueHandle timespec = ReadLineScratchGlobal(state, "__ashes_console_poll_ts", timespecType);
        LlvmValueHandle timespecPtr = GetArrayElementPointer(state, timespecType, timespec, LlvmApi.ConstInt(state.I64, 0, 0), "console_poll_ts_ptr");
        LlvmValueHandle seconds = LlvmApi.BuildSDiv(builder, clampedTimeout, LlvmApi.ConstInt(state.I64, 1000, 0), "console_poll_ts_sec");
        LlvmValueHandle milliRemainder = LlvmApi.BuildSub(builder, clampedTimeout,
            LlvmApi.BuildMul(builder, seconds, LlvmApi.ConstInt(state.I64, 1000, 0), "console_poll_ts_sec_ms"), "console_poll_ts_rem");
        LlvmValueHandle nanos = LlvmApi.BuildMul(builder, milliRemainder, LlvmApi.ConstInt(state.I64, 1000000, 0), "console_poll_ts_nsec");
        LlvmApi.BuildStore(builder, seconds, timespecPtr);
        LlvmValueHandle nsecPtr = LlvmApi.BuildGEP2(builder, state.I8, timespecPtr, [LlvmApi.ConstInt(state.I64, 8, 0)], "console_poll_ts_nsec_ptr");
        LlvmApi.BuildStore(builder, nanos, nsecPtr);

        LlvmValueHandle pollRet = EmitLinuxSyscall6(state, SyscallPpoll,
            LlvmApi.BuildPtrToInt(builder, pollFdPtr, state.I64, "console_pollfd_addr"),
            LlvmApi.ConstInt(state.I64, 1, 0),
            LlvmApi.BuildPtrToInt(builder, timespecPtr, state.I64, "console_poll_ts_addr"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 8, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "console_ppoll");
        LlvmValueHandle readable = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, pollRet, LlvmApi.ConstInt(state.I64, 0, 0), "console_poll_readable");
        LlvmApi.BuildCondBr(builder, readable, blocks.Read, blocks.Empty);

        LlvmApi.PositionBuilderAtEnd(builder, blocks.Read);
        return EmitLinuxSyscall(state, SyscallRead,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.BuildPtrToInt(builder, bufPtr, state.I64, "console_poll_buf_addr"),
            LlvmApi.ConstInt(state.I64, ConsolePollBufSize, 0),
            "console_poll_read_bytes");
    }

    private static LlvmValueHandle EmitConsolePollWaitReadWindows(LlvmCodegenState state, LlvmValueHandle clampedTimeout, LlvmValueHandle bufPtr, ConsolePollBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "console_poll_stdin");
        LlvmValueHandle probeSlot = ReadLineScratchGlobal(state, "__ashes_console_poll_mode_probe", state.I32);
        LlvmValueHandle isConsoleRet = EmitWindowsGetConsoleMode(state, stdinHandle, probeSlot, "console_poll_probe");
        var waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "console_poll_wait");
        LlvmValueHandle isConsole = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, isConsoleRet, LlvmApi.ConstInt(state.I32, 0, 0), "console_poll_is_console");
        LlvmApi.BuildCondBr(builder, isConsole, waitBlock, blocks.Read);

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        LlvmTypeHandle waitType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32]);
        LlvmValueHandle waitPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWaitForSingleObjectImport,
            "console_poll_wait_fn_ptr");
        LlvmValueHandle waitRet = LlvmApi.BuildCall2(builder, waitType, waitPtr,
            [stdinHandle, LlvmApi.BuildTrunc(builder, clampedTimeout, state.I32, "console_poll_timeout_i32")],
            "console_poll_wait_ret");
        LlvmValueHandle signaled = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, waitRet, LlvmApi.ConstInt(state.I32, 0, 0), "console_poll_signaled");
        LlvmApi.BuildCondBr(builder, signaled, blocks.Read, blocks.Empty);

        LlvmApi.PositionBuilderAtEnd(builder, blocks.Read);
        LlvmValueHandle bytesReadSlot = ReadLineScratchGlobal(state, "__ashes_console_poll_bytes_read", state.I32);
        return EmitWindowsReadBlock(state, stdinHandle, bufPtr, LlvmApi.ConstInt(state.I32, ConsolePollBufSize, 0), bytesReadSlot);
    }

    private static void EmitConsolePollFinish(LlvmCodegenState state, LlvmValueHandle resultSlot, LlvmValueHandle bufPtr, LlvmValueHandle bytesRead, ConsolePollBlocks blocks)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmApi.PositionBuilderAtEnd(builder, blocks.Some);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, bytesRead, LlvmApi.ConstInt(state.I64, 8, 0), "console_poll_string_bytes"));
        StoreMemory(state, stringRef, 0, bytesRead, "console_poll_string_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, "console_poll_string_dest"), bufPtr, bytesRead, "console_poll_copy");
        LlvmValueHandle someRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someRef, 8, stringRef, "console_poll_some_value");
        LlvmApi.BuildStore(builder, someRef, resultSlot);
        LlvmApi.BuildBr(builder, blocks.Done);

        LlvmApi.PositionBuilderAtEnd(builder, blocks.Empty);
        LlvmValueHandle emptyStringRef = EmitAllocDynamic(state, LlvmApi.ConstInt(state.I64, 8, 0));
        StoreMemory(state, emptyStringRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "console_poll_empty_len");
        LlvmValueHandle someEmptyRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someEmptyRef, 8, emptyStringRef, "console_poll_some_empty");
        LlvmApi.BuildStore(builder, someEmptyRef, resultSlot);
        LlvmApi.BuildBr(builder, blocks.Done);

        LlvmApi.PositionBuilderAtEnd(builder, blocks.None);
        LlvmApi.BuildStore(builder, EmitAllocAdt(state, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, blocks.Done);
    }

    private static (LlvmValueHandle Ptr, LlvmValueHandle Addr) ConsoleTermiosGlobal(LlvmCodegenState state, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle termiosType = LlvmApi.ArrayType2(state.I8, TermiosSizeBytes);
        LlvmValueHandle global = ReadLineScratchGlobal(state, name, termiosType);
        LlvmValueHandle ptr = GetArrayElementPointer(state, termiosType, global, LlvmApi.ConstInt(state.I64, 0, 0), name + "_ptr");
        LlvmValueHandle addr = LlvmApi.BuildPtrToInt(builder, ptr, state.I64, name + "_addr");
        return (ptr, addr);
    }

    private static void EmitConsoleAndFlagWord(LlvmCodegenState state, LlvmValueHandle termiosPtr, int offsetBytes, uint mask, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle wordPtr = LlvmApi.BuildGEP2(builder, state.I8, termiosPtr, [LlvmApi.ConstInt(state.I64, (ulong)offsetBytes, 0)], name + "_ptr");
        LlvmValueHandle word = LlvmApi.BuildLoad2(builder, state.I32, wordPtr, name + "_value");
        LlvmValueHandle masked = LlvmApi.BuildAnd(builder, word, LlvmApi.ConstInt(state.I32, mask, 0), name + "_masked");
        LlvmApi.BuildStore(builder, masked, wordPtr);
    }

    private static void EmitConsoleStoreByte(LlvmCodegenState state, LlvmValueHandle termiosPtr, int offsetBytes, byte value, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle bytePtr = LlvmApi.BuildGEP2(builder, state.I8, termiosPtr, [LlvmApi.ConstInt(state.I64, (ulong)offsetBytes, 0)], name + "_ptr");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, value, 0), bytePtr);
    }

    private static LlvmValueHandle EmitWindowsGetConsoleMode(LlvmCodegenState state, LlvmValueHandle handle, LlvmValueHandle modeSlot, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle getConsoleModeType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32Ptr]);
        LlvmValueHandle getConsoleModePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            LlvmApi.GetNamedGlobal(state.Target.Module, "__imp_GetConsoleMode"),
            name + "_fn_ptr");
        return LlvmApi.BuildCall2(builder, getConsoleModeType, getConsoleModePtr, [handle, modeSlot], name);
    }

    private static void EmitWindowsSetConsoleMode(LlvmCodegenState state, LlvmValueHandle handle, LlvmValueHandle mode, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle setConsoleModeType = LlvmApi.FunctionType(state.I32, [state.I64, state.I32]);
        LlvmValueHandle setConsoleModePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            LlvmApi.GetNamedGlobal(state.Target.Module, "__imp_SetConsoleMode"),
            name + "_fn_ptr");
        LlvmApi.BuildCall2(builder, setConsoleModeType, setConsoleModePtr, [handle, mode], name);
    }
}
