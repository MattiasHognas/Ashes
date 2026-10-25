using Ashes.Backend.Llvm.Interop;

namespace Ashes.Backend.Llvm;

/// <summary>
/// Leak checking (compiling with ASHES_RC_LEAKCHECK=1): the program counts the reference-counted
/// cells it allocates and releases, and reports how many are still live when it ends normally or
/// through <c>Ashes.IO.exit</c>, as the line <c>ashes-rc-live N</c> on standard error. A value a
/// program keeps to its end (a top-level binding) counts as live, so a leak is told by running the
/// same work twice: every cell one more round leaves behind is leaked. The count is one
/// process-wide word updated without synchronization, so only single-threaded programs report an
/// exact figure; a worker's own region, reclaimed whole when the worker ends, is never counted
/// down.
/// </summary>
internal static partial class LlvmCodegen
{
    private const string ReferenceCountedLiveCellsName = "__ashes_rc_live";

    private static bool LeakCheckEnabled
        => string.Equals(Environment.GetEnvironmentVariable("ASHES_RC_LEAKCHECK"), "1", StringComparison.Ordinal);

    private static void AddLeakCheckGlobal(LlvmTargetContext target, LlvmTypeHandle i64)
    {
        if (LeakCheckEnabled)
        {
            AddZeroInitializedI64Global(target, i64, ReferenceCountedLiveCellsName);
        }
    }

    private static void EmitLeakCheckCount(LlvmCodegenState state, long delta, string name)
    {
        if (!LeakCheckEnabled)
        {
            return;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        LlvmValueHandle counter = LlvmApi.GetNamedGlobal(state.Target.Module, ReferenceCountedLiveCellsName);
        LlvmValueHandle current = LlvmApi.BuildLoad2(builder, state.I64, counter, name + "_live");
        LlvmValueHandle next = LlvmApi.BuildAdd(builder, current, LlvmApi.ConstInt(state.I64, unchecked((ulong)delta), 1), name + "_live_next");
        LlvmApi.BuildStore(builder, next, counter);
    }

    /// <summary>Writes <c>ashes-rc-live N</c> to standard error, on Linux, when leak checking is on.</summary>
    private static void EmitLeakCheckReport(LlvmCodegenState state)
    {
        if (!LeakCheckEnabled || !IsLinuxFlavor(state.Flavor))
        {
            return;
        }

        LlvmBuilderHandle builder = state.Target.Builder;
        byte[] label = "ashes-rc-live "u8.ToArray();
        EmitWriteStandardError(state, EmitStackByteArray(state, label), LlvmApi.ConstInt(state.I64, (ulong)label.Length, 0));
        LlvmValueHandle live = LlvmApi.BuildLoad2(builder, state.I64,
            LlvmApi.GetNamedGlobal(state.Target.Module, ReferenceCountedLiveCellsName), "leak_check_live");
        var (indexSlot, workSlot, negativeSlot, buffer, bufferType, absValue, zero) = EmitPrintIntPrologue(state, live);
        _ = negativeSlot;
        LlvmBasicBlockHandle zeroBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "leak_check_zero");
        LlvmBasicBlockHandle loopCheckBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "leak_check_loop_check");
        LlvmBasicBlockHandle loopBodyBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "leak_check_loop_body");
        LlvmBasicBlockHandle writeBlock = LlvmApi.AppendBasicBlockInContext(state.Target.Context, state.Function, "leak_check_write");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, absValue, zero, "leak_check_is_zero"), zeroBlock, loopCheckBlock);

        LlvmApi.PositionBuilderAtEnd(builder, zeroBlock);
        StoreBufferByte(state, buffer, LlvmApi.ConstInt(state.I64, 31, 0), (byte)'0');
        LlvmApi.BuildStore(builder, LlvmApi.ConstInt(state.I64, 1, 0), indexSlot);
        LlvmApi.BuildBr(builder, writeBlock);

        LlvmApi.PositionBuilderAtEnd(builder, loopCheckBlock);
        LlvmValueHandle work = LlvmApi.BuildLoad2(builder, state.I64, workSlot, "leak_check_work");
        LlvmApi.BuildCondBr(builder, LlvmApi.BuildICmp(builder, LlvmIntPredicate.Eq, work, zero, "leak_check_done"), writeBlock, loopBodyBlock);
        EmitPrintIntDigitLoopBody(state, buffer, workSlot, indexSlot, work, loopCheckBlock, loopBodyBlock);

        LlvmApi.PositionBuilderAtEnd(builder, writeBlock);
        LlvmValueHandle count = LlvmApi.BuildLoad2(builder, state.I64, indexSlot, "leak_check_count");
        LlvmValueHandle start = LlvmApi.BuildSub(builder, LlvmApi.ConstInt(state.I64, 32, 0), count, "leak_check_start");
        EmitWriteStandardError(state, GetArrayElementPointer(state, bufferType, buffer, start, "leak_check_digits"), count);
        EmitWriteStandardError(state, EmitStackByteArray(state, [10]), LlvmApi.ConstInt(state.I64, 1, 0));
    }

    private static void EmitWriteStandardError(LlvmCodegenState state, LlvmValueHandle bytePtr, LlvmValueHandle len)
        => EmitLinuxSyscall(
            state,
            SyscallWrite,
            LlvmApi.ConstInt(state.I64, 2, 0),
            LlvmApi.BuildPtrToInt(state.Target.Builder, bytePtr, state.I64, "leak_check_ptr"),
            len,
            "leak_check_write");
}
