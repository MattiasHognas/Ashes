using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    private const int StdoutBufferSize = 64 * 1024;
    private const string StdoutBufferName = "__ashes_stdout_buffer";
    private const string StdoutLengthName = "__ashes_stdout_length";
    private const string StdoutNextTicketName = "__ashes_stdout_next_ticket";
    private const string StdoutServingTicketName = "__ashes_stdout_serving_ticket";

    private static void EmitStdoutBufferGlobals(
        LlvmTargetContext target,
        bool usesBufferedStdout,
        LlvmTypeHandle i8,
        LlvmTypeHandle i64)
    {
        if (!usesBufferedStdout)
        {
            return;
        }

        LlvmTypeHandle bufferType = LlvmApi.ArrayType2(i8, StdoutBufferSize);
        AddZeroGlobal(target, bufferType, StdoutBufferName);
        AddZeroGlobal(target, i64, StdoutLengthName);
        AddZeroGlobal(target, i64, StdoutNextTicketName);
        AddZeroGlobal(target, i64, StdoutServingTicketName);
    }

    private static void AddZeroGlobal(LlvmTargetContext target, LlvmTypeHandle type, string name)
    {
        LlvmValueHandle global = LlvmApi.AddGlobal(target.Module, type, name);
        LlvmApi.SetLinkage(global, LlvmLinkage.Internal);
        LlvmApi.SetInitializer(global, LlvmApi.ConstNull(type));
    }

    private static bool EmitBufferedWriteStringFromTemp(
        LlvmCodegenState state,
        LlvmValueHandle stringRef,
        bool appendNewline)
    {
        EmitStdoutLockAcquire(state);
        EmitBufferedWriteStringLocked(state, stringRef, appendNewline);
        EmitStdoutLockRelease(state);
        return false;
    }

    private static bool? EmitBufferedStdoutInstruction(LlvmCodegenState state, Ashes.Semantics.IrInst instruction)
        => instruction switch
        {
            Ashes.Semantics.IrInst.WriteBufferedStr write => EmitBufferedWriteStringFromTemp(
                state, LoadTemp(state, write.Source), write.AppendNewline),
            Ashes.Semantics.IrInst.FlushStdout => EmitFlushStdout(state),
            _ => null
        };

    private static void EmitBufferedWriteStringLocked(
        LlvmCodegenState state,
        LlvmValueHandle stringRef,
        bool appendNewline)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle stringBase = LlvmApi.BuildIntToPtr(builder, stringRef, state.I64Ptr, "stdout_str_len_ptr");
        LlvmValueHandle stringLength = LlvmApi.BuildLoad2(builder, state.I64, stringBase, "stdout_str_len");
        LlvmValueHandle stringAddress = LlvmApi.BuildAdd(builder, stringRef, LlvmApi.ConstInt(state.I64, 8, 0), "stdout_str_bytes_addr");
        LlvmValueHandle stringBytes = LlvmApi.BuildIntToPtr(builder, stringAddress, state.I8Ptr, "stdout_str_bytes");
        LlvmValueHandle totalLength = LlvmApi.BuildAdd(builder, stringLength,
            LlvmApi.ConstInt(state.I64, appendNewline ? 1UL : 0UL, 0), "stdout_total_len");

        LlvmBasicBlockHandle largeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_large");
        LlvmBasicBlockHandle fitCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_fit_check");
        LlvmBasicBlockHandle flushFirstBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_flush_first");
        LlvmBasicBlockHandle appendBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_append");
        LlvmBasicBlockHandle flushFullBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_flush_full");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_buffered_done");

        LlvmValueHandle isLarge = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ugt, totalLength,
            LlvmApi.ConstInt(state.I64, StdoutBufferSize, 0), "stdout_is_large");
        LlvmApi.BuildCondBr(builder, isLarge, largeBlock, fitCheckBlock);

        EmitLargeBufferedWrite(state, largeBlock, stringBytes, stringLength, appendNewline, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, fitCheckBlock);
        LlvmValueHandle bufferedLength = LoadStdoutLength(state, "stdout_buffered_len");
        LlvmValueHandle available = LlvmApi.BuildSub(builder,
            LlvmApi.ConstInt(state.I64, StdoutBufferSize, 0), bufferedLength, "stdout_available");
        LlvmValueHandle fits = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ule, totalLength, available, "stdout_fits");
        LlvmApi.BuildCondBr(builder, fits, appendBlock, flushFirstBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushFirstBlock);
        EmitFlushStdoutLocked(state);
        LlvmApi.BuildBr(builder, appendBlock);

        LlvmApi.PositionBuilderAtEnd(builder, appendBlock);
        LlvmValueHandle appendOffset = LoadStdoutLength(state, "stdout_append_offset");
        LlvmValueHandle destination = GetArrayElementPointer(state,
            LlvmApi.ArrayType2(state.I8, StdoutBufferSize), StdoutBufferGlobal(state), appendOffset, "stdout_append_ptr");
        LlvmApi.BuildMemCpy(builder, destination, 1, stringBytes, 1, stringLength);
        if (appendNewline)
        {
            LlvmValueHandle newlineOffset = LlvmApi.BuildAdd(builder, appendOffset, stringLength, "stdout_newline_offset");
            LlvmValueHandle newlinePointer = GetArrayElementPointer(state,
                LlvmApi.ArrayType2(state.I8, StdoutBufferSize), StdoutBufferGlobal(state), newlineOffset, "stdout_newline_ptr");
            LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I8, 10, 0), newlinePointer);
        }

        LlvmValueHandle newLength = LlvmApi.BuildAdd(builder, appendOffset, totalLength, "stdout_new_len");
        LlvmApi.BuildStore(builder, newLength, StdoutLengthGlobal(state));
        LlvmValueHandle isFull = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, newLength,
            LlvmApi.ConstInt(state.I64, StdoutBufferSize, 0), "stdout_is_full");
        LlvmApi.BuildCondBr(builder, isFull, flushFullBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, flushFullBlock);
        EmitFlushStdoutLocked(state);
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }

    private static void EmitLargeBufferedWrite(
        LlvmCodegenState state,
        LlvmBasicBlockHandle largeBlock,
        LlvmValueHandle stringBytes,
        LlvmValueHandle stringLength,
        bool appendNewline,
        LlvmBasicBlockHandle doneBlock)
    {
        LlvmApi.PositionBuilderAtEnd(state.Target.Builder, largeBlock);
        EmitFlushStdoutLocked(state);
        EmitWriteBytesRaw(state, stringBytes, stringLength);
        if (appendNewline)
        {
            EmitWriteBytesRaw(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
        }

        LlvmApi.BuildBr(state.Target.Builder, doneBlock);
    }

    private static bool EmitFlushStdout(LlvmCodegenState state)
    {
        if (!HasStdoutBuffer(state))
        {
            return false;
        }

        EmitStdoutLockAcquire(state);
        EmitFlushStdoutLocked(state);
        EmitStdoutLockRelease(state);
        return false;
    }

    private static void EmitFlushStdoutLocked(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmBasicBlockHandle writeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_flush_write");
        LlvmBasicBlockHandle doneBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_flush_done");
        LlvmValueHandle length = LoadStdoutLength(state, "stdout_flush_len");
        LlvmValueHandle hasBytes = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Ne, length,
            LlvmApi.ConstInt(state.I64, 0, 0), "stdout_flush_has_bytes");
        LlvmApi.BuildCondBr(builder, hasBytes, writeBlock, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeBlock);
        LlvmValueHandle buffer = GetArrayElementPointer(state,
            LlvmApi.ArrayType2(state.I8, StdoutBufferSize), StdoutBufferGlobal(state),
            LlvmApi.ConstInt(state.I64, 0, 0), "stdout_flush_buffer");
        EmitWriteBytesRaw(state, buffer, length);
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 0, 0), StdoutLengthGlobal(state));
        LlvmApi.BuildBr(builder, doneBlock);

        LlvmApi.PositionBuilderAtEnd(builder, doneBlock);
    }

    private static void EmitStdoutLockAcquire(LlvmCodegenState state)
    {
        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle nextAddress = LlvmApi.BuildPtrToInt(builder,
            LlvmApi.GetNamedGlobal(state.Target.Module, StdoutNextTicketName), state.I64, "stdout_next_addr");
        LlvmValueHandle ticket = EmitAtomicFetchAdd(state, nextAddress, 1, "stdout_ticket");
        LlvmValueHandle servingAddress = LlvmApi.BuildPtrToInt(builder,
            LlvmApi.GetNamedGlobal(state.Target.Module, StdoutServingTicketName), state.I64, "stdout_serving_addr");
        LlvmBasicBlockHandle waitBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_lock_wait");
        LlvmBasicBlockHandle acquiredBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "stdout_lock_acquired");
        LlvmApi.BuildBr(builder, waitBlock);

        LlvmApi.PositionBuilderAtEnd(builder, waitBlock);
        LlvmValueHandle serving = EmitAtomicFetchAdd(state, servingAddress, 0, "stdout_serving");
        LlvmValueHandle acquired = LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, serving, ticket, "stdout_lock_ready");
        LlvmApi.BuildCondBr(builder, acquired, acquiredBlock, waitBlock);
        LlvmApi.PositionBuilderAtEnd(builder, acquiredBlock);
    }

    private static void EmitStdoutLockRelease(LlvmCodegenState state)
    {
        LlvmValueHandle servingAddress = LlvmApi.BuildPtrToInt(state.Target.Builder,
            LlvmApi.GetNamedGlobal(state.Target.Module, StdoutServingTicketName), state.I64, "stdout_release_addr");
        _ = EmitAtomicFetchAdd(state, servingAddress, 1, "stdout_release");
    }

    private static bool HasStdoutBuffer(LlvmCodegenState state)
        => LlvmApi.GetNamedGlobal(state.Target.Module, StdoutBufferName).Ptr != 0;

    private static LlvmValueHandle StdoutBufferGlobal(LlvmCodegenState state)
        => LlvmApi.GetNamedGlobal(state.Target.Module, StdoutBufferName);

    private static LlvmValueHandle StdoutLengthGlobal(LlvmCodegenState state)
        => LlvmApi.GetNamedGlobal(state.Target.Module, StdoutLengthName);

    private static LlvmValueHandle LoadStdoutLength(LlvmCodegenState state, string name)
        => LlvmApi.BuildLoad2(state.Target.Builder, state.I64, StdoutLengthGlobal(state), name);
}
