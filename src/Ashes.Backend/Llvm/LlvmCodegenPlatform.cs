using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private static LlvmValueHandle EmitWindowsWsaStartup(LlvmCodegenState state, LlvmValueHandle wsadataPtr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle i16 = LlvmApi.Int16TypeInContext(state.Target.Context);
        LlvmTypeHandle wsaStartupType = LlvmApi.FunctionType(state.I32, [i16, state.I8Ptr]);
        LlvmValueHandle wsaStartupPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWsaStartupImport,
            name + "_ptr");
        LlvmValueHandle result = LlvmApi.BuildCall2(builder,
            wsaStartupType,
            wsaStartupPtr,
            new[]
            {
                LlvmApi.ConstInt(i16, 0x0202, 0),
                wsadataPtr
            },
            name);
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
    }

    private static LlvmValueHandle EmitWindowsSocket(LlvmCodegenState state, int af, int socketTypeValue, int protocol, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle socketType = LlvmApi.FunctionType(state.I64, [state.I32, state.I32, state.I32]);
        LlvmValueHandle socketPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsSocketImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            socketType,
            socketPtr,
            new[]
            {
                LlvmApi.ConstInt(state.I32, (uint)af, 0),
                LlvmApi.ConstInt(state.I32, (uint)socketTypeValue, 0),
                LlvmApi.ConstInt(state.I32, (uint)protocol, 0)
            },
            name);
    }

    private static LlvmValueHandle EmitWindowsConnect(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle sockaddrPtr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle connectType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32]);
        LlvmValueHandle connectPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsConnectImport,
            name + "_ptr");
        LlvmValueHandle result = LlvmApi.BuildCall2(builder,
            connectType,
            connectPtr,
            new[]
            {
                socket,
                sockaddrPtr,
                LlvmApi.ConstInt(state.I32, 16, 0)
            },
            name);
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, result, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
    }

    private static LlvmValueHandle EmitWindowsSend(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle buffer, LlvmValueHandle len, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle sendType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32]);
        LlvmValueHandle sendPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsSendImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            sendType,
            sendPtr,
            new[]
            {
                socket,
                buffer,
                len,
                LlvmApi.ConstInt(state.I32, 0, 0)
            },
            name);
    }

    private static LlvmValueHandle EmitWindowsRecv(LlvmCodegenState state, LlvmValueHandle socket, LlvmValueHandle buffer, LlvmValueHandle len, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle recvType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32]);
        LlvmValueHandle recvPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsRecvImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            recvType,
            recvPtr,
            new[]
            {
                socket,
                buffer,
                len,
                LlvmApi.ConstInt(state.I32, 0, 0)
            },
            name);
    }

    private static LlvmValueHandle EmitWindowsCloseSocket(LlvmCodegenState state, LlvmValueHandle socket, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle closeSocketType = LlvmApi.FunctionType(state.I32, [state.I64]);
        LlvmValueHandle closeSocketPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCloseSocketImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            closeSocketType,
            closeSocketPtr,
            new[] { socket },
            name);
    }

    private static LlvmValueHandle EmitWindowsCreateFile(LlvmCodegenState state, LlvmValueHandle pathCstr, int desiredAccess, int shareMode, int creationDisposition, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle createFileType = LlvmApi.FunctionType(state.I64, [state.I8Ptr, state.I32, state.I32, state.I8Ptr, state.I32, state.I32, state.I64]);
        LlvmValueHandle createFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCreateFileImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            createFileType,
            createFilePtr,
            new[]
            {
                pathCstr,
                LlvmApi.ConstInt(state.I32, unchecked((uint)desiredAccess), 1),
                LlvmApi.ConstInt(state.I32, unchecked((uint)shareMode), 0),
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_security"),
                LlvmApi.ConstInt(state.I32, unchecked((uint)creationDisposition), 0),
                LlvmApi.ConstInt(state.I32, 0x80, 0),
                LlvmApi.ConstInt(state.I64, 0, 0)
            },
            name);
    }

    private static void EmitWindowsCloseHandle(LlvmCodegenState state, LlvmValueHandle handle, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle closeHandleType = LlvmApi.FunctionType(state.I32, [state.I64]);
        LlvmValueHandle closeHandlePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsCloseHandleImport,
            name + "_ptr");
        LlvmApi.BuildCall2(builder,
            closeHandleType,
            closeHandlePtr,
            new[] { handle },
            name);
    }

    private static LlvmValueHandle EmitWindowsGetFileAttributes(LlvmCodegenState state, LlvmValueHandle pathCstr, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle getFileAttributesType = LlvmApi.FunctionType(state.I32, [state.I8Ptr]);
        LlvmValueHandle getFileAttributesPtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsGetFileAttributesImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            getFileAttributesType,
            getFileAttributesPtr,
            new[] { pathCstr },
            name);
    }

    private static LlvmValueHandle EmitWindowsReadFile(LlvmCodegenState state, LlvmValueHandle handle, LlvmValueHandle buffer, LlvmValueHandle len, LlvmValueHandle bytesReadSlot, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle readFileType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesReadSlot);
        LlvmValueHandle readFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsReadFileImport,
            name + "_ptr");
        LlvmValueHandle callResult = LlvmApi.BuildCall2(builder,
            readFileType,
            readFilePtr,
            new[]
            {
                handle,
                buffer,
                len,
                bytesReadSlot,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_overlapped")
            },
            name);
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, callResult, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
    }

    private static LlvmValueHandle EmitWindowsWriteFile(LlvmCodegenState state, LlvmValueHandle handle, LlvmValueHandle buffer, LlvmValueHandle len, LlvmValueHandle bytesWrittenSlot, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle writeFileType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesWrittenSlot);
        LlvmValueHandle writeFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWriteFileImport,
            name + "_ptr");
        LlvmValueHandle callResult = LlvmApi.BuildCall2(builder,
            writeFileType,
            writeFilePtr,
            new[]
            {
                handle,
                buffer,
                len,
                bytesWrittenSlot,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, name + "_overlapped")
            },
            name);
        return LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, callResult, LlvmApi.ConstInt(state.I32, 0, 0), name + "_success");
    }

    private static bool EmitPrintInt(LlvmCodegenState state, LlvmValueHandle value)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle indexSlot = LlvmApi.BuildAlloca(builder, state.I64, "print_idx");
        LlvmValueHandle workSlot = LlvmApi.BuildAlloca(builder, state.I64, "print_work");
        LlvmValueHandle negativeSlot = LlvmApi.BuildAlloca(builder, state.I64, "print_negative");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), indexSlot);

        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(state.I8, 32);
        LlvmValueHandle buffer = LlvmApi.BuildAlloca(builder, bufferType, "print_buf");

        LlvmValueHandle zero = LlvmApi.ConstInt(state.I64, 0, 0);
        LlvmValueHandle isNegative = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Slt, value, zero, "is_negative");
        LlvmValueHandle negativeValue = LlvmApi.BuildZExt(builder, isNegative, state.I64, "negative_i64");
        LlvmApi.BuildStore(builder, negativeValue, negativeSlot);
        LlvmValueHandle absValue = LlvmApi.BuildSelect(builder, isNegative, LlvmApi.BuildSub(builder, zero, value, "negated_value"), value, "abs_value");
        LlvmApi.BuildStore(builder, absValue, workSlot);

        var zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_zero");
        var loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_loop_check");
        var loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_loop_body");
        var maybeSignBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_maybe_sign");
        var signBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_sign");
        var writeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_write");
        var continueBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "print_int_continue");

        LlvmValueHandle isZero = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, absValue, zero, "is_zero");
        LlvmApi.BuildCondBr(builder, isZero, zeroBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        StoreBufferByte(state, buffer, LlvmApi.ConstInt(state.I64, 31, 0), (byte)'0');
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildBr(builder, writeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle work = LlvmApi.BuildLoad2(builder, state.I64, workSlot, "work_value");
        LlvmValueHandle loopDone = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, work, zero, "loop_done");
        LlvmApi.BuildCondBr(builder, loopDone, maybeSignBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopBodyBlock);
        LlvmValueHandle digit = LlvmApi.BuildSRem(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), "digit");
        LlvmValueHandle nextWork = LlvmApi.BuildSDiv(builder, work, LlvmApi.ConstInt(state.I64, 10, 0), "next_work");
        LlvmApi.BuildStore(builder, nextWork, workSlot);
        LlvmValueHandle idx = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "digit_idx");
        LlvmValueHandle writeIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idx, "digit_write_index");
        LlvmValueHandle asciiDigit = LlvmApi.BuildAdd(builder, digit, LlvmApi.ConstInt(state.I64, (byte)'0', 0), "ascii_digit");
        StoreBufferByte(state, buffer, writeIndex, asciiDigit);
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idx, LlvmApi.ConstInt(state.I64, 1, 0), "idx_inc"), indexSlot);
        LlvmApi.BuildBr(builder, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, maybeSignBlock);
        LlvmValueHandle negative = LlvmApi.BuildLoad2(builder, state.I64, negativeSlot, "negative_value");
        LlvmValueHandle hasSign = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, negative, zero, "has_sign");
        LlvmApi.BuildCondBr(builder, hasSign, signBlock, writeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, signBlock);
        LlvmValueHandle idxBeforeSign = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "idx_before_sign");
        LlvmValueHandle signIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 31, 0), idxBeforeSign, "sign_index");
        StoreBufferByte(state, buffer, signIndex, (byte)'-');
        LlvmApi.BuildStore(builder, LlvmApi.BuildAdd(builder, idxBeforeSign, LlvmApi.ConstInt(state.I64, 1, 0), "idx_with_sign"), indexSlot);
        LlvmApi.BuildBr(builder, writeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "print_count");
        LlvmValueHandle startIndex = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 32, 0), count, "start_index");
        LlvmValueHandle dataPtr = GetArrayElementPointer(state, bufferType, buffer, startIndex, "print_data_ptr");
        EmitWriteBytes(state, dataPtr, count);
        EmitWriteBytes(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
        LlvmApi.BuildBr(builder, continueBlock);

        LlvmApi.PositionBuilderAtEnd(builder, continueBlock);
        return false;
    }

    private static void EmitWriteBytes(LlvmCodegenState state, LlvmValueHandle bytePtr, LlvmValueHandle len)
    {
        if (state.Flavor == LlvmCodegenFlavor.Linux)
        {
            EmitSyscall(
                state,
                SyscallWrite,
                LlvmApi.ConstInt(state.I64, 1, 0),
                LlvmApi.BuildPtrToInt(state.Target.Builder, bytePtr, state.I64, "write_ptr_i64"),
                len,
                "sys_write");
            return;
        }

        EmitWindowsWriteBytes(state, bytePtr, len);
    }

    private static LlvmValueHandle EmitWindowsGetStdHandle(LlvmCodegenState state, uint handleKind, string name)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle getStdHandleType = LlvmApi.FunctionType(state.I64, [state.I32]);
        LlvmValueHandle getStdHandlePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsGetStdHandleImport,
            name + "_ptr");
        return LlvmApi.BuildCall2(builder,
            getStdHandleType,
            getStdHandlePtr,
            new[] { LlvmApi.ConstInt(state.I32, handleKind, 1) },
            name);
    }

    private static LlvmValueHandle EmitWindowsReadByte(LlvmCodegenState state, LlvmValueHandle stdinHandle, LlvmValueHandle byteSlot, LlvmValueHandle bytesReadSlot)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle readFileType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesReadSlot);
        LlvmValueHandle readFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsReadFileImport,
            "read_file_ptr");
        LlvmApi.BuildCall2(builder,
            readFileType,
            readFilePtr,
            new[]
            {
                stdinHandle,
                byteSlot,
                LlvmApi.ConstInt(state.I32, 1, 0),
                bytesReadSlot,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, "null_overlapped")
            },
            "read_file");
        return LlvmApi.BuildZExt(builder, LlvmApi.BuildLoad2(builder, state.I32, bytesReadSlot, "read_line_bytes_read_value"), state.I64, "read_line_bytes_read_i64");
    }

    private static void EmitWindowsWriteBytes(LlvmCodegenState state, LlvmValueHandle bytePtr, LlvmValueHandle len)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmTypeHandle writeFileType = LlvmApi.FunctionType(state.I32, [state.I64, state.I8Ptr, state.I32, state.I32Ptr, state.I8Ptr]);
        LlvmValueHandle stdoutHandle = EmitWindowsGetStdHandle(state, StdOutputHandle, "stdout_handle");
        LlvmValueHandle bytesWritten = LlvmApi.BuildAlloca(builder, state.I32, "bytes_written");
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I32, 0, 0), bytesWritten);
        LlvmValueHandle writeFilePtr = LlvmApi.BuildLoad2(builder,
            LlvmApi.PointerTypeInContext(state.Target.Context, 0),
            state.WindowsWriteFileImport,
            "write_file_ptr");
        LlvmApi.BuildCall2(builder,
            writeFileType,
            writeFilePtr,
            new[]
            {
                stdoutHandle,
                bytePtr,
                LlvmApi.BuildTrunc(builder, NormalizeToI64(state, len), state.I32, "write_len_i32"),
                bytesWritten,
                LlvmApi.BuildIntToPtr(builder, LlvmApi.ConstInt(state.I64, 0, 0), state.I8Ptr, "null_overlapped")
            },
            "write_file");
    }

    private static LlvmValueHandle EmitSyscall(LlvmCodegenState state, long nr, LlvmValueHandle arg1, LlvmValueHandle arg2, LlvmValueHandle arg3, string name)
    {
        LlvmTypeHandle syscallType = LlvmApi.FunctionType(state.I64, [state.I64, state.I64, state.I64, state.I64]);
        LlvmValueHandle syscall = LlvmApi.GetInlineAsm(
            syscallType,
            "syscall",
            "={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}",
            true,
            false);
        return LlvmApi.BuildCall2(state.Target.Builder,
            syscallType,
            syscall,
            new[]
            {
                LlvmApi.ConstInt(state.I64, unchecked((ulong)nr), 1),
                NormalizeToI64(state, arg1),
                NormalizeToI64(state, arg2),
                NormalizeToI64(state, arg3)
            },
            name);
    }
}
