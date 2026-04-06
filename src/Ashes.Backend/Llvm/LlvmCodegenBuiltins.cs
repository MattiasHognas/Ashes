using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LlvmValueHandle EmitReadLine(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle inputBufType = LlvmApi.ArrayType2(state.I8, InputBufSize);
        LlvmValueHandle inputBuf = LlvmApi.BuildAlloca(builder, inputBufType, "read_line_buf");
        LlvmValueHandle inputBufPtr = GetArrayElementPointer(state, inputBufType, inputBuf, LlvmApi.ConstInt(state.I64, 0, 0), "read_line_buf_ptr");
        LlvmValueHandle byteSlot = LlvmApi.BuildAlloca(builder, state.I8, "read_line_byte");
        LlvmValueHandle lenSlot = LlvmApi.BuildAlloca(builder, state.I64, "read_line_len");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "read_line_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), lenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        LlvmValueHandle stdinHandle = default;
        LlvmValueHandle bytesReadSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.WindowsX64)
        {
            stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "stdin_handle");
            bytesReadSlot = LlvmApi.BuildAlloca(builder, state.I32, "read_line_bytes_read");
        }

        var loopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_loop");
        var inspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_inspect");
        var skipCrBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_skip_cr");
        var storeByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_store_byte");
        var appendByteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_append_byte");
        var eofBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_eof");
        var finishSomeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_finish_some");
        var returnNoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_return_none");
        var overflowBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_overflow");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "read_line_continue");

        LlvmApi.BuildBr(builder, loopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBlock);
        LlvmValueHandle bytesRead = IsLinuxFlavor(state.Flavor)
            ? EmitLinuxSyscall(
                state,
                SyscallRead,
                LlvmApi.ConstInt(state.I64, 0, 0),
                LlvmApi.BuildPtrToInt(builder, byteSlot, state.I64, "read_line_byte_ptr"),
                LlvmApi.ConstInt(state.I64, 1, 0),
                "sys_read_line")
            : EmitWindowsReadByte(state, stdinHandle, byteSlot, bytesReadSlot);
        LlvmValueHandle hasByte = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, bytesRead, LlvmApi.ConstInt(state.I64, 0, 0), "read_line_has_byte");
        LlvmApi.BuildCondBr(builder, hasByte, inspectBlock, eofBlock);

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
        LlvmValueHandle destPtr = LlvmApi.BuildGEP2(builder, state.I8, inputBufPtr, new[] { currentLen }, "read_line_dest_ptr");
        LlvmApi.BuildStore(builder, currentByte, destPtr);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, currentLen, LlvmApi.ConstInt(state.I64, 1, 0), "read_line_len_next"), lenSlot);
        LlvmApi.BuildBr(builder, loopBlock);

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

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "read_line_result_value");
    }

    private static LlvmValueHandle EmitFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileReadText(state, pathRef)
            : EmitWindowsFileReadText(state, pathRef);
    }

    private static LlvmValueHandle EmitFileWriteText(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle textRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileWriteText(state, pathRef, textRef)
            : EmitWindowsFileWriteText(state, pathRef, textRef);
    }

    private static LlvmValueHandle EmitFileExists(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxFileExists(state, pathRef)
            : EmitWindowsFileExists(state, pathRef);
    }

    private static LlvmValueHandle EmitLinuxFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_read_path");
        LlvmValueHandle fdSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_fd");
        LlvmValueHandle stringSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_string");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_cursor");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), fdSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_open");
        var seekEndBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_seek_end");
        var seekStartBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_seek_start");
        var allocBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_alloc");
        var readCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_loop_check");
        var readBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_loop_body");
        var readDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_done");
        var utf8CheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_utf8_check");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_close_ok");
        var closeInvalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_close_invalid");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_close_error");
        var maybeCloseErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_maybe_close_error");
        var closeHandleBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_close_handle");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(
            state,
            SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, "fs_read_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_open_call");
        LlvmApi.BuildStore(builder, fd, fdSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_open_failed");
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, seekEndBlock);

        LlvmApi.PositionBuilderAtEnd(builder, seekEndBlock);
        LlvmValueHandle fileLength = EmitLinuxSyscall(
            state,
            SyscallLseek,
            fd,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 2, 0),
            "fs_read_seek_end_call");
        LlvmValueHandle seekEndFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fileLength, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_seek_end_failed");
        LlvmApi.BuildCondBr(builder, seekEndFailed, maybeCloseErrorBlock, seekStartBlock);

        LlvmApi.PositionBuilderAtEnd(builder, seekStartBlock);
        LlvmValueHandle seekStart = EmitLinuxSyscall(
            state,
            SyscallLseek,
            fd,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_seek_start_call");
        LlvmValueHandle seekStartFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, seekStart, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_seek_start_failed");
        LlvmApi.BuildCondBr(builder, seekStartFailed, maybeCloseErrorBlock, allocBlock);

        LlvmApi.PositionBuilderAtEnd(builder, allocBlock);
        LlvmValueHandle exceedsLimit = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, fileLength, LlvmApi.ConstInt(state.I64, MaxFileReadBytes, 0), "fs_read_exceeds_limit");
        var withinLimitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_within_limit");
        LlvmApi.BuildCondBr(builder, exceedsLimit, maybeCloseErrorBlock, withinLimitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, withinLimitBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, fileLength, LlvmApi.ConstInt(state.I64, 8, 0), "fs_read_total_bytes"));
        StoreMemory(state, stringRef, 0, fileLength, "fs_read_len");
        LlvmApi.BuildStore(builder, stringRef, stringSlot);
        LlvmApi.BuildStore(builder, fileLength, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, stringRef, "fs_read_cursor_start"), cursorSlot);
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, fileLength, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_empty");
        LlvmApi.BuildCondBr(builder, isEmpty, utf8CheckBlock, readCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "fs_read_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_done");
        LlvmApi.BuildCondBr(builder, done, utf8CheckBlock, readBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBodyBlock);
        LlvmValueHandle cursorAddress = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_read_cursor_value");
        LlvmValueHandle readBytes = EmitLinuxSyscall(
            state,
            SyscallRead,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_fd_value"),
            cursorAddress,
            remaining,
            "fs_read_read_call");
        LlvmValueHandle readFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, readBytes, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_failed");
        LlvmApi.BuildCondBr(builder, readFailed, maybeCloseErrorBlock, readDoneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readDoneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, readBytes, "fs_read_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursorAddress, readBytes, "fs_read_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, readCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, utf8CheckBlock);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(
            state,
            GetStringBytesPointer(state, LlvmApi.BuildLoad2(builder, state.I64, stringSlot, "fs_read_string_value"), "fs_read_utf8_ptr"),
            LoadStringLength(state, LlvmApi.BuildLoad2(builder, state.I64, stringSlot, "fs_read_string_len_value"), "fs_read_utf8_len"),
            "fs_read_utf8");
        LlvmValueHandle isUtf8Valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_is_utf8_valid");
        LlvmApi.BuildCondBr(builder, isUtf8Valid, closeOkBlock, closeInvalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_close_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_close_ok_call");
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, stringSlot, "fs_read_ok_value")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeInvalidBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_invalid_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_close_invalid_call");
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeCloseErrorBlock);
        LlvmValueHandle fdValue = LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_error_fd");
        LlvmValueHandle shouldClose = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, fdValue, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_should_close");
        LlvmApi.BuildCondBr(builder, shouldClose, closeHandleBlock, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeHandleBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_read_close_error_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_read_close_error_call");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_read_result_value");
    }

    private static LlvmValueHandle EmitLinuxFileWriteText(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_write_path");
        LlvmValueHandle fdSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_fd");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_cursor");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), fdSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_open");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_loop_body");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_advance");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_close_ok");
        var maybeCloseErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_maybe_close_error");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_close_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(
            state,
            SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, "fs_write_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0x241, 0),
            LlvmApi.ConstInt(state.I64, 420, 0),
            "fs_write_open_call");
        LlvmApi.BuildStore(builder, fd, fdSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_open_failed");
        LlvmApi.BuildStore(builder, LoadStringLength(state, textRef, "fs_write_text_len"), remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "fs_write_text_ptr"), cursorSlot);
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "fs_write_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_done");
        LlvmApi.BuildCondBr(builder, done, closeOkBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle cursorAddress = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_cursor_value");
        LlvmValueHandle bytesWritten = EmitLinuxSyscall(
            state,
            SyscallWrite,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_fd_value"),
            cursorAddress,
            remaining,
            "fs_write_write_call");
        LlvmValueHandle writeFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, bytesWritten, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_failed");
        LlvmApi.BuildCondBr(builder, writeFailed, maybeCloseErrorBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, bytesWritten, "fs_write_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursorAddress, bytesWritten, "fs_write_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_close_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_write_close_ok_call");
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeCloseErrorBlock);
        LlvmValueHandle fdValue = LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_error_fd");
        LlvmValueHandle shouldClose = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, fdValue, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_should_close");
        LlvmApi.BuildCondBr(builder, shouldClose, closeErrorBlock, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            LlvmApi.BuildLoad2(builder, state.I64, fdSlot, "fs_write_close_error_fd"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_write_close_error_call");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_write_result_value");
    }

    private static LlvmValueHandle EmitLinuxFileExists(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_exists_path");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_exists_result");
        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_open");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_found");
        var missingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_missing");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle fd = EmitLinuxSyscall(
            state,
            SyscallOpen,
            LlvmApi.BuildPtrToInt(builder, pathCstr, state.I64, "fs_exists_path_ptr"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_exists_open_call");
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, fd, LlvmApi.ConstInt(state.I64, 0, 0), "fs_exists_open_failed");
        LlvmApi.BuildCondBr(builder, openFailed, missingBlock, foundBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        EmitLinuxSyscall(
            state,
            SyscallClose,
            fd,
            LlvmApi.ConstInt(state.I64, 0, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "fs_exists_close_call");
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 1, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, missingBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_exists_result_value");
    }

    private static LlvmValueHandle EmitWindowsFileReadText(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_read_path");
        LlvmValueHandle handleSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_handle");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_read_result");
        LlvmValueHandle bytesReadSlot = LlvmApi.BuildAlloca(builder, state.I32, "fs_read_bytes_read");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), handleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_open");
        var readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_read");
        var utf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_utf8");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_close_ok");
        var closeInvalidBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_close_invalid");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_close_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_read_win_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            unchecked((int)0x80000000),
            1,
            3,
            "fs_read_create_file");
        LlvmApi.BuildStore(builder, handle, handleSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, handle, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "fs_read_handle_invalid");
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, readBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.ConstInt(state.I64, MaxFileReadBytes + 8, 0));
        StoreMemory(state, stringRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_win_len_init");
        LlvmValueHandle readSucceeded = EmitWindowsReadFile(
            state,
            LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_read_handle_value"),
            GetStringBytesPointer(state, stringRef, "fs_read_win_bytes"),
            LlvmApi.ConstInt(state.I32, MaxFileReadBytes, 0),
            bytesReadSlot,
            "fs_read_win_read_call");
        LlvmApi.BuildStore(builder, LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesReadSlot, "fs_read_bytes_read_value"), state.I64, "fs_read_bytes_i64"), GetMemoryPointer(state, stringRef, 0, "fs_read_win_len_ptr"));
        LlvmApi.BuildCondBr(builder, readSucceeded, utf8Block, closeErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, utf8Block);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(
            state,
            GetStringBytesPointer(state, stringRef, "fs_read_win_utf8_ptr"),
            LoadStringLength(state, stringRef, "fs_read_win_utf8_len"),
            "fs_read_win_utf8");
        LlvmValueHandle isUtf8Valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), "fs_read_win_is_utf8_valid");
        LlvmApi.BuildCondBr(builder, isUtf8Valid, closeOkBlock, closeInvalidBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_read_close_handle"), "fs_read_close_ok");
        LlvmApi.BuildStore(builder, EmitResultOk(state, stringRef), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeInvalidBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_read_invalid_handle"), "fs_read_close_invalid");
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_read_error_handle"), "fs_read_close_error");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_read_win_result_value");
    }

    private static LlvmValueHandle EmitWindowsFileWriteText(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_write_path");
        LlvmValueHandle handleSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_handle");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_cursor");
        LlvmValueHandle bytesWrittenSlot = LlvmApi.BuildAlloca(builder, state.I32, "fs_write_bytes_written");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_write_result");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), handleSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);

        var openBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_open");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_loop_body");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_advance");
        var closeOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_close_ok");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_close_error");
        var returnErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_return_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_continue");

        LlvmApi.BuildBr(builder, openBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openBlock);
        LlvmValueHandle handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            0x40000000,
            0,
            2,
            "fs_write_create_file");
        LlvmApi.BuildStore(builder, handle, handleSlot);
        LlvmApi.BuildStore(builder, LoadStringLength(state, textRef, "fs_write_win_text_len"), remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "fs_write_win_text_ptr"), cursorSlot);
        LlvmValueHandle openFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, handle, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "fs_write_handle_invalid");
        LlvmApi.BuildCondBr(builder, openFailed, returnErrorBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "fs_write_win_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_win_done");
        LlvmApi.BuildCondBr(builder, done, closeOkBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle chunkSize = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, uint.MaxValue, 0), "fs_write_win_chunk_gt"),
            LlvmApi.ConstInt(state.I64, uint.MaxValue, 0),
            remaining,
            "fs_write_win_chunk_size");
        LlvmValueHandle wrote = EmitWindowsWriteFile(
            state,
            LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_handle_value"),
            LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_cursor_value"), state.I8Ptr, "fs_write_cursor_ptr"),
            LlvmApi.BuildTrunc(builder, chunkSize, state.I32, "fs_write_chunk_i32"),
            bytesWrittenSlot,
            "fs_write_win_write_call");
        LlvmApi.BuildCondBr(builder, wrote, advanceBlock, closeErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmValueHandle bytesWritten = LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesWrittenSlot, "fs_write_bytes_written_value"), state.I64, "fs_write_bytes_written_i64");
        LlvmValueHandle wroteZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, bytesWritten, LlvmApi.ConstInt(state.I64, 0, 0), "fs_write_wrote_zero");
        var zeroWriteBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_zero");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_write_win_update");
        LlvmApi.BuildCondBr(builder, wroteZero, zeroWriteBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroWriteBlock);
        LlvmApi.BuildBr(builder, closeErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle cursorValue = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "fs_write_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, bytesWritten, "fs_write_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursorValue, bytesWritten, "fs_write_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeOkBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_close_handle"), "fs_write_close_ok");
        LlvmApi.BuildStore(builder, EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        EmitWindowsCloseHandle(state, LlvmApi.BuildLoad2(builder, state.I64, handleSlot, "fs_write_error_handle"), "fs_write_close_error");
        LlvmApi.BuildBr(builder, returnErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, returnErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_write_win_result_value");
    }

    private static LlvmValueHandle EmitWindowsFileExists(LlvmCodegenState state, LlvmValueHandle pathRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle pathCstr = EmitStringToCString(state, pathRef, "fs_exists_path");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "fs_exists_win_result");
        var checkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_win_check");
        var missingBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_win_missing");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_win_found");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "fs_exists_win_continue");

        LlvmApi.BuildBr(builder, checkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, checkBlock);
        LlvmValueHandle attrs = EmitWindowsGetFileAttributes(state, pathCstr, "fs_exists_get_attrs");
        LlvmValueHandle missing = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, attrs, LlvmApi.ConstInt(state.I32, uint.MaxValue, 0), "fs_exists_missing");
        LlvmApi.BuildCondBr(builder, missing, missingBlock, foundBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 1, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, missingBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "fs_exists_win_result_value");
    }

    private static LlvmValueHandle EmitTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpConnect(state, hostRef, port)
            : EmitWindowsTcpConnect(state, hostRef, port);
    }

    private static LlvmValueHandle EmitTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpSend(state, socket, textRef)
            : EmitWindowsTcpSend(state, socket, textRef);
    }

    private static LlvmValueHandle EmitTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpReceive(state, socket, maxBytes)
            : EmitWindowsTcpReceive(state, socket, maxBytes);
    }

    private static LlvmValueHandle EmitTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        return IsLinuxFlavor(state.Flavor)
            ? EmitLinuxTcpClose(state, socket)
            : EmitWindowsTcpClose(state, socket);
    }

    /// <summary>
    /// Emits a Drop operation for deterministic resource cleanup.
    /// Routes to the appropriate runtime close function based on resource type.
    /// The result of the close call is discarded (Drop is fire-and-forget).
    /// Returns false because Drop does not terminate the current basic block.
    /// </summary>
    private static bool EmitDrop(LlvmCodegenState state, LlvmValueHandle resourceValue, string resourceTypeName)
    {
        switch (resourceTypeName)
        {
            case "Socket":
                // Drop a socket by calling the platform-specific TCP close.
                // The result (Result[Unit, Str]) is discarded — the compiler
                // guarantees exactly-once semantics.
                EmitTcpClose(state, resourceValue);
                return false;

            default:
                throw new InvalidOperationException(
                    $"Unhandled resource type '{resourceTypeName}' in Drop instruction. " +
                    "Add a case to EmitDrop when introducing new resource types.");
        }
    }

    private static LlvmValueHandle EmitHttpRequest(LlvmCodegenState state, LlvmValueHandle urlRef, LlvmValueHandle bodyRef, bool hasBody)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_result");
        LlvmValueHandle hostSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host");
        LlvmValueHandle pathSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path");
        LlvmValueHandle portSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port");
        LlvmValueHandle responseSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_response");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_socket");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_index");
        LlvmValueHandle hostStartSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host_start");
        LlvmValueHandle hostEndSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_host_end");
        LlvmValueHandle pathStartSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path_start");
        LlvmValueHandle pathLenSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_path_len");
        LlvmValueHandle portValueSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port_value");
        LlvmValueHandle portDigitsSlot = LlvmApi.BuildAlloca(builder, state.I64, "http_port_digits");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), responseSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), socketSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 7, 0), hostStartSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), hostEndSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathStartSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), pathLenSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 80, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portDigitsSlot);

        LlvmValueHandle urlLen = LoadStringLength(state, urlRef, "http_url_len");
        LlvmValueHandle urlBytes = GetStringBytesPointer(state, urlRef, "http_url_bytes");

        var httpsCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_https_check");
        var httpCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_http_check");
        var scanHostSetupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_scan_host_setup");
        var scanHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_scan_host");
        var parsePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port");
        var parsePortLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_loop");
        var parsePortInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_inspect");
        var havePathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_have_path");
        var defaultPathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_default_path");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_connect");
        var sendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_send");
        var recvLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_loop");
        var recvInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_inspect");
        var recvDoneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_done");
        var parseResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_response");
        var httpsErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_https_error");
        var closeErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_close_error");
        var malformedResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_malformed_response");
        var chunkedErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_chunked_error");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_continue");

        LlvmApi.BuildBr(builder, httpsCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsCheckBlock);
        LlvmValueHandle httpsPrefix = EmitHeapStringLiteral(state, "https://");
        LlvmValueHandle isHttps = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitStartsWith(state, urlRef, httpsPrefix, "http_is_https"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "http_is_https_bool");
        LlvmApi.BuildCondBr(builder, isHttps, httpsErrorBlock, httpCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpCheckBlock);
        LlvmValueHandle httpPrefix = EmitHeapStringLiteral(state, "http://");
        LlvmValueHandle isHttp = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Ne,
            EmitStartsWith(state, urlRef, httpPrefix, "http_is_http"),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "http_is_http_bool");
        var malformedUrlBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_malformed_url");
        LlvmApi.BuildCondBr(builder, isHttp, scanHostSetupBlock, malformedUrlBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedUrlBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedUrlMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanHostSetupBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 7, 0), indexSlot);
        LlvmApi.BuildBr(builder, scanHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, scanHostBlock);
        LlvmValueHandle hostLoopIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_loop_index");
        LlvmValueHandle hostLoopDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, hostLoopIndex, urlLen, "http_host_loop_done");
        var hostInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_inspect");
        LlvmApi.BuildCondBr(builder, hostLoopDone, defaultPathBlock, hostInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostInspectBlock);
        LlvmValueHandle hostByte = LoadByteAt(state, urlBytes, hostLoopIndex, "http_host_byte");
        LlvmValueHandle isColon = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)':', 0), "http_host_is_colon");
        var hostCheckSlashBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_check_slash");
        LlvmApi.BuildCondBr(builder, isColon, parsePortBlock, hostCheckSlashBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostCheckSlashBlock);
        LlvmValueHandle isSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'/', 0), "http_host_is_slash");
        var hostRejectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_reject");
        var hostAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_advance");
        LlvmApi.BuildCondBr(builder, isSlash, defaultPathBlock, hostRejectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostRejectBlock);
        LlvmValueHandle isQuestion = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'?', 0), "http_host_is_question");
        var hostHashCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_host_hash_check");
        LlvmApi.BuildCondBr(builder, isQuestion, malformedUrlBlock, hostHashCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostHashCheckBlock);
        LlvmValueHandle isHash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostByte, LlvmApi.ConstInt(state.I8, (byte)'#', 0), "http_host_is_hash");
        LlvmApi.BuildCondBr(builder, isHash, malformedUrlBlock, hostAdvanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, hostAdvanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, hostLoopIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_host_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, scanHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortBlock);
        LlvmValueHandle hostEnd = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_end");
        LlvmValueHandle hostLenValue = LlvmApi.BuildSub(builder, hostEnd, LlvmApi.ConstInt(state.I64, 7, 0), "http_host_len_before_port");
        LlvmValueHandle missingHost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostLenValue, LlvmApi.ConstInt(state.I64, 0, 0), "http_missing_host");
        var parsePortSetupBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_port_setup");
        LlvmApi.BuildCondBr(builder, missingHost, malformedUrlBlock, parsePortSetupBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortSetupBlock);
        LlvmApi.BuildStore(builder, hostEnd, hostEndSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), portDigitsSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, hostEnd, LlvmApi.ConstInt(state.I64, 1, 0), "http_port_index_start"), indexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortLoopBlock);
        LlvmValueHandle portIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_port_index");
        LlvmValueHandle portDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, portIndex, urlLen, "http_port_done");
        LlvmApi.BuildCondBr(builder, portDone, defaultPathBlock, parsePortInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parsePortInspectBlock);
        LlvmValueHandle portByte = LoadByteAt(state, urlBytes, portIndex, "http_port_byte");
        LlvmValueHandle portIsSlash = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, portByte, LlvmApi.ConstInt(state.I8, (byte)'/', 0), "http_port_is_slash");
        var portDigitCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_port_digit_check");
        LlvmApi.BuildCondBr(builder, portIsSlash, defaultPathBlock, portDigitCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, portDigitCheckBlock);
        LlvmValueHandle portDigitValue = LlvmApi.BuildZExt(builder, portByte, state.I64, "http_port_digit_value");
        LlvmValueHandle portIsDigit = BuildByteRangeCheck(state, portDigitValue, (byte)'0', (byte)'9', "http_port_digit_range");
        var portAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_port_advance");
        LlvmApi.BuildCondBr(builder, portIsDigit, portAdvanceBlock, malformedUrlBlock);

        LlvmApi.PositionBuilderAtEnd(builder, portAdvanceBlock);
        LlvmValueHandle currentPort = LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, "http_port_current");
        LlvmValueHandle parsedDigit = LlvmApi.BuildSub(builder, portDigitValue, LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_parsed_digit");
        LlvmValueHandle nextPort = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, currentPort, LlvmApi.ConstInt(state.I64, 10, 0), "http_port_mul"), parsedDigit, "http_port_next");
        LlvmValueHandle tooLargePort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, nextPort, LlvmApi.ConstInt(state.I64, 65535, 0), "http_port_too_large");
        var storePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_port");
        LlvmApi.BuildCondBr(builder, tooLargePort, malformedUrlBlock, storePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storePortBlock);
        LlvmApi.BuildStore(builder, nextPort, portValueSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, LlvmApi.BuildLoad2(builder, state.I64, portDigitsSlot, "http_port_digits_value"), LlvmApi.ConstInt(state.I64, 1, 0), "http_port_digits_next"), portDigitsSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, portIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_port_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, parsePortLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, defaultPathBlock);
        LlvmValueHandle finalHostEnd = LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_final_host_end");
        LlvmValueHandle hostEndUnset = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, finalHostEnd, LlvmApi.ConstInt(state.I64, 0, 0), "http_host_end_unset");
        var setHostEndBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_set_host_end");
        var buildHostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_build_host");
        LlvmApi.BuildCondBr(builder, hostEndUnset, setHostEndBlock, buildHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, setHostEndBlock);
        LlvmValueHandle currentIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_current_index");
        LlvmValueHandle hostLenAtEnd = LlvmApi.BuildSub(builder, currentIndex, LlvmApi.ConstInt(state.I64, 7, 0), "http_host_len_at_end");
        LlvmValueHandle noHost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, hostLenAtEnd, LlvmApi.ConstInt(state.I64, 0, 0), "http_no_host");
        LlvmApi.BuildCondBr(builder, noHost, malformedUrlBlock, buildHostBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildHostBlock);
        LlvmValueHandle actualHostEnd = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_host_end_existing"), LlvmApi.ConstInt(state.I64, 0, 0), "http_host_end_is_zero"),
            LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_host_end_from_index"),
            LlvmApi.BuildLoad2(builder, state.I64, hostEndSlot, "http_host_end_final"),
            "http_actual_host_end");
        LlvmValueHandle actualHostLen = LlvmApi.BuildSub(builder, actualHostEnd, LlvmApi.ConstInt(state.I64, 7, 0), "http_actual_host_len");
        LlvmValueHandle hostPtr = LlvmApi.BuildGEP2(builder, state.I8, urlBytes, new[] { LlvmApi.ConstInt(state.I64, 7, 0) }, "http_host_ptr");
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, hostPtr, actualHostLen, "http_host"), hostSlot);
        LlvmValueHandle digitsCount = LlvmApi.BuildLoad2(builder, state.I64, portDigitsSlot, "http_digits_count");
        LlvmValueHandle hasPortDigits = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, digitsCount, LlvmApi.ConstInt(state.I64, 0, 0), "http_has_port_digits");
        var storeParsedPortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_parsed_port");
        LlvmApi.BuildCondBr(builder, hasPortDigits, storeParsedPortBlock, havePathBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeParsedPortBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, portValueSlot, "http_port_value_final"), portSlot);
        LlvmApi.BuildBr(builder, havePathBlock);

        LlvmApi.PositionBuilderAtEnd(builder, havePathBlock);
        LlvmValueHandle pathIndex = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "http_path_index");
        LlvmValueHandle hasExplicitPath = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, pathIndex, urlLen, "http_has_explicit_path");
        var explicitPathBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_explicit_path");
        var defaultPathStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_default_path_store");
        LlvmApi.BuildCondBr(builder, hasExplicitPath, explicitPathBlock, defaultPathStoreBlock);

        LlvmApi.PositionBuilderAtEnd(builder, explicitPathBlock);
        LlvmValueHandle explicitPathPtr = LlvmApi.BuildGEP2(builder, state.I8, urlBytes, new[] { pathIndex }, "http_explicit_path_ptr");
        LlvmValueHandle explicitPathLen = LlvmApi.BuildSub(builder, urlLen, pathIndex, "http_explicit_path_len");
        LlvmApi.BuildStore(builder, EmitHeapStringSliceFromBytesPointer(state, explicitPathPtr, explicitPathLen, "http_path"), pathSlot);
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, defaultPathStoreBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, "/"), pathSlot);
        LlvmApi.BuildBr(builder, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmValueHandle connectResult = EmitTcpConnect(state, LlvmApi.BuildLoad2(builder, state.I64, hostSlot, "http_host_value"), LlvmApi.BuildLoad2(builder, state.I64, portSlot, "http_port_value"));
        LlvmValueHandle connectTag = LoadMemory(state, connectResult, 0, "http_connect_tag");
        LlvmValueHandle connectFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, connectTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_connect_failed");
        var connectStoreBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_connect_store");
        LlvmApi.BuildCondBr(builder, connectFailed, connectStoreBlock, sendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectStoreBlock);
        LlvmApi.BuildStore(builder, connectResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendBlock);
        LlvmValueHandle socketValue = LoadMemory(state, connectResult, 8, "http_socket_value");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle requestRef = EmitHttpRequestString(state, LlvmApi.BuildLoad2(builder, state.I64, pathSlot, "http_path_value"), LlvmApi.BuildLoad2(builder, state.I64, hostSlot, "http_host_header_value"), bodyRef, hasBody);
        LlvmValueHandle sendResult = EmitTcpSend(state, socketValue, requestRef);
        LlvmValueHandle sendTag = LoadMemory(state, sendResult, 0, "http_send_tag");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, sendTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_send_failed");
        var sendErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_send_error");
        LlvmApi.BuildCondBr(builder, sendFailed, sendErrorBlock, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, sendErrorBlock);
        EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_send_error_socket"));
        LlvmApi.BuildStore(builder, sendResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvLoopBlock);
        LlvmValueHandle recvResult = EmitTcpReceive(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_recv_socket"), LlvmApi.ConstInt(state.I64, 65536, 0));
        LlvmValueHandle recvTag = LoadMemory(state, recvResult, 0, "http_recv_tag");
        LlvmValueHandle recvFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, recvTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_recv_failed");
        var recvErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_error");
        LlvmApi.BuildCondBr(builder, recvFailed, recvErrorBlock, recvInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvErrorBlock);
        EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_recv_error_socket"));
        LlvmApi.BuildStore(builder, recvResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvInspectBlock);
        LlvmValueHandle chunkRef = LoadMemory(state, recvResult, 8, "http_chunk_ref");
        LlvmValueHandle chunkLen = LoadStringLength(state, chunkRef, "http_chunk_len");
        LlvmValueHandle chunkEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, chunkLen, LlvmApi.ConstInt(state.I64, 0, 0), "http_chunk_empty");
        var recvAppendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_recv_append");
        LlvmApi.BuildCondBr(builder, chunkEmpty, recvDoneBlock, recvAppendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvAppendBlock);
        LlvmValueHandle currentResponse = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_current_response");
        LlvmValueHandle hasResponse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, currentResponse, LlvmApi.ConstInt(state.I64, 0, 0), "http_has_response");
        var concatResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_concat_response");
        var storeFirstChunkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_store_first_chunk");
        LlvmApi.BuildCondBr(builder, hasResponse, concatResponseBlock, storeFirstChunkBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeFirstChunkBlock);
        LlvmApi.BuildStore(builder, chunkRef, responseSlot);
        LlvmApi.BuildBr(builder, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, concatResponseBlock);
        LlvmApi.BuildStore(builder, EmitStringConcat(state, currentResponse, chunkRef), responseSlot);
        LlvmApi.BuildBr(builder, recvLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, recvDoneBlock);
        LlvmValueHandle closeResult = EmitTcpClose(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "http_close_socket"));
        LlvmValueHandle closeTag = LoadMemory(state, closeResult, 0, "http_close_tag");
        LlvmValueHandle closeFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, closeTag, LlvmApi.ConstInt(state.I64, 0, 0), "http_close_failed");
        LlvmApi.BuildCondBr(builder, closeFailed, closeErrorBlock, parseResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseResponseBlock);
        LlvmValueHandle responseRef = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_response_value");
        LlvmValueHandle emptyResponse = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, responseRef, LlvmApi.ConstInt(state.I64, 0, 0), "http_empty_response");
        var ensureEmptyResponseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_ensure_empty_response");
        var parseResponseContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_response_continue");
        LlvmApi.BuildCondBr(builder, emptyResponse, ensureEmptyResponseBlock, parseResponseContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, ensureEmptyResponseBlock);
        LlvmApi.BuildStore(builder, EmitHeapStringLiteral(state, string.Empty), responseSlot);
        LlvmApi.BuildBr(builder, parseResponseContinueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseResponseContinueBlock);
        LlvmValueHandle finalResponse = LlvmApi.BuildLoad2(builder, state.I64, responseSlot, "http_final_response");
        LlvmValueHandle responseLen = LoadStringLength(state, finalResponse, "http_response_len");
        LlvmValueHandle responseTooShort = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, responseLen, LlvmApi.ConstInt(state.I64, 12, 0), "http_response_too_short");
        var parseHeadersBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_headers");
        LlvmApi.BuildCondBr(builder, responseTooShort, malformedResponseBlock, parseHeadersBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseHeadersBlock);
        LlvmValueHandle responseBytes = GetStringBytesPointer(state, finalResponse, "http_response_bytes");
        LlvmValueHandle separatorIndex = EmitFindByteSequence(state, responseBytes, responseLen, "\r\n\r\n"u8.ToArray(), "http_separator");
        LlvmValueHandle hasSeparator = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, separatorIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_separator");
        var parseStatusBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_status");
        LlvmApi.BuildCondBr(builder, hasSeparator, parseStatusBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseStatusBlock);
        LlvmValueHandle headerLength = separatorIndex;
        LlvmValueHandle statusSpaceIndex = EmitFindByte(state, responseBytes, headerLength, 0, (byte)' ', "http_status_space");
        LlvmValueHandle hasStatusSpace = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_status_space");
        var parseDigitsBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_digits");
        LlvmApi.BuildCondBr(builder, hasStatusSpace, parseDigitsBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseDigitsBlock);
        LlvmValueHandle statusEnd = LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), "http_status_end");
        LlvmValueHandle digitsInRange = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, statusEnd, headerLength, "http_status_digits_in_range");
        var parseDigitsContinueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_parse_digits_continue");
        LlvmApi.BuildCondBr(builder, digitsInRange, parseDigitsContinueBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseDigitsContinueBlock);
        LlvmValueHandle hundredsByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 1, 0), "http_hundreds_idx"), "http_hundreds_byte");
        LlvmValueHandle tensByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 2, 0), "http_tens_idx"), "http_tens_byte");
        LlvmValueHandle onesByte = LoadByteAt(state, responseBytes, LlvmApi.BuildAdd(builder, statusSpaceIndex, LlvmApi.ConstInt(state.I64, 3, 0), "http_ones_idx"), "http_ones_byte");
        LlvmValueHandle digitsValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildAnd(builder,
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, "http_hundreds_i64"), (byte)'0', (byte)'9', "http_hundreds_range"),
                BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, tensByte, state.I64, "http_tens_i64"), (byte)'0', (byte)'9', "http_tens_range"),
                "http_digits_first"),
            BuildByteRangeCheck(state, LlvmApi.BuildZExt(builder, onesByte, state.I64, "http_ones_i64"), (byte)'0', (byte)'9', "http_ones_range"),
            "http_digits_valid");
        var detectChunkedBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_detect_chunked");
        LlvmApi.BuildCondBr(builder, digitsValid, detectChunkedBlock, malformedResponseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, detectChunkedBlock);
        LlvmValueHandle chunkedHeaderIndex = EmitFindByteSequence(state, responseBytes, headerLength, "Transfer-Encoding: chunked"u8.ToArray(), "http_chunked_header");
        LlvmValueHandle hasChunkedHeader = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, chunkedHeaderIndex, LlvmApi.ConstInt(state.I64, 0, 1), "http_has_chunked_header");
        var buildBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_build_body");
        LlvmApi.BuildCondBr(builder, hasChunkedHeader, chunkedErrorBlock, buildBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, buildBodyBlock);
        LlvmValueHandle statusCode = LlvmApi.BuildAdd(builder,
            LlvmApi.BuildAdd(builder,
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, hundredsByte, state.I64, "http_hundreds_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_hundreds_digit"), LlvmApi.ConstInt(state.I64, 100, 0), "http_hundreds_mul"),
                LlvmApi.BuildMul(builder, LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, tensByte, state.I64, "http_tens_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_tens_digit"), LlvmApi.ConstInt(state.I64, 10, 0), "http_tens_mul"),
                "http_status_prefix_sum"),
            LlvmApi.BuildSub(builder, LlvmApi.BuildZExt(builder, onesByte, state.I64, "http_ones_code"), LlvmApi.ConstInt(state.I64, (byte)'0', 0), "http_ones_digit"),
            "http_status_code");
        LlvmValueHandle bodyStart = LlvmApi.BuildAdd(builder, separatorIndex, LlvmApi.ConstInt(state.I64, 4, 0), "http_body_start");
        LlvmValueHandle bodyLength = LlvmApi.BuildSub(builder, responseLen, bodyStart, "http_body_len");
        LlvmValueHandle bodyBytes = LlvmApi.BuildGEP2(builder, state.I8, responseBytes, new[] { bodyStart }, "http_body_ptr");
        LlvmValueHandle bodyString = EmitHeapStringSliceFromBytesPointer(state, bodyBytes, bodyLength, "http_body");
        LlvmValueHandle statusOk = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, statusCode, LlvmApi.ConstInt(state.I64, 200, 0), "http_status_ge_200"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, statusCode, LlvmApi.ConstInt(state.I64, 299, 0), "http_status_le_299"),
            "http_status_ok");
        var statusOkBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_status_ok_block");
        var statusErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "http_status_error_block");
        LlvmApi.BuildCondBr(builder, statusOk, statusOkBlock, statusErrorBlock);

        LlvmApi.PositionBuilderAtEnd(builder, statusOkBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, bodyString), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, statusErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHttpStatusErrorString(state, statusCode, "http_status_error")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, httpsErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpHttpsNotSupportedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, closeErrorBlock);
        LlvmApi.BuildStore(builder, closeResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, malformedResponseBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedResponseMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, chunkedErrorBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, HttpUnsupportedTransferEncodingMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "http_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_result");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_socket");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), socketSlot);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_resolve_failed");
        var resolveErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_resolve_error");
        var validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_validate_port");
        var openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_open_socket");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_connect");
        var connectFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_fail");
        var connectCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_close_socket");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_continue");
        LlvmApi.BuildCondBr(builder, resolveFailed, resolveErrorBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveErrorBlock);
        LlvmApi.BuildStore(builder, resolveResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "tcp_connect_port_le_max"),
            "tcp_connect_port_valid");
        LlvmApi.BuildCondBr(builder, validPort, openSocketBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmValueHandle socketValue = EmitLinuxSyscall(
            state,
            SyscallSocket,
            LlvmApi.ConstInt(state.I64, 2, 0),
            LlvmApi.ConstInt(state.I64, 1, 0),
            LlvmApi.ConstInt(state.I64, 0, 0),
            "tcp_connect_socket_call");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle socketFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, socketValue, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_socket_failed");
        LlvmApi.BuildCondBr(builder, socketFailed, connectFailBlock, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "tcp_connect_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle sockaddrI64Ptr = LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "tcp_connect_sockaddr_i64");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), sockaddrI64Ptr);
        LlvmValueHandle sockaddrTailPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, new[] { LlvmApi.ConstInt(state.I64, 8, 0) }, "tcp_connect_sockaddr_tail");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrTailPtr, state.I64Ptr, "tcp_connect_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "tcp_connect_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, new[] { LlvmApi.ConstInt(state.I64, 2, 0) }, "tcp_connect_port_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "tcp_connect_port_network"), i16, "tcp_connect_port_i16"), LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "tcp_connect_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, new[] { LlvmApi.ConstInt(state.I64, 4, 0) }, "tcp_connect_addr_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, LoadMemory(state, resolveResult, 8, "tcp_connect_addr_value"), state.I32, "tcp_connect_addr_i32"), LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "tcp_connect_addr_ptr"));
        LlvmValueHandle connectResult = EmitLinuxSyscall(
            state,
            SyscallConnect,
            LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_socket_value"),
            LlvmApi.BuildPtrToInt(builder, sockaddrBytes, state.I64, "tcp_connect_sockaddr_ptr"),
            LlvmApi.ConstInt(state.I64, 16, 0),
            "tcp_connect_call");
        LlvmValueHandle connectFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, connectResult, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_failed_bool");
        var connectSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_success");
        LlvmApi.BuildCondBr(builder, connectFailed, connectCloseBlock, connectSuccessBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectCloseBlock);
        EmitLinuxSyscall(state, SyscallClose, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_close_socket_value"), LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_close_call");
        LlvmApi.BuildBr(builder, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectFailBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectSuccessBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_success_socket")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_connect_result_value");
    }

    private static LlvmValueHandle EmitWindowsTcpConnect(LlvmCodegenState state, LlvmValueHandle hostRef, LlvmValueHandle port)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_win_result");
        LlvmValueHandle socketSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_connect_win_socket");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), socketSlot);
        LlvmValueHandle resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_win_resolve");
        LlvmValueHandle resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_win_resolve_tag");
        LlvmValueHandle resolveFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, resolveTag, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_resolve_failed");
        var resolveErrorBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_resolve_error");
        var validatePortBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_validate_port");
        var initWinsockBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_init_winsock");
        var openSocketBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_open_socket");
        var connectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_connect");
        var connectCloseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_close_socket");
        var connectFailBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_continue");
        LlvmApi.BuildCondBr(builder, resolveFailed, resolveErrorBlock, validatePortBlock);

        LlvmApi.PositionBuilderAtEnd(builder, resolveErrorBlock);
        LlvmApi.BuildStore(builder, resolveResult, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validatePortBlock);
        LlvmValueHandle validPort = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, port, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_port_gt_zero"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, port, LlvmApi.ConstInt(state.I64, 65535, 0), "tcp_connect_win_port_le_max"),
            "tcp_connect_win_port_valid");
        LlvmApi.BuildCondBr(builder, validPort, initWinsockBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, initWinsockBlock);
        LlvmTypeHandle wsadataType = LlvmApi.ArrayType2(state.I8, 512);
        LlvmValueHandle wsadata = LlvmApi.BuildAlloca(builder, wsadataType, "tcp_connect_win_wsadata");
        LlvmValueHandle winsockStarted = EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_wsadata_ptr"), "tcp_connect_win_wsastartup");
        LlvmApi.BuildCondBr(builder, winsockStarted, openSocketBlock, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, openSocketBlock);
        LlvmValueHandle socketValue = EmitWindowsSocket(state, 2, 1, 6, "tcp_connect_win_socket_call");
        LlvmApi.BuildStore(builder, socketValue, socketSlot);
        LlvmValueHandle socketFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, socketValue, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), "tcp_connect_win_socket_failed");
        LlvmApi.BuildCondBr(builder, socketFailed, connectFailBlock, connectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectBlock);
        LlvmTypeHandle sockaddrType = LlvmApi.ArrayType2(state.I8, 16);
        LlvmValueHandle sockaddrStorage = LlvmApi.BuildAlloca(builder, sockaddrType, "tcp_connect_win_sockaddr");
        LlvmValueHandle sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_connect_win_sockaddr_bytes");
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle i16Ptr = LlvmApi.PointerTypeInContext(state.Target.Context, 0);
        LlvmValueHandle sockaddrI64Ptr = LlvmApi.BuildBitCast(builder, sockaddrBytes, state.I64Ptr, "tcp_connect_win_sockaddr_i64");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), sockaddrI64Ptr);
        LlvmValueHandle sockaddrTailPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, new[] { LlvmApi.ConstInt(state.I64, 8, 0) }, "tcp_connect_win_sockaddr_tail");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.BuildBitCast(builder, sockaddrTailPtr, state.I64Ptr, "tcp_connect_win_sockaddr_tail_i64"));
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(i16, 2, 0), LlvmApi.BuildBitCast(builder, sockaddrBytes, i16Ptr, "tcp_connect_win_family_ptr"));
        LlvmValueHandle portPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, new[] { LlvmApi.ConstInt(state.I64, 2, 0) }, "tcp_connect_win_port_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, EmitByteSwap16(state, port, "tcp_connect_win_port_network"), i16, "tcp_connect_win_port_i16"), LlvmApi.BuildBitCast(builder, portPtr, i16Ptr, "tcp_connect_win_port_ptr"));
        LlvmValueHandle addrPtr = LlvmApi.BuildGEP2(builder, state.I8, sockaddrBytes, new[] { LlvmApi.ConstInt(state.I64, 4, 0) }, "tcp_connect_win_addr_ptr_byte");
        LlvmApi.BuildStore(builder, LlvmApi.BuildTrunc(builder, LoadMemory(state, resolveResult, 8, "tcp_connect_win_addr_value"), state.I32, "tcp_connect_win_addr_i32"), LlvmApi.BuildBitCast(builder, addrPtr, state.I32Ptr, "tcp_connect_win_addr_ptr"));
        LlvmValueHandle connectResult = EmitWindowsConnect(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_socket_value"), sockaddrBytes, "tcp_connect_win_connect_call");
        var connectSuccessBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_connect_win_success");
        LlvmApi.BuildCondBr(builder, connectResult, connectSuccessBlock, connectCloseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectCloseBlock);
        EmitWindowsCloseSocket(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_close_socket_value"), "tcp_connect_win_close_socket_call");
        LlvmApi.BuildBr(builder, connectFailBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectFailBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, connectSuccessBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.BuildLoad2(builder, state.I64, socketSlot, "tcp_connect_win_success_socket")), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_connect_win_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_result");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_cursor");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "tcp_send_total_len");
        LlvmApi.BuildStore(builder, totalLen, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "tcp_send_cursor_start"), cursorSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_loop_body");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_update");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_continue");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "tcp_send_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_done");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_done_block");
        LlvmApi.BuildCondBr(builder, done, doneBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle sent = EmitLinuxSyscall(state, SyscallWrite, socket, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_cursor_value"), remaining, "tcp_send_syscall");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, sent, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_failed");
        LlvmApi.BuildCondBr(builder, sendFailed, failBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, sent, "tcp_send_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursor, sent, "tcp_send_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, totalLen), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_send_result_value");
    }

    private static LlvmValueHandle EmitWindowsTcpSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle textRef)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_result");
        LlvmValueHandle remainingSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_remaining");
        LlvmValueHandle cursorSlot = LlvmApi.BuildAlloca(builder, state.I64, "tcp_send_win_cursor");
        LlvmValueHandle totalLen = LoadStringLength(state, textRef, "tcp_send_win_total_len");
        LlvmApi.BuildStore(builder, totalLen, remainingSlot);
        LlvmApi.BuildStore(builder, GetStringBytesAddress(state, textRef, "tcp_send_win_cursor_start"), cursorSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_loop_body");
        var updateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_update");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_continue");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle remaining = LlvmApi.BuildLoad2(builder, state.I64, remainingSlot, "tcp_send_win_remaining_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, remaining, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_send_win_done");
        var doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "tcp_send_win_done_block");
        LlvmApi.BuildCondBr(builder, done, doneBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle chunk = LlvmApi.BuildSelect(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, remaining, LlvmApi.ConstInt(state.I64, int.MaxValue, 0), "tcp_send_win_chunk_gt"),
            LlvmApi.ConstInt(state.I64, int.MaxValue, 0),
            remaining,
            "tcp_send_win_chunk");
        LlvmValueHandle sentRaw = EmitWindowsSend(state, socket, LlvmApi.BuildIntToPtr(builder, LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_win_cursor_value"), state.I8Ptr, "tcp_send_win_cursor_ptr"), LlvmApi.BuildTrunc(builder, chunk, state.I32, "tcp_send_win_chunk_i32"), "tcp_send_win_call");
        LlvmValueHandle sendFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sle, sentRaw, LlvmApi.ConstInt(state.I32, 0, 1), "tcp_send_win_failed");
        LlvmApi.BuildCondBr(builder, sendFailed, failBlock, updateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, updateBlock);
        LlvmValueHandle sent = LlvmApi.BuildSExt(builder, sentRaw, state.I64, "tcp_send_win_sent");
        LlvmValueHandle cursor = LlvmApi.BuildLoad2(builder, state.I64, cursorSlot, "tcp_send_win_cursor_current");
        LlvmApi.BuildStore(builder, LlvmApi.BuildSub(builder, remaining, sent, "tcp_send_win_remaining_next"), remainingSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, cursor, sent, "tcp_send_win_cursor_next"), cursorSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, totalLen), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, "tcp_send_win_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive", static (s, sock, bytesPtr, max, name) => EmitLinuxSyscall(s, SyscallRead, sock, LlvmApi.BuildPtrToInt(s.Target.Builder, bytesPtr, s.I64, name + "_ptr"), LlvmApi.BuildSExt(s.Target.Builder, max, s.I64, name + "_len"), name));
    }

    private static LlvmValueHandle EmitWindowsTcpReceive(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive_win", static (s, sock, bytesPtr, max, name) => LlvmApi.BuildSExt(s.Target.Builder, EmitWindowsRecv(s, sock, bytesPtr, max, name), s.I64, name + "_sext"));
    }

    private static LlvmValueHandle EmitTcpReceiveCommon(
        LlvmCodegenState state,
        LlvmValueHandle socket,
        LlvmValueHandle maxBytes,
        string prefix,
        Func<LlvmCodegenState, LlvmValueHandle, LlvmValueHandle, LlvmValueHandle, string, LlvmValueHandle> emitRead)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        var invalidMaxBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid_max");
        var readBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_read");
        var handleReadBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_handle_read");
        var invalidUtf8Block = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_invalid_utf8");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmValueHandle positiveMax = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sgt, maxBytes, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_positive_max");
        LlvmApi.BuildCondBr(builder, positiveMax, readBlock, invalidMaxBlock);

        LlvmApi.PositionBuilderAtEnd(builder, invalidMaxBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, readBlock);
        LlvmValueHandle stringRef = EmitAllocDynamic(state, LlvmApi.BuildAdd(builder, maxBytes, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_size"));
        StoreMemory(state, stringRef, 0, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_len_init");
        LlvmValueHandle readCount = emitRead(state, socket, GetStringBytesPointer(state, stringRef, prefix + "_bytes"), LlvmApi.BuildTrunc(builder, maxBytes, state.I32, prefix + "_max_i32"), prefix + "_read_call");
        LlvmValueHandle readFailed = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, readCount, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_read_failed");
        LlvmApi.BuildCondBr(builder, readFailed, failBlock, handleReadBlock);

        LlvmApi.PositionBuilderAtEnd(builder, handleReadBlock);
        StoreMemory(state, stringRef, 0, readCount, prefix + "_len_store");
        LlvmValueHandle isEmpty = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, readCount, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_empty");
        var successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        var validateBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_validate");
        LlvmApi.BuildCondBr(builder, isEmpty, successBlock, validateBlock);

        LlvmApi.PositionBuilderAtEnd(builder, validateBlock);
        LlvmValueHandle utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, stringRef, prefix + "_validate_bytes"), readCount, prefix + "_utf8");
        LlvmValueHandle valid = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, utf8Valid, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_utf8_valid");
        LlvmApi.BuildCondBr(builder, valid, successBlock, invalidUtf8Block);

        LlvmApi.PositionBuilderAtEnd(builder, invalidUtf8Block);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, stringRef), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitLinuxTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle result = EmitLinuxSyscall(state, SyscallClose, socket, LlvmApi.ConstInt(state.I64, 0, 0), LlvmApi.ConstInt(state.I64, 0, 0), "tcp_close_call");
        LlvmValueHandle success = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Sge, result, LlvmApi.ConstInt(state.I64, 0, 0), "tcp_close_success");
        return LlvmApi.BuildSelect(builder, success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_result");
    }

    private static LlvmValueHandle EmitWindowsTcpClose(LlvmCodegenState state, LlvmValueHandle socket)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle closeResult = EmitWindowsCloseSocket(state, socket, "tcp_close_win_call");
        LlvmValueHandle success = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, closeResult, LlvmApi.ConstInt(state.I32, 0, 0), "tcp_close_win_success");
        return LlvmApi.BuildSelect(builder, success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_win_result");
    }

    private static LlvmValueHandle EmitResolveHostIpv4OrLocalhost(LlvmCodegenState state, LlvmValueHandle hostRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle partSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_part");
        LlvmValueHandle currentSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_current");
        LlvmValueHandle seenDigitSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_seen_digit");
        LlvmValueHandle addressSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_address");
        LlvmApi.BuildStore(builder, EmitResultError(state, EmitHeapStringLiteral(state, TcpResolveFailedMessage)), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), partSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), addressSlot);

        LlvmValueHandle localhostEquals = EmitStringComparison(state, hostRef, EmitStackStringObject(state, "localhost"));
        LlvmValueHandle isLocalhost = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, localhostEquals, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_is_localhost");
        LlvmValueHandle hostLen = LoadStringLength(state, hostRef, prefix + "_host_len");
        LlvmValueHandle hostBytes = GetStringBytesPointer(state, hostRef, prefix + "_host_bytes");
        var localhostBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_localhost");
        var parseLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_loop");
        var parseInspectBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_parse_inspect");
        var digitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_digit");
        var dotBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dot");
        var failBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_fail");
        var finalizeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_finalize");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildCondBr(builder, isLocalhost, localhostBlock, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, localhostBlock);
        LlvmApi.BuildStore(builder, EmitResultOk(state, LlvmApi.ConstInt(state.I64, 0x0100007FUL, 0)), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseLoopBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, hostLen, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, finalizeBlock, parseInspectBlock);

        LlvmApi.PositionBuilderAtEnd(builder, parseInspectBlock);
        LlvmValueHandle currentByte = LoadByteAt(state, hostBytes, index, prefix + "_current_byte");
        LlvmValueHandle currentByte64 = LlvmApi.BuildZExt(builder, currentByte, state.I64, prefix + "_current_byte_i64");
        LlvmValueHandle isDigit = BuildByteRangeCheck(state, currentByte64, (byte)'0', (byte)'9', prefix + "_digit_range");
        var dotCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_dot_check");
        LlvmApi.BuildCondBr(builder, isDigit, digitBlock, dotCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, dotCheckBlock);
        LlvmValueHandle isDot = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, (byte)'.', 0), prefix + "_is_dot");
        LlvmApi.BuildCondBr(builder, isDot, dotBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, digitBlock);
        LlvmValueHandle currentValue = LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_current_value");
        LlvmValueHandle parsedDigit = LlvmApi.BuildSub(builder, currentByte64, LlvmApi.ConstInt(state.I64, (byte)'0', 0), prefix + "_parsed_digit");
        LlvmValueHandle nextValue = LlvmApi.BuildAdd(builder, LlvmApi.BuildMul(builder, currentValue, LlvmApi.ConstInt(state.I64, 10, 0), prefix + "_mul"), parsedDigit, prefix + "_next_value");
        LlvmValueHandle valueTooLarge = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, nextValue, LlvmApi.ConstInt(state.I64, 255, 0), prefix + "_value_too_large");
        var storeDigitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_digit");
        LlvmApi.BuildCondBr(builder, valueTooLarge, failBlock, storeDigitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeDigitBlock);
        LlvmApi.BuildStore(builder, nextValue, currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, dotBlock);
        LlvmValueHandle seenDigit = LlvmApi.BuildLoad2(builder, state.I64, seenDigitSlot, prefix + "_seen_digit_value");
        LlvmValueHandle part = LlvmApi.BuildLoad2(builder, state.I64, partSlot, prefix + "_part_value");
        LlvmValueHandle dotValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, seenDigit, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_dot_seen_digit"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ult, part, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_dot_part_lt_three"),
            prefix + "_dot_valid");
        var storeDotBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_dot");
        LlvmApi.BuildCondBr(builder, dotValid, storeDotBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeDotBlock);
        LlvmValueHandle addressValue = LlvmApi.BuildLoad2(builder, state.I64, addressSlot, prefix + "_address_value");
        LlvmValueHandle shiftedOctet = LlvmApi.BuildShl(builder, LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_octet_value"), LlvmApi.BuildMul(builder, part, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_octet_shift"), prefix + "_shifted_octet");
        LlvmApi.BuildStore(builder, LlvmApi.BuildOr(builder, addressValue, shiftedOctet, prefix + "_address_next"), addressSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, part, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_part_next"), partSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), currentSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), seenDigitSlot);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_after_dot"), indexSlot);
        LlvmApi.BuildBr(builder, parseLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, finalizeBlock);
        LlvmValueHandle finalSeenDigit = LlvmApi.BuildLoad2(builder, state.I64, seenDigitSlot, prefix + "_final_seen_digit");
        LlvmValueHandle finalPart = LlvmApi.BuildLoad2(builder, state.I64, partSlot, prefix + "_final_part");
        LlvmValueHandle finalValid = LlvmApi.BuildAnd(builder,
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, finalSeenDigit, LlvmApi.ConstInt(state.I64, 0, 0), prefix + "_final_seen_digit_ok"),
            LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, finalPart, LlvmApi.ConstInt(state.I64, 3, 0), prefix + "_final_part_eq_three"),
            prefix + "_final_valid");
        var storeFinalBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_store_final");
        LlvmApi.BuildCondBr(builder, finalValid, storeFinalBlock, failBlock);

        LlvmApi.PositionBuilderAtEnd(builder, storeFinalBlock);
        LlvmValueHandle finalAddress = LlvmApi.BuildOr(builder,
            LlvmApi.BuildLoad2(builder, state.I64, addressSlot, prefix + "_address_before_final"),
            LlvmApi.BuildShl(builder, LlvmApi.BuildLoad2(builder, state.I64, currentSlot, prefix + "_current_before_final"), LlvmApi.ConstInt(state.I64, 24, 0), prefix + "_final_shifted_octet"),
            prefix + "_final_address");
        LlvmApi.BuildStore(builder, EmitResultOk(state, finalAddress), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, failBlock);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitHttpRequestString(LlvmCodegenState state, LlvmValueHandle pathRef, LlvmValueHandle hostRef, LlvmValueHandle bodyRef, bool hasBody)
    {
        LlvmValueHandle request = EmitHeapStringLiteral(state, hasBody ? "POST " : "GET ");
        request = EmitStringConcat(state, request, pathRef);
        request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, " HTTP/1.1\r\nHost: "));
        request = EmitStringConcat(state, request, hostRef);
        if (hasBody)
        {
            request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, "\r\nContent-Length: "));
            request = EmitStringConcat(state, request, EmitNonNegativeIntToString(state, LoadStringLength(state, bodyRef, "http_body_length"), "http_body_length_string"));
        }

        request = EmitStringConcat(state, request, EmitHeapStringLiteral(state, "\r\nConnection: close\r\n\r\n"));
        if (hasBody)
        {
            request = EmitStringConcat(state, request, bodyRef);
        }

        return request;
    }

    private static LlvmValueHandle EmitHttpStatusErrorString(LlvmCodegenState state, LlvmValueHandle statusCode, string prefix)
    {
        return EmitStringConcat(state, EmitHeapStringLiteral(state, "HTTP "), EmitNonNegativeIntToString(state, statusCode, prefix + "_code"));
    }

    private static LlvmValueHandle EmitStartsWith(LlvmCodegenState state, LlvmValueHandle sourceRef, LlvmValueHandle prefixRef, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle sourceLen = LoadStringLength(state, sourceRef, prefix + "_source_len");
        LlvmValueHandle prefixLen = LoadStringLength(state, prefixRef, prefix + "_prefix_len");
        LlvmValueHandle enough = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, sourceLen, prefixLen, prefix + "_enough");
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        var compareBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_compare");
        var falseBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_false");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildCondBr(builder, enough, compareBlock, falseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, falseBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, compareBlock);
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        LlvmValueHandle sourceBytes = GetStringBytesPointer(state, sourceRef, prefix + "_source_bytes");
        LlvmValueHandle prefixBytes = GetStringBytesPointer(state, prefixRef, prefix + "_prefix_bytes");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var successBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_success");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, index, prefixLen, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, successBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle sourceByte = LoadByteAt(state, sourceBytes, index, prefix + "_source_byte");
        LlvmValueHandle prefixByte = LoadByteAt(state, prefixBytes, index, prefix + "_prefix_byte");
        LlvmValueHandle matches = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, sourceByte, prefixByte, prefix + "_matches");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_advance");
        LlvmApi.BuildCondBr(builder, matches, advanceBlock, falseBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, successBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitFindByte(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, int startOffset, byte targetByte, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, (ulong)startOffset, 0), indexSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_found");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Uge, index, len, prefix + "_done");
        LlvmApi.BuildCondBr(builder, done, continueBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle currentByte = LoadByteAt(state, bytesPtr, index, prefix + "_byte");
        LlvmValueHandle matches = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, currentByte, LlvmApi.ConstInt(state.I8, targetByte, 0), prefix + "_matches");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_advance");
        LlvmApi.BuildCondBr(builder, matches, foundBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        LlvmApi.BuildStore(builder, index, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitFindByteSequence(LlvmCodegenState state, LlvmValueHandle bytesPtr, LlvmValueHandle len, IReadOnlyList<byte> patternBytes, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle resultSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_result");
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_index");
        LlvmValueHandle patternLen = LlvmApi.ConstInt(state.I64, (ulong)patternBytes.Count, 0);
        LlvmValueHandle patternPtr = EmitStackByteArray(state, patternBytes);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, unchecked((ulong)(-1L)), 1), resultSlot);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_loop_body");
        var compareLoopBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_compare_loop");
        var foundBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_found");
        var advanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_advance");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_continue");
        LlvmValueHandle compareIndexSlot = LlvmApi.BuildAlloca(builder, state.I64, prefix + "_compare_index");
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle index = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, prefix + "_index_value");
        LlvmValueHandle canMatch = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, LlvmApi.BuildAdd(builder, index, patternLen, prefix + "_candidate_end"), len, prefix + "_can_match");
        LlvmApi.BuildCondBr(builder, canMatch, loopBodyBlock, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), compareIndexSlot);
        LlvmApi.BuildBr(builder, compareLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, compareLoopBlock);
        LlvmValueHandle compareIndex = LlvmApi.BuildLoad2(builder, state.I64, compareIndexSlot, prefix + "_compare_index_value");
        LlvmValueHandle done = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, compareIndex, patternLen, prefix + "_compare_done");
        var compareBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_compare_body");
        LlvmApi.BuildCondBr(builder, done, foundBlock, compareBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, compareBodyBlock);
        LlvmValueHandle actualByte = LoadByteAt(state, bytesPtr, LlvmApi.BuildAdd(builder, index, compareIndex, prefix + "_actual_index"), prefix + "_actual_byte");
        LlvmValueHandle expectedByte = LoadByteAt(state, patternPtr, compareIndex, prefix + "_expected_byte");
        LlvmValueHandle matches = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, actualByte, expectedByte, prefix + "_compare_matches");
        var compareAdvanceBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, prefix + "_compare_advance");
        LlvmApi.BuildCondBr(builder, matches, compareAdvanceBlock, advanceBlock);

        LlvmApi.PositionBuilderAtEnd(builder, compareAdvanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, compareIndex, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_compare_index_next"), compareIndexSlot);
        LlvmApi.BuildBr(builder, compareLoopBlock);

        LlvmApi.PositionBuilderAtEnd(builder, foundBlock);
        LlvmApi.BuildStore(builder, index, resultSlot);
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, advanceBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, index, LlvmApi.ConstInt(state.I64, 1, 0), prefix + "_index_next"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return LlvmApi.BuildLoad2(builder, state.I64, resultSlot, prefix + "_result_value");
    }

    private static LlvmValueHandle EmitByteSwap16(LlvmCodegenState state, LlvmValueHandle value, string prefix)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle maskedLow = LlvmApi.BuildAnd(builder, value, LlvmApi.ConstInt(state.I64, 0xFF, 0), prefix + "_low");
        LlvmValueHandle maskedHigh = LlvmApi.BuildAnd(builder, LlvmApi.BuildLShr(builder, value, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_shr"), LlvmApi.ConstInt(state.I64, 0xFF, 0), prefix + "_high");
        return LlvmApi.BuildOr(builder, LlvmApi.BuildShl(builder, maskedLow, LlvmApi.ConstInt(state.I64, 8, 0), prefix + "_low_shifted"), maskedHigh, prefix + "_result");
    }

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

    private static void EmitLinuxProgramArgsInitialization(LlvmCodegenState state)
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

        var lenLoopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "program_args_len_loop_check");
        LlvmApi.BuildBr(builder, lenLoopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, lenLoopCheckBlock);
        LlvmValueHandle currentLen = LlvmApi.BuildLoad2(builder, state.I64, lenSlot, "program_args_current_len");
        LlvmValueHandle currentArgPtr = LlvmApi.BuildLoad2(builder, state.I64, argPtrSlot, "program_args_current_arg_ptr");
        LlvmValueHandle currentBytePtr = LlvmApi.BuildGEP2(builder,
            state.I8,
            LlvmApi.BuildIntToPtr(builder, currentArgPtr, state.I8Ptr, "program_args_arg_bytes"),
            new[] { currentLen },
            "program_args_current_byte_ptr");
        LlvmValueHandle currentByte = LlvmApi.BuildLoad2(builder, state.I8, currentBytePtr, "program_args_current_byte");
        LlvmValueHandle reachedTerminator = LlvmApi.BuildICmp(builder,
            LlvmIntPredicate.Eq,
            currentByte,
            LlvmApi.ConstInt(state.I8, 0, 0),
            "program_args_reached_terminator");
        LlvmApi.BuildCondBr(builder, reachedTerminator, buildNodeBlock, lenBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, lenBodyBlock);
        LlvmApi.BuildStore(builder,
            LlvmApi.BuildAdd(builder, currentLen, LlvmApi.ConstInt(state.I64, 1, 0), "program_args_next_len"),
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

    private static void EmitWindowsProgramArgsInitialization(LlvmCodegenState state)
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
            new[] { commandLinePtr, argcSlot },
            "argv_wide");

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
            new[] { LlvmApi.BuildSExt(builder, index, state.I64, "program_args_index_i64") },
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
            new[] { LlvmApi.BuildSExt(builder, wideLen, state.I64, "program_args_wide_len_i64") },
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
            new[]
            {
                LlvmApi.ConstInt(state.I32, Utf8CodePage, 0),
                LlvmApi.ConstInt(state.I32, 0, 0),
                wideArg,
                wcharCount,
                nullI8Ptr,
                LlvmApi.ConstInt(state.I32, 0, 0),
                nullI8Ptr,
                nullI8Ptr
            },
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
            new[]
            {
                LlvmApi.ConstInt(state.I32, Utf8CodePage, 0),
                LlvmApi.ConstInt(state.I32, 0, 0),
                wideArg,
                wcharCount,
                stringDest,
                byteCount,
                nullI8Ptr,
                nullI8Ptr
            },
            "program_args_copy_utf8");
        LlvmApi.BuildStore(builder, stringRef, stringRefSlot);
        LlvmApi.BuildBr(builder, linkArgBlock);

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
            new[] { LlvmApi.BuildBitCast(builder, argvWide, state.I8Ptr, "argv_wide_hlocal") },
            "program_args_local_free");
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
        LlvmApi.BuildStore(builder, LlvmApi.BuildLoad2(builder, state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
    }
}
