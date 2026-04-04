using LLVMSharp.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LLVMValueRef EmitWindowsWsaStartup(LlvmCodegenState state, LLVMValueRef wsadataPtr, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef i16 = state.Target.Context.Int16Type;
        LLVMTypeRef wsaStartupType = LLVMTypeRef.CreateFunction(state.I32, [i16, state.I8Ptr]);
        LLVMValueRef wsaStartupPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(wsaStartupType, 0),
            state.WindowsWsaStartupImport,
            name + "_ptr");
        LLVMValueRef result = builder.BuildCall2(
            wsaStartupType,
            wsaStartupPtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(i16, 0x0202, false),
                wsadataPtr
            },
            name);
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result, LLVMValueRef.CreateConstInt(state.I32, 0, false), name + "_success");
    }

    private static LLVMValueRef EmitWindowsSocket(LlvmCodegenState state, int af, int socketTypeValue, int protocol, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef socketType = LLVMTypeRef.CreateFunction(state.I64, [state.I32, state.I32, state.I32]);
        LLVMValueRef socketPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(socketType, 0),
            state.WindowsSocketImport,
            name + "_ptr");
        return builder.BuildCall2(
            socketType,
            socketPtr,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I32, (uint)af, false),
                LLVMValueRef.CreateConstInt(state.I32, (uint)socketTypeValue, false),
                LLVMValueRef.CreateConstInt(state.I32, (uint)protocol, false)
            },
            name);
    }

    private static LLVMValueRef EmitWindowsConnect(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef sockaddrPtr, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef connectType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32]);
        LLVMValueRef connectPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(connectType, 0),
            state.WindowsConnectImport,
            name + "_ptr");
        LLVMValueRef result = builder.BuildCall2(
            connectType,
            connectPtr,
            new[]
            {
                socket,
                sockaddrPtr,
                LLVMValueRef.CreateConstInt(state.I32, 16, false)
            },
            name);
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result, LLVMValueRef.CreateConstInt(state.I32, 0, false), name + "_success");
    }

    private static LLVMValueRef EmitWindowsSend(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef buffer, LLVMValueRef len, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef sendType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32]);
        LLVMValueRef sendPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(sendType, 0),
            state.WindowsSendImport,
            name + "_ptr");
        return builder.BuildCall2(
            sendType,
            sendPtr,
            new[]
            {
                socket,
                buffer,
                len,
                LLVMValueRef.CreateConstInt(state.I32, 0, false)
            },
            name);
    }

    private static LLVMValueRef EmitWindowsRecv(LlvmCodegenState state, LLVMValueRef socket, LLVMValueRef buffer, LLVMValueRef len, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef recvType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32]);
        LLVMValueRef recvPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(recvType, 0),
            state.WindowsRecvImport,
            name + "_ptr");
        return builder.BuildCall2(
            recvType,
            recvPtr,
            new[]
            {
                socket,
                buffer,
                len,
                LLVMValueRef.CreateConstInt(state.I32, 0, false)
            },
            name);
    }

    private static LLVMValueRef EmitWindowsCloseSocket(LlvmCodegenState state, LLVMValueRef socket, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef closeSocketType = LLVMTypeRef.CreateFunction(state.I32, [state.I64]);
        LLVMValueRef closeSocketPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(closeSocketType, 0),
            state.WindowsCloseSocketImport,
            name + "_ptr");
        return builder.BuildCall2(
            closeSocketType,
            closeSocketPtr,
            new[] { socket },
            name);
    }

    private static LLVMValueRef EmitWindowsCreateFile(LlvmCodegenState state, LLVMValueRef pathCstr, int desiredAccess, int shareMode, int creationDisposition, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef createFileType = LLVMTypeRef.CreateFunction(state.I64, [state.I8Ptr, state.I32, state.I32, state.I8Ptr, state.I32, state.I32, state.I64]);
        LLVMValueRef createFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(createFileType, 0),
            state.WindowsCreateFileImport,
            name + "_ptr");
        return builder.BuildCall2(
            createFileType,
            createFilePtr,
            new[]
            {
                pathCstr,
                LLVMValueRef.CreateConstInt(state.I32, unchecked((uint)desiredAccess), true),
                LLVMValueRef.CreateConstInt(state.I32, unchecked((uint)shareMode), false),
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, name + "_security"),
                LLVMValueRef.CreateConstInt(state.I32, unchecked((uint)creationDisposition), false),
                LLVMValueRef.CreateConstInt(state.I32, 0x80, false),
                LLVMValueRef.CreateConstInt(state.I64, 0, false)
            },
            name);
    }

    private static void EmitWindowsCloseHandle(LlvmCodegenState state, LLVMValueRef handle, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef closeHandleType = LLVMTypeRef.CreateFunction(state.I32, [state.I64]);
        LLVMValueRef closeHandlePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(closeHandleType, 0),
            state.WindowsCloseHandleImport,
            name + "_ptr");
        builder.BuildCall2(
            closeHandleType,
            closeHandlePtr,
            new[] { handle },
            name);
    }

    private static LLVMValueRef EmitWindowsGetFileAttributes(LlvmCodegenState state, LLVMValueRef pathCstr, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef getFileAttributesType = LLVMTypeRef.CreateFunction(state.I32, [state.I8Ptr]);
        LLVMValueRef getFileAttributesPtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(getFileAttributesType, 0),
            state.WindowsGetFileAttributesImport,
            name + "_ptr");
        return builder.BuildCall2(
            getFileAttributesType,
            getFileAttributesPtr,
            new[] { pathCstr },
            name);
    }

    private static LLVMValueRef EmitWindowsReadFile(LlvmCodegenState state, LLVMValueRef handle, LLVMValueRef buffer, LLVMValueRef len, LLVMValueRef bytesReadSlot, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef readFileType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), bytesReadSlot);
        LLVMValueRef readFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(readFileType, 0),
            state.WindowsReadFileImport,
            name + "_ptr");
        LLVMValueRef callResult = builder.BuildCall2(
            readFileType,
            readFilePtr,
            new[]
            {
                handle,
                buffer,
                len,
                bytesReadSlot,
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, name + "_overlapped")
            },
            name);
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, callResult, LLVMValueRef.CreateConstInt(state.I32, 0, false), name + "_success");
    }

    private static LLVMValueRef EmitWindowsWriteFile(LlvmCodegenState state, LLVMValueRef handle, LLVMValueRef buffer, LLVMValueRef len, LLVMValueRef bytesWrittenSlot, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef writeFileType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), bytesWrittenSlot);
        LLVMValueRef writeFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(writeFileType, 0),
            state.WindowsWriteFileImport,
            name + "_ptr");
        LLVMValueRef callResult = builder.BuildCall2(
            writeFileType,
            writeFilePtr,
            new[]
            {
                handle,
                buffer,
                len,
                bytesWrittenSlot,
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, name + "_overlapped")
            },
            name);
        return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, callResult, LLVMValueRef.CreateConstInt(state.I32, 0, false), name + "_success");
    }

    private static bool EmitPrintInt(LlvmCodegenState state, LLVMValueRef value)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMValueRef indexSlot = builder.BuildAlloca(state.I64, "print_idx");
        LLVMValueRef workSlot = builder.BuildAlloca(state.I64, "print_work");
        LLVMValueRef negativeSlot = builder.BuildAlloca(state.I64, "print_negative");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 0, false), indexSlot);

        LLVMTypeRef bufferType = LLVMTypeRef.CreateArray(state.I8, 32);
        LLVMValueRef buffer = builder.BuildAlloca(bufferType, "print_buf");

        LLVMValueRef zero = LLVMValueRef.CreateConstInt(state.I64, 0, false);
        LLVMValueRef isNegative = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, value, zero, "is_negative");
        LLVMValueRef negativeValue = builder.BuildZExt(isNegative, state.I64, "negative_i64");
        builder.BuildStore(negativeValue, negativeSlot);
        LLVMValueRef absValue = builder.BuildSelect(isNegative, builder.BuildSub(zero, value, "negated_value"), value, "abs_value");
        builder.BuildStore(absValue, workSlot);

        var zeroBlock = state.Function.AppendBasicBlock("print_int_zero");
        var loopCheckBlock = state.Function.AppendBasicBlock("print_int_loop_check");
        var loopBodyBlock = state.Function.AppendBasicBlock("print_int_loop_body");
        var maybeSignBlock = state.Function.AppendBasicBlock("print_int_maybe_sign");
        var signBlock = state.Function.AppendBasicBlock("print_int_sign");
        var writeBlock = state.Function.AppendBasicBlock("print_int_write");
        var continueBlock = state.Function.AppendBasicBlock("print_int_continue");

        LLVMValueRef isZero = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, absValue, zero, "is_zero");
        builder.BuildCondBr(isZero, zeroBlock, loopCheckBlock);

        builder.PositionAtEnd(zeroBlock);
        StoreBufferByte(state, buffer, LLVMValueRef.CreateConstInt(state.I64, 31, false), (byte)'0');
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I64, 1, false), indexSlot);
        builder.BuildBr(writeBlock);

        builder.PositionAtEnd(loopCheckBlock);
        LLVMValueRef work = builder.BuildLoad2(state.I64, workSlot, "work_value");
        LLVMValueRef loopDone = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, work, zero, "loop_done");
        builder.BuildCondBr(loopDone, maybeSignBlock, loopBodyBlock);

        builder.PositionAtEnd(loopBodyBlock);
        LLVMValueRef digit = builder.BuildSRem(work, LLVMValueRef.CreateConstInt(state.I64, 10, false), "digit");
        LLVMValueRef nextWork = builder.BuildSDiv(work, LLVMValueRef.CreateConstInt(state.I64, 10, false), "next_work");
        builder.BuildStore(nextWork, workSlot);
        LLVMValueRef idx = builder.BuildLoad2(state.I64, indexSlot, "digit_idx");
        LLVMValueRef writeIndex = builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 31, false), idx, "digit_write_index");
        LLVMValueRef asciiDigit = builder.BuildAdd(digit, LLVMValueRef.CreateConstInt(state.I64, (byte)'0', false), "ascii_digit");
        StoreBufferByte(state, buffer, writeIndex, asciiDigit);
        builder.BuildStore(builder.BuildAdd(idx, LLVMValueRef.CreateConstInt(state.I64, 1, false), "idx_inc"), indexSlot);
        builder.BuildBr(loopCheckBlock);

        builder.PositionAtEnd(maybeSignBlock);
        LLVMValueRef negative = builder.BuildLoad2(state.I64, negativeSlot, "negative_value");
        LLVMValueRef hasSign = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, negative, zero, "has_sign");
        builder.BuildCondBr(hasSign, signBlock, writeBlock);

        builder.PositionAtEnd(signBlock);
        LLVMValueRef idxBeforeSign = builder.BuildLoad2(state.I64, indexSlot, "idx_before_sign");
        LLVMValueRef signIndex = builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 31, false), idxBeforeSign, "sign_index");
        StoreBufferByte(state, buffer, signIndex, (byte)'-');
        builder.BuildStore(builder.BuildAdd(idxBeforeSign, LLVMValueRef.CreateConstInt(state.I64, 1, false), "idx_with_sign"), indexSlot);
        builder.BuildBr(writeBlock);

        builder.PositionAtEnd(writeBlock);
        LLVMValueRef count = builder.BuildLoad2(state.I64, indexSlot, "print_count");
        LLVMValueRef startIndex = builder.BuildSub(LLVMValueRef.CreateConstInt(state.I64, 32, false), count, "start_index");
        LLVMValueRef dataPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, "print_data_ptr");
        EmitWriteBytes(state, dataPtr, count);
        EmitWriteBytes(state, EmitStackByteArray(state, [10]), LLVMValueRef.CreateConstInt(state.I64, 1, false));
        builder.BuildBr(continueBlock);

        builder.PositionAtEnd(continueBlock);
        return false;
    }

    private static void EmitWriteBytes(LlvmCodegenState state, LLVMValueRef bytePtr, LLVMValueRef len)
    {
        if (state.Flavor == LlvmCodegenFlavor.Linux)
        {
            EmitSyscall(
                state,
                SyscallWrite,
                LLVMValueRef.CreateConstInt(state.I64, 1, false),
                state.Target.Builder.BuildPtrToInt(bytePtr, state.I64, "write_ptr_i64"),
                len,
                "sys_write");
            return;
        }

        EmitWindowsWriteBytes(state, bytePtr, len);
    }

    private static LLVMValueRef EmitWindowsGetStdHandle(LlvmCodegenState state, uint handleKind, string name)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef getStdHandleType = LLVMTypeRef.CreateFunction(state.I64, [state.I32]);
        LLVMValueRef getStdHandlePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(getStdHandleType, 0),
            state.WindowsGetStdHandleImport,
            name + "_ptr");
        return builder.BuildCall2(
            getStdHandleType,
            getStdHandlePtr,
            new[] { LLVMValueRef.CreateConstInt(state.I32, handleKind, true) },
            name);
    }

    private static LLVMValueRef EmitWindowsReadByte(LlvmCodegenState state, LLVMValueRef stdinHandle, LLVMValueRef byteSlot, LLVMValueRef bytesReadSlot)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef readFileType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), bytesReadSlot);
        LLVMValueRef readFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(readFileType, 0),
            state.WindowsReadFileImport,
            "read_file_ptr");
        builder.BuildCall2(
            readFileType,
            readFilePtr,
            new[]
            {
                stdinHandle,
                byteSlot,
                LLVMValueRef.CreateConstInt(state.I32, 1, false),
                bytesReadSlot,
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, "null_overlapped")
            },
            "read_file");
        return builder.BuildZExt(builder.BuildLoad2(state.I32, bytesReadSlot, "read_line_bytes_read_value"), state.I64, "read_line_bytes_read_i64");
    }

    private static void EmitWindowsWriteBytes(LlvmCodegenState state, LLVMValueRef bytePtr, LLVMValueRef len)
    {
        LLVMBuilderRef builder = state.Target.Builder;
        LLVMTypeRef writeFileType = LLVMTypeRef.CreateFunction(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LLVMValueRef stdoutHandle = EmitWindowsGetStdHandle(state, StdOutputHandle, "stdout_handle");
        LLVMValueRef bytesWritten = builder.BuildAlloca(state.I32, "bytes_written");
        builder.BuildStore(LLVMValueRef.CreateConstInt(state.I32, 0, false), bytesWritten);
        LLVMValueRef writeFilePtr = builder.BuildLoad2(
            LLVMTypeRef.CreatePointer(writeFileType, 0),
            state.WindowsWriteFileImport,
            "write_file_ptr");
        builder.BuildCall2(
            writeFileType,
            writeFilePtr,
            new[]
            {
                stdoutHandle,
                bytePtr,
                builder.BuildTrunc(NormalizeToI64(state, len), state.I32, "write_len_i32"),
                bytesWritten,
                builder.BuildIntToPtr(LLVMValueRef.CreateConstInt(state.I64, 0, false), state.I8Ptr, "null_overlapped")
            },
            "write_file");
    }

    private static LLVMValueRef EmitSyscall(LlvmCodegenState state, long nr, LLVMValueRef arg1, LLVMValueRef arg2, LLVMValueRef arg3, string name)
    {
        LLVMTypeRef syscallType = LLVMTypeRef.CreateFunction(state.I64, [state.I64, state.I64, state.I64, state.I64]);
        LLVMValueRef syscall = LLVMValueRef.CreateConstInlineAsm(
            syscallType,
            "syscall",
            "={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}",
            true,
            false);
        return state.Target.Builder.BuildCall2(
            syscallType,
            syscall,
            new[]
            {
                LLVMValueRef.CreateConstInt(state.I64, unchecked((ulong)nr), true),
                NormalizeToI64(state, arg1),
                NormalizeToI64(state, arg2),
                NormalizeToI64(state, arg3)
            },
            name);
    }
}
