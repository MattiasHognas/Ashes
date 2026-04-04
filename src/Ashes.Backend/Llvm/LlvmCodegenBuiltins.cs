using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LLVMValueRef EmitReadLine(LlvmCodegenState state)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef inputBufType = LLVMTypeRef.CreateArray(state.I8, InputBufSize);
        LLVMValueRef inputBuf = builder.BuildAlloca(inputBufType, "read_line_buf");
        LLVMValueRef inputBufPtr = GetArrayElementPointer(state, inputBufType, inputBuf, LLVMValueRef.CreateConstInt(state.I64, 0, false), "read_line_buf_ptr");
        LLVMValueRef byteSlot = builder.BuildAlloca(state.I8, "read_line_byte");
        LLVMValueRef lenSlot = builder.BuildAlloca(state.I64, "read_line_len");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "read_line_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), lenSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        LLVMValueRef stdinHandle = default;
        LLVMValueRef bytesReadSlot = default;
        if (state.Flavor == LlvmCodegenFlavor.Windows)
        {
            stdinHandle = EmitWindowsGetStdHandle(state, StdInputHandle, "stdin_handle");
            bytesReadSlot = builder.BuildAlloca(state.I32, "read_line_bytes_read");
        }

        var loopBlock = state.Function.AppendBasicBlock("read_line_loop");
        var inspectBlock = state.Function.AppendBasicBlock("read_line_inspect");
        var skipCrBlock = state.Function.AppendBasicBlock("read_line_skip_cr");
        var storeByteBlock = state.Function.AppendBasicBlock("read_line_store_byte");
        var appendByteBlock = state.Function.AppendBasicBlock("read_line_append_byte");
        var eofBlock = state.Function.AppendBasicBlock("read_line_eof");
        var finishSomeBlock = state.Function.AppendBasicBlock("read_line_finish_some");
        var returnNoneBlock = state.Function.AppendBasicBlock("read_line_return_none");
        var overflowBlock = state.Function.AppendBasicBlock("read_line_overflow");
        var continueBlock = state.Function.AppendBasicBlock("read_line_continue");

        builder.BuildBr(loopBlock);

        builder.PositionAtEnd(loopBlock);
        LLVMValueRef bytesRead = state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitSyscall(
                state,
                SyscallRead,
                LLVMValueRef.CreateConstInt(state.I64, 0, false),
                builder.BuildPtrToInt(byteSlot, state.I64, "read_line_byte_ptr"),
                LLVMValueRef.CreateConstInt(state.I64, 1, false),
                "sys_read_line")
            : EmitWindowsReadByte(state, stdinHandle, byteSlot, bytesReadSlot);
        LLVMValueRef hasByte = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bytesRead, LLVMValueRef.CreateConstInt(state.I64, 0, false), "read_line_has_byte");
        builder.BuildCondBr(hasByte, inspectBlock, eofBlock);

        builder.PositionAtEnd(inspectBlock);
        LLVMValueRef currentByte = builder.BuildLoad2(state.I8, byteSlot, "read_line_current_byte");
        LLVMValueRef isLf = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentByte, LLVMValueRef.CreateConstInt(state.I8, 10, false), "read_line_is_lf");
        builder.BuildCondBr(isLf, finishSomeBlock, skipCrBlock);

        builder.PositionAtEnd(skipCrBlock);
        LLVMValueRef isCr = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentByte, LLVMValueRef.CreateConstInt(state.I8, 13, false), "read_line_is_cr");
        builder.BuildCondBr(isCr, loopBlock, storeByteBlock);

        builder.PositionAtEnd(storeByteBlock);
        LLVMValueRef currentLen = builder.BuildLoad2(state.I64, lenSlot, "read_line_len_value");
        LLVMValueRef atCapacity = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, currentLen, LLVMValueRef.CreateConstInt(state.I64, InputBufSize, false), "read_line_at_capacity");
        builder.BuildCondBr(atCapacity, overflowBlock, appendByteBlock);

        builder.PositionAtEnd(appendByteBlock);
        LLVMValueRef destPtr = builder.BuildGEP2(state.I8, inputBufPtr, new[] { currentLen }, "read_line_dest_ptr");
        builder.BuildStore(currentByte, destPtr);
        builder.BuildStore(builder.BuildAdd(currentLen, LLVMValueRef.CreateConstInt(state.I64, 1, false), "read_line_len_next"), lenSlot);
        builder.BuildBr(loopBlock);

        builder.PositionAtEnd(eofBlock);
        LLVMValueRef lenAtEof = builder.BuildLoad2(state.I64, lenSlot, "read_line_len_at_eof");
        LLVMValueRef isEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lenAtEof, LLVMValueRef.CreateConstInt(state.I64, 0, false), "read_line_is_empty");
        builder.BuildCondBr(isEmpty, returnNoneBlock, finishSomeBlock);

        builder.PositionAtEnd(finishSomeBlock);
        LLVMValueRef finalLen = builder.BuildLoad2(state.I64, lenSlot, "read_line_final_len");
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(finalLen, LLVMValueRef.CreateConstInt(state.I64, 8, false), "read_line_string_bytes"));
        StoreMemory(state, stringRef, 0, finalLen, "read_line_string_len");
        EmitCopyBytes(state, GetStringBytesPointer(state, stringRef, "read_line_string_dest"), inputBufPtr, finalLen, "read_line_copy_bytes");
        LLVMValueRef someRef = EmitAllocAdt(state, 1, 1);
        StoreMemory(state, someRef, 8, stringRef, "read_line_some_value");
        builder.BuildStore(someRef, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(returnNoneBlock);
        builder.BuildStore(EmitAllocAdt(state, 0, 0), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(overflowBlock);
        EmitPanic(state, EmitStackStringObject(state, "readLine input too long"));

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "read_line_result_value");
    }

    private static LLVMValueRef EmitFileReadText(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxFileReadText(state, pathRef)
            : EmitWindowsFileReadText(state, pathRef);
    }

    private static LLVMValueRef EmitFileWriteText(LlvmCodegenState state, LLVMValueRef pathRef, LLVMValueRef textRef)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxFileWriteText(state, pathRef, textRef)
            : EmitWindowsFileWriteText(state, pathRef, textRef);
    }

    private static LLVMValueRef EmitFileExists(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxFileExists(state, pathRef)
            : EmitWindowsFileExists(state, pathRef);
    }

    private static LLVMValueRef EmitLinuxFileReadText(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_read_path");
        LLVMValueRef fdSlot = builder.BuildAlloca(state.I64, "fs_read_fd");
        LLVMValueRef stringSlot = builder.BuildAlloca(state.I64, "fs_read_string");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "fs_read_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "fs_read_cursor");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_read_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), fdSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var openBlock = state.Function.AppendBasicBlock("fs_read_open");
        var seekEndBlock = state.Function.AppendBasicBlock("fs_read_seek_end");
        var seekStartBlock = state.Function.AppendBasicBlock("fs_read_seek_start");
        var allocBlock = state.Function.AppendBasicBlock("fs_read_alloc");
        var readCheckBlock = state.Function.AppendBasicBlock("fs_read_loop_check");
        var readBodyBlock = state.Function.AppendBasicBlock("fs_read_loop_body");
        var readDoneBlock = state.Function.AppendBasicBlock("fs_read_done");
        var utf8CheckBlock = state.Function.AppendBasicBlock("fs_read_utf8_check");
        var closeOkBlock = state.Function.AppendBasicBlock("fs_read_close_ok");
        var closeInvalidBlock = state.Function.AppendBasicBlock("fs_read_close_invalid");
        var closeErrorBlock = state.Function.AppendBasicBlock("fs_read_close_error");
        var maybeCloseErrorBlock = state.Function.AppendBasicBlock("fs_read_maybe_close_error");
        var closeHandleBlock = state.Function.AppendBasicBlock("fs_read_close_handle");
        var returnErrorBlock = state.Function.AppendBasicBlock("fs_read_return_error");
        var continueBlock = state.Function.AppendBasicBlock("fs_read_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef fd = EmitSyscall(
            state,
            SyscallOpen,
            builder.BuildPtrToInt(pathCstr, state.I64, "fs_read_path_ptr"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_open_call");
        builder.BuildStore(fd, fdSlot);
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, fd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_open_failed");
        builder.BuildCondBr(openFailed, returnErrorBlock, seekEndBlock);

        builder.PositionAtEnd(seekEndBlock);
        LLVMValueRef fileLength = EmitSyscall(
            state,
            SyscallLseek,
            fd,
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 2, false),
            "fs_read_seek_end_call");
        LLVMValueRef seekEndFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, fileLength, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_seek_end_failed");
        builder.BuildCondBr(seekEndFailed, maybeCloseErrorBlock, seekStartBlock);

        builder.PositionAtEnd(seekStartBlock);
        LLVMValueRef seekStart = EmitSyscall(
            state,
            SyscallLseek,
            fd,
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_seek_start_call");
        LLVMValueRef seekStartFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, seekStart, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_seek_start_failed");
        builder.BuildCondBr(seekStartFailed, maybeCloseErrorBlock, allocBlock);

        builder.PositionAtEnd(allocBlock);
        LLVMValueRef exceedsLimit = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, fileLength, LLVMValueRef.CreateConstInt(state.I64, MaxFileReadBytes, false), "fs_read_exceeds_limit");
        var withinLimitBlock = state.Function.AppendBasicBlock("fs_read_within_limit");
        builder.BuildCondBr(exceedsLimit, maybeCloseErrorBlock, withinLimitBlock);

        builder.PositionAtEnd(withinLimitBlock);
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(fileLength, LLVMValueRef.CreateConstInt(state.I64, 8, false), "fs_read_total_bytes"));
        StoreMemory(state, stringRef, 0, fileLength, "fs_read_len");
        builder.BuildStore(stringRef, stringSlot);
        builder.BuildStore(fileLength, remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, stringRef, "fs_read_cursor_start"), cursorSlot);
        LLVMValueRef isEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, fileLength, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_empty");
        builder.BuildCondBr(isEmpty, utf8CheckBlock, readCheckBlock);

        builder.PositionAtEnd(readCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "fs_read_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_done");
        builder.BuildCondBr(done, utf8CheckBlock, readBodyBlock);

        builder.PositionAtEnd(readBodyBlock);
        LLVMValueRef cursorAddress = builder.BuildLoad2(state.I64, cursorSlot, "fs_read_cursor_value");
        LLVMValueRef readBytes = EmitSyscall(
            state,
            SyscallRead,
            builder.BuildLoad2(state.I64, fdSlot, "fs_read_fd_value"),
            cursorAddress,
            remaining,
            "fs_read_read_call");
        LLVMValueRef readFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, readBytes, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_failed");
        builder.BuildCondBr(readFailed, maybeCloseErrorBlock, readDoneBlock);

        builder.PositionAtEnd(readDoneBlock);
        builder.BuildStore(builder.BuildSub(remaining, readBytes, "fs_read_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursorAddress, readBytes, "fs_read_cursor_next"), cursorSlot);
        builder.BuildBr(readCheckBlock);

        builder.PositionAtEnd(utf8CheckBlock);
        LLVMValueRef utf8Valid = EmitValidateUtf8(
            state,
            GetStringBytesPointer(state, builder.BuildLoad2(state.I64, stringSlot, "fs_read_string_value"), "fs_read_utf8_ptr"),
            LoadStringLength(state, builder.BuildLoad2(state.I64, stringSlot, "fs_read_string_len_value"), "fs_read_utf8_len"),
            "fs_read_utf8");
        LLVMValueRef isUtf8Valid = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, utf8Valid, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_is_utf8_valid");
        builder.BuildCondBr(isUtf8Valid, closeOkBlock, closeInvalidBlock);

        builder.PositionAtEnd(closeOkBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_read_close_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_close_ok_call");
        builder.BuildStore(EmitResultOk(state, builder.BuildLoad2(state.I64, stringSlot, "fs_read_ok_value")), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeInvalidBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_read_invalid_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_close_invalid_call");
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileReadInvalidUtf8Message)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(maybeCloseErrorBlock);
        LLVMValueRef fdValue = builder.BuildLoad2(state.I64, fdSlot, "fs_read_error_fd");
        LLVMValueRef shouldClose = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, fdValue, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_should_close");
        builder.BuildCondBr(shouldClose, closeHandleBlock, returnErrorBlock);

        builder.PositionAtEnd(closeHandleBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_read_close_error_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_read_close_error_call");
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(returnErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeErrorBlock);
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_read_result_value");
    }

    private static LLVMValueRef EmitLinuxFileWriteText(LlvmCodegenState state, LLVMValueRef pathRef, LLVMValueRef textRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_write_path");
        LLVMValueRef fdSlot = builder.BuildAlloca(state.I64, "fs_write_fd");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "fs_write_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "fs_write_cursor");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_write_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), fdSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var openBlock = state.Function.AppendBasicBlock("fs_write_open");
        var loopCheckBlock = state.Function.AppendBasicBlock("fs_write_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("fs_write_loop_body");
        var advanceBlock = state.Function.AppendBasicBlock("fs_write_advance");
        var closeOkBlock = state.Function.AppendBasicBlock("fs_write_close_ok");
        var maybeCloseErrorBlock = state.Function.AppendBasicBlock("fs_write_maybe_close_error");
        var closeErrorBlock = state.Function.AppendBasicBlock("fs_write_close_error");
        var returnErrorBlock = state.Function.AppendBasicBlock("fs_write_return_error");
        var continueBlock = state.Function.AppendBasicBlock("fs_write_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef fd = EmitSyscall(
            state,
            SyscallOpen,
            builder.BuildPtrToInt(pathCstr, state.I64, "fs_write_path_ptr"),
            LLVMValueRef.CreateConstInt(state.I64, 0x241, false),
            LLVMValueRef.CreateConstInt(state.I64, 420, false),
            "fs_write_open_call");
        builder.BuildStore(fd, fdSlot);
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, fd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_open_failed");
        builder.BuildStore(LoadStringLength(state, textRef, "fs_write_text_len"), remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, textRef, "fs_write_text_ptr"), cursorSlot);
        builder.BuildCondBr(openFailed, returnErrorBlock, loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "fs_write_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_done");
        builder.BuildCondBr(done, closeOkBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef cursorAddress = builder.BuildLoad2(state.I64, cursorSlot, "fs_write_cursor_value");
        LLVMValueRef bytesWritten = EmitSyscall(
            state,
            SyscallWrite,
            builder.BuildLoad2(state.I64, fdSlot, "fs_write_fd_value"),
            cursorAddress,
            remaining,
            "fs_write_write_call");
        LLVMValueRef writeFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, bytesWritten, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_failed");
        builder.BuildCondBr(writeFailed, maybeCloseErrorBlock, advanceBlock);

        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildSub(remaining, bytesWritten, "fs_write_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursorAddress, bytesWritten, "fs_write_cursor_next"), cursorSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(closeOkBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_write_close_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_write_close_ok_call");
        builder.BuildStore(EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(maybeCloseErrorBlock);
        LLVMValueRef fdValue = builder.BuildLoad2(state.I64, fdSlot, "fs_write_error_fd");
        LLVMValueRef shouldClose = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, fdValue, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_should_close");
        builder.BuildCondBr(shouldClose, closeErrorBlock, returnErrorBlock);

        builder.PositionAtEnd(closeErrorBlock);
        EmitSyscall(
            state,
            SyscallClose,
            builder.BuildLoad2(state.I64, fdSlot, "fs_write_close_error_fd"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_write_close_error_call");
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(returnErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_write_result_value");
    }

    private static LLVMValueRef EmitLinuxFileExists(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_exists_path");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_exists_result");
        var openBlock = state.Function.AppendBasicBlock("fs_exists_open");
        var foundBlock = state.Function.AppendBasicBlock("fs_exists_found");
        var missingBlock = state.Function.AppendBasicBlock("fs_exists_missing");
        var continueBlock = state.Function.AppendBasicBlock("fs_exists_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef fd = EmitSyscall(
            state,
            SyscallOpen,
            builder.BuildPtrToInt(pathCstr, state.I64, "fs_exists_path_ptr"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_exists_open_call");
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, fd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_exists_open_failed");
        builder.BuildCondBr(openFailed, missingBlock, foundBlock);

        builder.PositionAtEnd(foundBlock);
        EmitSyscall(
            state,
            SyscallClose,
            fd,
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "fs_exists_close_call");
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 1, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(missingBlock);
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 0, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_exists_result_value");
    }

    private static LLVMValueRef EmitWindowsFileReadText(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_read_path");
        LLVMValueRef handleSlot = builder.BuildAlloca(state.I64, "fs_read_handle");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_read_result");
        LLVMValueRef bytesReadSlot = builder.BuildAlloca(state.I32, "fs_read_bytes_read");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), handleSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var openBlock = state.Function.AppendBasicBlock("fs_read_win_open");
        var readBlock = state.Function.AppendBasicBlock("fs_read_win_read");
        var utf8Block = state.Function.AppendBasicBlock("fs_read_win_utf8");
        var closeOkBlock = state.Function.AppendBasicBlock("fs_read_win_close_ok");
        var closeInvalidBlock = state.Function.AppendBasicBlock("fs_read_win_close_invalid");
        var closeErrorBlock = state.Function.AppendBasicBlock("fs_read_win_close_error");
        var returnErrorBlock = state.Function.AppendBasicBlock("fs_read_win_return_error");
        var continueBlock = state.Function.AppendBasicBlock("fs_read_win_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            unchecked((int)0x80000000),
            1,
            3,
            "fs_read_create_file");
        builder.BuildStore(handle, handleSlot);
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, handle, LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), "fs_read_handle_invalid");
        builder.BuildCondBr(openFailed, returnErrorBlock, readBlock);

        builder.PositionAtEnd(readBlock);
        LLVMValueRef stringRef = EmitAllocDynamic(state, LLVMValueRef.CreateConstInt(state.I64, MaxFileReadBytes + 8, false));
        StoreMemory(state, stringRef, 0, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_win_len_init");
        LLVMValueRef readSucceeded = EmitWindowsReadFile(
            state,
            builder.BuildLoad2(state.I64, handleSlot, "fs_read_handle_value"),
            GetStringBytesPointer(state, stringRef, "fs_read_win_bytes"),
            LLVMValueRef.CreateConstInt(state.I32, MaxFileReadBytes, false),
            bytesReadSlot,
            "fs_read_win_read_call");
        builder.BuildStore(builder.BuildZExt(builder.BuildLoad2(state.I32, bytesReadSlot, "fs_read_bytes_read_value"), state.I64, "fs_read_bytes_i64"), GetMemoryPointer(state, stringRef, 0, "fs_read_win_len_ptr"));
        builder.BuildCondBr(readSucceeded, utf8Block, closeErrorBlock);

        builder.PositionAtEnd(utf8Block);
        LLVMValueRef utf8Valid = EmitValidateUtf8(
            state,
            GetStringBytesPointer(state, stringRef, "fs_read_win_utf8_ptr"),
            LoadStringLength(state, stringRef, "fs_read_win_utf8_len"),
            "fs_read_win_utf8");
        LLVMValueRef isUtf8Valid = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, utf8Valid, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_read_win_is_utf8_valid");
        builder.BuildCondBr(isUtf8Valid, closeOkBlock, closeInvalidBlock);

        builder.PositionAtEnd(closeOkBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_read_close_handle"), "fs_read_close_ok");
        builder.BuildStore(EmitResultOk(state, stringRef), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeInvalidBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_read_invalid_handle"), "fs_read_close_invalid");
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileReadInvalidUtf8Message)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeErrorBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_read_error_handle"), "fs_read_close_error");
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(returnErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileReadFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_read_win_result_value");
    }

    private static LLVMValueRef EmitWindowsFileWriteText(LlvmCodegenState state, LLVMValueRef pathRef, LLVMValueRef textRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_write_path");
        LLVMValueRef handleSlot = builder.BuildAlloca(state.I64, "fs_write_handle");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "fs_write_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "fs_write_cursor");
        LLVMValueRef bytesWrittenSlot = builder.BuildAlloca(state.I32, "fs_write_bytes_written");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_write_result");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), handleSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);

        var openBlock = state.Function.AppendBasicBlock("fs_write_win_open");
        var loopCheckBlock = state.Function.AppendBasicBlock("fs_write_win_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("fs_write_win_loop_body");
        var advanceBlock = state.Function.AppendBasicBlock("fs_write_win_advance");
        var closeOkBlock = state.Function.AppendBasicBlock("fs_write_win_close_ok");
        var closeErrorBlock = state.Function.AppendBasicBlock("fs_write_win_close_error");
        var returnErrorBlock = state.Function.AppendBasicBlock("fs_write_win_return_error");
        var continueBlock = state.Function.AppendBasicBlock("fs_write_win_continue");

        builder.BuildBr(openBlock);

        builder.PositionAtEnd(openBlock);
        LLVMValueRef handle = EmitWindowsCreateFile(
            state,
            pathCstr,
            0x40000000,
            0,
            2,
            "fs_write_create_file");
        builder.BuildStore(handle, handleSlot);
        builder.BuildStore(LoadStringLength(state, textRef, "fs_write_win_text_len"), remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, textRef, "fs_write_win_text_ptr"), cursorSlot);
        LLVMValueRef openFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, handle, LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), "fs_write_handle_invalid");
        builder.BuildCondBr(openFailed, returnErrorBlock, loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "fs_write_win_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_win_done");
        builder.BuildCondBr(done, closeOkBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef chunkSize = builder.BuildSelect(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, remaining, LLVMValueRef.CreateConstInt(state.I64, uint.MaxValue, false), "fs_write_win_chunk_gt"),
            LLVMValueRef.CreateConstInt(state.I64, uint.MaxValue, false),
            remaining,
            "fs_write_win_chunk_size");
        LLVMValueRef wrote = EmitWindowsWriteFile(
            state,
            builder.BuildLoad2(state.I64, handleSlot, "fs_write_handle_value"),
            builder.BuildIntToPtr(builder.BuildLoad2(state.I64, cursorSlot, "fs_write_cursor_value"), state.I8Ptr, "fs_write_cursor_ptr"),
            builder.BuildTrunc(chunkSize, state.I32, "fs_write_chunk_i32"),
            bytesWrittenSlot,
            "fs_write_win_write_call");
        builder.BuildCondBr(wrote, advanceBlock, closeErrorBlock);

        builder.PositionAtEnd(advanceBlock);
        LLVMValueRef bytesWritten = builder.BuildZExt(builder.BuildLoad2(state.I32, bytesWrittenSlot, "fs_write_bytes_written_value"), state.I64, "fs_write_bytes_written_i64");
        LLVMValueRef wroteZero = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bytesWritten, LLVMValueRef.CreateConstInt(state.I64, 0, false), "fs_write_wrote_zero");
        var zeroWriteBlock = state.Function.AppendBasicBlock("fs_write_win_zero");
        var updateBlock = state.Function.AppendBasicBlock("fs_write_win_update");
        builder.BuildCondBr(wroteZero, zeroWriteBlock, updateBlock);

        builder.PositionAtEnd(zeroWriteBlock);
        builder.BuildBr(closeErrorBlock);

        builder.PositionAtEnd(updateBlock);
        LLVMValueRef cursorValue = builder.BuildLoad2(state.I64, cursorSlot, "fs_write_cursor_current");
        builder.BuildStore(builder.BuildSub(remaining, bytesWritten, "fs_write_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursorValue, bytesWritten, "fs_write_cursor_next"), cursorSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(closeOkBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_write_close_handle"), "fs_write_close_ok");
        builder.BuildStore(EmitResultOk(state, EmitUnitValue(state)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeErrorBlock);
        EmitWindowsCloseHandle(state, builder.BuildLoad2(state.I64, handleSlot, "fs_write_error_handle"), "fs_write_close_error");
        builder.BuildBr(returnErrorBlock);

        builder.PositionAtEnd(returnErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, FileWriteFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_write_win_result_value");
    }

    private static LLVMValueRef EmitWindowsFileExists(LlvmCodegenState state, LLVMValueRef pathRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef pathCstr = EmitStringToCString(state, pathRef, "fs_exists_path");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "fs_exists_win_result");
        var checkBlock = state.Function.AppendBasicBlock("fs_exists_win_check");
        var missingBlock = state.Function.AppendBasicBlock("fs_exists_win_missing");
        var foundBlock = state.Function.AppendBasicBlock("fs_exists_win_found");
        var continueBlock = state.Function.AppendBasicBlock("fs_exists_win_continue");

        builder.BuildBr(checkBlock);

        builder.PositionAtEnd(checkBlock);
        LLVMValueRef attrs = EmitWindowsGetFileAttributes(state, pathCstr, "fs_exists_get_attrs");
        LLVMValueRef missing = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, attrs, LLVMValueRef.CreateConstInt(state.I32, uint.MaxValue, false), "fs_exists_missing");
        builder.BuildCondBr(missing, missingBlock, foundBlock);

        builder.PositionAtEnd(foundBlock);
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 1, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(missingBlock);
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 0, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "fs_exists_win_result_value");
    }

    private static LLVMValueRef EmitTcpConnect(LlvmCodegenState state, LLVMValueRef hostRef, LLVMValueRef port)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxTcpConnect(state, hostRef, port)
            : EmitWindowsTcpConnect(state, hostRef, port);
    }

    private static LLVMValueRef EmitTcpSend(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef textRef)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxTcpSend(state, socket, textRef)
            : EmitWindowsTcpSend(state, socket, textRef);
    }

    private static LLVMValueRef EmitTcpReceive(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef maxBytes)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxTcpReceive(state, socket, maxBytes)
            : EmitWindowsTcpReceive(state, socket, maxBytes);
    }

    private static LLVMValueRef EmitTcpClose(LlvmCodegenState state, LLVMValueRef socket)
    {
        return state.Flavor == LlvmCodegenFlavor.Linux
            ? EmitLinuxTcpClose(state, socket)
            : EmitWindowsTcpClose(state, socket);
    }

    private static LLVMValueRef EmitHttpRequest(LlvmCodegenState state, LLVMValueRef urlRef, LLVMValueRef bodyRef, bool hasBody)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "http_result");
        LLVMValueRef hostSlot = builder.BuildAlloca(state.I64, "http_host");
        LLVMValueRef pathSlot = builder.BuildAlloca(state.I64, "http_path");
        LLVMValueRef portSlot = builder.BuildAlloca(state.I64, "http_port");
        LLVMValueRef responseSlot = builder.BuildAlloca(state.I64, "http_response");
        LLVMValueRef socketSlot = builder.BuildAlloca(state.I64, "http_socket");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, "http_index");
        LLVMValueRef hostStartSlot = builder.BuildAlloca(state.I64, "http_host_start");
        LLVMValueRef hostEndSlot = builder.BuildAlloca(state.I64, "http_host_end");
        LLVMValueRef pathStartSlot = builder.BuildAlloca(state.I64, "http_path_start");
        LLVMValueRef pathLenSlot = builder.BuildAlloca(state.I64, "http_path_len");
        LLVMValueRef portValueSlot = builder.BuildAlloca(state.I64, "http_port_value");
        LLVMValueRef portDigitsSlot = builder.BuildAlloca(state.I64, "http_port_digits");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), hostSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), pathSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 80, false), portSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), responseSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), socketSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 7, false), hostStartSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), hostEndSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), pathStartSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), pathLenSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 80, false), portValueSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), portDigitsSlot);

        LLVMValueRef urlLen = LoadStringLength(state, urlRef, "http_url_len");
        LLVMValueRef urlBytes = GetStringBytesPointer(state, urlRef, "http_url_bytes");

        var httpsCheckBlock = state.Function.AppendBasicBlock("http_https_check");
        var httpCheckBlock = state.Function.AppendBasicBlock("http_http_check");
        var scanHostSetupBlock = state.Function.AppendBasicBlock("http_scan_host_setup");
        var scanHostBlock = state.Function.AppendBasicBlock("http_scan_host");
        var parsePortBlock = state.Function.AppendBasicBlock("http_parse_port");
        var parsePortLoopBlock = state.Function.AppendBasicBlock("http_parse_port_loop");
        var parsePortInspectBlock = state.Function.AppendBasicBlock("http_parse_port_inspect");
        var havePathBlock = state.Function.AppendBasicBlock("http_have_path");
        var defaultPathBlock = state.Function.AppendBasicBlock("http_default_path");
        var connectBlock = state.Function.AppendBasicBlock("http_connect");
        var sendBlock = state.Function.AppendBasicBlock("http_send");
        var recvLoopBlock = state.Function.AppendBasicBlock("http_recv_loop");
        var recvInspectBlock = state.Function.AppendBasicBlock("http_recv_inspect");
        var recvDoneBlock = state.Function.AppendBasicBlock("http_recv_done");
        var parseResponseBlock = state.Function.AppendBasicBlock("http_parse_response");
        var httpsErrorBlock = state.Function.AppendBasicBlock("http_https_error");
        var closeErrorBlock = state.Function.AppendBasicBlock("http_close_error");
        var malformedResponseBlock = state.Function.AppendBasicBlock("http_malformed_response");
        var chunkedErrorBlock = state.Function.AppendBasicBlock("http_chunked_error");
        var continueBlock = state.Function.AppendBasicBlock("http_continue");

        builder.BuildBr(httpsCheckBlock);

        builder.PositionAtEnd(httpsCheckBlock);
        LLVMValueRef httpsPrefix = EmitHeapStringLiteral(state, "https://");
        LLVMValueRef isHttps = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntNE,
            EmitStartsWith(state, urlRef, httpsPrefix, "http_is_https"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "http_is_https_bool");
        builder.BuildCondBr(isHttps, httpsErrorBlock, httpCheckBlock);

        builder.PositionAtEnd(httpCheckBlock);
        LLVMValueRef httpPrefix = EmitHeapStringLiteral(state, "http://");
        LLVMValueRef isHttp = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntNE,
            EmitStartsWith(state, urlRef, httpPrefix, "http_is_http"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "http_is_http_bool");
        var malformedUrlBlock = state.Function.AppendBasicBlock("http_malformed_url");
        builder.BuildCondBr(isHttp, scanHostSetupBlock, malformedUrlBlock);

        builder.PositionAtEnd(malformedUrlBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedUrlMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(scanHostSetupBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 7, false), indexSlot);
        builder.BuildBr(scanHostBlock);

        builder.PositionAtEnd(scanHostBlock);
        LLVMValueRef hostLoopIndex = builder.BuildLoad2(state.I64, indexSlot, "http_host_loop_index");
        LLVMValueRef hostLoopDone = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, hostLoopIndex, urlLen, "http_host_loop_done");
        var hostInspectBlock = state.Function.AppendBasicBlock("http_host_inspect");
        builder.BuildCondBr(hostLoopDone, defaultPathBlock, hostInspectBlock);

        builder.PositionAtEnd(hostInspectBlock);
        LLVMValueRef hostByte = LoadByteAt(state, urlBytes, hostLoopIndex, "http_host_byte");
        LLVMValueRef isColon = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostByte, LLVMValueRef.CreateConstInt(state.I8, (byte)':', false), "http_host_is_colon");
        var hostCheckSlashBlock = state.Function.AppendBasicBlock("http_host_check_slash");
        builder.BuildCondBr(isColon, parsePortBlock, hostCheckSlashBlock);

        builder.PositionAtEnd(hostCheckSlashBlock);
        LLVMValueRef isSlash = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'/', false), "http_host_is_slash");
        var hostRejectBlock = state.Function.AppendBasicBlock("http_host_reject");
        var hostAdvanceBlock = state.Function.AppendBasicBlock("http_host_advance");
        builder.BuildCondBr(isSlash, defaultPathBlock, hostRejectBlock);

        builder.PositionAtEnd(hostRejectBlock);
        LLVMValueRef isQuestion = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'?', false), "http_host_is_question");
        var hostHashCheckBlock = state.Function.AppendBasicBlock("http_host_hash_check");
        builder.BuildCondBr(isQuestion, malformedUrlBlock, hostHashCheckBlock);

        builder.PositionAtEnd(hostHashCheckBlock);
        LLVMValueRef isHash = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'#', false), "http_host_is_hash");
        builder.BuildCondBr(isHash, malformedUrlBlock, hostAdvanceBlock);

        builder.PositionAtEnd(hostAdvanceBlock);
        builder.BuildStore(builder.BuildAdd(hostLoopIndex, LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_host_index_next"), indexSlot);
        builder.BuildBr(scanHostBlock);

        builder.PositionAtEnd(parsePortBlock);
        LLVMValueRef hostEnd = builder.BuildLoad2(state.I64, indexSlot, "http_host_end");
        LLVMValueRef hostLenValue = builder.BuildSub(hostEnd, LLVMValueRef.CreateConstInt(state.I64, 7, false), "http_host_len_before_port");
        LLVMValueRef missingHost = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostLenValue, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_missing_host");
        var parsePortSetupBlock = state.Function.AppendBasicBlock("http_parse_port_setup");
        builder.BuildCondBr(missingHost, malformedUrlBlock, parsePortSetupBlock);

        builder.PositionAtEnd(parsePortSetupBlock);
        builder.BuildStore(hostEnd, hostEndSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), portValueSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), portDigitsSlot);
        builder.BuildStore(builder.BuildAdd(hostEnd, LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_port_index_start"), indexSlot);
        builder.BuildBr(parsePortLoopBlock);

        builder.PositionAtEnd(parsePortLoopBlock);
        LLVMValueRef portIndex = builder.BuildLoad2(state.I64, indexSlot, "http_port_index");
        LLVMValueRef portDone = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, portIndex, urlLen, "http_port_done");
        builder.BuildCondBr(portDone, defaultPathBlock, parsePortInspectBlock);

        builder.PositionAtEnd(parsePortInspectBlock);
        LLVMValueRef portByte = LoadByteAt(state, urlBytes, portIndex, "http_port_byte");
        LLVMValueRef portIsSlash = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, portByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'/', false), "http_port_is_slash");
        var portDigitCheckBlock = state.Function.AppendBasicBlock("http_port_digit_check");
        builder.BuildCondBr(portIsSlash, defaultPathBlock, portDigitCheckBlock);

        builder.PositionAtEnd(portDigitCheckBlock);
        LLVMValueRef portDigitValue = builder.BuildZExt(portByte, state.I64, "http_port_digit_value");
        LLVMValueRef portIsDigit = BuildByteRangeCheck(state, portDigitValue, (byte)'0', (byte)'9', "http_port_digit_range");
        var portAdvanceBlock = state.Function.AppendBasicBlock("http_port_advance");
        builder.BuildCondBr(portIsDigit, portAdvanceBlock, malformedUrlBlock);

        builder.PositionAtEnd(portAdvanceBlock);
        LLVMValueRef currentPort = builder.BuildLoad2(state.I64, portValueSlot, "http_port_current");
        LLVMValueRef parsedDigit = builder.BuildSub(portDigitValue, LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "http_parsed_digit");
        LLVMValueRef nextPort = builder.BuildAdd(builder.BuildMul(currentPort, LLVMValueRef.CreateConstInt(state.I64, 10, false), "http_port_mul"), parsedDigit, "http_port_next");
        LLVMValueRef tooLargePort = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, nextPort, LLVMValueRef.CreateConstInt(state.I64, 65535, false), "http_port_too_large");
        var storePortBlock = state.Function.AppendBasicBlock("http_store_port");
        builder.BuildCondBr(tooLargePort, malformedUrlBlock, storePortBlock);

        builder.PositionAtEnd(storePortBlock);
        builder.BuildStore(nextPort, portValueSlot);
        builder.BuildStore(builder.BuildAdd(builder.BuildLoad2(state.I64, portDigitsSlot, "http_port_digits_value"), LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_port_digits_next"), portDigitsSlot);
        builder.BuildStore(builder.BuildAdd(portIndex, LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_port_index_next"), indexSlot);
        builder.BuildBr(parsePortLoopBlock);

        builder.PositionAtEnd(defaultPathBlock);
        LLVMValueRef finalHostEnd = builder.BuildLoad2(state.I64, hostEndSlot, "http_final_host_end");
        LLVMValueRef hostEndUnset = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, finalHostEnd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_host_end_unset");
        var setHostEndBlock = state.Function.AppendBasicBlock("http_set_host_end");
        var buildHostBlock = state.Function.AppendBasicBlock("http_build_host");
        builder.BuildCondBr(hostEndUnset, setHostEndBlock, buildHostBlock);

        builder.PositionAtEnd(setHostEndBlock);
        LLVMValueRef currentIndex = builder.BuildLoad2(state.I64, indexSlot, "http_current_index");
        LLVMValueRef hostLenAtEnd = builder.BuildSub(currentIndex, LLVMValueRef.CreateConstInt(state.I64, 7, false), "http_host_len_at_end");
        LLVMValueRef noHost = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hostLenAtEnd, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_no_host");
        builder.BuildCondBr(noHost, malformedUrlBlock, buildHostBlock);

        builder.PositionAtEnd(buildHostBlock);
        LLVMValueRef actualHostEnd = builder.BuildSelect(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, builder.BuildLoad2(state.I64, hostEndSlot, "http_host_end_existing"), LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_host_end_is_zero"),
            builder.BuildLoad2(state.I64, indexSlot, "http_host_end_from_index"),
            builder.BuildLoad2(state.I64, hostEndSlot, "http_host_end_final"),
            "http_actual_host_end");
        LLVMValueRef actualHostLen = builder.BuildSub(actualHostEnd, LLVMValueRef.CreateConstInt(state.I64, 7, false), "http_actual_host_len");
        LLVMValueRef hostPtr = builder.BuildGEP2(state.I8, urlBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 7, false) }, "http_host_ptr");
        builder.BuildStore(EmitHeapStringSliceFromBytesPointer(state, hostPtr, actualHostLen, "http_host"), hostSlot);
        LLVMValueRef digitsCount = builder.BuildLoad2(state.I64, portDigitsSlot, "http_digits_count");
        LLVMValueRef hasPortDigits = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, digitsCount, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_has_port_digits");
        var storeParsedPortBlock = state.Function.AppendBasicBlock("http_store_parsed_port");
        builder.BuildCondBr(hasPortDigits, storeParsedPortBlock, havePathBlock);

        builder.PositionAtEnd(storeParsedPortBlock);
        builder.BuildStore(builder.BuildLoad2(state.I64, portValueSlot, "http_port_value_final"), portSlot);
        builder.BuildBr(havePathBlock);

        builder.PositionAtEnd(havePathBlock);
        LLVMValueRef pathIndex = builder.BuildLoad2(state.I64, indexSlot, "http_path_index");
        LLVMValueRef hasExplicitPath = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, pathIndex, urlLen, "http_has_explicit_path");
        var explicitPathBlock = state.Function.AppendBasicBlock("http_explicit_path");
        var defaultPathStoreBlock = state.Function.AppendBasicBlock("http_default_path_store");
        builder.BuildCondBr(hasExplicitPath, explicitPathBlock, defaultPathStoreBlock);

        builder.PositionAtEnd(explicitPathBlock);
        LLVMValueRef explicitPathPtr = builder.BuildGEP2(state.I8, urlBytes, new[] { pathIndex }, "http_explicit_path_ptr");
        LLVMValueRef explicitPathLen = builder.BuildSub(urlLen, pathIndex, "http_explicit_path_len");
        builder.BuildStore(EmitHeapStringSliceFromBytesPointer(state, explicitPathPtr, explicitPathLen, "http_path"), pathSlot);
        builder.BuildBr(connectBlock);

        builder.PositionAtEnd(defaultPathStoreBlock);
        builder.BuildStore(EmitHeapStringLiteral(state, "/"), pathSlot);
        builder.BuildBr(connectBlock);

        builder.PositionAtEnd(connectBlock);
        LLVMValueRef connectResult = EmitTcpConnect(state, builder.BuildLoad2(state.I64, hostSlot, "http_host_value"), builder.BuildLoad2(state.I64, portSlot, "http_port_value"));
        LLVMValueRef connectTag = LoadMemory(state, connectResult, 0, "http_connect_tag");
        LLVMValueRef connectFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, connectTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_connect_failed");
        var connectStoreBlock = state.Function.AppendBasicBlock("http_connect_store");
        builder.BuildCondBr(connectFailed, connectStoreBlock, sendBlock);

        builder.PositionAtEnd(connectStoreBlock);
        builder.BuildStore(connectResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(sendBlock);
        LLVMValueRef socketValue = LoadMemory(state, connectResult, 8, "http_socket_value");
        builder.BuildStore(socketValue, socketSlot);
        LLVMValueRef requestRef = EmitHttpRequestString(state, builder.BuildLoad2(state.I64, pathSlot, "http_path_value"), builder.BuildLoad2(state.I64, hostSlot, "http_host_header_value"), bodyRef, hasBody);
        LLVMValueRef sendResult = EmitTcpSend(state, socketValue, requestRef);
        LLVMValueRef sendTag = LoadMemory(state, sendResult, 0, "http_send_tag");
        LLVMValueRef sendFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sendTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_send_failed");
        var sendErrorBlock = state.Function.AppendBasicBlock("http_send_error");
        builder.BuildCondBr(sendFailed, sendErrorBlock, recvLoopBlock);

        builder.PositionAtEnd(sendErrorBlock);
        EmitTcpClose(state, builder.BuildLoad2(state.I64, socketSlot, "http_send_error_socket"));
        builder.BuildStore(sendResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(recvLoopBlock);
        LLVMValueRef recvResult = EmitTcpReceive(state, builder.BuildLoad2(state.I64, socketSlot, "http_recv_socket"), LLVMValueRef.CreateConstInt(state.I64, 65536, false));
        LLVMValueRef recvTag = LoadMemory(state, recvResult, 0, "http_recv_tag");
        LLVMValueRef recvFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, recvTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_recv_failed");
        var recvErrorBlock = state.Function.AppendBasicBlock("http_recv_error");
        builder.BuildCondBr(recvFailed, recvErrorBlock, recvInspectBlock);

        builder.PositionAtEnd(recvErrorBlock);
        EmitTcpClose(state, builder.BuildLoad2(state.I64, socketSlot, "http_recv_error_socket"));
        builder.BuildStore(recvResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(recvInspectBlock);
        LLVMValueRef chunkRef = LoadMemory(state, recvResult, 8, "http_chunk_ref");
        LLVMValueRef chunkLen = LoadStringLength(state, chunkRef, "http_chunk_len");
        LLVMValueRef chunkEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, chunkLen, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_chunk_empty");
        var recvAppendBlock = state.Function.AppendBasicBlock("http_recv_append");
        builder.BuildCondBr(chunkEmpty, recvDoneBlock, recvAppendBlock);

        builder.PositionAtEnd(recvAppendBlock);
        LLVMValueRef currentResponse = builder.BuildLoad2(state.I64, responseSlot, "http_current_response");
        LLVMValueRef hasResponse = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, currentResponse, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_has_response");
        var concatResponseBlock = state.Function.AppendBasicBlock("http_concat_response");
        var storeFirstChunkBlock = state.Function.AppendBasicBlock("http_store_first_chunk");
        builder.BuildCondBr(hasResponse, concatResponseBlock, storeFirstChunkBlock);

        builder.PositionAtEnd(storeFirstChunkBlock);
        builder.BuildStore(chunkRef, responseSlot);
        builder.BuildBr(recvLoopBlock);

        builder.PositionAtEnd(concatResponseBlock);
        builder.BuildStore(EmitStringConcat(state, currentResponse, chunkRef), responseSlot);
        builder.BuildBr(recvLoopBlock);

        builder.PositionAtEnd(recvDoneBlock);
        LLVMValueRef closeResult = EmitTcpClose(state, builder.BuildLoad2(state.I64, socketSlot, "http_close_socket"));
        LLVMValueRef closeTag = LoadMemory(state, closeResult, 0, "http_close_tag");
        LLVMValueRef closeFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, closeTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_close_failed");
        builder.BuildCondBr(closeFailed, closeErrorBlock, parseResponseBlock);

        builder.PositionAtEnd(parseResponseBlock);
        LLVMValueRef responseRef = builder.BuildLoad2(state.I64, responseSlot, "http_response_value");
        LLVMValueRef emptyResponse = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, responseRef, LLVMValueRef.CreateConstInt(state.I64, 0, false), "http_empty_response");
        var ensureEmptyResponseBlock = state.Function.AppendBasicBlock("http_ensure_empty_response");
        var parseResponseContinueBlock = state.Function.AppendBasicBlock("http_parse_response_continue");
        builder.BuildCondBr(emptyResponse, ensureEmptyResponseBlock, parseResponseContinueBlock);

        builder.PositionAtEnd(ensureEmptyResponseBlock);
        builder.BuildStore(EmitHeapStringLiteral(state, string.Empty), responseSlot);
        builder.BuildBr(parseResponseContinueBlock);

        builder.PositionAtEnd(parseResponseContinueBlock);
        LLVMValueRef finalResponse = builder.BuildLoad2(state.I64, responseSlot, "http_final_response");
        LLVMValueRef responseLen = LoadStringLength(state, finalResponse, "http_response_len");
        LLVMValueRef responseTooShort = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, responseLen, LLVMValueRef.CreateConstInt(state.I64, 12, false), "http_response_too_short");
        var parseHeadersBlock = state.Function.AppendBasicBlock("http_parse_headers");
        builder.BuildCondBr(responseTooShort, malformedResponseBlock, parseHeadersBlock);

        builder.PositionAtEnd(parseHeadersBlock);
        LLVMValueRef responseBytes = GetStringBytesPointer(state, finalResponse, "http_response_bytes");
        LLVMValueRef separatorIndex = EmitFindByteSequence(state, responseBytes, responseLen, "\r\n\r\n"u8.ToArray(), "http_separator");
        LLVMValueRef hasSeparator = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, separatorIndex, LLVMValueRef.CreateConstInt(state.I64, 0, true), "http_has_separator");
        var parseStatusBlock = state.Function.AppendBasicBlock("http_parse_status");
        builder.BuildCondBr(hasSeparator, parseStatusBlock, malformedResponseBlock);

        builder.PositionAtEnd(parseStatusBlock);
        LLVMValueRef headerLength = separatorIndex;
        LLVMValueRef statusSpaceIndex = EmitFindByte(state, responseBytes, headerLength, 0, (byte)' ', "http_status_space");
        LLVMValueRef hasStatusSpace = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 0, true), "http_has_status_space");
        var parseDigitsBlock = state.Function.AppendBasicBlock("http_parse_digits");
        builder.BuildCondBr(hasStatusSpace, parseDigitsBlock, malformedResponseBlock);

        builder.PositionAtEnd(parseDigitsBlock);
        LLVMValueRef statusEnd = builder.BuildAdd(statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 3, false), "http_status_end");
        LLVMValueRef digitsInRange = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, statusEnd, headerLength, "http_status_digits_in_range");
        var parseDigitsContinueBlock = state.Function.AppendBasicBlock("http_parse_digits_continue");
        builder.BuildCondBr(digitsInRange, parseDigitsContinueBlock, malformedResponseBlock);

        builder.PositionAtEnd(parseDigitsContinueBlock);
        LLVMValueRef hundredsByte = LoadByteAt(state, responseBytes, builder.BuildAdd(statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 1, false), "http_hundreds_idx"), "http_hundreds_byte");
        LLVMValueRef tensByte = LoadByteAt(state, responseBytes, builder.BuildAdd(statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 2, false), "http_tens_idx"), "http_tens_byte");
        LLVMValueRef onesByte = LoadByteAt(state, responseBytes, builder.BuildAdd(statusSpaceIndex, LLVMValueRef.CreateConstInt(state.I64, 3, false), "http_ones_idx"), "http_ones_byte");
        LLVMValueRef digitsValid = builder.BuildAnd(
            builder.BuildAnd(
                BuildByteRangeCheck(state, builder.BuildZExt(hundredsByte, state.I64, "http_hundreds_i64"), (byte)'0', (byte)'9', "http_hundreds_range"),
                BuildByteRangeCheck(state, builder.BuildZExt(tensByte, state.I64, "http_tens_i64"), (byte)'0', (byte)'9', "http_tens_range"),
                "http_digits_first"),
            BuildByteRangeCheck(state, builder.BuildZExt(onesByte, state.I64, "http_ones_i64"), (byte)'0', (byte)'9', "http_ones_range"),
            "http_digits_valid");
        var detectChunkedBlock = state.Function.AppendBasicBlock("http_detect_chunked");
        builder.BuildCondBr(digitsValid, detectChunkedBlock, malformedResponseBlock);

        builder.PositionAtEnd(detectChunkedBlock);
        LLVMValueRef chunkedHeaderIndex = EmitFindByteSequence(state, responseBytes, headerLength, "Transfer-Encoding: chunked"u8.ToArray(), "http_chunked_header");
        LLVMValueRef hasChunkedHeader = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, chunkedHeaderIndex, LLVMValueRef.CreateConstInt(state.I64, 0, true), "http_has_chunked_header");
        var buildBodyBlock = state.Function.AppendBasicBlock("http_build_body");
        builder.BuildCondBr(hasChunkedHeader, chunkedErrorBlock, buildBodyBlock);

        builder.PositionAtEnd(buildBodyBlock);
        LLVMValueRef statusCode = builder.BuildAdd(
            builder.BuildAdd(
                builder.BuildMul(builder.BuildSub(builder.BuildZExt(hundredsByte, state.I64, "http_hundreds_code"), LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "http_hundreds_digit"), LLVMValueRef.CreateConstInt(state.I64, 100, false), "http_hundreds_mul"),
                builder.BuildMul(builder.BuildSub(builder.BuildZExt(tensByte, state.I64, "http_tens_code"), LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "http_tens_digit"), LLVMValueRef.CreateConstInt(state.I64, 10, false), "http_tens_mul"),
                "http_status_prefix_sum"),
            builder.BuildSub(builder.BuildZExt(onesByte, state.I64, "http_ones_code"), LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "http_ones_digit"),
            "http_status_code");
        LLVMValueRef bodyStart = builder.BuildAdd(separatorIndex, LLVMValueRef.CreateConstInt(state.I64, 4, false), "http_body_start");
        LLVMValueRef bodyLength = builder.BuildSub(responseLen, bodyStart, "http_body_len");
        LLVMValueRef bodyBytes = builder.BuildGEP2(state.I8, responseBytes, new[] { bodyStart }, "http_body_ptr");
        LLVMValueRef bodyString = EmitHeapStringSliceFromBytesPointer(state, bodyBytes, bodyLength, "http_body");
        LLVMValueRef statusOk = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, statusCode, LLVMValueRef.CreateConstInt(state.I64, 200, false), "http_status_ge_200"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, statusCode, LLVMValueRef.CreateConstInt(state.I64, 299, false), "http_status_le_299"),
            "http_status_ok");
        var statusOkBlock = state.Function.AppendBasicBlock("http_status_ok_block");
        var statusErrorBlock = state.Function.AppendBasicBlock("http_status_error_block");
        builder.BuildCondBr(statusOk, statusOkBlock, statusErrorBlock);

        builder.PositionAtEnd(statusOkBlock);
        builder.BuildStore(EmitResultOk(state, bodyString), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(statusErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHttpStatusErrorString(state, statusCode, "http_status_error")), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(httpsErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, HttpHttpsNotSupportedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(closeErrorBlock);
        builder.BuildStore(closeResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(malformedResponseBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, HttpMalformedResponseMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(chunkedErrorBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, HttpUnsupportedTransferEncodingMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "http_result_value");
    }

    private static LLVMValueRef EmitLinuxTcpConnect(LlvmCodegenState state, LLVMValueRef hostRef, LLVMValueRef port)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "tcp_connect_result");
        LLVMValueRef socketSlot = builder.BuildAlloca(state.I64, "tcp_connect_socket");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), socketSlot);
        LLVMValueRef resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_resolve");
        LLVMValueRef resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_resolve_tag");
        LLVMValueRef resolveFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, resolveTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_resolve_failed");
        var resolveErrorBlock = state.Function.AppendBasicBlock("tcp_connect_resolve_error");
        var validatePortBlock = state.Function.AppendBasicBlock("tcp_connect_validate_port");
        var openSocketBlock = state.Function.AppendBasicBlock("tcp_connect_open_socket");
        var connectBlock = state.Function.AppendBasicBlock("tcp_connect_connect");
        var connectFailBlock = state.Function.AppendBasicBlock("tcp_connect_fail");
        var connectCloseBlock = state.Function.AppendBasicBlock("tcp_connect_close_socket");
        var continueBlock = state.Function.AppendBasicBlock("tcp_connect_continue");
        builder.BuildCondBr(resolveFailed, resolveErrorBlock, validatePortBlock);

        builder.PositionAtEnd(resolveErrorBlock);
        builder.BuildStore(resolveResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(validatePortBlock);
        LLVMValueRef validPort = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, port, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_port_gt_zero"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, port, LLVMValueRef.CreateConstInt(state.I64, 65535, false), "tcp_connect_port_le_max"),
            "tcp_connect_port_valid");
        builder.BuildCondBr(validPort, openSocketBlock, connectFailBlock);

        builder.PositionAtEnd(openSocketBlock);
        LLVMValueRef socketValue = EmitSyscall(
            state,
            SyscallSocket,
            LLVMValueRef.CreateConstInt(state.I64, 2, false),
            LLVMValueRef.CreateConstInt(state.I64, 1, false),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "tcp_connect_socket_call");
        builder.BuildStore(socketValue, socketSlot);
        LLVMValueRef socketFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, socketValue, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_socket_failed");
        builder.BuildCondBr(socketFailed, connectFailBlock, connectBlock);

        builder.PositionAtEnd(connectBlock);
        LLVMTypeRef sockaddrType = LLVMTypeRef.CreateArray(state.I8, 16);
        LLVMValueRef sockaddrStorage = builder.BuildAlloca(sockaddrType, "tcp_connect_sockaddr");
        LLVMValueRef sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_sockaddr_bytes");
        LLVMTypeRef i16 = state.Target.Context.Int16Type;
        LLVMTypeRef i16Ptr = LLVMTypeRef.CreatePointer(i16, 0);
        LLVMValueRef sockaddrI64Ptr = builder.BuildBitCast(sockaddrBytes, state.I64Ptr, "tcp_connect_sockaddr_i64");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), sockaddrI64Ptr);
        LLVMValueRef sockaddrTailPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 8, false) }, "tcp_connect_sockaddr_tail");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), builder.BuildBitCast(sockaddrTailPtr, state.I64Ptr, "tcp_connect_sockaddr_tail_i64"));
        builder.BuildStore(LLVMValueRef.CreateConstInt(i16, 2, false), builder.BuildBitCast(sockaddrBytes, i16Ptr, "tcp_connect_family_ptr"));
        LLVMValueRef portPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 2, false) }, "tcp_connect_port_ptr_byte");
        builder.BuildStore(builder.BuildTrunc(EmitByteSwap16(state, port, "tcp_connect_port_network"), i16, "tcp_connect_port_i16"), builder.BuildBitCast(portPtr, i16Ptr, "tcp_connect_port_ptr"));
        LLVMValueRef addrPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 4, false) }, "tcp_connect_addr_ptr_byte");
        builder.BuildStore(builder.BuildTrunc(LoadMemory(state, resolveResult, 8, "tcp_connect_addr_value"), state.I32, "tcp_connect_addr_i32"), builder.BuildBitCast(addrPtr, state.I32Ptr, "tcp_connect_addr_ptr"));
        LLVMValueRef connectResult = EmitSyscall(
            state,
            SyscallConnect,
            builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_socket_value"),
            builder.BuildPtrToInt(sockaddrBytes, state.I64, "tcp_connect_sockaddr_ptr"),
            LLVMValueRef.CreateConstInt(state.I64, 16, false),
            "tcp_connect_call");
        LLVMValueRef connectFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, connectResult, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_failed_bool");
        var connectSuccessBlock = state.Function.AppendBasicBlock("tcp_connect_success");
        builder.BuildCondBr(connectFailed, connectCloseBlock, connectSuccessBlock);

        builder.PositionAtEnd(connectCloseBlock);
        EmitSyscall(state, SyscallClose, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_close_socket_value"), LLVMValueRef.CreateConstInt(state.I64, 0, false), LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_close_call");
        builder.BuildBr(connectFailBlock);

        builder.PositionAtEnd(connectFailBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(connectSuccessBlock);
        builder.BuildStore(EmitResultOk(state, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_success_socket")), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "tcp_connect_result_value");
    }

    private static LLVMValueRef EmitWindowsTcpConnect(LlvmCodegenState state, LLVMValueRef hostRef, LLVMValueRef port)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "tcp_connect_win_result");
        LLVMValueRef socketSlot = builder.BuildAlloca(state.I64, "tcp_connect_win_socket");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), socketSlot);
        LLVMValueRef resolveResult = EmitResolveHostIpv4OrLocalhost(state, hostRef, "tcp_connect_win_resolve");
        LLVMValueRef resolveTag = LoadMemory(state, resolveResult, 0, "tcp_connect_win_resolve_tag");
        LLVMValueRef resolveFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, resolveTag, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_win_resolve_failed");
        var resolveErrorBlock = state.Function.AppendBasicBlock("tcp_connect_win_resolve_error");
        var validatePortBlock = state.Function.AppendBasicBlock("tcp_connect_win_validate_port");
        var initWinsockBlock = state.Function.AppendBasicBlock("tcp_connect_win_init_winsock");
        var openSocketBlock = state.Function.AppendBasicBlock("tcp_connect_win_open_socket");
        var connectBlock = state.Function.AppendBasicBlock("tcp_connect_win_connect");
        var connectCloseBlock = state.Function.AppendBasicBlock("tcp_connect_win_close_socket");
        var connectFailBlock = state.Function.AppendBasicBlock("tcp_connect_win_fail");
        var continueBlock = state.Function.AppendBasicBlock("tcp_connect_win_continue");
        builder.BuildCondBr(resolveFailed, resolveErrorBlock, validatePortBlock);

        builder.PositionAtEnd(resolveErrorBlock);
        builder.BuildStore(resolveResult, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(validatePortBlock);
        LLVMValueRef validPort = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, port, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_win_port_gt_zero"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, port, LLVMValueRef.CreateConstInt(state.I64, 65535, false), "tcp_connect_win_port_le_max"),
            "tcp_connect_win_port_valid");
        builder.BuildCondBr(validPort, initWinsockBlock, connectFailBlock);

        builder.PositionAtEnd(initWinsockBlock);
        LLVMTypeRef wsadataType = LLVMTypeRef.CreateArray(state.I8, 512);
        LLVMValueRef wsadata = builder.BuildAlloca(wsadataType, "tcp_connect_win_wsadata");
        LLVMValueRef winsockStarted = EmitWindowsWsaStartup(state, GetArrayElementPointer(state, wsadataType, wsadata, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_win_wsadata_ptr"), "tcp_connect_win_wsastartup");
        builder.BuildCondBr(winsockStarted, openSocketBlock, connectFailBlock);

        builder.PositionAtEnd(openSocketBlock);
        LLVMValueRef socketValue = EmitWindowsSocket(state, 2, 1, 6, "tcp_connect_win_socket_call");
        builder.BuildStore(socketValue, socketSlot);
        LLVMValueRef socketFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, socketValue, LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), "tcp_connect_win_socket_failed");
        builder.BuildCondBr(socketFailed, connectFailBlock, connectBlock);

        builder.PositionAtEnd(connectBlock);
        LLVMTypeRef sockaddrType = LLVMTypeRef.CreateArray(state.I8, 16);
        LLVMValueRef sockaddrStorage = builder.BuildAlloca(sockaddrType, "tcp_connect_win_sockaddr");
        LLVMValueRef sockaddrBytes = GetArrayElementPointer(state, sockaddrType, sockaddrStorage, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_connect_win_sockaddr_bytes");
        LLVMTypeRef i16 = state.Target.Context.Int16Type;
        LLVMTypeRef i16Ptr = LLVMTypeRef.CreatePointer(i16, 0);
        LLVMValueRef sockaddrI64Ptr = builder.BuildBitCast(sockaddrBytes, state.I64Ptr, "tcp_connect_win_sockaddr_i64");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), sockaddrI64Ptr);
        LLVMValueRef sockaddrTailPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 8, false) }, "tcp_connect_win_sockaddr_tail");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), builder.BuildBitCast(sockaddrTailPtr, state.I64Ptr, "tcp_connect_win_sockaddr_tail_i64"));
        builder.BuildStore(LLVMValueRef.CreateConstInt(i16, 2, false), builder.BuildBitCast(sockaddrBytes, i16Ptr, "tcp_connect_win_family_ptr"));
        LLVMValueRef portPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 2, false) }, "tcp_connect_win_port_ptr_byte");
        builder.BuildStore(builder.BuildTrunc(EmitByteSwap16(state, port, "tcp_connect_win_port_network"), i16, "tcp_connect_win_port_i16"), builder.BuildBitCast(portPtr, i16Ptr, "tcp_connect_win_port_ptr"));
        LLVMValueRef addrPtr = builder.BuildGEP2(state.I8, sockaddrBytes, new[] { LLVMValueRef.CreateConstInt(state.I64, 4, false) }, "tcp_connect_win_addr_ptr_byte");
        builder.BuildStore(builder.BuildTrunc(LoadMemory(state, resolveResult, 8, "tcp_connect_win_addr_value"), state.I32, "tcp_connect_win_addr_i32"), builder.BuildBitCast(addrPtr, state.I32Ptr, "tcp_connect_win_addr_ptr"));
        LLVMValueRef connectResult = EmitWindowsConnect(state, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_win_socket_value"), sockaddrBytes, "tcp_connect_win_connect_call");
        var connectSuccessBlock = state.Function.AppendBasicBlock("tcp_connect_win_success");
        builder.BuildCondBr(connectResult, connectSuccessBlock, connectCloseBlock);

        builder.PositionAtEnd(connectCloseBlock);
        EmitWindowsCloseSocket(state, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_win_close_socket_value"), "tcp_connect_win_close_socket_call");
        builder.BuildBr(connectFailBlock);

        builder.PositionAtEnd(connectFailBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpConnectFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(connectSuccessBlock);
        builder.BuildStore(EmitResultOk(state, builder.BuildLoad2(state.I64, socketSlot, "tcp_connect_win_success_socket")), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "tcp_connect_win_result_value");
    }

    private static LLVMValueRef EmitLinuxTcpSend(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef textRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "tcp_send_result");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "tcp_send_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "tcp_send_cursor");
        LLVMValueRef totalLen = LoadStringLength(state, textRef, "tcp_send_total_len");
        builder.BuildStore(totalLen, remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, textRef, "tcp_send_cursor_start"), cursorSlot);
        var loopCheckBlock = state.Function.AppendBasicBlock("tcp_send_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("tcp_send_loop_body");
        var updateBlock = state.Function.AppendBasicBlock("tcp_send_update");
        var failBlock = state.Function.AppendBasicBlock("tcp_send_fail");
        var continueBlock = state.Function.AppendBasicBlock("tcp_send_continue");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "tcp_send_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_send_done");
        var doneBlock = state.Function.AppendBasicBlock("tcp_send_done_block");
        builder.BuildCondBr(done, doneBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef sent = EmitSyscall(state, SyscallWrite, socket, builder.BuildLoad2(state.I64, cursorSlot, "tcp_send_cursor_value"), remaining, "tcp_send_syscall");
        LLVMValueRef sendFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, sent, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_send_failed");
        builder.BuildCondBr(sendFailed, failBlock, updateBlock);

        builder.PositionAtEnd(updateBlock);
        LLVMValueRef cursor = builder.BuildLoad2(state.I64, cursorSlot, "tcp_send_cursor_current");
        builder.BuildStore(builder.BuildSub(remaining, sent, "tcp_send_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursor, sent, "tcp_send_cursor_next"), cursorSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(doneBlock);
        builder.BuildStore(EmitResultOk(state, totalLen), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(failBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "tcp_send_result_value");
    }

    private static LLVMValueRef EmitWindowsTcpSend(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef textRef)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, "tcp_send_win_result");
        LLVMValueRef remainingSlot = builder.BuildAlloca(state.I64, "tcp_send_win_remaining");
        LLVMValueRef cursorSlot = builder.BuildAlloca(state.I64, "tcp_send_win_cursor");
        LLVMValueRef totalLen = LoadStringLength(state, textRef, "tcp_send_win_total_len");
        builder.BuildStore(totalLen, remainingSlot);
        builder.BuildStore(GetStringBytesAddress(state, textRef, "tcp_send_win_cursor_start"), cursorSlot);
        var loopCheckBlock = state.Function.AppendBasicBlock("tcp_send_win_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("tcp_send_win_loop_body");
        var updateBlock = state.Function.AppendBasicBlock("tcp_send_win_update");
        var failBlock = state.Function.AppendBasicBlock("tcp_send_win_fail");
        var continueBlock = state.Function.AppendBasicBlock("tcp_send_win_continue");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef remaining = builder.BuildLoad2(state.I64, remainingSlot, "tcp_send_win_remaining_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, remaining, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_send_win_done");
        var doneBlock = state.Function.AppendBasicBlock("tcp_send_win_done_block");
        builder.BuildCondBr(done, doneBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef chunk = builder.BuildSelect(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, remaining, LLVMValueRef.CreateConstInt(state.I64, int.MaxValue, false), "tcp_send_win_chunk_gt"),
            LLVMValueRef.CreateConstInt(state.I64, int.MaxValue, false),
            remaining,
            "tcp_send_win_chunk");
        LLVMValueRef sentRaw = EmitWindowsSend(state, socket, builder.BuildIntToPtr(builder.BuildLoad2(state.I64, cursorSlot, "tcp_send_win_cursor_value"), state.I8Ptr, "tcp_send_win_cursor_ptr"), builder.BuildTrunc(chunk, state.I32, "tcp_send_win_chunk_i32"), "tcp_send_win_call");
        LLVMValueRef sendFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, sentRaw, LLVMValueRef.CreateConstInt(state.I32, 0, true), "tcp_send_win_failed");
        builder.BuildCondBr(sendFailed, failBlock, updateBlock);

        builder.PositionAtEnd(updateBlock);
        LLVMValueRef sent = builder.BuildSExt(sentRaw, state.I64, "tcp_send_win_sent");
        LLVMValueRef cursor = builder.BuildLoad2(state.I64, cursorSlot, "tcp_send_win_cursor_current");
        builder.BuildStore(builder.BuildSub(remaining, sent, "tcp_send_win_remaining_next"), remainingSlot);
        builder.BuildStore(builder.BuildAdd(cursor, sent, "tcp_send_win_cursor_next"), cursorSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(doneBlock);
        builder.BuildStore(EmitResultOk(state, totalLen), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(failBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpSendFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, "tcp_send_win_result_value");
    }

    private static LLVMValueRef EmitLinuxTcpReceive(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive", static (s, sock, bytesPtr, max, name) => EmitSyscall(s, SyscallRead, sock, s.Target.Builder.BuildPtrToInt(bytesPtr, s.I64, name + "_ptr"), s.Target.Builder.BuildSExt(max, s.I64, name + "_len"), name));
    }

    private static LLVMValueRef EmitWindowsTcpReceive(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef maxBytes)
    {
        return EmitTcpReceiveCommon(state, socket, maxBytes, "tcp_receive_win", static (s, sock, bytesPtr, max, name) => s.Target.Builder.BuildSExt(EmitWindowsRecv(s, sock, bytesPtr, max, name), s.I64, name + "_sext"));
    }

    private static LLVMValueRef EmitTcpReceiveCommon(
        LlvmCodegenState state,
        LLVMValueRef socket,
        LLVMValueRef maxBytes,
        string prefix,
        Func<LlvmCodegenState, LLVMValueRef, LLVMValueRef, LLVMValueRef, string, LLVMValueRef> emitRead)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        var invalidMaxBlock = state.Function.AppendBasicBlock(prefix + "_invalid_max");
        var readBlock = state.Function.AppendBasicBlock(prefix + "_read");
        var handleReadBlock = state.Function.AppendBasicBlock(prefix + "_handle_read");
        var invalidUtf8Block = state.Function.AppendBasicBlock(prefix + "_invalid_utf8");
        var failBlock = state.Function.AppendBasicBlock(prefix + "_fail");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        LLVMValueRef positiveMax = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, maxBytes, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_positive_max");
        builder.BuildCondBr(positiveMax, readBlock, invalidMaxBlock);

        builder.PositionAtEnd(invalidMaxBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidMaxBytesMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(readBlock);
        LLVMValueRef stringRef = EmitAllocDynamic(state, builder.BuildAdd(maxBytes, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_size"));
        StoreMemory(state, stringRef, 0, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_len_init");
        LLVMValueRef readCount = emitRead(state, socket, GetStringBytesPointer(state, stringRef, prefix + "_bytes"), builder.BuildTrunc(maxBytes, state.I32, prefix + "_max_i32"), prefix + "_read_call");
        LLVMValueRef readFailed = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, readCount, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_read_failed");
        builder.BuildCondBr(readFailed, failBlock, handleReadBlock);

        builder.PositionAtEnd(handleReadBlock);
        StoreMemory(state, stringRef, 0, readCount, prefix + "_len_store");
        LLVMValueRef isEmpty = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, readCount, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_is_empty");
        var successBlock = state.Function.AppendBasicBlock(prefix + "_success");
        var validateBlock = state.Function.AppendBasicBlock(prefix + "_validate");
        builder.BuildCondBr(isEmpty, successBlock, validateBlock);

        builder.PositionAtEnd(validateBlock);
        LLVMValueRef utf8Valid = EmitValidateUtf8(state, GetStringBytesPointer(state, stringRef, prefix + "_validate_bytes"), readCount, prefix + "_utf8");
        LLVMValueRef valid = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, utf8Valid, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_utf8_valid");
        builder.BuildCondBr(valid, successBlock, invalidUtf8Block);

        builder.PositionAtEnd(invalidUtf8Block);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpInvalidUtf8Message)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(successBlock);
        builder.BuildStore(EmitResultOk(state, stringRef), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(failBlock);
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpReceiveFailedMessage)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitLinuxTcpClose(LlvmCodegenState state, LLVMValueRef socket)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef result = EmitSyscall(state, SyscallClose, socket, LLVMValueRef.CreateConstInt(state.I64, 0, false), LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_close_call");
        LLVMValueRef success = builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, result, LLVMValueRef.CreateConstInt(state.I64, 0, false), "tcp_close_success");
        return builder.BuildSelect(success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_result");
    }

    private static LLVMValueRef EmitWindowsTcpClose(LlvmCodegenState state, LLVMValueRef socket)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef closeResult = EmitWindowsCloseSocket(state, socket, "tcp_close_win_call");
        LLVMValueRef success = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, closeResult, LLVMValueRef.CreateConstInt(state.I32, 0, false), "tcp_close_win_success");
        return builder.BuildSelect(success, EmitResultOk(state, EmitUnitValue(state)), EmitResultError(state, EmitHeapStringLiteral(state, TcpCloseFailedMessage)), "tcp_close_win_result");
    }

    private static LLVMValueRef EmitResolveHostIpv4OrLocalhost(LlvmCodegenState state, LLVMValueRef hostRef, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        LLVMValueRef partSlot = builder.BuildAlloca(state.I64, prefix + "_part");
        LLVMValueRef currentSlot = builder.BuildAlloca(state.I64, prefix + "_current");
        LLVMValueRef seenDigitSlot = builder.BuildAlloca(state.I64, prefix + "_seen_digit");
        LLVMValueRef addressSlot = builder.BuildAlloca(state.I64, prefix + "_address");
        builder.BuildStore(EmitResultError(state, EmitHeapStringLiteral(state, TcpResolveFailedMessage)), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), partSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), currentSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), seenDigitSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), addressSlot);

        LLVMValueRef localhostEquals = EmitStringComparison(state, hostRef, EmitStackStringObject(state, "localhost"));
        LLVMValueRef isLocalhost = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, localhostEquals, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_is_localhost");
        LLVMValueRef hostLen = LoadStringLength(state, hostRef, prefix + "_host_len");
        LLVMValueRef hostBytes = GetStringBytesPointer(state, hostRef, prefix + "_host_bytes");
        var localhostBlock = state.Function.AppendBasicBlock(prefix + "_localhost");
        var parseLoopBlock = state.Function.AppendBasicBlock(prefix + "_parse_loop");
        var parseInspectBlock = state.Function.AppendBasicBlock(prefix + "_parse_inspect");
        var digitBlock = state.Function.AppendBasicBlock(prefix + "_digit");
        var dotBlock = state.Function.AppendBasicBlock(prefix + "_dot");
        var failBlock = state.Function.AppendBasicBlock(prefix + "_fail");
        var finalizeBlock = state.Function.AppendBasicBlock(prefix + "_finalize");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        builder.BuildCondBr(isLocalhost, localhostBlock, parseLoopBlock);

        builder.PositionAtEnd(localhostBlock);
        builder.BuildStore(EmitResultOk(state, LLVMValueRef.CreateConstInt(state.I64, 0x0100007FUL, false)), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(parseLoopBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, hostLen, prefix + "_done");
        builder.BuildCondBr(done, finalizeBlock, parseInspectBlock);

        builder.PositionAtEnd(parseInspectBlock);
        LLVMValueRef currentByte = LoadByteAt(state, hostBytes, index, prefix + "_current_byte");
        LLVMValueRef currentByte64 = builder.BuildZExt(currentByte, state.I64, prefix + "_current_byte_i64");
        LLVMValueRef isDigit = BuildByteRangeCheck(state, currentByte64, (byte)'0', (byte)'9', prefix + "_digit_range");
        var dotCheckBlock = state.Function.AppendBasicBlock(prefix + "_dot_check");
        builder.BuildCondBr(isDigit, digitBlock, dotCheckBlock);

        builder.PositionAtEnd(dotCheckBlock);
        LLVMValueRef isDot = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentByte, LLVMValueRef.CreateConstInt(state.I8, (byte)'.', false), prefix + "_is_dot");
        builder.BuildCondBr(isDot, dotBlock, failBlock);

        builder.PositionAtEnd(digitBlock);
        LLVMValueRef currentValue = builder.BuildLoad2(state.I64, currentSlot, prefix + "_current_value");
        LLVMValueRef parsedDigit = builder.BuildSub(currentByte64, LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), prefix + "_parsed_digit");
        LLVMValueRef nextValue = builder.BuildAdd(builder.BuildMul(currentValue, LLVMValueRef.CreateConstInt(state.I64, 10, false), prefix + "_mul"), parsedDigit, prefix + "_next_value");
        LLVMValueRef valueTooLarge = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, nextValue, LLVMValueRef.CreateConstInt(state.I64, 255, false), prefix + "_value_too_large");
        var storeDigitBlock = state.Function.AppendBasicBlock(prefix + "_store_digit");
        builder.BuildCondBr(valueTooLarge, failBlock, storeDigitBlock);

        builder.PositionAtEnd(storeDigitBlock);
        builder.BuildStore(nextValue, currentSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), seenDigitSlot);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_next"), indexSlot);
        builder.BuildBr(parseLoopBlock);

        builder.PositionAtEnd(dotBlock);
        LLVMValueRef seenDigit = builder.BuildLoad2(state.I64, seenDigitSlot, prefix + "_seen_digit_value");
        LLVMValueRef part = builder.BuildLoad2(state.I64, partSlot, prefix + "_part_value");
        LLVMValueRef dotValid = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, seenDigit, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_dot_seen_digit"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, part, LLVMValueRef.CreateConstInt(state.I64, 3, false), prefix + "_dot_part_lt_three"),
            prefix + "_dot_valid");
        var storeDotBlock = state.Function.AppendBasicBlock(prefix + "_store_dot");
        builder.BuildCondBr(dotValid, storeDotBlock, failBlock);

        builder.PositionAtEnd(storeDotBlock);
        LLVMValueRef addressValue = builder.BuildLoad2(state.I64, addressSlot, prefix + "_address_value");
        LLVMValueRef shiftedOctet = builder.BuildShl(builder.BuildLoad2(state.I64, currentSlot, prefix + "_octet_value"), builder.BuildMul(part, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_octet_shift"), prefix + "_shifted_octet");
        builder.BuildStore(builder.BuildOr(addressValue, shiftedOctet, prefix + "_address_next"), addressSlot);
        builder.BuildStore(builder.BuildAdd(part, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_part_next"), partSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), currentSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), seenDigitSlot);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_after_dot"), indexSlot);
        builder.BuildBr(parseLoopBlock);

        builder.PositionAtEnd(finalizeBlock);
        LLVMValueRef finalSeenDigit = builder.BuildLoad2(state.I64, seenDigitSlot, prefix + "_final_seen_digit");
        LLVMValueRef finalPart = builder.BuildLoad2(state.I64, partSlot, prefix + "_final_part");
        LLVMValueRef finalValid = builder.BuildAnd(
            builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, finalSeenDigit, LLVMValueRef.CreateConstInt(state.I64, 0, false), prefix + "_final_seen_digit_ok"),
            builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, finalPart, LLVMValueRef.CreateConstInt(state.I64, 3, false), prefix + "_final_part_eq_three"),
            prefix + "_final_valid");
        var storeFinalBlock = state.Function.AppendBasicBlock(prefix + "_store_final");
        builder.BuildCondBr(finalValid, storeFinalBlock, failBlock);

        builder.PositionAtEnd(storeFinalBlock);
        LLVMValueRef finalAddress = builder.BuildOr(
            builder.BuildLoad2(state.I64, addressSlot, prefix + "_address_before_final"),
            builder.BuildShl(builder.BuildLoad2(state.I64, currentSlot, prefix + "_current_before_final"), LLVMValueRef.CreateConstInt(state.I64, 24, false), prefix + "_final_shifted_octet"),
            prefix + "_final_address");
        builder.BuildStore(EmitResultOk(state, finalAddress), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(failBlock);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitHttpRequestString(LlvmCodegenState state, LLVMValueRef pathRef, LLVMValueRef hostRef, LLVMValueRef bodyRef, bool hasBody)
    {
        LLVMValueRef request = EmitHeapStringLiteral(state, hasBody ? "POST " : "GET ");
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

    private static LLVMValueRef EmitHttpStatusErrorString(LlvmCodegenState state, LLVMValueRef statusCode, string prefix)
    {
        return EmitStringConcat(state, EmitHeapStringLiteral(state, "HTTP "), EmitNonNegativeIntToString(state, statusCode, prefix + "_code"));
    }

    private static LLVMValueRef EmitStartsWith(LlvmCodegenState state, LLVMValueRef sourceRef, LLVMValueRef prefixRef, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef sourceLen = LoadStringLength(state, sourceRef, prefix + "_source_len");
        LLVMValueRef prefixLen = LoadStringLength(state, prefixRef, prefix + "_prefix_len");
        LLVMValueRef enough = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, sourceLen, prefixLen, prefix + "_enough");
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        var compareBlock = state.Function.AppendBasicBlock(prefix + "_compare");
        var falseBlock = state.Function.AppendBasicBlock(prefix + "_false");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        builder.BuildCondBr(enough, compareBlock, falseBlock);

        builder.PositionAtEnd(falseBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(compareBlock);
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        LLVMValueRef sourceBytes = GetStringBytesPointer(state, sourceRef, prefix + "_source_bytes");
        LLVMValueRef prefixBytes = GetStringBytesPointer(state, prefixRef, prefix + "_prefix_bytes");
        var loopCheckBlock = state.Function.AppendBasicBlock(prefix + "_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock(prefix + "_loop_body");
        var successBlock = state.Function.AppendBasicBlock(prefix + "_success");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, index, prefixLen, prefix + "_done");
        builder.BuildCondBr(done, successBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef sourceByte = LoadByteAt(state, sourceBytes, index, prefix + "_source_byte");
        LLVMValueRef prefixByte = LoadByteAt(state, prefixBytes, index, prefix + "_prefix_byte");
        LLVMValueRef matches = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sourceByte, prefixByte, prefix + "_matches");
        var advanceBlock = state.Function.AppendBasicBlock(prefix + "_advance");
        builder.BuildCondBr(matches, advanceBlock, falseBlock);

        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_next"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(successBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitFindByte(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef len, int startOffset, byte targetByte, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, (ulong)startOffset, false), indexSlot);
        var loopCheckBlock = state.Function.AppendBasicBlock(prefix + "_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock(prefix + "_loop_body");
        var foundBlock = state.Function.AppendBasicBlock(prefix + "_found");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, index, len, prefix + "_done");
        builder.BuildCondBr(done, continueBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef currentByte = LoadByteAt(state, bytesPtr, index, prefix + "_byte");
        LLVMValueRef matches = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentByte, LLVMValueRef.CreateConstInt(state.I8, targetByte, false), prefix + "_matches");
        var advanceBlock = state.Function.AppendBasicBlock(prefix + "_advance");
        builder.BuildCondBr(matches, foundBlock, advanceBlock);

        builder.PositionAtEnd(foundBlock);
        builder.BuildStore(index, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_next"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitFindByteSequence(LlvmCodegenState state, LLVMValueRef bytesPtr, LLVMValueRef len, IReadOnlyList<byte> patternBytes, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef resultSlot = builder.BuildAlloca(state.I64, prefix + "_result");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, prefix + "_index");
        LLVMValueRef patternLen = LLVMValueRef.CreateConstInt(state.I64, (ulong)patternBytes.Count, false);
        LLVMValueRef patternPtr = EmitStackByteArray(state, patternBytes);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)(-1L)), true), resultSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);
        var loopCheckBlock = state.Function.AppendBasicBlock(prefix + "_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock(prefix + "_loop_body");
        var compareLoopBlock = state.Function.AppendBasicBlock(prefix + "_compare_loop");
        var foundBlock = state.Function.AppendBasicBlock(prefix + "_found");
        var advanceBlock = state.Function.AppendBasicBlock(prefix + "_advance");
        var continueBlock = state.Function.AppendBasicBlock(prefix + "_continue");
        LLVMValueRef compareIndexSlot = builder.BuildAlloca(state.I64, prefix + "_compare_index");
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, prefix + "_index_value");
        LLVMValueRef canMatch = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, builder.BuildAdd(index, patternLen, prefix + "_candidate_end"), len, prefix + "_can_match");
        builder.BuildCondBr(canMatch, loopBodyBlock, continueBlock);

        builder.PositionAtEnd(loopBodyBlock);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), compareIndexSlot);
        builder.BuildBr(compareLoopBlock);

        builder.PositionAtEnd(compareLoopBlock);
        LLVMValueRef compareIndex = builder.BuildLoad2(state.I64, compareIndexSlot, prefix + "_compare_index_value");
        LLVMValueRef done = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, compareIndex, patternLen, prefix + "_compare_done");
        var compareBodyBlock = state.Function.AppendBasicBlock(prefix + "_compare_body");
        builder.BuildCondBr(done, foundBlock, compareBodyBlock);

        builder.PositionAtEnd(compareBodyBlock);
        LLVMValueRef actualByte = LoadByteAt(state, bytesPtr, builder.BuildAdd(index, compareIndex, prefix + "_actual_index"), prefix + "_actual_byte");
        LLVMValueRef expectedByte = LoadByteAt(state, patternPtr, compareIndex, prefix + "_expected_byte");
        LLVMValueRef matches = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, actualByte, expectedByte, prefix + "_compare_matches");
        var compareAdvanceBlock = state.Function.AppendBasicBlock(prefix + "_compare_advance");
        builder.BuildCondBr(matches, compareAdvanceBlock, advanceBlock);

        builder.PositionAtEnd(compareAdvanceBlock);
        builder.BuildStore(builder.BuildAdd(compareIndex, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_compare_index_next"), compareIndexSlot);
        builder.BuildBr(compareLoopBlock);

        builder.PositionAtEnd(foundBlock);
        builder.BuildStore(index, resultSlot);
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(advanceBlock);
        builder.BuildStore(builder.BuildAdd(index, LLVMValueRef.CreateConstInt(state.I64, 1, false), prefix + "_index_next"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(continueBlock);
        return builder.BuildLoad2(state.I64, resultSlot, prefix + "_result_value");
    }

    private static LLVMValueRef EmitByteSwap16(LlvmCodegenState state, LLVMValueRef value, string prefix)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef maskedLow = builder.BuildAnd(value, LLVMValueRef.CreateConstInt(state.I64, 0xFF, false), prefix + "_low");
        LLVMValueRef maskedHigh = builder.BuildAnd(builder.BuildLShr(value, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_shr"), LLVMValueRef.CreateConstInt(state.I64, 0xFF, false), prefix + "_high");
        return builder.BuildOr(builder.BuildShl(maskedLow, LLVMValueRef.CreateConstInt(state.I64, 8, false), prefix + "_low_shifted"), maskedHigh, prefix + "_result");
    }

    private static void EmitEntryProgramArgsInitialization(LlvmCodegenState state)
    {
        state.Target.Builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.ProgramArgsSlot);

        if (state.Flavor == LlvmCodegenFlavor.Linux)
        {
            EmitLinuxProgramArgsInitialization(state);
            return;
        }

        EmitWindowsProgramArgsInitialization(state);
    }

    private static void EmitLinuxProgramArgsInitialization(LlvmCodegenState state)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef listSlot = builder.BuildAlloca(state.I64, "program_args_list");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, "program_args_index");
        LLVMValueRef argPtrSlot = builder.BuildAlloca(state.I64, "program_args_arg_ptr");
        LLVMValueRef lenSlot = builder.BuildAlloca(state.I64, "program_args_arg_len");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), listSlot);

        LLVMValueRef stackPtr = state.EntryStackPointer;
        LLVMValueRef argc = LoadMemory(state, stackPtr, 0, "program_args_argc");

        var initBlock = state.Function.AppendBasicBlock("program_args_init");
        var loopCheckBlock = state.Function.AppendBasicBlock("program_args_loop_check");
        var lenCheckBlock = state.Function.AppendBasicBlock("program_args_len_check");
        var lenBodyBlock = state.Function.AppendBasicBlock("program_args_len_body");
        var buildNodeBlock = state.Function.AppendBasicBlock("program_args_build_node");
        var doneBlock = state.Function.AppendBasicBlock("program_args_done");

        LLVMValueRef hasArgs = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            argc,
            LLVMValueRef.CreateConstInt(state.I64, 1, false),
            "program_args_has_args");
        builder.BuildCondBr(hasArgs, initBlock, doneBlock);

        builder.PositionAtEnd(initBlock);
        builder.BuildStore(
            builder.BuildSub(argc, LLVMValueRef.CreateConstInt(state.I64, 1, false), "program_args_start_index"),
            indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I64, indexSlot, "program_args_index_value");
        LLVMValueRef shouldContinue = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            index,
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "program_args_continue");
        builder.BuildCondBr(shouldContinue, lenCheckBlock, doneBlock);

        builder.PositionAtEnd(lenCheckBlock);
        LLVMValueRef argvEntryOffset = builder.BuildMul(index, LLVMValueRef.CreateConstInt(state.I64, 8, false), "program_args_argv_entry_offset");
        LLVMValueRef argvEntryAddress = builder.BuildAdd(
            stackPtr,
            builder.BuildAdd(LLVMValueRef.CreateConstInt(state.I64, 8, false), argvEntryOffset, "program_args_argv_offset"),
            "program_args_argv_entry_addr");
        LLVMValueRef argPtr = LoadMemory(state, argvEntryAddress, 0, "program_args_argv_entry");
        builder.BuildStore(argPtr, argPtrSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), lenSlot);

        var lenLoopCheckBlock = state.Function.AppendBasicBlock("program_args_len_loop_check");
        builder.BuildBr(lenLoopCheckBlock);

        builder.PositionAtEnd(lenLoopCheckBlock);
        LLVMValueRef currentLen = builder.BuildLoad2(state.I64, lenSlot, "program_args_current_len");
        LLVMValueRef currentArgPtr = builder.BuildLoad2(state.I64, argPtrSlot, "program_args_current_arg_ptr");
        LLVMValueRef currentBytePtr = builder.BuildGEP2(
            state.I8,
            builder.BuildIntToPtr(currentArgPtr, state.I8Ptr, "program_args_arg_bytes"),
            new[] { currentLen },
            "program_args_current_byte_ptr");
        LLVMValueRef currentByte = builder.BuildLoad2(state.I8, currentBytePtr, "program_args_current_byte");
        LLVMValueRef reachedTerminator = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntEQ,
            currentByte,
            LLVMValueRef.CreateConstInt(state.I8, 0, false),
            "program_args_reached_terminator");
        builder.BuildCondBr(reachedTerminator, buildNodeBlock, lenBodyBlock);

        builder.PositionAtEnd(lenBodyBlock);
        builder.BuildStore(
            builder.BuildAdd(currentLen, LLVMValueRef.CreateConstInt(state.I64, 1, false), "program_args_next_len"),
            lenSlot);
        builder.BuildBr(lenLoopCheckBlock);

        builder.PositionAtEnd(buildNodeBlock);
        LLVMValueRef argLen = builder.BuildLoad2(state.I64, lenSlot, "program_args_arg_len_value");
        LLVMValueRef stringRef = EmitAllocDynamic(
            state,
            builder.BuildAdd(argLen, LLVMValueRef.CreateConstInt(state.I64, 8, false), "program_args_string_bytes"));
        StoreMemory(state, stringRef, 0, argLen, "program_args_string_len");
        EmitCopyBytes(
            state,
            GetStringBytesPointer(state, stringRef, "program_args_string_dest"),
            builder.BuildIntToPtr(builder.BuildLoad2(state.I64, argPtrSlot, "program_args_copy_arg_ptr"), state.I8Ptr, "program_args_string_src"),
            argLen,
            "program_args_copy_bytes");
        LLVMValueRef consRef = EmitAlloc(state, 16);
        StoreMemory(state, consRef, 0, stringRef, "program_args_cons_head");
        StoreMemory(state, consRef, 8, builder.BuildLoad2(state.I64, listSlot, "program_args_prev_list"), "program_args_cons_tail");
        builder.BuildStore(consRef, listSlot);
        builder.BuildStore(
            builder.BuildSub(builder.BuildLoad2(state.I64, indexSlot, "program_args_index_before_dec"), LLVMValueRef.CreateConstInt(state.I64, 1, false), "program_args_index_dec"),
            indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(doneBlock);
        builder.BuildStore(builder.BuildLoad2(state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
    }

    private static void EmitWindowsProgramArgsInitialization(LlvmCodegenState state)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef i16 = state.Target.Context.Int16Type;
        LLVMTypeRef i16Ptr = LLVMTypeRef.CreatePointer(i16, 0);
        LLVMTypeRef i16PtrPtr = LLVMTypeRef.CreatePointer(i16Ptr, 0);
        LLVMTypeRef getCommandLineType = LLVMTypeRef.CreateFunction(i16Ptr, []);
        LLVMTypeRef wideCharToMultiByteType = LLVMTypeRef.CreateFunction(state.I32, [state.I32, state.I32, i16Ptr, state.I32, state.I8Ptr, state.I32, state.I8Ptr, state.I8Ptr]);
        LLVMTypeRef localFreeType = LLVMTypeRef.CreateFunction(state.I8Ptr, [state.I8Ptr]);
        LLVMTypeRef commandLineToArgvType = LLVMTypeRef.CreateFunction(i16PtrPtr, [i16Ptr, state.I32Ptr]);

        LLVMValueRef listSlot = builder.BuildAlloca(state.I64, "program_args_list");
        LLVMValueRef argcSlot = builder.BuildAlloca(state.I32, "program_args_argc");
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I32, "program_args_index");
        LLVMValueRef wideArgSlot = builder.BuildAlloca(i16Ptr, "program_args_wide_arg");
        LLVMValueRef wideLenSlot = builder.BuildAlloca(state.I32, "program_args_wide_len");
        LLVMValueRef stringRefSlot = builder.BuildAlloca(state.I64, "program_args_string_ref");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), listSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), argcSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), stringRefSlot);

        LLVMValueRef getCommandLinePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(getCommandLineType, 0),
            state.WindowsGetCommandLineImport,
            "get_command_line_ptr");
        LLVMValueRef commandLinePtr = builder.BuildCall2(
            getCommandLineType,
            getCommandLinePtr,
            Array.Empty<LLVMValueRef>(),
            "command_line");

        LLVMValueRef commandLineToArgvPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(commandLineToArgvType, 0),
            state.WindowsCommandLineToArgvImport,
            "command_line_to_argv_ptr");
        LLVMValueRef argvWide = builder.BuildCall2(
            commandLineToArgvType,
            commandLineToArgvPtr,
            new[] { commandLinePtr, argcSlot },
            "argv_wide");

        var haveArgvBlock = state.Function.AppendBasicBlock("program_args_have_argv");
        var maybeLoopBlock = state.Function.AppendBasicBlock("program_args_maybe_loop");
        var loopCheckBlock = state.Function.AppendBasicBlock("program_args_loop_check");
        var wideArgSetupBlock = state.Function.AppendBasicBlock("program_args_wide_arg_setup");
        var wideLenBodyBlock = state.Function.AppendBasicBlock("program_args_wide_len_body");
        var wideLenIncBlock = state.Function.AppendBasicBlock("program_args_wide_len_inc");
        var convertArgBlock = state.Function.AppendBasicBlock("program_args_convert_arg");
        var createUtf8StringBlock = state.Function.AppendBasicBlock("program_args_create_utf8_string");
        var createEmptyStringBlock = state.Function.AppendBasicBlock("program_args_create_empty_string");
        var linkArgBlock = state.Function.AppendBasicBlock("program_args_link_arg");
        var freeArgvBlock = state.Function.AppendBasicBlock("program_args_free_argv");
        var doneBlock = state.Function.AppendBasicBlock("program_args_done");

        LLVMValueRef hasArgv = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntNE,
            builder.BuildPtrToInt(argvWide, state.I64, "argv_wide_i64"),
            LLVMValueRef.CreateConstInt(state.I64, 0, false),
            "program_args_has_argv");
        builder.BuildCondBr(hasArgv, haveArgvBlock, doneBlock);

        builder.PositionAtEnd(haveArgvBlock);
        LLVMValueRef argc = builder.BuildLoad2(state.I32, argcSlot, "program_args_argc_value");
        LLVMValueRef hasUserArgs = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            argc,
            LLVMValueRef.CreateConstInt(state.I32, 1, false),
            "program_args_has_user_args");
        builder.BuildCondBr(hasUserArgs, maybeLoopBlock, freeArgvBlock);

        builder.PositionAtEnd(maybeLoopBlock);
        builder.BuildStore(
            builder.BuildSub(argc, LLVMValueRef.CreateConstInt(state.I32, 1, false), "program_args_start_index"),
            indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef index = builder.BuildLoad2(state.I32, indexSlot, "program_args_index_value");
        LLVMValueRef shouldContinue = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            index,
            LLVMValueRef.CreateConstInt(state.I32, 0, false),
            "program_args_continue");
        builder.BuildCondBr(shouldContinue, wideArgSetupBlock, freeArgvBlock);

        builder.PositionAtEnd(wideArgSetupBlock);
        LLVMValueRef wideArgPtrPtr = builder.BuildGEP2(
            i16Ptr,
            argvWide,
            new[] { builder.BuildSExt(index, state.I64, "program_args_index_i64") },
            "program_args_wide_arg_ptr");
        LLVMValueRef wideArgPtr = builder.BuildLoad2(i16Ptr, wideArgPtrPtr, "program_args_wide_arg_value");
        builder.BuildStore(wideArgPtr, wideArgSlot);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), wideLenSlot);
        builder.BuildBr(wideLenBodyBlock);

        builder.PositionAtEnd(wideLenBodyBlock);
        LLVMValueRef wideLen = builder.BuildLoad2(state.I32, wideLenSlot, "program_args_wide_len_value");
        LLVMValueRef wideCharPtr = builder.BuildGEP2(
            i16,
            builder.BuildLoad2(i16Ptr, wideArgSlot, "program_args_wide_arg_current"),
            new[] { builder.BuildSExt(wideLen, state.I64, "program_args_wide_len_i64") },
            "program_args_wide_char_ptr");
        LLVMValueRef wideChar = builder.BuildLoad2(i16, wideCharPtr, "program_args_wide_char");
        LLVMValueRef atTerminator = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntEQ,
            wideChar,
            LLVMValueRef.CreateConstInt(i16, 0, false),
            "program_args_at_wide_terminator");
        builder.BuildCondBr(atTerminator, convertArgBlock, wideLenIncBlock);

        builder.PositionAtEnd(wideLenIncBlock);
        builder.BuildStore(
            builder.BuildAdd(builder.BuildLoad2(state.I32, wideLenSlot, "program_args_wide_len_before_inc"), LLVMValueRef.CreateConstInt(state.I32, 1, false), "program_args_wide_len_inc"),
            wideLenSlot);
        builder.BuildBr(wideLenBodyBlock);

        builder.PositionAtEnd(convertArgBlock);
        LLVMValueRef wideArg = builder.BuildLoad2(i16Ptr, wideArgSlot, "program_args_wide_arg_for_convert");
        LLVMValueRef wcharCount = builder.BuildLoad2(state.I32, wideLenSlot, "program_args_wchar_count");
        LLVMValueRef wideCharToMultiBytePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(wideCharToMultiByteType, 0),
            state.WindowsWideCharToMultiByteImport,
            "wide_char_to_multi_byte_ptr");
        LLVMValueRef nullI8Ptr = builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, "null_i8_ptr");
        LLVMValueRef byteCount = builder.BuildCall2(
            wideCharToMultiByteType,
            wideCharToMultiBytePtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I32, Utf8CodePage, false),
                LLVMValueRef.CreateConstInt(state.I32, 0, false),
                wideArg,
                wcharCount,
                nullI8Ptr,
                LLVMValueRef.CreateConstInt(state.I32, 0, false),
                nullI8Ptr,
                nullI8Ptr
            },
            "program_args_byte_count");
        LLVMValueRef hasBytes = builder.BuildICmp(
            LLVMIntPredicate.LLVMIntSGT,
            byteCount,
            LLVMValueRef.CreateConstInt(state.I32, 0, false),
            "program_args_has_bytes");
        builder.BuildCondBr(hasBytes, createUtf8StringBlock, createEmptyStringBlock);

        builder.PositionAtEnd(createUtf8StringBlock);
        LLVMValueRef stringRef = EmitAllocDynamic(
            state,
            builder.BuildAdd(builder.BuildZExt(byteCount, state.I64, "program_args_byte_count_i64"), LLVMValueRef.CreateConstInt(state.I64, 8, false), "program_args_string_bytes"));
        StoreMemory(state, stringRef, 0, builder.BuildZExt(byteCount, state.I64, "program_args_string_len"), "program_args_string_len");
        LLVMValueRef stringDest = GetStringBytesPointer(state, stringRef, "program_args_string_dest");
        builder.BuildCall2(
            wideCharToMultiByteType,
            wideCharToMultiBytePtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I32, Utf8CodePage, false),
                LLVMValueRef.CreateConstInt(state.I32, 0, false),
                wideArg,
                wcharCount,
                stringDest,
                byteCount,
                nullI8Ptr,
                nullI8Ptr
            },
            "program_args_copy_utf8");
        builder.BuildStore(stringRef, stringRefSlot);
        builder.BuildBr(linkArgBlock);

        builder.PositionAtEnd(createEmptyStringBlock);
        LLVMValueRef emptyStringRef = EmitAlloc(state, 8);
        StoreMemory(state, emptyStringRef, 0, LLVMValueRef.CreateConstInt(state.I64, 0, false), "program_args_empty_string_len");
        builder.BuildStore(emptyStringRef, stringRefSlot);
        builder.BuildBr(linkArgBlock);

        builder.PositionAtEnd(linkArgBlock);
        LLVMValueRef consRef = EmitAlloc(state, 16);
        StoreMemory(state, consRef, 0, builder.BuildLoad2(state.I64, stringRefSlot, "program_args_string_ref_value"), "program_args_cons_head");
        StoreMemory(state, consRef, 8, builder.BuildLoad2(state.I64, listSlot, "program_args_prev_list"), "program_args_cons_tail");
        builder.BuildStore(consRef, listSlot);
        builder.BuildStore(
            builder.BuildSub(builder.BuildLoad2(state.I32, indexSlot, "program_args_index_before_dec"), LLVMValueRef.CreateConstInt(state.I32, 1, false), "program_args_index_dec"),
            indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(freeArgvBlock);
        LLVMValueRef localFreePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(localFreeType, 0),
            state.WindowsLocalFreeImport,
            "local_free_ptr");
        builder.BuildCall2(
            localFreeType,
            localFreePtr,
            new[] { builder.BuildBitCast(argvWide, state.I8Ptr, "argv_wide_hlocal") },
            "program_args_local_free");
        builder.BuildBr(doneBlock);

        builder.PositionAtEnd(doneBlock);
        builder.BuildStore(builder.BuildLoad2(state.I64, listSlot, "program_args_final_list"), state.ProgramArgsSlot);
    }
}
